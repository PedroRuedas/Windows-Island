using System.Windows;
using System.Windows.Threading;
using WindowsIsland.Core;
using WindowsIsland.Interop;
using WindowsIsland.Services;

namespace WindowsIsland;

public partial class MainWindow
{
    // ───────────────────────────── Updates ─────────────────────────────

    private const string UpdateActivityId = "system.update";
    private readonly UpdateService _updates = new();
    private readonly DispatcherTimer _updateRetry = new() { Interval = TimeSpan.FromMinutes(1) };
    private PendingUpdate? _pendingUpdate;
    private bool _updateDeclined;   // Said no to the administrator prompt: don't ask again until next start.

    private void StartUpdates()
    {
        _updates.UpdateReady += update => Dispatcher.BeginInvoke(() => OnUpdateReady(update));
        _updateRetry.Tick += (_, _) =>
        {
            _updateRetry.Stop();
            if (_pendingUpdate is { } update)
                OnUpdateReady(update);
        };
        _updates.Start();
        AnnounceUpdated();
    }

    /// <summary>A verified installer is ready: install it now (auto-update) or offer it.</summary>
    private void OnUpdateReady(PendingUpdate update)
    {
        _pendingUpdate = update;
        if (!_settings.AutoUpdate || _updateDeclined)
        {
            OfferUpdate(update);
            return;
        }
        // Not in the middle of something: typing a question, waiting for an answer, a game or fullscreen video.
        if (_keyboard || _chat.IsRunning || NativeMethods.IsFullscreenAppRunning())
        {
            _updateRetry.Start();
            return;
        }

        _controller.Upsert(new IslandActivity
        {
            Id = UpdateActivityId,
            Title = $"Atualizando para a versão {update.Version.ToString(3)}",
            Subtitle = "Confirme no aviso do Windows. A ilha volta sozinha em alguns segundos.",
            Icon = "download",
            Accent = Green,
            Duration = TimeSpan.FromSeconds(6),
            Priority = 90,
            ExpandOnArrive = true,
            Source = "update",
        });
        // Give the island a moment to say what's about to happen before Windows asks.
        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            InstallPendingUpdate();
        };
        delay.Start();
    }

    private void InstallPendingUpdate()
    {
        if (_pendingUpdate is not { } update)
            return;
        switch (UpdateService.Install(update))
        {
            case UpdateService.InstallResult.Started:
                // The installer replaces our files and reopens the island when it's done.
                Close();
                break;
            case UpdateService.InstallResult.Declined:
                _updateDeclined = true;
                OfferUpdate(update);
                break;
            default:
                _pendingUpdate = null;
                _controller.Remove(UpdateActivityId);
                break;
        }
    }

    /// <summary>Stays in the island (as a page) until you click it to install.</summary>
    private void OfferUpdate(PendingUpdate update) => _controller.Upsert(new IslandActivity
    {
        Id = UpdateActivityId,
        Title = $"Versão {update.Version.ToString(3)} pronta",
        Subtitle = "Clique para atualizar o Windows Island.",
        Icon = "download",
        Accent = Green,
        Priority = 30,
        Source = "update",
    });

    /// <summary>Once, right after an update: what version this is now, with a link to what's new.</summary>
    private void AnnounceUpdated()
    {
        string current = UpdateService.CurrentVersion.ToString(3);
        string? previous = _settings.LastVersion;
        if (previous == current)
            return;
        ChangeSettings(s => s.LastVersion = current);
        if (previous is null)
            return;
        _controller.Upsert(new IslandActivity
        {
            Id = "system.updated",
            Title = $"Windows Island atualizado para {current}",
            Subtitle = "Clique para ver as novidades.",
            Icon = "check",
            Accent = Green,
            Duration = TimeSpan.FromSeconds(8),
            ExpandOnArrive = true,
            ActionUrl = $"https://github.com/PedroRuedas/Windows-Island/releases/tag/v{current}",
            Source = "system",
        });
    }

    private async void UpdateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not null)
        {
            InstallPendingUpdate();
            return;
        }
        string message;
        if (!UpdateService.IsInstalledBuild)
            message = "A atualização automática só funciona na versão instalada.";
        else if (await _updates.CheckAsync() is null)
            message = $"Você já está na versão mais recente ({UpdateService.CurrentVersion.ToString(3)}).";
        else
            return; // UpdateReady takes it from here.
        _controller.Upsert(new IslandActivity
        {
            Id = "system.update-check",
            Title = message,
            Icon = "check",
            Accent = Green,
            Duration = TimeSpan.FromSeconds(5),
            ExpandOnArrive = true,
            Source = "system",
        });
    }
}
