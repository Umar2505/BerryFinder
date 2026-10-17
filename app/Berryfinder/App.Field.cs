using System.Globalization;
using System.Text.Json;

// Field Mode: large touch targets, GPS, saved places and the observation form. Reports are private by
// default; sharing an aggregated summary and training use each need their own explicit switch.
public partial class BerryfinderApp
{
    private readonly UserReactive<string?> _obsOutcome = new((string?)null);
    private readonly UserReactive<string> _obsMethod = new("visit");
    private readonly UserReactive<string?> _obsStage = new((string?)null);
    private readonly UserReactive<string> _obsMinutes = new("");
    private readonly UserReactive<string> _obsArea = new("");
    private readonly UserReactive<string> _obsLitres = new("");
    private readonly UserReactive<string> _obsPlots = new("");
    private readonly UserReactive<string> _obsRipe = new("");
    private readonly UserReactive<string> _obsNotes = new("");
    private readonly UserReactive<string> _obsSpecies = new(Species.Bilberry);
    private readonly UserReactive<LatLon?> _obsPoint = new((LatLon?)null);
    private readonly UserReactive<double?> _obsAccuracy = new((double?)null);
    private readonly UserReactive<string> _obsLocationSource = new("gps");
    private readonly UserReactive<long?> _obsAreaId = new((long?)null);
    private readonly UserReactive<bool> _obsShare = new(false);
    private readonly UserReactive<bool> _obsTraining = new(false);
    private readonly UserReactiveList<PhotoRef> _obsPhotos = new();
    private readonly ClientReactive<bool> _obsBusy = new(false);
    private readonly ClientReactive<string?> _obsError = new((string?)null);
    private readonly ClientReactive<string?> _obsDone = new((string?)null);
    private readonly ClientReactive<bool> _obsPicking = new(false);
    private readonly ClientReactive<string?> _photoProblem = new((string?)null);

    private static readonly (string Value, string Icon, string En, string Fi)[] OutcomeChoices =
    [
        ("many", "smile", "Many berries", "Paljon marjoja"),
        ("moderate", "meh", "A moderate amount", "Kohtalaisesti"),
        ("few", "frown", "A few berries", "Muutamia marjoja"),
        ("unripe", "sprout", "Unripe berries", "Raakoja marjoja"),
        ("none", "circle-slash", "No berries found", "Ei marjoja"),
        ("not_surveyed", "map-pin-off", "Didn't look properly", "En etsinyt kunnolla"),
    ];

    private Task StartReportAsync(AreaSummary area)
    {
        _obsSpecies.Value = area.Species;
        _obsAreaId.Value = area.Id;
        _obsPoint.Value = null;
        _obsDone.Value = null;
        _obsShare.Value = _shareConsent.Value;
        _obsTraining.Value = _trainingConsent.Value;
        return GoAsync("/field");
    }

    private void RenderFieldPage(UIView view)
    {
        RenderScrollPage(view, page =>
        {
            page.Column(["gap-1"], content: head =>
            {
                head.Text([Text.H1, "font-heading"], text: T("Field mode", "Maastotila"));
                head.Text([Text.Body, "text-muted-foreground"], text: T("Did you find the berries you were looking for? Your answer helps everyone, and makes the model better.",
                    "Löysitkö etsimiäsi marjoja? Vastauksesi auttaa kaikkia ja parantaa mallia."));
            });

            if (!_isSignedIn.Value)
            {
                page.EmptyState(T("Sign in to send a report", "Kirjaudu lähettääksesi havainnon"), icon: "clipboard-pen",
                    description: T("Reports need an account so we can stop spam and let you see and delete your own.", "Havainnot vaativat tilin, jotta voimme torjua roskaa ja jotta voit nähdä ja poistaa omasi."),
                    action: a => a.Button([Button.PrimaryLg], text: T("Sign in", "Kirjaudu"), onClick: async () => await ClientFunctions.LoginShowAsync(T("Sign in to send field reports", "Kirjaudu lähettääksesi havaintoja"))));
                return;
            }

            if (_obsDone.Value is { } done)
            {
                page.Card([Card.Elevated], content: c =>
                {
                    c.Row(["items-center gap-3"], content: r =>
                    {
                        r.Icon([Icon.Lg, "text-success-600"], name: "circle-check");
                        r.Text([Text.H4, "font-heading"], text: T("Thank you, report sent", "Kiitos, havainto lähetetty"));
                    });
                    c.Text([Text.Small, "text-muted-foreground"], text: done);
                    c.Row(["gap-2 mt-3 flex-wrap"], content: r =>
                    {
                        r.Button([Button.PrimaryMd], text: T("Send another", "Lähetä toinen"), onClick: async () => { _obsDone.Value = null; ClearObservationForm(); });
                        r.Button([Button.OutlineMd], text: T("Back to map", "Takaisin karttaan"), onClick: async () => await GoAsync("/map"));
                    });
                });
                RenderMyReports(page);
                return;
            }

            RenderObservationForm(page);
            RenderMyReports(page);
        });
    }

    private void RenderObservationForm(UIView page)
    {
        page.Card([Card.Default], content: form =>
        {
            form.Column(["gap-6"], content: f =>
            {
                f.Column(["gap-2"], content: s =>
                {
                    s.Text([Text.Label], text: T("1. Which berry?", "1. Mikä marja?"));
                    s.ToggleGroupSingle(["grid grid-cols-2 sm:grid-cols-3 gap-2"], value: _obsSpecies.Value, ariaLabel: T("Berry", "Marja"),
                        onValueChange: async v => { if (BerryCatalog.TryGet(v, out _)) _obsSpecies.Value = v; },
                        content: g =>
                        {
                            foreach (var berry in BerryCatalog.All)
                            {
                                g.ToggleGroupItem(["h-12 rounded-xl border border-secondary text-base data-[state=on]:bg-brand-solid data-[state=on]:text-[var(--text-brand-button)]"], value: berry.Id,
                                    content: i => i.Text(text: SpeciesName(berry.Id)));
                            }
                        });
                });

                f.Column(["gap-2"], content: s =>
                {
                    s.Text([Text.Label], text: T("2. Where were you?", "2. Missä olit?"));
                    if (_obsPoint.Value is { } p)
                    {
                        s.Text([Text.Small], text: string.Format(CultureInfo.InvariantCulture, T("Location set: {0:0.0000}, {1:0.0000}", "Sijainti asetettu: {0:0.0000}, {1:0.0000}"), p.Lat, p.Lon)
                            + (_obsAccuracy.Value is { } acc ? $" (±{acc:0} m)" : ""));
                    }

                    s.Row(["gap-2 flex-wrap"], content: r =>
                    {
                        r.Button([Button.PrimaryLg, "gap-2 h-14"], onClick: async () => await UseGpsForObservationAsync(), content: b =>
                        {
                            b.Icon([Icon.Md], name: "locate-fixed");
                            b.Text(text: T("Use my GPS", "Käytä GPS:ää"));
                        });
                        r.Button([Button.OutlineLg, "h-14"], text: T("Pick on the map", "Valitse kartalta"), onClick: async () => await PickObservationOnMapAsync());
                    });
                    s.Text([Text.Caption, "text-muted-foreground"], text: T("Your exact spot stays private. Only you and the research team can see it.", "Tarkka paikkasi pysyy yksityisenä. Vain sinä ja tutkimustiimi näette sen."));
                });

                f.Column(["gap-2"], content: s =>
                {
                    s.Text([Text.Label], text: T("3. Did you find the berries you were looking for?", "3. Löysitkö etsimiäsi marjoja?"));
                    s.Box(["grid grid-cols-1 sm:grid-cols-2 gap-2"], content: grid =>
                    {
                        foreach (var (value, icon, en, fi) in OutcomeChoices)
                        {
                            var selected = _obsOutcome.Value == value;
                            grid.Button([selected ? Button.PrimaryLg : Button.OutlineLg, "h-14 justify-start gap-3 text-base"], key: value, onClick: async () => _obsOutcome.Value = value, content: b =>
                            {
                                b.Icon([Icon.Md], name: icon);
                                b.Text(text: T(en, fi));
                            });
                        }
                    });
                });

                f.Column(["gap-3"], content: s =>
                {
                    s.Text([Text.Label], text: T("4. A little more (optional)", "4. Hieman lisää (valinnainen)"));
                    s.Text([Text.Caption, "text-muted-foreground -mt-2"], text: T("A feeling is useful, a measurement is better. These stay clearly separate in our data.", "Tuntuma on hyödyllinen, mittaus parempi. Ne pidetään tiedoissamme selvästi erillään."));
                    s.Row(["gap-3 flex-wrap"], content: r =>
                    {
                        r.TextField(["flex-1 min-w-[140px]"], label: T("Time spent (minutes)", "Käytetty aika (min)"), type: "number", bind: _obsMinutes);
                        r.TextField(["flex-1 min-w-[140px]"], label: T("Area searched (m²)", "Etsitty ala (m²)"), type: "number", bind: _obsArea);
                        r.TextField(["flex-1 min-w-[140px]"], label: T("Amount picked (litres)", "Poimittu määrä (litraa)"), type: "number", bind: _obsLitres);
                    });
                    s.Select(value: _obsStage.Value ?? "", placeholder: T("Ripeness stage", "Kypsyysaste"), ariaLabel: T("Ripeness stage", "Kypsyysaste"),
                        options: [new("flowering", T("Flowering", "Kukinta")), new("unripe", T("Unripe", "Raaka")), new("ripe", T("Ripe", "Kypsä")), new("overripe", T("Overripe", "Ylikypsä"))],
                        onValueChange: async v => _obsStage.Value = v.Length == 0 ? null : v);

                    RenderPlotProtocol(s);
                });

                f.Column(["gap-2"], content: s =>
                {
                    s.Text([Text.Label], text: T("5. A photo (optional)", "5. Kuva (valinnainen)"));
                    s.FileUpload(["border border-dashed border-input rounded-xl p-4 flex items-center justify-center min-h-[72px]"], accept: ["image/jpeg", "image/png"], maxFileSize: 8_000_000,
                        onUploadStart: OnPhotoStartAsync, onUploadComplete: OnPhotoCompleteAsync,
                        onUploadError: async _ => _photoProblem.Value = T("The photo didn't upload — try again or skip it.", "Kuvaa ei voitu ladata — yritä uudelleen tai ohita."),
                        content: z => z.Row(["items-center gap-2 text-muted-foreground"], content: r =>
                        {
                            r.Icon([Icon.Md], name: "camera");
                            r.Text(text: T("Take or choose a photo", "Ota tai valitse kuva"));
                        }));
                    if (_photoProblem.Value is { } problem)
                    {
                        s.Text([Text.Caption, "text-error-primary"], text: problem);
                    }

                    foreach (var photo in _obsPhotos)
                    {
                        s.Row(["items-center gap-2"], key: photo.AssetUri, content: r =>
                        {
                            r.Icon([Icon.Sm, "text-success-600"], name: "image");
                            r.Text([Text.Small], text: $"{photo.Width}×{photo.Height}" + (photo.QualityFlags.Count > 0 ? " · " + string.Join(", ", photo.QualityFlags.Select(QualityFlagLabel)) : ""));
                            r.Button([Button.GhostSm], ariaLabel: T("Remove photo", "Poista kuva"), onClick: async () => _obsPhotos.Remove(photo), content: b => b.Icon([Icon.Sm], name: "x"));
                        });
                    }
                });

                f.Column(["gap-2"], content: s =>
                {
                    s.Text([Text.Label], text: T("6. What may we do with it?", "6. Mitä saamme tehdä sillä?"));
                    s.Switch(label: T("Include in aggregated area summaries (never your exact spot)", "Sisällytä koottuihin aluetiivistelmiin (ei koskaan tarkkaa paikkaasi)"), value: _obsShare.Value, onValueChange: async v => _obsShare.Value = v);
                    s.Switch(label: T("Use to train and validate the models", "Käytä mallien kouluttamiseen ja validointiin"), value: _obsTraining.Value, onValueChange: async v => _obsTraining.Value = v);
                    s.Text([Text.Caption, "text-muted-foreground"], text: T("Both are off unless you turn them on.", "Molemmat ovat pois päältä, ellet kytke niitä päälle."));
                });

                if (_obsError.Value is { } error)
                {
                    f.Alert(error, tone: SemanticTone.Error);
                }

                f.Button([Button.PrimaryLg, "h-14 gap-2"], disabled: _obsBusy.Value, onClick: async () => await SubmitObservationAsync(), content: b =>
                {
                    if (_obsBusy.Value) b.Box([Icon.Spinner, "w-4 h-4"]); else b.Icon([Icon.Md], name: "send");
                    b.Text(text: _obsBusy.Value ? T("Sending…", "Lähetetään…") : T("Send report", "Lähetä havainto"));
                });
            });
        });
    }

    // The standard 1 m² protocol, shown as a short how-to so a volunteer can follow it in the forest
    private void RenderPlotProtocol(UIView s)
    {
        s.Switch(label: T("I counted standardised 1 m² plots", "Laskin vakioituja 1 m² koealoja"), value: _obsMethod.Value == "plot_1m2",
            onValueChange: async v => _obsMethod.Value = v ? "plot_1m2" : "visit");

        if (_obsMethod.Value != "plot_1m2")
        {
            return;
        }

        s.Card([Card.Subtle], content: c =>
        {
            c.Text([Text.BodyStrong], text: T("1 m² plot protocol", "1 m² koealan ohje"));
            c.Text([Text.Small], text: T("Lay a 1 m × 1 m frame (or a string square) on the forest floor at 5 spots, each at least 10 m apart along a straight line. Count ripe and unripe berries inside each frame. Zero is a real answer: record it.",
                "Aseta 1 m × 1 m kehys (tai nyöri) metsänpohjalle 5 kohtaan, vähintään 10 m välein suoralla linjalla. Laske kypsät ja raa'at marjat kunkin kehyksen sisällä. Nolla on oikea vastaus: kirjaa se."));
            c.Row(["gap-3 flex-wrap mt-2"], content: r =>
            {
                r.TextField(["flex-1 min-w-[120px]"], label: T("Plots counted", "Lasketut koealat"), type: "number", bind: _obsPlots);
                r.TextField(["flex-1 min-w-[120px]"], label: T("Total ripe berries", "Kypsiä marjoja yhteensä"), type: "number", bind: _obsRipe);
            });
        });
    }

    private string QualityFlagLabel(string flag) => flag switch
    {
        "low_resolution" => T("low resolution", "matala resoluutio"),
        "very_small_file" => T("tiny file", "pieni tiedosto"),
        _ => flag,
    };

    private void ClearObservationForm()
    {
        _obsOutcome.Value = null;
        _obsMethod.Value = "visit";
        _obsStage.Value = null;
        _obsMinutes.Value = "";
        _obsArea.Value = "";
        _obsLitres.Value = "";
        _obsPlots.Value = "";
        _obsRipe.Value = "";
        _obsNotes.Value = "";
        _obsPoint.Value = null;
        _obsAccuracy.Value = null;
        _obsAreaId.Value = null;
        _obsPhotos.Clear();
        _obsError.Value = null;
        _photoProblem.Value = null;
    }

    private async Task UseGpsForObservationAsync()
    {
        var location = await ClientFunctions.GetLocationAsync();
        if (location is null)
        {
            _obsError.Value = T("Couldn't read your GPS. Allow location access for this site, or pick the spot on the map.", "GPS-sijaintia ei saatu. Salli sijainnin käyttö tälle sivustolle tai valitse paikka kartalta.");
            return;
        }

        _obsError.Value = null;
        _obsPoint.Value = new LatLon(location.Latitude, location.Longitude);
        _obsAccuracy.Value = location.Accuracy;
        _obsLocationSource.Value = "gps";
        _userLocation.Value = new UserLocation(location.Latitude, location.Longitude, location.Accuracy);
    }

    private async Task PickObservationOnMapAsync()
    {
        _obsPicking.Value = true;
        _obsLocationSource.Value = "map_pick";
        await GoAsync("/map");
        _toast.Value = T("Tap the map where you were, then come back to Field", "Napauta karttaa siinä, missä olit, ja palaa Maastotilaan");
    }

    private async Task<FileUploadResult> OnPhotoStartAsync(FileUploadStartArgs args)
    {
        if (CurrentUserId is not { } userId)
        {
            return false;
        }

        _photoProblem.Value = null;
        var uri = new AssetUri(AssetClass.CloudFile, $"observations/{userId}/{Guid.NewGuid():N}{(args.MimeType == "image/png" ? ".png" : ".jpg")}", spaceId: app.GlobalState.SpaceId);
        return new FileUploadResult { Accepted = true, AssetUri = uri };
    }

    // Photo checks are cheap and honest: they flag, never silently reject; a person reviews flagged reports
    private async Task OnPhotoCompleteAsync(FileUploadCompleteArgs args)
    {
        if (args.AssetUri is not { } uri)
        {
            return;
        }

        var flags = new List<string>();
        var (width, height) = (0, 0);
        try
        {
            var bytes = await Asset.Instance.GetBytesAsync(uri);
            (width, height) = ImageUtils.GetImageDimensions(bytes);
            if (Math.Min(width, height) < 640) flags.Add("low_resolution");
            if (bytes.Length < 30_000) flags.Add("very_small_file");
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Photo inspection failed: {ex.Message}");
            await Asset.Instance.DeleteAsync(uri);
            _photoProblem.Value = T("That file didn't look like a photo. Use a JPEG or PNG.", "Tiedosto ei näyttänyt valokuvalta. Käytä JPEG- tai PNG-kuvaa.");
            return;
        }

        _obsPhotos.Add(new PhotoRef(uri.ToString(), args.MimeType, width, height, args.Size, flags));
    }

    private static double? ParseNumber(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    private async Task SubmitObservationAsync()
    {
        _obsError.Value = null;

        if (CurrentUserId is not { } userId)
        {
            return;
        }

        if (_obsPoint.Value is not { } point)
        {
            _obsError.Value = T("Set where you were first: use your GPS or pick the spot on the map.", "Aseta ensin missä olit: käytä GPS:ää tai valitse paikka kartalta.");
            return;
        }

        if (_obsOutcome.Value is not { } outcome)
        {
            _obsError.Value = T("Choose what you found.", "Valitse, mitä löysit.");
            return;
        }

        _obsBusy.Value = true;
        try
        {
            var input = new ObservationInput(_obsSpecies.Value, point.Lat, point.Lon, _obsAccuracy.Value, _obsLocationSource.Value, DateOnly.FromDateTime(DateTime.UtcNow),
                _obsMethod.Value, outcome, _obsStage.Value, (int?)ParseNumber(_obsMinutes.Value), ParseNumber(_obsArea.Value), ParseNumber(_obsLitres.Value),
                (int?)ParseNumber(_obsPlots.Value), (int?)ParseNumber(_obsRipe.Value), null, null, _obsAreaId.Value, _obsShare.Value, _obsTraining.Value,
                _obsNotes.Value.Length == 0 ? null : _obsNotes.Value, [.. _obsPhotos]);

            var result = await _observations.SubmitAsync(userId, input);
            if (result.Id is null)
            {
                _obsError.Value = result.Error;
                return;
            }

            await _queries.TrackAsync(userId, "observation_submitted", new { species = input.Species, outcome, hasPhoto = _obsPhotos.Count > 0 });
            _obsDone.Value = result.Flags.Contains("possible_duplicate")
                ? T("It looks like you already reported this spot today, so a reviewer will check for a double.", "Näyttää siltä, että raportoit tämän paikan jo tänään, joten tarkastaja tarkistaa mahdollisen tuplan.")
                : T("It's saved privately. A reviewer checks reports before they count as evidence.", "Se on tallennettu yksityisesti. Tarkastaja käy havainnot läpi ennen kuin ne lasketaan näytöksi.");
            ClearObservationForm();
        }
        catch (Exception ex)
        {
            Log.Instance.Error($"Observation submit failed: {ex}");
            _obsError.Value = T("Couldn't send the report — everything you entered is kept, try again.", "Havainnon lähetys epäonnistui — kaikki syöttämäsi säilyy, yritä uudelleen.");
        }
        finally
        {
            _obsBusy.Value = false;
        }
    }

    private readonly ClientReactiveList<ObservationRow> _myReports = new();

    private void RenderMyReports(UIView page)
    {
        page.Row(["items-center justify-between"], content: r =>
        {
            r.Text([Text.H4, "font-heading"], text: T("My reports", "Omat havaintoni"));
            r.Button([Button.GhostSm], text: T("Refresh", "Päivitä"), onClick: async () => _myReports.ReplaceAll(await _queries.MyObservationsAsync(CurrentUserId!, 30)));
        });

        if (_myReports.Count == 0)
        {
            page.Text([Text.Small, "text-muted-foreground"], text: T("Press Refresh to load your latest reports.", "Paina Päivitä ladataksesi viimeisimmät havaintosi."));
            return;
        }

        foreach (var o in _myReports)
        {
            page.Card([Card.Subtle, "p-0"], key: $"report-{o.Id}", content: c =>
                c.Row(["items-center gap-3 p-3 flex-wrap"], content: r =>
                {
                    r.Column(["flex-1 min-w-[160px] gap-0.5"], content: t =>
                    {
                        t.Text([Text.BodyStrong], text: $"{SpeciesName(o.Species)} · {OutcomeLabel(o.Outcome)}");
                        t.Text([Text.Caption, "text-muted-foreground"], text: o.ObservedOn.ToString("d", Culture) + " · " + (o.ShareAggregated ? T("shared in summaries", "jaettu tiivistelmissä") : T("private", "yksityinen")));
                    });
                    r.Badge(QcLabel(o.QcStatus), tone: o.QcStatus switch { "accepted" => SemanticTone.Success, "rejected" => SemanticTone.Error, _ => SemanticTone.Neutral }, size: BadgeSize.Sm);
                    r.Button([Button.GhostErrorSm], ariaLabel: T("Delete report", "Poista havainto"), onClick: async () =>
                    {
                        if (await _observations.DeleteOwnAsync(CurrentUserId!, o.Id))
                        {
                            _myReports.ReplaceAll(await _queries.MyObservationsAsync(CurrentUserId!, 30));
                        }
                    }, content: b => b.Icon([Icon.Sm], name: "trash-2"));
                }));
        }
    }

    private string OutcomeLabel(string outcome) => OutcomeChoices.Where(c => c.Value == outcome).Select(c => T(c.En, c.Fi)).FirstOrDefault() ?? outcome;

    private string QcLabel(string status) => status switch
    {
        "accepted" => T("Accepted", "Hyväksytty"),
        "rejected" => T("Not accepted", "Hylätty"),
        "needs_info" => T("Needs info", "Tarvitsee tietoja"),
        _ => T("In review", "Tarkastettavana"),
    };
}
