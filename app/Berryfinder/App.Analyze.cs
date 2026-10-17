using System.Globalization;
using System.Text.Json;

// "Find berries nearby": the user picks a point on the map, chooses a radius, and the trained model analyses that area in the
// inference service. The map shows the result as a heatmap of the scored cells or as pins on candidate areas.
public partial class BerryfinderApp
{
    private static readonly int[] RadiusOptions = [1000, 3000, 5000, 10000];

    private readonly ClientReactive<LatLon?> _pick = new(null);
    private readonly ClientReactive<string?> _pickPlace = new(null);
    private readonly ClientReactive<int> _radius = new(3000);
    private readonly ClientReactive<AnalysisJob?> _job = new(null);
    private readonly ClientReactive<string?> _jobProblem = new(null);
    private readonly ClientReactive<string> _analysisMode = new("heatmap");
    private readonly ClientReactive<string?> _analysisSpecies = new(null);
    private readonly ClientReactive<string?> _selectedPin = new(null);
    private readonly ClientReactive<int> _pollGeneration = new(0);
    private readonly ClientReactive<int> _locateRequest = new(0);
    private readonly ClientReactive<int> _fitRequest = new(0);
    private readonly Reactive<bool> _inferenceOnline = new(false);

    private InferenceClient? _inference;
    private string _analysisUrlTemplate = "";

    private bool AnalysisShown => _job.Value is { Status: "succeeded", Summary: not null };

    private string? AnalysisUrl => AnalysisShown && _analysisUrlTemplate.Length > 0 ? _analysisUrlTemplate.Replace("{jobId}", _job.Value!.JobId) : null;

    private async Task CheckInferenceAsync()
    {
        _inferenceOnline.Value = _inference is not null && await _inference.HealthyAsync();
    }

    // ─── Selecting a place ────────────────────────────────────────────────

    private async Task OnMapClickAsync(string json)
    {
        try
        {
            var v = JsonSerializer.Deserialize<JsonElement>(json);
            await SelectPointAsync(v.GetProperty("lat").GetDouble(), v.GetProperty("lon").GetDouble());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A malformed click is ignored
        }
    }

    private async Task SelectPointAsync(double lat, double lon)
    {
        _pollGeneration.Value++;
        _places.Clear();
        _pick.Value = new LatLon(lat, lon);
        _pickPlace.Value = null;
        _job.Value = null;
        _jobProblem.Value = null;
        _selectedPin.Value = null;
        _selectedAreaId.Value = null;
        _detail.Value = null;
        _stand.Value = null;
        _sheetExpanded.Value = false;
        await _queries.TrackAsync(CurrentUserId, "analysis_point_selected", new { });
        _pickPlace.Value = await ReverseGeocoder.NameAsync(lat, lon);
    }

    private async Task UseMapCentreAsync()
    {
        if (_viewport.Value is { } vp)
        {
            await SelectPointAsync(vp.Lat, vp.Lon);
        }
    }

    private async Task UseMyLocationForAnalysisAsync()
    {
        if (_userLocation.Value is { } u)
        {
            await SelectPointAsync(u.Lat, u.Lon);
            return;
        }

        // Ask the map to locate; OnLocateAsync selects the point when the fix arrives
        _locateRequest.Value++;
    }

    private void ClearAnalysis()
    {
        _pollGeneration.Value++;
        _pick.Value = null;
        _pickPlace.Value = null;
        _job.Value = null;
        _jobProblem.Value = null;
        _selectedPin.Value = null;
    }

    // ─── Running an analysis ──────────────────────────────────────────────

    private async Task AnalyzeAsync()
    {
        if (_pick.Value is not { } pick || _inference is null)
        {
            return;
        }

        _jobProblem.Value = null;
        _selectedPin.Value = null;
        var generation = ++_pollGeneration.Value;
        try
        {
            var job = await _inference.StartAsync(pick.Lat, pick.Lon, _radius.Value, null);
            _job.Value = job;
            _fitRequest.Value++;
            await _queries.TrackAsync(CurrentUserId, "analysis_started", new { radius = _radius.Value });
            var cid = ReactiveScope.ClientId;
            _ = Task.Run(() => FollowJobAsync(cid, job.JobId, generation));
        }
        catch (InferenceRequestException ex)
        {
            _jobProblem.Value = ex.Message;
        }
        catch (InferenceUnavailableException)
        {
            _inferenceOnline.Value = false;
            _jobProblem.Value = T("Berry analysis is offline right now. Try again in a minute.", "Marja-analyysi ei ole käytettävissä juuri nyt. Yritä hetken päästä uudelleen.");
        }
    }

    // Polls the service once a second while this client still wants this job; the stage shown is the data source really running
    private async Task FollowJobAsync(int cid, string jobId, int generation)
    {
        using var scope = ReactiveScope.UseNested(new ClientScope(cid));
        var failures = 0;
        for (var i = 0; i < 900; i++)
        {
            if (_pollGeneration.Value != generation || app.StoppingToken.IsCancellationRequested)
            {
                return;
            }

            AnalysisJob status;
            try
            {
                status = await _inference!.StatusAsync(jobId, app.StoppingToken);
                failures = 0;
            }
            catch (Exception ex) when (ex is InferenceUnavailableException or InferenceRequestException)
            {
                if (++failures >= 5)
                {
                    _jobProblem.Value = T("Lost contact with the berry analysis. Try again.", "Yhteys marja-analyysiin katkesi. Yritä uudelleen.");
                    _job.Value = null;
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
                continue;
            }

            if (_pollGeneration.Value != generation)
            {
                return;
            }

            _job.Value = status;
            if (status.Status == "succeeded" && status.Summary is { } summary)
            {
                _analysisSpecies.Value = DefaultAnalysisSpecies(summary);
                _analysisMode.Value = "heatmap";
                _sheetExpanded.Value = false;
                _fitRequest.Value++;
                await _queries.TrackAsync(CurrentUserId, "analysis_finished", new { pins = summary.Pins.Count });
                return;
            }

            if (status.Status == "failed")
            {
                _jobProblem.Value = status.Error?.Message ?? T("The analysis couldn't finish. Try again.", "Analyysi ei valmistunut. Yritä uudelleen.");
                _job.Value = null;
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private string? DefaultAnalysisSpecies(AnalysisSummary summary)
    {
        // Keep the berry the user chose before; otherwise the one with the most candidate areas
        if (_analysisSpecies.Value is { } current && summary.Species.Any(s => s.Species == current))
        {
            return current;
        }

        return summary.Species.OrderByDescending(s => s.CandidateAreas).ThenByDescending(s => s.AreaIndex).FirstOrDefault()?.Species;
    }

    private async Task SelectPinAsync(string id)
    {
        if (_job.Value?.Summary?.Pins.FirstOrDefault(p => p.Id == id) is not { } pin)
        {
            return;
        }

        _selectedPin.Value = id;
        _analysisSpecies.Value = pin.Species;
        _viewRequest.Value = new MapView(pin.Lat, pin.Lon, Math.Max(_viewport.Value?.Zoom ?? 13, 14), (_viewRequest.Value?.Key ?? 0) + 1);
        await _queries.TrackAsync(CurrentUserId, "analysis_pin_opened", new { species = pin.Species });
    }

    private async Task SavePinAsync(AnalysisPin pin)
    {
        if (CurrentUserId is not { } userId)
        {
            await ClientFunctions.LoginShowAsync(T("Sign in to save places", "Kirjaudu tallentaaksesi paikkoja"));
            return;
        }

        var label = string.Format(Culture, T("{0} · {1} · {2}", "{0} · {1} · {2}"), SpeciesName(pin.Species), LocalBandLabel(pin.Band), Habitat(pin));
        await _queries.AddFavoriteAsync(userId, label, pin.Species, null, pin.Lat, pin.Lon, $"Analysis {_job.Value?.Summary?.ModelVersion}, local rank {pin.LocalRank:0}");
        await LoadSavedAsync();
        _toast.Value = T("Saved to your places", "Tallennettu paikkoihisi");
    }

    private async Task SaveAnalysisAsync()
    {
        if (_pick.Value is not { } pick)
        {
            return;
        }

        if (CurrentUserId is not { } userId)
        {
            await ClientFunctions.LoginShowAsync(T("Sign in to save places", "Kirjaudu tallentaaksesi paikkoja"));
            return;
        }

        var label = string.Format(Culture, T("Analysis near {0} · {1:0} km", "Analyysi lähellä: {0} · {1:0} km"), _pickPlace.Value ?? T("selected place", "valittu paikka"), _radius.Value / 1000.0);
        await _queries.AddFavoriteAsync(userId, label, _analysisSpecies.Value, null, pick.Lat, pick.Lon, null);
        await LoadSavedAsync();
        _toast.Value = T("Saved to your places", "Tallennettu paikkoihisi");
    }

    // ─── Words ────────────────────────────────────────────────────────────

    private string LocalBandLabel(string band) => band switch
    {
        "very_high" => T("Best in this area", "Alueen parhaita"),
        "high" => T("Among the best here", "Täällä parhaiden joukossa"),
        "moderate" => T("Average for this area", "Alueen keskitasoa"),
        _ => T("Below average here", "Alueen keskitason alla"),
    };

    private string AreaBandLabel(string band) => band switch
    {
        "very_high" => T("Very high", "Erittäin hyvä"),
        "high" => T("High", "Hyvä"),
        "moderate" => T("Moderate", "Kohtalainen"),
        _ => T("Low", "Heikko"),
    };

    private string Habitat(AnalysisPin pin) => _language.Value == "fi" ? pin.HabitatFi : pin.HabitatEn;

    private string StageText(AnalysisJob job) => job.Status == "queued"
        ? T("Waiting for the analyser", "Odotetaan analyysia")
        : job.Progress.Stage switch
        {
            "finnish_forest" => T("Analyzing forest conditions", "Analysoidaan metsää"),
            "landcover" => T("Processing satellite land cover", "Käsitellään satelliittimaanpeitettä"),
            "soil" => T("Reading soil maps", "Luetaan maaperäkarttoja"),
            "climate" or "climate_chelsa" => T("Reading climate normals", "Luetaan ilmastotietoja"),
            "elevation" or "terrain" => T("Reading terrain", "Luetaan maastoa"),
            "inference" => T("Running berry predictions", "Lasketaan marjaennusteita"),
            "postprocess" => T("Preparing your map", "Valmistellaan karttaa"),
            _ => T("Starting", "Aloitetaan"),
        };

    private static string Km(double metres, CultureInfo culture) => metres < 1000
        ? string.Format(culture, "{0:0} m", Math.Round(metres / 10) * 10)
        : string.Format(culture, "{0:0.0} km", metres / 1000);

    // ─── Sheet ────────────────────────────────────────────────────────────

    private void RenderAnalysisIntro(UIView view)
    {
        view.Column([Design.Card, "p-5 gap-3", Design.Appear], content: c =>
        {
            c.Row(["items-center gap-3"], content: r =>
            {
                r.Box(["w-11 h-11 rounded-full bg-brand-solid/10 flex items-center justify-center shrink-0"], content: b => b.Icon([Icon.Md, "text-brand-secondary"], name: "sparkles"));
                r.Column(["gap-0.5 min-w-0"], content: t =>
                {
                    t.Text([Design.Headline], text: T("Find berries nearby", "Löydä marjoja läheltä"));
                    t.Text([Design.Subhead], text: T("Tap any forest on the map and our model analyses the area around it.", "Napauta mitä tahansa metsää kartalla, niin mallimme analysoi ympäristön."));
                });
            });
            c.Row(["gap-2 flex-wrap"], content: r =>
            {
                r.Button([Button.OutlineMd, "rounded-full h-11 gap-2", Design.Press], onClick: UseMyLocationForAnalysisAsync, content: b =>
                {
                    b.Icon([Icon.Sm], name: "locate-fixed");
                    b.Text(text: T("Use my location", "Käytä sijaintiani"));
                });
                r.Button([Button.OutlineMd, "rounded-full h-11 gap-2", Design.Press], onClick: UseMapCentreAsync, disabled: _viewport.Value is null, content: b =>
                {
                    b.Icon([Icon.Sm], name: "crosshair");
                    b.Text(text: T("Use map centre", "Käytä kartan keskustaa"));
                });
            });
        });
    }

    private void RenderAnalysisSheet(UIView view)
    {
        var pick = _pick.Value!;
        var job = _job.Value;

        view.Row(["items-start gap-3", Design.Appear], content: head =>
        {
            head.Box(["w-11 h-11 rounded-full bg-brand-solid flex items-center justify-center shrink-0 shadow-sm"], content: b => b.Icon([Icon.Md, "text-white"], name: "map-pin"));
            head.Column(["flex-1 min-w-0 gap-0.5"], content: t =>
            {
                t.Text([Design.Headline, "truncate"], text: _pickPlace.Value ?? T("Selected place", "Valittu paikka"));
                t.Text([Design.Caption], text: string.Format(CultureInfo.InvariantCulture, "{0:0.0000}° N, {1:0.0000}° E", pick.Lat, pick.Lon));
            });
            head.Button(["w-10 h-10 rounded-full bg-muted border-0 flex items-center justify-center shrink-0", Design.Press], ariaLabel: T("Close", "Sulje"),
                onClick: async () => ClearAnalysis(), content: b => b.Icon([Icon.Sm], name: "x"));
        });

        if (job is { Status: "succeeded", Summary: { } summary })
        {
            RenderAnalysisResult(view, summary);
            return;
        }

        if (job is { Status: "queued" or "running" })
        {
            RenderAnalysisProgress(view, job);
            return;
        }

        RenderAnalysisSetup(view);
    }

    private void RenderRadiusSelector(UIView view)
    {
        view.ToggleGroupSingle(["grid grid-cols-4 gap-1 bg-muted rounded-full p-1"], value: _radius.Value.ToString(CultureInfo.InvariantCulture), ariaLabel: T("Search radius", "Hakusäde"),
            onValueChange: async v =>
            {
                if (int.TryParse(v, out var r) && RadiusOptions.Contains(r))
                {
                    _radius.Value = r;
                }
            },
            content: g =>
            {
                foreach (var r in RadiusOptions)
                {
                    g.ToggleGroupItem(["h-10 rounded-full text-[15px] font-medium text-muted-foreground data-[state=on]:bg-card data-[state=on]:text-foreground data-[state=on]:shadow-sm"],
                        value: r.ToString(CultureInfo.InvariantCulture), content: i => i.Text(text: $"{r / 1000} km"));
                }
            });
    }

    private void RenderAnalysisSetup(UIView view)
    {
        view.Column(["gap-3"], content: c =>
        {
            c.Text([Design.Title], text: T("Find berries nearby", "Löydä marjoja läheltä"));
            c.Text([Design.Overline, "text-muted-foreground"], text: T("Search radius", "Hakusäde"));
            RenderRadiusSelector(c);
            var area = Math.PI * Math.Pow(_radius.Value / 1000.0, 2);
            c.Text([Design.Caption], text: string.Format(Culture, T("{0:0.#} km² · the model scores every square of the area. Usually under a minute; areas analysed before are instant.",
                "{0:0.#} km² · malli pisteyttää alueen jokaisen ruudun. Yleensä alle minuutti; aiemmin analysoidut alueet heti."), area));

            if (_jobProblem.Value is { } problem)
            {
                c.Alert(problem, tone: SemanticTone.Error);
            }

            c.Button([Button.PrimaryLg, "w-full h-14 rounded-full text-[17px] font-semibold gap-2", Design.Press], onClick: AnalyzeAsync, disabled: !_inferenceOnline.Value,
                content: b =>
                {
                    b.Icon([Icon.Md], name: "sparkles");
                    b.Text(text: _jobProblem.Value is null ? T("Analyze area", "Analysoi alue") : T("Try again", "Yritä uudelleen"));
                });

            if (!_inferenceOnline.Value)
            {
                c.Text([Design.Caption, "text-center"], text: T("Berry analysis is offline right now, so the button is disabled. Try again in a minute.",
                    "Marja-analyysi ei ole juuri nyt käytettävissä, joten painike on pois käytöstä. Yritä hetken päästä."));
            }
        });
    }

    private void RenderAnalysisProgress(UIView view, AnalysisJob job)
    {
        view.Column([Design.Card, "p-5 gap-4"], content: c =>
        {
            c.Row(["items-center gap-3"], content: r =>
            {
                r.Box([Icon.Spinner, "w-5 h-5 text-brand-secondary"]);
                r.Column(["gap-0.5"], content: t =>
                {
                    t.Text([Design.Headline], key: job.Progress.Stage ?? "start", text: StageText(job) + "…");
                    t.Text([Design.Caption], text: job.Progress.CompletedStages > 0
                        ? string.Format(Culture, job.Progress.CompletedStages == 1 ? T("{0} data source done", "{0} tietolähde valmiina") : T("{0} data sources done", "{0} tietolähdettä valmiina"), job.Progress.CompletedStages)
                        : T("Collecting the data the model needs", "Kerätään mallin tarvitsemat tiedot"));
                });
            });
            c.Box([Skeleton.Text]);
            c.Box([Skeleton.Text, "w-2/3"]);
            c.Text([Design.Caption], text: T("You can keep moving the map while this runs.", "Voit liikutella karttaa sillä aikaa."));
        });
    }

    private void RenderAnalysisResult(UIView view, AnalysisSummary summary)
    {
        var species = _analysisSpecies.Value is { } chosen && summary.Species.Any(s => s.Species == chosen) ? chosen : summary.Species.FirstOrDefault()?.Species;
        var info = summary.Species.FirstOrDefault(s => s.Species == species);
        var pins = summary.Pins.Where(p => p.Species == species).OrderBy(p => p.Rank).ToList();
        var selected = summary.Pins.FirstOrDefault(p => p.Id == _selectedPin.Value);

        if (selected is not null)
        {
            RenderPinCard(view, selected, summary);
        }

        // Heatmap or pins
        view.ToggleGroupSingle(["grid grid-cols-2 gap-1 bg-muted rounded-full p-1"], value: _analysisMode.Value, ariaLabel: T("Show results as", "Näytä tulokset"),
            onValueChange: async v => { if (v is "heatmap" or "pins") _analysisMode.Value = v; },
            content: g =>
            {
                foreach (var (id, icon, en, fi) in new[] { ("heatmap", "layers", "Heatmap", "Lämpökartta"), ("pins", "map-pin", "Pins", "Nastat") })
                {
                    g.ToggleGroupItem(["h-10 rounded-full text-[15px] font-medium text-muted-foreground data-[state=on]:bg-card data-[state=on]:text-foreground data-[state=on]:shadow-sm"], value: id,
                        content: i => i.Row(["items-center justify-center gap-2"], content: r => { r.Icon([Icon.Sm], name: icon); r.Text(text: T(en, fi)); }));
                }
            });

        // Which berry
        view.ToggleGroupSingle(["flex gap-2 overflow-x-auto -mx-1 px-1 pb-1"], value: species ?? "", ariaLabel: T("Berry", "Marja"),
            onValueChange: async v => { if (v.Length > 0) { _analysisSpecies.Value = v; _selectedPin.Value = null; } },
            content: g =>
            {
                foreach (var s in summary.Species)
                {
                    var color = BerryCatalog.TryGet(s.Species, out var b) ? b.Color : "#1E7B4F";
                    g.ToggleGroupItem(["h-10 shrink-0 px-4 rounded-full border border-secondary text-[15px] font-medium whitespace-nowrap data-[state=on]:border-transparent data-[state=on]:text-white", $"data-[state=on]:bg-[{color}]"],
                        value: s.Species, content: i => i.Text(text: SpeciesName(s.Species)));
                }
            });

        if (info is not null)
        {
            view.Column([Design.Card, "p-5 gap-2"], content: c =>
            {
                c.Text([Design.Overline, "text-muted-foreground"], text: string.Format(Culture, T("{0} in this area", "{0} tällä alueella"), SpeciesName(info.Species)));
                c.Text([Design.Title], text: AreaBandLabel(info.AreaBand));
                c.Text([Design.Subhead], text: string.Format(Culture,
                    T("As a whole, this area scores higher than {0:0}% of the Nordic places the model learned from.", "Kokonaisuutena alue saa paremmat pisteet kuin {0:0} % mallin pohjoismaisista vertailupaikoista."),
                    Math.Min(99, Math.Floor(info.AreaIndex))));
                c.Text([Design.Caption], text: info.LocalDifferences switch
                {
                    "clear" => T("Inside the area the model sees clear differences: the brightest squares and the pins are the better ground.",
                        "Alueen sisällä malli näkee selviä eroja: kirkkaimmat ruudut ja nastat ovat parempaa maastoa."),
                    "weak" => T("Inside the area the differences are small next to the model's own uncertainty, so treat the ranking as a rough guide.",
                        "Alueen sisäiset erot ovat pieniä mallin omaan epävarmuuteen nähden, joten pidä järjestystä suuntaa-antavana."),
                    _ => T("The model hardly tells places inside this area apart, so any spot here is about as good as another.",
                        "Malli tuskin erottaa alueen paikkoja toisistaan, joten paikat ovat suunnilleen yhtä hyviä."),
                });
            });
        }

        view.Text([Design.Headline, "mt-1"], text: T("Best places", "Parhaat paikat"));
        if (pins.Count == 0)
        {
            view.EmptyState(T("No stand-out places here", "Ei erottuvia paikkoja"), icon: "search-x",
                description: info is { AreaBand: "low" }
                    ? T("This area doesn't suit this berry well. Try another berry or another forest.", "Alue ei sovi tälle marjalle hyvin. Kokeile toista marjaa tai metsää.")
                    : T("None of the better squares are on this berry's kind of ground. Try a larger radius.", "Paremmat ruudut eivät ole tämän marjan kasvupaikoilla. Kokeile suurempaa sädettä."),
                action: a => a.Button([Button.OutlineSm], text: T("Change radius", "Vaihda säde"), onClick: async () => { _job.Value = null; }));
        }
        else
        {
            view.Column(["gap-2"], content: list =>
            {
                foreach (var p in pins)
                {
                    var color = BerryCatalog.TryGet(p.Species, out var b) ? b.Color : "#1E7B4F";
                    list.Button([$"w-full flex items-center gap-3 p-3 rounded-2xl text-left border-0 {(p.Id == _selectedPin.Value ? "bg-brand-solid/10" : "bg-muted")}", Design.Press],
                        key: p.Id, ariaLabel: $"{p.Rank}. {Habitat(p)}, {Km(p.DistanceM, Culture)}", onClick: async () => await SelectPinAsync(p.Id), content: row =>
                        {
                            row.Box([$"w-9 h-9 rounded-full bg-[{color}] text-white flex items-center justify-center text-[15px] font-semibold shrink-0"], content: n => n.Text(text: p.Rank.ToString(Culture)));
                            row.Column(["flex-1 min-w-0 gap-0.5"], content: t =>
                            {
                                t.Text([Design.Headline, "truncate"], text: Habitat(p));
                                t.Text([Design.Caption], text: string.Format(Culture, "{0} · {1} · {2:0.#} ha", LocalBandLabel(p.Band), Km(p.DistanceM, Culture), p.AreaHa));
                            });
                            row.Icon([Icon.Sm, "text-muted-foreground"], name: "chevron-right");
                        });
                }
            });
        }

        view.Column(["gap-2 mt-1"], content: actions =>
        {
            actions.Row(["gap-2"], content: r =>
            {
                r.Button([Button.OutlineMd, "flex-1 rounded-full h-11 gap-2", Design.Press], onClick: async () => { _job.Value = null; }, content: b =>
                {
                    b.Icon([Icon.Sm], name: "circle-dashed");
                    b.Text(text: T("Change radius", "Vaihda säde"));
                });
                r.Button([Button.OutlineMd, "flex-1 rounded-full h-11 gap-2", Design.Press], onClick: SaveAnalysisAsync, content: b =>
                {
                    b.Icon([Icon.Sm], name: "bookmark-plus");
                    b.Text(text: T("Save results", "Tallenna"));
                });
            });
            actions.Button([Button.GhostMd, "rounded-full h-11 gap-2", Design.Press], onClick: AnalyzeAsync, content: b =>
            {
                b.Icon([Icon.Sm], name: "refresh-cw");
                b.Text(text: T("Run prediction again", "Aja ennuste uudelleen"));
            });
        });

        view.Column([Design.Sheet, "p-4 gap-1.5"], content: c =>
        {
            c.Text([Design.Overline, "text-muted-foreground"], text: T("About this result", "Tästä tuloksesta"));
            c.Text([Design.Caption], text: string.Format(Culture,
                T("{0} squares of {1} m scored by the model {2} on {3:0.#} km². It predicts where the habitat suits a berry, not whether berries are ripe now, how many there are, or whether you may pick there.",
                  "Malli {2} pisteytti {0} ruutua ({1} m) {3:0.#} km² alueelta. Se ennustaa, missä kasvupaikka sopii marjalle, ei sitä, ovatko marjat nyt kypsiä, kuinka paljon niitä on tai saako alueella poimia."),
                summary.LandCells, summary.StepM, summary.ModelVersion, summary.AreaKm2));
            c.Text([Design.Caption], text: T("Data: NASA POWER climate normals (about 55 km), ISRIC SoilGrids 2.0 (250 m), ESA WorldCover 2021 from Sentinel satellites (10 m), Copernicus elevation and Finnish Forest Centre stand data.",
                "Aineistot: NASA POWER -ilmastokeskiarvot (noin 55 km), ISRIC SoilGrids 2.0 (250 m), ESA WorldCover 2021 Sentinel-satelliiteista (10 m), Copernicus-korkeusmalli ja metsäkeskuksen kuviotiedot."));
        });
    }

    private void RenderPinCard(UIView view, AnalysisPin pin, AnalysisSummary summary)
    {
        var berry = BerryCatalog.TryGet(pin.Species, out var b) ? b : null;
        view.Column([Design.Card, "overflow-hidden", Design.Appear], key: $"pin-{pin.Id}", content: card =>
        {
            card.Row([$"items-center gap-3 p-4 bg-[{berry?.Color ?? "#1E7B4F"}]"], content: top =>
            {
                if (berry is not null)
                {
                    top.Image(["w-14 h-14 shrink-0"], src: berry.Art, alt: "");
                }

                top.Column(["flex-1 min-w-0 gap-0.5"], content: t =>
                {
                    t.Text(["text-[12px] font-semibold uppercase tracking-[0.08em] text-white/75"], text: LocalBandLabel(pin.Band));
                    // "Promising" only where the model really tells this place apart from the rest of the area
                    var distinct = summary.Species.FirstOrDefault(s => s.Species == pin.Species)?.LocalDifferences is "clear" or "weak";
                    var name = _language.Value == "fi" ? (berry?.NameFi.ToLowerInvariant() ?? pin.Species) : (berry?.NameEn.ToLowerInvariant() ?? pin.Species);
                    t.Text(["text-[20px] leading-tight font-bold text-white"], text: distinct
                        ? string.Format(Culture, T("Promising {0} area", "Lupaava {0}alue"), name)
                        : string.Format(Culture, T("{0} habitat", "Kasvupaikka: {0}"), Design.Capitalise(name)));
                });
                top.Button(["w-9 h-9 rounded-full bg-white/20 border-0 flex items-center justify-center shrink-0", Design.Press], ariaLabel: T("Close", "Sulje"),
                    onClick: async () => _selectedPin.Value = null, content: x => x.Icon([Icon.Sm, "text-white"], name: "x"));
            });
            card.Column(["p-4 gap-1"], content: c =>
            {
                Fact(c, T("Suitability here", "Soveltuvuus täällä"), string.Format(Culture, T("Better than {0:0}% of this area", "Parempi kuin {0:0} % alueesta"), Math.Min(99, Math.Floor(pin.LocalRank))));
                Fact(c, T("Berry", "Marja"), SpeciesName(pin.Species));
                Fact(c, T("Habitat", "Kasvupaikka"), Habitat(pin));
                Fact(c, T("Distance", "Etäisyys"), string.Format(Culture, T("{0} from the selected point", "{0} valitusta pisteestä"), Km(pin.DistanceM, Culture)));
                Fact(c, T("Size", "Koko"), string.Format(Culture, "{0:0.#} ha", pin.AreaHa));
                Fact(c, T("Model agreement", "Mallin yksimielisyys"), pin.Uncertainty <= 0.6 ? T("Good", "Hyvä") : pin.Uncertainty <= 0.9 ? T("Fair", "Kohtalainen") : T("Low", "Heikko"));
                c.Text([Design.Caption, "mt-1"], text: string.Format(Culture, T("Location accuracy: squares of {0} m, from map data. Check access and protected-area rules on site.",
                    "Sijainnin tarkkuus: {0} metrin ruudut karttatiedoista. Tarkista kulku- ja suojelusäännöt paikan päällä."), summary.StepM));
            });
            card.Row(["gap-2 px-4 pb-4"], content: r =>
            {
                r.Link([Button.PrimaryMd, "flex-1 rounded-full h-11 gap-2 no-underline", Design.Press], href: Router.NavigationUrl(pin.Lat, pin.Lon), target: "_blank", rel: "noopener",
                    onClick: async () => await _queries.TrackAsync(CurrentUserId, "analysis_directions", new { species = pin.Species }), content: l =>
                    {
                        l.Icon([Icon.Sm], name: "navigation");
                        l.Text(text: T("Get directions", "Reittiohjeet"));
                    });
                r.Button([Button.OutlineMd, "flex-1 rounded-full h-11 gap-2", Design.Press], onClick: async () => await SavePinAsync(pin), content: x =>
                {
                    x.Icon([Icon.Sm], name: "bookmark-plus");
                    x.Text(text: T("Save location", "Tallenna paikka"));
                });
            });
        });
    }
}
