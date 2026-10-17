"""One analysis: a point and a radius in, georeferenced model output out.

selected point -> grid of cells inside the circle -> features from the model's own sources -> the model's preprocessing and
ensemble -> per-cell suitability -> cells as polygons (heatmap) and connected strong cells as candidate areas (pins).
"""
from __future__ import annotations

import math
from datetime import datetime, timezone
from typing import Callable

import pandas as pd

from . import geo
from .habitat import describe, in_habitat, is_mire

# Two levels, because they answer different questions:
# - Area level: the model's suitability index, the share of its Nordic background places (vascular-plant records) scoring
#   lower. Around Oulu almost every place is in the top few per cent, mostly because of climate.
# - Within the area: each cell's rank against the other cells of this search (0 to 100) by the ensemble's mean output. That is
#   what the heatmap and the pins show. It says "better than the rest of this area", never "a 90 % chance of berries".
BANDS = [(95.0, "very_high"), (80.0, "high"), (50.0, "moderate"), (0.0, "low")]
PIN_MIN_LOCAL = 80.0          # a candidate area is made of cells in the best fifth of the search area
PIN_MIN_AREA_INDEX = 50.0     # and the berry must be at least moderately suited to the area as a whole
PINS_PER_SPECIES = 6


def local_contrast(logits, spread) -> float:
    """How far apart the area's best and worst places are (5th to 95th percentile of the mean logit), measured in units of
    the ensemble's disagreement. Below 1 the model barely tells the places apart."""
    import numpy as np
    lo, hi = np.quantile(logits, [0.05, 0.95])
    return float((hi - lo) / max(float(np.median(spread)), 1e-6))


# What each data source is called while it runs, in the words the app shows
STAGES = {
    "finnish_forest": "Analyzing forest conditions",
    "landcover": "Processing satellite land cover",
    "soil": "Reading soil maps",
    "climate": "Reading climate normals",
    "climate_chelsa": "Reading climate normals",
    "elevation": "Reading terrain",
    "terrain": "Reading terrain",
    "human": "Checking roads and buildings",
    "inference": "Running berry predictions",
    "postprocess": "Preparing your map",
}


def band(index: float) -> str:
    for lo, name in BANDS:
        if index >= lo:
            return name
    return "low"


def _clean(v):
    if v is None:
        return None
    try:
        f = float(v)
    except (TypeError, ValueError):
        return None
    return None if math.isnan(f) else round(f, 3)


def run_analysis(runtime, lat: float, lon: float, radius_m: int, species: list[str] | None,
                 on_stage: Callable[[str], None] | None = None) -> dict:
    step = geo.step_for_radius(radius_m)
    cells = geo.analysis_grid(lat, lon, radius_m, step)
    species = [s for s in (species or runtime.species) if s in runtime.species]
    points = pd.DataFrame({"lat": [c.lat for c in cells], "lon": [c.lon for c in cells],
                           "i": [c.i for c in cells], "j": [c.j for c in cells], "x": [c.x for c in cells], "y": [c.y for c in cells]})

    scored, dropped = runtime.score(points, on_stage=on_stage)
    if on_stage:
        on_stage("postprocess")

    import numpy as np
    from scipy.stats import rankdata

    local = {}
    for s in species:
        m = scored[f"m_{s}"].to_numpy()
        local[s] = (100.0 * (rankdata(m) - 0.5) / len(m)).round(1) if len(m) else m       # equal outputs share a rank

    features = []
    habitat_rows = {}
    for n, row in enumerate(scored.itertuples(index=False)):
        r = row._asdict()
        for s in species:
            r[f"loc_{s}"] = float(local[s][n])
        h = {k: r.get(k) for k in ("lc_tree", "lc_shrub", "lc_grass", "lc_crop", "lc_built", "lc_water", "lc_wetland", "lc_moss",
                                   "ph", "soc", "sand", "elevation", "fi_fertility", "fi_site_main", "fi_prop_pine", "fi_prop_spruce",
                                   "fi_prop_other", "fi_age")}
        habitat_rows[(r["i"], r["j"])] = (r, h)
        props = {"i": int(r["i"]), "j": int(r["j"]), "ood": bool(r["ood"])}
        for s in species:
            props[f"s_{s}"] = r[f"loc_{s}"]                # rank within this search area, 0-100
            props[f"g_{s}"] = float(r[f"idx_{s}"])          # Nordic suitability index, 0-100
            props[f"u_{s}"] = float(r[f"sd_{s}"])
        features.append({"type": "Feature", "geometry": {"type": "Polygon", "coordinates": [geo.cell_polygon(r["x"], r["y"], step)]},
                         "properties": props})

    pins = []
    summary_species = []
    for s in species:
        values = [float(r[f"idx_{s}"]) for r, _ in habitat_rows.values()]
        area_index = float(np.median(values)) if values else 0.0
        contrast = local_contrast(scored[f"m_{s}"].to_numpy(), scored[f"sd_{s}"].to_numpy()) if len(values) > 10 else 0.0
        strong = [(i, j) for (i, j), (r, h) in habitat_rows.items()
                  if not r["ood"] and r[f"loc_{s}"] >= PIN_MIN_LOCAL and in_habitat(s, h)] if area_index >= PIN_MIN_AREA_INDEX else []
        areas = []
        for group in geo.components(strong):
            members = [habitat_rows[c] for c in group]
            best = max(members, key=lambda m: m[0][f"loc_{s}"])[0]
            mean_local = sum(m[0][f"loc_{s}"] for m in members) / len(members)
            avg = {}
            for key in members[0][1]:
                vals = [v for v in (_clean(m[1][key]) for m in members) if v is not None]
                if vals:
                    avg[key] = sum(vals) / len(vals)
            avg["mire"] = sum(1 for m in members if is_mire(m[1])) / len(members)
            areas.append({
                "species": s, "lat": round(best["lat"], 6), "lon": round(best["lon"], 6),
                "local_rank": round(mean_local, 1), "best_local_rank": round(best[f"loc_{s}"], 1), "band": band(mean_local),
                "nordic_index": round(sum(m[0][f"idx_{s}"] for m in members) / len(members), 1),
                "cells": len(group), "area_ha": round(len(group) * step * step / 10000, 1),
                "uncertainty": round(sum(m[0][f"sd_{s}"] for m in members) / len(members), 3),
                "habitat": describe(avg), "distance_m": round(geo.distance_m(lat, lon, best["lat"], best["lon"])),
            })
        areas.sort(key=lambda a: a["local_rank"] * math.sqrt(a["cells"]), reverse=True)
        for rank, a in enumerate(areas[:PINS_PER_SPECIES], start=1):
            a["id"] = f"{s}-{rank}"
            a["rank"] = rank
            pins.append(a)
        summary_species.append({
            "species": s, "candidate_areas": min(len(areas), PINS_PER_SPECIES),
            "area_index": round(area_index, 1), "area_band": band(area_index),
            "local_contrast": round(contrast, 2), "local_differences": "clear" if contrast >= 1.5 else "weak" if contrast >= 0.75 else "minimal",
        })

    pin_features = [{"type": "Feature", "geometry": {"type": "Point", "coordinates": [p["lon"], p["lat"]]},
                     "properties": {k: v for k, v in p.items() if k not in ("habitat",)} | {"habitat": p["habitat"]["code"],
                                                                                         "habitat_en": p["habitat"]["en"], "habitat_fi": p["habitat"]["fi"]}}
                    for p in pins]
    area_km2 = math.pi * (radius_m / 1000) ** 2
    return {
        "model": {"version": runtime.version, "label_mode": runtime.card.get("label_mode"), "output": runtime.card.get("output"),
                  "local_scale_note": runtime.card.get("local_scale_note")},
        "request": {"lat": lat, "lon": lon, "radius_m": radius_m, "species": species},
        "grid": {"step_m": step, "crs": "EPSG:3067", "cells": len(cells), "land_cells": len(features), "water_cells": dropped},
        "area_km2": round(area_km2, 2),
        "bands": [{"name": n, "min_index": lo} for lo, n in BANDS],
        "summary": summary_species,
        "pins": pins,
        "cells_geojson": {"type": "FeatureCollection", "features": features},
        "pins_geojson": {"type": "FeatureCollection", "features": pin_features},
        "data_sources": runtime.card.get("features", {}),
        "computed_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
    }
