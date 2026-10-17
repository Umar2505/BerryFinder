"""Terrain and microclimate indices from a window of elevation values (metres).

Used by the notebook on 21 x 21 windows of the 30 m Copernicus DEM. Kept here, free of any network code, so it can be tested.
"""
import math

import numpy as np

TOPO_COLS = ["elevation", "slope", "northness", "eastness", "tpi_local", "tpi_broad", "relief"]


def terrain_indices(a, dx, dy, r, c):
    """a: elevation window in metres (rows run south, columns run east); (r, c): the place inside it; dx and dy: cell size in metres.

    Returns, in TOPO_COLS order: elevation, slope (degrees), northness and eastness (cosine and sine of the direction the slope
    faces, measured clockwise from north; 0 on flat ground), tpi_local (elevation minus the mean of the ~200 m neighbourhood;
    negative in a hollow, positive on a ridge), tpi_broad (same for the whole window, ~600 m) and relief (max minus min).
    """
    gy, gx = np.gradient(a, dy, dx)                     # gy: change per metre going south, gx: change per metre going east
    centre_rows, centre_cols = slice(max(r - 2, 0), r + 3), slice(max(c - 2, 0), c + 3)
    gx_c, gy_c = np.nanmean(gx[centre_rows, centre_cols]), np.nanmean(gy[centre_rows, centre_cols])
    slope = math.degrees(math.atan(math.hypot(gx_c, gy_c)))
    if slope >= 1.0:
        aspect = math.atan2(-gx_c, gy_c)                # downhill east = -gx, downhill north = +gy (rows run south)
        northness, eastness = math.cos(aspect), math.sin(aspect)
    else:
        northness = eastness = 0.0                      # flat ground has no aspect
    centre = a[r, c]
    local = a[max(r - 3, 0):r + 4, max(c - 3, 0):c + 4]
    return [centre, slope, northness, eastness, centre - np.nanmean(local), centre - np.nanmean(a), np.nanmax(a) - np.nanmin(a)]
