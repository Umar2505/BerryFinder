<!-- This file is automatically updated by ikon tool commands. User edits are preserved only below the ikon-user-content-below marker. -->

# IKON App Development Guidelines

## Quick start

1. The scaffold already runs: the `[App]` class in `app/<Project>/` declares its UI in `Main()` with `UI.Root(...)`.
2. Pick who shares an instance (`app-structure` guide): the scaffold's `SessionIdentity(string? UserId)` gives each user their own, `SessionIdentity()` one for all, `SessionIdentity(string? Room)` one per `?room=`; a local run hosts just one. An instance holds 200 clients; more need `[Sharded]` (`scaling` guide).
3. Name the kind of product and a visual direction ("What the app must contain", "How the app must look").
4. Read the agent guides it needs, and before writing a screen adapt the closest pattern (both listed at the bottom).
5. Check "Common Pitfalls" before the first build.
6. `ikon run --detach`, then `ikon browse --steps "observe"` ("Running and checking the app").

## Architecture

An IKON app is a C# app that runs in the cloud and streams reactive UI to browser clients over WebSocket; an instance keeps running when every client disconnects. Nearly everything, styling included, is C#; touch `frontend-node` only for custom React components (`frontend-fundamentals` guide). Add a NuGet package with `dotnet add app/<Project>/<Project>.csproj package <Id>`, an npm one with `npm install <name>` in `frontend-node`.

`ikon new` creates a cloud app that a local run uses too: AI calls are billed to its space. A deployed instance has 1 CPU and 512 MB (the app uses about 300 MB). Its image has .NET and `ffmpeg`, no Python and no extra fonts (`app-container` guide).

**AI services** (the platform holds every API key): `Emerge.Run<T>()` for LLM text and structured output; `ImageGenerator`, `SpeechGenerator`, `SpeechRecognizer`, `VideoGenerator`, `MusicGenerator`, `EmbeddingGenerator`, `WebSearcher`, `Classifier` (moderation, free), `DepthEstimator`, `ImageSegmenter`, `MeshGenerator`. Each has a static one-shot on a default model (`ImageGenerator.GenerateAsync(prompt)`), and `new Xxx(model)` with its `XxxConfig` for the options (the `ai-*` guides).

**UI** is declared in C# inside `UI.Root([Page.Default], content: view => { ... })`. Components are methods on `view` (`UIView`, aliased `IView`; there is no `ViewContext`, CS0246); nested content lambdas get their own `view`.

**Styling:** Crosswind, IKON's utility classes in C# string arrays: nearly every Tailwind utility, with Tailwind v4's names and scale, plus IKON's own, such as semantic theme classes, keyframe motion and variant groups.
Use **semantic theme classes** (`bg-card`, `text-foreground`, `bg-brand-solid`, `border-secondary`), which follow light/dark and the `IkonTheme`, and the **`Ikon.Parallax.Theming` tokens** built from them (`Button.PrimaryMd`, `Card.Default`, `Text.H1`, `Layout.Page`), in one array: `[Button.PrimaryMd, "mt-4"]`. Fixed palette classes (`bg-amber-400`) are for looks that ignore the theme; a colour the palette lacks goes in the theme as a role, not a hex class (IKON013).

Animations are keyframe steps, each `time:utilities` joined by `_`: `"motion-[0:opacity-0_translate-y-[8px],100:opacity-100_translate-y-0] motion-duration-300ms"`. There are no named animations and no `motion-preset-*` (IKON022).

**A `style:` array is exactly what renders.** With none a component gets its themed default; with one, only those classes (`view.Button(["px-6"], …)` is a bare button). `"default"` first keeps the themed default under yours: `view.Button(["default", "px-6"], …)`. Token composites (`Button.OutlineMd`) are complete: add classes after, never `"default"` before. Slot styles (`contentStyle:`, `titleStyle:`) work the same. `view.Card` pads its `content:`; `contentStyle: ["p-0"]` makes it edge to edge.

**Theming:** `new IkonTheme { ["primary"] = "amber-400", ["background"] = "zinc-950", … }` at the `UI` initializer. The only named properties are `Mode`, `DarkMode` (or `Dark`) and `Light`. Of the status colours only `["destructive"]` is a key; set `["success-500"]` or a variable by name (`theming` guide).

## Running and checking the app

From the app root, `ikon run --detach` starts the app and returns once it is ready; a plain
`ikon run` does not return while the app runs. `build/app/artifacts/bin/<App>/debug/ikon-server-info.json`
has the URLs. `ikon stop` ends the run after the app saves its persistent state. A `.cs` edit under `app/<App>/` hot-reloads
the run, detached too (a compile error keeps the previous build, and `browse` prints the error); a `.csproj`
or `ikon-config.toml` change needs `ikon stop` and a new run. Check the app as text before reaching for a browser:

```bash
ikon browse --steps "observe"                                   # screen text + [id] action handles
ikon browse --steps "fill Message hello; tap Send; wait-absent 60s Thinking; wait 5s --new Sent"
ikon browse --steps "as alice: tap Join; as bob: observe"      # two test users (local run)
ikon browse --text-only --format json < steps.txt               # one step per line, one JSON object per step
```

`ikon browse --help` lists every step and option. Acting steps print only what changed; `click`,
`eval` and `console` reach custom React components. An observation costs a few hundred tokens and a
screenshot thousands, so use `look` only when the question is about pixels. `wait 15s Sent` returns
once the text shows, or at once if it is already there; `--new` waits for a new one, and `wait 15s`
alone waits for the next update. `sleep 2s` always pauses. A bare number is milliseconds. Each run is a new client session with new ids, so a saved script names controls by
label. Where the shell strips quotes (PowerShell), use `--steps-file scripts/steps.txt`. `hold`, `audio`,
`mic` and `--fake-media-file` (a .wav that loops from when the page first opens the microphone) test a voice or camera app.
`look`, `upload`, `mic`, `audio`, downloads, fake media and `--grant` need the browser, not
`--text-only`; an `as <name>:` user has no page (`--as-user <name>` has one).

A script worth keeping is a **check gate**, `.ikon/gates/<name>.toml`: `ikon test` runs it against
the local run or the deployed app (`ikon help test --json` lists the file's keys), and
`ikon task set <task-id> --reproduction <name>` records it as a defect's reproduction.

`--app-id <space>` or `--target <name>` makes `browse` and `test` drive the **deployed** app;
without them they drive the local run, else the linked deployed app. The page signs in as you when
the app offers the method you used for `ikon login`; `--as-guest` opens it as a new guest, and on a
local run `--as-user <name>` or `as <name>: <step>` act as named test users. When
`[Auth] Methods` lists "global", every guest is one shared user, which a deployed app's page cannot
join, so use `--text-only` there or list "guest" instead. `click <name>` is a real mouse click;
audio in the page plays without one.

A local run uses the deployed app's `app.Files`; `ikon file list|delete` shows and clears them (`--cloud-file`: every `AssetClass.CloudFile` asset, uploads too).

## The ikon tool's commands

This part comes from the ikon tool, whose help is the authority on its commands:
`ikon help <section> --json`, `ikon help <command> --json`, and `ikon help --json` for all of them, how the app and
environment are chosen, and the shape of `--format json` output. If a doc names a command the tool
does not know, the command was renamed; look it up in the help. Settings are in `ikon-config.toml`,
each explained by the comment above it: `ikon-config.development.toml` for an app on the development
platform (`run` and `deploy` read that one file), `ikon-config.<name>.toml` for `--target <name>`.
On Windows, edit files (steps files too) with your file-edit tool: PowerShell 5.1's `Get-Content` and `Set-Content`
default to the system code page and garble non-ASCII text.

The tool uses these top-level folders: `app/`, `frontend-node/`, `frontend-flutter/`, `data/`,
`public/`, `branding/`, `emails/`, `tools/<runtime>/` (programs the app runs), `container/` (the
app's image), and the generated `build/` and `docs/`. Keep your own scripts and steps files in the
app's `scripts/`, never a shared `/tmp`; `.gitignore` keeps it out of the app's repository.

## What the app must contain

Before writing UI, name the KIND of product and build what its ordinary workflow needs from the first build, and nothing unrequested: the `product-design` guide lists it per kind (records get search, sort, filters and export; a board a non-drag move; a review queue Approve · Reject · Request changes).
Every visible control works or is disabled with a visible reason; changes, AI-made ones included, can be undone; a filter never loses records; and anything reaching outside the app (`app.Payments`, `app.Email`, `app.Telephony`, `app.Notifications`, a publish, a permanent delete) happens only when a person presses a control or turns on a setting naming it, never as a side effect of an AI turn.

## How the app must look

Every app ships polished on the first build, unasked. Before the first `view.*` call pick a visual direction in one sentence (feel, palette, type, radius, density) and set it with `new IkonTheme { … }`, `["primary"]` included (IKON015).
Then: one primary action per screen in the brand colour; a real type scale; `Card.Elevated` for what matters and one flat colour per card (IKON011); generous padding in a max-width `Layout.Page`; a tinted background; motion only where state changes; designed empty, loading and error states (`EmptyState.*`, `Skeleton.*`, `Alert.*`); every viewport: an `h-screen` root, a `ScrollArea` in a `flex-1 min-h-0` parent, touch targets ≥ 40px.

## Common Pitfalls

Each of these fails the build or misbehaves at runtime. The two most common compile failures:
1. Collection state uses the reactive collections: `ReactiveList<T>`, `ReactiveDictionary<TKey,TValue>`, `ReactiveHashSet<T>` (+ `Client*`/`User*`/`Mount*`/`Persistent*`/`PersistentUser*` variants). A `Reactive<List<T>>` is IKON002.
2. There is NO `view.Keyed(...)` (CS1061). Render list items directly in the loop; `key:` is a named parameter, needed only where a stateful component must keep or drop its state.

### Reactive state

The `reactive-state` guide has the rest ("Rules that are easy to get wrong").

- **Re-render is implicit.** Reading `_x.Value` in a UI lambda registers the dependency, and only the diff is streamed. There is NO `view.Dynamic`/`Watch`/`Bind`/`Observe`/`When`/`Live`/`Reactive` (CS1061).
- **Reactives are `private readonly` fields constructed with the initial value**: `private readonly ClientReactive<int> _count = new(0);`, collections `= new();`. No `app` argument, no `app.ClientReactive(...)` (CS1503/CS7036). A `ClientReactive` of a mutable type needs `ClientReactive.Create(_ => new Settings())`.
- **Mutate collections on the reactive** (`_todos.Add(item)`, `.RemoveAll`, `.ReplaceAll`, `.Update(…)`, `_map[key] = v`) and enumerate it directly. `.Value` is read-only (`.Value.Add` is CS1061).
- **Scope by lifetime**: `Reactive<T>` is shared by all clients; view state (an open menu, an in-flight flag) is `ClientReactive`, typed text and drafts `UserReactive` (a reload is a new client). `Persistent*` is ONE value for the app, `PersistentSession*` one per `SessionIdentity`, `PersistentUser*` one per user; they save only when an instance stops cleanly, so records go in a database.
- **`_x.Value` is the current client's** in a UI lambda or handler. Outside a scope (a loop from `Main`, a timer, an endpoint) name the target: `_x.SetFor(cid, value)`, `_x.ValueFor(cid)` or `ReactiveScope.Use(new ClientScope(cid))`, with `var cid = ReactiveScope.ClientId;` captured in a scope. The user is `ReactiveScope.UserId` (`UserIdOrNull`).
- **Render only reads.** A reactive written inside a render lambda is not propagated; create-if-missing goes in a handler or lifecycle callback.

### AI services

- **`Emerge` is static, and `await Emerge.Run<T>(…)` returns the result** (no `IEmerge`, no `.ResultAsync()`): `var result = await Emerge.Run<MyResult>(LLMModel.Claude55Sonnet, pass => { pass.Command = userPrompt; });`. The prompt goes in `pass.Command`, never positionally (CS1660); never interpolate `pass.JsonSchema`, which is appended for you. The result is a non-null `T`, not a tuple (CS8130). Images, PDFs and prior turns go in a `KernelContext` (`emergence` guide).
- **`Emerge.AskAsync` is prompt-first**: `AskAsync(userPrompt)` or `AskAsync(userPrompt, LLMModel.Claude45Haiku)` (model-first is CS1503). It returns a `string`; `AskAsync<T>(prompt)` a `T` (a `public record`/`class`, never a `struct`).
- **Models.** `LLMModel.Default` is `Claude45Haiku`, cheap and fast, and what the one-shots run on. For reasoning and chat name `Claude55Sonnet`, for the hardest tasks `Claude55Opus` (`ai-models` guide).
- **AI calls never return null — catch instead of guarding.** The services throw `AIException` (`InsufficientCreditsException` when credits run out); Emerge throws `EmergenceStoppedException` when a run ends without a result and otherwise rethrows the generation failure. So `catch (AIException)` around the services, `catch (Exception)` around Emerge.
- **The catch shows a human sentence and a retry, never the exception** (`"Couldn't create the image — try again."`, not `ex.Message` or a model id), keeping the inputs so Retry is one press. A generated result has three states: waiting, failed-with-retry, empty.
- **Disclose AI** with `view.AiDisclosure(AiDisclosureKind.Interaction)` where a conversation with an AI starts, and `kind: AiDisclosureKind.GeneratedContent` beside what a model made.

### The view-call shape

The `ui-components` guide has the rest ("Call-shape details").

- **The leading `string[]` style array is positional; name everything else.** `view.Column(["flex gap-4"], content: v => …)`, `view.Text(["text-lg"], text: "Hi")`, `view.Button(["px-4"], text: "Save", onClick: async () => …)`. Never also write `style:` (CS1744), never a named arg before a positional (CS8323). Lambdas are always named (IKON003).
- **The display string is `text:`** on Text, Button, Link and ActionButton (not `value:`/`label:`). Content-first overloads exist too (`view.Text("Hi")`, `view.Button("Save", onClick: …)`, `view.Alert("Saved", tone: …)`).
- **Form controls take `bind:`** (TextField/TextArea/Select/RadioGroup a `Reactive<string>`, Checkbox/Switch `Reactive<bool>`, Slider `Reactive<double>`) or `value:` + `onValueChange:`; a `value:` alone is read-only. `view.Select` options are `new SelectOption(value, label)`.
- **Slider's style-first overload takes a LIST of thumbs**, `view.Slider([Slider.Default], value: [50.0], onValueChange: async v => …)`, and `bind:`; the scalar one is value-first: `view.Slider(50, style: [Slider.Default], …)`.
- **Handlers.** `onSubmit:` is `Func<string, Task>`, always `async`; `onClick:` takes any parameterless lambda. Only `Box`, `Button`, `Card`, `Image`, `Link`, `TableRow` and `ToolbarButton` take `onClick:` (charts one with `ChartClickArgs`). `onOpenChange` hands a plain `bool`.
- **`ariaLabel:`** is on Box, Button, ActionButton, Link, Icon, Progress, the form fields and the capture/mic buttons; Card, Image (`alt:`), RadioGroup (`label:`), TableRow and ToolbarButton have none (CS1739). It replaces the visible text as the name screen readers and `ikon browse` use: set it only where there is no visible text, or start it with that text (`$"Preview {clip.Title}"`).
- **`props:` needs `new Dictionary<string, object> { … }`**, not target-typed `new()`. Column/Row/Grid take no `props:`.
- **The style array is classes, not CSS**; there is no `inlineStyle:`. A runtime value is an arbitrary value: `$"w-[{pct:0.#}%]"`.
- **Never name an app method or property like a style class** (`Page`, `Text`, `Card`, `Select`, `Button`): it hides the `Theming` class, so `Page.Default` fails (CS0119).
- **Lambda parameters can't be reserved words** (`checked`, `event`, `params`, `lock`, `default`); name it `isChecked`.
- **A ternary of two style arrays needs a target type** (CS0173 on `var`): a `string[]` local, the ternary inside the call, or a conditional element `["p-2", isActive ? "bg-active" : "bg-card"]`. CS1003/CS1525/CS1026 near a style array is an unescaped quote, an unbalanced brace or a reserved-word parameter.
- **Dialog** takes `title:`/`description:`/`content:` and `titleStyle:`/`descriptionStyle:`/`headerStyle:`; **AlertDialog** the same without `headerStyle:`, plus `cancelLabel:`/`actionLabel:`/`onAction:` and `footerStyle:`/`cancelStyle:`/`actionStyle:`. A field's `label:` is a VISIBLE label above it (omit it when the design has none); on Checkbox/Switch/Toggle it is the clickable trailing text.
- **Size enums never go in a style array** (CS0029): `view.Icon(name: "check", size: IconSize.Lg)`, or the class `Icon.Sm`. Buttons bake variant and size into one constant (`Button.PrimaryMd`, `Button.ErrorMd`, `Button.OutlineErrorMd`); no `Button.Danger*`, no `variant:`/`size:`. A token is a string, so even one token goes in an array: `[Button.PrimaryMd]`.
- **There is no** `Text.Xs`, `Tabs.Default` (`Tabs.List`/`Trigger`/`Content`), `SemanticTone.Destructive` (`Error`), `text:` on `ToggleGroupItem` (a `Text` in `content:`) or `title:` on `StatCard` (`label:`).
- **Copy, share and download are `view.ActionButton`** with `options:` `new CopyToClipboardActionOptions { Text = … }`, `ShareActionOptions` or `DownloadFileActionOptions`. There is no `ClientFunctions.CopyToClipboardAsync`/`RunJavaScriptAsync`.

### Custom frontend and real-time

- **Native `view.*` first** (`view.RichTextEditor`; `view.CodeEditor` has no highlighting). For a highlighting editor, a map, a 3D view, a chart library or a signature pad, never ship a worse approximation: follow the `custom-react-node-embed` pattern and write all four parts at once (React component, resolver, `registerModule` in `app.tsx`, C# extension method).
- **Crosswind classes in a `.tsx` file do nothing**; they exist only in C# style arrays. Style a custom component's elements with inline `style={{ … }}`, an imported CSS file or `var(--ikon-…)`, and its root from C#.
- **Real-time MULTI-USER surfaces** (shared whiteboard, multiplayer game) are a custom canvas plus a `schema/<Name>.tp` message: `view.*` has no client-to-client channel, and per-frame `Reactive` diffs flood the connection (`realtime-game-loop` pattern). Single-user drawing is `view.ImageEditorCanvas`.

### Audio, files, media

- **Audio is mixed SERVER-side** and played through one `private Audio Audio { get; } = new(app);` (`Play`, `Speak`, `PlayLive`, microphones), never browser Web Audio; each returns an `AudioPlayback` whose `Completion` yields an outcome instead of throwing. **Video** (cameras, screens) is one `private Video Video { get; } = new(app);` playing on named surfaces: clients render `view.VideoSurface(surface: "stage")` (`audio-video` guide).
- **`FileUpload` `accept:` is a string array** (`["image/*", ".pdf"]`). Anything kept goes to asset storage: `onUploadStart: async args => new FileUploadResult { AssetUri = new AssetUri(AssetClass.CloudFile, path, app.GlobalState.SpaceId) }`, then `onUploadComplete` gets `args.AssetUri` (`media-analysis` guide). Resize an image with `ImageUtils.EncodeJpeg(bytes, maxDimension)`.
- **Never copy video or audio into the container** (disk and RAM are far too small): `ffprobe`/`ffmpeg` read `(await Asset.Instance.GetMetadataAsync(uri)).Url` over HTTP.
- **App files: root `public/` (served by path, `src: "/hero.png"`) and `data/` (private), `app.Files.Public` and `app.Files.Data` at runtime**, whose writes go to the cloud app's storage even in a local run. Never read through `AppContext.BaseDirectory` (`app-structure` guide).

### App structure and platform

- **Keep the scaffold's shape**: `[App] public partial class MyApp(IApp<SessionIdentity, ClientParameters> app)` and its two records.
- **Field initializers may use the primary-constructor `app`** (`private Audio Audio { get; } = new(app);`) but no other instance member (CS0236). Wire events (`app.OnClientJoined(...)`) in `Main()`.
- **`Main()` returns after `UI.Root(...)`**: a Main that never returns (`await Task.Delay(Timeout.Infinite)`) fails the boot.
- **Lifecycle**: `app.OnStarting(async () => …)`, `app.OnStopping`, `app.OnClientJoined(async ctx => …)` (or `(ctx, parameters)`), `app.OnClientLeft`, `app.OnMessageReceived(async msg => …)`, not `(sender, args)` (CS1593).
- **A hot reload starts a new instance** and runs `Main`/`OnStarting` again: reactives are restored, plain fields (engines, caches, counters) are not. In `OnStarting` rebuild them and settle restored in-flight state (a `"recording"` mode, clients that left). End loops on `app.StoppingToken` (`app-structure` guide).
- **Seed demo content** in `app.OnStarting` only when the store is empty, per-user state in `app.OnClientJoined`. `ikon bundle`/`deploy` run `Main` and `OnStarting` against the real databases to capture a boot snapshot: guard side effects with `SnapshotCapture.IsCaptureProcess`.
- **A value presented as unique is GENERATED** (room and invite codes, share slugs, ids) from `Random.Shared` or `Guid`. Demo rows look real, not `Item 1`.
- **Cloud servers run in UTC**: store UTC and show times in the viewer's `ctx.Timezone`.
- **Log with `Log.Instance.Info/Warning/Error/Debug(…)`** (`logging` guide); there is no `app.Logger`, `app.Log` or `Information`.
- **A "Sign in" button drawn in C#** calls `ClientFunctions.LoginShowAsync(reason)`; `LoginAsync(provider)` is OAuth only.
- **Open a database connection PER operation**: `await using var conn = await app.DatabaseAsync("mydb"); await conn.OpenAsync();` in each method, never a field (`databases` guide). A database `"X"` needs `ikon db create --name X`; the built-in one needs nothing.
- **Don't invent a `using Ikon.*;`** (CS0234), but a nested namespace is not imported with its parent (CS0246/CS0103). Outside `GlobalUsings.cs`: `Ikon.Common.Core.Protocol` (`TriggerEventType`, `VideoCodec`, `ServerRunType`), `Ikon.App.Cells`, `Ikon.App.Cron`, `Ikon.App.Mcp`, `Ikon.App.Triggers`, `Ikon.AI.Retrieving`, `Ikon.AI.Reranking`, `Ikon.AI.Emergence.Structured`, `Ikon.AI.Emergence.Tree`, `Ikon.AI.Database`, `Ikon.AI.Storage`, `Ikon.Sdk`, `Ikon.Crosswind`.
- **`System.Globalization`, `System.Diagnostics` and `System.Text.RegularExpressions` need a `using`.**
- **`Context` and the `Ikon.Common.Core.Email` types are already imported.** All of `Ikon.Common.Core.Protocol` makes a bare `Action` ambiguous (CS0104), as `using System.ComponentModel;` does `[Description]`.
- **Connector and pipeline namespaces need their package**, a `PackageReference` named like the namespace (`Ikon.Connectors.Google`, `Ikon.Pipeline`; `pipelines` guide).
- **Name a `using` local**: `using var busy = _busy.AsToken();`. `using var _ = …` declares a variable `_`, so a later `_ = SomeTask();` fails (CS1656).
- **Keep the entry point and the global namespace.** `using` directives precede the top-level `return await App.Run(args);` (CS1529); never delete it (CS5001) or move it to a `Program.cs`. Never add a `namespace` to app files: inside `namespace Ikon.App.<Anything>`, `App` is the `Ikon.App` namespace (CS0234/CS0305/CS8956). Partial files go beside the main one; shared `using`s in `GlobalUsings.cs`.

### Color tokens and theming

- **`bg-primary` is NOT the brand fill** but the PAGE SURFACE (white in light, near-black in dark); the theme key `["primary"]` sets the brand colour that the `brand` classes use. Brand fill is `bg-brand-solid`, brand text `text-brand-secondary`, brand border `border-brand`; neutrals are `bg-background`/`bg-card`, `text-foreground`, `border-secondary`.
- **A hardcoded palette needs `Mode = ThemeMode.Fixed`**, or dark-preference clients get dark-scheme text on your light surfaces; for both schemes set `DarkMode`.
- **Text colour goes on the Text**, after its token: `[Text.Body, "text-white"]`. Only the role tokens take a numeric shade (`brand-100`, `error-700`); `text-tertiary-600` is IKON006.
- **Tailwind class names are v4's**: `shadow-xs`/`shadow-sm`/`rounded-xs`/`rounded-sm` where v3 wrote `shadow-sm`/`shadow`/`rounded-sm`/`rounded`, `outline-hidden`, `ring-3`, `bg-black/50` for `bg-opacity-50` (`styling-and-motion` guide, "Colour tokens").

### C# string literals

- **ASCII `"` delimiters only**: a curly `“”` as a delimiter is CS1056.
- **Escape inner quotes in `$"…"`** (`$"Add card to \"{col}\""`): one unescaped `"` cascades into CS1002/CS1010/CS1026 across the file; fix that quote, not the phantom lines. Many quotes → `$"""…"""`.
- **Raw-string `$` count matches brace nesting**: literal `{}` (JSON in prompts) plus interpolation → `$$"""…"""` with `{{x}}`; interpolation only → `$"""…"""`; static → `"""…"""`. A mismatch is CS9006/CS9007.

### Runtime blank-screen triage

**Blank page after a clean build**: make the root fill the viewport (`view.Column(["h-screen w-full …"], …)`), paint on `bg-background`/`bg-card` under a committed theme, and if a red "Unregistered node type" shows, register the custom module.

## API Reference Guides

Each agent guide is `docs/Ikon.Agent.Docs/guides/<name>.md`, one subject per guide:

- **agent-stages-and-history**: stage machines, pass records, journals and checkpoints of agent threads
- **agent-threads**: long-running agent threads, sub-agents, artifacts, budgets, asking the user
- **ai-advanced**: the low-level AI database and storage
- **ai-image**: image generation, upscaling, depth maps, segmentation and 3D meshes
- **ai-models**: choosing an LLM model; KernelContext
- **ai-speech-and-audio**: text to speech, speech recognition, sound effects and music
- **ai-video**: video generation and enhancement; playing a video file
- **ai-web-and-data**: web search and scraping, classification, decisions, OCR, embeddings, retrieval
- **app-api-reference**: where the signatures of IApp and the host services are
- **app-container**: what the cloud server image has, and bringing other programs, fonts and packages
- **app-structure**: the app class, identity, lifecycle, files, navigation, client functions, join-URL QR
- **asset-system**: asset storage: cloud files and JSON, metadata, expiry, concurrency
- **audio-video**: playing and capturing audio and video, effects, calls, broadcasts, synthesis
- **charts**: pie, bar and line charts
- **component-vocabulary**: the enums, records and event arguments components take
- **consent**: asking for and recording end-user consent
- **costs**: the app's AI costs in credits, per day and per usage event
- **crosswind-reference**: Crosswind styling recipes: layouts, gradients, overlays, effects
- **csharp-primer**: modern C# idioms and the syntax mistakes that fail a build
- **databases**: Postgres, SQL, EF Core, migrations and seed data
- **email**: sending and receiving email
- **emergence-strategies**: advanced Emerge patterns: map-reduce, tree search, critics, ensembles
- **emergence**: Emerge.Run<T>: structured output, tools, chat history, cancellation
- **endpoints-webhooks**: HTTP endpoints, public HTML pages (HttpResult.Html), webhooks, MCP tools, raw sockets and cron methods
- **frontend-fundamentals**: the React frontend: custom components, sign-in, embedding, branding
- **function-registry**: functions the client and LLM tools can call
- **location**: one-off and continuous GPS location
- **logging**: logging from an app
- **media-analysis**: uploading large media and analysing it with ffmpeg
- **motion-reference**: the full motion and keyframe syntax
- **notifications**: push and in-app notifications, inbox, channels
- **parallax-overview**: the Parallax UI library as a whole, SEO and share cards
- **payments**: payments, subscriptions, offers and entitlements
- **pipelines-reference**: the pipeline API
- **pipelines**: background processing pipelines
- **platform-runtime-types**: TOML, run type, name conversions, exceptions and other runtime types
- **product-design**: what each kind of app must contain, and how it must look on the first build
- **profiles-and-roles**: user profiles, roles and custom attributes
- **reactive-state**: shared, per-client, per-user and persistent state; scopes
- **scaling**: instances, shards and cells for many users
- **secrets**: secrets and credentials
- **sso**: enterprise single sign-on
- **styling-and-motion**: Crosswind classes, theme tokens, colour tokens and motion
- **tailwind-reference**: every Tailwind utility Crosswind supports
- **telephony**: SMS and phone calls
- **theming**: IkonTheme: palettes, dark mode and design tokens
- **ui-api-reference**: the parameters every component shares; where signatures are
- **ui-components**: every UI component with examples: layout, inputs, overlays, drag and drop
- **utilities**: retries, embedded resources, fuzzy matching, logger providers


## API Signatures

Exact signatures of every public platform type are in `docs/Ikon.Agent.Docs/api/`, one file per namespace (`Ikon.App.md`; `~2`, `~3` parts where one needs several, listed in `_index.md`), generated from the assemblies, so they win when a guide disagrees. Grep them by type or member name before guessing a signature (CS1739, CS1061). A type's line is indented two spaces and its members four: `OpenDatabaseAsync` is on `AppServices`, not on `app`. Grep can print `[Omitted long matching line]` for a long signature; read the file at that line instead.


## Code Patterns

`docs/Ikon.Agent.Docs/patterns/_index.md` groups patterns from production IKON apps by task, each compiled and render tested: adapt the closest one rather than writing a screen or a flow from scratch.

<!-- ikon-user-content-below -->
