# Data-source and licensing register

Every connector records provenance (endpoint, licence, attribution, coverage, last attempt and success, record counts) in the
`data_sources` table. The admin dashboard shows the same register live. This file is the written version; **verify licences again before
launch** — this is engineering diligence, not legal advice.

| Source | What we take | Licence (as published) | Commercial use | Status |
|---|---|---|---|---|
| Finnish Forest Centre open forest resource data (WFS `v2:stand`) | Stand polygons, site fertility, soil type, drainage, development class, species shares, age, basal area, height, measurement dates | CC BY 4.0 | Yes, with attribution "Suomen metsäkeskus" | Active |
| GTK superficial deposits (WFS 1:20k / 1:50k / 1:200k) | Surface and base deposit class per polygon | CC BY 4.0 | Yes, with attribution "Geologian tutkimuskeskus (GTK)" | Active. No measured pH, nutrients or moisture exist in this service; they stay missing |
| FMI open data (WFS `fmi::observations::weather::daily::simple`) | Daily temperature, minimum, precipitation, snow, ground minimum at two Oulu stations | CC BY 4.0 | Yes, with attribution "Ilmatieteen laitos" | Active. Station data only |
| **NASA POWER** (MERRA-2 / SYN1DEG daily API) | Gridded daily T2M, T2M_MIN, PRECTOTCORR, root-zone soil wetness (GWETROOT), solar radiation | NASA open data policy; POWER asks for acknowledgement ("NASA Prediction Of Worldwide Energy Resources") | Yes | Active. A **model grid** of about 55 km cells, never presented as a measurement; used as labelled fallback and context |
| Copernicus Data Space STAC (Sentinel-2 L2A) | Scene catalogue only (date, cloud cover, footprint) | Copernicus Sentinel data licence (free, full and open) | Yes, with "Contains modified Copernicus Sentinel data" | Catalogue active. Pixel download and NDVI/NDMI compositing need CDSE S3 credentials and the Python raster pipeline: **pending** |
| Syke protected areas (INSPIRE WFS) | State and private nature reserve polygons | CC BY 4.0 | Yes, with attribution "Syke" | Active. Used to warn and, for strict nature reserves, exclude |
| OpenStreetMap via Overpass | Roads, forest tracks, paths | ODbL 1.0 | Yes; share-alike applies to published derived databases | Active (public Overpass is fair-use; self-host for production) |
| OpenStreetMap Nominatim | Place search | ODbL; the public service's usage policy allows light use only | Light | Active with caching and 1 request/second; run your own for production |
| OSRM public demo server | Road driving times | ODbL data; the demo server is **not for production** | Demo only | Active for the pilot; replace with a self-hosted OSRM or a commercial router before launch |
| OpenFreeMap vector tiles | Basemap | OpenMapTiles (CC BY 4.0) + OpenStreetMap (ODbL), free without a key | Yes, with attribution | Active |
| GBIF occurrence search | Presence-only records, CC0 and CC BY 4.0 only | Per record | Yes, with citation | Active. Never used as absence, abundance or yield; only a sanity check |
| Luke berry monitoring (marjahavainnot.fi, forecasts) | — | **Not established.** Publicly viewable is not the same as licensed for commercial ML training | Unknown | **Pending authorization.** No download API or licence found. A data request to Luke is the next step; until then it is not used |
| National Land Survey of Finland (aerial imagery, DEM) | Aerial basemap; terrain features | CC BY 4.0 | Yes, with attribution | **Pending credentials**: needs a free API key (`NLS_API_KEY`) |
| BerryFinder field observations | User reports | Contributor consent per report | Only with consent | Active. Private by default |

## NIST

NIST publishes no forest, soil or berry data, so none is ingested. We use **NIST's Statistical Reference Datasets (StRD)** for
validation instead: the *Norris* and *Longley* linear-regression datasets, with NIST's certified values, are compiled into the test
suite (`tests/Berryfinder.Tests/NistReferenceTests.cs`, fixtures in `Fixtures/nist/`, unedited). They check the linear solver inside
the logistic-regression fit against independent ground truth: 12 to 14 correct digits on Norris and 7 to 9 on the ill-conditioned
Longley. NIST StRD is a U.S. government work, <https://www.itl.nist.gov/div898/strd/>.

## Required attributions (also shown in the app footer and map)

Forest data © Suomen metsäkeskus (CC BY 4.0) · Soil © GTK (CC BY 4.0) · Weather © Ilmatieteen laitos (CC BY 4.0) ·
Protected areas © Syke · Weather model: NASA POWER · Contains modified Copernicus Sentinel data · © OpenStreetMap contributors
