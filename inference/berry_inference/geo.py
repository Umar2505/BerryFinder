"""Geometry for an analysis: the grid of prediction cells inside a search circle, their polygons, and candidate areas.

Cells are squares on the Finnish national grid (ETRS-TM35FIN, EPSG:3067, metres), so a 250 m cell really is 250 m on the ground.
Their centres are what the model scores; their corners, transformed to WGS84, are what the map draws, so the heatmap lines up
with the scored places at every zoom level.
"""
from __future__ import annotations

import math
from dataclasses import dataclass

from pyproj import Transformer

TO_TM35 = Transformer.from_crs("EPSG:4326", "EPSG:3067", always_xy=True)
TO_WGS84 = Transformer.from_crs("EPSG:3067", "EPSG:4326", always_xy=True)

# Finland (the national grid, the Finnish stand data and the app's coverage)
FINLAND_BBOX = (19.0, 59.7, 31.7, 70.1)   # west, south, east, north

# Search radius -> cell size. Every option scores roughly 300 to 1,300 cells, which keeps an analysis to about a minute.
RADIUS_STEPS = {1000: 100, 3000: 150, 5000: 250, 10000: 500}


def in_finland(lat: float, lon: float) -> bool:
    w, s, e, n = FINLAND_BBOX
    return s <= lat <= n and w <= lon <= e


def step_for_radius(radius_m: int) -> int:
    if radius_m not in RADIUS_STEPS:
        raise ValueError(f"radius must be one of {sorted(RADIUS_STEPS)}")
    return RADIUS_STEPS[radius_m]


@dataclass(frozen=True)
class Cell:
    i: int            # column and row on the analysis grid (0, 0 is the south-west corner)
    j: int
    x: float          # centre, EPSG:3067 metres
    y: float
    lat: float
    lon: float


def analysis_grid(lat: float, lon: float, radius_m: int, step_m: int) -> list[Cell]:
    """Cells whose centres lie inside the circle. The grid is snapped to multiples of the step, so the same place always gets
    the same cells (and the download caches hit) whatever point inside it the user tapped."""
    cx, cy = TO_TM35.transform(lon, lat)
    x0 = math.floor((cx - radius_m) / step_m) * step_m
    y0 = math.floor((cy - radius_m) / step_m) * step_m
    n = int(math.ceil(2 * radius_m / step_m)) + 2
    xs, ys, ij = [], [], []
    for j in range(n):
        for i in range(n):
            x = x0 + (i + 0.5) * step_m
            y = y0 + (j + 0.5) * step_m
            if (x - cx) ** 2 + (y - cy) ** 2 <= radius_m ** 2:
                xs.append(x)
                ys.append(y)
                ij.append((i, j))
    lons, lats = TO_WGS84.transform(xs, ys)
    return [Cell(i, j, x, y, la, lo) for (i, j), x, y, la, lo in zip(ij, xs, ys, lats, lons)]


def cell_polygon(x: float, y: float, step_m: float) -> list[list[float]]:
    """The square around a cell centre as a closed WGS84 ring, [lon, lat] pairs, counter-clockwise (GeoJSON order)."""
    h = step_m / 2
    corners = [(x - h, y - h), (x + h, y - h), (x + h, y + h), (x - h, y + h), (x - h, y - h)]
    lons, lats = TO_WGS84.transform([c[0] for c in corners], [c[1] for c in corners])
    return [[round(lo, 6), round(la, 6)] for lo, la in zip(lons, lats)]


def distance_m(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    r = 6371000.0
    p1, p2 = math.radians(lat1), math.radians(lat2)
    dp, dl = p2 - p1, math.radians(lon2 - lon1)
    a = math.sin(dp / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(dl / 2) ** 2
    return 2 * r * math.asin(math.sqrt(a))


def components(cells: list[tuple[int, int]]) -> list[list[tuple[int, int]]]:
    """Groups of grid cells that touch along an edge or a corner: each group is one candidate area."""
    remaining = set(cells)
    groups = []
    while remaining:
        start = remaining.pop()
        group, stack = [start], [start]
        while stack:
            i, j = stack.pop()
            for di in (-1, 0, 1):
                for dj in (-1, 0, 1):
                    nb = (i + di, j + dj)
                    if nb in remaining:
                        remaining.remove(nb)
                        group.append(nb)
                        stack.append(nb)
        groups.append(sorted(group))
    return sorted(groups, key=lambda g: g[0])
