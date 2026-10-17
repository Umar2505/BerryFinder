"""Loads a trained BerryFinder model and scores places with it, using the exact code that trained it.

A model lives in models/<version>/: berry_model.pt (the ensemble), notebook.ipynb (the notebook version that trained it, whose
feature, preprocessing and network code is reused here) and card.json (how the run was evaluated). Only the notebook's
definitions run (imports, functions, classes, UPPER_CASE constants); its download and training cells never do.
"""
from __future__ import annotations

import ast
import builtins
import codecs
import json
import threading
from pathlib import Path
from typing import Callable

# Raw features returned next to the scores, so a place can be described in plain words ("open bog", "pine forest")
HABITAT_COLS = ["lc_tree", "lc_shrub", "lc_grass", "lc_crop", "lc_built", "lc_water", "lc_wetland", "lc_moss", "ph", "soc", "sand",
                "elevation", "built_dist_m", "fi_fertility", "fi_site_main", "fi_prop_pine", "fi_prop_spruce", "fi_prop_other", "fi_age"]


def _free_names(node):
    loads = {n.id for n in ast.walk(node) if isinstance(n, ast.Name) and isinstance(n.ctx, ast.Load)}
    bound = {n.id for n in ast.walk(node) if isinstance(n, ast.Name) and isinstance(n.ctx, ast.Store)}
    bound |= {a.arg for n in ast.walk(node) if isinstance(n, ast.Lambda) for a in n.args.args}
    return loads - bound


def _target_names(node):
    targets = node.targets if isinstance(node, ast.Assign) else [node.target]
    names = []
    for t in targets:
        if isinstance(t, ast.Name):
            names.append(t.id)
        elif isinstance(t, ast.Tuple) and all(isinstance(e, ast.Name) for e in t.elts):
            names.extend(e.id for e in t.elts)
        else:
            return None
    return names


def load_notebook_definitions(path: Path, cache_dir: Path | None = None) -> dict:
    """Run the notebook's imports, functions, classes and UPPER_CASE constants, skipping everything that downloads or trains.

    A constant is kept only when every name it uses is already defined, so values built from training data (the bundle, the
    tensors) are left out on their own. CACHE is pointed at cache_dir before any later definition uses it."""
    nb = json.loads(Path(path).read_text(encoding="utf-8"))
    ns: dict = {"__name__": "berry_notebook", "display": print}
    for cell in nb["cells"]:
        if cell["cell_type"] != "code":
            continue
        src = "".join(cell["source"])
        if src.lstrip().startswith(("%", "!")):
            continue
        for node in ast.parse(src).body:
            keep = isinstance(node, (ast.Import, ast.ImportFrom, ast.FunctionDef, ast.ClassDef))
            if isinstance(node, (ast.Assign, ast.AnnAssign)) and node.value is not None:
                names = _target_names(node)
                keep = bool(names) and all(n.isupper() or n == "session" for n in names) \
                    and all(n in ns or hasattr(builtins, n) for n in _free_names(node.value))
            elif isinstance(node, ast.Expr) and isinstance(node.value, ast.Call):
                root = node.value.func
                while isinstance(root, ast.Attribute):
                    root = root.value
                keep = isinstance(root, ast.Name) and root.id == "session"      # the HTTP session's User-Agent header
            if keep:
                exec(compile(ast.Module([node], []), str(path), "exec"), ns)
                if cache_dir is not None and isinstance(node, ast.Assign) and "CACHE" in (_target_names(node) or []):
                    ns["CACHE"] = Path(cache_dir)
    if cache_dir is not None:
        ns["CACHE"] = Path(cache_dir)
        (Path(cache_dir) / "power").mkdir(parents=True, exist_ok=True)   # the notebook creates this at import time
    return ns


def load_bundle(path: Path, ns: dict) -> dict:
    """Rebuild the ensemble. weights_only=True: the file may hold tensors, NumPy arrays and plain containers, never code."""
    import numpy as np
    import torch

    allowed = [np._core.multiarray._reconstruct, np.ndarray, np.dtype, codecs.encode]
    allowed += [type(np.dtype(t)) for t in ("float32", "float64", "int64", "int32", "bool")]
    with torch.serialization.safe_globals(allowed):
        blob = torch.load(path, map_location="cpu", weights_only=True)
    cfg = blob["cfg"]
    members = []
    for sd in blob["state_dicts"]:
        m = ns["BerryNet"](blob["dims"], len(blob["species"]), cfg["emb"], cfg["dropout"], cfg["modality_dropout"])
        m.load_state_dict(sd)
        m.eval()
        members.append(m)
    return dict(members=members, pre=ns["Preprocessor"].from_dict(blob["pre"]), species=list(blob["species"]),
                bg_sorted=blob["bg_sorted"], ood_mean=blob["ood_mean"], ood_icov=blob["ood_icov"],
                ood_threshold=float(blob["ood_threshold"]), label_mode=blob.get("label_mode", "gbif"), cfg=cfg, dims=blob["dims"])


class MissingDataError(RuntimeError):
    """A data source the model needs returned nothing for land points. Never filled in: the caller says so instead."""

    def __init__(self, source: str):
        super().__init__(source)
        self.source = source


def score_features(feats, bundle: dict, ns: dict):
    """Scores for already-extracted features. Per species: idx (0-100, share of the model's training background scoring
    lower), p (sigmoid of the ensemble's mean logit), sd (spread between members, in logits); plus an ood flag."""
    import numpy as np
    import torch

    d = bundle["pre"].transform(feats)
    tensors = {k: torch.as_tensor(v) for k, v in d.items()}
    stack = np.stack([ns["predict_logits"](m, tensors) for m in bundle["members"]])
    mean, spread = stack.mean(0), stack.std(0)
    zq = torch.cat([tensors["der"], tensors["soil"], tensors["topo"]], dim=1).numpy()
    dist = np.sqrt(np.einsum("ij,jk,ik->i", zq - bundle["ood_mean"], bundle["ood_icov"], zq - bundle["ood_mean"]))
    out = {"ood": (dist > bundle["ood_threshold"]).astype(int)}
    for k, s in enumerate(bundle["species"]):
        bg = bundle["bg_sorted"][s]
        out[f"idx_{s}"] = (100.0 * np.searchsorted(bg, mean[:, k]) / len(bg)).round(1)
        out[f"p_{s}"] = (1 / (1 + np.exp(-mean[:, k]))).round(4)
        out[f"sd_{s}"] = spread[:, k].round(3)
        out[f"m_{s}"] = mean[:, k].round(4)          # the ensemble's mean logit: the finest-grained ranking the model gives
    return out


SOILGRIDS = "https://files.isric.org/soilgrids/latest/data/"


def local_soilgrids_vrt(prop: str, depth: str, cache_dir: Path) -> Path:
    """SoilGrids publishes each layer as a 6 MB VRT index of remote tiles, which GDAL downloads again on every open (30 s at
    ISRIC's current speed). This keeps one local copy whose tile paths point at the same remote files: the same pixels, read
    without fetching the index each time."""
    import requests

    path = Path(cache_dir) / "soilgrids" / f"{prop}_{depth}_mean.vrt"
    if path.exists():
        return path
    path.parent.mkdir(parents=True, exist_ok=True)
    r = requests.get(f"{SOILGRIDS}{prop}/{prop}_{depth}_mean.vrt", timeout=180)
    r.raise_for_status()
    text = r.text.replace('relativeToVRT="1">./', f'relativeToVRT="0">/vsicurl/{SOILGRIDS}{prop}/')
    tmp = path.with_suffix(".tmp")
    tmp.write_text(text)
    tmp.replace(path)
    return path


def install_fast_soil_reader(ns: dict, cache_dir: Path) -> None:
    """Replace the notebook's _soil_task with the same read (same layer, depth, sampling, nodata and unit factor) from the
    local index. Notebook versions without SoilGrids are left alone."""
    if "_soil_task" not in ns or "SOIL_PROPS" not in ns:
        return
    import time as _time

    import numpy as np
    import rasterio
    from pyproj import Transformer

    props, depth, env = ns["SOIL_PROPS"], ns["SOIL_DEPTH"], ns["GDAL_ENV"]

    def _soil_task(prop, lons, lats, idx):
        for attempt in range(4):
            try:
                vrt = local_soilgrids_vrt(prop, depth, cache_dir)
                with rasterio.Env(**env):
                    with rasterio.open(vrt) as ds:
                        xs, ys = Transformer.from_crs("EPSG:4326", ds.crs, always_xy=True).transform(lons, lats)
                        vals = np.array([v[0] for v in ds.sample(zip(xs, ys))], dtype="float64")
                        nodata = ds.nodata
                if nodata is not None:
                    vals[vals == nodata] = np.nan
                return prop, idx, vals * props[prop][1]
            except Exception as e:
                if attempt == 3:
                    print("soil read failed for", prop, str(e)[:120])
                    return prop, idx, np.full(len(idx), np.nan)
                _time.sleep(3 * (attempt + 1))

    ns["_soil_task"] = _soil_task


def _merge_into_cache(path: Path, rows):
    """Add rows to a cache table. Several processes may share a cache (the service and predict_region.py), so the write
    re-reads the file under an exclusive lock, merges, and replaces it atomically."""
    import fcntl

    import pandas as pd

    with open(path.with_suffix(".lock"), "w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        current = pd.read_parquet(path) if path.exists() else None
        merged = rows if current is None else pd.concat([current[~current.index.isin(rows.index)], rows])
        tmp = path.with_suffix(".tmp.parquet")
        merged.to_parquet(tmp)
        tmp.replace(path)
        return merged


GLO90 = "https://copernicus-dem-90m.s3.amazonaws.com/"


def dem_glo90(lats, lons):
    """Elevation from the Copernicus DEM GLO-90, bilinear between pixel centres, read straight from its public cloud-optimised
    GeoTIFFs. This is the dataset Open-Meteo's elevation API serves: on 400 places cached from Open-Meteo the median difference
    is 0.8 m. NaN where no tile exists (open sea)."""
    import math

    import numpy as np
    import rasterio
    from rasterio.windows import Window

    lats, lons = np.asarray(lats, float), np.asarray(lons, float)
    out = np.full(len(lats), np.nan)
    tiles: dict[tuple[int, int], list[int]] = {}
    for i, (la, lo) in enumerate(zip(lats, lons)):
        tiles.setdefault((math.floor(la), math.floor(lo)), []).append(i)
    with rasterio.Env(GDAL_DISABLE_READDIR_ON_OPEN="EMPTY_DIR", CPL_VSIL_CURL_ALLOWED_EXTENSIONS=".tif", GDAL_HTTP_MAX_RETRY="4", GDAL_HTTP_RETRY_DELAY="2"):
        for (la0, lo0), idx in tiles.items():
            name = f"Copernicus_DSM_COG_30_{'N' if la0 >= 0 else 'S'}{abs(la0):02d}_00_{'E' if lo0 >= 0 else 'W'}{abs(lo0):03d}_00_DEM"
            try:
                ds = rasterio.open(f"/vsicurl/{GLO90}{name}/{name}.tif")
            except rasterio.errors.RasterioIOError:
                continue            # no tile: sea
            with ds:
                inv = ~ds.transform
                cols, rows = zip(*[inv * (lons[i], lats[i]) for i in idx])
                cols, rows = np.array(cols) - 0.5, np.array(rows) - 0.5          # to pixel-centre coordinates
                c0 = max(int(np.floor(cols.min())), 0)
                r0 = max(int(np.floor(rows.min())), 0)
                c1 = min(int(np.floor(cols.max())) + 2, ds.width)
                r1 = min(int(np.floor(rows.max())) + 2, ds.height)
                if c1 <= c0 or r1 <= r0:
                    continue
                a = ds.read(1, window=Window(c0, r0, c1 - c0, r1 - r0)).astype(float)
                if ds.nodata is not None:
                    a[a == ds.nodata] = np.nan
                for k, i in enumerate(idx):
                    cc = min(max(cols[k] - c0, 0), a.shape[1] - 1.000001)
                    rr = min(max(rows[k] - r0, 0), a.shape[0] - 1.000001)
                    ci, ri = int(cc), int(rr)
                    fc, fr = cc - ci, rr - ri
                    c2, r2 = min(ci + 1, a.shape[1] - 1), min(ri + 1, a.shape[0] - 1)
                    out[i] = (a[ri, ci] * (1 - fc) * (1 - fr) + a[ri, c2] * fc * (1 - fr) + a[r2, ci] * (1 - fc) * fr + a[r2, c2] * fc * fr)
    return out


def install_dem_elevation(ns: dict) -> None:
    """The notebook version that fetches elevation from Open-Meteo's API (Copernicus GLO-90) is rate-limited to a few thousand
    places a day. Read the same DEM directly instead; the column, units and caching stay the notebook's."""
    fetch = ns.get("fetch_elevation")

    def constants(code):
        for c in code.co_consts:
            if isinstance(c, str):
                yield c
            elif hasattr(c, "co_consts"):
                yield from constants(c)

    # Code compiled from a notebook has no readable source file, so look at the function's own string constants
    if fetch is None or not any("open-meteo" in c for c in constants(fetch.__code__)):
        return
    import pandas as pd

    def fetch_elevation(points):
        return pd.DataFrame({"elevation": dem_glo90(points["lat"].to_numpy(), points["lon"].to_numpy())})

    ns["fetch_elevation"] = fetch_elevation


def install_safe_cache(ns: dict) -> None:
    """The notebook's cached_table stores whatever a source returns, failures included, so one outage would poison an area
    for good. This version has the same interface and file format but stores only rows that hold at least one value."""
    import pandas as pd

    key_of = ns["key_of"]

    def cached_table(name, points, compute, chunk=3000):
        path = ns["CACHE"] / f"{name}.parquet"
        keys = key_of(points)
        cache = pd.read_parquet(path) if path.exists() else None
        missing = pd.Series(True, index=keys.index) if cache is None else ~keys.isin(cache.index)
        fresh = []
        if missing.any():
            todo = points.loc[missing, ["lat", "lon"]].copy()
            todo["key"] = keys[missing]
            todo = todo.drop_duplicates("key").reset_index(drop=True)
            for start in range(0, len(todo), chunk):
                part = todo.iloc[start:start + chunk].reset_index(drop=True)
                new = compute(part[["lat", "lon"]])
                new.index = part["key"].to_numpy()
                fresh.append(new)
                keep = new[new.notna().any(axis=1)]
                if len(keep):
                    cache = _merge_into_cache(path, keep)
        table = pd.concat(([cache] if cache is not None else []) + fresh)
        table = table[~table.index.duplicated(keep="last")]
        out = table.reindex(keys.to_numpy())
        out.index = points.index
        return out

    ns["cached_table"] = cached_table


# Sources whose values the model needs; if every land cell comes back empty, the analysis fails instead of guessing
REQUIRED_SOURCES = {"soil": "ph", "landcover": "lc_tree", "elevation": "elevation", "terrain": "elevation"}


class ModelRuntime:
    """One loaded model per process. Feature downloads go through the notebook's on-disk caches (parquet tables per source),
    which are not safe to write from two threads, so one scoring call runs at a time."""

    def __init__(self, model_dir: Path, cache_dir: Path):
        self.model_dir = Path(model_dir)
        self.card = json.loads((self.model_dir / "card.json").read_text(encoding="utf-8"))
        Path(cache_dir).mkdir(parents=True, exist_ok=True)
        self.ns = load_notebook_definitions(self.model_dir / "notebook.ipynb", cache_dir)
        install_safe_cache(self.ns)
        install_fast_soil_reader(self.ns, Path(cache_dir))
        install_dem_elevation(self.ns)
        self.bundle = load_bundle(self.model_dir / "berry_model.pt", self.ns)
        self._lock = threading.Lock()

    @property
    def version(self) -> str:
        return self.card["version"]

    @property
    def species(self) -> list[str]:
        return self.bundle["species"]

    def score(self, points, on_stage: Callable[[str], None] | None = None):
        """points: DataFrame with lat and lon. Returns (land cells with scores and habitat columns, number of water points dropped).
        on_stage(name) is called as each data source starts ("climate", "soil", ...) and before "inference"."""
        import pandas as pd

        with self._lock:
            ns = self.ns
            originals = {k: ns[k] for k in ("cached_table", "climate_table") if k in ns}

            def staged_table(name, *args, **kwargs):
                if on_stage:
                    on_stage(name)
                return originals["cached_table"](name, *args, **kwargs)

            def staged_climate(*args, **kwargs):
                if on_stage:
                    on_stage("climate")
                return originals["climate_table"](*args, **kwargs)

            ns["cached_table"] = staged_table
            if "climate_table" in originals:
                ns["climate_table"] = staged_climate
            try:
                feats = ns["extract_all_features"](points[["lat", "lon"]].reset_index(drop=True))
            finally:
                ns.update(originals)

            if feats["lc_tree"].isna().all():
                raise MissingDataError("landcover")
            water = feats["lc_water"].fillna(0) >= 0.5
            if (feats["t_ann"].isna() & ~water).any():
                raise MissingDataError("climate")
            # SoilGrids has no values under towns, so only open land counts when deciding that soil failed
            open_land = ~water & (feats["lc_built"].fillna(0) < 0.5)
            for source, column in REQUIRED_SOURCES.items():
                rows = open_land if source == "soil" else ~water
                if column in feats.columns and rows.any() and feats.loc[rows, column].isna().all():
                    raise MissingDataError(source)
            land = ~water & feats["t_ann"].notna()
            feats = feats[land.to_numpy()].reset_index(drop=True)
            meta = points[land.to_numpy()].reset_index(drop=True)
            if len(feats) == 0:
                return meta, int((~land).sum())

            if on_stage:
                on_stage("inference")
            scores = score_features(feats, self.bundle, ns)
            out = meta.copy()
            for k, v in scores.items():
                out[k] = v
            for c in HABITAT_COLS:
                out[c] = feats[c].to_numpy().round(3) if c in feats.columns else float("nan")
            return pd.DataFrame(out), int((~land).sum())
