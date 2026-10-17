import numpy as np
import pandas as pd

from berry_inference.runtime import install_safe_cache, load_notebook_definitions


def ns_for(tmp_path):
    ns = {"CACHE": tmp_path, "key_of": lambda p: p["lat"].round(5).map("{:.5f}".format) + "_" + p["lon"].round(5).map("{:.5f}".format)}
    install_safe_cache(ns)
    return ns


def test_a_failed_read_is_never_cached(tmp_path):
    ns = ns_for(tmp_path)
    pts = pd.DataFrame({"lat": [65.0, 65.1], "lon": [25.0, 25.1]})
    calls = []

    def flaky(p):
        calls.append(len(p))
        return pd.DataFrame({"ph": [np.nan] * len(p)})       # the service failed for everything

    out = ns["cached_table"]("soil", pts, flaky)
    assert out["ph"].isna().all()
    ns["cached_table"]("soil", pts, flaky)
    assert calls == [2, 2]                                     # asked again, not served from cache


def test_good_rows_are_cached_and_merged(tmp_path):
    ns = ns_for(tmp_path)
    a = pd.DataFrame({"lat": [65.0], "lon": [25.0]})
    b = pd.DataFrame({"lat": [65.1], "lon": [25.1]})
    ns["cached_table"]("soil", a, lambda p: pd.DataFrame({"ph": [4.6]}))
    ns["cached_table"]("soil", b, lambda p: pd.DataFrame({"ph": [5.1]}))
    both = ns["cached_table"]("soil", pd.concat([a, b], ignore_index=True), lambda p: (_ for _ in ()).throw(AssertionError("should hit the cache")))
    assert both["ph"].tolist() == [4.6, 5.1]


def test_only_definitions_run_from_a_notebook(tmp_path):
    import json
    nb = {"cells": [
        {"cell_type": "code", "source": ["%pip install something\n"]},
        {"cell_type": "code", "source": ["import math\nLIMIT = 3\nresult = train_everything()\n\ndef double(x):\n    return 2 * x\n"]},
        {"cell_type": "code", "source": ["BUNDLE = dict(members=members)\nSCALE = LIMIT * 2\n"]},
    ]}
    path = tmp_path / "nb.ipynb"
    path.write_text(json.dumps(nb))
    ns = load_notebook_definitions(path, tmp_path / "cache")
    assert ns["LIMIT"] == 3 and ns["SCALE"] == 6 and ns["double"](2) == 4
    assert "result" not in ns and "BUNDLE" not in ns        # training code and values built from it never run
    assert ns["CACHE"] == tmp_path / "cache"
