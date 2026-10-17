"""BerryFinder inference API. Run: uvicorn berry_inference.api:app --port 8090

POST /api/v1/predictions             start (or reuse) an analysis of a point and radius
GET  /api/v1/predictions/{job_id}    status, the real data stage it is in, errors, and the georeferenced result
GET  /api/v1/berries/types           berries the model predicts, with how well it did on held-out regions
GET  /api/v1/locations/search        place search in Finland
GET  /api/v1/health                  model, data warm-up and queue state

Saved locations belong to signed-in users, so they live in the BerryFinder app (/api/v1/users/me/saved-locations there).
"""
from __future__ import annotations

import logging
import os
import threading
import time
from collections import defaultdict, deque
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Literal

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field, field_validator

from . import geo, geocode
from .jobs import JobManager, QueueFull

log = logging.getLogger("berry_inference")

ROOT = Path(__file__).resolve().parents[2]
MODEL_DIR = Path(os.environ.get("BERRY_MODEL_DIR", ROOT / "models" / "nordic-gbif-1.0"))
CACHE_DIR = Path(os.environ.get("BERRY_CACHE_DIR", ROOT / "inference" / ".cache"))
USER_AGENT = os.environ.get("BERRY_USER_AGENT", "BerryFinder/1.0 (+https://github.com/Umar2505/BerryFinder)")
ALLOWED_ORIGINS = [o for o in os.environ.get("BERRY_ALLOWED_ORIGINS", "").split(",") if o]

BERRIES = {
    "bilberry": ("Bilberry", "Mustikka"), "lingonberry": ("Lingonberry", "Puolukka"), "cloudberry": ("Cloudberry", "Hilla"),
    "raspberry": ("Raspberry", "Vadelma"), "bog_bilberry": ("Bog bilberry", "Juolukka"), "cranberry": ("Cranberry", "Karpalo"),
    "crowberry": ("Crowberry", "Variksenmarja"), "wild_strawberry": ("Wild strawberry", "Metsämansikka"),
    "arctic_bramble": ("Arctic bramble", "Mesimarja"), "sea_buckthorn": ("Sea buckthorn", "Tyrni"),
}
MIN_HELD_OUT_AUC = 0.55     # a berry is offered only where the model beat chance clearly on real held-out sightings

state: dict = {"runtime": None, "jobs": None, "warm": "pending", "load_error": None}


def _warm_soil() -> None:
    from .runtime import local_soilgrids_vrt
    rt = state["runtime"]
    try:
        state["warm"] = "running"
        for prop in rt.ns.get("SOIL_PROPS", {}):
            local_soilgrids_vrt(prop, rt.ns["SOIL_DEPTH"], CACHE_DIR)
        state["warm"] = "ready"
    except Exception as e:
        log.warning("SoilGrids warm-up failed (it retries on first use): %s", e)
        state["warm"] = "failed"


def load(model_dir: Path = MODEL_DIR, cache_dir: Path = CACHE_DIR) -> None:
    from .runtime import ModelRuntime
    try:
        state["runtime"] = ModelRuntime(model_dir, cache_dir)
        state["jobs"] = JobManager(state["runtime"], Path(cache_dir) / "results")
        threading.Thread(target=_warm_soil, daemon=True).start()
    except Exception as e:
        log.exception("model failed to load")
        state["load_error"] = str(e)


@asynccontextmanager
async def lifespan(_: FastAPI):
    if state["runtime"] is None:
        load()
    yield


app = FastAPI(title="BerryFinder inference API", version="1.0.0", lifespan=lifespan)

if ALLOWED_ORIGINS:
    from fastapi.middleware.cors import CORSMiddleware
    app.add_middleware(CORSMiddleware, allow_origins=ALLOWED_ORIGINS, allow_methods=["GET", "POST"], allow_headers=["*"])


# ---------------------------------------------------------------- errors: one shape, nothing internal

def error(status: int, code: str, message: str, headers: dict | None = None) -> JSONResponse:
    return JSONResponse({"error": {"code": code, "message": message}}, status_code=status, headers=headers)


@app.exception_handler(HTTPException)
async def http_error(_: Request, exc: HTTPException):
    detail = exc.detail if isinstance(exc.detail, dict) else {"code": "error", "message": str(exc.detail)}
    return error(exc.status_code, detail["code"], detail["message"], getattr(exc, "headers", None))


@app.exception_handler(RequestValidationError)
async def validation_error(_: Request, exc: RequestValidationError):
    first = exc.errors()[0] if exc.errors() else {}
    field_name = ".".join(str(p) for p in first.get("loc", [])[1:]) or "request"
    return error(422, "invalid_request", f"{field_name}: {first.get('msg', 'invalid value')}")


@app.exception_handler(Exception)
async def unhandled(_: Request, exc: Exception):
    log.exception("unhandled error: %s", exc)
    return error(500, "internal", "Something went wrong. Try again in a minute.")


# ---------------------------------------------------------------- rate limiting (per client address, sliding window)

_hits: dict[tuple[str, str], deque] = defaultdict(deque)
_hits_lock = threading.Lock()


def rate_limit(request: Request, bucket: str, per_minute: int) -> None:
    ip = request.headers.get("x-forwarded-for", "").split(",")[0].strip() or (request.client.host if request.client else "?")
    now = time.time()
    with _hits_lock:
        q = _hits[(bucket, ip)]
        while q and now - q[0] > 60:
            q.popleft()
        if len(q) >= per_minute:
            raise HTTPException(429, {"code": "rate_limited", "message": "Too many requests. Wait a minute and try again."}, headers={"Retry-After": "60"})
        q.append(now)


def runtime_or_503():
    if state["runtime"] is None:
        raise HTTPException(503, {"code": "model_unavailable", "message": "The berry model isn't loaded on this server."})
    return state["runtime"], state["jobs"]


# ---------------------------------------------------------------- schemas

class PredictionRequest(BaseModel):
    lat: float = Field(..., ge=-90, le=90, description="Latitude, WGS84")
    lon: float = Field(..., ge=-180, le=180, description="Longitude, WGS84")
    radius_m: Literal[1000, 3000, 5000, 10000] = Field(3000, description="Search radius in metres")
    species: list[str] | None = Field(None, description="Berries to score; default all the model supports")

    @field_validator("species")
    @classmethod
    def known_species(cls, v):
        if v is None:
            return v
        unknown = [s for s in v if s not in BERRIES]
        if unknown:
            raise ValueError(f"unknown berry {unknown[0]!r}")
        return sorted(set(v))


def supported_species(rt) -> list[str]:
    return [s for s in rt.species if (rt.card["species"].get(s, {}).get("gbif_auc") or 0) >= MIN_HELD_OUT_AUC]


# ---------------------------------------------------------------- routes

@app.post("/api/v1/predictions", status_code=202)
def create_prediction(body: PredictionRequest, request: Request):
    rate_limit(request, "predict", 10)
    rt, jobs = runtime_or_503()
    if not geo.in_finland(body.lat, body.lon):
        raise HTTPException(422, {"code": "outside_coverage", "message": "BerryFinder covers Finland. Choose a place in Finland."})
    allowed = supported_species(rt)
    species = [s for s in (body.species or allowed) if s in allowed]
    if not species:
        raise HTTPException(422, {"code": "unsupported_species", "message": "The model doesn't support those berries."})
    try:
        job = jobs.submit(body.lat, body.lon, body.radius_m, species)
    except QueueFull:
        raise HTTPException(503, {"code": "busy", "message": "Many analyses are running. Try again in a minute."}, headers={"Retry-After": "30"})
    return JSONResponse(job.public(include_result=False), status_code=200 if job.status == "succeeded" else 202)


@app.get("/api/v1/predictions/{job_id}")
def get_prediction(job_id: str, request: Request, include: Literal["all", "summary"] = "all"):
    rate_limit(request, "poll", 240)
    _, jobs = runtime_or_503()
    job = jobs.get(job_id) if len(job_id) == 32 and job_id.isalnum() else None
    if job is None:
        raise HTTPException(404, {"code": "not_found", "message": "No such analysis. It may have expired; run it again."})
    out = job.public(include_result=True)
    if include == "summary" and "result" in out:
        out["result"] = {k: v for k, v in out["result"].items() if k not in ("cells_geojson",)}
    return out


@app.get("/api/v1/berries/types")
def berry_types():
    rt, _ = runtime_or_503()
    supported = set(supported_species(rt))
    return {
        "model_version": rt.version,
        "output": rt.card.get("output"),
        "berries": [{
            "id": s, "name_en": BERRIES[s][0], "name_fi": BERRIES[s][1], "supported": s in supported,
            "held_out_auc": rt.card["species"].get(s, {}).get("gbif_auc"),
            "held_out_boyce": rt.card["species"].get(s, {}).get("gbif_boyce"),
        } for s in rt.species if s in BERRIES],
    }


@app.get("/api/v1/locations/search")
def search_locations(request: Request, q: str = Query(..., min_length=2, max_length=120), lang: Literal["en", "fi"] = "en"):
    rate_limit(request, "search", 30)
    try:
        return {"results": geocode.search(q, lang, USER_AGENT)}
    except Exception as e:
        log.warning("place search failed: %s", e)
        raise HTTPException(502, {"code": "search_unavailable", "message": "Place search is unavailable right now. Try again."})


@app.get("/api/v1/health")
def health():
    rt, jobs = state["runtime"], state["jobs"]
    ok = rt is not None
    body = {
        "status": "ok" if ok else "degraded",
        "model": {"loaded": ok, "version": rt.version if ok else None, "species": rt.species if ok else []},
        "data_warmup": state["warm"],
        "queue": {"waiting": jobs.queue.qsize() if jobs else 0,
                  "running": sum(1 for j in jobs.jobs.values() if j.status == "running") if jobs else 0},
    }
    return JSONResponse(body, status_code=200 if ok else 503)
