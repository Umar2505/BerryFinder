"""Score a region with a trained BerryFinder model and write the grid that the app imports for its published spots.

    python predict_region.py --model-dir ../models/nordic-gbif-1.0 --out ../data/ml/oulu_grid_gbif.csv.gz

A model directory holds berry_model.pt, notebook.ipynb (the notebook version that trained it) and card.json. The model, feature
and prediction code comes from that notebook through inference/berry_inference/runtime.py, the same loader the live inference
service uses, so the network and its inputs always match what was trained. Features come from the same open sources as training,
so this needs internet access; downloads are cached in --cache, so a stopped run resumes.

The default region is the app's Oulu pilot square (EPSG:3067, 420000..445000 E, 7190000..7215000 N) at 250 m.
"""
import argparse
import gzip
import json
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "inference"))

from berry_inference.runtime import ModelRuntime  # noqa: E402


def grid_points(x0, y0, x1, y1, step):
    import numpy as np
    import pandas as pd
    from pyproj import Transformer

    xs = np.arange(x0 + step / 2, x1, step)
    ys = np.arange(y0 + step / 2, y1, step)
    gx, gy = np.meshgrid(xs, ys)
    lon, lat = Transformer.from_crs("EPSG:3067", "EPSG:4326", always_xy=True).transform(gx.ravel(), gy.ravel())
    return pd.DataFrame({"cell_x": gx.ravel().round().astype(int), "cell_y": gy.ravel().round().astype(int), "lat": lat.round(6), "lon": lon.round(6)})


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--model-dir", type=Path, default=HERE.parent / "models" / "nordic-gbif-1.0")
    ap.add_argument("--out", type=Path, default=HERE.parent / "data" / "ml" / "oulu_grid_gbif.csv.gz")
    ap.add_argument("--bbox", type=float, nargs=4, metavar=("X_MIN", "Y_MIN", "X_MAX", "Y_MAX"), default=[420000, 7190000, 445000, 7215000],
                    help="EPSG:3067 metres (default: the Oulu pilot square)")
    ap.add_argument("--step", type=float, default=250.0, help="grid spacing in metres")
    ap.add_argument("--cache", type=Path, default=HERE.parent / "inference" / ".cache", help="download cache, shared with the inference service")
    ap.add_argument("--limit", type=int, default=0, help="score only the first N points (smoke test)")
    args = ap.parse_args()

    started = time.time()
    runtime = ModelRuntime(args.model_dir, args.cache)
    print(f"model {runtime.version}: {len(runtime.bundle['members'])} members, {len(runtime.species)} species, labels: {runtime.bundle['label_mode']}")

    points = grid_points(*args.bbox, args.step)
    if args.limit:
        points = points.head(args.limit)
    print(f"{len(points)} grid points at {args.step:.0f} m")

    out, dropped = runtime.score(points, on_stage=lambda s: print(f"  {s}…", flush=True))
    out = out.drop(columns=[c for c in out.columns if c.startswith("m_")])
    args.out.parent.mkdir(parents=True, exist_ok=True)
    with gzip.open(args.out, "wt", encoding="utf-8", newline="") as f:
        out.to_csv(f, index=False)
    print(f"wrote {len(out)} land cells to {args.out} ({dropped} water points dropped) in {time.time() - started:.0f}s")
    summary = {s: round(float((out[f'idx_{s}'] >= 90).mean()), 3) for s in runtime.species}
    print("share of cells in the top 10 % of the training background:", json.dumps(summary))


if __name__ == "__main__":
    main()
