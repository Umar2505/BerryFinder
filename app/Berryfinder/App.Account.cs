using System.Globalization;
using System.Text.Json;
using Dapper;
using Ikon.Common.Core.Protocol;

public sealed record TripCandidate(AreaSummary Area, double? DriveMinutes, double? DriveKm, double WalkAllowanceMinutes, double PickingMinutes, double Value);

public sealed record TripPlan(IReadOnlyList<TripCandidate> Candidates, bool RoutingAvailable, string Note);

// Accounts, saved places, the trip planner, billing and the user's data rights (consent, export, erasure)
public partial class BerryfinderApp
{
    private readonly ClientReactiveList<SavedLocation> _saved = new();
    private readonly ClientReactive<bool> _shareConsent = new(false);
    private readonly ClientReactive<bool> _trainingConsent = new(false);
    private readonly ClientReactive<DateTimeOffset?> _removalDate = new((DateTimeOffset?)null);
    private readonly ClientReactive<bool> _confirmingRemoval = new(false);
    private readonly ClientReactive<string?> _accountMessage = new((string?)null);
    private readonly ClientReactive<string?> _exportUrl = new((string?)null);
    private readonly ClientReactive<bool> _planBusy = new(false);
    private readonly ClientReactive<TripPlan?> _tripPlan = new((TripPlan?)null);
    private readonly ClientReactive<string?> _planProblem = new((string?)null);
    private readonly ClientReactive<string> _tripMinutes = new("180");
    private readonly ClientReactive<string> _tripDriveKm = new("30");
    private readonly ClientReactive<string> _tripWalk = new("500");
    private readonly ClientReactive<string?> _checkoutUrl = new((string?)null);
    private readonly ClientReactive<string?> _billingProblem = new((string?)null);

    // Plan comes from the platform's entitlement for the signed-in user, never from a flag the app stores
    private string PlanIdNow()
    {
        try
        {
            var payments = PaymentsService.Instance;
            if (Plans.Offers.Any(o => o.PlanId == "professional" && payments.IsEntitled(o.OfferId))) return "professional";
            if (Plans.Offers.Any(o => o.PlanId == "premium" && payments.IsEntitled(o.OfferId))) return "premium";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            // Payments not available (e.g. before sign-in): treat as the free plan
        }

        return "free";
    }

    private async Task<bool> HasPremiumAsync(string userId)
    {
        try
        {
            foreach (var offer in Plans.Offers)
            {
                if ((await PaymentsService.Instance.GetEntitlementAsync(offer.OfferId, userId)).Active)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Entitlement lookup failed for a user: {ex.Message}");
        }

        return false;
    }

    private static async Task DeclareOffersAsync()
    {
        foreach (var offer in Plans.Offers)
        {
            await PaymentsService.Instance.CreateOfferAsync(new OfferSpec(offer.OfferId, offer.NameEn,
                new OfferPriceSpec(offer.AmountMinor, offer.Currency, PriceKind.Recurring, offer.Interval == "year" ? PriceInterval.Year : PriceInterval.Month)));
        }
    }

    private async Task LoadAccountAsync(string userId)
    {
        await using var db = await OpenDbAsync();
        var consent = await db.QuerySingleOrDefaultAsync<(bool Share, bool Training)>("SELECT consent_share_aggregated, consent_training FROM user_accounts WHERE user_id = @userId", new { userId });
        _shareConsent.Value = consent.Share;
        _trainingConsent.Value = consent.Training;
        await LoadSavedAsync();

        if (ClientFunctions.GetAccountRemovalAsync() is { } pending && await pending is { } removal)
        {
            _removalDate.Value = removal.ScheduledFor;
        }
    }

    private async Task LoadSavedAsync()
    {
        if (CurrentUserId is { } userId)
        {
            _saved.ReplaceAll(await _queries.FavoritesAsync(userId));
        }
    }

    // ─── Saved places ─────────────────────────────────────────────────────

    private void RenderSavedPage(UIView view)
    {
        RenderScrollPage(view, page =>
        {
            page.Text([Text.H1, "font-heading"], text: T("Saved places", "Tallennetut paikat"));

            if (!_isSignedIn.Value)
            {
                page.EmptyState(T("Sign in to keep your places", "Kirjaudu säilyttääksesi paikkasi"), icon: "bookmark",
                    description: T("Saved places are private to you.", "Tallennetut paikat näkyvät vain sinulle."),
                    action: a => a.Button([Button.PrimaryMd], text: T("Sign in", "Kirjaudu"), onClick: async () => await ClientFunctions.LoginShowAsync(T("Sign in to save areas", "Kirjaudu tallentaaksesi alueita"))));
                return;
            }

            if (_saved.Count == 0)
            {
                page.EmptyState(T("Nothing saved yet", "Ei vielä tallennettuja"), icon: "bookmark-plus",
                    description: T("Open an area on the map and press Save to keep it here.", "Avaa alue kartalla ja paina Tallenna, niin se säilyy täällä."),
                    action: a => a.Button([Button.PrimaryMd], text: T("Open the map", "Avaa kartta"), onClick: async () => await GoAsync("/map")));
                return;
            }

            if (_lastRemoved.Value is { } removed)
            {
                page.Alert(T("Removed ", "Poistettu ") + removed.Label, tone: SemanticTone.Info, onDismiss: async () => _lastRemoved.Value = null, content: a =>
                    a.Button([Button.OutlineSm, "mt-2 self-start"], text: T("Undo", "Kumoa"), onClick: async () =>
                    {
                        if (CurrentUserId is { } uid)
                        {
                            await _queries.AddFavoriteAsync(uid, removed.Label, removed.Species, removed.AreaId, removed.Lat, removed.Lon, removed.Notes);
                            _lastRemoved.Value = null;
                            await LoadSavedAsync();
                        }
                    }));
            }

            page.Text([Text.Small, "text-muted-foreground"], text: string.Format(Culture, T("{0} of {1} places used", "{0} / {1} paikkaa käytössä"), _saved.Count, CurrentPlan.SavedLimit));
            foreach (var place in _saved)
            {
                page.Card([Card.Default, "p-0"], key: $"saved-{place.Id}", content: card =>
                    card.Row(["items-center gap-3 p-4 flex-wrap"], content: row =>
                    {
                        row.Column(["flex-1 min-w-[200px] gap-0.5"], content: c =>
                        {
                            c.Text([Text.BodyStrong], text: place.Label);
                            c.Text([Text.Caption, "text-muted-foreground"], text: T("Saved ", "Tallennettu ") + place.CreatedAt.ToString("d", Culture));
                        });
                        row.Link([Button.PrimarySm, "gap-1"], href: Router.NavigationUrl(place.Lat, place.Lon), target: "_blank", rel: "noopener", content: l =>
                        {
                            l.Icon([Icon.Sm], name: "navigation");
                            l.Text(text: T("Navigate", "Navigoi"));
                        });
                        row.Button([Button.OutlineSm], text: T("Open on map", "Avaa kartalla"), onClick: async () => await OpenSavedOnMapAsync(place));
                        row.Button([Button.GhostErrorSm], ariaLabel: T("Remove", "Poista") + " " + place.Label, onClick: async () => await RemoveSavedAsync(place),
                            content: b => b.Icon([Icon.Sm], name: "trash-2"));
                    }));
            }
        });
    }

    private async Task OpenSavedOnMapAsync(SavedLocation place)
    {
        if (place.Species is { } species && Species.Supported.Contains(species))
        {
            _species.Value = species;
        }

        await GoAsync("/map");
        _viewRequest.Value = new MapView(place.Lat, place.Lon, 14, (_viewRequest.Value?.Key ?? 0) + 1);
        if (place.AreaId is { } areaId)
        {
            await SelectAreaAsync(areaId, fly: false);
        }
    }

    // Removal can be undone for a few seconds' worth of attention: the row is restored from the toast action's data
    private async Task RemoveSavedAsync(SavedLocation place)
    {
        if (CurrentUserId is not { } userId || await _queries.RemoveFavoriteAsync(userId, place.Id) is null)
        {
            return;
        }

        await LoadSavedAsync();
        _lastRemoved.Value = place;
        _toast.Value = T("Removed from your places", "Poistettu paikoistasi");
    }

    private readonly ClientReactive<SavedLocation?> _lastRemoved = new((SavedLocation?)null);

    // ─── Trip planner ─────────────────────────────────────────────────────

    private void RenderPlanPage(UIView view)
    {
        RenderScrollPage(view, page =>
        {
            page.Column(["gap-2"], content: head =>
            {
                head.Text([Text.H1, "font-heading"], text: T("Smart trip planner", "Älykäs retkisuunnittelija"));
                head.Text([Text.Body, "text-muted-foreground"], text: T("Tell us where you start and how long you have. We rank areas by how good the forest is against how much of your time the drive eats.",
                    "Kerro lähtöpaikkasi ja käytettävissä oleva aika. Järjestämme alueet metsän laadun ja ajoajan mukaan."));
            });

            if (!CurrentPlan.TripPlanner)
            {
                page.Card([Card.Elevated], content: c =>
                {
                    c.Text([Text.H4, "font-heading"], text: T("Trip planning is part of Premium", "Retkisuunnittelu kuuluu Premiumiin"));
                    c.Text([Text.Small, "text-muted-foreground"], text: T("Premium plans trips using real driving times from where you are.", "Premium suunnittelee retket todellisilla ajoajoilla sijainnistasi."));
                    c.Button([Button.PrimaryMd, "mt-3 self-start"], text: T("See plans", "Katso tilaukset"), onClick: async () => await GoAsync("/pricing"));
                });
                return;
            }

            page.Card([Card.Default], content: form =>
            {
                form.Column(["gap-4"], content: f =>
                {
                    f.Column(["gap-1.5"], content: s =>
                    {
                        s.Text([Text.Label], text: T("Berry", "Marja"));
                        RenderSpeciesToggle(s, compact: false);
                    });

                    f.Column(["gap-1.5"], content: s =>
                    {
                        s.Text([Text.Label], text: T("Start", "Lähtöpaikka"));
                        if (_userLocation.Value is { } u)
                        {
                            s.Text([Text.Small], text: string.Format(CultureInfo.InvariantCulture, T("Your location ({0:0.000}, {1:0.000})", "Sijaintisi ({0:0.000}, {1:0.000})"), u.Lat, u.Lon));
                        }

                        s.Button([Button.OutlineSm, "self-start gap-2"], onClick: async () => await UseMyLocationAsync(), content: b =>
                        {
                            b.Icon([Icon.Sm], name: "locate-fixed");
                            b.Text(text: _userLocation.Value is null ? T("Use my location", "Käytä sijaintiani") : T("Update my location", "Päivitä sijaintini"));
                        });
                        s.Text([Text.Caption, "text-muted-foreground"], text: T("Or search a place on the map page first; the map centre is used when no location is set.", "Tai hae paikka kartta-sivulla; ilman sijaintia käytetään kartan keskustaa."));
                    });

                    f.Row(["gap-3 flex-wrap"], content: r =>
                    {
                        r.Column(["gap-1.5 min-w-[150px] flex-1"], content: s =>
                        {
                            s.Text([Text.Label], text: T("Time available", "Käytettävissä oleva aika"));
                            s.Select(value: _tripMinutes.Value, ariaLabel: T("Time available", "Käytettävissä oleva aika"), onValueChange: async v => _tripMinutes.Value = v,
                                options: [new("90", T("1.5 hours", "1,5 tuntia")), new("180", T("3 hours", "3 tuntia")), new("300", T("5 hours", "5 tuntia")), new("480", T("Full day", "Koko päivä"))]);
                        });
                        r.Column(["gap-1.5 min-w-[150px] flex-1"], content: s =>
                        {
                            s.Text([Text.Label], text: T("Max drive (one way)", "Enimmäisajomatka (yhteen suuntaan)"));
                            s.Select(value: _tripDriveKm.Value, ariaLabel: T("Max drive", "Enimmäisajomatka"), onValueChange: async v => _tripDriveKm.Value = v,
                                options: [new("15", "15 km"), new("30", "30 km"), new("50", "50 km"), new("80", "80 km")]);
                        });
                        r.Column(["gap-1.5 min-w-[150px] flex-1"], content: s =>
                        {
                            s.Text([Text.Label], text: T("Walking from the road", "Kävely tieltä"));
                            s.Select(value: _tripWalk.Value, ariaLabel: T("Walking", "Kävely"), onValueChange: async v => _tripWalk.Value = v,
                                options: [new("200", T("Short (≤ 200 m)", "Lyhyt (≤ 200 m)")), new("500", T("Moderate (≤ 500 m)", "Kohtalainen (≤ 500 m)")), new("1500", T("Happy to walk (≤ 1.5 km)", "Kävelen mielelläni (≤ 1,5 km)"))]);
                        });
                    });

                    f.Button([Button.PrimaryLg, "self-start gap-2"], disabled: _planBusy.Value, onClick: async () => await RunPlanAsync(), content: b =>
                    {
                        if (_planBusy.Value) b.Box([Icon.Spinner, "w-4 h-4"]); else b.Icon([Icon.Sm], name: "route");
                        b.Text(text: _planBusy.Value ? T("Planning…", "Suunnitellaan…") : T("Plan my trip", "Suunnittele retkeni"));
                    });
                });
            });

            if (_planProblem.Value is { } problem)
            {
                page.Alert(problem, tone: SemanticTone.Error);
            }

            if (_tripPlan.Value is { } plan)
            {
                RenderTripResults(page, plan);
            }
        });
    }

    private void RenderTripResults(UIView view, TripPlan plan)
    {
        if (!plan.RoutingAvailable)
        {
            view.Alert(T("Driving times aren't available right now", "Ajoaikoja ei saada juuri nyt"), tone: SemanticTone.Warning,
                description: T("The road router didn't answer, so candidates are ranked by forest quality alone and show no driving time. Try again in a minute.",
                    "Reitityspalvelu ei vastannut, joten ehdokkaat on järjestetty vain metsän laadun mukaan ilman ajoaikaa. Yritä hetken kuluttua uudelleen."));
        }

        if (plan.Candidates.Count == 0)
        {
            view.EmptyState(T("No area fits those limits", "Mikään alue ei sovi rajauksiin"), icon: "route",
                description: T("Try a longer drive, more time or a longer walk.", "Kokeile pidempää ajomatkaa, enemmän aikaa tai pidempää kävelyä."));
            return;
        }

        view.Text([Text.H3, "font-heading"], text: T("Best options for your time", "Parhaat vaihtoehdot ajallesi"));
        var rank = 0;
        foreach (var c in plan.Candidates)
        {
            rank++;
            var number = rank;
            view.Card([Card.Elevated, "p-0"], key: $"trip-{c.Area.Id}", content: card =>
                card.Column(["p-4 gap-3"], content: col =>
                {
                    col.Row(["items-center gap-3"], content: r =>
                    {
                        r.Box(["w-9 h-9 shrink-0 rounded-full bg-[#1d3b2a] text-[#f5f1e6] flex items-center justify-center text-sm font-semibold"], content: b => b.Text(text: number.ToString(Culture)));
                        r.Column(["gap-0.5 flex-1"], content: t =>
                        {
                            t.Text([Text.BodyStrong], text: $"{SuitabilityLabel(c.Area.Suitability)} · {c.Area.AreaHa:0.#} ha");
                            t.Text([Text.Small, "text-muted-foreground"], text: c.DriveMinutes is { } m
                                ? string.Format(Culture, T("{0:0} min drive ({1:0.#} km by road) each way", "{0:0} min ajoa ({1:0.#} km tietä pitkin) yhteen suuntaan"), m, c.DriveKm)
                                : T("Driving time unavailable", "Ajoaika ei saatavilla"));
                        });
                    });
                    col.Text([Text.Small], text: c.DriveMinutes is null
                        ? T("Picking time can't be estimated without a driving time.", "Poimina-aikaa ei voi arvioida ilman ajoaikaa.")
                        : string.Format(Culture, T("About {0:0} min left for picking after driving both ways and a {1:0}-minute walking allowance. The walk is a straight-line allowance from the road, not a mapped path.",
                            "Poimintaan jää noin {0:0} min ajettuasi molempiin suuntiin ja {1:0} min kävelyvarauksen jälkeen. Kävely on linnuntievaraus tieltä, ei kartoitettu polku."), c.PickingMinutes, c.WalkAllowanceMinutes * 2));
                    col.Row(["gap-2 flex-wrap"], content: r =>
                    {
                        var target = c.Area.AccessLat is { } lat && c.Area.AccessLon is { } lon ? (lat, lon) : (c.Area.Lat, c.Area.Lon);
                        r.Link([Button.PrimarySm, "gap-1"], href: Router.NavigationUrl(target.Item1, target.Item2), target: "_blank", rel: "noopener",
                            onClick: async () => await _queries.TrackAsync(CurrentUserId, "trip_navigation", new { species = c.Area.Species }),
                            content: l => { l.Icon([Icon.Sm], name: "navigation"); l.Text(text: T("Navigate", "Navigoi")); });
                        r.Button([Button.OutlineSm], text: T("Details on map", "Tiedot kartalla"), onClick: async () =>
                        {
                            await GoAsync("/map");
                            await SelectAreaAsync(c.Area.Id, fly: true);
                        });
                    });
                }));
        }

        view.Text([Text.Caption, "text-muted-foreground"], text: plan.Note);
    }

    private async Task UseMyLocationAsync()
    {
        var location = await ClientFunctions.GetLocationAsync();
        if (location is null)
        {
            _toast.Value = T("Couldn't read your location. Check that location access is allowed for this site.", "Sijaintia ei saatu. Tarkista, että sijainnin käyttö on sallittu.");
            return;
        }

        _userLocation.Value = new UserLocation(location.Latitude, location.Longitude, location.Accuracy);
    }

    private async Task RunPlanAsync()
    {
        _planProblem.Value = null;
        _tripPlan.Value = null;
        var start = _userLocation.Value is { } u ? (u.Lat, u.Lon) : _viewport.Value is { } vp ? (vp.Lat, vp.Lon) : (65.01, 25.47);

        _planBusy.Value = true;
        try
        {
            var request = new TripRequest(start.Item1, start.Item2, _species.Value, int.Parse(_tripMinutes.Value, CultureInfo.InvariantCulture),
                double.Parse(_tripDriveKm.Value, CultureInfo.InvariantCulture), double.Parse(_tripWalk.Value, CultureInfo.InvariantCulture));
            _tripPlan.Value = await PlanTripCoreAsync(CurrentUserId, request);
            await _queries.TrackAsync(CurrentUserId, "trip_planned", new { species = request.Species, candidates = _tripPlan.Value.Candidates.Count });
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Trip planning failed: {ex.Message}");
            _planProblem.Value = T("Couldn't plan the trip — your settings are kept, try again.", "Retken suunnittelu epäonnistui — asetukset säilyvät, yritä uudelleen.");
        }
        finally
        {
            _planBusy.Value = false;
        }
    }

    // Value of a candidate = habitat quality x picking time left after real driving time and a walking allowance.
    // Driving comes from the road router; when it is down, nothing is passed off as a route.
    private async Task<TripPlan> PlanTripCoreAsync(string? userId, TripRequest request)
    {
        // Pre-filter by straight line (a fast bound, never shown as a distance), then ask the router for the shortlist
        var areas = await _queries.SearchAreasAsync(request.Species, request.Lat, request.Lon, request.MaxDriveKm, false, request.MaxWalkM, true, 25);
        var shortlist = areas.Take(15).ToList();
        var targets = shortlist.Select(a => a.AccessLat is { } lat && a.AccessLon is { } lon ? (lat, lon) : (a.Lat, a.Lon)).ToList();
        var drives = await Router.DriveTimesAsync((request.Lat, request.Lon), targets);
        var routing = drives.Any(d => d is not null);
        var candidates = new List<TripCandidate>();

        for (var i = 0; i < shortlist.Count; i++)
        {
            var a = shortlist[i];
            var drive = drives.Count > i ? drives[i] : null;
            // Walking allowance: straight-line distance x 1.5 for terrain, at 4 km/h, one way
            var walk = (a.DistRoadM ?? 150) * 1.5 / 1000 / 4 * 60;
            var quality = (a.Suitability == "high" ? 1.0 : 0.6) * Math.Min(1.0, Math.Sqrt(a.AreaHa / 5)) * (0.5 + 0.5 * a.MeanScore);

            if (drive is null)
            {
                candidates.Add(new TripCandidate(a, null, null, walk, 0, quality * 0.1));
                continue;
            }

            if (drive.Kilometres > request.MaxDriveKm * 1.6)
            {
                continue;
            }

            var picking = request.AvailableMinutes - 2 * drive.Minutes - 2 * walk;
            if (picking < 20)
            {
                continue;
            }

            candidates.Add(new TripCandidate(a, drive.Minutes, drive.Kilometres, walk, picking, quality * Math.Min(picking, 240) / 240.0));
        }

        var ranked = candidates.OrderByDescending(c => c.Value).Take(6).ToList();

        await using var db = await OpenDbAsync();
        if (userId is not null)
        {
            await Queries.EnsureUserAsync(db, userId);
        }

        await db.ExecuteAsync("""
            INSERT INTO picking_trips (user_id, start_geom, params, candidates)
            VALUES (@userId, ST_SetSRID(ST_MakePoint(@lon, @lat), 4326), CAST(@p AS jsonb), CAST(@c AS jsonb))
            """, new
        {
            userId,
            lat = request.Lat,
            lon = request.Lon,
            p = JsonSerializer.Serialize(request),
            c = JsonSerializer.Serialize(ranked.Select(c => new { area = c.Area.Id, drive = c.DriveMinutes, picking = c.PickingMinutes })),
        });

        return new TripPlan(ranked, routing, T("Driving times come from OpenStreetMap road routing. Estimates are uncertain and cannot guarantee any harvest; check access rules on site.",
            "Ajoajat tulevat OpenStreetMapin tiereitityksestä. Arviot ovat epävarmoja eivätkä takaa satoa; tarkista käyttöoikeudet paikan päällä."));
    }

    // ─── Account, privacy and data rights ────────────────────────────────

    private void RenderAccountPage(UIView view)
    {
        RenderScrollPage(view, page =>
        {
            page.Text([Text.H1, "font-heading"], text: T("Account", "Tili"));

            if (!_isSignedIn.Value)
            {
                page.EmptyState(T("You're browsing as a guest", "Selaat vieraana"), icon: "user-round",
                    description: T("Sign in to save places, send field reports and keep a plan.", "Kirjaudu tallentaaksesi paikkoja, lähettääksesi havaintoja ja säilyttääksesi tilauksen."),
                    action: a => a.Button([Button.PrimaryMd], text: T("Sign in", "Kirjaudu"), onClick: async () => await ClientFunctions.LoginShowAsync(T("Sign in to BerryFinder", "Kirjaudu BerryFinderiin"))));
                return;
            }

            var plan = PlanIdNow();
            page.Card([Card.Elevated], content: c =>
            {
                c.Row(["items-center justify-between gap-3 flex-wrap"], content: r =>
                {
                    r.Column(["gap-0.5"], content: t =>
                    {
                        t.Text([Text.Label], text: T("Your plan", "Tilauksesi"));
                        t.Text([Text.H3, "font-heading"], text: plan switch { "professional" => T("Professional", "Ammattilainen"), "premium" => "Premium", _ => T("Free", "Ilmainen") });
                    });
                    r.Button([plan == "free" ? Button.PrimaryMd : Button.OutlineMd], text: plan == "free" ? T("Upgrade", "Päivitä") : T("Plans", "Tilaukset"), onClick: async () => await GoAsync("/pricing"));
                });
                if (_isAdmin.Value)
                {
                    c.Text([Text.Caption, "text-muted-foreground mt-2"], text: T("You're the administrator, so every feature is open to you.", "Olet ylläpitäjä, joten kaikki ominaisuudet ovat käytössäsi."));
                }
            });

            page.Card([Card.Default], content: c =>
            {
                c.Text([Text.H4, "font-heading"], text: T("Your field reports and consent", "Havaintosi ja suostumukset"));
                c.Text([Text.Small, "text-muted-foreground"], text: T("Your coordinates are private by default. These two switches are the defaults for new reports; you can still change them on each report.",
                    "Koordinaattisi ovat oletuksena yksityisiä. Nämä kaksi valintaa ovat uusien havaintojen oletuksia; voit muuttaa niitä jokaisessa havainnossa."));
                c.Switch(label: T("Share my reports in aggregated area summaries (never exact spots)", "Jaa havaintoni koottuina aluetiivistelminä (ei koskaan tarkkoja paikkoja)"),
                    value: _shareConsent.Value, onValueChange: async v => await SaveConsentAsync(share: v, training: null));
                c.Switch(label: T("Allow my reports to be used to train and validate the berry models", "Salli havaintojeni käyttö marjamallien kouluttamiseen ja validointiin"),
                    value: _trainingConsent.Value, onValueChange: async v => await SaveConsentAsync(share: null, training: v));
            });

            page.Card([Card.Default], content: c =>
            {
                c.Text([Text.H4, "font-heading"], text: T("Your data", "Tietosi"));
                c.Text([Text.Small, "text-muted-foreground"], text: T("Download everything we hold about you as a JSON file, or delete your account.", "Lataa kaikki tallessa olevat tietosi JSON-tiedostona tai poista tilisi."));
                c.Row(["gap-2 flex-wrap mt-2"], content: r =>
                {
                    r.Button([Button.OutlineMd, "gap-2"], onClick: async () => await PrepareExportAsync(), content: b =>
                    {
                        b.Icon([Icon.Sm], name: "download");
                        b.Text(text: T("Prepare my data export", "Valmistele tietojen vienti"));
                    });
                    if (_exportUrl.Value is { } url)
                    {
                        r.Link([Button.PrimaryMd], href: url, target: "_blank", rel: "noopener", text: T("Open export (link valid 1 hour)", "Avaa vienti (linkki voimassa 1 tunnin)"));
                    }
                });

                c.Separator(["my-3"]);
                if (_removalDate.Value is { } date)
                {
                    c.Text([Text.Body], text: string.Format(Culture, T("Your account and data will be deleted on {0:d}. You can change your mind until then.", "Tilisi ja tietosi poistetaan {0:d}. Voit perua poiston siihen asti."), date));
                    c.Button([Button.OutlineMd, "self-start"], text: T("Keep my account", "Säilytä tilini"), onClick: async () =>
                    {
                        if (await ClientFunctions.CancelAccountRemovalAsync() is { Succeeded: true }) _removalDate.Value = null;
                    });
                }
                else
                {
                    c.Button([Button.ErrorMd, "self-start"], text: T("Delete my account…", "Poista tilini…"), onClick: async () => _confirmingRemoval.Value = true);
                }

                if (_accountMessage.Value is { } message)
                {
                    c.Text([Text.Caption, "text-muted-foreground mt-1"], text: message);
                }
            });

            page.Text([Text.Caption, "text-muted-foreground"], text: T("Reports are kept while your account exists; account deletion removes them, your saved places and your trip history. Aggregated, anonymised counts may remain.",
                "Havainnot säilytetään tilin olemassaolon ajan; tilin poisto poistaa ne, tallennetut paikat ja retkihistorian. Koottuja, anonymisoituja lukuja voi säilyä."));
            page.Row(["gap-3 flex-wrap"], content: r =>
            {
                r.Button([Button.GhostSm], text: T("Responsible picking", "Vastuullinen marjastus"), onClick: async () => await GoAsync("/responsible"));
                r.Button([Button.GhostSm], text: T("Contact", "Yhteystiedot"), onClick: async () => await GoAsync("/contact"));
            });

            page.AlertDialog(open: _confirmingRemoval.Value, onOpenChange: async open => _confirmingRemoval.Value = open,
                overlayStyle: [AlertDialog.Overlay], contentStyle: [AlertDialog.Content],
                title: T("Delete your account?", "Poistetaanko tilisi?"), titleStyle: [AlertDialog.Title],
                description: T("Your account, reports, saved places and trips are deleted after a 14-day grace period, which you can cancel.", "Tilisi, havaintosi, tallennetut paikat ja retket poistetaan 14 päivän lisäajan jälkeen, jonka voit perua."),
                descriptionStyle: [AlertDialog.Description], footerStyle: [AlertDialog.Footer],
                cancelLabel: T("Keep it", "Säilytä"), cancelStyle: [AlertDialog.Cancel],
                actionLabel: T("Delete my account", "Poista tilini"), actionStyle: [Button.ErrorMd], onAction: RequestRemovalAsync);
        });
    }

    private async Task SaveConsentAsync(bool? share, bool? training)
    {
        if (CurrentUserId is not { } userId)
        {
            return;
        }

        if (share is { } s) _shareConsent.Value = s;
        if (training is { } t) _trainingConsent.Value = t;

        await using var db = await OpenDbAsync();
        await db.ExecuteAsync("""
            UPDATE user_accounts SET consent_share_aggregated = @share, consent_training = @training, consent_updated_at = now() WHERE user_id = @userId
            """, new { userId, share = _shareConsent.Value, training = _trainingConsent.Value });
        await Audit.WriteAsync(db, userId, "consent.update", $"user:{userId}", new { share = _shareConsent.Value, training = _trainingConsent.Value });
    }

    private async Task PrepareExportAsync()
    {
        if (CurrentUserId is not { } userId)
        {
            return;
        }

        await using var db = await OpenDbAsync();
        var export = new
        {
            exportedAt = DateTime.UtcNow,
            account = await db.QuerySingleOrDefaultAsync("SELECT user_id, role, language, consent_share_aggregated, consent_training, created_at FROM user_accounts WHERE user_id = @userId", new { userId }),
            savedLocations = await _queries.FavoritesAsync(userId),
            observations = await _queries.MyObservationsAsync(userId, 5000),
            trips = await db.QueryAsync("SELECT id, params, candidates, created_at FROM picking_trips WHERE user_id = @userId ORDER BY created_at", new { userId }),
            contactMessages = await db.QueryAsync("SELECT at, topic, message FROM contact_messages WHERE user_id = @userId", new { userId }),
        };

        var uri = new AssetUri(AssetClass.CloudFile, $"exports/{Guid.NewGuid():N}/berryfinder-data.json", spaceId: app.GlobalState.SpaceId);
        await Asset.Instance.SetBytesAsync(uri, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true })),
            new AssetMetadata(mimeType: "application/json", expiresAt: DateTime.UtcNow.AddHours(2)));
        _exportUrl.Value = (await Asset.Instance.GetMetadataAsync(uri)).Url;
        await Audit.WriteAsync(db, userId, "account.export", $"user:{userId}", new { });
    }

    private async Task RequestRemovalAsync()
    {
        if (app.GlobalState.ServerRunType == ServerRunType.Local)
        {
            _accountMessage.Value = T("Account deletion is switched off in a local run.", "Tilin poisto on pois päältä paikallisessa ajossa.");
            return;
        }

        var removal = await ClientFunctions.RequestAccountRemovalAsync();
        if (removal is not { Succeeded: true })
        {
            _accountMessage.Value = T("Couldn't schedule the deletion. Sign in and try again.", "Poistoa ei voitu ajoittaa. Kirjaudu ja yritä uudelleen.");
            return;
        }

        _removalDate.Value = removal.ScheduledFor;
    }

    // Runs when the removal goes through: deletes everything keyed by the user, including photo files
    [Trigger(TriggerEventType.UserErased)]
    internal async Task EraseUserDataAsync(UserDataErasureEventArgs args)
    {
        await using var db = await OpenDbAsync();
        var photos = await db.QueryAsync<string>("""
            SELECT m.asset_uri FROM observation_media m JOIN field_observations o ON o.id = m.observation_id WHERE o.user_id = @userId
            """, new { userId = args.UserId });

        foreach (var uri in photos)
        {
            try
            {
                if (AssetUri.TryParse(uri, out var parsed))
                {
                    await Asset.Instance.DeleteAsync(parsed);
                }
            }
            catch (FileNotFoundException)
            {
                // Removed by an earlier delivery of this event
            }
        }

        // Cascades remove observations, media rows, saved places, trips and subscriptions
        await db.ExecuteAsync("""
            DELETE FROM user_accounts WHERE user_id = @userId;
            DELETE FROM contact_messages WHERE user_id = @userId;
            UPDATE analytics_events SET user_id = NULL WHERE user_id = @userId;
            """, new { userId = args.UserId });
        await db.ExecuteAsync("INSERT INTO audit_logs (actor, action, target) VALUES ('system', 'account.erased', @t)", new { t = $"erasure:{args.ErasureId}" });
    }

    // ─── Billing ──────────────────────────────────────────────────────────

    private async Task StartCheckoutAsync(string offerId)
    {
        if (!_isSignedIn.Value)
        {
            await ClientFunctions.LoginShowAsync(T("Sign in to subscribe", "Kirjaudu tilataksesi"));
            return;
        }

        try
        {
            var link = await PaymentsService.Instance.CreatePaymentLinkAsync(offerId);
            _checkoutUrl.Value = link.Url;
            _billingProblem.Value = null;
            await _queries.TrackAsync(CurrentUserId, "checkout_started", new { offerId });
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Checkout could not start: {ex.Message}");
            _billingProblem.Value = T("Billing isn't switched on for this pilot yet, so nothing was charged. Contact us for early access.",
                "Laskutus ei ole vielä käytössä tässä pilotissa, joten mitään ei veloitettu. Ota yhteyttä saadaksesi varhaisen pääsyn.");
        }
    }
}
