using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowsIsland.Controls;
using WindowsIsland.Core;
using WindowsIsland.Interop;
using WindowsIsland.Services;

namespace WindowsIsland;

public partial class MainWindow : Window
{
    private enum ViewKind { Idle, IdleExpanded, Compact, MediaExpanded, ActivityExpanded, ClaudeExpanded, NotificationsExpanded, SettingsExpanded, AskExpanded, ShelfExpanded }

    private const string SettingsKey = "settings";
    private const string ClockKey = "clock";
    private const string AskKey = "ask";
    private const string ShelfKey = "shelf";
    private const int AskHotkeyId = 0x4953;

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
    private readonly BluetoothService _bluetooth;
    private readonly NotificationService _notifications;
    private readonly ClaudeService _claude;
    private readonly ClaudeChatService _chat;
    private bool _keyboard;                 // The ask box has keyboard focus (the island is activatable meanwhile).
    private IntPtr _previousForeground;     // Where focus goes back to after asking.
    private string? _askSignature;
    private string _hotkeyLabel = "";
    private readonly List<ChatAttachment> _askAttachments = new();  // Goes with the next question.
    private readonly Shelf _shelf = Shelf.Load();
    private string? _shelfSignature;
    private bool _dragging;                 // Files are being dragged over the island.
    private readonly IslandSettings _settings = IslandSettings.Load();
    private TrayIcon? _tray;
    private bool _swallowing;
    private bool _settingsBuilt;
    private string? _usageSignature;
    private readonly YouTubeResolver _youtube = new();
    private readonly HashSet<string> _unembeddable = new();
    private YouTubeMirror? _mirror;
    private bool _videoShown;
    private VideoPipWindow? _pip;          // The video pinned out of the island, if any.
    private DateTime? _pipMediaLostSince;  // When the pinned video's browser session went away.
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
        _bluetooth = new BluetoothService(_controller);
        _notifications = new NotificationService(_controller);
        _claude = new ClaudeService(_controller, Dispatcher);
        _chat = new ClaudeChatService(Dispatcher);
        _views = new()
        {
            [ViewKind.Idle] = IdleView,
            [ViewKind.IdleExpanded] = ClockView,
            [ViewKind.Compact] = CompactView,
            [ViewKind.MediaExpanded] = MediaView,
            [ViewKind.ActivityExpanded] = ActivityView,
            [ViewKind.ClaudeExpanded] = ClaudeView,
            [ViewKind.NotificationsExpanded] = NotificationsView,
            [ViewKind.SettingsExpanded] = SettingsView,
            [ViewKind.AskExpanded] = AskView,
            [ViewKind.ShelfExpanded] = ShelfView,
        };
        foreach (var view in _views.Values)
        {
            // Scale (for the blur-in entrance) + translate (for sliding between pages), anchored at the top.
            view.RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } };
            view.RenderTransformOrigin = new Point(0.5, 0);
        }

        Stage.Clip = _clip;
        ApplyAppearance();
        VideoHost.Clip = new RectangleGeometry(new Rect(0, 0, VideoHost.Width, VideoHost.Height), 18, 18);
        BuildEqualizer(CompactEqualizer, 16);
        BuildEqualizer(MediaEqualizer, 18);
        ApplyGeometry();

        _controller.Changed += Refresh;
        _controller.Arrived += _ => Peek();
        _notifications.Changed += Refresh;
        _claude.Changed += Refresh;
        _chat.Changed += Refresh;
        _shelf.Changed += Refresh;
        _chat.Answered += OnChatAnswered;
        // Clicking anywhere else ends typing: the island goes back to never taking focus.
        Deactivated += (_, _) =>
        {
            if (!_keyboard)
                return;
            DisableKeyboard(restoreFocus: false);
            if (_hoverExpanded && !IsCursorOverIsland())
                _leaveDelay.Start();
        };
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
            // While you're typing a question, it stays open wherever the mouse goes.
            if (IsCursorOverIsland() || _keyboard)
                return;
            _leaveDelay.Stop();
            _hoverExpanded = false;
            // Settings is a place you visit, not something the compact island should come back to.
            if (_selectedKey == SettingsKey)
                _selectedKey = null;
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
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeOverlayWindow(hwnd);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        RegisterAskHotkey(hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == AskHotkeyId)
        {
            handled = true;
            OpenAsk();
        }
        return IntPtr.Zero;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Reposition();
        UpdateClock();
        Refresh();
        _tick.Start();

        _tray = new TrayIcon();
        _tray.ToggleRequested += () => Dispatcher.BeginInvoke(ToggleIsland);
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(Close);
        if (_settings.Hidden)
        {
            // Stays swallowed across restarts; the tray icon brings it back.
            RootScale.ScaleX = RootScale.ScaleY = 0;
            Hide();
            _tray.SetIslandVisible(false);
        }

        _battery.Start();
        _bluetooth.Start();
        _claude.Start();
        StartUpdates();

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
        _pip?.Close();
        NativeMethods.UnregisterHotKey(new WindowInteropHelper(this).Handle, AskHotkeyId);
        _chat.Dispose();
        _battery.Stop();
        _bluetooth.Dispose();
        _notifications.Enabled = false;
        _claude.Dispose();
        _tray?.Dispose();
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
        if (sessions.Count > 0 || _claude.Usage.WeekResponses > 0)
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

        // While open, the tab bar always ends with "ask Claude" and the gear; with nothing else going on, the clock
        // keeps them company. While Claude is answering, the ask page is live (compact pill, bubble).
        if (_hoverExpanded && pages.Count == 0)
            pages.Add(new(ClockKey, ViewKind.IdleExpanded, Icons.Resolve("clock"), null, Color.FromRgb(0xAE, 0xAE, 0xB2), false, -1));
        // The shelf shows up once it holds something, or while files are being dragged over the island.
        if (_hoverExpanded && (_shelf.Items.Count > 0 || _dragging))
            pages.Add(new(ShelfKey, ViewKind.ShelfExpanded, Icons.Resolve("folder"), null, Color.FromRgb(0x64, 0xD2, 0xFF), false, -1));
        if (_hoverExpanded || _chat.IsRunning)
            pages.Add(new(AskKey, ViewKind.AskExpanded, Icons.Resolve("chat"), null, ClaudeService.Orange, _chat.IsRunning, _chat.IsRunning ? 60 : -1));
        if (_hoverExpanded)
            pages.Add(new(SettingsKey, ViewKind.SettingsExpanded, "", null, Color.FromRgb(0xAE, 0xAE, 0xB2), false, -1));

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
            page = FindPage(_selectedKey) ?? PickCompactPage() ?? _pages.FirstOrDefault(p => p.Key != SettingsKey);
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
        SetSpin(CompactIconSpin, kind == ViewKind.Compact && (page?.Key == "claude" && claudeWorking || page?.Key == AskKey && _chat.IsRunning));
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
        UpdatePip();
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
            case AskKey:
                SetBadge(CompactBadge, CompactIcon, null, "claude", ClaudeService.Orange, 13);
                SetCompactText("Claude", "", _chat.Status);
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
            case ViewKind.AskExpanded:
                FillAsk();
                _chat.HasUnread = false;
                break;
            case ViewKind.ShelfExpanded:
                FillShelf();
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
            case ViewKind.SettingsExpanded:
                FillSettings();
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
