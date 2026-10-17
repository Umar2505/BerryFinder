using System.Data.Common;
using System.Globalization;
using Dapper;
using Npgsql;

return await App.Run(args);

// One shared instance serves everyone (up to 200 clients); per-person state lives in Client/User reactives and the database
public record SessionIdentity();

// ?lang=fi or ?lang=en picks the language; ?species=lingonberry opens the map on that berry
public record ClientParameters(string Name = "", string? Lang = null, string? Species = null);

// Visual direction: a calm, premium Nordic outdoor-navigation product. Forest-green brand on soft cream,
// dark pine in dark mode, Fraunces headings over Inter, generous radius, a berry-purple accent used sparingly.
[App]
public partial class BerryfinderApp(IApp<SessionIdentity, ClientParameters> app)
{
    private UI UI { get; } = new(app, new IkonTheme
    {
        // Visual direction: quiet and neutral like an Apple app. Light grey canvas, white cards, near-black text, one forest-green
        // accent for the single main action on a screen, and colour only where it means something (the berries).
        ["primary"] = "#1E7B4F",
        ["primary-foreground"] = "#ffffff",
        ["background"] = "#f5f5f7",
        ["foreground"] = "#1d1d1f",
        ["card"] = "#ffffff",
        ["muted"] = "#ececf0",
        ["muted-foreground"] = "#6e6e73",
        ["border"] = "#e5e5ea",
        ["input"] = "#d2d2d7",
        ["accent-500"] = "#5e5ce6",
        ["accent-600"] = "#4b49c9",
        ["font-heading"] = "Inter",
        ["font-body"] = "Inter",
        ["radius"] = "rounded-xl",
        ["density"] = "comfortable",
        ["motion-duration-base"] = "180ms",
        ["ease-default"] = "ease-out",

        DarkMode = new IkonTheme
        {
            ["primary"] = "#30d158",
            ["primary-foreground"] = "#04210f",
            ["background"] = "#000000",
            ["foreground"] = "#f5f5f7",
            ["card"] = "#1c1c1e",
            ["muted"] = "#2c2c2e",
            ["muted-foreground"] = "#98989d",
            ["border"] = "#38383a",
            ["input"] = "#48484a",
        },
    });

    private ClientProfiles Profiles { get; } = new(app);

    // Services, built in Main; plain fields come back empty after a hot reload, so Main rebuilds them
    private PredictionPublisher _publisher = null!;
    private TrainingService _trainer = null!;
    private JobRunner _jobs = null!;
    private GridModelImporter _gridModel = null!;
    private Queries _queries = null!;
    private GuideService _guideSvc = null!;
    private ObservationService _observations = null!;
    private string _tileUrl = "";
    private string? _satelliteUrl;
    private string _coverageGeoJson = """{"type":"FeatureCollection","features":[]}""";

    // Shared: bumped when predictions are republished so every map reloads its tiles
    private readonly Reactive<int> _dataVersion = new(0);
    private readonly Reactive<int> _jobsVersion = new(0);
    private readonly Reactive<bool> _dbReady = new(false);
    private readonly Reactive<string?> _startupProblem = new(null);

    // Per client
    private readonly ClientReactive<string> _language = new("en");
    private readonly ClientReactive<string> _path = new("/");
    private readonly ClientReactive<bool> _isAdmin = new(false);
    private readonly ClientReactive<bool> _isSignedIn = new(false);
    private readonly ClientReactive<string?> _toast = new(null);

    public async Task Main()
    {
        _dbUrl = Environment.GetEnvironmentVariable("BERRYFINDER_DB") ?? (app.Secrets.TryGet("POSTGIS_URL", out var url) ? url : null);
        OpenDb openDb = OpenDbAsync;

        _publisher = new PredictionPublisher(openDb, path => app.Files.Data.ReadTextAsync(path));
        _trainer = new TrainingService(openDb, (path, json) => app.Files.Data.WriteTextAsync(path, json), async id =>
        {
            await _publisher.RefreshProductionAsync();
            await _jobs.EnqueueAsync(JobKinds.Publish, new { reason = "promotion", model = id }, "system");
        });
        _gridModel = new GridModelImporter(openDb, path => app.Files.Data.ReadBytesAsync(path), _publisher);
        _jobs = new JobRunner(openDb, async (duration, reason) => await app.BackgroundWork.StartAsync(duration, reason), _publisher, _trainer, _gridModel);
        _queries = new Queries(openDb, _publisher);
        _guideSvc = new GuideService(_queries, _publisher);
        // The model runs in the inference service (inference/). Locally it listens on 8090; deployed, set the INFERENCE_URL secret.
        var inferenceUrl = Environment.GetEnvironmentVariable("BERRYFINDER_INFERENCE_URL")
            ?? (app.Secrets.TryGet("INFERENCE_URL", out var iu) && !string.IsNullOrWhiteSpace(iu) ? iu : "http://127.0.0.1:8090");
        _inference = new InferenceClient(inferenceUrl);
        _observations = new ObservationService(openDb);
        _admin = new AdminQueries(openDb);
        _satelliteUrl = app.Secrets.TryGet("NLS_API_KEY", out var nlsKey) && !string.IsNullOrWhiteSpace(nlsKey)
            ? $"https://avoin-karttakuva.maanmittauslaitos.fi/avoin/wmts/1.0.0/ortokuva/default/WGS84_Pseudo-Mercator/{{z}}/{{y}}/{{x}}.jpg?api-key={nlsKey}"
            : null;

        _jobs.Changed += () =>
        {
            _jobsVersion.Value++;
            _ = RefreshJobListAsync();
        };
        _publisher.Published += () => _dataVersion.Value++;

        app.OnStarting(async () =>
        {
            if (SnapshotCapture.IsCaptureProcess)
            {
                return;
            }

            try
            {
                await using var db = await openDb();
                var applied = await Migrations.ApplyAsync(db);
                await Registry.EnsureAsync(db);
                foreach (var species in Species.Supported)
                {
                    await _publisher.EnsureBaselineAsync(species);
                }

                await _publisher.RefreshProductionAsync();
                _coverageGeoJson = await db.ExecuteScalarAsync<string>("""
                    SELECT json_build_object('type', 'FeatureCollection', 'features', json_build_array(json_build_object('type', 'Feature', 'properties', json_build_object(),
                        'geometry', ST_AsGeoJSON(ST_Transform(ST_Union(geom), 4326), 5)::json)))::text FROM pilot_tiles WHERE enabled
                    """) ?? _coverageGeoJson;

                // First start with an empty database: ingest the pilot region from the official sources
                if (await db.ExecuteScalarAsync<long>("SELECT count(*) FROM forest_stands") == 0
                    && await db.ExecuteScalarAsync<long>("SELECT count(*) FROM ingestion_jobs") == 0)
                {
                    await _jobs.EnqueueAsync(JobKinds.Pipeline, new { onlyMissing = true }, "system:first-start");
                }

                // A model card in data/ml that this database has not seen yet: import its grid and publish its spots
                if (await _gridModel.TryReadCardAsync() is { } card && !await _gridModel.IsImportedAsync(card.Version))
                {
                    await _jobs.EnqueueAsync(JobKinds.ModelGrid, new { version = card.Version }, "system:model-card");
                }

                Log.Instance.Info($"Database ready ({applied} migrations applied)");
                _dbReady.Value = true;
            }
            catch (Exception ex)
            {
                Log.Instance.Error($"Database start failed: {ex}");
                _startupProblem.Value = ex.Message.Contains("postgis", StringComparison.OrdinalIgnoreCase)
                    ? "The map database has no PostGIS. Set the POSTGIS_URL secret (see docs/deployment.md)."
                    : "The map data store is not reachable right now.";
                return;
            }

            try
            {
                await DeclareOffersAsync();
            }
            catch (Exception ex)
            {
                Log.Instance.Warning($"Payment offers could not be declared (billing not configured?): {ex.Message}");
            }

            try
            {
                // A local run and the deployed app each mint their own tile URL; {z}/{x}/{y} and {species} stay open for the map
                _tileUrl = (await app.MintUrlAsync(nameof(GetTile))).Url;
            }
            catch (Exception ex)
            {
                Log.Instance.Warning($"Tile URL could not be minted, falling back to the public address: {ex.Message}");
                _tileUrl = app.Endpoints.FirstOrDefault(e => e.FunctionName.EndsWith(nameof(GetTile)))?.PublicUrl ?? "";
            }

            try
            {
                _analysisUrlTemplate = (await app.MintUrlAsync(nameof(GetAnalysisGeo))).Url;
            }
            catch (Exception ex)
            {
                Log.Instance.Warning($"Analysis URL could not be minted, falling back to the public address: {ex.Message}");
                _analysisUrlTemplate = app.Endpoints.FirstOrDefault(e => e.FunctionName.EndsWith(nameof(GetAnalysisGeo)))?.PublicUrl ?? "";
            }

            // Keep the "Analyze area" button honest about whether the analyser is reachable
            _ = Task.Run(async () =>
            {
                while (!app.StoppingToken.IsCancellationRequested)
                {
                    await CheckInferenceAsync();
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_inferenceOnline.Value ? 60 : 15), app.StoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            });

            _ = Task.Run(() => _jobs.RunLoopAsync(app.StoppingToken));
        });

        app.OnClientJoined(async (ctx, parameters) =>
        {
            _language.Value = SupportedLanguage(parameters.Lang) ?? SupportedLanguage(ctx.Locale) ?? "en";
            _path.Value = NormalisePath(app.Navigation.CurrentPath);

            if (parameters.Species is { } sp && HasSpots(sp))
            {
                _species.Value = sp;
            }

            _isSignedIn.Value = !ctx.IsAnonymous && !string.IsNullOrEmpty(ctx.UserId);
            if (!string.IsNullOrEmpty(ctx.Timezone))
            {
                _timezone.Value = ctx.Timezone;
            }

            RestoreHome();

            if (_dbReady.Value && !string.IsNullOrEmpty(ctx.UserId))
            {
                await using (var db = await OpenDbAsync())
                {
                    await Queries.EnsureUserAsync(db, ctx.UserId);
                }

                _isAdmin.Value = !ctx.IsAnonymous && await IsAdminAsync(ctx.UserId, ctx);
                await LoadAccountAsync(ctx.UserId);
            }
        });

        app.Navigation.PathChangedAsync += async args => _path.Value = NormalisePath(args.Path);

        UI.Root([Page.Default], content: view =>
        {
            view.Column(["h-screen w-full bg-background text-foreground font-body flex flex-col"], content: page =>
            {
                RenderTopBar(page);

                page.Box(["flex-1 min-h-0 flex flex-col"], content: main =>
                {
                    if (_startupProblem.Value is { } problem)
                    {
                        main.Box([Layout.Page, "py-10"], content: b => b.Alert(T("Service unavailable", "Palvelu ei ole käytettävissä"), tone: SemanticTone.Error,
                            description: T(problem, "Karttatietovarastoon ei saada yhteyttä juuri nyt.")));
                        return;
                    }

                    switch (_path.Value)
                    {
                        case "/map": RenderMapPage(main); break;
                        case "/plan": RenderPlanPage(main); break;
                        case "/field": RenderFieldPage(main); break;
                        case "/saved": RenderSavedPage(main); break;
                        case "/account": RenderAccountPage(main); break;
                        case "/admin": RenderAdminPage(main); break;
                        case "/pricing": RenderPricingPage(main); break;
                        case "/methodology": RenderMethodologyPage(main); break;
                        case "/berries": RenderBerriesPage(main); break;
                        case "/about": RenderHomePage(main); break;
                        case "/faq": RenderFaqPage(main); break;
                        case "/responsible": RenderResponsiblePage(main); break;
                        case "/contact": RenderContactPage(main); break;
                        default: RenderTodayPage(main); break;
                    }
                });

                if (IsAppPath(_path.Value))
                {
                    RenderBottomNav(page);
                }
            });

            RenderBerrySheet(view);
            RenderProfileSheet(view);

            if (_toast.Value is { } toast)
            {
                view.Toast(open: true, durationMs: 3500, title: toast, onOpenChange: async open => { if (!open) _toast.Value = null; });
            }
        });
    }

    // ─── Language ─────────────────────────────────────────────────────────

    private static readonly SelectOption[] Languages = [new("en", "English"), new("fi", "Suomi")];

    private static string? SupportedLanguage(string? tag) =>
        string.IsNullOrEmpty(tag) ? null : Languages.Select(l => l.Value).FirstOrDefault(l => tag.StartsWith(l, StringComparison.OrdinalIgnoreCase));

    // English text is the key; Finnish is written beside it. More languages plug in through data/i18n/<lang>.json keyed by the English text.
    private string T(string en, string fi) => _language.Value == "fi" ? fi : en;

    private CultureInfo Culture => CultureInfo.GetCultureInfo(_language.Value == "fi" ? "fi-FI" : "en-GB");

    private string SpeciesName(string species) => BerryCatalog.TryGet(species, out var berry) ? berry.Name(_language.Value) : species;

    // A berry has spots when some model for it is in production: the stand baseline, or the grid model where it passed its check
    private bool HasSpots(string species) => _publisher.Production.ContainsKey(species);

    private IReadOnlyList<string> MappedSpecies => BerryCatalog.All.Select(b => b.Id).Where(HasSpots).ToList();

    // ─── Navigation ───────────────────────────────────────────────────────

    private static readonly string[] AppPaths = ["/", "/berries", "/map", "/plan", "/field", "/saved", "/account"];
    private static readonly string[] TabPaths = ["/", "/berries", "/map"];

    private static bool IsAppPath(string path) => AppPaths.Contains(path);

    private static string NormalisePath(string? path)
    {
        var p = (path ?? "/").Split('?', '#')[0].TrimEnd('/');
        return p.Length == 0 ? "/" : p.ToLowerInvariant();
    }

    private async Task GoAsync(string path)
    {
        _path.Value = path;
        await app.Navigation.SetPathAsync(path);
    }

    private static readonly (string Path, string Icon, string En, string Fi)[] Tabs =
    [
        ("/", "sun", "Today", "Tänään"),
        ("/berries", "leaf", "Berries", "Marjat"),
        ("/map", "map", "Map", "Kartta"),
    ];

    // Frosted top bar: the wordmark, a segmented control on wide screens, and the avatar that opens everything else
    private void RenderTopBar(UIView view)
    {
        view.Row(["shrink-0 h-14 px-5 items-center justify-between gap-3 border-b border-secondary/60 z-20", Design.Frosted], content: bar =>
        {
            bar.Button(["flex items-center gap-2 bg-transparent border-0 p-0", Design.Press], ariaLabel: "BerryFinder", onClick: async () => await GoAsync("/"), content: logo =>
            {
                logo.Box(["w-8 h-8 rounded-[10px] bg-brand-solid flex items-center justify-center"], content: b => b.Icon([Icon.Sm, "text-[var(--text-brand-button)]"], name: "leaf"));
                logo.Text(["text-[17px] font-semibold tracking-tight text-foreground"], text: "BerryFinder");
            });

            bar.Row(["hidden md:flex items-center gap-1 bg-muted rounded-full p-1"], content: seg =>
            {
                foreach (var (path, _, en, fi) in Tabs)
                {
                    var active = _path.Value == path;
                    seg.Button(["h-9 px-5 rounded-full border-0 text-[15px] font-medium", Design.Press, active ? "bg-card text-foreground shadow-sm" : "bg-transparent text-muted-foreground"],
                        text: T(en, fi), onClick: async () => await GoAsync(path));
                }
            });

            bar.Button(["w-10 h-10 rounded-full bg-card shadow-sm border-0 flex items-center justify-center", Design.Press], ariaLabel: T("Profile and settings", "Profiili ja asetukset"),
                onClick: async () => _profileOpen.Value = true, content: b => b.Icon([Icon.Md, "text-foreground"], name: "user-round"));
        });
    }

    // Frosted tab bar with the three things that matter; hidden on wide screens where the top bar has them
    private void RenderBottomNav(UIView view)
    {
        view.Row(["shrink-0 grid grid-cols-3 border-t border-secondary/60 md:hidden pb-[env(safe-area-inset-bottom)] z-20", Design.Frosted], content: nav =>
        {
            foreach (var (path, icon, en, fi) in Tabs)
            {
                var active = _path.Value == path;
                nav.Button(["flex flex-col items-center justify-center gap-0.5 bg-transparent border-0 h-[58px]", Design.Press, active ? "text-brand-secondary" : "text-muted-foreground"],
                    ariaLabel: T(en, fi), onClick: async () => await GoAsync(path), content: b =>
                    {
                        b.Icon([Icon.Md], name: icon);
                        b.Text(["text-[10px] font-medium"], text: T(en, fi));
                    });
            }
        });
    }

    // Shared page frame for the content pages: a max-width column that scrolls inside the viewport
    private void RenderScrollPage(UIView view, Action<UIView> content)
    {
        view.ScrollArea(["flex-1 min-h-0"], content: scroll =>
            scroll.Column([Layout.Page, "py-8 md:py-12 gap-10 max-w-5xl"], content: content));
    }

    // ─── Ownership and roles ──────────────────────────────────────────────

    // Admin rights live in user_accounts.role (the source of truth for this app). The platform's own Admin role also counts,
    // and the first signed-in person to open a fresh app claims ownership through a one-row table, which makes the claim race-free.
    private async Task<bool> IsAdminAsync(string userId, Context? ctx)
    {
        try
        {
            if (ctx is not null && await Profiles.GetProfileAsync(ctx) is { } profile && profile.HasRole(UserRole.Admin))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Platform role lookup failed: {ex.Message}");
        }

        await using var db = await OpenDbAsync();
        if (await db.ExecuteScalarAsync<string>("SELECT role FROM user_accounts WHERE user_id = @userId", new { userId }) == "admin")
        {
            return true;
        }

        if (await db.ExecuteAsync("INSERT INTO app_owner (id, user_id) VALUES (1, @userId) ON CONFLICT (id) DO NOTHING", new { userId }) != 1)
        {
            return false;
        }

        await db.ExecuteAsync("UPDATE user_accounts SET role = 'admin' WHERE user_id = @userId", new { userId });
        await Audit.WriteAsync(db, userId, "owner.claimed", $"user:{userId}", new { });
        return true;
    }

    // The app's spatial database needs PostGIS. The platform's built-in Postgres does not ship it, so the app
    // connects to a PostGIS server given by the POSTGIS_URL secret (or BERRYFINDER_DB for local development, see
    // docker-compose.yml). Without either it falls back to the platform database and reports what is missing.
    private string? _dbUrl;

    private async Task<DbConnection> OpenDbAsync()
    {
        DbConnection connection = _dbUrl is null ? await app.DatabaseAsync() : new NpgsqlConnection(_dbUrl);
        await connection.OpenAsync();
        return connection;
    }

    private string? CurrentUserId => ReactiveScope.UserIdOrNull is { Length: > 0 } id ? id : null;

    private async Task<bool> RequireAdminAsync()
    {
        if (!_isAdmin.Value)
        {
            _toast.Value = T("Only admins can do that", "Vain ylläpitäjät voivat tehdä tämän");
            return false;
        }

        return true;
    }
}
