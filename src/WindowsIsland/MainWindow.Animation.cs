using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace WindowsIsland;

public partial class MainWindow
{
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
            ViewKind.SettingsExpanded => (400.0, MeasureHeight(SettingsView), 36.0),
            ViewKind.AskExpanded => (400.0, MeasureHeight(AskView), 36.0),
            ViewKind.ShelfExpanded => (400.0, MeasureHeight(ShelfView), 36.0),
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
}
