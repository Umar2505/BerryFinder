return await App.Run(args);

// Which clients share an app instance: one per user. Leave it empty to share one instance.
public record SessionIdentity(string? UserId);

// Per-client data, mapped from URL query params (`?name=Alice` -> `Name`).
public record ClientParameters(string Name);

public record Picture(byte[] Data, string MimeType, string Name = "image");

public record ChatMessage(bool FromUser, string Text, IReadOnlyList<Picture> Pictures);

// What went wrong, and the message Try again resends when there is one
public record Problem(string Message, ChatMessage? Retry = null);

[App]
public partial class BerryfinderApp(IApp<SessionIdentity, ClientParameters> app)
{
    private UI UI { get; } = new(app, new IkonTheme
    {
        ["background"] = "#f3f1ec",
        ["foreground"] = "#151412",
        ["card"] = "#ffffff",
        ["muted-foreground"] = "#5f5b54",
        ["border"] = "#e4e0d8",
        ["primary"] = "#ff2a8c",
        ["font-heading"] = "Anton",
        ["radius"] = "rounded-2xl",

        DarkMode = new IkonTheme
        {
            ["background"] = "#151412",
            ["foreground"] = "#f3f1ec",
            ["card"] = "#1d1c1a",
            ["muted-foreground"] = "#a8a39a",
            ["border"] = "#2a2926",
        },
    });

    private Audio Audio { get; } = new(app);

    // The model caps an image at 5 MB after base64 encoding, which grows it by a third
    private const long MaxImageBytes = 3_500_000;

    // The image types the model reads; others (HEIC, SVG) would fail on every retry
    private static readonly string[] ImageTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    // Message blocks, not exchanges: images ride along in every turn, so the history stays short
    private const int ContextBlocks = 20;

    private static readonly string[] Suggestions = ["Draw a lighthouse at dusk", "What can you help me with?"];

    private readonly UserReactive<string> _draft = new("");
    private readonly UserReactiveList<Picture> _attached = new();
    private readonly UserReactiveList<ChatMessage> _messages = new();
    private readonly UserReactive<KernelContext> _context = new(new KernelContext());
    private readonly UserReactive<bool> _busy = new(false);
    private readonly UserReactive<Problem?> _problem = new(null);

    public async Task Main()
    {
        // Short push-to-talk clips leave Whisper guessing the language; Scribe identifies it better
        Audio.UseSpeechRecognition(SpeechRecognizerModel.ElevenScribe2);
        Audio.SpeechRecognizedAsync += async args => await OnSpeechAsync(args.Text);
        Audio.SpeechNotRecognizedAsync += async args => ShowProblem(args.Reason == SpeechNotRecognizedReason.NoSignal
            ? "No sound from your mic. Check which mic your device uses."
            : "Didn't catch that. Try again.");

        UI.Root([Page.Default], content: view =>
        {
            view.Column(["h-screen w-full bg-background text-foreground"], content: page =>
            {
                page.ChatLog(["h-full w-full max-w-2xl mx-auto px-6 py-8 md:py-12 gap-6"],
                    autoScrollKey: _messages.Count,
                    header: RenderHeader,
                    content: list => list.Column(["gap-6 pb-2"], content: RenderMessages),
                    footer: RenderInput);
            });
        });
    }

    private void RenderHeader(UIView view)
    {
        // The client list is reactive, so the count re-renders as people join and leave
        var here = app.ReactiveGlobalState.GetHumanClients().Count();

        view.Column(["shrink-0 gap-4"], content: header =>
        {
            header.Row(["justify-end items-center gap-2"], content: row =>
            {
                row.Box(["w-2 h-2 rounded-full bg-brand-solid"]);
                row.Text([Text.Caption, "text-muted-foreground"], text: $"{here} here now");
            });

            header.Text(["font-heading uppercase leading-[0.9] text-[18vw] sm:text-8xl"], text: "Untitled.");
        });
    }

    private void RenderMessages(UIView view)
    {
        if (_messages.Count == 0 && !_busy.Value)
        {
            view.Row(["flex-wrap gap-2"], content: row =>
            {
                foreach (var suggestion in Suggestions)
                {
                    row.Button([Button.OutlineMd, "rounded-full"], text: suggestion, onClick: async () => await SendAsync(suggestion, []));
                }
            });
        }

        foreach (var message in _messages)
        {
            view.Column([message.FromUser ? "gap-2 items-end" : "gap-2"], content: column =>
            {
                foreach (var picture in message.Pictures)
                {
                    column.Image([message.FromUser ? "w-32 rounded-2xl" : "w-full max-w-md rounded-2xl"], data: picture.Data, mimeType: picture.MimeType, alt: picture.Name);
                }

                if (message.FromUser && message.Text.Length > 0)
                {
                    column.Text([Text.Body, "text-muted-foreground"], text: message.Text);
                }
                else if (!message.FromUser)
                {
                    // Models answer in Markdown
                    column.Markdown(["default", Text.Body, "leading-relaxed"], content: message.Text);

                    column.Button(["self-start -ml-1 -mt-1 p-1 rounded-md bg-transparent border-0 text-muted-foreground opacity-60 hover:opacity-100 pointer-coarse:opacity-100"],
                        text: "Read aloud",
                        onClick: async () => await ReadAloudAsync(message.Text),
                        content: icon => icon.Icon([Icon.Sm], name: "volume-2"));
                }
            });
        }

        if (_busy.Value)
        {
            view.Spinner();
        }
    }

    private void RenderInput(UIView view)
    {
        view.Column(["shrink-0 gap-3"], content: input =>
        {
            if (_problem.Value is { } problem)
            {
                input.Alert(problem.Message, tone: SemanticTone.Error, onDismiss: async () => _problem.Value = null, content: alert =>
                {
                    if (problem.Retry is { } retry)
                    {
                        alert.Button([Button.OutlineMd, "mt-3 self-start"], text: "Try again", onClick: async () => await SendAsync(retry.Text, retry.Pictures));
                    }
                });
            }

            input.Composer(
                style: ["default", "rounded-3xl bg-card border-0 pl-3"],
                fieldStyle: ["default", "text-base"],
                value: _draft.Value,
                placeholder: "Ask anything, ask for a picture, or drop one in…",
                busy: _busy.Value,
                attachments: [.. _attached.Select(p => new ComposerAttachment(p.Name, p.MimeType, p.Data.Length))],
                accept: ImageTypes,
                onValueChange: async text => _draft.Value = text,
                onSubmit: async text =>
                {
                    if (_busy.Value)
                    {
                        return;
                    }

                    var pictures = _attached.ToList();
                    _draft.Value = "";
                    _attached.Clear();
                    await SendAsync(text, pictures);
                },
                onAttachmentAdded: async args =>
                {
                    if (args.LocalTempFilePath is not { } path)
                    {
                        return;
                    }

                    try
                    {
                        // Checked here rather than with maxFileSize, which skips a file without telling anyone
                        if (args.Size <= MaxImageBytes)
                        {
                            _attached.Add(new Picture(await File.ReadAllBytesAsync(path), args.MimeType, args.FileName));
                        }
                        else
                        {
                            ShowProblem($"{args.FileName} is too large. Images up to 3.5 MB work.");
                        }
                    }
                    finally
                    {
                        File.Delete(path);
                    }
                },
                onAttachmentRemoved: async index => _attached.RemoveAt(index),
                onAttachmentError: async args => ShowProblem($"Couldn't attach {args.FileName}, try again"),
                // The mic only renders when both capture callbacks are wired; transcripts arrive through Audio.SpeechRecognizedAsync
                onCaptureStart: async _ => { },
                onCaptureStop: async _ => { });
        });
    }

    private async Task OnSpeechAsync(string transcript)
    {
        // Never drop what was said: while a reply runs, or when something is already typed, it joins the draft
        if (_busy.Value || _draft.Value.Length > 0 || _attached.Count > 0)
        {
            _draft.Value = $"{_draft.Value} {transcript}".Trim();
            return;
        }

        await SendAsync(transcript, []);
    }

    // Keeps a pending retry: a failed message lives only there, so a later problem must not drop it
    private void ShowProblem(string message) => _problem.Value = new Problem(message, _problem.Value?.Retry);

    private async Task ReadAloudAsync(string text)
    {
        int clientId = ReactiveScope.ClientId;

        // Plays only to the client that asked, not to everyone connected
        var line = Audio.Speak(MediaTargets.To([clientId]), text);

        if (await line.Completion == AudioPlaybackOutcome.Failed)
        {
            // The reply stays on screen to read, so a failed playback only needs saying
            Log.Instance.Warning($"Read aloud failed for client {clientId}: {line.Error?.Message}");
            ShowProblem("Couldn't play that");
        }
    }

    private async Task SendAsync(string text, IReadOnlyList<Picture> pictures)
    {
        if (_busy.Value || (string.IsNullOrWhiteSpace(text) && pictures.Count == 0))
        {
            return;
        }

        using var busy = _busy.AsToken();
        var question = new ChatMessage(true, text.Trim(), pictures);
        _messages.Add(question);
        _problem.Value = null;

        IMessagePart[] parts = [.. pictures.Select(p => (IMessagePart)new ImagePart(p.Data, p.MimeType))];

        if (question.Text.Length > 0)
        {
            parts = [new TextPart(question.Text), .. parts];
        }

        var drawn = new List<Picture>();

        try
        {
            var (reply, updated) = await Emerge.Run<string>(LLMModel.Claude45Haiku, _context.Value.Add(new MessageBlock(MessageBlockRole.User, parts)), pass =>
            {
                pass.SystemPrompt = "You are a friendly assistant. Answer briefly in Markdown. When the user wants a picture, call generate_image.";
                pass.AddTool(Tool.Of("generate_image", "Generate an image and show it to the user",
                    async ([Description("A detailed description of the image")] string description) =>
                    {
                        try
                        {
                            var image = await ImageGenerator.GenerateAsync(description);
                            drawn.Add(new Picture(await image.GetDataAsync(), image.MimeType, description));
                            return "The image is now shown to the user";
                        }
                        catch (AIException e)
                        {
                            // The model hears a plain failure and tells the user, never the provider's error text
                            Log.Instance.Warning($"Image generation failed for client {ReactiveScope.ClientIdOrNull}: {e.Message}");
                            return "Image generation failed. Tell the user briefly and suggest trying again";
                        }
                    }));
            }).FinalAsync();

            if (string.IsNullOrWhiteSpace(reply) && drawn.Count == 0)
            {
                throw new InvalidOperationException("The model returned no reply");
            }

            _context.Value = updated.TrimToLastMessages(ContextBlocks);
            _messages.Add(new ChatMessage(false, reply ?? "", drawn));
        }
        catch (Exception e)
        {
            // Keep the message so Try again resends it as it was
            Log.Instance.Warning($"Reply failed for client {ReactiveScope.ClientIdOrNull}: {e.GetType().Name}: {e.Message}");
            // Busy guarantees nothing was added after the question
            _messages.RemoveAt(_messages.Count - 1);
            _problem.Value = new Problem("Couldn't answer that", question);
        }
    }
}
