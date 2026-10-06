using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsIsland.Core;

namespace WindowsIsland.Services;

public enum ClaudeSessionState { Idle, Working, Waiting, Done }

public sealed class ClaudeSession
{
    public required string SessionId { get; init; }
    public string Cwd { get; set; } = "";
    public string Project => string.IsNullOrEmpty(Cwd) ? "Claude Code" : Path.GetFileName(Cwd.TrimEnd('\\', '/'));
    public string? Title { get; set; }
    public string? TranscriptPath { get; set; }
    public ClaudeSessionState State { get; set; }
    public string Detail { get; set; } = "";
    public DateTime? TurnStartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed record DayUsage(DateTime Date, long Tokens, int Responses);

/// <summary>Token usage from local transcripts. <see cref="Daily"/> holds the last 7 days, oldest first, today last.</summary>
public sealed record ClaudeUsage(IReadOnlyList<DayUsage> Daily, long TodayOutputTokens, long Last5hTokens)
{
    public static readonly ClaudeUsage Empty = new(
        Enumerable.Range(0, 7).Select(i => new DayUsage(DateTime.Today.AddDays(i - 6), 0, 0)).ToList(), 0, 0);

    public long TodayTokens => Daily[^1].Tokens;
    public int TodayResponses => Daily[^1].Responses;
    public long WeekTokens => Daily.Sum(d => d.Tokens);
    public int WeekResponses => Daily.Sum(d => d.Responses);
}

/// <summary>
/// Claude Code integration:
/// • live session state from Claude Code hooks, POSTed by curl to /claude/hook (see README);
/// • token usage and session titles read from the local transcripts in ~/.claude/projects.
/// </summary>
public sealed partial class ClaudeService : IDisposable
{
    public static readonly Color Orange = Color.FromRgb(0xD9, 0x77, 0x57);
    private static readonly Color Yellow = Color.FromRgb(0xFF, 0xD6, 0x0A);

    private static readonly TimeSpan StaleWorking = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan VisibleFor = TimeSpan.FromHours(3);

    private readonly IslandController _controller;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, ClaudeSession> _sessions = new();
    private readonly CancellationTokenSource _cts = new();

    // Transcript scanning state, owned by the background scan loop.
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime At, long Total, long Output)> _responses = new();
    private readonly Dictionary<string, string> _titles = new();
    private Dictionary<string, string> _uiTitles = new(); // UI-thread copy of _titles

    public ClaudeService(IslandController controller, Dispatcher dispatcher)
    {
        _controller = controller;
        _dispatcher = dispatcher;
    }

    /// <summary>Raised on the UI thread.</summary>
    public event Action? Changed;

    public ClaudeUsage Usage { get; private set; } = ClaudeUsage.Empty;

    /// <summary>True once any hook event arrived, i.e. the hooks are installed.</summary>
    public bool HooksConnected { get; private set; }

    /// <summary>Recent sessions, active ones first. UI thread only.</summary>
    public IReadOnlyList<ClaudeSession> Sessions => _sessions.Values
        .Where(s => s.State is ClaudeSessionState.Working or ClaudeSessionState.Waiting || DateTime.Now - s.UpdatedAt < VisibleFor)
        .OrderBy(s => s.State switch { ClaudeSessionState.Waiting => 0, ClaudeSessionState.Working => 1, _ => 2 })
        .ThenByDescending(s => s.UpdatedAt)
        .ToList();

    public static string ProjectsDirectory =>
        Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "projects");

    public void Start() => _ = ScanLoopAsync();

    // ───────────────────────────── Hooks ─────────────────────────────

    /// <summary>Accepts the raw JSON a Claude Code hook receives on stdin. Any thread.</summary>
    public void HandleHook(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }
        _dispatcher.BeginInvoke(() =>
        {
            using (document)
                Apply(document.RootElement);
        });
    }

    private void Apply(JsonElement root)
    {
        string? eventName = GetString(root, "hook_event_name");
        string? sessionId = GetString(root, "session_id");
        if (eventName is null || sessionId is null)
            return;

        HooksConnected = true;
        var now = DateTime.Now;
        if (!_sessions.TryGetValue(sessionId, out var session))
            _sessions[sessionId] = session = new ClaudeSession { SessionId = sessionId };
        if (GetString(root, "cwd") is { Length: > 0 } cwd)
            session.Cwd = cwd;
        if (GetString(root, "transcript_path") is { Length: > 0 } transcript)
            session.TranscriptPath = transcript;
        if (_uiTitles.TryGetValue(sessionId, out var title))
            session.Title = title;
        session.UpdatedAt = now;

        switch (eventName)
        {
            case "SessionStart":
                session.State = ClaudeSessionState.Idle;
                session.Detail = "Sessão iniciada";
                break;

            case "UserPromptSubmit":
                session.State = ClaudeSessionState.Working;
                session.Detail = "Pensando…";
                session.TurnStartedAt = now;
                session.FinishedAt = null;
                _controller.Remove(DoneAlertId(sessionId));
                break;

            case "PreToolUse":
                if (session.State != ClaudeSessionState.Working)
                    session.TurnStartedAt ??= now;
                session.State = ClaudeSessionState.Working;
                session.Detail = DescribeTool(GetString(root, "tool_name"), root.TryGetProperty("tool_input", out var input) ? input : default);
                break;

            case "PostToolUse":
                // Permission was granted and the tool ran: back to work.
                session.State = ClaudeSessionState.Working;
                session.Detail = "Pensando…";
                break;

            case "Notification":
                OnNotification(session, GetString(root, "message") ?? "");
                break;

            case "Stop":
                OnStop(session);
                break;

            case "SessionEnd":
                _sessions.Remove(sessionId);
                break;
        }
        Changed?.Invoke();
    }

    private void OnNotification(ClaudeSession session, string message)
    {
        // "Claude is waiting for your input" is the idle reminder after a finished turn: not news.
        if (message.Contains("waiting for your input", StringComparison.OrdinalIgnoreCase))
            return;

        session.State = ClaudeSessionState.Waiting;
        var permission = PermissionRegex().Match(message);
        session.Detail = permission.Success ? $"Precisa de permissão: {permission.Groups[1].Value}" : "Aguardando você";

        _controller.Upsert(new IslandActivity
        {
            Id = $"claude.waiting.{session.SessionId}",
            Caption = $"Claude Code · {session.Project}",
            Title = "Claude precisa de você",
            Subtitle = permission.Success ? $"Permitir o uso de {permission.Groups[1].Value}?" : message,
            Icon = "claude",
            Accent = Yellow,
            Duration = TimeSpan.FromSeconds(10),
            Priority = 75,
            ExpandOnArrive = true,
            ActionUrl = VsCodeUrl(session.Cwd),
            Source = "claude",
        });
    }

    private void OnStop(ClaudeSession session)
    {
        var now = DateTime.Now;
        var elapsed = session.TurnStartedAt is { } started ? now - started : (TimeSpan?)null;
        session.State = ClaudeSessionState.Done;
        session.FinishedAt = now;
        session.Detail = elapsed is { } e ? $"Concluído em {FormatDuration(e)}" : "Concluído";
        _controller.Remove($"claude.waiting.{session.SessionId}");

        string id = DoneAlertId(session.SessionId);
        string caption = $"Claude Code · {session.Project}" + (elapsed is { } d ? $" · {FormatDuration(d)}" : "");
        string? transcript = session.TranscriptPath;
        string? cwd = session.Cwd;

        // The final message may still be flushing to the transcript; read it shortly after.
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            string? summary = transcript is null ? null : ReadLastAssistantText(transcript);
            _controller.Upsert(new IslandActivity
            {
                Id = id,
                Caption = caption,
                Title = "Claude terminou",
                Subtitle = summary ?? "A resposta está pronta.",
                Icon = "claude",
                Accent = Orange,
                Duration = TimeSpan.FromSeconds(8),
                Priority = 65,
                ExpandOnArrive = true,
                ActionUrl = VsCodeUrl(cwd),
                Source = "claude",
            });
        });
    }

    /// <summary>Marks interrupted turns (Esc doesn't fire Stop) as idle. Call periodically on the UI thread.</summary>
    public void Tick()
    {
        bool changed = false;
        foreach (var session in _sessions.Values)
        {
            if (session.State is ClaudeSessionState.Working && DateTime.Now - session.UpdatedAt > StaleWorking)
            {
                session.State = ClaudeSessionState.Idle;
                session.Detail = "Parado";
                changed = true;
            }
        }
        if (changed)
            Changed?.Invoke();
    }

    private static string DescribeTool(string? tool, JsonElement input)
    {
        string? Field(string name) => input.ValueKind == JsonValueKind.Object ? GetString(input, name) : null;
        static string FileName(string? path) => string.IsNullOrEmpty(path) ? "arquivo" : Path.GetFileName(path);

        return tool switch
        {
            null => "Trabalhando…",
            "Bash" or "PowerShell" => "Executando: " + Shorten(Field("description") ?? Field("command")),
            "Edit" or "MultiEdit" => "Editando " + FileName(Field("file_path")),
            "NotebookEdit" => "Editando " + FileName(Field("notebook_path")),
            "Write" => "Criando " + FileName(Field("file_path")),
            "Read" => "Lendo " + FileName(Field("file_path")),
            "Grep" or "Glob" => "Pesquisando no código",
            "WebSearch" or "WebFetch" => "Pesquisando na web",
            "Task" or "Agent" => "Delegando: " + Shorten(Field("description")),
            "TodoWrite" => "Organizando tarefas",
            _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => "Usando " + (tool.Split("__").ElementAtOrDefault(1) ?? tool),
            _ => "Usando " + tool,
        };
    }

    // ───────────────────────────── Transcripts ─────────────────────────────

    private async Task ScanLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                var usage = await Task.Run(Scan);
                var titles = new Dictionary<string, string>(_titles);
                await _dispatcher.InvokeAsync(() =>
                {
                    Usage = usage;
                    _uiTitles = titles;
                    foreach (var session in _sessions.Values)
                        if (titles.TryGetValue(session.SessionId, out var title))
                            session.Title = title;
                    Changed?.Invoke();
                });
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false));
    }

    /// <summary>Reads only what was appended since the last scan, so multi-MB transcripts stay cheap.</summary>
    private ClaudeUsage Scan()
    {
        var now = DateTime.Now;
        // A week of history for the daily/weekly view (plus a day of slack for time zones).
        var horizon = now.Date.AddDays(-7);
        string root = ProjectsDirectory;
        if (!Directory.Exists(root))
            return ClaudeUsage.Empty;

        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
        {
            if (file.LastWriteTime < horizon)
                continue;
            try
            {
                ReadAppended(file.FullName, file.Length);
            }
            catch (IOException)
            {
                // Being written right now; next scan picks it up.
            }
        }

        foreach (var stale in _responses.Where(r => r.Value.At < horizon).Select(r => r.Key).ToList())
            _responses.Remove(stale);

        var today = now.Date;
        var last5h = now.AddHours(-5);
        var daily = new DayUsage[7];
        for (int i = 0; i < daily.Length; i++)
            daily[i] = new DayUsage(today.AddDays(i - 6), 0, 0);
        long recentTokens = 0, todayOutput = 0;
        foreach (var (at, total, output) in _responses.Values)
        {
            int day = 6 - (int)(today - at.Date).TotalDays;
            if (day is >= 0 and < 7)
                daily[day] = daily[day] with { Tokens = daily[day].Tokens + total, Responses = daily[day].Responses + 1 };
            if (at.Date == today)
                todayOutput += output;
            if (at >= last5h)
                recentTokens += total;
        }
        return new ClaudeUsage(daily, todayOutput, recentTokens);
    }

    private void ReadAppended(string path, long length)
    {
        _offsets.TryGetValue(path, out long offset);
        if (length < offset)
            offset = 0; // File was rewritten.
        if (length == offset)
            return;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[length - offset];
        int read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);

        // Only consume complete lines; a half-written last line is read again next time.
        int end = Array.LastIndexOf(bytes, (byte)'\n', Math.Max(0, read - 1));
        if (end < 0)
            return;
        _offsets[path] = offset + end + 1;

        foreach (var line in Encoding.UTF8.GetString(bytes, 0, end).Split('\n'))
        {
            if (line.Contains("\"ai-title\"", StringComparison.Ordinal))
                ParseTitle(line);
            else if (line.Contains("\"usage\"", StringComparison.Ordinal) && line.Contains("\"assistant\"", StringComparison.Ordinal))
                ParseResponse(line);
        }
    }

    private void ParseTitle(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (GetString(doc.RootElement, "sessionId") is { } id && GetString(doc.RootElement, "aiTitle") is { } title)
                _titles[id] = title;
        }
        catch (JsonException) { }
    }

    private void ParseResponse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (GetString(root, "type") != "assistant" || !root.TryGetProperty("message", out var message))
                return;
            // A response with several content blocks is written as several lines with the same id.
            if (GetString(message, "id") is not { } id || _responses.ContainsKey(id))
                return;
            if (!message.TryGetProperty("usage", out var usage) || !DateTime.TryParse(GetString(root, "timestamp"), out var at))
                return;

            long Tokens(string name) => usage.TryGetProperty(name, out var v) && v.TryGetInt64(out long n) ? n : 0;
            long output = Tokens("output_tokens");
            long total = Tokens("input_tokens") + output + Tokens("cache_creation_input_tokens") + Tokens("cache_read_input_tokens");
            _responses[id] = (at.ToLocalTime(), total, output);
        }
        catch (JsonException) { }
    }

    private static string? ReadLastAssistantText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - 512 * 1024);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n');

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (!lines[i].Contains("\"assistant\"", StringComparison.Ordinal) || !lines[i].Contains("\"text\"", StringComparison.Ordinal))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(lines[i]);
                    if (GetString(doc.RootElement, "type") != "assistant"
                        || !doc.RootElement.TryGetProperty("message", out var message)
                        || !message.TryGetProperty("content", out var content)
                        || content.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var block in content.EnumerateArray().Reverse())
                        if (GetString(block, "type") == "text" && GetString(block, "text") is { Length: > 0 } text)
                            return Summarize(text);
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
        return null;
    }

    /// <summary>First meaningful line of a markdown answer, without markup.</summary>
    private static string Summarize(string markdown)
    {
        foreach (var raw in markdown.Split('\n'))
        {
            string line = MarkupRegex().Replace(raw, "").Trim();
            if (line.Length > 0 && !line.StartsWith("```", StringComparison.Ordinal) && !line.StartsWith('|'))
                return Shorten(line, 160);
        }
        return Shorten(markdown.Trim(), 160);
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    public static string? VsCodeUrl(string? cwd) =>
        string.IsNullOrEmpty(cwd) ? null : "vscode://file/" + cwd.Replace('\\', '/');

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{Math.Max(1, t.Seconds)}s";

    private static string Shorten(string? text, int max = 48)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "…";
        string line = text.Trim().Split('\n')[0].Trim();
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }

    private static string DoneAlertId(string sessionId) => $"claude.done.{sessionId}";

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"needs your permission to use (.+?)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex PermissionRegex();

    [GeneratedRegex(@"[*_`#>]+|\[(?=[^\]]*\]\()|\]\([^)]*\)")]
    private static partial Regex MarkupRegex();

    public void Dispose() => _cts.Cancel();
}
