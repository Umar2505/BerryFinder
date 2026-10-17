"""The real trained model: it loads from its own notebook code, and its preprocessing and ensemble run on a feature table.
The features below are SYNTHETIC values in realistic ranges (no downloads); they test the wiring, not the predictions."""
from pathlib import Path

import numpy as np
import pandas as pd
import pytest

MODEL_DIR = Path(__file__).resolve().parents[2] / "models" / "nordic-gbif-1.0"
pytestmark = pytest.mark.skipif(not (MODEL_DIR / "berry_model.pt").exists(), reason="model file not present")


@pytest.fixture(scope="module")
def runtime(tmp_path_factory):
    from berry_inference.runtime import ModelRuntime
    return ModelRuntime(MODEL_DIR, tmp_path_factory.mktemp("cache"))


def synthetic_features(ns, n=8):
    rng = np.random.default_rng(0)
    df = pd.DataFrame({"lat": np.linspace(64.9, 65.1, n), "lon": np.linspace(25.5, 25.9, n)})
    temps = np.array([-11, -10, -5, 1, 8, 14, 17, 14, 9, 3, -3, -8], dtype=float)
    for k, m in enumerate(ns["MONTHS"]):
        df[f"T2M_{m}"] = temps[k] + rng.normal(0, 0.2, n)
        df[f"T2M_MIN_{m}"] = temps[k] - 5
        df[f"PRECTOTCORR_{m}"] = 1.8
        df[f"GWETROOT_{m}"] = 0.7
        df[f"ALLSKY_SFC_SW_DWN_{m}"] = 3.0
    df = pd.concat([df, ns["derive_climate"](df)], axis=1)
    df["elevation"] = 60.0
    for c, v in {"ph": 4.7, "soc": 150.0, "nitrogen": 5.0, "sand": 60.0, "clay": 6.0}.items():
        df[c] = v
    for c in ns["LC_COLS"]:
        df[c] = 0.0
    df["lc_tree"] = 0.9
    df["lc_wetland"] = 0.1
    for c in ns["FI_COLS"]:
        df[c] = np.nan
    return df


def test_the_model_loads_with_its_training_shapes(runtime):
    assert runtime.version == "nordic-gbif-1.0"
    assert len(runtime.bundle["members"]) == 5
    assert len(runtime.species) == 10 and "cloudberry" in runtime.species


def test_preprocessing_and_ensemble_produce_bounded_scores(runtime):
    from berry_inference.runtime import score_features
    feats = synthetic_features(runtime.ns)
    out = score_features(feats, runtime.bundle, runtime.ns)
    for s in runtime.species:
        assert out[f"idx_{s}"].shape == (len(feats),)
        assert np.all((out[f"idx_{s}"] >= 0) & (out[f"idx_{s}"] <= 100))
        assert np.all((out[f"p_{s}"] > 0) & (out[f"p_{s}"] < 1))
        assert np.all(out[f"sd_{s}"] >= 0)


def test_scores_respond_to_the_inputs(runtime):
    from berry_inference.runtime import score_features
    feats = synthetic_features(runtime.ns)
    wet = feats.copy()
    wet["lc_tree"], wet["lc_wetland"], wet["soc"] = 0.1, 0.9, 300.0
    a = score_features(feats, runtime.bundle, runtime.ns)["p_cloudberry"]
    b = score_features(wet, runtime.bundle, runtime.ns)["p_cloudberry"]
    assert not np.allclose(a, b)


@pytest.mark.network
def test_dem_elevation_matches_known_heights():
    """Copernicus GLO-90 read directly (what the notebook got from Open-Meteo). Needs internet."""
    from berry_inference.runtime import dem_glo90
    oulu, halti = dem_glo90([65.0121, 69.3076], [25.4651, 21.2629])
    assert 0 <= oulu <= 30                 # Oulu city centre, near sea level
    assert 1100 <= halti <= 1400           # Halti, Finland's highest point area


def test_the_rate_limited_elevation_api_is_replaced_by_the_dem(runtime):
    from berry_inference.runtime import install_dem_elevation
    assert runtime.ns["fetch_elevation"].__code__.co_filename.endswith("runtime.py")    # the DEM reader, not the notebook's API call
    install_dem_elevation(runtime.ns)                                                   # idempotent
    assert runtime.ns["fetch_elevation"].__code__.co_filename.endswith("runtime.py")
