using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WindowsIsland.Services;

/// <summary>A file sent along with a question: a screenshot, or something dropped on the island.</summary>
public sealed record ChatAttachment(string Path, bool IsImage)
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>What to call it in the chat ("Tela: Chrome"); the file name when null.</summary>
    public string? Label { get; init; }

    public static ChatAttachment FromPath(string path) => new(path, ImageExtensions.Contains(System.IO.Path.GetExtension(path)) && File.Exists(path));
}

public sealed class ChatMessage
{
    public required bool FromUser { get; init; }
    public string Text { get; set; } = "";
    public bool IsError { get; set; }
    public IReadOnlyList<ChatAttachment> Attachments { get; init; } = [];
}

/// <summary>
/// "Ask Claude" from the island: runs the Claude Code CLI headless (<c>claude -p</c>, stream-json) with the user's
/// own login, streams the answer in, and keeps one conversation going with <c>--resume</c>.
/// Public members are UI-thread only; <see cref="Changed"/> is raised on the UI thread.
/// </summary>
public sealed class ClaudeChatService : IDisposable
{
    private const string SystemPrompt =
        "Você está respondendo pela Windows Island, uma pílula pequena no topo da tela do Windows do usuário. " +
        "Responda em português do Brasil, curto e direto: poucas frases ou uma lista curta. Evite tabelas, títulos e " +
        "blocos longos de código, a não ser que o usuário peça. Use no máximo **negrito** e `código` como formatação. " +
        "Nesta janela você não consegue pedir permissão: se uma tarefa exigir editar arquivos ou rodar comandos, " +
        "explique o que faria e sugira abrir o Claude Code para executar. Quando houver imagem anexada, ela é a tela " +
        "ou um arquivo que o usuário quer que você veja.";

    private const int MaxImageSide = 1600;

    private readonly Dispatcher _dispatcher;
    private readonly List<ChatMessage> _messages = new();
    private Process? _process;
    private string? _sessionId;
    private bool _changePending;

    public ClaudeChatService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public IReadOnlyList<ChatMessage> Messages => _messages;
    public bool IsRunning => _process is not null;

    /// <summary>What Claude is doing right now ("Pensando…", "Pesquisando na web…").</summary>
    public string Status { get; private set; } = "";

    /// <summary>A finished answer the user hasn't looked at yet.</summary>
    public bool HasUnread { get; set; }

    public event Action? Changed;

    /// <summary>A reply finished (text of the answer). Raised on the UI thread.</summary>
    public event Action<string>? Answered;

    public void Send(string prompt, IReadOnlyList<ChatAttachment> attachments)
    {
        prompt = prompt.Trim();
        if ((prompt.Length == 0 && attachments.Count == 0) || IsRunning)
            return;
        if (prompt.Length == 0)
            prompt = attachments.Any(a => a.IsImage) ? "O que você vê aqui?" : "Dê uma olhada nesse arquivo.";

        _messages.Add(new ChatMessage { FromUser = true, Text = prompt, Attachments = attachments.ToList() });
        var reply = new ChatMessage { FromUser = false };
        _messages.Add(reply);
        HasUnread = false;

        string? claude = FindClaude();
        if (claude is null)
        {
            Fail(reply, "Não encontrei o Claude Code neste computador. Instale a extensão do Claude Code no VS Code ou o CLI (claude.ai/code) e tente de novo.");
            return;
        }

        string input;
        try
        {
            input = BuildInput(prompt, attachments);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Fail(reply, $"Não consegui ler o anexo: {ex.Message}");
            return;
        }

        var start = new ProcessStartInfo(claude)
        {
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // stream-json in as well as out: the question can carry images (screenshots).
        foreach (var arg in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages" })
            start.ArgumentList.Add(arg);
        // The island's own questions shouldn't show up as sessions in the island (the hooks would report them).
        start.ArgumentList.Add("--settings");
        start.ArgumentList.Add("{\"disableAllHooks\":true}");
        start.ArgumentList.Add("--append-system-prompt");
        start.ArgumentList.Add(SystemPrompt);
        // Read-only tools work without asking; the web needs an explicit allowance in headless mode.
        start.ArgumentList.Add("--allowedTools");
        start.ArgumentList.Add("WebSearch");
        start.ArgumentList.Add("WebFetch");
        if (_sessionId is not null)
        {
            start.ArgumentList.Add("--resume");
            start.ArgumentList.Add(_sessionId);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Fail(reply, $"Não consegui abrir o Claude Code: {ex.Message}");
            return;
        }

        _process = process;
        Status = "Pensando…";
        RaiseChanged();
        _ = RunAsync(process, input, reply);
    }

    /// <summary>One stream-json user message: the images, then the text (other files are named by path for Read).</summary>
    private static string BuildInput(string prompt, IReadOnlyList<ChatAttachment> attachments)
    {
        var content = new List<object>();
        foreach (var image in attachments.Where(a => a.IsImage))
            content.Add(new { type = "image", source = new { type = "base64", media_type = "image/png", data = Convert.ToBase64String(EncodeImage(image.Path)) } });

        var text = new StringBuilder(prompt);
        var files = attachments.Where(a => !a.IsImage).ToList();
        if (files.Count > 0)
        {
            text.Append("\n\nArquivos anexados (leia com a ferramenta Read se precisar):");
            foreach (var file in files)
                text.Append("\n- ").Append(file.Path);
        }
        content.Add(new { type = "text", text = text.ToString() });

        return JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content } }) + "\n";
    }

    /// <summary>PNG, scaled down so a 4K screenshot doesn't cost a fortune in tokens.</summary>
    private static byte[] EncodeImage(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        double scale = Math.Min(1, (double)MaxImageSide / Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (scale < 1)
            frame = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Stops the answer in progress.</summary>
    public void Cancel()
    {
        if (_process is not { } process)
            return;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>Forgets the conversation: the next question starts a fresh session.</summary>
    public void NewConversation()
    {
        Cancel();
        _messages.Clear();
        _sessionId = null;
        HasUnread = false;
        Status = "";
        RaiseChanged();
    }

    public void Dispose() => Cancel();

    private async Task RunAsync(Process process, string input, ChatMessage reply)
    {
        var errors = new StringBuilder();
        string? resultText = null;
        bool resultIsError = false;
        var denied = new List<string>();
        try
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();

            var stderr = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                    errors.AppendLine(line);
            });

            bool textInBlock = false;
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length == 0 || line[0] != '{')
                    continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("session_id", out var sid) && sid.GetString() is { Length: > 0 } id)
                    _sessionId = id;

                switch (root.GetProperty("type").GetString())
                {
                    case "stream_event":
                        var ev = root.GetProperty("event");
                        switch (ev.GetProperty("type").GetString())
                        {
                            case "content_block_start":
                                var block = ev.GetProperty("content_block");
                                if (block.GetProperty("type").GetString() == "tool_use")
                                {
                                    SetStatus(DescribeTool(block.GetProperty("name").GetString()));
                                    textInBlock = false;
                                }
                                else if (block.GetProperty("type").GetString() == "text")
                                {
                                    // Text after a tool call starts a new paragraph.
                                    if (reply.Text.Length > 0 && !textInBlock)
                                        Append(reply, "\n\n");
                                    textInBlock = true;
                                    SetStatus("Escrevendo…");
                                }
                                break;
                            case "content_block_delta":
                                var delta = ev.GetProperty("delta");
                                if (delta.GetProperty("type").GetString() == "text_delta")
                                    Append(reply, delta.GetProperty("text").GetString() ?? "");
                                break;
                        }
                        break;

                    case "result":
                        resultIsError = root.TryGetProperty("is_error", out var isError) && isError.GetBoolean();
                        resultText = root.TryGetProperty("result", out var result) ? result.GetString() : null;
                        if (root.TryGetProperty("permission_denials", out var denials) && denials.ValueKind == JsonValueKind.Array)
                            foreach (var d in denials.EnumerateArray())
                                if (d.TryGetProperty("tool_name", out var tool) && tool.GetString() is { } name && !denied.Contains(name))
                                    denied.Add(name);
                        break;
                }
            }

            await process.WaitForExitAsync();
            await stderr;
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        await _dispatcher.InvokeAsync(() =>
        {
            bool cancelled = process.HasExited && process.ExitCode != 0 && resultText is null && errors.Length == 0;
            if (reply.Text.Length == 0 && !string.IsNullOrWhiteSpace(resultText))
                reply.Text = resultText.Trim();
            if (resultIsError || (reply.Text.Length == 0 && !cancelled))
            {
                string detail = !string.IsNullOrWhiteSpace(resultText) ? resultText!.Trim()
                    : errors.ToString().Trim() is { Length: > 0 } err ? err
                    : "O Claude Code terminou sem responder.";
                reply.IsError = true;
                reply.Text = reply.Text.Length > 0 ? reply.Text + "\n\n" + detail : detail;
                App.Log($"Claude (ilha): {detail}");
            }
            else if (cancelled && reply.Text.Length == 0)
            {
                reply.Text = "Resposta interrompida.";
                reply.IsError = true;
            }
            if (denied.Count > 0)
                reply.Text += $"\n\n(Precisaria de permissão para usar {string.Join(", ", denied)}. Para isso, abra o Claude Code.)";

            process.Dispose();
            _process = null;
            Status = "";
            HasUnread = true;
            RaiseChanged();
            if (!reply.IsError)
                Answered?.Invoke(reply.Text);
        });
    }

    private void Append(ChatMessage reply, string text)
    {
        _dispatcher.InvokeAsync(() =>
        {
            reply.Text += text;
            RaiseChangedThrottled();
        });
    }

    private void SetStatus(string status) => _dispatcher.InvokeAsync(() =>
    {
        Status = status;
        RaiseChangedThrottled();
    });

    private void Fail(ChatMessage reply, string message)
    {
        reply.Text = message;
        reply.IsError = true;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>Streaming produces dozens of deltas per second: repaint at most ~20 times a second.</summary>
    private void RaiseChangedThrottled()
    {
        if (_changePending)
            return;
        _changePending = true;
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _changePending = false;
            RaiseChanged();
        };
        timer.Start();
    }

    private static string DescribeTool(string? tool) => tool switch
    {
        "WebSearch" => "Pesquisando na web…",
        "WebFetch" => "Lendo uma página…",
        "Read" => "Lendo um arquivo…",
        "Glob" or "Grep" => "Procurando nos arquivos…",
        _ => $"Usando {tool}…",
    };

    /// <summary>
    /// The Claude Code CLI: on PATH, the standalone install (~/.local/bin), or the binary bundled with the newest
    /// VS Code extension (its folder name changes with every update, so look it up each time).
    /// </summary>
    public static string? FindClaude()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), "claude.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Path.Combine(home, ".local", "bin", "claude.exe");
        if (File.Exists(local))
            return local;

        try
        {
            var extensions = Path.Combine(home, ".vscode", "extensions");
            if (Directory.Exists(extensions))
            {
                return Directory.GetDirectories(extensions, "anthropic.claude-code-*")
                    .Select(d => Path.Combine(d, "resources", "native-binary", "claude.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return null;
    }
}
