using System.Globalization;
using System.Text.Json;
using Dapper;

// Internal dashboard: data coverage and health, ingestion jobs, model registry and promotion, the
// moderation queue, restrictions and product metrics. Every action re-checks the admin role.
public partial class BerryfinderApp
{
    private AdminQueries _admin = null!;
    private readonly ReactiveList<JobRow> _jobList = new();
    private readonly ClientReactive<string> _adminTab = new("overview");
    private readonly ClientReactive<CoverageStats?> _coverage = new((CoverageStats?)null);
    private readonly ClientReactiveList<SourceRow> _sources = new();
    private readonly ClientReactiveList<ModelRow> _models = new();
    private readonly ClientReactiveList<LabelRow2> _labels = new();
    private readonly ClientReactiveList<AreaPrediction> _mix = new();
    private readonly ClientReactiveList<string> _warnings = new();
    private readonly ClientReactiveList<ReviewItem> _queue = new();
    private readonly ClientReactiveList<RestrictionRow> _restrictions = new();
    private readonly ClientReactiveList<AuditRow> _audit = new();
    private readonly ClientReactive<Engagement?> _engagement = new((Engagement?)null);
    private readonly ClientReactive<string> _queueFilter = new("pending");
    private readonly ClientReactive<string> _restrictAreaId = new("");
    private readonly ClientReactive<string> _restrictReason = new("");
    private readonly ClientReactive<string> _restrictSeverity = new("exclude");
    private readonly ClientReactive<TrainingService.PresenceCheck?> _presence = new((TrainingService.PresenceCheck?)null);
    private readonly ClientReactive<bool> _adminBusy = new(false);
    private readonly ClientReactive<string?> _adminMessage = new((string?)null);
    private readonly ClientReactive<string> _reviewNote = new("");

    private async Task RefreshJobListAsync()
    {
        try
        {
            _jobList.ReplaceAll(await _admin.RecentJobsAsync());
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Job list refresh failed: {ex.Message}");
        }
    }

    private async Task LoadAdminAsync()
    {
        if (!await RequireAdminAsync())
        {
            return;
        }

        _adminBusy.Value = true;
        try
        {
            var coverage = await _admin.CoverageAsync();
            var sources = await _admin.SourcesAsync();
            var models = await _admin.ModelsAsync();
            var labels = await _admin.LabelsAsync();
            _coverage.Value = coverage;
            _sources.ReplaceAll(sources);
            _models.ReplaceAll(models);
            _labels.ReplaceAll(labels);
            _mix.ReplaceAll(await _admin.PredictionMixAsync());
            _warnings.ReplaceAll(AdminQueries.Warnings(coverage, sources, models, labels));
            _queue.ReplaceAll(await _observations.ReviewQueueAsync(_queueFilter.Value));
            _restrictions.ReplaceAll(await _admin.RestrictionsAsync(false));
            _audit.ReplaceAll(await _admin.AuditAsync());
            _engagement.Value = await _admin.EngagementAsync();
            await RefreshJobListAsync();
        }
        catch (Exception ex)
        {
            Log.Instance.Error($"Admin load failed: {ex}");
            _adminMessage.Value = "Couldn't load the dashboard. Try Refresh.";
        }
        finally
        {
            _adminBusy.Value = false;
        }
    }

    private void RenderAdminPage(UIView view)
    {
        if (!_isAdmin.Value)
        {
            view.Box([Layout.Page, "py-12"], content: b => b.EmptyState("Admins only", icon: "lock", description: "This dashboard is for the BerryFinder team."));
            return;
        }

        view.Column(["flex-1 min-h-0 flex flex-col"], content: page =>
        {
            page.Row(["shrink-0 px-4 md:px-6 py-3 border-b border-secondary items-center gap-3 flex-wrap bg-card"], content: bar =>
            {
                bar.Text([Text.H3, "font-heading"], text: "Research & operations");
                bar.ToggleGroupSingle(["flex gap-1 bg-muted rounded-lg p-1 flex-wrap"], value: _adminTab.Value, ariaLabel: "Dashboard section",
                    onValueChange: async v => { if (v.Length > 0) { _adminTab.Value = v; await LoadAdminAsync(); } },
                    content: g =>
                    {
                        foreach (var (id, label) in new[] { ("overview", "Overview"), ("data", "Data & jobs"), ("models", "Models"), ("review", "Review"), ("access", "Restrictions"), ("product", "Product") })
                        {
                            g.ToggleGroupItem(["h-9 px-3 rounded-md text-sm data-[state=on]:bg-card data-[state=on]:shadow-sm"], value: id, content: i => i.Text(text: label));
                        }
                    });
                bar.Button([Button.OutlineSm, "ml-auto gap-2"], onClick: async () => await LoadAdminAsync(), content: b =>
                {
                    if (_adminBusy.Value) b.Box([Icon.Spinner, "w-4 h-4"]); else b.Icon([Icon.Sm], name: "refresh-cw");
                    b.Text(text: "Refresh");
                });
            });

            page.ScrollArea(["flex-1 min-h-0"], content: scroll =>
                scroll.Column([Layout.Page, "py-6 gap-6 max-w-6xl"], content: body =>
                {
                    if (_adminMessage.Value is { } message)
                    {
                        body.Alert(message, tone: SemanticTone.Info, onDismiss: async () => _adminMessage.Value = null);
                    }

                    if (_coverage.Value is null)
                    {
                        body.EmptyState("Dashboard not loaded", icon: "layout-dashboard", description: "Press Refresh to load the latest numbers.",
                            action: a => a.Button([Button.PrimaryMd], text: "Load dashboard", onClick: async () => await LoadAdminAsync()));
                        return;
                    }

                    switch (_adminTab.Value)
                    {
                        case "data": RenderAdminData(body); break;
                        case "models": RenderAdminModels(body); break;
                        case "review": RenderAdminReview(body); break;
                        case "access": RenderAdminAccess(body); break;
                        case "product": RenderAdminProduct(body); break;
                        default: RenderAdminOverview(body); break;
                    }
                }));
        });
    }

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private void RenderAdminOverview(UIView body)
    {
        var c = _coverage.Value!;
        if (_warnings.Count > 0)
        {
            body.Card([Card.Elevated], content: w =>
            {
                w.Text([Text.H4, "font-heading"], text: "Data quality warnings");
                foreach (var warning in _warnings)
                {
                    w.Row(["items-start gap-2 py-1"], content: r =>
                    {
                        r.Icon([Icon.Sm, "text-warning-600 mt-0.5 shrink-0"], name: "triangle-alert");
                        r.Text([Text.Small], text: warning);
                    });
                }
            });
        }

        body.Box(["grid grid-cols-2 md:grid-cols-4 gap-3"], content: g =>
        {
            g.StatCard("Forest stands", N(c.Stands), icon: "trees");
            g.StatCard("Pilot tiles with data", $"{c.TilesWithStands}/{c.Tiles}", icon: "grid-2x2");
            g.StatCard("Stands with soil", c.SoilShare.ToString("P0", CultureInfo.InvariantCulture), icon: "layers");
            g.StatCard("Mapped access ways", N(c.AccessWays), icon: "route");
            g.StatCard("Protected areas", N(c.ProtectedAreas), icon: "shield");
            g.StatCard("FMI station-days", N(c.WeatherStationDays), icon: "cloud-sun");
            g.StatCard("NASA grid-days", N(c.GridDays), icon: "satellite");
            g.StatCard("Sentinel-2 scenes", N(c.Scenes), icon: "image");
        });

        body.Card([Card.Default], content: card =>
        {
            card.Text([Text.H4, "font-heading"], text: "What the map is showing");
            if (_mix.Count == 0)
            {
                card.Text([Text.Small, "text-muted-foreground"], text: "No predictions are published yet.");
            }

            foreach (var group in _mix.GroupBy(m => (m.Species, m.Kind)))
            {
                var total = group.Sum(g => g.Stands);
                card.Row(["items-center gap-3 flex-wrap py-1"], content: r =>
                {
                    r.Text([Text.BodyStrong, "w-28"], text: group.Key.Species);
                    r.Badge(group.Key.Kind == "heuristic" ? "Ecological baseline (unvalidated)" : "Trained model", tone: group.Key.Kind == "heuristic" ? SemanticTone.Warning : SemanticTone.Success, size: BadgeSize.Sm);
                    r.Text([Text.Small, "text-muted-foreground"], text: string.Join(" · ", group.Select(g => $"{g.Suitability} {(double)g.Stands / total:P0}")));
                });
            }
        });

        body.Card([Card.Default], content: card =>
        {
            card.Text([Text.H4, "font-heading"], text: "Forest data freshness");
            Fact(card, "Newest source update (ingested)", c.LastStandIngest?.ToString("u", CultureInfo.InvariantCulture) ?? "never");
            Fact(card, "Stand measurement years", c.OldestMeasurementYear is { } a && c.NewestMeasurementYear is { } b ? $"{a}–{b}" : "unknown");
            Fact(card, "Latest Sentinel-2 scene in the catalogue", c.LatestScene?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none yet");
            Fact(card, "GBIF presence records (CC0/CC BY)", N(c.Occurrences));
        });
    }

    private void RenderAdminData(UIView body)
    {
        body.Card([Card.Default], content: card =>
        {
            card.Text([Text.H4, "font-heading"], text: "Run ingestion");
            card.Text([Text.Small, "text-muted-foreground"], text: "Jobs run one at a time and resume per tile. 'Only missing tiles' skips tiles already ingested.");
            card.Row(["gap-2 flex-wrap mt-2"], content: r =>
            {
                foreach (var kind in JobKinds.All)
                {
                    var label = kind switch
                    {
                        JobKinds.Pipeline => "Full pipeline (missing tiles)", JobKinds.Stands => "Forest stands", JobKinds.Soil => "GTK soil", JobKinds.Access => "OSM access",
                        JobKinds.Protected => "Protected areas", JobKinds.Features => "Build features", JobKinds.Weather => "FMI weather", JobKinds.GridWeather => "NASA POWER grid",
                        JobKinds.Scenes => "Sentinel-2 scenes", JobKinds.Gbif => "GBIF records", _ => "Publish predictions",
                    };
                    r.Button([kind == JobKinds.Pipeline ? Button.PrimarySm : Button.OutlineSm], key: kind, text: label, onClick: async () => await EnqueueJobAsync(kind, onlyMissing: kind == JobKinds.Pipeline));
                }
            });
        });

        body.Card([Card.Default, "p-0 overflow-hidden"], content: card =>
        {
            card.Text([Text.H4, "font-heading", "p-4 pb-0"], text: "Jobs");
            card.Table([], content: t =>
            {
                t.TableHeader(content: h => h.TableRow(content: r =>
                {
                    foreach (var col in new[] { "#", "Kind", "Status", "Progress", "In / stored", "Message", "" })
                    {
                        r.TableCell(col, [Text.Overline]);
                    }
                }));
                t.TableBody(content: tb =>
                {
                    foreach (var job in _jobList.Take(15))
                    {
                        tb.TableRow(key: $"job-{job.Id}", content: r =>
                        {
                            r.TableCell(job.Id.ToString(CultureInfo.InvariantCulture));
                            r.TableCell(job.Kind);
                            r.TableCell(content: c => c.Badge(job.Status, tone: job.Status switch { "succeeded" => SemanticTone.Success, "failed" => SemanticTone.Error, "running" => SemanticTone.Info, _ => SemanticTone.Neutral }, size: BadgeSize.Sm));
                            r.TableCell($"{job.Progress:P0}");
                            r.TableCell($"{job.RecordsIn} / {job.RecordsUpserted}");
                            r.TableCell(job.Error ?? job.Message ?? "");
                            r.TableCell(content: c =>
                            {
                                if (job.Status == "queued")
                                {
                                    c.Button([Button.GhostSm], text: "Cancel", onClick: async () => { if (await RequireAdminAsync()) await _jobs.CancelAsync(job.Id, CurrentUserId); });
                                }
                            });
                        });
                    }
                });
            });
        });

        body.Card([Card.Default, "p-0 overflow-hidden"], content: card =>
        {
            card.Text([Text.H4, "font-heading", "p-4 pb-0"], text: "Data sources, licences and freshness");
            card.Table([], content: t =>
            {
                t.TableHeader(content: h => h.TableRow(content: r =>
                {
                    foreach (var col in new[] { "Source", "Licence", "Commercial use", "Status", "Records", "Last success" })
                    {
                        r.TableCell(col, [Text.Overline]);
                    }
                }));
                t.TableBody(content: tb =>
                {
                    foreach (var s in _sources)
                    {
                        tb.TableRow(key: s.Id, content: r =>
                        {
                            r.TableCell(content: c =>
                            {
                                c.Text([Text.BodyStrong], text: s.Name);
                                c.Text([Text.Caption, "text-muted-foreground"], text: s.Provider);
                            });
                            r.TableCell(s.License);
                            r.TableCell(s.CommercialUse.Replace('_', ' '));
                            r.TableCell(content: c => c.Badge(s.Status.Replace('_', ' '), tone: s.Status == "active" ? SemanticTone.Success : SemanticTone.Warning, size: BadgeSize.Sm));
                            r.TableCell(N(s.Records));
                            r.TableCell(s.LastSuccessAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "never");
                        });
                    }
                });
            });
        });
    }

    private async Task EnqueueJobAsync(string kind, bool onlyMissing)
    {
        if (!await RequireAdminAsync())
        {
            return;
        }

        var id = await _jobs.EnqueueAsync(kind, new { onlyMissing }, CurrentUserId);
        _adminMessage.Value = $"Queued job #{id} ({kind}).";
        await RefreshJobListAsync();
    }

    private void RenderAdminModels(UIView body)
    {
        var accepted = _labels.Where(l => l.Qc == "accepted").Sum(l => l.N);
        body.Card([Card.Elevated], content: card =>
        {
            card.Text([Text.H4, "font-heading"], text: "Training");
            card.Text([Text.Small], text: $"{accepted} accepted field observations. A model is trained only with at least {TrainingPipeline.MinRows} usable labels (≥ {TrainingPipeline.MinPerClass} presences and ≥ {TrainingPipeline.MinPerClass} surveyed absences) across ≥ {TrainingPipeline.MinBlocks} spatial blocks, validated on held-out blocks and compared with the ecological baseline. Nothing is promoted automatically.");
            card.Row(["gap-2 mt-2 flex-wrap"], content: r =>
            {
                r.Button([Button.PrimaryMd], text: "Run training now", onClick: async () => await EnqueueJobAsync(JobKinds.Train, false));
                r.Button([Button.OutlineMd], text: "Check against GBIF presence records", onClick: async () => await RunPresenceChecksAsync());
            });

            if (_labels.Count > 0)
            {
                card.Text([Text.Label, "mt-3"], text: "Label distribution (real observations only)");
                foreach (var g in _labels.GroupBy(l => (l.Species, l.Qc)))
                {
                    card.Text([Text.Small, "text-muted-foreground"], text: $"{g.Key.Species} · {g.Key.Qc}: " + string.Join(", ", g.Select(x => $"{x.Outcome} ({x.Method}) {x.N}")));
                }
            }

            if (_presence.Value is { } p)
            {
                card.Alert("GBIF presence-only sanity check", tone: SemanticTone.Info,
                    description: $"{p.Species}: {p.PresenceStands} stands with records vs {p.BackgroundStands} sampled stands. " + (p.Auc is { } a ? $"Rank AUC {a:0.00}. " : "Too few records to compute. ") + p.Caveat);
            }
        });

        foreach (var m in _models)
        {
            body.Card([m.Status == "production" ? Card.Elevated : Card.Default], key: $"model-{m.Id}", content: card =>
            {
                card.Row(["items-center gap-2 flex-wrap"], content: r =>
                {
                    r.Text([Text.BodyStrong], text: $"{m.Species} · {m.Version}");
                    r.Badge(m.Status.Replace('_', ' '), tone: m.Status switch { "production" => SemanticTone.Success, "rejected" or "insufficient_data" => SemanticTone.Warning, _ => SemanticTone.Neutral }, size: BadgeSize.Sm);
                    r.Badge(m.Kind == "heuristic" ? "Unvalidated baseline" : m.Kind, tone: SemanticTone.Neutral, size: BadgeSize.Sm, outline: true);
                    r.Text([Text.Caption, "text-muted-foreground ml-auto"], text: m.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                });
                if (m.Notes.Length > 0) card.Text([Text.Small, "text-muted-foreground"], text: m.Notes);
                RenderModelMetrics(card, m);

                if (m.Status != "production" && m.Status != "insufficient_data")
                {
                    card.Button([Button.OutlineSm, "self-start mt-2"], text: m.Kind == "heuristic" ? "Roll back to this baseline" : "Promote to production", onClick: async () => await PromoteAsync(m));
                }
            });
        }
    }

    private void RenderModelMetrics(UIView card, ModelRow m)
    {
        if (m.Kind == "heuristic")
        {
            card.Text([Text.Caption, "text-muted-foreground"], text: "No accuracy metrics: this baseline has not been validated against field data, so none are shown.");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(m.Metrics);
            var root = doc.RootElement;
            if (m.Kind == ModelGate.Kind)
            {
                string G(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble().ToString("0.000", CultureInfo.InvariantCulture) : "n/a";
                card.Table([], content: t =>
                {
                    t.TableHeader(content: h => h.TableRow(content: r => { foreach (var c in new[] { "Held-out blocks", "AUC", "Boyce", "Top-20 % recall" }) r.TableCell(c, [Text.Overline]); }));
                    t.TableBody(content: tb =>
                    {
                        if (root.TryGetProperty("RulesAuc", out var rules) && rules.ValueKind == JsonValueKind.Number)
                        {
                            tb.TableRow(content: r => { r.TableCell("Recovers its rule labels"); r.TableCell(G("RulesAuc")); r.TableCell(G("RulesBoyce")); r.TableCell("n/a"); });
                        }

                        tb.TableRow(content: r => { r.TableCell("Real GBIF sightings vs plant background"); r.TableCell(G("GbifAuc")); r.TableCell(G("GbifBoyce")); r.TableCell(G("GbifRecallTop20")); });
                    });
                });
                card.Text([Text.Caption, "text-muted-foreground"], text: $"PyTorch ensemble of {G("Members").Split('.')[0]} networks, mean validation AUC {G("ValidationAuc")}. Spots need GBIF AUC ≥ {ModelGate.MinGbifAuc:0.00} with a positive Boyce index; presence-only records are biased toward roads and towns, so this is a sanity check, not accuracy.");
                return;
            }

            if (root.TryGetProperty("Model", out var model) && model.ValueKind == JsonValueKind.Object && root.TryGetProperty("Baseline", out var baseline))
            {
                string F(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble().ToString("0.000", CultureInfo.InvariantCulture) : "n/a";
                card.Table([], content: t =>
                {
                    t.TableHeader(content: h => h.TableRow(content: r => { foreach (var c in new[] { "Held-out blocks", "AUC", "Brier", "Calibration error", "Precision top 10%", "Precision top 20%" }) r.TableCell(c, [Text.Overline]); }));
                    t.TableBody(content: tb =>
                    {
                        foreach (var (name, e) in new[] { ("Ecological baseline", baseline), ("Model", model) })
                        {
                            tb.TableRow(content: r =>
                            {
                                r.TableCell(name);
                                r.TableCell(F(e, "Auc")); r.TableCell(F(e, "Brier")); r.TableCell(F(e, "CalibrationError")); r.TableCell(F(e, "PrecisionAt10")); r.TableCell(F(e, "PrecisionAt20"));
                            });
                        }
                    });
                });
                if (root.TryGetProperty("Split", out var split)) card.Text([Text.Caption, "text-muted-foreground"], text: split.GetString() ?? "");
            }
        }
        catch (JsonException)
        {
            card.Text([Text.Caption, "text-muted-foreground"], text: "Metrics could not be read.");
        }
    }

    private async Task PromoteAsync(ModelRow m)
    {
        if (!await RequireAdminAsync())
        {
            return;
        }

        var error = await _trainer.PromoteAsync(m.Id, CurrentUserId);
        _adminMessage.Value = error ?? $"{m.Version} is now in production for {m.Species}. Predictions are being republished.";
        await LoadAdminAsync();
    }

    private async Task RunPresenceChecksAsync()
    {
        if (!await RequireAdminAsync())
        {
            return;
        }

        _presence.Value = await _trainer.PresenceCheckAsync(Species.Bilberry);
    }

    private void RenderAdminReview(UIView body)
    {
        body.Row(["items-center gap-3 flex-wrap"], content: r =>
        {
            r.Text([Text.H4, "font-heading"], text: "Observation review");
            r.Select(["w-44"], value: _queueFilter.Value, ariaLabel: "Status filter", onValueChange: async v => { _queueFilter.Value = v; _queue.ReplaceAll(await _observations.ReviewQueueAsync(v)); },
                options: [new("pending", "Pending"), new("needs_info", "Needs info"), new("accepted", "Accepted"), new("rejected", "Rejected")]);
        });

        if (_queue.Count == 0)
        {
            body.EmptyState("Nothing in this queue", icon: "inbox", description: "New field reports appear here for review.");
            return;
        }

        body.TextField(["max-w-md"], label: "Note to include with the next decision (optional)", bind: _reviewNote);

        foreach (var o in _queue)
        {
            body.Card([Card.Default], key: $"review-{o.Id}", content: card =>
            {
                card.Row(["items-center gap-2 flex-wrap"], content: r =>
                {
                    r.Text([Text.BodyStrong], text: $"#{o.Id} · {o.Species} · {o.Outcome}");
                    r.Badge(o.Method == "plot_1m2" ? "1 m² plot" : "visit", tone: SemanticTone.Neutral, size: BadgeSize.Sm, outline: true);
                    foreach (var flag in JsonSerializer.Deserialize<List<string>>(o.QcFlags) ?? [])
                    {
                        r.Badge(flag.Replace('_', ' '), tone: SemanticTone.Warning, size: BadgeSize.Sm);
                    }
                });
                card.Text([Text.Small, "text-muted-foreground"], text: $"{o.ObservedOn:yyyy-MM-dd} · user {Short(o.UserId)} ({o.UserSubmissions} reports) · effort {(o.EffortMinutes?.ToString() ?? "–")} min · GPS ±{(o.GpsAccuracyM?.ToString("0") ?? "?")} m ({o.LocationSource}) · model says: {o.StandSuitability ?? "n/a"} · photos {o.Photos} · training consent {(o.UseForTraining ? "yes" : "no")}");
                if (o.Notes is { Length: > 0 } notes) card.Text([Text.Small], text: $"“{notes}”");
                card.Text([Text.Caption, "text-muted-foreground"], text: $"Exact location (admins only): {o.Lat:0.00000}, {o.Lon:0.00000}");

                card.Row(["gap-2 flex-wrap mt-2"], content: r =>
                {
                    r.Button([Button.PrimarySm], text: "Approve", onClick: async () => await ReviewAsync(o.Id, "accepted"));
                    r.Button([Button.OutlineSm], text: "Request changes", onClick: async () => await ReviewAsync(o.Id, "needs_info"));
                    r.Button([Button.OutlineErrorSm], text: "Reject", onClick: async () => await ReviewAsync(o.Id, "rejected"));
                });
            });
        }
    }

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    private async Task ReviewAsync(long id, string decision)
    {
        if (!await RequireAdminAsync() || CurrentUserId is not { } actor)
        {
            return;
        }

        await _observations.ReviewAsync(id, decision, _reviewNote.Value.Length == 0 ? null : _reviewNote.Value, actor);
        _queue.ReplaceAll(await _observations.ReviewQueueAsync(_queueFilter.Value));
        _adminMessage.Value = $"Report #{id}: {decision}. Undo by changing its status from the matching queue.";
    }

    private void RenderAdminAccess(UIView body)
    {
        body.Card([Card.Default], content: card =>
        {
            card.Text([Text.H4, "font-heading"], text: "Mark an area unavailable");
            card.Text([Text.Small, "text-muted-foreground"], text: "Use the area id shown in the map's address of an area, or the list below. 'Exclude' hides the area from recommendations; 'Warn' keeps it with a warning.");
            card.Row(["gap-3 flex-wrap items-end"], content: r =>
            {
                r.TextField(["w-32"], label: "Area id", type: "number", bind: _restrictAreaId);
                r.TextField(["flex-1 min-w-[200px]"], label: "Reason (shown to admins)", bind: _restrictReason);
                r.Select(["w-32"], value: _restrictSeverity.Value, ariaLabel: "Severity", onValueChange: async v => _restrictSeverity.Value = v, options: [new("exclude", "Exclude"), new("warn", "Warn")]);
                r.Button([Button.PrimaryMd], text: "Apply", onClick: async () => await RestrictAreaAsync());
            });
        });

        foreach (var r in _restrictions)
        {
            body.Card([Card.Subtle], key: $"restr-{r.Id}", content: card =>
                card.Row(["items-center gap-3 flex-wrap"], content: row =>
                {
                    row.Column(["flex-1 min-w-[200px] gap-0.5"], content: c =>
                    {
                        c.Text([Text.BodyStrong], text: r.Name ?? r.RestrictionType);
                        c.Text([Text.Caption, "text-muted-foreground"], text: $"{r.Source} · {r.RestrictionType}" + (r.Notes is { Length: > 0 } n ? $" · {n}" : ""));
                    });
                    row.Badge(r.Severity, tone: r.Severity == "exclude" ? SemanticTone.Error : SemanticTone.Warning, size: BadgeSize.Sm);
                    if (r.Source == "admin")
                    {
                        row.Button([Button.OutlineSm], text: r.Active ? "Lift" : "Re-apply", onClick: async () => await ToggleRestrictionAsync(r));
                    }
                }));
        }
    }

    private async Task RestrictAreaAsync()
    {
        if (!await RequireAdminAsync() || CurrentUserId is not { } actor)
        {
            return;
        }

        if (!long.TryParse(_restrictAreaId.Value, out var areaId) || _restrictReason.Value.Trim().Length < 3)
        {
            _adminMessage.Value = "Enter a numeric area id and a reason.";
            return;
        }

        var id = await _admin.RestrictAreaAsync(areaId, _restrictReason.Value.Trim(), _restrictSeverity.Value, actor);
        if (id == 0)
        {
            _adminMessage.Value = $"No area with id {areaId}.";
            return;
        }

        await FeatureBuilder.RefreshRestrictionsAsync(OpenDbAsync);
        await _jobs.EnqueueAsync(JobKinds.Publish, new { reason = "restriction" }, actor);
        _adminMessage.Value = $"Area {areaId} marked {_restrictSeverity.Value}. Predictions are being republished.";
        _restrictions.ReplaceAll(await _admin.RestrictionsAsync(false));
    }

    private async Task ToggleRestrictionAsync(RestrictionRow r)
    {
        if (!await RequireAdminAsync() || CurrentUserId is not { } actor)
        {
            return;
        }

        await _admin.SetRestrictionActiveAsync(r.Id, !r.Active, actor);
        await FeatureBuilder.RefreshRestrictionsAsync(OpenDbAsync);
        await _jobs.EnqueueAsync(JobKinds.Publish, new { reason = "restriction" }, actor);
        _restrictions.ReplaceAll(await _admin.RestrictionsAsync(false));
    }

    private void RenderAdminProduct(UIView body)
    {
        if (_engagement.Value is { } e)
        {
            body.Text([Text.H4, "font-heading"], text: "Last 7 days");
            body.Box(["grid grid-cols-2 md:grid-cols-4 gap-3"], content: g =>
            {
                g.StatCard("Active users", N(e.Users7d), icon: "users");
                g.StatCard("Place searches", N(e.Searches7d), icon: "search");
                g.StatCard("Areas opened", N(e.AreasOpened7d), icon: "map-pin");
                g.StatCard("Trips planned", N(e.TripsPlanned7d), icon: "route");
                g.StatCard("Navigation hand-offs", N(e.Navigations7d), icon: "navigation");
                g.StatCard("Field reports", N(e.Reports7d), icon: "clipboard-pen");
                g.StatCard("Checkouts started", N(e.Checkouts7d), icon: "credit-card");
                g.StatCard("Accounts", N(e.Accounts), icon: "user-round");
            });
            body.Card([Card.Default], content: c =>
            {
                c.Text([Text.H4, "font-heading"], text: "Funnel and revenue signals");
                Fact(c, "Search → area opened", e.Searches7d == 0 ? "n/a" : $"{(double)e.AreasOpened7d / e.Searches7d:P0}");
                Fact(c, "Area opened → navigation hand-off", e.AreasOpened7d == 0 ? "n/a" : $"{(double)e.Navigations7d / e.AreasOpened7d:P0}");
                Fact(c, "Premium / Professional subscribers", $"{e.Premium} / {e.Professional}");
                c.Text([Text.Caption, "text-muted-foreground"], text: "Visit completion, perceived usefulness, retention, CAC and costs are measured in the 2027 pilot (see docs/validation-plan.md); they are not estimated here.");
            });
        }

        body.Card([Card.Default], content: c =>
        {
            c.Text([Text.H4, "font-heading"], text: "Pilot controls");
            c.Switch(label: "Open every feature to pilot testers (no billing needed)", value: Plans.PilotOpenAccess, onValueChange: async v =>
            {
                if (await RequireAdminAsync())
                {
                    Plans.PilotOpenAccess = v;
                    await using var db = await OpenDbAsync();
                    await Audit.WriteAsync(db, CurrentUserId, "pilot.open_access", null, new { enabled = v });
                    _adminMessage.Value = v ? "All features are open to everyone until the app restarts." : "Plan limits apply again.";
                }
            });
        });

        body.Text([Text.H4, "font-heading"], text: "Audit log");
        foreach (var a in _audit)
        {
            body.Text([Text.Caption, "text-muted-foreground font-mono"], text: $"{a.At:yyyy-MM-dd HH:mm} {Short(a.Actor ?? "system")} {a.Action} {a.Target} {a.Details}");
        }
    }
}
