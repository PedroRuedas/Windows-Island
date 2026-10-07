using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WindowsIsland;

public partial class MainWindow
{
    // ───────────────────────────── Black hole (hide / show) ─────────────────────────────

    private void ToggleIsland()
    {
        if (_settings.Hidden)
            ShowIsland();
        else
            SwallowIsland();
    }

    /// <summary>The island spins and shrinks into its own center, like falling into a black hole, then hides.</summary>
    private void SwallowIsland()
    {
        if (_settings.Hidden || _swallowing)
            return;
        _settings.Hidden = true;
        _settings.Save();
        _tray?.SetIslandVisible(false);

        _hoverDelay.Stop();
        _leaveDelay.Stop();
        _hoverExpanded = false;
        _ = _mirror?.PauseAsync();

        if (!IsVisible)
            return;
        _swallowing = true;
        var duration = TimeSpan.FromMilliseconds(460);
        var fall = new CubicEase { EasingMode = EasingMode.EaseIn };
        var shrink = new DoubleAnimation(0, duration) { EasingFunction = fall };
        shrink.Completed += (_, _) =>
        {
            _swallowing = false;
            if (_settings.Hidden)
                Hide();
        };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        RootSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, -200, duration) { EasingFunction = fall });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
    }

    /// <summary>The reverse: the island is spat back out of the singularity with a little overshoot.</summary>
    private void ShowIsland()
    {
        if (!_settings.Hidden)
            return;
        _settings.Hidden = false;
        _settings.Save();
        _tray?.SetIslandVisible(true);

        _swallowing = false;
        Show();
        Refresh();
        var duration = TimeSpan.FromMilliseconds(560);
        var grow = new DoubleAnimation(0, 1, duration) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        RootSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(200, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
    }

    private void HideMenuItem_Click(object sender, RoutedEventArgs e) => SwallowIsland();
}
