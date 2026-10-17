# Nordic habitat model: two trained runs, and how the app uses the better one

## Which model is used: `models/nordic-gbif-1.0`

Two Kaggle runs of the notebook exist. Both are scored on the same question: in held-out 1° blocks, do real GBIF sightings
score above the plant background? (0.5 is chance.)

| Berry | Run 2: GBIF labels, NASA POWER climate (`nordic-gbif-1.0`) | Run 1: rule labels, CHELSA climate (`nordic-rules-1.0`) |
|---|---|---|
| bilberry | **0.661** | 0.437 |
| lingonberry | **0.668** | 0.567 |
| cloudberry | **0.785** | 0.648 |
| raspberry | **0.689** | 0.578 |
| bog bilberry | **0.738** | 0.607 |
| cranberry | **0.702** | 0.553 |
| crowberry | **0.759** | 0.488 |
| wild strawberry | **0.629** | 0.501 |
| arctic bramble | **0.769** | 0.716 |
| sea buckthorn | **0.893** | 0.776 |
| mean | **0.729** | 0.587 |

Run 2 (Kaggle version 359145676, 10 h, completed) wins for every berry, and it is what the app and the inference service use. It is
the notebook as first committed (75561c3); its executed copy is `models/nordic-gbif-1.0/notebook.ipynb`, with its code
checked line for line against that commit. Run 1 is kept in `models/nordic-rules-1.0` for the record.

Run 2's limits:
* Its climate is NASA POWER at about 55 km, and climate carries most of its skill (permutation AUC drop 0.156 against soil 0.038,
  land cover 0.017, elevation 0.011, Finnish stand data none). Inside one search area the climate is the same everywhere, so the
  model's within-area differences come from soil, land cover and elevation only. They are often about as large as the disagreement
  between its five networks. The app reports this per berry as **local contrast** and says when differences are weak.
* Around Oulu nearly every place is in the top 1 % of the Nordic background for most berries, so the app ranks squares *within* an
  analysed area for the heatmap and pins, and quotes the Nordic index only for the area as a whole.
* Labels are presence-only sightings, so the output is relative habitat suitability, never a probability of finding berries.

## Run 1 (rule labels), kept for comparison

## The run

| | |
|---|---|
| Where | Kaggle notebook `umarrustamov/nordic-berry-habitat-model6c59f73292`, version 359152479, CPU, 81 min |
| Data | 29,473 GBIF presence cells (3,000 per berry, 2,473 sea buckthorn), 16,155 plant-background cells; 34,003 land places after dropping water |
| Labels | `LABEL_MODE = "rules"`: pseudo-labels from the written habitat rules (30 % / 50 % undisturbed / 2 %) |
| Model | 5-member ensemble, 670,506 parameters each; best validation AUC 0.796 to 0.801 (epochs 23 to 41) |
| File | `models/nordic-rules-1.0/berry_model.pt`, SHA-256 `03e2d41a…c74c` |

The run is marked failed on Kaggle, but only after the model was saved: the demo cell crashed when predicting a single
point, because a one-item list passed to pandas `groupby` gives tuple keys. That and a second bug (a point within half a DEM pixel of a
Copernicus tile edge indexed one row past the tile) are fixed in the notebook; neither affected training.

## What it learned

Test blocks held out by 1° squares. "Rules" scores how well the network recovers its own labels. "GBIF" asks whether real sightings
score above the plant background (0.5 = chance). It is presence-only and biased toward roads and towns, so it is a sanity check, not accuracy.

| Berry | Rules AUC | GBIF AUC | GBIF Boyce | Used for spots |
|---|---|---|---|---|
| bilberry | 0.831 | **0.437** | −0.50 | no (worse than chance; the stand baseline stays) |
| lingonberry | 0.823 | 0.567 | 0.41 | candidate only (the stand baseline stays) |
| cloudberry | 0.834 | 0.648 | 0.95 | yes |
| raspberry | 0.765 | 0.578 | 0.76 | yes |
| bog bilberry | 0.851 | 0.607 | 0.80 | yes |
| cranberry | 0.853 | 0.553 | 0.42 | yes |
| crowberry | 0.838 | 0.488 | −0.07 | no |
| wild strawberry | 0.831 | 0.501 | −0.02 | no |
| arctic bramble | 0.797 | 0.716 | 0.84 | yes |
| sea buckthorn | 0.745 | 0.776 | 0.64 | yes |

The network reproduces the rules as well as gradient boosting does (mean AUC 0.817 against 0.827). The rules themselves agree
only weakly with real sightings (AUC 0.48 to 0.59), and the network inherits that. Permutation importance puts soil first (AUC drop 0.108),
then land cover (0.088) and climate (0.037); terrain barely matters (0.001).

## Published spots (`app/Berryfinder/Core/GridModel.cs`)

This pipeline now publishes run 2's grid (`data/ml/oulu_grid_gbif.csv.gz`). It was first built for run 1, whose numbers are quoted below.

1. `notebooks/predict_region.py` scores the Oulu pilot square at 250 m: 8,677 land cells, shipped as `data/ml/oulu_grid.csv.gz`.
2. **Gate:** a berry gets spots from the model only if its GBIF AUC is ≥ 0.55 with a positive Boyce index, and it has no
   forest-stand baseline. Otherwise the model version is stored as `candidate` or `rejected`, visible on the admin dashboard.
3. **Habitat guard:** around Oulu, SoilGrids gives high organic carbon almost everywhere. The rule "carbon ≥ 150 g/kg means peat"
   therefore made 45 % of the region top-tier cloudberry ground, and 625 of its 869 best cells were mineral-soil forest by
   Finnish stand data. Spots must match the berry's basic habitat as the app describes it: mire by Finnish site class (or
   satellite wetland where stand data is missing) for cloudberry, cranberry and bog bilberry; open ground for raspberry; never
   towns or fields. After the guard, 95 % of the bog-berry cells are mires.
4. **Regional cut:** of the cells that pass, at most the best 10 % of the region's land per berry become spots; the best 3 %
   (index ≥ 97) are the "high" ones.
5. **Wording:** grid areas have `limited` evidence, so they read "Worth a try" or "Long shot", never "Promising". The guide weighs
   them at 0.8 against better-evidenced places, and a berry takes at most two of the three suggestions while others are in season.

## Making it better

* Train with `LABEL_MODE = "gbif"` (real presences against the plant background) and compare its GBIF AUC with this run.
* Replace the SoilGrids peat proxy in the rules with a mire layer (Finnish site class, or the Syke mire inventory).
* Field reports for every berry are now accepted; with enough of them, `calibrate_with_field_data` in the notebook fits real odds.

## Live analysis (`inference/`)

The map's **Analyze area** sends the tapped point and radius (1, 3, 5 or 10 km) to the inference service. That service runs run 2's
notebook code for features and preprocessing, then its ensemble, over 150 to 500 m squares of the circle. It returns them as GeoJSON
with candidate areas, and the map draws them as a heatmap or pins. A first 3 km analysis takes under a minute, and repeats come from
cache. Scoring the 10,000-cell Oulu grid takes about 3 minutes. See `inference/README.md`.
