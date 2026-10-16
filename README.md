# BerryFinder AI

Finds promising wild bilberry and lingonberry forests around Oulu, Finland, from open forest, soil, weather and protected-area data, and
says plainly how sure it is. Built on the IKON platform (C# server, MapLibre custom component, PostGIS).

> **Status: pilot.** Predictions are unvalidated ecological estimates until field observations validate a trained model.
> No fabricated accuracy numbers, no synthetic data in production. See `handbook/data-availability.md` for exactly what is real and what is pending.

## Run it
See `handbook/deployment.md`. In short: `docker compose up -d`, set `BERRYFINDER_DB`, `ikon run --detach`.

## Tests
`dotnet test tests/Berryfinder.Tests` runs domain, parser (against recorded real NASA POWER and FMI responses), ML and **NIST StRD reference** tests.

## Layout
- `app/Berryfinder/` server: `Core/` (baseline, phenology, ML, training, plans: pure and tested), `Ingestion/` (connectors, job runner), `Modeling/`, `Services/`, `App.*.cs` (pages and API)
- `frontend-node/src/lib/berry-map/` MapLibre custom component
- `tests/` xUnit tests · `handbook/` data register, protocol, plans · `docker-compose.yml` PostGIS
