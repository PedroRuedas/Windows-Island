using System.Windows.Media;
using global::Windows.Media.Control;
using WindowsIsland.Core;

namespace WindowsIsland.Services;

/// <summary>
/// Reads "now playing" from Windows' global media controls (Spotify, browsers, Media Player, ...)
/// and forwards transport commands back to the active app.
/// </summary>
public sealed class MediaService : IDisposable
{
    private readonly object _gate = new();
    private readonly List<GlobalSystemMediaTransportControlsSession> _subscribed = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private volatile GlobalSystemMediaTransportControlsSession? _active;
    private int _version;
    private string? _artworkKey;
    private ImageSource? _artwork;
    private Color _accent = Colors.White;
    private TimeSpan _timelineStart;

    public event Action<MediaInfo?>? Changed;

    public async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += (_, _) => Resubscribe();
        _manager.CurrentSessionChanged += (_, _) => _ = RefreshAsync();
        Resubscribe();
    }

    public Task TogglePlayPauseAsync() => _active is { } s ? s.TryTogglePlayPauseAsync().AsTask() : Task.CompletedTask;
    public Task NextAsync() => _active is { } s ? s.TrySkipNextAsync().AsTask() : Task.CompletedTask;
    public Task PreviousAsync() => _active is { } s ? s.TrySkipPreviousAsync().AsTask() : Task.CompletedTask;

    /// <summary>Jumps to <paramref name="position"/> (Spotify, browsers and most players support it).</summary>
    public Task SeekAsync(TimeSpan position) =>
        _active is { } s ? s.TryChangePlaybackPositionAsync((_timelineStart + position).Ticks).AsTask() : Task.CompletedTask;

    private void Resubscribe()
    {
        lock (_gate)
        {
            foreach (var session in _subscribed)
                Unsubscribe(session);
            _subscribed.Clear();

            if (_manager is null)
                return;

            foreach (var session in _manager.GetSessions())
            {
                session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                _subscribed.Add(session);
            }
        }
        _ = RefreshAsync();
    }

    private void Unsubscribe(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch
        {
            // Session already gone.
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = RefreshAsync();
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => _ = RefreshAsync();

    /// <summary>Prefers whatever is actually playing over Windows' notion of the "current" session.</summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null)
            return null;

        var sessions = _manager.GetSessions();
        foreach (var session in sessions)
        {
            try
            {
                if (session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    return session;
            }
            catch
            {
                // Ignore sessions that died between enumeration and query.
            }
        }
        return _manager.GetCurrentSession() ?? sessions.FirstOrDefault();
    }

    private async Task RefreshAsync()
    {
        int version = Interlocked.Increment(ref _version);
        try
        {
            var session = PickSession();
            _active = session;
            if (session is null)
            {
                Changed?.Invoke(null);
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _version)
                return;

            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            string key = $"{session.SourceAppUserModelId}|{props.Title}|{props.Artist}|{props.AlbumTitle}";
            if (key != _artworkKey)
            {
                var artwork = await ImageLoader.LoadAsync(props.Thumbnail, 160);
                var accent = ImageLoader.AccentFrom(artwork);
                if (version != _version)
                    return;
                _artwork = artwork;
                _accent = accent;
                // Players often publish the thumbnail a moment after the title: keep retrying until it shows up.
                _artworkKey = artwork is null ? null : key;
            }

            if (string.IsNullOrWhiteSpace(props.Title))
            {
                Changed?.Invoke(null);
                return;
            }

            var now = DateTimeOffset.Now;
            var updatedAt = timeline.LastUpdatedTime;
            if (updatedAt.Year < 2000 || updatedAt > now)
                updatedAt = now;

            _timelineStart = timeline.StartTime;
            Changed?.Invoke(new MediaInfo(
                props.Title,
                props.Artist ?? "",
                props.AlbumTitle ?? "",
                _artwork,
                _accent,
                playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                timeline.Position,
                timeline.EndTime - timeline.StartTime,
                updatedAt,
                FriendlyAppName(session.SourceAppUserModelId)));
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x800706BA))
        {
            // RPC_S_SERVER_UNAVAILABLE: the player quit mid-query. Re-attach to whatever sessions remain.
            await Task.Delay(500);
            Resubscribe();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>"Spotify.exe" → "Spotify", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → "ZuneMusic".</summary>
    private static string FriendlyAppName(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
            return "";
        string name = aumid;
        int bang = name.LastIndexOf('!');
        if (bang >= 0)
            name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
            name = name[(dot + 1)..];
        name = name switch
        {
            "msedge" or "MSEdge" => "Edge",
            "ZuneMusic" => "Media Player",
            _ => name,
        };
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var session in _subscribed)
                Unsubscribe(session);
            _subscribed.Clear();
        }
    }
}
