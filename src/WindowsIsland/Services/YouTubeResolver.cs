using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsIsland.Core;
using WindowsIsland.Interop;

namespace WindowsIsland.Services;

public sealed record YouTubeVideo(string VideoId, string Title, TimeSpan? Length);

/// <summary>
/// Figures out which YouTube video a browser is playing. Windows' media controls only expose title, channel and
/// duration, so we search YouTube for "title channel" and accept a result only when the title matches exactly and
/// either the duration matches or a browser window title confirms it is YouTube. Ads never match, so they fall back
/// to the normal artwork view.
/// </summary>
public sealed partial class YouTubeResolver
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome", "Edge", "MSEdge", "Brave", "Firefox", "Opera", "Vivaldi", "Arc",
    };

    private readonly Dictionary<string, YouTubeVideo?> _cache = new();
    private readonly HashSet<string> _inFlight = new();

    public static bool IsBrowser(string sourceApp) => Browsers.Contains(sourceApp);

    public static string KeyFor(MediaInfo media) => $"{media.SourceApp}|{media.Title}|{media.Artist}|{(int)media.Duration.TotalSeconds}";

    /// <summary>Cached answer for this media, if resolved. UI thread.</summary>
    public bool TryGet(MediaInfo media, out YouTubeVideo? video) => _cache.TryGetValue(KeyFor(media), out video);

    /// <summary>Resolves in the background and invokes <paramref name="resolved"/> on the UI thread when done. UI thread.</summary>
    public async void Resolve(MediaInfo media, Action resolved)
    {
        string key = KeyFor(media);
        if (_cache.ContainsKey(key) || !_inFlight.Add(key) || !IsBrowser(media.SourceApp))
            return;
        try
        {
            bool windowSaysYouTube = BrowserWindowShowsYouTube(media.Title);
            var video = await Task.Run(() => FindAsync(media, windowSaysYouTube));
            _cache[key] = video;
            resolved();
        }
        catch (Exception ex)
        {
            App.Log($"YouTube: não foi possível identificar \"{media.Title}\": {ex.Message}");
            _cache[key] = null;
        }
        finally
        {
            _inFlight.Remove(key);
        }
    }

    private static async Task<YouTubeVideo?> FindAsync(MediaInfo media, bool windowSaysYouTube)
    {
        string query = Uri.EscapeDataString($"{media.Title} {media.Artist}".Trim());
        string html = await Http.GetStringAsync($"https://www.youtube.com/results?search_query={query}");

        string wanted = Normalize(media.Title);
        foreach (var result in ParseResults(html))
        {
            if (Normalize(result.Title) != wanted)
                continue;
            bool durationMatches = media.Duration > TimeSpan.Zero && result.Length is { } length
                && Math.Abs((length - media.Duration).TotalSeconds) <= 3;
            if (durationMatches || windowSaysYouTube)
                return result;
        }
        return null;
    }

    /// <summary>Reads the video list out of the page's embedded ytInitialData JSON.</summary>
    private static IEnumerable<YouTubeVideo> ParseResults(string html)
    {
        const string marker = "var ytInitialData = ";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            yield break;
        start += marker.Length;
        int end = html.IndexOf(";</script>", start, StringComparison.Ordinal);
        if (end < 0)
            yield break;

        using var doc = JsonDocument.Parse(html.AsMemory(start, end - start));
        var found = new List<YouTubeVideo>();
        Collect(doc.RootElement, found);
        foreach (var video in found)
            yield return video;
    }

    private static void Collect(JsonElement element, List<YouTubeVideo> found)
    {
        if (found.Count >= 10)
            return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("videoRenderer") && ToVideo(property.Value) is { } video)
                        found.Add(video);
                    else
                        Collect(property.Value, found);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, found);
                break;
        }
    }

    private static YouTubeVideo? ToVideo(JsonElement renderer)
    {
        if (!renderer.TryGetProperty("videoId", out var id) || id.GetString() is not { } videoId || !VideoIdRegex().IsMatch(videoId))
            return null;
        string? title = renderer.TryGetProperty("title", out var t) && t.TryGetProperty("runs", out var runs) && runs.GetArrayLength() > 0
            ? string.Concat(runs.EnumerateArray().Select(r => r.TryGetProperty("text", out var text) ? text.GetString() : ""))
            : null;
        if (string.IsNullOrEmpty(title))
            return null;
        TimeSpan? length = renderer.TryGetProperty("lengthText", out var l) && l.TryGetProperty("simpleText", out var simple)
            ? ParseLength(simple.GetString())
            : null;
        return new YouTubeVideo(videoId, title, length);
    }

    private static TimeSpan? ParseLength(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var parts = text.Split(':');
        double seconds = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out int n))
                return null;
            seconds = seconds * 60 + n;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Browsers title their window after the active tab: "Video - YouTube - Google Chrome".</summary>
    private static bool BrowserWindowShowsYouTube(string mediaTitle)
    {
        string wanted = Normalize(mediaTitle);
        return NativeMethods.VisibleWindowTitles().Any(title =>
            title.Contains("YouTube", StringComparison.Ordinal) && Normalize(title).Contains(wanted, StringComparison.Ordinal));
    }

    private static string Normalize(string text) => WhitespaceRegex().Replace(text.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant(), " ");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en;q=0.8");
        // Skips the EU cookie-consent interstitial, which has no results in it.
        client.DefaultRequestHeaders.Add("Cookie", "CONSENT=YES+1");
        return client;
    }

    [GeneratedRegex(@"^[\w-]{11}$")]
    public static partial Regex VideoIdRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
