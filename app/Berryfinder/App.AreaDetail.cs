using System.Globalization;

public partial class BerryfinderApp
{
    private void RenderAreaDetail(UIView view)
    {
        view.Button([Button.GhostSm, "-ml-2 gap-1 self-start"], onClick: async () => { _selectedAreaId.Value = null; _detail.Value = null; }, content: b =>
        {
            b.Icon([Icon.Sm], name: "arrow-left");
            b.Text(text: T("Back to list", "Takaisin listaan"));
        });

        if (_detailLoading.Value || _detail.Value is null)
        {
            view.Box([Skeleton.Title]);
            view.Box([Skeleton.Card]);
            view.Box([Skeleton.Text]);
            return;
        }

        var d = _detail.Value;
        var a = d.Area;
        var trained = d.ModelKind != "heuristic";

        view.Column(["gap-2 motion-[0:opacity-0_translate-y-[6px],100:opacity-100_translate-y-0] motion-duration-200ms"], content: head =>
        {
            head.Row(["items-center gap-2 flex-wrap"], content: r =>
            {
                r.Badge(SpeciesName(a.Species), tone: SemanticTone.Neutral, size: BadgeSize.Sm);
                r.Badge(trained ? T("Trained model", "Koulutettu malli") : T("Unvalidated estimate", "Validoimaton arvio"),
                    tone: trained ? SemanticTone.Success : SemanticTone.Warning, size: BadgeSize.Sm, outline: true);
            });
            head.Text([Text.H2, "font-heading leading-tight"], text: SuitabilityLabel(a.Suitability));
            head.Text([Text.Small, "text-muted-foreground"], text: string.Format(Culture,
                T("{0:0.#} ha of contiguous forest across {1} stands · {2:0.#} km from you or the map centre (straight line)",
                  "{0:0.#} ha yhtenäistä metsää, {1} kuviota · {2:0.#} km sinusta tai kartan keskeltä (linnuntietä)"),
                a.AreaHa, a.StandCount, a.DistanceKm));
        });

        if (d.RestrictionSeverity is not null || a.Restriction is not null)
        {
            view.Alert(T("Protected area overlaps this forest", "Suojelualue on päällekkäin tämän metsän kanssa"), tone: SemanticTone.Warning,
                description: (a.Restriction is { } name ? $"{name}. " : "") + T("Everyman's rights usually allow berry picking in nature reserves, but some have their own restrictions. Check the signs and rules on site.",
                    "Jokaisenoikeudet sallivat yleensä marjastuksen luonnonsuojelualueilla, mutta joillakin on omia rajoituksia. Tarkista kyltit ja säännöt paikan päällä."));
        }

        // Actions first: they are what a picker came for
        view.Row(["gap-2 flex-wrap"], content: actions =>
        {
            var target = a.AccessLat is { } alat && a.AccessLon is { } alon ? (alat, alon) : (a.Lat, a.Lon);
            actions.Link([Button.PrimaryMd, "gap-2"], href: Router.NavigationUrl(target.Item1, target.Item2), target: "_blank", rel: "noopener",
                onClick: async () => await _queries.TrackAsync(CurrentUserId, "navigation_handoff", new { species = a.Species }),
                content: l =>
                {
                    l.Icon([Icon.Sm], name: "navigation");
                    l.Text(text: T("Navigate to access point", "Navigoi kulkupisteeseen"));
                });
            actions.Button([Button.OutlineMd, "gap-2"], onClick: async () => await SaveAreaAsync(d), content: b =>
            {
                b.Icon([Icon.Sm], name: "bookmark-plus");
                b.Text(text: T("Save", "Tallenna"));
            });
            actions.Button([Button.OutlineMd, "gap-2"], onClick: async () => await StartReportAsync(a), content: b =>
            {
                b.Icon([Icon.Sm], name: "clipboard-pen");
                b.Text(text: T("Report", "Raportoi"));
            });
        });

        view.Card([Card.Subtle], content: c =>
        {
            c.Text([Text.Label], text: T("Getting there", "Perille pääsy"));
            Fact(c, T("Nearest road or forest track", "Lähin tie tai metsäautotie"), WalkLabel(a.DistRoadM));
            if (_drive.Value is { } drive)
            {
                Fact(c, T("Driving from your location", "Ajomatka sijainnistasi"), string.Format(Culture, T("{0:0} min · {1:0.#} km by road", "{0:0} min · {1:0.#} km tietä pitkin"), drive.Minutes, drive.Kilometres));
            }
            else if (_driveState.Value is { } state)
            {
                c.Text([Text.Caption, "text-muted-foreground"], text: state);
            }
            else if (_userLocation.Value is null)
            {
                c.Text([Text.Caption, "text-muted-foreground"], text: T("Use the location button on the map to see driving time.", "Käytä kartan sijaintipainiketta nähdäksesi ajoajan."));
            }
            else
            {
                c.Button([Button.GhostSm, "self-start -ml-2"], text: T("Show driving time", "Näytä ajoaika"), onClick: async () => await LoadDriveTimeAsync(a));
            }

            c.Text([Text.Caption, "text-muted-foreground mt-1"], text: T("The walk from the road is a straight-line distance, not a path. Navigation leads to the road, never through private yards.",
                "Kävelymatka tieltä on linnuntie-etäisyys, ei polku. Navigointi ohjaa tielle, ei pihojen läpi."));
        });

        view.Column(["gap-2"], content: why =>
        {
            why.Text([Text.H4, "font-heading"], text: T("Why this area", "Miksi tämä alue"));
            RenderDrivers(why, d.Drivers);
        });

        RenderSeasonCard(view, a.Species);

        view.Card([Card.Subtle], content: c =>
        {
            c.Text([Text.Label], text: T("Forest (Finnish Forest Centre)", "Metsä (Suomen metsäkeskus)"));
            Fact(c, T("Main site type", "Pääkasvupaikka"), TopShare(d.Fertility, k => int.TryParse(k, out var f) ? ForestCodes.Label(ForestCodes.Fertility, (int?)f, _language.Value) : T("Not recorded", "Ei tietoa")));
            Fact(c, T("Development class", "Kehitysluokka"), TopShare(d.Development, k => ForestCodes.Label(ForestCodes.Development, k == "unknown" ? null : k, _language.Value)));
            Fact(c, T("Site class", "Kasvupaikan pääryhmä"), TopShare(d.SiteMain, k => ForestCodes.Label(ForestCodes.SiteMainClass, k == "unknown" ? null : k, _language.Value)));
            Fact(c, T("Mean tree age", "Puuston keski-ikä"), d.MeanAge is { } age ? string.Format(Culture, T("{0:0} years", "{0:0} vuotta"), age) : T("Not recorded", "Ei tietoa"));
            Fact(c, T("Basal area", "Pohjapinta-ala"), d.BasalArea is { } ba ? $"{ba:0} m²/ha" : T("Not recorded", "Ei tietoa"));
            Fact(c, T("Mean height", "Keskipituus"), d.MeanHeight is { } h ? $"{h:0} m" : T("Not recorded", "Ei tietoa"));

            if (d.PropPine is { } pine && d.PropSpruce is { } spruce && d.PropOther is { } other)
            {
                c.Text([Text.Caption, "text-muted-foreground mt-2"], text: T("Tree species by volume", "Puulajit tilavuuden mukaan"));
                c.Row(["h-2.5 w-full rounded-full overflow-hidden"], content: bar =>
                {
                    bar.Box([$"h-full bg-[#c58b3a] w-[{pine * 100:0.#}%]"]);
                    bar.Box([$"h-full bg-[#2f5d3a] w-[{spruce * 100:0.#}%]"]);
                    bar.Box([$"h-full bg-[#9bbf6a] w-[{other * 100:0.#}%]"]);
                });
                c.Text([Text.Caption, "text-muted-foreground"], text: string.Format(Culture, T("Pine {0:P0} · Spruce {1:P0} · Broadleaf {2:P0}", "Mänty {0:P0} · Kuusi {1:P0} · Lehtipuu {2:P0}"), pine, spruce, other));
            }
        });

        view.Card([Card.Subtle], content: c =>
        {
            c.Text([Text.Label], text: T("Soil (Geological Survey of Finland)", "Maaperä (Geologian tutkimuskeskus)"));
            foreach (var share in d.Soil.Take(3))
            {
                Fact(c, share.Key == "unknown" ? T("Not mapped", "Ei kartoitettu") : SoilLabel(share.Key), share.Fraction.ToString("P0", Culture));
            }

            c.Text([Text.Caption, "text-muted-foreground"], text: T("Deposit type only: GTK's map has no measured pH, nutrients or moisture.", "Vain maalaji: GTK:n kartassa ei ole mitattua pH:ta, ravinteita tai kosteutta."));
        });

        if (d.Community is { } community)
        {
            view.Card([Card.Subtle], content: c =>
            {
                c.Text([Text.Label], text: T("Field reports shared by pickers", "Poimijoiden jakamat havainnot"));
                c.Text([Text.Small], text: string.Format(Culture, T("{0} reports from {1} people: {2} found berries, {3} found none. Latest {4:d}.",
                    "{0} havaintoa {1} henkilöltä: {2} löysi marjoja, {3} ei löytänyt. Viimeisin {4:d}."), community.Reports, community.Contributors, community.Found, community.NotFound, community.Latest));
            });
        }

        view.Card([Card.Subtle], content: c =>
        {
            c.Text([Text.Label], text: T("Evidence and freshness", "Näyttö ja ajantasaisuus"));
            Fact(c, T("Prediction", "Ennuste"), trained ? T("Trained model", "Koulutettu malli") : T("Ecological baseline (unvalidated)", "Ekologinen perusmalli (validoimaton)"));
            Fact(c, T("Model version", "Malliversio"), d.ModelVersion);
            Fact(c, T("Published", "Julkaistu"), d.PublishedAt.ToString("d", Culture));
            Fact(c, T("Forest data measured", "Metsätiedot mitattu"), d.OldestMeasurement is { } o && d.NewestMeasurement is { } n
                ? (o.Year == n.Year ? o.Year.ToString(Culture) : $"{o.Year}–{n.Year}") : T("Unknown", "Tuntematon"));
            Fact(c, T("Downloaded from source", "Haettu lähteestä"), d.IngestedAt?.ToString("d", Culture) ?? T("Unknown", "Tuntematon"));
            Fact(c, T("Evidence quality", "Näytön laatu"), TopShare(d.Evidence, EvidenceLabel));
        });

        view.Text([Text.Caption, "text-muted-foreground"], text: T("This is an estimate from forest, soil and weather records, not a field check. It cannot guarantee any harvest.",
            "Tämä on arvio metsä-, maaperä- ja säätiedoista, ei maastotarkistus. Se ei takaa satoa."));
    }

    private string EvidenceLabel(string evidence) => evidence switch
    {
        "good" => T("Good: recent, complete records", "Hyvä: tuoreet, kattavat tiedot"),
        "fair" => T("Fair: some records old or missing", "Kohtalainen: osa tiedoista vanhoja tai puuttuu"),
        _ => T("Limited: key records missing", "Rajallinen: keskeisiä tietoja puuttuu"),
    };

    private string TopShare(IReadOnlyList<Share> shares, Func<string, string> label)
    {
        if (shares.Count == 0)
        {
            return T("Not recorded", "Ei tietoa");
        }

        var top = shares[0];
        return top.Fraction >= 0.95 ? label(top.Key) : $"{label(top.Key)} ({top.Fraction.ToString("P0", Culture)})";
    }

    private void RenderSeasonCard(UIView view, string species)
    {
        view.Card([Card.Elevated], content: c =>
        {
            c.Row(["items-center justify-between"], content: r =>
            {
                r.Text([Text.Label], text: T("This season", "Tämä kausi"));
                r.Badge(T("Heuristic", "Heuristinen"), tone: SemanticTone.Neutral, size: BadgeSize.Sm, outline: true);
            });

            if (!CurrentPlan.SeasonalEstimates)
            {
                c.Text([Text.Small, "text-muted-foreground"], text: T("Premium shows where the season stands here: flowering, unripe, ripe or over, from the nearest FMI weather station.",
                    "Premium näyttää kauden vaiheen: kukinta, raakile, kypsä tai ohi, lähimmän Ilmatieteen laitoksen aseman mukaan."));
                c.Button([Button.OutlineSm, "self-start mt-2"], text: T("See Premium", "Katso Premium"), onClick: async () => await GoAsync("/pricing"));
                return;
            }

            if (_season.Value is not { } season)
            {
                c.Box([Skeleton.Text]);
                return;
            }

            var e = season.Estimate;
            c.Text([Text.H4, "font-heading"], text: StageLabel(e.Stage));
            c.Text([Text.Small], text: AvailabilityLabel(e.Availability));

            foreach (var note in e.Notes)
            {
                c.Row(["items-start gap-2 mt-1"], content: r =>
                {
                    r.Icon([Icon.Sm, "text-warning-600 mt-0.5 shrink-0"], name: "triangle-alert");
                    r.Text([Text.Small], text: _language.Value == "fi" ? note.Fi : note.En);
                });
            }

            if (e.Source == "station" && e.Gdd is { } gdd)
            {
                c.Text([Text.Caption, "text-muted-foreground mt-2"], text: string.Format(Culture,
                    T("Based on {0} weather station, {1:0} km away (observations to {2:d}); {3:0} growing degree days since April. Station weather, not measured on site.",
                      "Perustuu säähavaintoasemaan {0}, {1:0} km päässä (havainnot {2:d} asti); {3:0} kasvuastepäivää huhtikuusta. Aseman sää, ei paikan päältä mitattu."),
                    season.Station, season.StationKm, e.AsOf, gdd));
            }
            else if (e.Source == "nasa_power" && e.Gdd is { } modelledGdd)
            {
                c.Text([Text.Caption, "text-muted-foreground mt-2"], text: string.Format(Culture,
                    T("Based on NASA POWER's modelled weather grid (about 55 km cells, observations to {0:d}); {1:0} growing degree days since April. A model of the area, not a measurement.",
                      "Perustuu NASA POWERin mallinnettuun säähän (noin 55 km ruudut, havainnot {0:d} asti); {1:0} kasvuastepäivää huhtikuusta. Alueen malli, ei mittaus."),
                    e.AsOf, modelledGdd));
            }
            else
            {
                c.Text([Text.Caption, "text-muted-foreground mt-2"], text: T("Outside the berry season, or no recent weather data.", "Marjakauden ulkopuolella tai ei tuoreita säätietoja."));
            }

            if (e.RootWetness is { } wet)
            {
                c.Text([Text.Caption, "text-muted-foreground"], text: string.Format(Culture,
                    T("Modelled root-zone soil wetness, last 14 days (NASA POWER): {0:0.00} on a scale from 0 (dry) to 1 (saturated).",
                      "Mallinnettu juuristokerroksen kosteus, viimeiset 14 päivää (NASA POWER): {0:0.00} asteikolla 0 (kuiva) – 1 (kyllästynyt)."), wet));
            }
        });
    }

    private string StageLabel(string stage) => stage switch
    {
        "before_flowering" => T("Before flowering", "Ennen kukintaa"),
        "flowering" => T("Flowering", "Kukinta"),
        "unripe" => T("Unripe berries", "Raakileet"),
        "ripening" => T("Ripening", "Kypsymässä"),
        "ripe" => T("Ripe season", "Kypsä kausi"),
        "late" => T("Late season", "Myöhäinen kausi"),
        "dormant" => T("Season over", "Kausi ohi"),
        _ => T("Season stage unknown", "Kauden vaihe tuntematon"),
    };

    private string AvailabilityLabel(string availability) => availability switch
    {
        "not_yet" => T("Not ready for picking yet.", "Ei vielä poimittavissa."),
        "starting" => T("The first ripe berries may appear soon.", "Ensimmäiset kypsät marjat voivat ilmestyä pian."),
        "peak" => T("Likely within the main picking window.", "Todennäköisesti pääpoiminta-aikaa."),
        "late" => T("End of season: fewer and softer berries.", "Kauden loppu: vähemmän ja pehmeämpiä marjoja."),
        "over" => T("The picking season is over here.", "Poimintakausi on täällä ohi."),
        _ => T("Not enough weather data to say.", "Säätietoja ei ole riittävästi."),
    };

    private async Task LoadDriveTimeAsync(AreaSummary area)
    {
        if (_userLocation.Value is not { } u)
        {
            return;
        }

        _driveState.Value = T("Asking the road router…", "Haetaan reittiä…");
        var target = area.AccessLat is { } lat && area.AccessLon is { } lon ? (lat, lon) : (area.Lat, area.Lon);
        var result = await Router.DriveTimesAsync((u.Lat, u.Lon), [target]);
        _drive.Value = result.FirstOrDefault();
        _driveState.Value = _drive.Value is null ? T("Driving time is not available right now.", "Ajoaika ei ole saatavilla juuri nyt.") : null;
    }

    private async Task SaveAreaAsync(AreaDetail detail)
    {
        if (CurrentUserId is not { } userId)
        {
            await ClientFunctions.LoginShowAsync(T("Sign in to save areas", "Kirjaudu tallentaaksesi alueita"));
            return;
        }

        var saved = await _queries.FavoritesAsync(userId);
        if (saved.Count >= CurrentPlan.SavedLimit)
        {
            _toast.Value = string.Format(Culture, T("Your plan keeps up to {0} saved places.", "Tilauksesi säilyttää enintään {0} paikkaa."), CurrentPlan.SavedLimit);
            return;
        }

        var label = string.Format(Culture, T("{0} · {1} · {2:0.#} ha", "{0} · {1} · {2:0.#} ha"), SpeciesName(detail.Area.Species), SuitabilityLabel(detail.Area.Suitability), detail.Area.AreaHa);
        var target = detail.Area.AccessLat is { } lat && detail.Area.AccessLon is { } lon ? (lat, lon) : (detail.Area.Lat, detail.Area.Lon);
        await _queries.AddFavoriteAsync(userId, label, detail.Area.Species, detail.Area.Id, target.Item1, target.Item2, null);
        await LoadSavedAsync();
        await _queries.TrackAsync(userId, "area_saved", new { species = detail.Area.Species });
        _toast.Value = T("Saved to your places", "Tallennettu paikkoihisi");
    }
}
