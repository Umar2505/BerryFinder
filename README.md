# BerryFinder AI

A map-first app for finding wild berry habitat in Finland. Tap any forest, choose a radius, and a trained PyTorch model analyses
the area from climate, soil, satellite land cover, terrain and Finnish forest data, then shows the result as a heatmap and as pins
on the best places. Today and Berries suggest where to go now. Built on the IKON platform (C# server, MapLibre custom component,
PostGIS) with a Python inference service (`inference/`) for the model.

> **Status: pilot.** Predictions are unvalidated ecological estimates until field observations validate a trained model.
> No fabricated accuracy numbers, no synthetic data in production. See `handbook/data-availability.md` for exactly what is real and what is pending.

## Run it
See `handbook/deployment.md`. In short: `docker compose up -d`, set `BERRYFINDER_DB`, `ikon run --detach`.

## Tests
`dotnet test tests/Berryfinder.Tests` runs domain, parser (against recorded real NASA POWER and FMI responses), ML and **NIST StRD reference** tests.
`cd inference && pytest` runs the inference service tests: grid geometry and georeferencing, the API contract and job flow, and the real model.

## Layout
- `app/Berryfinder/` server: `Core/` (baseline, phenology, ML, training, plans: pure and tested), `Ingestion/` (connectors, job runner), `Modeling/`, `Services/`, `App.*.cs` (pages and API)
- `frontend-node/src/lib/berry-map/` MapLibre custom component
- `inference/` FastAPI service that runs the trained model for **Analyze area** (`inference/README.md`)
- `notebooks/` the PyTorch notebook and `predict_region.py` · `models/<version>/` each trained ensemble with the notebook code that trained it and its evaluation card · `data/ml/` the published grid and model card, imported on start (`handbook/ml-model.md`)
- `tests/` xUnit tests · `handbook/` data register, protocol, plans · `docker-compose.yml` PostGIS
