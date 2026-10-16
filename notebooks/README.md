# Notebooks

`nordic_berry_habitat_model.ipynb` builds a multi-species PyTorch habitat model for Nordic wild berries from open datasets
(GBIF, NASA POWER, ISRIC SoilGrids, ESA WorldCover, Copernicus DEM elevation, and Finnish Forest Centre stand data for Finland).

It was written but **not executed**; run it on your training server:

```bash
pip install -r notebooks/requirements.txt
jupyter lab notebooks/nordic_berry_habitat_model.ipynb
```

* Set `QUICK = True` in the setup cell for a smoke test (a few hundred places) before the full run.
* Downloads are cached in `data/cache/` and resume after interruption. The soil step makes many small reads and is the slowest.
* Outputs: `artifacts/berry_model.pt` (ensemble + preprocessing + score percentiles) and a held-out-block evaluation table.
* The model predicts relative habitat suitability from presence-only records, not yield or probability; see the first and last sections.
