"""Tests for rule_labels.py. Run with `python test_rule_labels.py` or `pytest`.
The rows below are SYNTHETIC test fixtures, not real places."""
import numpy as np
import pandas as pd

import rule_labels as rl


def make_rows(n=1, **overrides):
    # a typical undisturbed boreal mineral-soil forest, then overrides
    row = dict(ph=4.8, soc=60.0, nitrogen=3.0, sand=55.0, clay=8.0, lc_tree=0.85, lc_shrub=0.05, lc_grass=0.02, lc_crop=0.0,
               lc_built=0.0, lc_bare=0.01, lc_water=0.01, lc_wetland=0.05, lc_moss=0.01, gsl=155.0, scd=140.0, gdd5=1000.0,
               precip_ann=600.0, t_range=25.0, elevation=60.0, fi_fertility=np.nan, built_dist_m=500.0, lc_tree_1km=0.9,
               lc_built_1km=0.0, lc_crop_1km=0.0)
    row.update(overrides)
    return pd.DataFrame([row] * n)


def test_right_conditions_undisturbed_gives_50_percent():
    p = rl.rule_probability(make_rows())
    assert p["bilberry"].iloc[0] == rl.P_UNDISTURBED


def test_right_conditions_but_disturbed_gives_30_percent():
    p = rl.rule_probability(make_rows(built_dist_m=100.0))
    assert p["bilberry"].iloc[0] == rl.P_RIGHT


def test_farmland_nearby_counts_as_disturbed():
    assert rl.rule_probability(make_rows(lc_crop_1km=0.20))["bilberry"].iloc[0] == rl.P_RIGHT


def test_wrong_conditions_give_the_small_background_rate():
    p = rl.rule_probability(make_rows(ph=7.5))          # too alkaline for the calcifuge berries
    assert p["bilberry"].iloc[0] == rl.P_OTHER
    assert p["lingonberry"].iloc[0] == rl.P_OTHER


def test_missing_values_are_not_right_conditions():
    assert rl.rule_probability(make_rows(ph=np.nan))["bilberry"].iloc[0] == rl.P_OTHER


def test_unknown_disturbance_is_not_boosted():
    assert rl.rule_probability(make_rows(built_dist_m=np.nan))["bilberry"].iloc[0] == rl.P_RIGHT


def test_finnish_stand_rule_applies_only_where_data_exists():
    assert rl.conditions_met(make_rows(fi_fertility=np.nan), "bilberry")[0]
    assert rl.conditions_met(make_rows(fi_fertility=3.0), "bilberry")[0]
    assert not rl.conditions_met(make_rows(fi_fertility=6.0), "bilberry")[0]       # barren heath


def test_cloudberry_needs_acidic_peat():
    bog = make_rows(ph=4.0, soc=220.0, lc_wetland=0.6, lc_tree=0.1, gdd5=800.0, scd=150.0)
    assert rl.conditions_met(bog, "cloudberry")[0]
    assert not rl.conditions_met(make_rows(), "cloudberry")[0]                       # dry mineral forest


def test_open_ground_berries_follow_their_ecology_when_not_literal():
    meadow = make_rows(ph=6.0, lc_tree=0.3, lc_tree_1km=0.4, gdd5=1100.0, built_dist_m=100.0)
    assert rl.rule_probability(meadow, literal=True)["raspberry"].iloc[0] == rl.P_RIGHT        # specified rule: disturbed is not boosted
    assert rl.rule_probability(meadow, literal=False)["raspberry"].iloc[0] == rl.P_UNDISTURBED  # ecology: disturbed open ground is boosted


def test_labels_follow_the_probabilities():
    n = 40000
    df = pd.concat([make_rows(n), make_rows(n, built_dist_m=100.0), make_rows(n, ph=7.5)], ignore_index=True)
    out = rl.label_dataset(df, seed=1)
    y = out["y_bilberry"].to_numpy()
    assert abs(y[:n].mean() - 0.50) < 0.01
    assert abs(y[n:2 * n].mean() - 0.30) < 0.01
    assert abs(y[2 * n:].mean() - 0.02) < 0.005


def test_labels_are_reproducible_and_binary():
    df = make_rows(500)
    a, b = rl.label_dataset(df, seed=7), rl.label_dataset(df, seed=7)
    assert a["y_bilberry"].equals(b["y_bilberry"])
    assert set(np.unique(a[[c for c in a.columns if c.startswith("y_")]].to_numpy())) <= {0, 1}


def test_missing_columns_raise_a_clear_error():
    try:
        rl.label_dataset(make_rows().drop(columns=["scd"]))
    except KeyError as e:
        assert "scd" in str(e)
    else:
        raise AssertionError("expected KeyError")


def test_every_species_has_rules_and_references_resolve():
    for name, rule in rl.RULES.items():
        assert rule["conditions"], name
        for cond in rule["conditions"]:
            refs = [cond["ref"]] if cond["kind"] == "range" else [cond["ref"]] + [c["ref"] for c in cond["conds"]]
            for ref in refs:
                first = ref.split(":")[0].split("+")[0].split(",")[0].split(" ")[0]
                assert first == "assumption" or first in rl.REFERENCES, (name, ref)


if __name__ == "__main__":
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    for t in tests:
        t()
        print("ok", t.__name__)
    print(len(tests), "tests passed")
