using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WindowsIsland.Controls;
using WindowsIsland.Core;
using WindowsIsland.Interop;

namespace WindowsIsland;

/// <summary>
/// The YouTube video pinned out of the island: a small always-on-top window (picture-in-picture) you can drag
/// anywhere, which snaps to the screen edges, resizes from its corner or with the mouse wheel, and shows
/// transport controls on hover. The video is its own <see cref="YouTubeMirror"/>, kept in sync by MainWindow.
/// </summary>
public partial class VideoPipWindow : Window
{
    private const double Inset = 14;          // Root margin: room for the shadow around the video.
    private const double Corner = 14;
    private const double DefaultVideoWidth = 400, MinVideoWidth = 240, MaxVideoWidth = 1100;
    private const double SnapDistance = 32, EdgeMargin = 16;
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    private readonly IslandSettings _settings;
    private readonly DispatcherTimer _hoverCheck = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private double _videoWidth;
    private bool _controlsShown, _resizing, _closing, _hasVideo, _frameReady;
    private Point _resizeOrigin;
    private double _resizeStartWidth;

    public VideoPipWindow(IslandSettings settings, YouTubeMirror mirror)
    {
        InitializeComponent();
        _settings = settings;
        Mirror = mirror;
        VideoHost.Child = mirror.View;
        // A fresh player takes a few seconds to start: keep the artwork up until it has a frame.
        mirror.FrameReady += () =>
        {
            _frameReady = true;
            UpdatePlaceholder();
        };

        SetVideoWidth(settings.PipWidth ?? DefaultVideoWidth);
        PlaceInitially();

        Surface.SizeChanged += (_, _) =>
            Surface.Clip = new RectangleGeometry(new Rect(Surface.RenderSize), Corner, Corner);
        // Mouse-leave is not always delivered to a no-activate window: poll while the controls are up.
        _hoverCheck.Tick += (_, _) =>
        {
            if (!_resizing && !IsCursorOver())
                SetControls(false);
        };
        Loaded += (_, _) =>
        {
            SettleOnScreen(animate: false, save: false);
            AnimateIn();
        };
    }

    public YouTubeMirror Mirror { get; }

    public event Action? PlayPauseRequested, PreviousRequested, NextRequested, CloseRequested, ReturnRequested;

    /// <summary>The user clicked the timeline (0–1).</summary>
    public event Action<double>? SeekRequested;

    private double VideoHeight => Math.Round(_videoWidth * 9 / 16);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Out of Alt+Tab, and clicking it never steals focus from what you're doing.
        NativeMethods.MakeOverlayWindow(new WindowInteropHelper(this).Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hoverCheck.Stop();
        Mirror.Dispose();
        base.OnClosed(e);
    }

    // ───────────────────────────── State from the browser ─────────────────────────────

    /// <summary>Follows the browser: title, play/pause, progress; artwork while no video is identified. UI thread.</summary>
    public void Update(MediaInfo? media, bool hasVideo)
    {
        _hasVideo = hasVideo;
        UpdatePlaceholder();
        PlaceholderArt.Source = media?.Artwork;
        PlaceholderTitle.Text = media?.Title ?? "";
        TitleText.Text = media?.Title ?? "";

        bool playing = media?.IsPlaying == true;
        PlayPauseIcon.Data = (Geometry)FindResource(playing ? "PauseGeometry" : "PlayGeometry");
        // Optical centering: a play triangle looks off-center unless nudged right.
        PlayPauseIcon.Margin = new Thickness(playing ? 0 : 3, 0, 0, 0);

        if (media is not { Duration.TotalSeconds: > 0 })
        {
            Timeline.Visibility = Visibility.Hidden;
            return;
        }
        Timeline.Visibility = Visibility.Visible;
        var position = media.CurrentPosition;
        PositionText.Text = FormatTime(position);
        DurationText.Text = FormatTime(media.Duration);
        double fraction = Math.Clamp(position.TotalSeconds / media.Duration.TotalSeconds, 0, 1);
        Progress.BeginAnimation(LevelBar.ValueProperty, playing
            ? new DoubleAnimation(fraction, TimeSpan.FromMilliseconds(480))
            : null);
        if (!playing)
            Progress.Value = fraction;
    }

    private void UpdatePlaceholder()
    {
        Placeholder.Visibility = _hasVideo && _frameReady ? Visibility.Collapsed : Visibility.Visible;
        PlaceholderHint.Text = _hasVideo ? "Carregando o vídeo…" : "Aguardando o vídeo no navegador…";
    }

    public void CloseAnimated()
    {
        if (_closing)
            return;
        _closing = true;
        IsHitTestVisible = false;
        var duration = TimeSpan.FromMilliseconds(170);
        var fade = new DoubleAnimation(0, duration);
        fade.Completed += (_, _) => Close();
        Root.BeginAnimation(OpacityProperty, fade);
        var shrink = new DoubleAnimation(0.9, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    private void AnimateIn()
    {
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        var pop = new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    // ───────────────────────────── Position and size ─────────────────────────────

    private void SetVideoWidth(double width)
    {
        _videoWidth = Math.Round(Math.Clamp(width, MinVideoWidth, MaxVideoWidth));
        Width = _videoWidth + 2 * Inset;
        Height = VideoHeight + 2 * Inset;
    }

    private void PlaceInitially()
    {
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (_settings.PipLeft is { } left && _settings.PipTop is { } top && desktop.Contains(new Rect(left, top, _videoWidth, VideoHeight)))
        {
            MoveVideoTo(left, top);
            return;
        }
        // First time: bottom-right corner, where picture-in-picture usually lives.
        var area = SystemParameters.WorkArea;
        MoveVideoTo(area.Right - _videoWidth - EdgeMargin, area.Bottom - VideoHeight - EdgeMargin);
    }

    /// <summary>Keeps the video whole on its monitor and pulls it onto nearby edges, like iOS and macOS do.</summary>
    private void SettleOnScreen(bool animate, bool save = true)
    {
        var area = CurrentWorkArea();
        double w = _videoWidth, h = VideoHeight;
        double x = Math.Clamp(Left + Inset, area.Left, Math.Max(area.Left, area.Right - w));
        double y = Math.Clamp(Top + Inset, area.Top, Math.Max(area.Top, area.Bottom - h));

        if (x - area.Left < SnapDistance)
            x = area.Left + EdgeMargin;
        else if (area.Right - (x + w) < SnapDistance)
            x = area.Right - w - EdgeMargin;
        if (y - area.Top < SnapDistance)
            y = area.Top + EdgeMargin;
        else if (area.Bottom - (y + h) < SnapDistance)
            y = area.Bottom - h - EdgeMargin;

        MoveVideoTo(x, y, animate);
        if (save)
        {
            _settings.PipLeft = x;
            _settings.PipTop = y;
            _settings.PipWidth = _videoWidth;
            _settings.Save();
        }
    }

    /// <summary>Work area of the monitor the window is on, in WPF units.</summary>
    private Rect CurrentWorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return SystemParameters.WorkArea;
        var area = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        var fromDevice = target.TransformFromDevice;
        return new Rect(fromDevice.Transform(new Point(area.Left, area.Top)), fromDevice.Transform(new Point(area.Right, area.Bottom)));
    }

    private void MoveVideoTo(double x, double y, bool animate = false)
    {
        double left = x - Inset, top = y - Inset;
        if (!animate)
        {
            StopMoveAnimation();
            Left = left;
            Top = top;
            return;
        }
        Glide(LeftProperty, left);
        Glide(TopProperty, top);
    }

    private void Glide(DependencyProperty property, double to)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            // Hand the final value back to the property so later drags start from it.
            if (_closing)
                return;
            BeginAnimation(property, null);
            SetValue(property, to);
        };
        BeginAnimation(property, animation);
    }

    /// <summary>Freezes a running snap animation where it is, so a new drag or resize starts from there.</summary>
    private void StopMoveAnimation()
    {
        double left = Left, top = Top;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = left;
        Top = top;
    }

    // ───────────────────────────── Mouse ─────────────────────────────

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons, the timeline and the grip handle their own clicks; anywhere else picks the window up.
        StopMoveAnimation();
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return; // The button was already released.
        }
        SettleOnScreen(animate: true);
    }

    private void Surface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        StopMoveAnimation();
        // Grow or shrink around the center.
        double centerX = Left + Inset + _videoWidth / 2, centerY = Top + Inset + VideoHeight / 2;
        SetVideoWidth(_videoWidth * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        MoveVideoTo(centerX - _videoWidth / 2, centerY - VideoHeight / 2);
        SettleOnScreen(animate: false);
    }

    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        StopMoveAnimation();
        _resizing = true;
        _resizeOrigin = e.GetPosition(this);
        _resizeStartWidth = _videoWidth;
        Grip.CaptureMouse();
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_resizing)
            return;
        // The top-left corner stays put; follow whichever axis the cursor moved more (in 16:9 terms).
        var delta = e.GetPosition(this) - _resizeOrigin;
        SetVideoWidth(_resizeStartWidth + Math.Max(delta.X, delta.Y * 16 / 9));
    }

    private void Grip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing)
            return;
        e.Handled = true;
        _resizing = false;
        Grip.ReleaseMouseCapture();
        SettleOnScreen(animate: true);
    }

    private void Seek_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement area || area.ActualWidth <= 0)
            return;
        double fraction = Math.Clamp(e.GetPosition(area).X / area.ActualWidth, 0, 1);
        Progress.BeginAnimation(LevelBar.ValueProperty, null);
        Progress.Value = fraction;
        SeekRequested?.Invoke(fraction);
    }

    private void Root_MouseEnter(object sender, MouseEventArgs e) => SetControls(true);

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_resizing)
            SetControls(false);
    }

    private void SetControls(bool show)
    {
        if (show == _controlsShown)
            return;
        _controlsShown = show;
        ControlsLayer.IsHitTestVisible = show;
        var fade = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 140 : 260));
        ControlsLayer.BeginAnimation(OpacityProperty, fade);
        Grip.BeginAnimation(OpacityProperty, fade);
        if (show)
            _hoverCheck.Start();
        else
            _hoverCheck.Stop();
    }

    private bool IsCursorOver()
    {
        if (!IsVisible || Surface.ActualWidth <= 0)
            return false;
        var cursor = NativeMethods.CursorPosition();
        var topLeft = Surface.PointToScreen(new Point(0, 0));
        var bottomRight = Surface.PointToScreen(new Point(Surface.ActualWidth, Surface.ActualHeight));
        return cursor.X >= topLeft.X && cursor.X <= bottomRight.X && cursor.Y >= topLeft.Y && cursor.Y <= bottomRight.Y;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke();
    private void Previous_Click(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke();
    private void Next_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void Return_Click(object sender, RoutedEventArgs e) => ReturnRequested?.Invoke();

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", Culture) : t.ToString(@"m\:ss", Culture);
}
