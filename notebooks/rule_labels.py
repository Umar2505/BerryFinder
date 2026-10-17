"""Rule-based pseudo-labels for Nordic wild berries.

Goes through a combined environmental table (one row per place) and, for every berry, decides:

  1. Are the "right conditions" met?  (soil, climate, land cover, terrain thresholds, one rule set per species)
  2. If yes, a berry is assumed present with probability P_RIGHT (30 %).
  3. If the place is also undisturbed (no built-up or paved surface nearby, little farmland), the probability is
     raised to P_UNDISTURBED (50 %).  This is the assumption requested for this project.
  4. Everywhere else the probability is a small P_OTHER (2 %), so the labels are not perfectly deterministic.

A label is then drawn at random from that probability with a fixed seed, giving 0/1 labels for every row and species.

IMPORTANT: these are ASSUMPTIONS written down as rules, not observations. The thresholds are informed by the papers listed
in REFERENCES, but most numeric cut-offs are the author's reading of that literature, flagged "assumption" below.
A model trained on these labels learns the rules; it does not learn where berries actually grow. Check the result against
real records (the notebook compares it with GBIF) and replace the rules with field data when you have it.

Usage:
    import rule_labels
    labelled = rule_labels.label_dataset(df, seed=42)      # adds rule_ok_*, p_rule_* and y_* columns
    rule_labels.describe_rules("cloudberry")
"""
import numpy as np
import pandas as pd

P_RIGHT = 0.30            # probability of a berry where the species' conditions are right
P_UNDISTURBED = 0.50      # ... and the place is undisturbed (deep in forest, no roads or buildings nearby)
P_OTHER = 0.02            # everywhere else (set to 0.0 for fully deterministic labels)

BUILT_FREE_M = 500.0      # no built-up surface within this distance counts as undisturbed
CROP_MAX = 0.05           # at most 5 % cropland within about 1 km
OPEN_TREE_MAX = 0.60      # below this tree share (1 km) a place counts as open or edge land

REFERENCES = {
    "Miina2009": "Miina, Hotanen & Salo (2009), Silva Fennica, doi:10.14214/sf.181: bilberry abundance and yield in Finnish "
                 "mineral-soil forests; yield in mature pine stands about twice that of spruce stands. https://silvafennica.fi/pdf/181",
    "Turtiainen2013": "Turtiainen, Miina, Salo & Hotanen (2013), Silva Fennica: cowberry (lingonberry) cover and yield depend on "
                      "site fertility and stand basal area. https://silvafennica.fi/article/1005",
    "Turtiainen2016": "Turtiainen, Miina, Salo & Hotanen (2016), Silva Fennica: bilberry cover highest in mesic heath and fell "
                      "forests, lower on peatlands. https://silvafennica.fi/article/1573",
    "MiinaMgmt": "Miina, Bohlin, Lind et al., Silva Fennica article 10573: berry yields under forest management; lower removals "
                 "favour bilberry, higher removals lingonberry. https://silvafennica.fi/article/10573",
    "CloudberryLatvia": "Habitat characteristics and mineral nutrition of Rubus chamaemorus in Latvia (PMC): acidic, organic, "
                        "little-decomposed peat. https://www.ncbi.nlm.nih.gov/pmc/articles/PMC9920050/",
    "CloudberryLaval": "Bellemare et al. / Theroux Rancourt et al., Universite Laval: cloudberry substrate pH 3.5 to 4.5 and water "
                       "table 30 to 50 cm. https://www.gret-perg.ulaval.ca/fileadmin/Fichiers/centre_recherche/Bellemare_etal_MP_2009_02.pdf",
    "USFSEricaceae": "USDA Forest Service Ericaceae note: Vaccinium species are calcifuges on acid, well-drained, organic soils. "
                     "https://www.fs.usda.gov/Internet/FSE_DOCUMENTS/fseprd1181067.pdf",
    "LingonberryReview": "Review of environmental conditions for wild lingonberry (Notulae Scientia Biologicae): podzols and cambisols, "
                         "moist but well-drained acid soils. https://notulaebiologicae.ro/index.php/nsb/article/view/12149",
    "EUNISHeath": "EUNIS sub-boreal Vaccinium heaths: bilberry, cowberry and Empetrum heaths. https://eunis.eea.europa.eu/habitats/22639",
    "EUNISBog": "EUNIS raised bog: Vaccinium uliginosum, Empetrum nigrum and Rubus chamaemorus are diagnostic. "
                "https://eunis.eea.europa.eu/habitats/32002",
    "RubusIdaeus": "Rubus idaeus species profile (ACRRE, Univ. of Alberta): open woods, clearings and edges, early successional, "
                   "slightly acid to near-neutral. https://acrre.ualberta.ca/acrre/wp-content/uploads/sites/45/2018/04/Rubus_idaeus.pdf",
    "RubusArcticus": "Rubus arcticus species profile (ACRRE): coarse soils at pH 5 to 7, no shade tolerance, favours man-made open "
                     "habitats in Finland, avoids oceanic climate in Scandinavia. "
                     "https://acrre.ualberta.ca/acrre/wp-content/uploads/sites/45/2018/04//Rubus_arcticus.pdf",
    "EdgeEcotone": "Study of a bilberry pine forest and clear-cut ecotone: interior forest had the highest bilberry and lingonberry "
                   "abundance and yield, the edge zone significantly less (citation details not verified). "
                   "https://archivog.com/0033-9946/article/view/674478",
    "SLUClearcuts": "SLU thesis on 935 Swedish clear-cuts: berry production low on young cuts, peaking around year 8. "
                    "https://stud.epsilon.slu.se/13481/",
}


# ----------------------------------------------------------------------------------------------- rule building blocks
def between(col, lo, hi, ref, optional=False):
    return {"kind": "range", "col": col, "lo": lo, "hi": hi, "ref": ref, "optional": optional}


def at_least(col, lo, ref, optional=False):
    return between(col, lo, np.inf, ref, optional)


def at_most(col, hi, ref, optional=False):
    return between(col, -np.inf, hi, ref, optional)


def any_of(label, *conds, ref="assumption"):
    return {"kind": "any", "label": label, "conds": list(conds), "ref": ref}


# Column units: ph (pH in water), soc (g/kg), sand (%), lc_* (share 0-1 within about 200 m), lc_*_1km (share within 500 m),
# gdd5 (degree-days above 5 C), gsl (growing season days), scd (snow cover days), precip_ann (mm), t_range (C), elevation (m).
RULES = {
    "bilberry": dict(boost="undisturbed", conditions=[
        between("ph", 3.5, 5.6, "USFSEricaceae+assumption"),
        at_least("lc_tree", 0.40, "Turtiainen2016"),
        at_most("lc_wetland", 0.25, "Turtiainen2016"),
        at_least("gsl", 90, "assumption"),
        at_least("scd", 60, "assumption: snow cover shelters it from winter frost"),
        at_least("precip_ann", 450, "assumption"),
        between("fi_fertility", 2, 4, "Miina2009,Turtiainen2016: mesic heath (Finnish stand data only)", optional=True),
    ]),
    "lingonberry": dict(boost="undisturbed", conditions=[
        between("ph", 3.3, 5.6, "USFSEricaceae+LingonberryReview"),
        at_least("lc_tree", 0.20, "EUNISHeath"),
        at_most("lc_wetland", 0.30, "Turtiainen2013"),
        at_least("sand", 35, "LingonberryReview: well-drained coarse soils"),
        at_least("gsl", 80, "assumption"),
        at_least("precip_ann", 400, "assumption"),
        between("fi_fertility", 3, 5, "Turtiainen2013: drier heaths (Finnish stand data only)", optional=True),
    ]),
    "cloudberry": dict(boost="undisturbed", conditions=[
        between("ph", 3.2, 5.0, "CloudberryLaval (pH 3.5 to 4.5) plus margin"),
        any_of("acidic peat or bog", at_least("soc", 150, "CloudberryLatvia"), at_least("lc_wetland", 0.35, "EUNISBog")),
        between("gdd5", 300, 1500, "assumption: circumboreal, scarce in the warm south"),
        at_least("scd", 100, "assumption"),
        at_least("precip_ann", 450, "assumption"),
    ]),
    "cranberry": dict(boost="undisturbed", conditions=[
        between("ph", 3.0, 5.0, "EUNISBog"),
        any_of("bog or wet peat", at_least("soc", 150, "EUNISBog"), at_least("lc_wetland", 0.30, "EUNISBog")),
        at_least("gsl", 90, "assumption"),
        at_least("precip_ann", 450, "assumption"),
    ]),
    "bog_bilberry": dict(boost="undisturbed", conditions=[
        between("ph", 3.0, 5.2, "EUNISBog"),
        any_of("bog, peat or fell heath", at_least("lc_wetland", 0.25, "EUNISBog"), at_least("soc", 150, "EUNISBog"),
               at_least("elevation", 350, "assumption: fell heaths")),
        at_most("gdd5", 1500, "assumption: cool-climate species"),
        at_least("scd", 100, "assumption"),
    ]),
    "crowberry": dict(boost="undisturbed", conditions=[
        between("ph", 3.3, 5.2, "EUNISHeath"),
        at_most("lc_tree", 0.70, "EUNISHeath: open heaths and dry pine forest"),
        at_most("lc_wetland", 0.40, "assumption"),
        any_of("dry coarse soil or upland", at_least("sand", 35, "assumption"), at_least("elevation", 300, "assumption")),
        at_most("gdd5", 1800, "assumption"),
        at_least("scd", 80, "assumption"),
    ]),
    "raspberry": dict(boost="disturbed", conditions=[
        between("ph", 5.0, 7.2, "RubusIdaeus: slightly acid to near-neutral, nutrient-rich"),
        between("lc_tree", 0.10, 0.70, "RubusIdaeus: open woods, clearings and edges"),
        at_most("lc_wetland", 0.15, "assumption"),
        at_most("soc", 120, "assumption: mineral soil"),
        at_least("gdd5", 700, "assumption"),
        at_least("precip_ann", 450, "assumption"),
    ]),
    "wild_strawberry": dict(boost="disturbed", conditions=[
        between("ph", 5.0, 7.5, "assumption: no source found"),
        at_most("lc_tree", 0.60, "assumption: no source found"),
        at_most("lc_wetland", 0.10, "assumption"),
        at_least("gdd5", 900, "assumption"),
        at_least("gsl", 120, "assumption"),
    ]),
    "arctic_bramble": dict(boost="disturbed", conditions=[
        between("ph", 4.8, 7.0, "RubusArcticus: pH 5 to 7"),
        at_least("sand", 35, "RubusArcticus: coarse soils"),
        any_of("moist open ground", at_least("lc_wetland", 0.05, "RubusArcticus"), at_least("lc_grass", 0.10, "RubusArcticus")),
        between("gdd5", 300, 1500, "assumption"),
        at_least("scd", 100, "assumption"),
        at_least("t_range", 20, "RubusArcticus: avoids oceanic climate in Scandinavia"),
    ]),
    "sea_buckthorn": dict(boost="disturbed", conditions=[
        between("ph", 5.0, 8.5, "assumption: horticultural sources report a wide pH tolerance (not verified here)"),
        at_most("elevation", 30, "assumption: coastal lowland (no distance-to-coast layer yet)"),
        at_least("sand", 40, "assumption: sandy or gravelly shores"),
        at_most("lc_tree", 0.30, "assumption: needs full light"),
        at_least("gdd5", 600, "assumption"),
    ]),
}

# Species that thrive on open, disturbed ground. With literal=True (the default) the "undisturbed = 50 %" rule is applied to them
# too, exactly as specified; with literal=False they get their 50 % in disturbed, open or edge places instead, which matches
# their ecology (RubusIdaeus, RubusArcticus).
DISTURBANCE_ADAPTED = {s for s, r in RULES.items() if r["boost"] == "disturbed"}


# ----------------------------------------------------------------------------------------------------- evaluation
def _eval(df, cond):
    if cond["kind"] == "any":
        out = np.zeros(len(df), dtype=bool)
        for sub in cond["conds"]:
            out |= _eval(df, sub)
        return out
    v = df[cond["col"]].to_numpy(dtype=float)
    ok = (v >= cond["lo"]) & (v <= cond["hi"])        # NaN compares False: an unknown value is not "right conditions"
    if cond["optional"]:
        ok = ok | np.isnan(v)                         # optional rules (Finnish stand data) only apply where the data exists
    return ok


def _columns(cond):
    if cond["kind"] == "any":
        return {c for sub in cond["conds"] for c in _columns(sub)}
    return {cond["col"]}


def required_columns():
    cols = {"built_dist_m", "lc_crop_1km", "lc_tree_1km"}
    for rule in RULES.values():
        for cond in rule["conditions"]:
            cols |= _columns(cond)
    return sorted(cols)


def _check(df):
    missing = [c for c in required_columns() if c not in df.columns]
    if missing:
        raise KeyError(f"rule_labels needs these columns, which are missing: {missing}")


def conditions_met(df, species):
    # True where every condition of the species' rule set holds
    ok = np.ones(len(df), dtype=bool)
    for cond in RULES[species]["conditions"]:
        ok &= _eval(df, cond)
    return ok


def undisturbed_mask(df):
    # No built-up surface within BUILT_FREE_M and little farmland nearby. Unknown values are not "undisturbed".
    return (df["built_dist_m"].to_numpy(float) >= BUILT_FREE_M) & (df["lc_crop_1km"].to_numpy(float) <= CROP_MAX)


def disturbed_mask(df):
    built = df["built_dist_m"].to_numpy(float) < BUILT_FREE_M
    crop = df["lc_crop_1km"].to_numpy(float) > CROP_MAX
    open_land = df["lc_tree_1km"].to_numpy(float) < OPEN_TREE_MAX
    return built | crop | open_land


def rule_probability(df, p_right=P_RIGHT, p_undisturbed=P_UNDISTURBED, p_other=P_OTHER, literal=True):
    # Probability of a berry at every row under the rules, one column per species (no random draw)
    _check(df)
    undisturbed, disturbed = undisturbed_mask(df), disturbed_mask(df)
    out = {}
    for species, rule in RULES.items():
        ok = conditions_met(df, species)
        boost_mask = disturbed if (rule["boost"] == "disturbed" and not literal) else undisturbed
        p = np.full(len(df), p_other, dtype=float)
        p[ok] = p_right
        p[ok & boost_mask] = p_undisturbed
        out[species] = p
    return pd.DataFrame(out, index=df.index)


def label_dataset(df, seed=42, p_right=P_RIGHT, p_undisturbed=P_UNDISTURBED, p_other=P_OTHER, literal=True):
    # Returns a copy of df with, per species: rule_ok_<s> (conditions met), p_rule_<s> (probability) and y_<s> (0/1 label)
    probs = rule_probability(df, p_right, p_undisturbed, p_other, literal)
    rng = np.random.default_rng(seed)
    draws = rng.random(probs.shape)
    out = df.copy()
    for k, species in enumerate(probs.columns):
        out[f"rule_ok_{species}"] = conditions_met(df, species)
        out[f"p_rule_{species}"] = probs[species].to_numpy()
        out[f"y_{species}"] = (draws[:, k] < probs[species].to_numpy()).astype(np.int8)
    return out


def summarize(labelled):
    rows = []
    for species in RULES:
        rows.append(dict(species=species, conditions_met=float(labelled[f"rule_ok_{species}"].mean()),
                         mean_probability=float(labelled[f"p_rule_{species}"].mean()), positive_rate=float(labelled[f"y_{species}"].mean()),
                         positives=int(labelled[f"y_{species}"].sum())))
    return pd.DataFrame(rows).set_index("species")


def describe_rules(species=None):
    for name in ([species] if species else list(RULES)):
        rule = RULES[name]
        print(f"{name}  (50 % boost where: {rule['boost']})")
        for cond in rule["conditions"]:
            if cond["kind"] == "any":
                parts = " OR ".join(f"{c['col']} in [{c['lo']}, {c['hi']}]" for c in cond["conds"])
                print(f"   any of: {cond['label']}: {parts}")
            else:
                tag = " (only where data exists)" if cond["optional"] else ""
                print(f"   {cond['col']} in [{cond['lo']}, {cond['hi']}]{tag}   <- {cond['ref']}")


if __name__ == "__main__":
    describe_rules()
