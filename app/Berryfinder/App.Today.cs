using System.Globalization;

public sealed record HomePlace(string Name, double Lat, double Lon);

public sealed record PendingTrip(long AreaId, string Species, double Lat, double Lon, string Place, DateTime At);

// "Today": the one screen most people ever need. Where to go, which berry, how far, and how good it looks.
// No numbers to interpret, no map to read: one big recommendation and a couple of alternatives.
public partial class BerryfinderApp
{
    // Ask how it went only after the outing has had time to happen
    private static readonly TimeSpan CheckInAfter = TimeSpan.FromMinutes(10);
    private static readonly HomePlace OuluCentre = new("Oulu", 65.0121, 25.4651);

    private readonly PersistentUserReactive<string> _homeSaved = new("");
    private readonly PersistentUserReactive<string> _tripSaved = new("");
    private readonly ClientReactive<HomePlace?> _home = new((HomePlace?)null);
    private readonly ClientReactive<GuideResult?> _guide = new((GuideResult?)null);
    private readonly ClientReactive<bool> _guideBusy = new(false);
    private readonly ClientReactive<string?> _guideProblem = new((string?)null);
    private readonly ClientReactive<int> _heroIndex = new(0);
    private readonly ClientReactive<bool> _changingPlace = new(false);
    private readonly ClientReactive<string> _homeQuery = new("");
    private readonly ClientReactiveList<Place> _homePlaces = new();
    private readonly ClientReactive<bool> _homeSearching = new(false);
    private readonly ClientReactive<string?> _berrySheet = new((string?)null);
    private readonly ClientReactive<bool> _profileOpen = new(false);
    private readonly ClientReactive<string> _timezone = new("Europe/Helsinki");
    private readonly ClientReactive<int> _checkInTick = new(0);

    // Today's date where the user is, not on the server (which runs in UTC): after midnight in Finland it is already tomorrow in UTC terms
    private DateOnly LocalToday()
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(_timezone.Value, out var tz) ? tz : TimeZoneInfo.Utc;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
    }

    // ─── persistence of the chosen place and the trip in progress ────────────────────────

    private static HomePlace? ParseHome(string saved)
    {
        var parts = saved.Split('|');
        return parts.Length == 3
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            ? new HomePlace(parts[2], lat, lon)
            : null;
    }

    private static string FormatHome(HomePlace h) => string.Create(CultureInfo.InvariantCulture, $"{h.Lat:0.#####}|{h.Lon:0.#####}|{h.Name.Replace('|', ' ')}");

    private static PendingTrip? ParseTrip(string saved)
    {
        var p = saved.Split('|');
        return p.Length == 6 && long.TryParse(p[0], out var area)
               && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
               && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
               && long.TryParse(p[5], out var ticks)
            ? new PendingTrip(area, p[1], lat, lon, p[4], new DateTime(ticks, DateTimeKind.Utc))
            : null;
    }

    private static string FormatTrip(GuidePick p) =>
        string.Create(CultureInfo.InvariantCulture, $"{p.AreaId}|{p.Species}|{p.NavLat:0.#####}|{p.NavLon:0.#####}|{p.Place.Replace('|', ' ')}|{DateTime.UtcNow.Ticks}");

    // Loads the remembered place when a client joins; the guide itself loads in the background so the first paint is instant
    private void RestoreHome()
    {
        if (ParseHome(_homeSaved.Value) is { } home)
        {
            _home.Value = home;
            _ = Task.Run(RefreshGuideAsync);
        }
    }

    // ─── actions ─────────────────────────────────────────────────────────────────────────

    private async Task RefreshGuideAsync()
    {
        if (_home.Value is not { } home)
        {
            return;
        }

        _guideBusy.Value = true;
        _guideProblem.Value = null;
        try
        {
            _guide.Value = await _guideSvc.GetAsync(home.Lat, home.Lon, LocalToday());
            _heroIndex.Value = 0;
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Guide failed: {ex.Message}");
            _guideProblem.Value = T("Couldn't load today's picks. Check your connection and try again.", "Tämän päivän valintoja ei saatu ladattua. Tarkista yhteys ja yritä uudelleen.");
        }
        finally
        {
            _guideBusy.Value = false;
        }
    }

    private async Task SetHomeAsync(HomePlace place)
    {
        _home.Value = place;
        _homeSaved.Value = FormatHome(place);
        _homePlaces.Clear();
        _homeQuery.Value = "";
        _changingPlace.Value = false;
        _guide.Value = null;
        await RefreshGuideAsync();
        await _queries.TrackAsync(CurrentUserId, "home_set");
    }

    private async Task UseMyLocationForTodayAsync()
    {
        var location = await ClientFunctions.GetLocationAsync();
        if (location is null)
        {
            _toast.Value = T("Couldn't read your location. Allow location access, or search for a town.", "Sijaintia ei saatu. Salli sijainnin käyttö tai hae paikkakuntaa.");
            return;
        }

        _userLocation.Value = new UserLocation(location.Latitude, location.Longitude, location.Accuracy);
        var name = await ReverseGeocoder.NameAsync(location.Latitude, location.Longitude) ?? T("Your location", "Sijaintisi");
        await SetHomeAsync(new HomePlace(name, location.Latitude, location.Longitude));
    }

    private async Task SearchHomeAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _homeSearching.Value = true;
        try
        {
            var places = await Geocoder.SearchAsync(text, _language.Value);
            _homePlaces.ReplaceAll(places);
            if (places.Count == 0)
            {
                _toast.Value = T("No place found with that name", "Paikkaa ei löytynyt");
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Place search failed: {ex.Message}");
            _toast.Value = T("Search isn't available right now. Try again in a moment.", "Haku ei toimi juuri nyt. Yritä hetken kuluttua uudelleen.");
        }
        finally
        {
            _homeSearching.Value = false;
        }
    }

    private async Task StartTripAsync(GuidePick pick)
    {
        _tripSaved.Value = FormatTrip(pick);

        // Nothing re-renders when time passes, so bump a counter once the wait is over and the "how was it?" card can appear
        _ = Task.Run(async () =>
        {
            await Task.Delay(CheckInAfter + TimeSpan.FromSeconds(1));
            _checkInTick.Value++;
        });
        await _queries.TrackAsync(CurrentUserId, "navigation_handoff", new { species = pick.Species, source = "today" });
    }

    // One tap: how was it? Stored privately, never shared, never used for training: it is a quick note, not a survey.
    private async Task CheckInAsync(PendingTrip trip, string outcome)
    {
        if (CurrentUserId is not { } userId)
        {
            await ClientFunctions.LoginShowAsync(T("Sign in to tell us how it went", "Kirjaudu kertoaksesi, miten meni"));
            return;
        }

        var input = new ObservationInput(Species: trip.Species, Lat: trip.Lat, Lon: trip.Lon, GpsAccuracyM: null, LocationSource: "area_centroid",
            ObservedOn: DateOnly.FromDateTime(DateTime.UtcNow), Method: "visit", Outcome: outcome, Stage: null, EffortMinutes: null, AreaSearchedM2: null,
            AmountLitres: null, PlotCount: null, RipeCount: null, UnripeCount: null, FlowerCount: null, AreaId: trip.AreaId,
            ShareAggregated: false, UseForTraining: false, Notes: null, Photos: []);
        var result = await _observations.SubmitAsync(userId, input);
        _tripSaved.Value = "";
        _toast.Value = result.Id is null ? T("Couldn't save that, sorry.", "Tallennus ei onnistunut.") : T("Thanks, that helps everyone.", "Kiitos, tämä auttaa kaikkia.");
        if (result.Id is not null)
        {
            await _queries.TrackAsync(userId, "checkin", new { outcome, species = trip.Species });
        }
    }

    // ─── screen ──────────────────────────────────────────────────────────────────────────

    private void RenderTodayPage(UIView view)
    {
        view.ScrollArea(["flex-1 min-h-0"], content: scroll =>
            scroll.Column(["w-full max-w-xl mx-auto px-5 pt-3 pb-10 gap-6", Design.Appear], content: page =>
            {
                RenderTodayHeader(page);

                if (_home.Value is null || _changingPlace.Value)
                {
                    RenderWelcomeCard(page);
                    if (_home.Value is null)
                    {
                        return;
                    }
                }

                if (_guideProblem.Value is { } problem)
                {
                    page.Column([Design.Card, "p-6 gap-3 items-start"], content: c =>
                    {
                        c.Text([Design.Headline], text: T("Something went wrong", "Jokin meni pieleen"));
                        c.Text([Design.Subhead], text: problem);
                        c.Button([Button.PrimaryMd, "rounded-full h-12 px-6"], text: T("Try again", "Yritä uudelleen"), onClick: async () => await RefreshGuideAsync());
                    });
                    return;
                }

                if (_guide.Value is not { } guide)
                {
                    RenderGuideSkeleton(page);
                    return;
                }

                RenderCheckInCard(page);

                switch (guide.State)
                {
                    case "ready": RenderPicks(page, guide); break;
                    case "off_season": RenderOffSeason(page, guide); break;
                    default: RenderNoSpots(page); break;
                }

                RenderInSeasonRow(page, guide);
            }));
    }

    private void RenderTodayHeader(UIView view)
    {
        var date = LocalToday().ToString(_language.Value == "fi" ? "dddd d. MMMM" : "dddd d MMMM", Culture).ToUpper(Culture);
        view.Column(["gap-2 pt-2"], content: head =>
        {
            head.Text([Design.Overline, "text-muted-foreground"], text: date);
            head.Text([Design.LargeTitle], text: T("Today", "Tänään"));
            if (_home.Value is { } home && !_changingPlace.Value)
            {
                head.Button(["self-start flex items-center gap-1.5 bg-transparent border-0 p-0 -ml-0.5 text-primary text-[17px] font-medium", Design.Press],
                    ariaLabel: T("Change place", "Vaihda paikka") + ": " + home.Name, onClick: async () => _changingPlace.Value = true, content: b =>
                    {
                        b.Icon([Icon.Sm], name: "map-pin");
                        b.Text(text: home.Name);
                        b.Icon([Icon.Xs, "opacity-60"], name: "chevron-down");
                    });
            }
        });
    }

    private void RenderWelcomeCard(UIView view)
    {
        view.Column([Design.Card, "p-6 gap-5"], content: card =>
        {
            card.Box(["w-14 h-14 rounded-2xl bg-brand-solid/10 flex items-center justify-center"], content: b => b.Icon([Icon.Lg, "text-brand-secondary"], name: "map-pin"));
            card.Column(["gap-1.5"], content: t =>
            {
                t.Text([Design.Title], text: T("Where are you?", "Missä olet?"));
                t.Text([Design.Subhead], text: T("We'll find the best berry spots nearby.", "Etsimme lähialueen parhaat marjapaikat."));
            });
            card.Button([Button.PrimaryLg, "w-full h-14 rounded-full text-[17px] font-semibold gap-2", Design.Press], onClick: async () => await UseMyLocationForTodayAsync(), content: b =>
            {
                b.Icon([Icon.Md], name: "locate-fixed");
                b.Text(text: T("Use my location", "Käytä sijaintiani"));
            });
            card.Row(["items-center gap-2 bg-muted rounded-full px-4"], content: row =>
            {
                row.Icon([Icon.Sm, "text-muted-foreground"], name: "search");
                row.TextField(["flex-1 h-12 border-0 bg-transparent shadow-none px-0 text-[17px] focus-visible:ring-0"], value: _homeQuery.Value,
                    placeholder: T("Search a town or village", "Hae kaupunkia tai kylää"), ariaLabel: T("Search a town or village", "Hae kaupunkia tai kylää"),
                    onValueChange: async v => _homeQuery.Value = v, onSubmit: async v => await SearchHomeAsync(v));
                if (_homeSearching.Value)
                {
                    row.Box([Icon.Spinner, "w-4 h-4"]);
                }
            });

            foreach (var place in _homePlaces.Take(5))
            {
                card.Button(["w-full flex flex-col items-start text-left bg-transparent border-0 px-1 py-2 border-t border-secondary", Design.Press], key: $"hp-{place.Lat}:{place.Lon}",
                    onClick: async () => await SetHomeAsync(new HomePlace(place.Name, place.Lat, place.Lon)), content: b =>
                    {
                        b.Text([Design.Headline], text: place.Name);
                        b.Text([Design.Caption, "line-clamp-1"], text: place.Detail);
                    });
            }

            card.Button(["self-start bg-transparent border-0 p-0 text-primary text-[15px] font-medium", Design.Press], text: T("Or start with Oulu", "Tai aloita Oulusta"),
                onClick: async () => await SetHomeAsync(OuluCentre));
        });
    }

    private void RenderGuideSkeleton(UIView view)
    {
        view.Box([Skeleton.Card, "h-[360px] rounded-[28px]"]);
        view.Box([Skeleton.Title, "w-1/2"]);
        view.Box([Skeleton.Card, "h-24 rounded-[24px]"]);
    }

    private void RenderPicks(UIView view, GuideResult guide)
    {
        var picks = guide.All;
        var heroIndex = Math.Min(_heroIndex.Value, picks.Count - 1);
        var hero = picks[heroIndex];

        RenderHeroCard(view, hero);

        var others = picks.Select((p, i) => (Pick: p, Index: i)).Where(x => x.Index != heroIndex).ToList();
        if (others.Count == 0)
        {
            return;
        }

        view.Text([Design.Title, "pt-2"], text: T("Also nearby", "Myös lähellä"));
        view.Row(["flex gap-3 overflow-x-auto snap-x snap-mandatory -mx-5 px-5 pb-3 [scrollbar-width:none]"], content: row =>
        {
            foreach (var (pick, index) in others)
            {
                var berry = BerryCatalog.Get(pick.Species);
                row.Button([Design.Card, "shrink-0 w-[270px] snap-start p-4 flex items-center gap-3 text-left border-0", Design.Press], key: $"alt-{pick.AreaId}",
                    ariaLabel: $"{Design.Capitalise(berry.Plural(_language.Value))} {pick.Place}", onClick: async () => _heroIndex.Value = index, content: b =>
                    {
                        b.Box([$"w-14 h-14 shrink-0 rounded-2xl flex items-center justify-center bg-[{berry.Color}]"], content: art => art.Image(["w-10 h-10"], src: berry.Art, alt: ""));
                        b.Column(["gap-0.5 min-w-0 flex-1"], content: t =>
                        {
                            t.Text([Design.Headline, "truncate"], text: Design.Capitalise(berry.Plural(_language.Value)));
                            t.Text([Design.Caption, "truncate"], text: pick.Place.Length > 0 ? $"{pick.Place} · {Guide.DriveText(pick.DriveMinutes, _language.Value)}" : Guide.DriveText(pick.DriveMinutes, _language.Value));
                            t.Text([Design.Caption, "truncate"], text: Guide.OddsLabel((Odds)pick.Odds, _language.Value));
                        });
                    });
            }
        });
    }

    private void RenderHeroCard(UIView view, GuidePick pick)
    {
        var lang = _language.Value;
        var berry = BerryCatalog.Get(pick.Species);

        view.Column([$"rounded-[28px] p-6 gap-5 text-white shadow-[0_14px_40px_rgba(0,0,0,0.20)] bg-[{berry.Color}]"], key: $"hero-{pick.AreaId}", content: card =>
        {
            card.Row(["items-center justify-between"], content: top =>
            {
                top.Text([Design.Overline, "text-white/70"], text: T("Best pick today", "Päivän paras valinta"));
                top.Row(["items-center gap-2"], content: odds =>
                {
                    odds.Row(["gap-1"], content: dots =>
                    {
                        for (var i = 1; i <= 3; i++)
                        {
                            dots.Box([$"w-2 h-2 rounded-full {(i <= pick.Odds ? "bg-white" : "bg-white/30")}"]);
                        }
                    });
                    odds.Text(["text-[13px] font-semibold text-white"], text: Guide.OddsLabel((Odds)pick.Odds, lang));
                });
            });

            card.Row(["items-center justify-between gap-4"], content: mid =>
            {
                mid.Column(["gap-1 min-w-0 flex-1"], content: t =>
                {
                    t.Text(["text-[34px] leading-[1.05] font-bold tracking-tight text-white"], text: Design.Capitalise(berry.Plural(lang)));
                    t.Text(["text-[17px] text-white/85"], text: pick.Place.Length > 0 ? T($"near {pick.Place}", $"lähellä: {pick.Place}") : T("near you", "lähellä sinua"));
                });
                mid.Image(["w-32 h-32 shrink-0 -mr-2"], src: berry.Art, alt: "");
            });

            card.Column(["gap-1"], content: t =>
            {
                t.Text(["text-[15px] text-white/90"], text: Guide.Describe(pick.Habitat, lang));
                t.Text(["text-[13px] text-white/70"], text: Guide.StageText(pick.Availability, lang));
            });

            card.Row(["flex-wrap gap-2"], content: chips =>
            {
                chips.Row([Design.GlassChip], content: c =>
                {
                    c.Icon([Icon.Xs], name: "car");
                    c.Text(text: Guide.DriveText(pick.DriveMinutes, lang));
                });
                chips.Row([Design.GlassChip], content: c =>
                {
                    c.Icon([Icon.Xs], name: "footprints");
                    c.Text(text: Guide.WalkText(pick.DistRoadM, lang));
                });
            });

            card.Column(["gap-2"], content: actions =>
            {
                actions.Link([$"w-full h-14 rounded-full bg-white text-[17px] font-semibold flex items-center justify-center gap-2 text-[{berry.Color}] no-underline", Design.Press],
                    href: Router.NavigationUrl(pick.NavLat, pick.NavLon), target: "_blank", rel: "noopener",
                    onClick: async () => await StartTripAsync(pick), content: l =>
                    {
                        l.Icon([Icon.Md], name: "navigation");
                        l.Text(text: T("Take me there", "Vie minut sinne"));
                    });
                actions.Button(["w-full h-11 rounded-full bg-transparent border-0 text-white/90 text-[15px] font-medium", Design.Press],
                    text: T($"What do {berry.Plural("en")} look like?", $"Miltä {berry.Name("fi").ToLowerInvariant()} näyttää?"), onClick: async () => _berrySheet.Value = pick.Species);
            });

            card.Text(["text-[12px] leading-snug text-white/65"], text: pick.Source == "grid_model"
                ? T("A machine-learning guess from climate, soil and satellite maps, not a promise.", "Koneoppimisen arvaus ilmasto-, maaperä- ja satelliittikartoista, ei lupaus.")
                : T("A smart guess from forest and soil maps, not a promise.", "Älykäs arvaus metsä- ja maaperäkartoista, ei lupaus."));
        });
    }

    private void RenderCheckInCard(UIView view)
    {
        _ = _checkInTick.Value;      // re-render when the wait is over
        if (ParseTrip(_tripSaved.Value) is not { } trip || DateTime.UtcNow - trip.At < CheckInAfter || DateTime.UtcNow - trip.At > TimeSpan.FromHours(48))
        {
            return;
        }

        var berry = BerryCatalog.Get(trip.Species);
        view.Column([Design.Card, "p-5 gap-4"], key: "checkin", content: card =>
        {
            card.Column(["gap-1"], content: t =>
            {
                t.Text([Design.Headline], text: T($"How were the {berry.Plural("en")}?", $"Miten {berry.Plural("fi")} onnistuivat?"));
                t.Text([Design.Subhead], text: trip.Place.Length > 0 ? T($"You went near {trip.Place}. One tap is enough.", $"Kävit lähellä: {trip.Place}. Yksi napautus riittää.") : T("One tap is enough.", "Yksi napautus riittää."));
            });
            card.Row(["gap-2"], content: row =>
            {
                foreach (var (outcome, en, fi, icon) in new[] { ("many", "Lots", "Paljon", "smile"), ("few", "A few", "Vähän", "meh"), ("none", "None", "Ei yhtään", "frown") })
                {
                    row.Button([Button.OutlineMd, "flex-1 h-14 rounded-2xl flex-col gap-0.5 text-[15px] font-medium", Design.Press], key: outcome,
                        onClick: async () => await CheckInAsync(trip, outcome), content: b =>
                        {
                            b.Icon([Icon.Md], name: icon);
                            b.Text(text: T(en, fi));
                        });
                }
            });
            card.Button(["self-start bg-transparent border-0 p-0 text-muted-foreground text-[15px]", Design.Press], text: T("Skip", "Ohita"), onClick: async () => _tripSaved.Value = "");
        });
    }

    private void RenderOffSeason(UIView view, GuideResult guide)
    {
        var next = guide.NextSpecies is { } id ? BerryCatalog.Get(id) : null;
        var stillRipe = guide.InSeason.Select(BerryCatalog.Get).ToList();

        view.Column([Design.Card, "p-6 gap-4"], content: card =>
        {
            card.Box(["w-14 h-14 rounded-2xl bg-muted flex items-center justify-center"], content: b => b.Icon([Icon.Lg, "text-muted-foreground"], name: stillRipe.Count > 0 ? "leaf" : "snowflake"));
            card.Column(["gap-1.5"], content: t =>
            {
                t.Text([Design.Title], text: stillRipe.Count > 0 ? T("Not much to pick near you right now", "Lähelläsi ei ole nyt paljon poimittavaa") : T("Berry season is over", "Marjakausi on ohi"));
                t.Text([Design.Subhead], text: stillRipe.Count > 0
                    ? T("Some berries are ripe, but we can't find spots for them yet.", "Jotkin marjat ovat kypsiä, mutta emme vielä löydä niille paikkoja.")
                    : next is null ? "" : T($"Next up: {next.Plural("en")}, {WhenText(guide.DaysUntilNext ?? 0, "en")}.", $"Seuraavaksi: {next.Plural("fi")}, {WhenText(guide.DaysUntilNext ?? 0, "fi")}."));
            });
            card.Button([Button.PrimaryMd, "self-start h-12 px-6 rounded-full text-[17px] font-semibold", Design.Press], text: T("See what to look for", "Katso, mitä etsiä"), onClick: async () => await GoAsync("/berries"));
        });
    }

    private static string WhenText(int days, string lang) => days switch
    {
        < 2 => lang == "fi" ? "huomenna" : "tomorrow",
        < 60 => lang == "fi" ? $"{days} päivän päästä" : $"in {days} days",
        _ => lang == "fi" ? $"noin {days / 30} kuukauden päästä" : $"in about {days / 30} months",
    };

    private void RenderNoSpots(UIView view)
    {
        view.Column([Design.Card, "p-6 gap-4"], content: card =>
        {
            card.Box(["w-14 h-14 rounded-2xl bg-muted flex items-center justify-center"], content: b => b.Icon([Icon.Lg, "text-muted-foreground"], name: "map-pinned"));
            card.Column(["gap-1.5"], content: t =>
            {
                t.Text([Design.Title], text: T("We don't cover this area yet", "Emme vielä kata tätä aluetta"));
                t.Text([Design.Subhead], text: T("BerryFinder works around Oulu for now. More places are coming.", "BerryFinder toimii toistaiseksi Oulun seudulla. Lisää paikkoja on tulossa."));
            });
            card.Button([Button.PrimaryMd, "self-start h-12 px-6 rounded-full text-[17px] font-semibold", Design.Press], text: T("Show me Oulu", "Näytä Oulu"), onClick: async () => await SetHomeAsync(OuluCentre));
        });
    }

    private void RenderInSeasonRow(UIView view, GuideResult guide)
    {
        if (guide.InSeason.Count == 0)
        {
            return;
        }

        view.Column(["gap-3 pt-2"], content: section =>
        {
            section.Text([Design.Title], text: T("In season now", "Nyt sesongissa"));
            section.Row(["flex-wrap gap-2"], content: row =>
            {
                foreach (var id in guide.InSeason)
                {
                    var berry = BerryCatalog.Get(id);
                    row.Button([Design.Card, "h-11 pl-2 pr-4 rounded-full flex items-center gap-2 border-0 text-[15px] font-medium text-foreground", Design.Press], key: id,
                        onClick: async () => _berrySheet.Value = id, content: b =>
                        {
                            b.Box([$"w-7 h-7 rounded-full flex items-center justify-center bg-[{berry.Color}]"], content: art => art.Image(["w-5 h-5"], src: berry.Art, alt: ""));
                            b.Text(text: berry.Name(_language.Value));
                        });
                }
            });
        });
    }
}
