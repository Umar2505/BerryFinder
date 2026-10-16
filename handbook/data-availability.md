# Data availability and missing dependencies

Honest status of what works today and what is blocked. Nothing below is simulated in the product.

## Real and working
- Forest stands, GTK soil, OSM access, Syke protected areas, FMI weather and NASA POWER grid, ingested per 5 km tile into PostGIS.
- Habitat suitability baseline (literature-grounded, **unvalidated**) for bilberry and lingonberry, published per stand and merged into picking areas.
- Seasonal stage estimate from growing degree days (heuristic; thresholds are uncalibrated).
- Road driving times (OSRM demo), place search (Nominatim), navigation hand-off.
- Field observations with QC flags, review queue, consent, export and deletion.
- Training pipeline (logistic regression, random forest, spatial block validation, calibration, baseline comparison, promotion guard).

## Not possible yet, and why
| Gap | Why | Next step |
|---|---|---|
| **Trained models** | Zero accepted field observations exist. The pipeline refuses to train below 200 usable labels, 30 per class, 5 spatial blocks and says so. Every prediction is labelled an unvalidated ecological estimate | Run the 2027 pilot (see `validation-plan.md`) |
| **Luke plot-level berry data** | No public download API or commercial-use licence found | Written data request to Luke; adapter stays "pending authorization" |
| **Sentinel-2 NDVI/NDMI features** | Scenes are catalogued, but downloading pixels needs Copernicus Data Space S3 credentials, and the raster compositing is Python (rasterio/rioxarray) which is **not built yet** | Obtain CDSE credentials; build `ml/` raster job; the schema (`satellite_features`) and the baseline's NDVI factor already exist |
| **Terrain and aerial basemap** | Needs a free National Land Survey API key | Set the `NLS_API_KEY` secret |
| **Seasonal availability model (Model B) trained on data** | Needs verified flowering/ripeness observations | Collected by the same pilot |
| **Cloudberry** | Needs peatland data and a separate model | After the first season |
| **Offline maps** | Not built. Field mode keeps drafts and saved places only | Roadmap |
| **Berry counting from photos** | Not built (optional); photos get cheap quality flags only | Roadmap |
| **Stripe billing** | Offers are declared through the platform's payments service; payment and tax configuration are not switched on, and checkout says so | Configure the payment provider and tax settings before launch |
| **PostGIS on the platform database** | The platform's built-in Postgres does not include PostGIS | Provide a PostGIS server via the `POSTGIS_URL` secret (`deployment.md`) |

## Architecture deviations from the original brief
- The backend is C# on the IKON platform rather than FastAPI. Jobs are a Postgres-backed queue (`ingestion_jobs`, `FOR UPDATE SKIP LOCKED`), not Celery/Redis; at this scale it needs no extra service. The model registry is a table (`model_versions`), not MLflow.
- Learners are implemented in C# (logistic regression, random forest, Platt calibration). LightGBM/CatBoost and rasterio-based raster processing would be a separate Python service; not built.
- Prediction cells are forest stands (about 1 to 5 ha), not a 100 m grid: the stand is the unit the source data actually describes.
