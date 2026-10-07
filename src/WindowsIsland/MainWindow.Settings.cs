using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WindowsIsland.Core;
using WindowsIsland.Services;

namespace WindowsIsland;

public partial class MainWindow
{
    // ───────────────────────────── Appearance & settings ─────────────────────────────

    /// <summary>Paints the rim (gradient border, thickness, glow, motion) and applies the other visual settings.</summary>
    private void ApplyAppearance()
    {
        var colors = _settings.BorderColors();
        Brush brush;
        double thickness;
        if (colors.Length == 0)
        {
            // Classic: the barely-there hairline.
            brush = new SolidColorBrush(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF));
            thickness = 1;
        }
        else
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            // Repeat the first color at the end so the loop is seamless when the gradient rotates.
            Color[] stops = colors.Length == 1 ? [colors[0], colors[0]] : [.. colors, colors[0]];
            for (int i = 0; i < stops.Length; i++)
                gradient.GradientStops.Add(new GradientStop(stops[i], (double)i / (stops.Length - 1)));

            var spin = new RotateTransform(0, 0.5, 0.5);
            gradient.RelativeTransform = spin;
            if (_settings.AnimateBorder)
            {
                var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(7)) { RepeatBehavior = RepeatBehavior.Forever };
                // The island is a layered window, so every frame is a full repaint: 30 fps is plenty for a slow spin.
                Timeline.SetDesiredFrameRate(turn, 30);
                spin.BeginAnimation(RotateTransform.AngleProperty, turn);
            }
            brush = gradient;
            thickness = _settings.BorderThickness;
        }

        IslandRim.BorderBrush = BubbleRim.BorderBrush = brush;
        IslandRim.BorderThickness = BubbleRim.BorderThickness = new Thickness(thickness);
        IslandRim.Effect = colors.Length > 0 && _settings.Glow
            ? new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = colors[colors.Length / 2],
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.75,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            }
            : null;

        IdleClock.Visibility = _settings.ShowIdleClock ? Visibility.Visible : Visibility.Hidden;
    }

    private void FillSettings()
    {
        if (_settingsBuilt)
            return;
        _settingsBuilt = true;

        var preset = IslandSettings.Presets.FirstOrDefault(p => p.Id == _settings.Border);
        BorderName.Text = _settings.Border == IslandSettings.Custom ? "Personalizada" : preset?.Name ?? "";

        PresetSwatches.Children.Clear();
        foreach (var option in IslandSettings.Presets)
            PresetSwatches.Children.Add(Swatch(option.Colors.Select(IslandSettings.ParseColor).ToArray(), option.Name,
                _settings.Border == option.Id, () => ChangeSettings(s => s.Border = option.Id), size: 30));
        PresetSwatches.Children.Add(Swatch(_settings.CustomColors.Select(IslandSettings.ParseColor).ToArray(), "Personalizada",
            _settings.Border == IslandSettings.Custom, () => ChangeSettings(s => s.Border = IslandSettings.Custom), size: 30, edit: true));

        bool custom = _settings.Border == IslandSettings.Custom;
        CustomColorsPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom)
        {
            FillPalette(CustomColor1, 0);
            FillPalette(CustomColor2, 1);
        }

        (_settings.BorderThickness switch { <= 1 => ThinBorder, >= 3 => ThickBorder, _ => MediumBorder }).IsChecked = true;
        AnimateToggle.IsChecked = _settings.AnimateBorder;
        GlowToggle.IsChecked = _settings.Glow;
        ClockToggle.IsChecked = _settings.ShowIdleClock;
        StartupToggle.IsChecked = StartupRegistration.IsEnabled;
        AutoUpdateToggle.IsChecked = _settings.AutoUpdate;
        AutoUpdateLabel.ToolTip = $"Versão {UpdateService.CurrentVersion.ToString(3)}. As atualizações vêm do GitHub, assinadas pelo projeto, e pedem sua confirmação de administrador.";

        // Border options mean nothing for the classic hairline.
        bool hasGradient = _settings.Border != IslandSettings.NoBorder;
        foreach (var control in new UIElement[] { ThinBorder, MediumBorder, ThickBorder, AnimateToggle, GlowToggle })
        {
            control.IsEnabled = hasGradient;
            control.Opacity = hasGradient ? 1 : 0.35;
        }
    }

    private void FillPalette(Panel host, int index)
    {
        host.Children.Clear();
        foreach (string hex in IslandSettings.Palette)
        {
            string color = hex;
            host.Children.Add(Swatch([IslandSettings.ParseColor(hex)], hex,
                string.Equals(_settings.CustomColors[index], hex, StringComparison.OrdinalIgnoreCase),
                () => ChangeSettings(s => s.CustomColors[index] = color), size: 22));
        }
    }

    /// <summary>A round color chip (gradient when several colors) with a white ring when selected.</summary>
    private Border Swatch(Color[] colors, string name, bool selected, Action pick, double size, bool edit = false)
    {
        Brush fill;
        if (colors.Length == 0)
        {
            fill = Brushes.Black;
        }
        else if (colors.Length == 1)
        {
            fill = new SolidColorBrush(colors[0]);
        }
        else
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            for (int i = 0; i < colors.Length; i++)
                gradient.GradientStops.Add(new GradientStop(colors[i], (double)i / (colors.Length - 1)));
            fill = gradient;
        }

        var chip = new Border
        {
            Width = size - 8,
            Height = size - 8,
            CornerRadius = new CornerRadius((size - 8) / 2),
            Background = fill,
            // The classic swatch is black-on-black: outline it so it's visible.
            BorderBrush = colors.Length == 0 ? new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)) : null,
            BorderThickness = new Thickness(colors.Length == 0 ? 1 : 0),
        };
        if (edit)
        {
            chip.Child = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 10,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var ring = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Margin = new Thickness(0, 0, 6, 4),
            BorderThickness = new Thickness(2),
            BorderBrush = selected ? Brushes.White : Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = name,
            Child = chip,
        };
        ring.MouseEnter += (_, _) => { if (!selected) ring.BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)); };
        ring.MouseLeave += (_, _) => { if (!selected) ring.BorderBrush = Brushes.Transparent; };
        ring.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            pick();
        };
        return ring;
    }

    private void ChangeSettings(Action<IslandSettings> change)
    {
        change(_settings);
        _settings.Save();
        ApplyAppearance();
        _settingsBuilt = false; // Rebuild the page so selections and the custom-color panel follow.
        Refresh();
    }

    private void Thickness_Checked(object sender, RoutedEventArgs e)
    {
        if (!_settingsBuilt || sender is not RadioButton { Tag: string tag } || !double.TryParse(tag, CultureInfo.InvariantCulture, out double value))
            return;
        if (Math.Abs(_settings.BorderThickness - value) > 0.01)
            ChangeSettings(s => s.BorderThickness = value);
    }

    private void SettingToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = sender is CheckBox { IsChecked: true };
        if (sender == StartupToggle)
        {
            try
            {
                StartupRegistration.Set(on);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                StartupToggle.IsChecked = StartupRegistration.IsEnabled;
            }
            return;
        }
        ChangeSettings(s =>
        {
            if (sender == AnimateToggle)
                s.AnimateBorder = on;
            else if (sender == GlowToggle)
                s.Glow = on;
            else if (sender == ClockToggle)
                s.ShowIdleClock = on;
            else if (sender == AutoUpdateToggle)
                s.AutoUpdate = on;
        });
    }
}
