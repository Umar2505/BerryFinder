# BerryFinder inference service

The trained Nordic berry habitat model behind an HTTP API. The BerryFinder app (IKON, C#) calls it when someone taps the map and
presses **Analyze area**. The app never runs PyTorch itself.

```
selected point → grid of squares inside the radius (EPSG:3067) → the model's own data sources → its preprocessing and
5-network ensemble → per-square suitability → heatmap cells and candidate-area pins (GeoJSON) → the map
```

## Run it

```bash
cd inference
python -m venv .venv && . .venv/bin/activate
pip install --extra-index-url https://download.pytorch.org/whl/cpu -r requirements.txt
uvicorn berry_inference.api:app --port 8090
```

The app looks for the service at `http://127.0.0.1:8090` (set `BERRYFINDER_INFERENCE_URL`, or the `INFERENCE_URL` secret when
deployed). Interactive API docs: `http://127.0.0.1:8090/docs`. With Docker, from the repository root:
`docker compose up -d inference`.

On first start the service downloads five SoilGrids index files (about 30 MB, a few minutes at ISRIC's current speed); `/api/v1/health` reports
`data_warmup`. Keep `BERRY_CACHE_DIR` on a persistent volume: it also caches every place's features and every finished analysis.

## API

| Endpoint | |
|---|---|
| `POST /api/v1/predictions` | `{lat, lon, radius_m: 1000 \| 3000 \| 5000 \| 10000, species?}`. Returns `202` with a `job_id`, or `200` straight away when the same area was analysed before. Identical requests share one job. |
| `GET /api/v1/predictions/{job_id}` | `status` (`queued`, `running`, `succeeded`, `failed`), `progress.stage` (the data source really running: `climate`, `elevation`, `soil`, `landcover`, `finnish_forest`, `inference`, `postprocess`), `error {code, message}`, and `result`. `?include=summary` leaves out the cell polygons. |
| `GET /api/v1/berries/types` | The berries the model predicts and its held-out AUC for each. A berry is `supported` only where the model beat chance clearly (AUC ≥ 0.55). |
| `GET /api/v1/locations/search?q=` | Place search in Finland (OpenStreetMap Nominatim, cached, one request a second). |
| `GET /api/v1/health` | Model, data warm-up and queue. |

Saved locations belong to signed-in users, so they are in the app: `GET`/`POST /api/v1/users/me/saved-locations`.

Errors always look like `{"error": {"code": "...", "message": "..."}}`, with a sentence a person can read and never a stack trace.
Limits: 10 analyses a minute per address, 20 waiting jobs, radius options only, Finland only.

## What a result means

`result.cells_geojson` has one square per scored place. Per berry, each square has:

* `s_<berry>`: the square's rank **within this search area**, 0 to 100, by the ensemble's mean output. The heatmap colours this.
* `g_<berry>`: the model's suitability index, the share of its Nordic background places (other plant records) that score lower.
* `u_<berry>`: how much the five networks disagree, in logits.

`result.summary` gives each berry's area-level index, a band, and `local_contrast`. That is the spread of the area's scores in units of
model disagreement: below about 1.5 the differences inside the area are weak, and the app says so. `result.pins` are connected squares
in the area's best fifth that also lie on the berry's basic habitat (`habitat.py`, the same rule as the app's published spots).

None of these numbers is a probability of finding berries, and the service never fills a gap. If climate, soil, land cover or
elevation comes back empty for the land in an area, the job fails with a `data_unavailable:<source>` code and a retry message.

## Model

`BERRY_MODEL_DIR` holds `berry_model.pt`, `notebook.ipynb` (the notebook version that trained it) and `card.json` (how the run was
evaluated). `runtime.py` runs only the notebook's definitions (imports, functions, classes, constants), so features, preprocessing and
network are exactly those of training. It loads the weights with `weights_only=True`.

The default model is `models/nordic-gbif-1.0`; why it was chosen is in `handbook/ml-model.md`. Three operational changes keep the
notebook's data while making it usable live:
* **SoilGrids:** the 6 MB index is kept locally, pointing at the same remote tiles, so it isn't downloaded on every open. The values
  are identical.
* **Elevation:** read straight from the Copernicus DEM GLO-90 that the notebook fetched through Open-Meteo's API, which allows only a few
  thousand places a day. Bilinear reads match Open-Meteo's values within 0.8 m (median of 400 places), and this feature's
  permutation importance is 0.011.
* **Failed reads:** the download cache never stores one; caches can be shared between processes (file lock, atomic replace).

## Tests

```bash
pytest          # geometry, habitat rule, API contract and job flow, and the real model's preprocessing and ensemble
```
