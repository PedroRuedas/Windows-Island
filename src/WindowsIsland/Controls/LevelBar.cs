using System.Windows;
using System.Windows.Media;

namespace WindowsIsland.Controls;

/// <summary>Pill-shaped progress/level bar.</summary>
public sealed class LevelBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LevelBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(LevelBar),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(LevelBar),
        new FrameworkPropertyMetadata(CreateTrack(), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, r = h / 2;
        if (w <= 0 || h <= 0)
            return;

        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), r, r);
        double v = Math.Clamp(double.IsFinite(Value) ? Value : 0, 0, 1);
        if (v > 0)
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(h, w * v), h), r, r);
    }

    private static Brush CreateTrack()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        brush.Freeze();
        return brush;
    }
}
