using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowsIsland.Controls;
using WindowsIsland.Core;
using WindowsIsland.Interop;
using WindowsIsland.Services;

namespace WindowsIsland;

public partial class MainWindow : Window
{
    private enum ViewKind { Idle, IdleExpanded, Compact, MediaExpanded, ActivityExpanded, ClaudeExpanded, NotificationsExpanded }

    /// <summary>
    /// Something you can switch to in the expanded island (Spotify, Claude, notifications, an API activity).
    /// Active pages can also occupy the compact island or the side bubble.
    /// </summary>
    private sealed record Page(string Key, ViewKind View, string Glyph, ImageSource? Image, Color Accent, bool IsActive, int Priority,
        IslandActivity? Activity = null);

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Color Yellow = Color.FromRgb(0xFF, 0xD6, 0x0A);
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    private const double PagerHeight = 36;

    private readonly IslandController _controller;
    private readonly MediaService _media = new();
    private readonly VolumeService _volume = new();
    private readonly BatteryService _battery;
    private readonly NotificationService _notifications;
    private readonly ClaudeService _claude;
    private readonly YouTubeResolver _youtube = new();
    private readonly HashSet<string> _unembeddable = new();
    private YouTubeMirror? _mirror;
    private bool _videoShown;
    private ApiServer? _api;

    // Island geometry is driven by springs instead of storyboards for the Apple-like overshoot.
    private readonly Spring _width = new(140), _height = new(34), _radius = new(17), _bubble = new(0);
    private readonly RectangleGeometry _clip = new();
    private readonly Dictionary<ViewKind, FrameworkElement> _views;
    private bool _animating;
    private TimeSpan _lastFrame;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _hoverDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _leaveDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private readonly DispatcherTimer _mediaPeekTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private ViewKind _view = ViewKind.Idle;
    private bool _hoverExpanded, _peeking, _peekingMedia, _equalizerRunning, _pagerVisible;
    private int _tickCount;
    private readonly List<(Rectangle Bar, ScaleTransform Scale)> _equalizerBars = new();
    private string? _lastTrack;
    private readonly DateTime _startedAt = DateTime.Now;

    // Paging state.
    private List<Page> _pages = new();
    private string? _selectedKey;     // Page the user picked (tabs, wheel, bubble).
    private string? _shownKey;        // What is on screen right now (page key or alert id).
    private IslandActivity? _shownAlert;
    private Page? _shownPage;
    private Page? _bubblePage;
    private string? _pagerSignature, _claudeSignature, _notificationSignature;
    private double _pagerWidth;

    public MainWindow()
    {
        InitializeComponent();

        _controller = new IslandController(Dispatcher);
        _battery = new BatteryService(_controller);
        _notifications = new NotificationService(_controller);
        _claude = new ClaudeService(_controller, Dispatcher);
        _views = new()
        {
            [ViewKind.Idle] = IdleView,
            [ViewKind.IdleExpanded] = ClockView,
            [ViewKind.Compact] = CompactView,
            [ViewKind.MediaExpanded] = MediaView,
            [ViewKind.ActivityExpanded] = ActivityView,
            [ViewKind.ClaudeExpanded] = ClaudeView,
            [ViewKind.NotificationsExpanded] = NotificationsView,
        };
        foreach (var view in _views.Values)
        {
            // Scale (for the blur-in entrance) + translate (for sliding between pages), anchored at the top.
            view.RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } };
            view.RenderTransformOrigin = new Point(0.5, 0);
        }

        Stage.Clip = _clip;
        VideoHost.Clip = new RectangleGeometry(new Rect(0, 0, VideoHost.Width, VideoHost.Height), 18, 18);
        BuildEqualizer(CompactEqualizer, 16);
        BuildEqualizer(MediaEqualizer, 18);
        ApplyGeometry();

        _controller.Changed += Refresh;
        _controller.Arrived += _ => Peek();
        _notifications.Changed += Refresh;
        _claude.Changed += Refresh;
        _tick.Tick += (_, _) => OnTick();
        _hoverDelay.Tick += (_, _) =>
        {
            _hoverDelay.Stop();
            // Open on whatever the compact island was showing.
            if (_view == ViewKind.Compact && _shownPage is not null)
                _selectedKey = _shownPage.Key;
            _hoverExpanded = true;
            Refresh();
        };
        _leaveDelay.Tick += (_, _) =>
        {
            // WPF sometimes reports a leave that didn't happen (e.g. on mouse wheel over a no-activate
            // window). Trust the real cursor: keep polling until it is actually outside the island.
            if (IsCursorOverIsland())
                return;
            _leaveDelay.Stop();
            _hoverExpanded = false;
            Refresh();
        };
        _peekTimer.Tick += (_, _) => { _peekTimer.Stop(); _peeking = false; Refresh(); };
        _mediaPeekTimer.Tick += (_, _) => { _mediaPeekTimer.Stop(); _peekingMedia = false; Refresh(); };

        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.MakeOverlayWindow(new WindowInteropHelper(this).Handle);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Reposition();
        UpdateClock();
        Refresh();
        _tick.Start();

        _battery.Start();
        _claude.Start();

        try
        {
            _volume.Changed += OnVolumeChanged;
            _volume.Start();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        int port = int.TryParse(Environment.GetEnvironmentVariable("WINDOWS_ISLAND_PORT"), out var p) ? p : ApiServer.DefaultPort;
        try
        {
            _api = new ApiServer(_controller, port, _notifications, _claude);
            _api.Start();
        }
        catch (SocketException ex)
        {
            App.Log($"API indisponível na porta {port}: {ex.Message}");
            _api = null;
        }

        try
        {
            _media.Changed += _controller.SetMedia;
            await _media.InitializeAsync();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        var access = await _notifications.StartAsync();
        if (access == NotificationAccess.Denied)
        {
            _controller.Upsert(new IslandActivity
            {
                Id = "system.notifications",
                Title = "Permita o acesso às notificações",
                Subtitle = "Clique para abrir as configurações de privacidade do Windows.",
                Icon = "bell",
                Accent = Color.FromRgb(0xFF, 0x9F, 0x0A),
                Duration = TimeSpan.FromSeconds(10),
                ExpandOnArrive = true,
                ActionUrl = "ms-settings:privacy-notifications",
                Source = "system",
            });
        }
        else if (access == NotificationAccess.Allowed && Environment.GetCommandLineArgs().Contains(PackageRegistration.RestartArgument))
        {
            _controller.Upsert(new IslandActivity
            {
                Id = "system.notifications",
                Title = "Notificações do Windows ativadas",
                Subtitle = "As próximas notificações vão aparecer aqui na ilha.",
                Icon = "check",
                Accent = Green,
                Duration = TimeSpan.FromSeconds(5),
                ExpandOnArrive = true,
                Source = "system",
            });
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _tick.Stop();
        _battery.Stop();
        _notifications.Enabled = false;
        _claude.Dispose();
        _api?.Dispose();
        _media.Dispose();
        _volume.Dispose();
        base.OnClosing(e);
    }

    // ───────────────────────────── Pages ─────────────────────────────

    private List<Page> BuildPages(MediaInfo? media)
    {
        var pages = new List<Page>();

        if (media is not null)
            pages.Add(new("media", ViewKind.MediaExpanded, Icons.Resolve("music"), media.Artwork, media.Accent, media.IsPlaying, 20));

        var sessions = _claude.Sessions;
        if (sessions.Count > 0 || _claude.Usage.TodayResponses > 0)
        {
            bool waiting = sessions.Any(s => s.State == ClaudeSessionState.Waiting);
            bool working = sessions.Any(s => s.State == ClaudeSessionState.Working);
            pages.Add(new("claude", ViewKind.ClaudeExpanded, Icons.Resolve("claude"), null,
                waiting ? Yellow : ClaudeService.Orange, working || waiting, waiting ? 70 : 40));
        }

        if (_notifications.History.Count > 0)
            pages.Add(new("notifications", ViewKind.NotificationsExpanded, Icons.Resolve("bell"), null,
                NotificationService.Blue, _notifications.HasRecentUnread, 30));

        foreach (var activity in _controller.PersistentActivities)
            pages.Add(new($"activity:{activity.Id}", ViewKind.ActivityExpanded, Icons.Resolve(activity.Icon), activity.Image,
                activity.Accent, true, activity.Priority, activity));

        return pages;
    }

    private Page? FindPage(string? key) => key is null ? null : _pages.FirstOrDefault(p => p.Key == key);

    /// <summary>The user's pick if it is live, otherwise the most important live page.</summary>
    private Page? PickCompactPage()
    {
        var active = _pages.Where(p => p.IsActive).ToList();
        return active.FirstOrDefault(p => p.Key == _selectedKey) ?? active.OrderByDescending(p => p.Priority).FirstOrDefault();
    }

    private void SelectPage(string key)
    {
        _selectedKey = key;
        Refresh();
    }

    // ───────────────────────────── State → view ─────────────────────────────

    private void Refresh()
    {
        var alert = _controller.CurrentAlert;
        // While you're browsing the open island, new messages land in the Notifications page instead of
        // hijacking the view (a busy WhatsApp group would otherwise flip it back and forth every second).
        if (_hoverExpanded && alert?.Source == "notification")
            alert = null;
        var media = _controller.Media;
        DetectTrackChange(media);
        _pages = BuildPages(media);

        Page? page = null;
        ViewKind kind;
        if (alert is not null && (_hoverExpanded || _peeking))
            kind = ViewKind.ActivityExpanded;
        else if (alert is not null)
            kind = ViewKind.Compact;
        else if (_hoverExpanded)
        {
            page = FindPage(_selectedKey) ?? PickCompactPage() ?? _pages.FirstOrDefault();
            kind = page?.View ?? ViewKind.IdleExpanded;
        }
        else if (_peekingMedia && FindPage("media") is { } mediaPage)
        {
            page = mediaPage;
            kind = ViewKind.MediaExpanded;
        }
        else if (PickCompactPage() is { } compactPage)
        {
            page = compactPage;
            kind = ViewKind.Compact;
        }
        else
            kind = ViewKind.Idle;

        // Slide left/right when moving between pages.
        int direction = 0;
        if (page is not null && _shownPage is not null && page.Key != _shownPage.Key && kind != ViewKind.Compact)
            direction = Math.Sign(_pages.FindIndex(p => p.Key == page.Key) - _pages.FindIndex(p => p.Key == _shownPage.Key));

        _shownAlert = page is null && kind is ViewKind.Compact or ViewKind.ActivityExpanded ? alert : null;
        _shownPage = page;

        if (_shownAlert is not null)
        {
            if (kind == ViewKind.Compact)
                FillCompact(_shownAlert);
            else
                FillActivity(_shownAlert);
        }
        else if (page is not null)
        {
            if (kind == ViewKind.Compact)
                FillCompactPage(page, media);
            else
                FillExpandedPage(page, media);
        }

        bool claudeWorking = _claude.Sessions.Any(s => s.State == ClaudeSessionState.Working);
        SetEqualizer(media?.IsPlaying == true && page?.Key == "media");
        SetSpin(CompactIconSpin, kind == ViewKind.Compact && page?.Key == "claude" && claudeWorking);
        SetSpin(ClaudeHeaderSpin, kind == ViewKind.ClaudeExpanded && claudeWorking);

        // Split island: the next live page sits in a bubble beside the compact pill.
        SetBubble(kind == ViewKind.Compact && page is not null
            ? _pages.Where(p => p.IsActive && p.Key != page.Key).OrderByDescending(p => p.Priority).FirstOrDefault()
            : null, claudeWorking);

        bool pager = _hoverExpanded && page is not null && kind != ViewKind.Compact && _pages.Count > 1;
        UpdatePager(page, pager);

        string shownKey = _shownAlert is not null ? $"alert:{_shownAlert.Id}" : page?.Key ?? kind.ToString();
        ShowView(kind, shownKey, direction);
        UpdateVideoMirror(kind, media);
    }

    /// <summary>The YouTube video the browser is playing, once identified (null while unknown or not YouTube).</summary>
    private YouTubeVideo? CurrentVideo(MediaInfo media)
    {
        if (!YouTubeResolver.IsBrowser(media.SourceApp))
            return null;
        if (_youtube.TryGet(media, out var video))
            return video is not null && !_unembeddable.Contains(video.VideoId) ? video : null;
        _youtube.Resolve(media, Refresh);
        return null;
    }

    /// <summary>Plays the mirror while the player page is open; pauses it otherwise so it costs nothing.</summary>
    private void UpdateVideoMirror(ViewKind kind, MediaInfo? media)
    {
        if (media is not null && CurrentVideo(media) is { } video && kind == ViewKind.MediaExpanded)
        {
            if (_mirror is null)
            {
                _mirror = new YouTubeMirror();
                _mirror.Failed += id =>
                {
                    _unembeddable.Add(id);
                    Refresh();
                };
                VideoHost.Child = _mirror.View;
            }
            _videoShown = true;
            // Called on every refresh (≈1/s): the first call loads the video, later ones keep it in sync.
            _ = _mirror.ShowAsync(video.VideoId, media.CurrentPosition, media.IsPlaying);
        }
        else if (_videoShown)
        {
            _videoShown = false;
            _ = _mirror?.PauseAsync();
        }
    }

    private void FillCompactPage(Page page, MediaInfo? media)
    {
        switch (page.Key)
        {
            case "media" when media is not null:
                FillCompact(media);
                break;
            case "claude":
                FillCompactClaude();
                break;
            case "notifications":
                FillCompactNotifications();
                break;
            default:
                if (page.Activity is not null)
                    FillCompact(page.Activity);
                break;
        }
    }

    private void FillExpandedPage(Page page, MediaInfo? media)
    {
        switch (page.View)
        {
            case ViewKind.MediaExpanded when media is not null:
                FillMedia(media);
                break;
            case ViewKind.ClaudeExpanded:
                FillClaude();
                break;
            case ViewKind.NotificationsExpanded:
                FillNotifications();
                // Looking at the list counts as reading it (deferred: it raises Changed → Refresh).
                if (_notifications.UnreadCount > 0)
                    Dispatcher.BeginInvoke(_notifications.MarkAllRead);
                break;
            case ViewKind.ActivityExpanded when page.Activity is not null:
                FillActivity(page.Activity);
                break;
        }
    }

    /// <summary>Like iOS: when a new song starts, open the player for a moment so you see what's playing.</summary>
    private void DetectTrackChange(MediaInfo? media)
    {
        // Players publish the title just before they start playing, so only count a track once it plays.
        if (media is not { IsPlaying: true })
            return;
        string track = $"{media.SourceApp}|{media.Title}|{media.Artist}";
        if (track == _lastTrack)
            return;
        _lastTrack = track;

        // Skip the session that was already playing when the island launched.
        if (DateTime.Now - _startedAt > TimeSpan.FromSeconds(3))
        {
            _peekingMedia = true;
            _mediaPeekTimer.Stop();
            _mediaPeekTimer.Start();
        }
    }

    // ── Compact ──

    private void FillCompact(IslandActivity activity)
    {
        SetBadge(CompactBadge, CompactIcon, activity.Image, activity.Icon, activity.Accent, activity.Image is null ? 13 : 7);
        CompactEqualizer.Visibility = Visibility.Collapsed;
        CompactValue.Visibility = Visibility.Visible;

        if (activity.Style == ActivityStyle.Level && activity.Progress is { } level)
        {
            CompactTitle.Visibility = Visibility.Collapsed;
            CompactLevel.Visibility = Visibility.Visible;
            CompactLevel.Fill = new SolidColorBrush(activity.Accent);
            AnimateLevel(CompactLevel, level, 140);
            CompactValue.Text = $"{level * 100:0}";
        }
        else
        {
            SetCompactText(activity.Title, activity.Progress is { } p ? $"{p * 100:0}%" : "");
        }
    }

    private void FillCompact(MediaInfo media)
    {
        SetBadge(CompactBadge, CompactIcon, media.Artwork, "music", Colors.White, 7);
        SetCompactText(media.Title, null, media.Artist);
        CompactEqualizer.Visibility = Visibility.Visible;
        SetMediaAccent(media.Accent);
    }

    private void FillCompactClaude()
    {
        if (_claude.Sessions.FirstOrDefault() is not { } session)
            return;
        bool waiting = session.State == ClaudeSessionState.Waiting;
        SetBadge(CompactBadge, CompactIcon, null, "claude", waiting ? Yellow : ClaudeService.Orange, 13);
        string elapsed = session.State == ClaudeSessionState.Working && session.TurnStartedAt is { } started
            ? FormatTime(DateTime.Now - started)
            : "";
        SetCompactText(session.Project, elapsed, session.Detail);
    }

    private void FillCompactNotifications()
    {
        if (_notifications.History.FirstOrDefault() is not { } latest)
            return;
        SetBadge(CompactBadge, CompactIcon, latest.Logo, "bell", NotificationService.Blue, latest.Logo is null ? 13 : 7);
        int unread = _notifications.UnreadCount;
        SetCompactText(latest.Title, unread > 1 ? $"+{unread - 1}" : "", latest.Body);
    }

    /// <summary>
    /// Title in the middle (with an optional dimmer <paramref name="secondary"/> after it, Apple-style hierarchy);
    /// <paramref name="value"/> on the right (null hides it, for the equalizer).
    /// </summary>
    private void SetCompactText(string title, string? value, string? secondary = null)
    {
        CompactTitle.Visibility = Visibility.Visible;
        CompactLevel.Visibility = Visibility.Collapsed;
        if (string.IsNullOrEmpty(secondary))
        {
            CompactTitle.Text = title;
        }
        else
        {
            CompactTitle.Inlines.Clear();
            CompactTitle.Inlines.Add(new Run(title));
            CompactTitle.Inlines.Add(new Run("  " + secondary) { Foreground = (Brush)FindResource("DimText"), FontWeight = FontWeights.Normal });
        }
        CompactEqualizer.Visibility = Visibility.Collapsed;
        CompactValue.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        CompactValue.Text = value ?? "";
    }

    // ── Media ──

    /// <summary>The waveform and the artwork glow take the album's color; the scrubber stays white, as on iOS.</summary>
    private void SetMediaAccent(Color accent)
    {
        var brush = new SolidColorBrush(accent);
        brush.Freeze();
        foreach (var (bar, _) in _equalizerBars)
            bar.Fill = brush;
        MediaArtGlow.Color = accent;
    }

    private void FillMedia(MediaInfo media)
    {
        // YouTube in a browser: the video takes the artwork's place, above the title.
        bool video = CurrentVideo(media) is not null;
        VideoHost.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        MediaArt.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        MediaTexts.Margin = video ? new Thickness(0, 0, 14, 0) : new Thickness(14, 0, 14, 0);

        if (media.Artwork is not null)
        {
            MediaArt.Background = new ImageBrush(media.Artwork) { Stretch = Stretch.UniformToFill };
            MediaArtIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            MediaArt.Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            MediaArtIcon.Visibility = Visibility.Visible;
        }

        MediaTitle.Text = media.Title;
        MediaArtist.Text = media.Artist;
        MediaSource.Text = media.SourceApp;
        PlayPauseIcon.Data = (Geometry)FindResource(media.IsPlaying ? "PauseGeometry" : "PlayGeometry");
        // Optical centering: a play triangle looks off-center unless nudged right.
        PlayPauseIcon.Margin = new Thickness(media.IsPlaying ? 0 : 3, 0, 0, 0);
        SetMediaAccent(media.Accent);
        UpdateMediaTimeline(media);
    }

    private void UpdateMediaTimeline(MediaInfo media)
    {
        if (media.Duration <= TimeSpan.Zero)
        {
            MediaTimeline.Visibility = Visibility.Hidden;
            return;
        }
        MediaTimeline.Visibility = Visibility.Visible;
        var position = media.CurrentPosition;
        MediaPosition.Text = FormatTime(position);
        MediaDuration.Text = FormatTime(media.Duration);
        AnimateLevel(MediaProgress, position.TotalSeconds / media.Duration.TotalSeconds, media.IsPlaying ? 480 : 0);
    }

    // ── Activity ──

    private void FillActivity(IslandActivity activity)
    {
        SetBadge(ActivityBadge, ActivityIcon, activity.Image, activity.Icon, activity.Accent, activity.Image is null ? 22 : 10);
        ActivityCaption.Text = activity.Caption ?? "";
        ActivityCaption.Visibility = string.IsNullOrEmpty(activity.Caption) ? Visibility.Collapsed : Visibility.Visible;
        ActivityTitle.Text = string.IsNullOrEmpty(activity.Title) ? " " : activity.Title;
        ActivitySubtitle.Text = activity.Subtitle ?? "";
        ActivitySubtitle.Visibility = string.IsNullOrEmpty(activity.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        if (activity.Progress is { } p)
        {
            ActivityProgressRow.Visibility = Visibility.Visible;
            ActivityProgress.Fill = new SolidColorBrush(activity.Accent);
            AnimateLevel(ActivityProgress, p, 200);
            ActivityProgressText.Text = $"{p * 100:0}%";
        }
        else
        {
            ActivityProgressRow.Visibility = Visibility.Collapsed;
        }
    }

    // ── Claude ──

    private void FillClaude()
    {
        var sessions = _claude.Sessions.Take(3).ToList();
        int waiting = sessions.Count(s => s.State == ClaudeSessionState.Waiting);
        int working = sessions.Count(s => s.State == ClaudeSessionState.Working);
        ClaudeSummary.Text = waiting > 0 ? $"{waiting} aguardando você"
            : working > 0 ? (working == 1 ? "1 trabalhando" : $"{working} trabalhando")
            : _claude.HooksConnected ? "Tudo em dia" : "";

        var usage = _claude.Usage;
        UsageToday.Text = FormatTokens(usage.TodayTokens);
        UsageRecent.Text = FormatTokens(usage.Last5hTokens);
        UsageResponses.Text = usage.TodayResponses.ToString("N0", Culture);

        var now = DateTime.Now;
        var rows = sessions.Select(s => (Session: s, Time: SessionTime(s, now))).ToList();
        string signature = string.Join("|", rows.Select(r => $"{r.Session.SessionId}{r.Session.State}{r.Session.Detail}{r.Session.Title}{r.Time}"))
            + _claude.HooksConnected;
        if (signature == _claudeSignature)
            return;
        _claudeSignature = signature;

        ClaudeSessions.Children.Clear();
        if (rows.Count == 0)
        {
            ClaudeSessions.Children.Add(new TextBlock
            {
                Text = _claude.HooksConnected
                    ? "Nenhuma sessão nas últimas horas."
                    : "Conecte os hooks do Claude Code para acompanhar as sessões aqui (veja o README).",
                Foreground = (Brush)FindResource("DimText"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
            });
            return;
        }

        foreach (var (session, time) in rows)
        {
            var color = session.State switch
            {
                ClaudeSessionState.Working => ClaudeService.Orange,
                ClaudeSessionState.Waiting => Yellow,
                ClaudeSessionState.Done => Green,
                _ => Color.FromRgb(0x8E, 0x8E, 0x93),
            };

            var name = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            name.Inlines.Add(new Run(session.Project) { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(session.Title))
                name.Inlines.Add(new Run($"  {session.Title}") { Foreground = (Brush)FindResource("FaintText"), FontSize = 12 });

            var detail = new TextBlock
            {
                Text = session.Detail,
                FontSize = 12,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = session.State == ClaudeSessionState.Waiting ? new SolidColorBrush(Yellow) : (Brush)FindResource("DimText"),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 10, 0) };
            if (session.State == ClaudeSessionState.Working)
            {
                // Breathing dot while Claude works.
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(850))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
            }
            var texts = new StackPanel { Children = { name, detail } };
            var clock = new TextBlock { Text = time, FontSize = 11, Foreground = (Brush)FindResource("FaintText"), Margin = new Thickness(8, 2, 0, 0) };
            Grid.SetColumn(texts, 1);
            Grid.SetColumn(clock, 2);
            grid.Children.Add(dot);
            grid.Children.Add(texts);
            grid.Children.Add(clock);

            var row = new Border { Style = (Style)FindResource("RowBorder"), Child = grid, ToolTip = "Abrir no VS Code" };
            string? url = ClaudeService.VsCodeUrl(session.Cwd);
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                OpenUrl(url);
            };
            ClaudeSessions.Children.Add(row);
        }
    }

    private static string SessionTime(ClaudeSession session, DateTime now) => session.State switch
    {
        ClaudeSessionState.Working or ClaudeSessionState.Waiting when session.TurnStartedAt is { } started => FormatTime(now - started),
        ClaudeSessionState.Done when session.FinishedAt is { } finished => FormatAgo(finished, now),
        _ => FormatAgo(session.UpdatedAt, now),
    };

    // ── Notifications ──

    private void FillNotifications()
    {
        var now = DateTime.Now;
        var items = _notifications.History.Take(4).ToList();
        string signature = string.Join("|", items.Select(i => $"{i.Id}{FormatAgo(i.ReceivedAt, now)}"));
        if (signature == _notificationSignature)
            return;
        _notificationSignature = signature;

        NotificationList.Children.Clear();
        foreach (var item in items)
        {
            var logo = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };
            var logoIcon = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13 };
            logo.Child = logoIcon;
            SetBadge(logo, logoIcon, item.Logo, "bell", NotificationService.Blue, 8);

            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = item.AppName, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("FaintText"), TextTrimming = TextTrimming.CharacterEllipsis });
            texts.Children.Add(new TextBlock { Text = item.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(item.Body))
                texts.Children.Add(new TextBlock { Text = item.Body, FontSize = 12, Foreground = (Brush)FindResource("DimText"), TextTrimming = TextTrimming.CharacterEllipsis });

            var time = new TextBlock { Text = FormatAgo(item.ReceivedAt, now), FontSize = 11, Foreground = (Brush)FindResource("FaintText"), Margin = new Thickness(8, 1, 0, 0) };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(texts, 1);
            Grid.SetColumn(time, 2);
            grid.Children.Add(logo);
            grid.Children.Add(texts);
            grid.Children.Add(time);

            var row = new Border { Style = (Style)FindResource("RowBorder"), Child = grid, ToolTip = $"Abrir {item.AppName}" };
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _notifications.Open(item);
            };
            NotificationList.Children.Add(row);
        }
    }

    // ── Shared bits ──

    private static void SetBadge(Border badge, TextBlock icon, ImageSource? image, string? glyph, Color accent, double radius)
    {
        badge.CornerRadius = new CornerRadius(radius);
        if (image is not null)
        {
            badge.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            icon.Text = "";
            return;
        }
        badge.Background = new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B));
        icon.Foreground = new SolidColorBrush(accent);
        string text = Icons.Resolve(glyph);
        icon.Text = text;
        icon.FontFamily = GlyphFont(text);
    }

    private static readonly FontFamily FluentIcons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol");

    /// <summary>
    /// Fluent icons live in the Unicode private use area; anything else (Claude's ✻, ★ from the API...)
    /// needs Segoe UI Symbol. WPF's family fallback doesn't reliably reach it, so pick per glyph.
    /// </summary>
    private static FontFamily GlyphFont(string text) =>
        text.Length > 0 && text[0] is >= '' and <= '' ? FluentIcons : SymbolFont;

    private static void AnimateLevel(LevelBar bar, double value, int milliseconds)
    {
        value = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
        if (milliseconds <= 0)
        {
            bar.BeginAnimation(LevelBar.ValueProperty, null);
            bar.Value = value;
            return;
        }
        bar.BeginAnimation(LevelBar.ValueProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>Claude-style spinning ✻ while it works.</summary>
    private static void SetSpin(RotateTransform transform, bool spinning)
    {
        if (spinning == transform.HasAnimatedProperties)
            return;
        if (spinning)
        {
            transform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            transform.BeginAnimation(RotateTransform.AngleProperty, null);
            transform.Angle = 0;
        }
    }

    private void SetBubble(Page? page, bool claudeWorking)
    {
        _bubblePage = page;
        Bubble.IsHitTestVisible = page is not null;
        _bubble.Target = page is null ? 0 : 1;
        if (page is not null)
        {
            SetBadge(BubbleBadge, BubbleIcon, page.Image, page.Glyph, page.Accent, page.Image is null ? 12 : 7);
            SetSpin(BubbleIconSpin, page.Key == "claude" && claudeWorking);
            Bubble.ToolTip = page.Key switch { "media" => "Música", "claude" => "Claude Code", "notifications" => "Notificações", _ => page.Activity?.Title };
        }
        StartAnimation();
    }

    private void UpdatePager(Page? current, bool visible)
    {
        if (visible != _pagerVisible)
        {
            _pagerVisible = visible;
            Fade(PagerBar, visible, 0);
        }
        if (!visible)
            return;

        string signature = string.Join("|", _pages.Select(p => $"{p.Key}{p.Image?.GetHashCode()}{p.Accent}{p.IsActive}")) + "#" + current?.Key;
        if (signature == _pagerSignature)
            return;
        _pagerSignature = signature;

        PagerBar.Children.Clear();
        foreach (var page in _pages)
        {
            bool selected = page.Key == current?.Key;
            var icon = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11 };
            var badge = new Border { Width = 22, Height = 22, Child = icon };
            SetBadge(badge, icon, page.Image, page.Glyph, page.Accent, page.Image is null ? 11 : 6);

            // Selected: a thin ring in the page's own color. Live but not selected: a small dot, like an unread badge.
            var content = new Grid { Children = { badge } };
            if (page.IsActive && !selected)
            {
                content.Children.Add(new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = new SolidColorBrush(page.Accent),
                    Stroke = Brushes.Black,
                    StrokeThickness = 1.2,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 2, 0),
                });
            }

            double restOpacity = selected ? 1 : 0.5;
            var tab = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Margin = new Thickness(3, 0, 3, 0),
                Background = selected ? new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
                BorderBrush = selected ? new SolidColorBrush(Color.FromArgb(0xB0, page.Accent.R, page.Accent.G, page.Accent.B)) : Brushes.Transparent,
                BorderThickness = new Thickness(1.5),
                Opacity = restOpacity,
                Cursor = Cursors.Hand,
                Child = content,
                ToolTip = page.Key switch { "media" => "Música", "claude" => "Claude Code", "notifications" => "Notificações", _ => page.Activity?.Title },
            };
            tab.MouseEnter += (_, _) => tab.Opacity = 1;
            tab.MouseLeave += (_, _) => tab.Opacity = restOpacity;
            string key = page.Key;
            tab.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SelectPage(key);
            };
            PagerBar.Children.Add(tab);
        }
        PagerBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _pagerWidth = PagerBar.DesiredSize.Width;
        ApplyGeometry();
    }

    // ───────────────────────────── Animation ─────────────────────────────

    private void ShowView(ViewKind kind, string shownKey, int direction)
    {
        if (kind != _view)
        {
            Fade(_views[_view], show: false, 0);
            Fade(_views[kind], show: true, direction);
            _view = kind;
        }
        else if (shownKey != _shownKey && kind != ViewKind.Idle)
        {
            // Same layout, different content (e.g. one activity to another): quick re-entrance.
            var view = _views[kind];
            view.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(220)));
            Slide(view, direction);
        }
        _shownKey = shownKey;

        var (w, h, r) = kind switch
        {
            ViewKind.Idle => (140.0, 34.0, 17.0),
            ViewKind.IdleExpanded => (240.0, 86.0, 32.0),
            ViewKind.Compact => (320.0, 38.0, 19.0),
            ViewKind.MediaExpanded => (400.0, MeasureHeight(MediaView), 42.0),
            ViewKind.ClaudeExpanded => (400.0, MeasureHeight(ClaudeView), 36.0),
            ViewKind.NotificationsExpanded => (400.0, MeasureHeight(NotificationsView), 36.0),
            _ => (400.0, MeasureHeight(ActivityView), 34.0),
        };
        if (_pagerVisible)
            h += PagerHeight;

        // Like the real island: a lively bounce when it opens, a calmer settle when it closes.
        bool growing = w * h > _width.Target * _height.Target;
        foreach (var spring in new[] { _width, _height, _radius })
            spring.Damping = growing ? 20 : 29;

        _width.Target = w;
        _height.Target = h;
        _radius.Target = r;
        StartAnimation();
    }

    private static double MeasureHeight(FrameworkElement view)
    {
        view.Measure(new Size(view.Width, double.PositiveInfinity));
        return Math.Max(76, Math.Ceiling(view.DesiredSize.Height));
    }

    /// <summary>
    /// iOS-style content transition: incoming content un-blurs and grows into place while the island
    /// springs open; outgoing content blurs and fades away quickly.
    /// </summary>
    private static void Fade(UIElement element, bool show, int direction)
    {
        element.IsHitTestVisible = show;
        var duration = TimeSpan.FromMilliseconds(show ? 320 : 130);
        var delay = show ? TimeSpan.FromMilliseconds(60) : TimeSpan.Zero;
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        element.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, duration) { BeginTime = delay, EasingFunction = easeOut });

        if (GetScale(element) is { } scale)
        {
            var grow = new DoubleAnimation(show ? 0.93 : 1, show ? 1 : 0.97, duration)
            {
                BeginTime = delay,
                EasingFunction = show ? new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } : easeOut,
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        var blur = new System.Windows.Media.Effects.BlurEffect { Radius = show ? 12 : 0, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        element.Effect = blur;
        var unblur = new DoubleAnimation(show ? 0 : 8, duration) { BeginTime = delay, EasingFunction = easeOut };
        // Drop the shader once settled so static content renders at full sharpness and no GPU cost.
        unblur.Completed += (_, _) =>
        {
            if (element.Effect == blur)
                element.Effect = null;
        };
        blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, unblur);

        if (show)
            Slide(element, direction);
    }

    private static ScaleTransform? GetScale(UIElement element) =>
        element.RenderTransform is TransformGroup group && group.Children.Count > 0 ? group.Children[0] as ScaleTransform : null;

    private static void Slide(UIElement element, int direction)
    {
        var shift = element.RenderTransform as TranslateTransform
            ?? (element.RenderTransform as TransformGroup)?.Children.OfType<TranslateTransform>().FirstOrDefault();
        if (direction == 0 || shift is null)
            return;
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(36 * direction, 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void StartAnimation()
    {
        if (_animating)
            return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : (now - _lastFrame).TotalSeconds;
        if (dt <= 0)
            return; // Rendering can fire more than once per frame.
        _lastFrame = now;
        dt = Math.Min(dt, 1 / 30.0);

        // Non-short-circuit | so every spring advances.
        bool moving = _width.Step(dt) | _height.Step(dt) | _radius.Step(dt) | _bubble.Step(dt);
        ApplyGeometry();

        if (!moving)
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
    }

    private void ApplyGeometry()
    {
        double w = Math.Max(24, _width.Value);
        double h = Math.Max(20, _height.Value);
        double r = Math.Clamp(_radius.Value, 0, h / 2);

        Island.Width = IslandRim.Width = w;
        Island.Height = IslandRim.Height = h;
        Island.CornerRadius = IslandRim.CornerRadius = new CornerRadius(r);
        _clip.Rect = new Rect(0, 0, w, h);
        _clip.RadiusX = _clip.RadiusY = r;

        foreach (var view in _views.Values)
            Canvas.SetLeft(view, (w - view.Width) / 2);
        Canvas.SetLeft(PagerBar, (w - _pagerWidth) / 2);
        Canvas.SetTop(PagerBar, h - PagerHeight + 2);

        // The bubble rides just right of the pill and pops in with its own spring.
        double scale = Math.Max(0, _bubble.Value);
        BubbleScale.ScaleX = BubbleScale.ScaleY = scale;
        Bubble.Opacity = Math.Clamp(scale, 0, 1);
        BubbleShift.X = w / 2 + 8 + Bubble.Width / 2;
    }

    private const double WaveRest = 0.3;

    private void BuildEqualizer(Panel host, double height)
    {
        // Five slim, centered bars: the iOS "now playing" waveform.
        for (int i = 0; i < 5; i++)
        {
            var scale = new ScaleTransform(1, WaveRest);
            var bar = new Rectangle
            {
                Width = 2.6,
                Height = height,
                RadiusX = 1.3,
                RadiusY = 1.3,
                Margin = new Thickness(1.1, 0, 1.1, 0),
                Fill = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
            };
            host.Children.Add(bar);
            _equalizerBars.Add((bar, scale));
        }
    }

    /// <summary>An organic, never-repeating-looking bar motion: random heights eased from one to the next, looping seamlessly.</summary>
    private static DoubleAnimationUsingKeyFrames Wave(Random random)
    {
        var wave = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        double time = 0;
        for (int i = 0; i < 7; i++)
        {
            time += random.Next(150, 300);
            wave.KeyFrames.Add(new EasingDoubleKeyFrame(0.28 + random.NextDouble() * 0.72, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(time)),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
        }
        time += random.Next(150, 300);
        wave.KeyFrames.Add(new EasingDoubleKeyFrame(WaveRest, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(time)), new SineEase()));
        return wave;
    }

    private void SetEqualizer(bool running)
    {
        if (running == _equalizerRunning)
            return;
        _equalizerRunning = running;

        var random = new Random();
        foreach (var (_, bar) in _equalizerBars)
        {
            if (running)
            {
                bar.BeginAnimation(ScaleTransform.ScaleYProperty, Wave(random));
            }
            else
            {
                bar.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                bar.ScaleY = WaveRest;
            }
        }
    }

    // ───────────────────────────── Events ─────────────────────────────

    private void OnVolumeChanged(float level, bool muted) => _controller.Upsert(new IslandActivity
    {
        Id = "system.volume",
        Title = "Volume",
        Icon = muted || level < 0.005f ? "mute" : "volume",
        Progress = muted ? 0 : level,
        Style = ActivityStyle.Level,
        Duration = TimeSpan.FromSeconds(1.6),
        Priority = 100,
        Source = "system",
    });

    private void Peek()
    {
        _peeking = true;
        _peekTimer.Stop();
        _peekTimer.Start();
        Refresh();
    }

    private void OnTick()
    {
        _tickCount++;
        UpdateClock();

        if (_view == ViewKind.MediaExpanded && _controller.Media is { } media)
            UpdateMediaTimeline(media);

        // Once a second: elapsed timers ("2:31"), "há 3 min" labels, stale Claude sessions.
        if (_tickCount % 2 == 0 && IsVisible)
        {
            _claude.Tick();
            Refresh();
        }

        // Get out of the way of games, fullscreen video and presentations.
        if (_tickCount % 4 == 0)
        {
            bool fullscreen = NativeMethods.IsFullscreenAppRunning();
            if (fullscreen && IsVisible)
                Hide();
            else if (!fullscreen && !IsVisible)
                Show();
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        IdleClock.Text = now.ToString("HH:mm", Culture);
        BigClock.Text = now.ToString("HH:mm", Culture);
        DateText.Text = Culture.TextInfo.ToTitleCase(now.ToString("dddd, d 'de' MMMM", Culture));
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
            Dispatcher.BeginInvoke(Reposition);
    }

    private void Reposition()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _leaveDelay.Stop();
        if (!_hoverExpanded)
            _hoverDelay.Start();
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverDelay.Stop();
        if (_hoverExpanded)
            _leaveDelay.Start();
    }

    private bool IsCursorOverIsland()
    {
        if (!IsVisible || Island.ActualWidth <= 0)
            return false;
        var cursor = NativeMethods.CursorPosition();
        var topLeft = Island.PointToScreen(new Point(0, 0));
        var bottomRight = Island.PointToScreen(new Point(Island.ActualWidth, Island.ActualHeight));        return cursor.X >= topLeft.X && cursor.X <= bottomRight.X && cursor.Y >= topLeft.Y && cursor.Y <= bottomRight.Y;
    }

    private void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Alerts and API activities: open their link (if any) and dismiss.
        if ((_shownAlert ?? _shownPage?.Activity) is { } activity)
        {
            OpenUrl(activity.ActionUrl);
            _controller.Remove(activity.Id);
            return;
        }

        // A compact page: open it right away instead of waiting for the hover delay.
        if (_view == ViewKind.Compact && _shownPage is not null)
        {
            _hoverDelay.Stop();
            _selectedKey = _shownPage.Key;
            _hoverExpanded = true;
            Refresh();
        }
    }

    /// <summary>Scroll over the expanded island to flip through pages.</summary>
    private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_hoverExpanded || _shownAlert is not null || _shownPage is null || _pages.Count < 2)
            return;
        int index = _pages.FindIndex(p => p.Key == _shownPage.Key);
        int next = Math.Clamp(index + (e.Delta < 0 ? 1 : -1), 0, _pages.Count - 1);
        if (next != index)
            SelectPage(_pages[next].Key);
        e.Handled = true;
    }

    /// <summary>Tapping the side bubble swaps it with the main pill.</summary>
    private void Bubble_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_bubblePage is { } page)
            SelectPage(page.Key);
    }

    private void ClearNotifications_Click(object sender, RoutedEventArgs e) => _notifications.Clear();

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e) => await RunMedia(_media.TogglePlayPauseAsync);
    private async void NextButton_Click(object sender, RoutedEventArgs e) => await RunMedia(_media.NextAsync);
    private async void PreviousButton_Click(object sender, RoutedEventArgs e) => await RunMedia(_media.PreviousAsync);

    private static async Task RunMedia(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private async void MediaSeek_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_controller.Media is not { Duration.TotalSeconds: > 0 } media || sender is not FrameworkElement area || area.ActualWidth <= 0)
            return;
        double fraction = Math.Clamp(e.GetPosition(area).X / area.ActualWidth, 0, 1);
        AnimateLevel(MediaProgress, fraction, 0);
        await RunMedia(() => _media.SeekAsync(media.Duration * fraction));
    }

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        (NotificationsMenuItem.Header, NotificationsMenuItem.IsCheckable, NotificationsMenuItem.IsChecked) = _notifications.Access switch
        {
            NotificationAccess.Allowed => ("Espelhar notificações do Windows", true, _notifications.Enabled),
            NotificationAccess.Denied => ("Permitir acesso às notificações…", false, false),
            NotificationAccess.NoIdentity => ("Ativar notificações do Windows…", false, false),
            _ => ("Notificações do Windows indisponíveis", false, false),
        };
        NotificationsMenuItem.IsEnabled = _notifications.Access != NotificationAccess.Unavailable;
        StartupMenuItem.IsChecked = StartupRegistration.IsEnabled;
        ApiMenuItem.Header = _api is null ? "API indisponível (porta em uso)" : $"Copiar endereço da API ({_api.BaseUrl})";
        ApiMenuItem.IsEnabled = _api is not null;
    }

    private async void NotificationsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        switch (_notifications.Access)
        {
            case NotificationAccess.Allowed:
                _notifications.Enabled = NotificationsMenuItem.IsChecked;
                break;

            case NotificationAccess.Denied:
                OpenUrl("ms-settings:privacy-notifications");
                break;

            case NotificationAccess.NoIdentity:
                try
                {
                    await PackageRegistration.RegisterAsync();
                    PackageRegistration.Restart();
                }
                catch (Exception ex)
                {
                    App.Log(ex);
                    _controller.Upsert(new IslandActivity
                    {
                        Id = "system.notifications",
                        Title = "Não foi possível ativar as notificações",
                        Subtitle = "Ative o Modo de Desenvolvedor do Windows e tente de novo. Clique para abrir.",
                        Icon = "error",
                        Accent = Color.FromRgb(0xFF, 0x45, 0x3A),
                        Duration = TimeSpan.FromSeconds(10),
                        ExpandOnArrive = true,
                        ActionUrl = "ms-settings:developers",
                        Source = "system",
                    });
                }
                break;
        }
    }

    private void StartupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupRegistration.Set(StartupMenuItem.IsChecked);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            StartupMenuItem.IsChecked = StartupRegistration.IsEnabled;
        }
    }

    private void ApiMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null)
            return;
        try
        {
            Clipboard.SetText(_api.BaseUrl);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private void TestMenuItem_Click(object sender, RoutedEventArgs e) => _controller.Upsert(new IslandActivity
    {
        Id = "test",
        Title = "Olá do Windows Island",
        Subtitle = "Passe o mouse para expandir. Clique para dispensar.",
        Icon = "bell",
        Accent = NotificationService.Blue,
        Duration = TimeSpan.FromSeconds(6),
        ExpandOnArrive = true,
        Source = "test",
    });

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

    // ───────────────────────────── Formatting ─────────────────────────────

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", Culture) : t.ToString(@"m\:ss", Culture);

    private static string FormatAgo(DateTime at, DateTime now)
    {
        var ago = now - at;
        if (ago < TimeSpan.FromMinutes(1))
            return "agora";
        if (ago < TimeSpan.FromHours(1))
            return $"{(int)ago.TotalMinutes} min";
        return at.Date == now.Date ? at.ToString("HH:mm", Culture) : at.ToString("dd/MM", Culture);
    }

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => (tokens / 1e9).ToString("0.0", Culture) + " bi",
        >= 1_000_000 => (tokens / 1e6).ToString("0.0", Culture) + " mi",
        >= 1_000 => (tokens / 1e3).ToString("0", Culture) + " mil",
        _ => tokens.ToString(Culture),
    };
}
