using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WindowsIsland.Controls;
using WindowsIsland.Core;
using WindowsIsland.Services;

namespace WindowsIsland;

public partial class MainWindow
{
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
        // YouTube in a browser: the video takes the artwork's place, above the title (unless it's pinned to its own window).
        bool pinned = _pip is not null;
        bool video = !pinned && CurrentVideo(media) is not null;
        VideoArea.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        PinnedBar.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
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
        UsageTodaySub.Text = Responses(usage.TodayResponses);
        UsageWeek.Text = FormatTokens(usage.WeekTokens);
        UsageWeekSub.Text = Responses(usage.WeekResponses);
        UsageRecent.Text = FormatTokens(usage.Last5hTokens);
        BuildUsageChart(usage);

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
                ToolTip = page.Key switch { "media" => "Música", "claude" => "Claude Code", "notifications" => "Notificações", AskKey => "Perguntar ao Claude", ShelfKey => "Prateleira", SettingsKey => "Personalizar", ClockKey => "Relógio", _ => page.Activity?.Title },
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

    // ───────────────────────────── Claude usage chart ─────────────────────────────

    private static string Responses(int count) => count == 1 ? "1 resposta" : $"{count.ToString("N0", Culture)} respostas";

    /// <summary>
    /// Tokens per day for the last 7 days: one hue (Claude orange), today at full strength, earlier days softer,
    /// top-rounded bars on a shared baseline, exact values in each column's tooltip.
    /// </summary>
    private void BuildUsageChart(ClaudeUsage usage)
    {
        string signature = string.Join("|", usage.Daily.Select(d => $"{d.Date:yyyyMMdd}:{d.Tokens}"));
        if (signature == _usageSignature)
            return;
        _usageSignature = signature;

        const double maxBar = 40;
        long peak = Math.Max(1, usage.Daily.Max(d => d.Tokens));
        var accent = ClaudeService.Orange;

        UsageChart.Children.Clear();
        UsageChart.ColumnDefinitions.Clear();
        for (int i = 0; i < usage.Daily.Count; i++)
        {
            var day = usage.Daily[i];
            bool isToday = i == usage.Daily.Count - 1;
            UsageChart.ColumnDefinitions.Add(new ColumnDefinition());

            double height = day.Tokens == 0 ? 2 : Math.Max(4, maxBar * day.Tokens / peak);
            var bar = new Border
            {
                Width = 26,
                Height = height,
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Background = day.Tokens == 0
                    ? new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))
                    : new SolidColorBrush(isToday ? accent : Color.FromArgb(0xA6, accent.R, accent.G, accent.B)),
            };
            string dayName = Culture.DateTimeFormat.GetAbbreviatedDayName(day.Date.DayOfWeek).TrimEnd('.');
            var label = new TextBlock
            {
                Text = isToday ? "hoje" : dayName,
                FontSize = 10.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = isToday ? Brushes.White : (Brush)FindResource("FaintText"),
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
            };

            // The whole column is the hover target, not just the (possibly tiny) bar.
            var column = new Grid
            {
                Background = Brushes.Transparent,
                ToolTip = $"{Culture.TextInfo.ToTitleCase(day.Date.ToString("dddd, d/MM", Culture))}\n{FormatTokens(day.Tokens)} tokens · {Responses(day.Responses)}",
            };
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(maxBar) });
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(label, 1);
            column.Children.Add(bar);
            column.Children.Add(label);
            Grid.SetColumn(column, i);
            UsageChart.Children.Add(column);
        }
    }
}
