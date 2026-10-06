using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using WindowsIsland.Core;

namespace WindowsIsland.Services;

/// <summary>
/// Tiny HTTP/1.1 server bound to 127.0.0.1 so any app, script or tool can push activities into the island.
/// Uses a raw TcpListener on loopback, which (unlike HttpListener) needs no URL ACL / admin rights.
/// </summary>
/// <remarks>
/// POST   /notify          → transient notification (auto-expands, 5s by default)
/// POST   /activity        → live activity (stays until DELETE or "duration")
/// DELETE /activity/{id}   → removes an activity
/// GET    /status          → current state
/// </remarks>
public sealed class ApiServer : IDisposable
{
    public const int DefaultPort = 5199;
    private const int MaxHeaderBytes = 16 * 1024;

    // Claude Code hook payloads include tool inputs, e.g. the full content of a file being written.
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Responses are JSON-only (never embedded in HTML), so keep accents readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IslandController _controller;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    private readonly NotificationService? _notifications;
    private readonly ClaudeService? _claude;

    public ApiServer(IslandController controller, int port, NotificationService? notifications = null, ClaudeService? claude = null)
    {
        _controller = controller;
        _notifications = notifications;
        _claude = claude;
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                var request = await ReadRequestAsync(stream, timeout.Token);
                if (request is null)
                {
                    await WriteAsync(stream, 400, new { error = "Requisição inválida" });
                    return;
                }
                var (status, payload) = Route(request);
                await WriteAsync(stream, status, payload);
            }
            catch (ObjectDisposedException)
            {
                // A request arrived while the island was closing (e.g. during an update): nothing to answer.
            }
            catch (Exception ex)
            {
                App.Log(ex);
                try { await WriteAsync(stream, 500, new { error = "Erro interno" }); } catch { }
            }
        }
    }

    private (int Status, object? Payload) Route(HttpRequest request)
    {
        string path = request.Path.TrimEnd('/').ToLowerInvariant();

        switch (request.Method, path)
        {
            case ("OPTIONS", _):
                return (204, null);

            case ("GET", "" or "/status"):
                return (200, Status());

            case ("POST", "/notify"):
            case ("POST", "/activity"):
            {
                ActivityRequest? body;
                try
                {
                    body = JsonSerializer.Deserialize<ActivityRequest>(request.Body, Json);
                }
                catch (JsonException ex)
                {
                    return (400, new { error = $"JSON inválido: {ex.Message}" });
                }
                var activity = body is null ? null : ToActivity(body, notify: path == "/notify");
                if (activity is null)
                    return (400, new { error = "Informe pelo menos 'title' ou 'progress'." });
                _controller.Upsert(activity);
                return (200, new { id = activity.Id });
            }

            // Raw Claude Code hook payload (stdin piped by curl). 204 keeps curl silent: for
            // UserPromptSubmit, anything a hook prints would be added to Claude's context.
            case ("POST", "/claude/hook"):
                _claude?.HandleHook(request.Body);
                return (204, null);

            case ("DELETE", _) when path.StartsWith("/activity/"):
            {
                string id = Uri.UnescapeDataString(request.Path.TrimEnd('/')["/activity/".Length..]);
                return _controller.Remove(id) ? (200, new { removed = id }) : (404, new { error = "Atividade não encontrada" });
            }

            default:
                return (404, new { error = "Rota não encontrada", routes = new[] { "GET /status", "POST /notify", "POST /activity", "DELETE /activity/{id}" } });
        }
    }

    private object Status()
    {
        // Mirrored Windows notifications can be private messages: never hand their text to other local apps.
        var activities = _controller.Snapshot().Where(a => a.Source != "notification").Select(a => new
        {
            a.Id,
            a.Title,
            a.Subtitle,
            a.Icon,
            a.Progress,
            a.Priority,
            a.Source,
        });
        var media = _controller.Media;
        return new
        {
            name = "Windows Island",
            version = typeof(ApiServer).Assembly.GetName().Version?.ToString(3),
            packageIdentity = PackageRegistration.HasIdentity,
            windowsNotifications = (_notifications?.Access ?? NotificationAccess.Unavailable).ToString(),
            activities,
            media =media is null ? null : new { media.Title, media.Artist, media.IsPlaying, source = media.SourceApp },
        };
    }

    private static IslandActivity? ToActivity(ActivityRequest r, bool notify)
    {
        double? progress = r.Progress is { } p && double.IsFinite(p) ? Math.Clamp(p, 0, 1) : null;
        if (string.IsNullOrWhiteSpace(r.Title) && progress is null)
            return null;

        TimeSpan? duration = r.Duration is { } d && double.IsFinite(d) && d > 0
            ? TimeSpan.FromSeconds(Math.Min(d, 86400))
            : notify ? TimeSpan.FromSeconds(5) : null;

        return new IslandActivity
        {
            Id = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N")[..8] : Truncate(r.Id.Trim(), 64)!,
            Title = Truncate(r.Title, 120) ?? "",
            Subtitle = Truncate(r.Subtitle, 300),
            Icon = r.Icon,
            Accent = ParseColor(r.Color),
            Progress = progress,
            Duration = duration,
            Priority = Math.Clamp(r.Priority ?? 50, 0, 99),
            ActionUrl = IsSafeUrl(r.Action) ? r.Action : null,
            Style = string.Equals(r.Style, "level", StringComparison.OrdinalIgnoreCase) ? ActivityStyle.Level : ActivityStyle.Standard,
            ExpandOnArrive = r.Expand ?? notify,
            Source = Truncate(r.Source, 40) ?? "api",
        };
    }

    /// <summary>Only web links: never let an HTTP caller make us launch an arbitrary file or protocol.</summary>
    private static bool IsSafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static Color ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Colors.White;
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch
        {
            return Colors.White;
        }
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[MaxHeaderBytes];
        int read = 0, headerEnd = -1;

        while (headerEnd < 0)
        {
            if (read == header.Length)
                return null;
            int n = await stream.ReadAsync(header.AsMemory(read), ct);
            if (n == 0)
                return null;
            read += n;
            headerEnd = header.AsSpan(0, read).IndexOf("\r\n\r\n"u8);
        }

        var lines = Encoding.ASCII.GetString(header, 0, headerEnd).Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
            return null;

        int contentLength = 0;
        bool expectContinue = false;
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            string name = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(value, out contentLength);
            else if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase))
                expectContinue = value.Equals("100-continue", StringComparison.OrdinalIgnoreCase);
        }
        if (contentLength < 0 || contentLength > MaxBodyBytes)
            return null;

        // Bytes after the header that already arrived belong to the body.
        var body = new byte[contentLength];
        int bodyRead = Math.Min(contentLength, read - (headerEnd + 4));
        header.AsSpan(headerEnd + 4, bodyRead).CopyTo(body);

        // curl asks permission before sending large bodies; answer instead of making it wait a second.
        if (expectContinue && bodyRead < contentLength)
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct);

        while (bodyRead < contentLength)
        {
            int n = await stream.ReadAsync(body.AsMemory(bodyRead), ct);
            if (n == 0)
                break;
            bodyRead += n;
        }

        string path = requestLine[1].Split('?')[0];
        return new HttpRequest(requestLine[0].ToUpperInvariant(), path, Encoding.UTF8.GetString(body, 0, bodyRead));
    }

    private static async Task WriteAsync(Stream stream, int status, object? payload)
    {
        byte[] body = payload is null ? [] : JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        string reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            404 => "Not Found",
            _ => "Internal Server Error",
        };
        string header =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(body);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }

    private sealed record HttpRequest(string Method, string Path, string Body);

    private sealed class ActivityRequest
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public string? Icon { get; set; }
        public string? Color { get; set; }
        public double? Progress { get; set; }
        public double? Duration { get; set; }
        public int? Priority { get; set; }
        public string? Action { get; set; }
        public string? Style { get; set; }
        public bool? Expand { get; set; }
        public string? Source { get; set; }
    }
}
