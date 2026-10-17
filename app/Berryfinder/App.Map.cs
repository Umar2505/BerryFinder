using System.Globalization;
using System.Text.Json;

public sealed record Viewport(double West, double South, double East, double North, double Lat, double Lon, double Zoom);

public sealed record SeasonInfo(Phenology.Result Estimate, string Station, double StationKm);

public sealed record StandInfo(long StandId, string Suitability, string Evidence, string Drivers, short? Fertility, string? Development, float? MeanAge,
    float? BasalArea, string? SoilClass, DateOnly? Measured, float? DistRoadM, string? Restriction, double Lat, double Lon);

// The main product screen: a full-bleed map with search, species choice and filters, a ranked list that
// follows the map, and the area detail. Desktop shows a side panel; a phone shows a bottom sheet.
public partial class BerryfinderApp
{
    private readonly ClientReactive<string> _species = new(Species.Bilberry);
    private readonly ClientReactive<MapView?> _viewRequest = new(new MapView(64.985, 25.62, 10.6, 0));
    private readonly ClientReactive<Viewport?> _viewport = new(null);
    private readonly ClientReactive<string> _searchText = new("");
    private readonly ClientReactiveList<Place> _places = new();
    private readonly ClientReactive<bool> _placesBusy = new(false);
    private readonly ClientReactiveList<AreaSummary> _areas = new();
    private readonly ClientReactive<bool> _areasLoading = new(false);
    private readonly ClientReactive<long?> _selectedAreaId = new(null);
    private readonly ClientReactive<AreaDetail?> _detail = new(null);
    private readonly ClientReactive<bool> _detailLoading = new(false);
    private readonly ClientReactive<SeasonInfo?> _season = new(null);
    private readonly ClientReactive<DriveEstimate?> _drive = new(null);
    private readonly ClientReactive<string?> _driveState = new(null);
    private readonly ClientReactive<StandInfo?> _stand = new(null);
    private readonly ClientReactive<UserLocation?> _userLocation = new(null);
    private readonly ClientReactive<bool> _highOnly = new(false);
    private readonly ClientReactive<string> _maxWalk = new("any");
    private readonly ClientReactive<bool> _hideRestricted = new(true);
    private readonly ClientReactive<string> _basemap = new("map");
    private readonly ClientReactive<bool> _showForest = new(false);
    private readonly ClientReactive<bool> _showSoil = new(false);
    private readonly ClientReactive<bool> _sheetExpanded = new(false);
    private readonly ClientReactive<bool> _filtersOpen = new(false);
    private readonly ClientReactive<string?> _mapProblem = new(null);

    private const string Attribution = "Forest data © Suomen metsäkeskus (CC BY 4.0) · Soil © GTK (CC BY 4.0) · Weather © FMI (CC BY 4.0) · Protected areas © Syke · Contains modified Copernicus Sentinel data · © OpenStreetMap contributors";

    private PlanLimits CurrentPlan => _isAdmin.Value || Plans.PilotOpenAccess ? Plans.Professional : Plans.For(PlanIdNow());

    private void RenderMapPage(UIView view)
    {
        var analysing = _pick.Value is not null;
        view.Box(["relative flex-1 min-h-0"], content: page =>
        {
            page.Box(["absolute inset-0"], content: mapBox =>
            {
                mapBox.BerryMap(
                    tileUrl: _tileUrl,
                    species: _species.Value,
                    dataVersion: _dataVersion.Value,
                    attribution: Attribution,
                    locateLabel: T("Show my location", "Näytä sijaintini"),
                    viewRequest: _viewRequest.Value,
                    basemap: _basemap.Value,
                    satelliteUrl: _satelliteUrl,
                    showForest: _showForest.Value,
                    showSoil: _showSoil.Value,
                    selectedAreaId: _selectedAreaId.Value,
                    rankedAreas: analysing ? [] : [.. _areas.Take(CurrentPlan.RecommendationLimit).Select((a, i) => new RankedPin(a.Id, a.Lat, a.Lon, i + 1))],
                    userLocation: _userLocation.Value,
                    pickMode: _obsPicking.Value,
                    pickedPoint: _obsPoint.Value,
                    onPick: OnPickedAsync,
                    coverage: _coverageGeoJson,
                    color: BerryCatalog.TryGet(_species.Value, out var mapBerry) ? mapBerry.Color : null,
                    analysis: new MapAnalysis(_pick.Value, _radius.Value, AnalysisUrl, _analysisMode.Value, _analysisSpecies.Value,
                        _analysisSpecies.Value is { } a && BerryCatalog.TryGet(a, out var ab) ? ab.Color : null, _selectedPin.Value,
                        _job.Value is { Status: "queued" or "running" }, _fitRequest.Value),
                    locateRequest: _locateRequest.Value,
                    onMapClick: OnMapClickAsync,
                    onSelectPin: SelectPinAsync,
                    onViewChange: OnViewChangeAsync,
                    onSelectArea: async id => { if (long.TryParse(id, out var areaId)) await SelectAreaAsync(areaId, fly: false); },
                    onSelectStand: async id => { if (long.TryParse(id, out var standId)) await SelectStandAsync(standId); },
                    onLocate: OnLocateAsync,
                    style: ["absolute inset-0"],
                    key: "main-map");

                RenderMapOverlayControls(mapBox);
            });

            // Desktop: a floating glass panel; the map stays visible and usable around it
            page.Column(["hidden md:flex absolute left-4 top-4 bottom-4 w-[400px] z-10 flex-col min-h-0 overflow-hidden rounded-[28px] border border-secondary/60",
                "bg-card/90 backdrop-blur-xl shadow-[0_12px_40px_rgba(0,0,0,0.14)]"], content: panel => RenderMapPanel(panel, mobile: false));

            // Phone: a bottom sheet over the map, taller while there is something to read
            page.Column(["md:hidden absolute left-0 right-0 bottom-0 z-10 flex flex-col min-h-0 rounded-t-[28px] border-t border-secondary/60",
                "bg-card/95 backdrop-blur-xl shadow-[0_-10px_30px_rgba(0,0,0,0.12)] pb-[env(safe-area-inset-bottom)] transition-[height] duration-300 ease-out",
                _sheetExpanded.Value || _selectedAreaId.Value is not null || _stand.Value is not null ? "h-[70vh]" : _pick.Value is not null ? "h-[46vh]" : "h-[34vh]"], content: sheet =>
            {
                sheet.Button(["self-center mt-2 mb-1 w-16 h-6 flex items-center justify-center bg-transparent border-0 shrink-0"],
                    ariaLabel: _sheetExpanded.Value ? T("Collapse panel", "Pienennä paneeli") : T("Expand panel", "Suurenna paneeli"),
                    onClick: async () => _sheetExpanded.Value = !_sheetExpanded.Value,
                    content: h => h.Box(["w-10 h-1.5 rounded-full bg-muted-foreground/40"]));
                RenderMapPanel(sheet, mobile: true);
            });
        });
    }

    private void RenderMapOverlayControls(UIView view)
    {
        // Search and species float over the map on phones; on desktop they live in the side panel
        view.Column(["absolute top-3 left-3 right-16 md:hidden gap-2 z-20"], content: top =>
        {
            RenderSearchBox(top);
            if (_pick.Value is null)
            {
                RenderSpeciesToggle(top, compact: true);
            }
        });

        view.Column(["absolute bottom-6 md:bottom-4 right-3 gap-2 z-10 items-end"], content: col =>
        {
            col.DropdownMenu(open: _filtersOpen.Value, onOpenChange: async o => _filtersOpen.Value = o, contentStyle: [Menu.Content, "w-72 p-3"],
                trigger: t => t.Button([Button.SecondarySm, "shadow-md gap-2"], content: b =>
                {
                    b.Icon([Icon.Sm], name: "layers");
                    b.Text(text: T("Layers", "Tasot"));
                }),
                content: menu => RenderLayerMenu(menu));
        });
    }

    private void RenderLayerMenu(UIView view)
    {
        view.Column(["gap-3"], content: col =>
        {
            col.Text([Text.Label], text: T("Base map", "Taustakartta"));
            col.ToggleGroupSingle(["grid grid-cols-2 gap-1"], value: _basemap.Value, onValueChange: async v => { if (v.Length > 0) _basemap.Value = v; }, content: g =>
            {
                g.ToggleGroupItem(["default"], value: "map", content: i => i.Text(text: T("Map", "Kartta")));
                g.ToggleGroupItem(["default"], value: "satellite", disabled: _satelliteUrl is null, content: i => i.Text(text: T("Aerial", "Ilmakuva")));
            });

            if (_satelliteUrl is null)
            {
                col.Text([Text.Caption, "text-muted-foreground"], text: T("Aerial imagery needs a National Land Survey API key (not configured yet).",
                    "Ilmakuva vaatii Maanmittauslaitoksen API-avaimen (ei vielä määritetty)."));
            }

            col.Separator();
            col.Switch(label: T("Forest stands (Metsäkeskus)", "Metsikkökuviot (Metsäkeskus)"), value: _showForest.Value, onValueChange: async v => _showForest.Value = v);
            col.Text([Text.Caption, "text-muted-foreground -mt-2"], text: T("Visible when zoomed in", "Näkyy lähelle zoomattaessa"));
            col.Switch(label: T("Soil map (GTK 1:200 000)", "Maaperäkartta (GTK 1:200 000)"), value: _showSoil.Value, onValueChange: async v => _showSoil.Value = v);
            col.Separator();
            RenderLegend(col);
        });
    }

    private void RenderLegend(UIView view)
    {
        view.Text([Text.Label], text: T("Habitat suitability", "Kasvupaikan soveltuvuus"));
        var colors = _species.Value switch
        {
            Species.Lingonberry => new[] { "#a3172f", "#d9566c", "#f0b7c0", "#b9b4a8" },
            Species.Bilberry => new[] { "#3f2a86", "#7b6cc0", "#c9c1e6", "#b9b4a8" },
            var other => BerryCatalog.TryGet(other, out var b)
                ? new[] { b.Color, Design.MixWithWhite(b.Color, 0.4), Design.MixWithWhite(b.Color, 0.7), "#b9b4a8" }
                : new[] { "#3f2a86", "#7b6cc0", "#c9c1e6", "#b9b4a8" },
        };
        var gridModel = _publisher.ProductionKinds.GetValueOrDefault(_species.Value) == ModelGate.Kind;
        var labels = gridModel
            ? new[] { Guide.OddsLabel(Odds.WorthATry, _language.Value), Guide.OddsLabel(Odds.LongShot, _language.Value) }
            : new[] { SuitabilityLabel("high"), SuitabilityLabel("moderate"), SuitabilityLabel("low"), SuitabilityLabel("unlikely") };

        view.Column(["gap-1.5"], content: legend =>
        {
            for (var i = 0; i < labels.Length; i++)
            {
                var color = colors[i];
                legend.Row(["items-center gap-2"], content: row =>
                {
                    row.Box([$"w-4 h-4 rounded-sm bg-[{color}]"]);
                    row.Text([Text.Small], text: labels[i]);
                });
            }
        });
        view.Text([Text.Caption, "text-muted-foreground"], text: gridModel
            ? T("Machine-learning estimate from climate, soil and satellite land cover on a 250 m grid, checked only against public sightings. Dashed line: pilot coverage.",
                "Koneoppimisarvio ilmastosta, maaperästä ja satelliittimaanpeitteestä 250 m ruudukossa, tarkistettu vain julkisia havaintoja vasten. Katkoviiva: pilottialue.")
            : T("Unvalidated ecological estimate from forest and soil data. Dashed line: pilot coverage.",
                "Validoimaton ekologinen arvio metsä- ja maaperätiedoista. Katkoviiva: pilottialue."));
    }

    private void RenderSearchBox(UIView view)
    {
        view.Column(["gap-1 relative"], content: col =>
        {
            col.Row(["items-center gap-2 bg-card rounded-xl shadow-md border border-secondary px-3"], content: row =>
            {
                row.Icon([Icon.Sm, "text-muted-foreground"], name: "search");
                row.TextField(["flex-1 border-0 bg-transparent shadow-none h-11 px-0 focus-visible:ring-0"], value: _searchText.Value,
                    placeholder: T("Search a place, e.g. Oulu", "Hae paikkaa, esim. Oulu"), ariaLabel: T("Search a place", "Hae paikkaa"),
                    onValueChange: async v => _searchText.Value = v, onSubmit: async v => await SearchPlacesAsync(v));
                if (_placesBusy.Value)
                {
                    row.Box([Icon.Spinner, "w-4 h-4"]);
                }
            });

            if (_places.Count > 0)
            {
                col.Column([Card.Elevated, "absolute top-12 left-0 right-0 z-20 p-1 gap-0"], content: list =>
                {
                    foreach (var place in _places)
                    {
                        list.Button([Menu.Item, "flex-col items-start h-auto py-2"], key: $"{place.Lat}:{place.Lon}", onClick: async () => await GoToPlaceAsync(place), content: b =>
                        {
                            b.Text([Text.BodyStrong], text: place.Name);
                            b.Text([Text.Caption, "text-muted-foreground line-clamp-1"], text: place.Detail);
                        });
                    }

                    list.Button([Button.GhostSm, "self-end"], text: T("Close", "Sulje"), onClick: async () => _places.Clear());
                });
            }
        });
    }

    private void RenderSpeciesToggle(UIView view, bool compact)
    {
        // Every berry with spots, in a row that scrolls sideways when it does not fit
        view.ToggleGroupSingle([compact ? "flex gap-1 overflow-x-auto bg-card/95 rounded-xl p-1 shadow-md" : "flex gap-1 overflow-x-auto bg-muted rounded-xl p-1"],
            value: _species.Value, ariaLabel: T("Berry", "Marja"),
            onValueChange: async v =>
            {
                if (HasSpots(v) && v != _species.Value)
                {
                    _species.Value = v;
                    _selectedAreaId.Value = null;
                    _detail.Value = null;
                    _stand.Value = null;
                    await RefreshAreasAsync();
                    await _queries.TrackAsync(CurrentUserId, "species_selected", new { species = v });
                }
            },
            content: g =>
            {
                foreach (var sp in MappedSpecies)
                {
                    var dot = BerryCatalog.TryGet(sp, out var berry) ? berry.Color : "#3f2a86";
                    g.ToggleGroupItem(["h-10 shrink-0 grow px-3 rounded-lg text-sm font-medium whitespace-nowrap data-[state=on]:bg-card data-[state=on]:shadow-sm data-[state=on]:text-foreground text-muted-foreground"], value: sp, content: i =>
                        i.Row(["items-center justify-center gap-2"], content: r =>
                        {
                            r.Box([$"w-2.5 h-2.5 rounded-full shrink-0 bg-[{dot}]"]);
                            r.Text(text: SpeciesName(sp));
                        }));
                }
            });
    }

    private void RenderMapPanel(UIView view, bool mobile)
    {
        view.Column(["flex-1 min-h-0 flex flex-col"], content: panel =>
        {
            if (!mobile)
            {
                panel.Column(["shrink-0 p-4 gap-3 border-b border-secondary/60"], content: head =>
                {
                    RenderSearchBox(head);
                    if (_pick.Value is null)
                    {
                        RenderSpeciesToggle(head, compact: false);
                        RenderFilters(head);
                    }
                });
            }

            panel.ScrollArea(["flex-1 min-h-0"], content: scroll =>
            {
                scroll.Column(["p-4 gap-3"], content: body =>
                {
                    if (_pick.Value is not null)
                    {
                        RenderAnalysisSheet(body);
                    }
                    else if (_stand.Value is { } stand && _selectedAreaId.Value is null)
                    {
                        RenderStandDetail(body, stand);
                    }
                    else if (_selectedAreaId.Value is not null)
                    {
                        RenderAreaDetail(body);
                    }
                    else
                    {
                        RenderAnalysisIntro(body);
                        if (mobile)
                        {
                            RenderFilters(body);
                        }

                        RenderAreaList(body);
                    }
                });
            });
        });
    }

    private void RenderFilters(UIView view)
    {
        var plan = CurrentPlan;
        view.Row(["flex-wrap items-center gap-2"], content: row =>
        {
            row.Switch(label: T("High only", "Vain hyvät"), value: _highOnly.Value, disabled: !plan.AdvancedFilters,
                onValueChange: async v => { _highOnly.Value = v; await RefreshAreasAsync(); });
            row.Select(["w-40"], value: _maxWalk.Value, disabled: !plan.AdvancedFilters, ariaLabel: T("Walk from road", "Kävely tieltä"),
                options:
                [
                    new("any", T("Any walk", "Kävelymatka mikä vain")),
                    new("200", T("≤ 200 m from road", "≤ 200 m tieltä")),
                    new("500", T("≤ 500 m from road", "≤ 500 m tieltä")),
                    new("1000", T("≤ 1 km from road", "≤ 1 km tieltä")),
                ],
                onValueChange: async v => { _maxWalk.Value = v; await RefreshAreasAsync(); });
            row.Switch(label: T("Hide protected", "Piilota suojellut"), value: _hideRestricted.Value,
                onValueChange: async v => { _hideRestricted.Value = v; await RefreshAreasAsync(); });
        });

        if (!plan.AdvancedFilters)
        {
            view.Text([Text.Caption, "text-muted-foreground"], text: T("Suitability and walking filters are part of Premium.", "Soveltuvuus- ja kävelysuodattimet kuuluvat Premiumiin."));
        }
    }

    private void RenderAreaList(UIView view)
    {
        var plan = CurrentPlan;
        view.Row(["items-baseline justify-between"], content: head =>
        {
            head.Text([Text.H4, "font-heading"], text: T("Recommended areas in view", "Suositellut alueet näkymässä"));
            if (_areasLoading.Value)
            {
                head.Box([Icon.Spinner, "w-4 h-4"]);
            }
        });

        if (_publisher.Production.Count == 0 || !_dbReady.Value)
        {
            view.Box([Skeleton.Card]);
            return;
        }

        if (_viewport.Value is { Zoom: < 9 })
        {
            view.EmptyState(T("Zoom in to see areas", "Lähennä nähdäksesi alueet"), icon: "zoom-in",
                description: T("The pilot covers forests south-east of Oulu.", "Pilotti kattaa Oulun kaakkoispuolen metsät."));
            return;
        }

        if (_areas.Count == 0 && !_areasLoading.Value)
        {
            view.EmptyState(T("No recommended areas here", "Ei suositeltuja alueita täällä"), icon: "trees",
                description: T("Either the forests in view score low, or this is outside the Oulu pilot (dashed line). Try moving the map or clearing filters.",
                    "Näkymän metsät ovat heikkoja tai alue on Oulun pilotin ulkopuolella (katkoviiva). Siirrä karttaa tai poista suodattimia."),
                action: a => a.Button([Button.OutlineSm], text: T("Go to the pilot area", "Siirry pilottialueelle"), onClick: async () =>
                    _viewRequest.Value = new MapView(64.985, 25.62, 11, (_viewRequest.Value?.Key ?? 0) + 1)));
            return;
        }

        var shown = _areas.Take(plan.RecommendationLimit).ToList();
        var index = 1;
        foreach (var area in shown)
        {
            var rank = index++;
            view.Card([Card.Interactive, "p-0"], key: $"area-{area.Id}", onClick: async () => await SelectAreaAsync(area.Id, fly: true), content: card =>
            {
                card.Row(["items-center gap-3 p-3"], content: row =>
                {
                    row.Box(["w-9 h-9 shrink-0 rounded-full bg-[#1d3b2a] text-[#f5f1e6] flex items-center justify-center text-sm font-semibold"], content: b => b.Text(text: rank.ToString(Culture)));
                    row.Column(["flex-1 min-w-0 gap-0.5"], content: c =>
                    {
                        c.Row(["items-center gap-2 flex-wrap"], content: r =>
                        {
                            r.Badge(AreaLabel(area), tone: area.Suitability == "high" && area.PredictionKind != ModelGate.Kind ? SemanticTone.Success : SemanticTone.Info, size: BadgeSize.Sm);
                            if (area.Restriction is not null)
                            {
                                r.Badge(T("Protected", "Suojeltu"), tone: SemanticTone.Warning, size: BadgeSize.Sm);
                            }
                        });
                        c.Text([Text.Small, "text-muted-foreground"], text: string.Format(Culture, T("{0:0.#} ha · {1:0.#} km away · {2}", "{0:0.#} ha · {1:0.#} km päässä · {2}"),
                            area.AreaHa, area.DistanceKm, WalkLabel(area.DistRoadM)));
                    });
                    row.Icon([Icon.Sm, "text-muted-foreground"], name: "chevron-right");
                });
            });
        }

        if (_areas.Count > shown.Count)
        {
            view.Card([Card.Subtle], content: c =>
            {
                c.Text([Text.BodyStrong], text: string.Format(Culture, T("{0} more areas in view", "{0} aluetta lisää näkymässä"), _areas.Count - shown.Count));
                c.Text([Text.Small, "text-muted-foreground"], text: T("Free shows the top 5. Premium ranks every area and adds seasonal estimates.",
                    "Ilmaisversio näyttää 5 parasta. Premium järjestää kaikki alueet ja lisää kausiarviot."));
                c.Button([Button.PrimarySm, "mt-2 self-start"], text: T("See plans", "Katso tilaukset"), onClick: async () => await GoAsync("/pricing"));
            });
        }

        view.Text([Text.Caption, "text-muted-foreground pt-2"], text: T("Predictions are uncertain and cannot guarantee a harvest. Distances to roads are straight-line.",
            "Arviot ovat epävarmoja eivätkä takaa satoa. Etäisyydet teille ovat linnuntietä."));
    }

    // Plain words: no one needs the word "suitability" to decide where to walk
    // Grid-model areas speak in the guide's odds, capped at "Worth a try" because their evidence is limited; stand areas keep their scale
    private string AreaLabel(AreaSummary a) => a.PredictionKind == ModelGate.Kind
        ? Guide.OddsLabel(Guide.OddsOf(a.Suitability, ModelGate.Evidence), _language.Value)
        : SuitabilityLabel(a.Suitability);

    private string SuitabilityLabel(string suitability) => suitability switch
    {
        "high" => T("Very promising", "Erittäin lupaava"),
        "moderate" => T("Promising", "Lupaava"),
        "low" => T("Not great", "Heikko"),
        "unlikely" => T("Unlikely", "Epätodennäköinen"),
        _ => T("Not assessed", "Ei arvioitu"),
    };

    private string WalkLabel(double? metres) => metres switch
    {
        null => T("no mapped road nearby", "ei kartoitettua tietä lähellä"),
        < 50 => T("next to a road or track", "tien tai uran vieressä"),
        _ => string.Format(Culture, T("≈{0:0} m from a road", "≈{0:0} m tieltä"), Math.Round(metres.Value / 10) * 10),
    };

    private async Task OnViewChangeAsync(string json)
    {
        try
        {
            var v = JsonSerializer.Deserialize<JsonElement>(json);
            _viewport.Value = new Viewport(v.GetProperty("west").GetDouble(), v.GetProperty("south").GetDouble(), v.GetProperty("east").GetDouble(),
                v.GetProperty("north").GetDouble(), v.GetProperty("lat").GetDouble(), v.GetProperty("lon").GetDouble(), v.GetProperty("zoom").GetDouble());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return;
        }

        await RefreshAreasAsync();
    }

    private async Task RefreshAreasAsync()
    {
        if (_viewport.Value is not { } vp || vp.Zoom < 9 || !_dbReady.Value)
        {
            _areas.Clear();
            return;
        }

        _areasLoading.Value = true;
        try
        {
            var plan = CurrentPlan;
            var reference = _userLocation.Value is { } u ? (u.Lat, u.Lon) : (vp.Lat, vp.Lon);
            double? maxWalk = plan.AdvancedFilters && double.TryParse(_maxWalk.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : null;
            var areas = await _queries.AreasInViewAsync(_species.Value, vp.West, vp.South, vp.East, vp.North, reference.Item1, reference.Item2,
                plan.AdvancedFilters && _highOnly.Value, maxWalk, _hideRestricted.Value, 60);
            _areas.ReplaceAll(areas);
            _mapProblem.Value = null;
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Area list failed: {ex.Message}");
            _mapProblem.Value = T("Couldn't load areas — try moving the map again.", "Alueiden lataus epäonnistui — siirrä karttaa uudelleen.");
        }
        finally
        {
            _areasLoading.Value = false;
        }
    }

    private async Task SearchPlacesAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _placesBusy.Value = true;
        try
        {
            var places = await Geocoder.SearchAsync(text, _language.Value);
            _places.ReplaceAll(places);
            await _queries.TrackAsync(CurrentUserId, "place_search", new { results = places.Count });

            if (places.Count == 0)
            {
                _toast.Value = T("No places found in Finland", "Paikkoja ei löytynyt Suomesta");
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Place search failed: {ex.Message}");
            _toast.Value = T("Place search is unavailable right now — try again.", "Paikkahaku ei toimi juuri nyt — yritä uudelleen.");
        }
        finally
        {
            _placesBusy.Value = false;
        }
    }

    private async Task GoToPlaceAsync(Place place)
    {
        _places.Clear();
        _searchText.Value = place.Name;
        _selectedAreaId.Value = null;
        _stand.Value = null;
        _viewRequest.Value = new MapView(place.Lat, place.Lon, 11.5, (_viewRequest.Value?.Key ?? 0) + 1);
    }

    // The map was opened to pick where a field report happened: store the point and return to Field mode
    private async Task OnPickedAsync(string json)
    {
        try
        {
            var v = JsonSerializer.Deserialize<JsonElement>(json);
            _obsPoint.Value = new LatLon(v.GetProperty("lat").GetDouble(), v.GetProperty("lon").GetDouble());
            _obsAccuracy.Value = null;
            _obsLocationSource.Value = "map_pick";
            _obsPicking.Value = false;
            await GoAsync("/field");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Ignore a malformed click
        }
    }

    private async Task OnLocateAsync(string json)
    {
        try
        {
            var v = JsonSerializer.Deserialize<JsonElement>(json);
            _userLocation.Value = new UserLocation(v.GetProperty("lat").GetDouble(), v.GetProperty("lon").GetDouble(), v.GetProperty("accuracy").GetDouble());
            if (_pick.Value is null && _locateRequest.Value > 0)
            {
                await SelectPointAsync(_userLocation.Value.Lat, _userLocation.Value.Lon);
            }

            await RefreshAreasAsync();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A malformed fix is ignored; the next one replaces it
        }
    }

    private async Task SelectAreaAsync(long id, bool fly)
    {
        _selectedAreaId.Value = id;
        _stand.Value = null;
        _detail.Value = null;
        _season.Value = null;
        _drive.Value = null;
        _driveState.Value = null;
        _detailLoading.Value = true;

        try
        {
            var reference = _userLocation.Value is { } u ? (u.Lat, u.Lon) : _viewport.Value is { } vp ? (vp.Lat, vp.Lon) : (65.01, 25.47);
            var detail = await _queries.AreaDetailAsync(id, reference.Item1, reference.Item2);
            if (_selectedAreaId.Value != id)
            {
                return;
            }

            _detail.Value = detail;
            if (detail is not null && fly)
            {
                _viewRequest.Value = new MapView(detail.Area.Lat, detail.Area.Lon, Math.Max(_viewport.Value?.Zoom ?? 13, 13.5), (_viewRequest.Value?.Key ?? 0) + 1);
            }

            await _queries.TrackAsync(CurrentUserId, "area_opened", new { species = _species.Value, suitability = detail?.Area.Suitability });

            if (detail is not null && CurrentPlan.SeasonalEstimates)
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var weather = await _queries.WeatherNearAsync(detail.Area.Lat, detail.Area.Lon, today.Year);
                var grid = await _queries.GridWeatherAsync(detail.Area.Lat, detail.Area.Lon, today.Year);
                _season.Value = new SeasonInfo(Phenology.Estimate(detail.Area.Species, today, weather?.Days ?? [], weather?.NormalJunJulPrecip, grid),
                    weather?.Name ?? "", weather?.DistanceKm ?? 0);
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Area detail failed: {ex.Message}");
            _toast.Value = T("Couldn't open that area — try again.", "Aluetta ei voitu avata — yritä uudelleen.");
        }
        finally
        {
            _detailLoading.Value = false;
        }
    }

    private async Task SelectStandAsync(long standId)
    {
        _selectedAreaId.Value = null;
        _detail.Value = null;

        if (!_publisher.Production.TryGetValue(_species.Value, out var mv))
        {
            return;
        }

        await using var db = await OpenDbAsync();
        _stand.Value = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<StandInfo>(db, """
            SELECT s.stand_id AS StandId, p.suitability, p.evidence, p.drivers::text AS Drivers, s.fertility_class AS Fertility, s.development_class AS Development,
                s.mean_age AS MeanAge, s.basal_area AS BasalArea, sf.soil_class AS SoilClass, s.measurement_date AS Measured, af.dist_road_m AS DistRoadM,
                r.name AS Restriction, ST_Y(s.centroid) AS Lat, ST_X(s.centroid) AS Lon
            FROM forest_stands s
            JOIN predictions p ON p.stand_id = s.stand_id AND p.model_version_id = @mv AND p.species = @species
            LEFT JOIN soil_features sf ON sf.stand_id = s.stand_id
            LEFT JOIN access_features af ON af.stand_id = s.stand_id
            LEFT JOIN access_restrictions r ON r.id = af.restriction_id
            WHERE s.stand_id = @standId
            """, new { standId, mv, species = _species.Value });
        _sheetExpanded.Value = true;
    }

    private void RenderStandDetail(UIView view, StandInfo stand)
    {
        view.Row(["items-center justify-between"], content: head =>
        {
            head.Button([Button.GhostSm, "-ml-2 gap-1"], onClick: async () => _stand.Value = null, content: b =>
            {
                b.Icon([Icon.Sm], name: "arrow-left");
                b.Text(text: T("Back to list", "Takaisin listaan"));
            });
            head.Text([Text.Caption, "text-muted-foreground"], text: $"{T("Stand", "Kuvio")} {stand.StandId}");
        });

        view.Text([Text.H3, "font-heading"], text: $"{SpeciesName(_species.Value)}: {SuitabilityLabel(stand.Suitability)}");
        view.Text([Text.Small, "text-muted-foreground"], text: T("A single forest stand that is not part of a recommended area.", "Yksittäinen metsikkökuvio, joka ei kuulu suositeltuun alueeseen."));
        RenderDrivers(view, JsonSerializer.Deserialize<List<JsonElement>>(stand.Drivers)!
            .Select(d => new DriverSummary(d.GetProperty("f").GetString()!, d.GetProperty("effect").GetString()!, d.GetProperty("en").GetString()!, d.GetProperty("fi").GetString()!, 1)).ToList());

        view.Card([Card.Subtle], content: c =>
        {
            Fact(c, T("Site type", "Kasvupaikka"), ForestCodes.Label(ForestCodes.Fertility, (int?)stand.Fertility, _language.Value));
            Fact(c, T("Development", "Kehitysluokka"), ForestCodes.Label(ForestCodes.Development, stand.Development, _language.Value));
            Fact(c, T("Mean age", "Keski-ikä"), stand.MeanAge is { } age ? string.Format(Culture, T("{0:0} years", "{0:0} vuotta"), age) : T("Not recorded", "Ei tietoa"));
            Fact(c, T("Basal area", "Pohjapinta-ala"), stand.BasalArea is { } ba ? $"{ba:0} m²/ha" : T("Not recorded", "Ei tietoa"));
            Fact(c, T("Soil (GTK)", "Maaperä (GTK)"), stand.SoilClass is { } soil ? SoilLabel(soil) : T("Not mapped", "Ei kartoitettu"));
            Fact(c, T("Measured", "Mitattu"), stand.Measured?.ToString("d", Culture) ?? T("Unknown", "Tuntematon"));
            Fact(c, T("From road", "Tieltä"), WalkLabel(stand.DistRoadM));
        });

        if (stand.Restriction is { } r)
        {
            view.Alert(T("Protected area", "Suojelualue"), tone: SemanticTone.Warning, description: $"{r}. " + T("Check the area's own rules before picking.", "Tarkista alueen säännöt ennen poimintaa."));
        }
    }

    private string SoilLabel(string soilClass) =>
        SoilClassifier.Labels.TryGetValue(soilClass, out var l) ? (_language.Value == "fi" ? l.Fi : l.En) : T("Not mapped", "Ei kartoitettu");

    private static void Fact(UIView view, string label, string value)
    {
        view.Row(["justify-between gap-4 py-1.5 border-b border-secondary last:border-0"], content: r =>
        {
            r.Text([Text.Small, "text-muted-foreground"], text: label);
            r.Text([Text.Small, "font-medium text-right"], text: value);
        });
    }

    private void RenderDrivers(UIView view, IReadOnlyList<DriverSummary> drivers)
    {
        view.Column(["gap-2"], content: list =>
        {
            foreach (var d in drivers)
            {
                list.Row(["items-start gap-2"], key: d.Factor, content: row =>
                {
                    var (icon, tone) = d.Effect switch
                    {
                        "positive" => ("circle-check", "text-success-600"),
                        "negative" => ("circle-minus", "text-warning-600"),
                        _ => ("circle-dot", "text-muted-foreground"),
                    };
                    row.Icon([Icon.Sm, tone, "mt-0.5 shrink-0"], name: icon);
                    row.Text([Text.Small], text: _language.Value == "fi" ? d.Fi : d.En);
                });
            }
        });
    }
}
