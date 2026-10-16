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
        ["primary"] = "#2f5d3a",
        ["primary-foreground"] = "#f7f3e8",
        ["background"] = "#f3efe4",
        ["foreground"] = "#16261c",
        ["card"] = "#fffdf7",
        ["muted"] = "#ebe5d6",
        ["muted-foreground"] = "#56655a",
        ["border"] = "#e0d9c8",
        ["input"] = "#d3cbb7",
        ["accent-500"] = "#7b2e7d",
        ["accent-600"] = "#5e215f",
        ["font-heading"] = "Fraunces",
        ["font-body"] = "Inter",
        ["radius"] = "rounded-xl",
        ["density"] = "comfortable",
        ["motion-duration-base"] = "180ms",
        ["ease-default"] = "ease-out",

        DarkMode = new IkonTheme
        {
            ["primary"] = "#8cc49a",
            ["primary-foreground"] = "#0e1a12",
            ["background"] = "#0e1712",
            ["foreground"] = "#ebe6d8",
            ["card"] = "#15231a",
            ["muted"] = "#1c2d22",
            ["muted-foreground"] = "#9fb0a2",
            ["border"] = "#253a2c",
            ["input"] = "#2f4636",
        },
    });

    private ClientProfiles Profiles { get; } = new(app);

    // Services, built in Main; plain fields come back empty after a hot reload, so Main rebuilds them
    private PredictionPublisher _publisher = null!;
    private TrainingService _trainer = null!;
    private JobRunner _jobs = null!;
    private Queries _queries = null!;
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
        _jobs = new JobRunner(openDb, async (duration, reason) => await app.BackgroundWork.StartAsync(duration, reason), _publisher, _trainer);
        _queries = new Queries(openDb, _publisher);
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

            _ = Task.Run(() => _jobs.RunLoopAsync(app.StoppingToken));
        });

        app.OnClientJoined(async (ctx, parameters) =>
        {
            _language.Value = SupportedLanguage(parameters.Lang) ?? SupportedLanguage(ctx.Locale) ?? "en";
            _path.Value = NormalisePath(app.Navigation.CurrentPath);

            if (parameters.Species is { } sp && Species.Supported.Contains(sp))
            {
                _species.Value = sp;
            }

            _isSignedIn.Value = !ctx.IsAnonymous && !string.IsNullOrEmpty(ctx.UserId);

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
                        case "/faq": RenderFaqPage(main); break;
                        case "/responsible": RenderResponsiblePage(main); break;
                        case "/contact": RenderContactPage(main); break;
                        default: RenderHomePage(main); break;
                    }
                });

                if (IsAppPath(_path.Value))
                {
                    RenderBottomNav(page);
                }
            });

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

    private string SpeciesName(string species) => species switch
    {
        Species.Bilberry => T("Bilberry", "Mustikka"),
        Species.Lingonberry => T("Lingonberry", "Puolukka"),
        Species.Cloudberry => T("Cloudberry", "Hilla"),
        _ => species,
    };

    // ─── Navigation ───────────────────────────────────────────────────────

    private static readonly string[] AppPaths = ["/map", "/plan", "/field", "/saved", "/account"];

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

    private void RenderTopBar(UIView view)
    {
        var inApp = IsAppPath(_path.Value) || _path.Value == "/admin";

        view.Row(["shrink-0 h-14 px-4 md:px-6 items-center justify-between gap-3 border-b border-secondary bg-card/80 backdrop-blur"], content: bar =>
        {
            bar.Button(["flex items-center gap-2 bg-transparent border-0 p-0 cursor-pointer"], ariaLabel: "BerryFinder", onClick: async () => await GoAsync("/"), content: logo =>
            {
                logo.Box(["w-8 h-8 rounded-full bg-brand-solid flex items-center justify-center"], content: b =>
                    b.Icon([Icon.Sm, "text-[var(--text-brand-button)]"], name: "trees"));
                logo.Text(["font-heading text-lg font-semibold tracking-tight text-foreground"], text: "BerryFinder");
                logo.Text(["hidden sm:inline text-[10px] uppercase tracking-widest text-accent-600 font-semibold mt-1"], text: T("Oulu pilot", "Oulun pilotti"));
            });

            bar.Row(["hidden md:flex items-center gap-1"], content: links =>
            {
                foreach (var (path, en, fi) in new[] { ("/map", "Map", "Kartta"), ("/methodology", "How it works", "Miten se toimii"), ("/berries", "Berries", "Marjat"), ("/pricing", "Pricing", "Hinnat"), ("/faq", "FAQ", "UKK") })
                {
                    var active = _path.Value == path;
                    links.Button([Button.GhostSm, active ? "text-brand-secondary font-semibold" : "text-muted-foreground"], text: T(en, fi), onClick: async () => await GoAsync(path));
                }

                if (_isAdmin.Value)
                {
                    links.Button([Button.GhostSm, _path.Value == "/admin" ? "text-brand-secondary font-semibold" : "text-muted-foreground"], text: "Admin", onClick: async () => await GoAsync("/admin"));
                }
            });

            bar.Row(["items-center gap-2"], content: right =>
            {
                right.Select(["w-24"], options: Languages, value: _language.Value, ariaLabel: T("Language", "Kieli"), onValueChange: async language => _language.Value = language);

                if (!inApp)
                {
                    right.Button([Button.PrimarySm], text: T("Open map", "Avaa kartta"), onClick: async () => await GoAsync("/map"));
                }
                else if (!_isSignedIn.Value)
                {
                    right.Button([Button.OutlineSm], text: T("Sign in", "Kirjaudu"), onClick: async () => await ClientFunctions.LoginShowAsync(T("Sign in to save areas and send field reports", "Kirjaudu tallentaaksesi alueita ja lähettääksesi havaintoja")));
                }
                else
                {
                    right.Button([Button.GhostSm, "hidden md:flex"], text: T("Account", "Tili"), onClick: async () => await GoAsync("/account"));
                }
            });
        });
    }

    private void RenderBottomNav(UIView view)
    {
        view.Row(["shrink-0 h-16 border-t border-secondary bg-card grid grid-cols-5 md:hidden"], content: nav =>
        {
            foreach (var (path, icon, en, fi) in new[]
            {
                ("/map", "map", "Map", "Kartta"), ("/plan", "route", "Plan", "Suunnittele"), ("/field", "footprints", "Field", "Maastossa"),
                ("/saved", "bookmark", "Saved", "Tallennetut"), ("/account", "user-round", "Account", "Tili"),
            })
            {
                var active = _path.Value == path;
                nav.Button(["flex flex-col items-center justify-center gap-1 bg-transparent border-0 min-h-[48px]", active ? "text-brand-secondary" : "text-muted-foreground"],
                    ariaLabel: T(en, fi), onClick: async () => await GoAsync(path), content: b =>
                    {
                        b.Icon([Icon.Md], name: icon);
                        b.Text(["text-[11px] font-medium"], text: T(en, fi));
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
