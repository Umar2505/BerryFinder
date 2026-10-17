# Product design

**The user only wants to know two things: where to go, and what to look for.** Everything else is behind the avatar.

## Principles (from Apple's Human Interface Guidelines)
1. **One question per screen.** Today answers "where do I go?" with one large recommendation. Berries answers "what do I look for?". Map is for the curious.
2. **No numbers to interpret.** Hectares, scores, indices and probabilities never appear on the main screens. Odds are three dots and a word: *Promising*, *Worth a try*, *Long shot*. Time is in minutes.
3. **One primary action.** "Take me there" is the only filled button on Today. Everything else is quiet.
4. **Clarity through whitespace and type.** A 34 pt large title, 17 pt body, 13 pt captions; cards with 24 to 28 pt corners and soft shadows; a neutral canvas so the berry colours carry the meaning.
5. **Touch first.** Every target is at least 44 pt, a frosted tab bar with three items on phones, a segmented control on wide screens, bottom sheets with a grab handle, press feedback on everything tappable.
6. **Honest, in plain words.** "A smart guess from forest and soil maps, not a promise" sits on the card. Drive time says "unavailable" when routing is down instead of inventing a number. Check-ins are private and one tap.
7. **Both languages from the start**, English and Finnish, including dates and berry names, and dark mode that follows the system.

## Screens
| Screen | Purpose |
|---|---|
| **Today** (`/`) | Pick a place once (location or search; remembered). Hero card: berry, nearest good spot, drive and walk time, odds, habitat in a sentence, "Take me there". Two alternatives, and what is in season. After an outing, a one-tap "How were the berries?" |
| **Berries** (`/berries`) | Grid of berries (in season first). Tap for a sheet: what it looks like, where it grows, a tip, season dates, and a safety line. |
| **Map** (`/map`) | The detailed view for people who want to explore. |
| **You** (avatar sheet) | Saved places, trip planner, field report, language, account, how it works, plans, contact. |

## How a recommendation is made (`Core/Guide.cs`, `Services/GuideService.cs`)
1. Which berries are ripe now? From local weather where available, otherwise the calendar.
2. Candidate areas within 40 km for those berries (stand-level forest and soil data).
3. Utility = forest quality x season stage x a drive-time discount (about half the appeal every 30 minutes). Real road drive times for the best eight.
4. Take the top three that are at least 800 m apart, so they are three different outings.
5. Name the place (nearest village, from OpenStreetMap), describe the forest in plain words, and map the area's suitability to the three odds.

Today the guide runs on the app's own real-data baseline for the Oulu pilot (bilberry and lingonberry). The notebook model is not connected yet because it has not been trained.

## API
* `GET /api/guide?lat=&lon=&lang=en|fi[&date=YYYY-MM-DD]` returns `state` (`ready`, `off_season`, `no_spots`), the `hero` and `alternatives` (berry, place, drive, walk, odds, habitat, stage, a `navigate` link), what is `inSeason`, and the `next` season when nothing is ripe.
* `GET /api/berries?lang=` returns the berry cards.
* Public, rate limited, no personal data.
