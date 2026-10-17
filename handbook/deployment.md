# Deployment

## Local development
```bash
docker compose up -d postgis               # PostGIS on 127.0.0.1:5433
docker compose up -d inference             # the model service on 127.0.0.1:8090 (or run it from inference/README.md)
export BERRYFINDER_DB="Host=127.0.0.1;Port=5433;Username=berryfinder;Password=berryfinder-dev-only;Database=berryfinder"
ikon run --detach                          # app on https://localhost:9443
ikon browse --steps "observe"              # check it as text
dotnet test tests/Berryfinder.Tests        # unit tests incl. NIST reference tests
(cd inference && pytest)                   # inference service: geometry, API contract, the real model
```
Without the inference service the app still runs; **Analyze area** is disabled with a note saying the analyser is offline.
On first start with an empty database the app queues the full pipeline for the Oulu pilot (25 tiles). Progress is on the admin dashboard
(first signed-in person becomes admin) or in `ingestion_jobs`.

## Production
1. Provide a PostGIS 3.x server (Cloud SQL, Neon, Supabase, Crunchy and similar all support it). The platform's built-in Postgres has no PostGIS.
2. `ikon secret set POSTGIS_URL` with an Npgsql connection string (`Host=...;Username=...;Password=...;Database=...;SSL Mode=Require`).
3. Optional: `ikon secret set NLS_API_KEY` (National Land Survey aerial imagery).
4. Configure the payment provider and tax settings in the IKON platform before offering Premium.
5. Replace the public OSRM and Nominatim servers with self-hosted instances or paid providers (set `Router.Endpoint`).
6. Deploy the inference service (`inference/Dockerfile`, 1 CPU and 2 GB RAM are enough; give it a persistent volume for
   `/var/cache/berryfinder`), keep it private or behind your gateway, and `ikon secret set INFERENCE_URL` to its address.
   The IKON container has no Python, so the model cannot run inside the app itself.
7. `ikon deploy`. Health check: `GET /api/health` (200 ok, 503 degraded). Logs: `ikon logs`.

## Environment and secrets
No secret is committed. See `.env.example` for the full list. Backups: enable provider point-in-time recovery on the PostGIS server;
`field_observations` and `audit_logs` are the irreplaceable tables.
