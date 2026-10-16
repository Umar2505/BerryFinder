# Startup validation plan: Oulu 2027 berry season

**Question.** Does BerryFinder help people find berries faster than they would otherwise? Paid conversion and harvest improvement are
hypotheses to prove, not assumptions.

## Design
- **Who:** 30 to 60 volunteers and prospective paying users around Oulu (4H groups, berry-picking clubs, a local university group, nature-tourism guides). Include experienced pickers: beating them is the hard test.
- **Compare:** for each outing, the user is given (a) a BerryFinder-recommended stand and (b) a baseline stand chosen by a simple, fixed rule, e.g. the nearest mapped forest of the right site type to a road, or the picker's own usual spot. Assignment order is randomised; both visited the same day where possible.
- **Per visit, recorded in the app:** presence/absence on a 1 m² plot protocol (5 plots), ripe and unripe counts, minutes searching, minutes travelling, litres picked, optional photo.
- **Seasons:** bilberry mid-July to mid-August, lingonberry late August to October. Hold out the latest fortnight for validation.

## Success thresholds (fixed now, before any result)
| Measure | Success | Stop / rethink |
|---|---|---|
| Recommended stands with berries present | ≥ 70 % and at least 15 percentage points above baseline (≥ 40 paired visits) | Not above baseline: the baseline model does not help; fix before charging |
| Top-ranked precision (top 20 % of stands) | Beats the ecological baseline on held-out blocks | Equal or worse |
| Litres per hour (picking visits only) | ≥ 25 % higher than baseline, median paired difference | No improvement |
| Time to first berries | ≥ 20 % less search time | — |
| Calibration | Model error ≤ 0.10 before showing any probability | Keep showing classes only |
| User-rated usefulness (1 to 5) | Median ≥ 4 | Median ≤ 3 |
| Repeat use | ≥ 40 % of pilot users plan a second trip within the season | < 20 % |
| Willingness to pay | ≥ 20 % of users accept the €4.99/month or €19.99/season price in a real checkout test | < 8 % |

## Method notes
- Report every threshold with a confidence interval, not just a point estimate. Pre-register the analysis in this repository before the season.
- Volunteer bias: users choose where to go. Record how far each stand was from a road and from the user's home, and include both in the analysis.
- Do not train on a visit that is also used to evaluate it: validation blocks are chosen by map tile.

## Partnerships to explore
Luke researchers (data access and protocol review), University of Oulu, 4H Finland local groups, berry-buying companies and wild-product buyers,
Visit Oulu and guided nature-tourism operators, Metsähallitus for state-land access and protected-area rules.
