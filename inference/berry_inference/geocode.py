"""Place search in Finland through OpenStreetMap Nominatim. The public instance allows one request a second and asks for
caching and a real User-Agent, so results are cached for a week and calls are spaced."""
from __future__ import annotations

import threading
import time

import requests

NOMINATIM = "https://nominatim.openstreetmap.org/search"
_cache: dict[str, tuple[float, list[dict]]] = {}
_lock = threading.Lock()
_last = 0.0


def search(query: str, language: str, user_agent: str, limit: int = 6) -> list[dict]:
    global _last
    query = " ".join(query.split())
    key = f"{language}:{query.lower()}"
    hit = _cache.get(key)
    if hit and time.time() - hit[0] < 7 * 24 * 3600:
        return hit[1]
    with _lock:
        wait = 1.1 - (time.time() - _last)
        if wait > 0:
            time.sleep(wait)
        _last = time.time()
        r = requests.get(NOMINATIM, params={"q": query, "countrycodes": "fi", "format": "jsonv2", "limit": limit, "accept-language": language},
                         headers={"User-Agent": user_agent}, timeout=15)
        r.raise_for_status()
    places = [{
        "name": p.get("name") or p.get("display_name", query).split(",")[0],
        "detail": p.get("display_name", ""),
        "lat": float(p["lat"]), "lon": float(p["lon"]),
        "kind": p.get("type"),
        "bbox": [float(v) for v in p["boundingbox"]] if p.get("boundingbox") else None,   # south, north, west, east
    } for p in r.json()]
    if len(_cache) > 5000:
        _cache.clear()
    _cache[key] = (time.time(), places)
    return places
