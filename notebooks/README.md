# Notebooks

`nordic_berry_habitat_model.ipynb` builds a multi-species PyTorch habitat model for Nordic wild berries from open datasets:
GBIF sightings, CHELSA 1 km climate normals, ISRIC SoilGrids, ESA WorldCover, the Copernicus 30 m DEM (terrain and microclimate
indices), and Finnish Forest Centre stand data for Finland.

It has been trained twice on Kaggle. The better run (real GBIF labels) is `models/nordic-gbif-1.0`, and the app and the inference
service use it. `handbook/ml-model.md` compares the two runs. To train again:

```bash
pip install -r notebooks/requirements.txt
jupyter lab notebooks/nordic_berry_habitat_model.ipynb
```

* Set `QUICK = True` in the setup cell for a smoke test (a few hundred places) before the full run.
* Downloads are cached in `data/cache/` and resume after interruption. Climate and soil are the slowest steps.
* Outputs: `artifacts/berry_model.pt` (ensemble, preprocessing, percentiles) and the held-out-block evaluation tables.

## Labels (`LABEL_MODE`)

| Mode | Labels | What the result means |
|---|---|---|
| `"rules"` (default) | Written by `rule_labels.py` from literature-based "right conditions" per berry: 30 % chance of a berry where conditions are right, 50 % if the place is also undisturbed, 2 % elsewhere, drawn with a fixed seed | The network recovers **assumptions**. Not real berry distributions. The notebook compares the rules and the trained network with real GBIF records as a reality check |
| `"gbif"` | Real presence-only GBIF records against a plant background | Relative habitat suitability, not a probability |

## Files

The notebook is **self-contained**: upload just the `.ipynb` to your server. The labelling script (section 4b) and the terrain maths (section 4.2) are inside it as ordinary cells, so you can edit the rules and the three probabilities there.

The `.py` files are the tested source those cells were generated from. They are optional and only needed to run the tests or the CLI, and they do not update if you edit the notebook's copy:

* `rule_labels.py`: the labelling script. Every threshold is tagged with its source paper or `assumption`. `python rule_labels.py` prints the rules.
* `terrain_features.py`: slope, aspect, valley/ridge position and relief from a window of elevation values.
* `test_rule_labels.py`, `test_terrain_features.py`: tests on synthetic fixtures (`python test_rule_labels.py`, `python test_terrain_features.py`, or `pytest`).

## Using a trained model in the app

Put the run in `models/<version>/`: `berry_model.pt`, the executed `notebook.ipynb` (its code is what loads the model) and a
`card.json` with the run's held-out results. Then:

* **Live analysis:** point the inference service at it (`BERRY_MODEL_DIR`, see `inference/README.md`).
* **Published spots:** score the pilot grid and update `data/ml/berry_model_card.json` to the new version and grid file:

```bash
python predict_region.py --model-dir ../models/nordic-gbif-1.0 --out ../data/ml/oulu_grid_gbif.csv.gz   # Oulu pilot, 250 m
```

On its next start the app sees an unknown version and queues a `model_grid` import. That stores the cells, applies the gate in
`app/Berryfinder/Core/GridModel.cs` and publishes picking areas, replacing older grid versions that the system had promoted.
