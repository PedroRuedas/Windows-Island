using System.Windows.Media;
using Windows.Devices.Enumeration;
using WindowsIsland.Core;

namespace WindowsIsland.Services;

/// <summary>
/// Like the iPhone with AirPods: when Bluetooth headphones connect, the island shows them with their battery level;
/// it also says when they disconnect and when their battery runs low (20%, 10%).
/// </summary>
/// <remarks>
/// Paired classic Bluetooth devices are watched as association endpoints; only audio ones (headphones, earbuds,
/// speakers) are announced. Windows doesn't put the battery on the endpoint: headsets report it over Hands-Free, and
/// Windows stores it on that device node, which shares the endpoint's container id.
/// </remarks>
public sealed class BluetoothService : IDisposable
{
    private static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);

    private const string ClassicBluetooth = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private const string IsConnected = "System.Devices.Aep.IsConnected";
    private const string ContainerId = "System.Devices.Aep.ContainerId";
    private const string MajorClass = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string BatteryLevel = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"; // DEVPKEY_Bluetooth_Battery
    private const ushort AudioVideo = 4;                                              // Class of Device: major class

    private static readonly TimeSpan InitialEnumeration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectedDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan BatteryPoll = TimeSpan.FromMinutes(2);
    // Headsets report their battery a moment after connecting.
    private static readonly int[] BatteryRetries = [0, 1500, 3000, 6000];

    private sealed class Headset
    {
        public required string Name;
        public Guid? Container;
        public bool IsAudio;
        public bool Connected;
        public DateTime ConnectedAt;
        public int? Battery;
    }

    private readonly IslandController _controller;
    private readonly Dictionary<string, Headset> _devices = new();
    private readonly object _lock = new();
    private DeviceWatcher? _watcher;
    private Timer? _poll;
    private bool _enumerated;
    private DateTime _startedAt;

    public BluetoothService(IslandController controller) => _controller = controller;

    public void Start()
    {
        try
        {
            _watcher = DeviceInformation.CreateWatcher(
                $"System.Devices.Aep.ProtocolId:=\"{ClassicBluetooth}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True",
                [IsConnected, ContainerId, MajorClass],
                DeviceInformationKind.AssociationEndpoint);
            // These run on a Windows thread pool thread, where an exception would take the whole island down.
            _watcher.Added += (sender, info) => Safely(() => OnAdded(sender, info));
            _watcher.Updated += (sender, update) => Safely(() => OnUpdated(sender, update));
            _watcher.Removed += (_, update) => { lock (_lock) _devices.Remove(update.Id); };
            // Headphones already connected when the island starts are known but not announced. EnumerationCompleted
            // waits for a full Bluetooth inquiry (~30 s), but paired devices arrive within a second: also count
            // the first few seconds as the initial enumeration.
            _watcher.EnumerationCompleted += (_, _) => { lock (_lock) _enumerated = true; };
            _startedAt = DateTime.Now;
            _watcher.Start();
            _poll = new Timer(_ => _ = PollBatteriesAsync(), null, BatteryPoll, BatteryPoll);
        }
        catch (Exception ex)
        {
            // No Bluetooth radio, or the service is off: nothing to show.
            App.Log($"Bluetooth indisponível: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _poll?.Dispose();
        if (_watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
            _watcher.Stop();
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        var device = new Headset
        {
            Name = info.Name,
            Container = info.Properties.TryGetValue(ContainerId, out var container) ? container as Guid? : null,
            IsAudio = info.Properties.TryGetValue(MajorClass, out var major) && major is ushort m && m == AudioVideo,
        };
        bool connected = info.Properties.TryGetValue(IsConnected, out var c) && c is true;
        lock (_lock)
            _devices[info.Id] = device;
        if (connected)
            SetConnected(info.Id, device, true);
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (!update.Properties.TryGetValue(IsConnected, out var c) || c is not bool connected)
            return;
        Headset? device;
        lock (_lock)
            _devices.TryGetValue(update.Id, out device);
        if (device is not null)
            SetConnected(update.Id, device, connected);
    }

    private void SetConnected(string id, Headset device, bool connected)
    {
        bool announce;
        lock (_lock)
        {
            if (device.Connected == connected)
                return;
            device.Connected = connected;
            announce = (_enumerated || DateTime.Now - _startedAt > InitialEnumeration) && device.IsAudio;
        }
        if (!device.IsAudio)
            return;

        if (!connected)
        {
            device.Battery = null;
            if (announce)
                _controller.Upsert(new IslandActivity
                {
                    Id = AlertId("disconnected", id),
                    Title = device.Name,
                    Subtitle = "Desconectado",
                    Icon = "headphones",
                    Accent = Colors.White,
                    Duration = TimeSpan.FromSeconds(2.5),
                    Priority = 70,
                    Source = "system",
                });
            return;
        }

        device.ConnectedAt = DateTime.Now;
        if (announce)
            ShowConnected(id, device, battery: null);
        _ = ReadBatteryAfterConnectAsync(id, device, announce);
    }

    private async Task ReadBatteryAfterConnectAsync(string id, Headset device, bool announce)
    {
        foreach (int delay in BatteryRetries)
        {
            await Task.Delay(delay);
            if (!device.Connected)
                return;
            if (await ReadBatteryAsync(device) is not { } battery)
                continue;
            device.Battery = battery;
            // Fill in the level while the "connected" pill is still up; later it would pop up out of nowhere.
            if (announce && DateTime.Now - device.ConnectedAt < ConnectedDuration)
                ShowConnected(id, device, battery);
            return;
        }
    }

    private void ShowConnected(string id, Headset device, int? battery) => _controller.Upsert(new IslandActivity
    {
        Id = AlertId("connected", id),
        Title = device.Name,
        Subtitle = battery is { } level ? $"Conectado · bateria {level}%" : "Conectado",
        Icon = "headphones",
        Accent = battery is { } b ? LevelColor(b) : Blue,
        Progress = battery / 100.0,
        // Filling in the battery doesn't restart the clock: the pill stays up 4 s in total.
        Duration = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, (ConnectedDuration - (DateTime.Now - device.ConnectedAt)).Ticks)),
        Priority = 70,
        ExpandOnArrive = true,
        Source = "system",
    });

    private async Task PollBatteriesAsync()
    {
        List<(string Id, Headset Device)> connected;
        lock (_lock)
            connected = _devices.Where(d => d.Value.Connected && d.Value.IsAudio).Select(d => (d.Key, d.Value)).ToList();
        foreach (var (id, device) in connected)
        {
            if (await ReadBatteryAsync(device) is not { } battery)
                continue;
            int? crossed = LowBatteryCrossed(device.Battery, battery);
            device.Battery = battery;
            if (crossed is null)
                continue;
            _controller.Upsert(new IslandActivity
            {
                Id = AlertId("low", id),
                Title = $"{device.Name}: bateria fraca",
                Subtitle = $"{battery}% restante.",
                Icon = "headphones",
                Accent = LevelColor(battery),
                Progress = battery / 100.0,
                Duration = TimeSpan.FromSeconds(6),
                Priority = 80,
                ExpandOnArrive = true,
                Source = "system",
            });
        }
    }

    private static async Task<int?> ReadBatteryAsync(Headset device)
    {
        if (device.Container is not { } container)
            return null;
        try
        {
            var nodes = await DeviceInformation.FindAllAsync(
                $"System.Devices.ContainerId:=\"{container:B}\"", [BatteryLevel], DeviceInformationKind.Device);
            foreach (var node in nodes)
                if (node.Properties.TryGetValue(BatteryLevel, out var value) && value is byte level && level <= 100)
                    return level;
        }
        catch (Exception ex)
        {
            App.Log($"Bateria do Bluetooth: {ex.Message}");
        }
        return null;
    }

    // One id per kind: a reconnect right after a disconnect must arrive as new, or the island wouldn't open for it.
    private static string AlertId(string kind, string deviceId) => $"system.bluetooth.{kind}:{deviceId}";

    private static void Safely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    internal static Color LevelColor(int battery) => battery switch
    {
        <= 10 => Red,
        <= 20 => Orange,
        _ => Green,
    };

    /// <summary>The low-battery threshold (20 or 10) crossed going from <paramref name="previous"/> to <paramref name="current"/>, if any.</summary>
    internal static int? LowBatteryCrossed(int? previous, int current)
    {
        if (previous is not { } before)
            return null;
        foreach (int threshold in new[] { 10, 20 })
            if (before > threshold && current <= threshold)
                return threshold;
        return null;
    }
}
