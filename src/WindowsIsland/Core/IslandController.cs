using System.Windows.Threading;

namespace WindowsIsland.Core;

/// <summary>
/// Holds every live activity plus the current media session and decides what the island shows.
/// All public members are safe to call from any thread; state is only mutated on the UI thread.
/// </summary>
public sealed class IslandController
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, IslandActivity> _activities = new();
    private readonly Dictionary<string, DispatcherTimer> _expiry = new();

    public IslandController(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Raised (on the UI thread) whenever anything visible changes.</summary>
    public event Action? Changed;

    /// <summary>Raised (on the UI thread) when a new activity asks for attention.</summary>
    public event Action<IslandActivity>? Arrived;

    public MediaInfo? Media { get; private set; }

    /// <summary>
    /// Highest-priority transient activity (one with a <see cref="IslandActivity.Duration"/>): volume, alerts,
    /// notifications. These take over the island until they expire. UI thread only.
    /// </summary>
    public IslandActivity? CurrentAlert => _activities.Values
        .Where(a => a.Duration is not null)
        .OrderByDescending(a => a.Priority)
        .ThenByDescending(a => a.UpdatedAt)
        .FirstOrDefault();

    /// <summary>Activities without a duration (downloads, timers...): each one becomes a page you can switch to.</summary>
    public IReadOnlyList<IslandActivity> PersistentActivities => _activities.Values
        .Where(a => a.Duration is null)
        .OrderByDescending(a => a.Priority)
        .ThenBy(a => a.Id, StringComparer.Ordinal)
        .ToList();

    public void Upsert(IslandActivity activity) => OnUi(() =>
    {
        bool isNew = !_activities.ContainsKey(activity.Id);
        activity.UpdatedAt = DateTime.Now;
        _activities[activity.Id] = activity;
        ResetExpiry(activity);
        Changed?.Invoke();
        if (isNew && activity.ExpandOnArrive)
            Arrived?.Invoke(activity);
    });

    public bool Remove(string id) => _dispatcher.Invoke(() =>
    {
        if (_expiry.Remove(id, out var timer))
            timer.Stop();
        if (!_activities.Remove(id))
            return false;
        Changed?.Invoke();
        return true;
    });

    public void SetMedia(MediaInfo? media) => OnUi(() =>
    {
        Media = media;
        Changed?.Invoke();
    });

    public IReadOnlyList<IslandActivity> Snapshot() => _dispatcher.Invoke(() =>
        (IReadOnlyList<IslandActivity>)_activities.Values.OrderByDescending(a => a.Priority).ToList());

    private void ResetExpiry(IslandActivity activity)
    {
        if (_expiry.Remove(activity.Id, out var old))
            old.Stop();
        if (activity.Duration is not { } duration)
            return;

        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Only expire if no newer update replaced this timer.
            if (_expiry.TryGetValue(activity.Id, out var current) && current == timer)
                Remove(activity.Id);
        };
        _expiry[activity.Id] = timer;
        timer.Start();
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }
}
