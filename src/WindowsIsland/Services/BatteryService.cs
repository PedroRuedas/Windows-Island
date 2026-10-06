using System.Windows.Media;
using System.Windows.Threading;
using WindowsIsland.Core;
using WindowsIsland.Interop;

namespace WindowsIsland.Services;

/// <summary>Shows charger plug/unplug and low-battery activities.</summary>
public sealed class BatteryService
{
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);

    private readonly IslandController _controller;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool? _wasOnAc;
    private int _lastPercent = -1;

    public BatteryService(IslandController controller)
    {
        _controller = controller;
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        Poll();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
            return;
        // 128 = no system battery (desktop PC), 255 = unknown percentage.
        if ((status.BatteryFlag & 128) != 0 || status.BatteryLifePercent > 100)
            return;

        bool onAc = status.ACLineStatus == 1;
        int percent = status.BatteryLifePercent;

        if (_wasOnAc is { } wasOnAc && wasOnAc != onAc)
        {
            _controller.Upsert(new IslandActivity
            {
                Id = "system.battery",
                Title = onAc ? "Carregando" : "Usando bateria",
                Icon = onAc ? "charging" : "battery",
                Accent = onAc ? Green : Colors.White,
                Progress = percent / 100.0,
                Duration = TimeSpan.FromSeconds(3.5),
                Priority = 70,
                Source = "system",
            });
        }
        else if (!onAc && _lastPercent >= 0)
        {
            foreach (int threshold in new[] { 20, 10, 5 })
            {
                if (_lastPercent > threshold && percent <= threshold)
                {
                    _controller.Upsert(new IslandActivity
                    {
                        Id = "system.battery",
                        Title = "Bateria fraca",
                        Subtitle = $"{percent}% restante. Conecte o carregador.",
                        Icon = "battery",
                        Accent = threshold <= 10 ? Red : Orange,
                        Progress = percent / 100.0,
                        Duration = TimeSpan.FromSeconds(6),
                        Priority = 80,
                        ExpandOnArrive = true,
                        Source = "system",
                    });
                    break;
                }
            }
        }

        _wasOnAc = onAc;
        _lastPercent = percent;
    }
}
