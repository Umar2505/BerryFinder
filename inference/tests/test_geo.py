import math

import pytest

from berry_inference import geo
from berry_inference.habitat import describe, in_habitat, is_mire


def test_grid_cells_lie_inside_the_circle_and_cover_it():
    lat, lon, r = 65.03, 25.75, 3000
    step = geo.step_for_radius(r)
    cells = geo.analysis_grid(lat, lon, r, step)
    cx, cy = geo.TO_TM35.transform(lon, lat)
    assert all((c.x - cx) ** 2 + (c.y - cy) ** 2 <= r * r for c in cells)
    expected = math.pi * r * r / step ** 2
    assert abs(len(cells) - expected) / expected < 0.03          # the cells tile the disc


def test_cell_centres_round_trip_to_their_coordinates():
    for c in geo.analysis_grid(61.5, 23.8, 1000, 100)[:50]:
        x, y = geo.TO_TM35.transform(c.lon, c.lat)
        assert abs(x - c.x) < 0.01 and abs(y - c.y) < 0.01


def test_the_grid_is_snapped_so_nearby_taps_share_cells():
    a = {(round(c.x), round(c.y)) for c in geo.analysis_grid(65.03, 25.75, 1000, 100)}
    b = {(round(c.x), round(c.y)) for c in geo.analysis_grid(65.0301, 25.7502, 1000, 100)}
    assert len(a & b) / len(a) > 0.95
    assert all(x % 100 == 50 and y % 100 == 50 for x, y in a)


def test_cell_polygons_are_closed_squares_around_their_centre():
    c = geo.analysis_grid(65.03, 25.75, 1000, 100)[0]
    ring = geo.cell_polygon(c.x, c.y, 100)
    assert ring[0] == ring[-1] and len(ring) == 5
    xs, ys = geo.TO_TM35.transform([p[0] for p in ring[:4]], [p[1] for p in ring[:4]])
    assert max(xs) - min(xs) == pytest.approx(100, abs=0.1)
    assert max(ys) - min(ys) == pytest.approx(100, abs=0.1)
    assert sum(xs) / 4 == pytest.approx(c.x, abs=0.1) and sum(ys) / 4 == pytest.approx(c.y, abs=0.1)


def test_radius_options_are_fixed():
    assert geo.step_for_radius(5000) == 250
    with pytest.raises(ValueError):
        geo.step_for_radius(50000)


def test_finland_check():
    assert geo.in_finland(65.01, 25.47)
    assert not geo.in_finland(59.33, 18.07)       # Stockholm


def test_distance():
    assert geo.distance_m(65.0, 25.0, 65.0, 25.0) == 0
    assert geo.distance_m(65.0, 25.0, 66.0, 25.0) == pytest.approx(111_195, rel=0.001)


def test_touching_cells_form_one_area():
    groups = geo.components([(0, 0), (1, 1), (5, 5), (5, 6), (9, 0)])
    assert sorted(len(g) for g in groups) == [1, 2, 2]


# The habitat rule mirrors ModelGate.InHabitat in the app (Core/GridModel.cs); these cases match its tests
def test_bog_berries_need_a_mire():
    assert in_habitat("cloudberry", {"fi_site_main": 3, "lc_tree": 0.5})
    assert not in_habitat("cloudberry", {"fi_site_main": 1, "lc_tree": 0.95})
    assert in_habitat("cranberry", {"lc_wetland": 0.4})
    assert not in_habitat("cranberry", {"lc_wetland": 0.05})


def test_nobody_is_sent_into_a_town_or_a_field():
    for species in ("raspberry", "sea_buckthorn", "arctic_bramble", "bilberry"):
        assert not in_habitat(species, {"lc_tree": 0.5, "lc_built": 0.4})
        assert not in_habitat(species, {"lc_tree": 0.1, "lc_crop": 0.8})


def test_missing_values_are_not_counted_as_present():
    assert not is_mire({"fi_site_main": float("nan"), "lc_wetland": float("nan")})


def test_habitat_words():
    assert describe({"lc_tree": 0.9, "fi_prop_pine": 0.8})["en"] == "Pine forest"
    assert describe({"lc_tree": 0.9, "fi_prop_pine": 0.4, "fi_prop_spruce": 0.4})["en"] == "Coniferous forest"
    assert describe({"lc_tree": 0.1, "mire": 1.0})["en"] == "Open bog"
    assert describe({"lc_tree": 0.1})["en"] == "Open ground"
