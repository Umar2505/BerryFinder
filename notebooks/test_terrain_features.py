"""Tests for terrain_features.py (synthetic elevation surfaces). Run with `python test_terrain_features.py` or `pytest`."""
import math

import numpy as np

from terrain_features import TOPO_COLS, terrain_indices

N, C = 21, 10
DX, DY = 26.0, 31.0        # roughly a 30 m cell at 65 degrees north
rows, cols = np.mgrid[0:N, 0:N]


def run(a):
    return dict(zip(TOPO_COLS, terrain_indices(a.astype(float), DX, DY, C, C)))


def test_flat_ground_has_no_slope_or_aspect():
    r = run(np.full((N, N), 100.0))
    assert r["slope"] == 0 and r["northness"] == 0 and r["eastness"] == 0 and r["relief"] == 0 and r["elevation"] == 100


def test_slope_is_the_angle_of_the_surface():
    a = 50 + 0.10 * DX * cols                            # rises 0.1 m per metre going east
    assert abs(run(a)["slope"] - math.degrees(math.atan(0.10))) < 0.01


def test_slope_rising_to_the_east_faces_west():
    r = run(50 + 0.10 * DX * cols)
    assert abs(r["eastness"] + 1) < 1e-6 and abs(r["northness"]) < 1e-6


def test_slope_rising_to_the_south_faces_north():
    r = run(50 + 0.10 * DY * rows)                       # rows run south, so height rises going south
    assert abs(r["northness"] - 1) < 1e-6 and abs(r["eastness"]) < 1e-6


def test_slope_rising_to_the_north_faces_south():
    r = run(50 - 0.10 * DY * rows)
    assert abs(r["northness"] + 1) < 1e-6


def test_slope_rising_to_the_west_faces_east():
    r = run(50 - 0.10 * DX * cols)
    assert abs(r["eastness"] - 1) < 1e-6


def test_a_hollow_has_negative_position_and_a_hill_positive():
    dist2 = ((rows - C) * DY) ** 2 + ((cols - C) * DX) ** 2
    hill = 100 + 40 * np.exp(-dist2 / (2 * 120.0 ** 2))
    bowl = 100 - 40 * np.exp(-dist2 / (2 * 120.0 ** 2))
    assert run(hill)["tpi_local"] > 0 and run(hill)["tpi_broad"] > 0
    assert run(bowl)["tpi_local"] < 0 and run(bowl)["tpi_broad"] < 0


def test_relief_is_the_range_of_the_window():
    a = np.full((N, N), 100.0)
    a[0, 0], a[-1, -1] = 80.0, 130.0
    assert run(a)["relief"] == 50


def test_window_clipped_at_a_tile_edge_still_works():
    a = (50 + 0.05 * DX * cols)[:, :14]                  # the place sits near the right edge of the clipped window
    r = dict(zip(TOPO_COLS, terrain_indices(a.astype(float), DX, DY, 10, 10)))
    assert abs(r["slope"] - math.degrees(math.atan(0.05))) < 0.01


if __name__ == "__main__":
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    for t in tests:
        t()
        print("ok", t.__name__)
    print(len(tests), "tests passed")
