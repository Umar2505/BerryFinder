"""API contract tests. The runtime here is a stand-in with the real interface so the HTTP layer, validation, the job flow and
the georeferencing of results are tested without network downloads; test_model.py runs the real model."""
import time

import pandas as pd
import pytest
from fastapi.testclient import TestClient

from berry_inference import api
from berry_inference.jobs import JobManager
from berry_inference.runtime import MissingDataError


class StubRuntime:
    version = "stub-1"
    species = ["bilberry", "cloudberry", "crowberry"]
    card = {"species": {"bilberry": {"gbif_auc": 0.66}, "cloudberry": {"gbif_auc": 0.78}, "crowberry": {"gbif_auc": 0.50}},
            "label_mode": "gbif", "output": "relative", "features": {}}
    fail_with = None
    background_index = 70.0

    def score(self, points, on_stage=None):
        for s in ("climate", "soil", "landcover", "finnish_forest", "inference"):
            on_stage and on_stage(s)
        if self.fail_with:
            raise MissingDataError(self.fail_with)
        out = points.copy()
        out["ood"] = 0
        # a ridge of high scores along one grid column, so pins must land exactly there
        top = out["i"] == out["i"].median()
        for s in self.species:
            out[f"idx_{s}"] = top.map({True: 97.0, False: self.background_index})
            out[f"p_{s}"] = 0.5
            out[f"sd_{s}"] = 0.2
            out[f"m_{s}"] = top.map({True: 2.0, False: 0.5})
        out["lc_tree"] = 0.9
        out["fi_site_main"] = 3.0      # mire, so cloudberry passes its habitat check
        out["fi_prop_pine"] = 0.8
        for c in ("lc_built", "lc_crop", "lc_wetland", "lc_grass"):
            out[c] = 0.0
        return pd.DataFrame(out), 0


@pytest.fixture
def client(tmp_path):
    rt = StubRuntime()
    api.state.update(runtime=rt, jobs=JobManager(rt, tmp_path / "results"), warm="ready", load_error=None)
    api._hits.clear()
    with TestClient(api.app) as c:
        yield c, rt


def wait_done(c, job_id):
    for _ in range(100):
        body = c.get(f"/api/v1/predictions/{job_id}").json()
        if body["status"] in ("succeeded", "failed"):
            return body
        time.sleep(0.05)
    raise AssertionError("job did not finish")


def test_health(client):
    c, _ = client
    body = c.get("/api/v1/health").json()
    assert body["status"] == "ok" and body["model"]["version"] == "stub-1"


def test_only_berries_that_beat_chance_are_supported(client):
    c, _ = client
    berries = {b["id"]: b["supported"] for b in c.get("/api/v1/berries/types").json()["berries"]}
    assert berries == {"bilberry": True, "cloudberry": True, "crowberry": False}


@pytest.mark.parametrize("body, code", [
    ({"lat": 65.0, "lon": 25.5, "radius_m": 2000}, "invalid_request"),          # not one of the radius options
    ({"lat": 95.0, "lon": 25.5}, "invalid_request"),
    ({"lat": 59.33, "lon": 18.07}, "outside_coverage"),                      # Stockholm
    ({"lat": 65.0, "lon": 25.5, "species": ["mango"]}, "invalid_request"),
    ({"lat": 65.0, "lon": 25.5, "species": ["crowberry"]}, "unsupported_species"),
])
def test_bad_requests_get_a_clear_error(client, body, code):
    c, _ = client
    r = c.post("/api/v1/predictions", json=body)
    assert r.status_code == 422
    assert r.json()["error"]["code"] == code
    assert "Traceback" not in r.text


def test_a_prediction_runs_and_its_results_are_georeferenced(client):
    c, _ = client
    r = c.post("/api/v1/predictions", json={"lat": 65.03, "lon": 25.75, "radius_m": 1000})
    assert r.status_code == 202
    body = wait_done(c, r.json()["job_id"])
    assert body["status"] == "succeeded"
    res = body["result"]
    assert res["request"]["species"] == ["bilberry", "cloudberry"]
    cells = res["cells_geojson"]["features"]
    assert len(cells) == res["grid"]["land_cells"] > 250
    assert {"s_bilberry", "s_cloudberry"} <= set(cells[0]["properties"])
    # pins sit on the ridge of high cells, inside the search circle
    assert res["pins"], "expected candidate areas"
    for p in res["pins"]:
        assert p["band"] == "very_high" and p["distance_m"] <= 1000
        ridge = [f for f in cells if f["properties"][f"s_{p['species']}"] >= 90]
        assert len(ridge) < len(cells) / 10
        ring = ridge[0]["geometry"]["coordinates"][0]
        assert min(q[0] for q in ring) - 0.01 < p["lon"] < max(q[0] for q in ring) + 0.01


def test_the_same_request_reuses_the_job(client):
    c, _ = client
    a = c.post("/api/v1/predictions", json={"lat": 65.03, "lon": 25.75, "radius_m": 1000}).json()["job_id"]
    b = c.post("/api/v1/predictions", json={"lat": 65.03001, "lon": 25.75001, "radius_m": 1000}).json()["job_id"]
    assert a == b


def test_missing_source_data_fails_with_a_human_message(client):
    c, rt = client
    rt.fail_with = "soil"
    job = c.post("/api/v1/predictions", json={"lat": 64.5, "lon": 26.0, "radius_m": 1000}).json()["job_id"]
    body = wait_done(c, job)
    assert body["status"] == "failed"
    assert body["error"]["code"] == "data_unavailable:soil"
    assert "SoilGrids" in body["error"]["message"] and "result" not in body


def test_unknown_job(client):
    c, _ = client
    r = c.get("/api/v1/predictions/" + "0" * 32)
    assert r.status_code == 404 and r.json()["error"]["code"] == "not_found"


def test_rate_limit(client):
    c, _ = client
    codes = [c.post("/api/v1/predictions", json={"lat": 65.0 + i / 100, "lon": 25.5, "radius_m": 1000}).status_code for i in range(12)]
    assert 429 in codes


def test_no_pins_where_the_area_as_a_whole_does_not_suit_the_berry(client):
    c, rt = client
    rt.background_index = 20.0
    job = c.post("/api/v1/predictions", json={"lat": 62.0, "lon": 27.0, "radius_m": 1000}).json()["job_id"]
    res = wait_done(c, job)["result"]
    assert res["pins"] == []
    assert all(s["area_band"] == "low" for s in res["summary"])
