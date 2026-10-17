"""Prediction jobs: queued, run one at a time by a single worker, deduplicated and cached.

The same request (a point rounded to about 10 m, a radius and a species list) returns the running or finished job instead of
starting another; finished results are also kept on disk, so a restart does not recompute them.
"""
from __future__ import annotations

import hashlib
import json
import queue
import threading
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path

from .predict import STAGES, run_analysis
from .runtime import MissingDataError

RESULT_TTL_S = 7 * 24 * 3600

# What a failure says to a person. Raw exceptions and infrastructure details never leave the service.
MISSING_SOURCE_MESSAGES = {
    "climate": "Climate data (NASA POWER) isn't available for this area right now. Try again in a minute.",
    "soil": "Soil maps (SoilGrids) didn't load for this area. Try again in a minute.",
    "landcover": "Satellite land cover (ESA WorldCover) didn't load for this area. Try again in a minute.",
    "elevation": "Elevation data didn't load for this area. Try again in a minute.",
    "terrain": "Elevation data didn't load for this area. Try again in a minute.",
}


def request_key(lat: float, lon: float, radius_m: int, species: list[str], model_version: str) -> str:
    raw = json.dumps([round(lat, 4), round(lon, 4), radius_m, sorted(species), model_version])
    return hashlib.sha256(raw.encode()).hexdigest()[:24]


@dataclass
class Job:
    id: str
    key: str
    lat: float
    lon: float
    radius_m: int
    species: list[str]
    status: str = "queued"              # queued | running | succeeded | failed
    stage: str | None = None
    stages_seen: list[str] = field(default_factory=list)
    error_code: str | None = None
    error_message: str | None = None
    created_at: float = field(default_factory=time.time)
    finished_at: float | None = None
    result: dict | None = None

    def public(self, include_result: bool = True) -> dict:
        out = {
            "job_id": self.id, "status": self.status,
            "request": {"lat": self.lat, "lon": self.lon, "radius_m": self.radius_m, "species": self.species},
            # Stages are the real data sources as they start, in order; no percentage is invented
            "progress": {"stage": self.stage, "label": STAGES.get(self.stage or "", None), "completed_stages": len(self.stages_seen) - (1 if self.status == "running" else 0)},
            "created_at": self.created_at, "finished_at": self.finished_at,
        }
        if self.error_code:
            out["error"] = {"code": self.error_code, "message": self.error_message}
        if include_result and self.result is not None:
            out["result"] = self.result
        return out


class QueueFull(RuntimeError):
    pass


class JobManager:
    def __init__(self, runtime, results_dir: Path, max_queue: int = 20):
        self.runtime = runtime
        self.results_dir = Path(results_dir)
        self.results_dir.mkdir(parents=True, exist_ok=True)
        self.jobs: dict[str, Job] = {}
        self.by_key: dict[str, str] = {}
        self.queue: queue.Queue[str] = queue.Queue(maxsize=max_queue)
        self.lock = threading.Lock()
        self.worker = threading.Thread(target=self._work, name="prediction-worker", daemon=True)
        self.worker.start()

    def submit(self, lat: float, lon: float, radius_m: int, species: list[str]) -> Job:
        key = request_key(lat, lon, radius_m, species, self.runtime.version)
        with self.lock:
            existing = self.jobs.get(self.by_key.get(key, ""))
            if existing and existing.status in ("queued", "running", "succeeded"):
                return existing
            job = Job(id=uuid.uuid4().hex, key=key, lat=lat, lon=lon, radius_m=radius_m, species=species)
            cached = self._load_result(key)
            if cached is not None:
                job.status, job.result, job.finished_at = "succeeded", cached, time.time()
                job.stages_seen = ["cache"]
            else:
                try:
                    self.queue.put_nowait(job.id)
                except queue.Full:
                    raise QueueFull()
            self.jobs[job.id] = job
            self.by_key[key] = job.id
            return job

    def get(self, job_id: str) -> Job | None:
        return self.jobs.get(job_id)

    def _result_path(self, key: str) -> Path:
        return self.results_dir / f"{key}.json"

    def _load_result(self, key: str) -> dict | None:
        path = self._result_path(key)
        if not path.exists() or time.time() - path.stat().st_mtime > RESULT_TTL_S:
            return None
        try:
            return json.loads(path.read_text())
        except (OSError, ValueError):
            return None

    def _work(self) -> None:
        while True:
            job = self.jobs[self.queue.get()]
            job.status = "running"

            def on_stage(name: str, job=job) -> None:
                job.stage = name
                job.stages_seen.append(name)

            try:
                job.result = run_analysis(self.runtime, job.lat, job.lon, job.radius_m, job.species, on_stage=on_stage)
                tmp = self._result_path(job.key).with_suffix(".tmp")
                tmp.write_text(json.dumps(job.result))
                tmp.replace(self._result_path(job.key))
                job.status = "succeeded"
            except MissingDataError as e:
                job.status, job.error_code = "failed", f"data_unavailable:{e.source}"
                job.error_message = MISSING_SOURCE_MESSAGES.get(e.source, "Some map data didn't load for this area. Try again in a minute.")
            except Exception as e:     # logged in full here, summarised for the client
                import logging
                logging.getLogger("berry_inference").exception("prediction %s failed: %s", job.id, e)
                job.status, job.error_code = "failed", "prediction_failed"
                job.error_message = "The analysis couldn't finish. Try again in a minute."
            finally:
                job.finished_at = time.time()
                job.stage = None if job.status == "succeeded" else job.stage
