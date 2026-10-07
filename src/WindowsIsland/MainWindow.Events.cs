using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsIsland.Core;
using WindowsIsland.Interop;
using WindowsIsland.Services;

namespace WindowsIsland;

public partial class MainWindow
{
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

        // The pinned video follows the browser twice a second, whether or not the island itself is visible.
        UpdatePip();

        // Mouse-leave is not always delivered to a no-activate window (e.g. right after clicking a control
        // that captured the mouse). Self-heal: if the cursor is gone, start the normal collapse.
        if (_hoverExpanded && !_leaveDelay.IsEnabled && !_keyboard && !IsCursorOverIsland())
            _leaveDelay.Start();

        // Once a second: elapsed timers ("2:31"), "há 3 min" labels, stale Claude sessions.
        if (_tickCount % 2 == 0 && IsVisible)
        {
            _claude.Tick();
            Refresh();
        }

        // Get out of the way of games, fullscreen video and presentations (unless the user already hid it).
        if (_tickCount % 4 == 0 && !_settings.Hidden && !_swallowing)
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
            // "Versão x pronta": install it.
            if (activity.Source == "update")
            {
                _updateDeclined = false;
                InstallPendingUpdate();
                return;
            }
            // "Claude respondeu": open the conversation.
            if (activity.Source == AskKey)
            {
                _controller.Remove(activity.Id);
                _hoverDelay.Stop();
                _selectedKey = AskKey;
                _hoverExpanded = true;
                Refresh();
                return;
            }
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
}
