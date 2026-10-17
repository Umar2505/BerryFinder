"""What a place is, in plain words, and whether it is the kind of ground a berry grows on.

The habitat check mirrors ModelGate.InHabitat in app/Berryfinder/Core/GridModel.cs, so a spot the app publishes and a spot
an analysis finds follow the same rule: a berry is only suggested on its basic habitat (a mire for cloudberry, open ground for
raspberry), never in towns or fields. Keep the two in step.
"""
from __future__ import annotations

import math


def _v(h: dict, key: str):
    v = h.get(key)
    return None if v is None or (isinstance(v, float) and math.isnan(v)) else float(v)


def is_mire(h: dict) -> bool:
    """Finnish site class 2 to 5 (spruce mire, pine mire, open bog, fen); without stand data, wetland in satellite land cover."""
    site = _v(h, "fi_site_main")
    if site is not None:
        return 2 <= site <= 5
    return (_v(h, "lc_wetland") or 0) >= 0.2


def in_habitat(species: str, h: dict) -> bool:
    if (_v(h, "lc_built") or 0) >= 0.3 or (_v(h, "lc_crop") or 0) >= 0.5:
        return False     # towns, yards and fields
    mire = is_mire(h)
    tree = _v(h, "lc_tree") or 0
    if species in ("cloudberry", "cranberry", "bog_bilberry"):
        return mire
    if species in ("raspberry", "wild_strawberry"):
        return tree <= 0.7 and not mire
    if species == "arctic_bramble":
        return (_v(h, "lc_wetland") or 0) + (_v(h, "lc_grass") or 0) >= 0.15
    if species == "sea_buckthorn":
        return tree <= 0.3
    if species in ("bilberry", "lingonberry"):
        return tree >= 0.4
    return True


def describe(h: dict) -> dict:
    """A short habitat label from averaged features: code plus English and Finnish words."""
    tree = _v(h, "lc_tree") or 0
    mire_share = _v(h, "mire")
    mire = (mire_share or 0) >= 0.5 if mire_share is not None else is_mire(h)
    if mire:
        return {"code": "wooded_mire", "en": "Wooded bog", "fi": "Puustoinen suo"} if tree >= 0.4 else {"code": "bog", "en": "Open bog", "fi": "Avosuo"}
    if tree < 0.3:
        return {"code": "open", "en": "Open ground", "fi": "Avoin maasto"}
    if tree < 0.6:
        return {"code": "edge", "en": "Forest edge or clearing", "fi": "Metsänreuna tai aukko"}
    pine, spruce, other = _v(h, "fi_prop_pine"), _v(h, "fi_prop_spruce"), _v(h, "fi_prop_other")
    if pine is not None and pine >= 0.6:
        return {"code": "pine", "en": "Pine forest", "fi": "Mäntymetsä"}
    if spruce is not None and spruce >= 0.6:
        return {"code": "spruce", "en": "Spruce forest", "fi": "Kuusimetsä"}
    if other is not None and other >= 0.5:
        return {"code": "broadleaf", "en": "Broadleaf forest", "fi": "Lehtimetsä"}
    if pine is not None and spruce is not None and pine + spruce >= 0.7:
        return {"code": "conifer", "en": "Coniferous forest", "fi": "Havumetsä"}
    return {"code": "forest", "en": "Forest", "fi": "Metsä"}
