using System.Windows.Media;
using System.Windows.Threading;
using global::Windows.UI.Notifications;
using global::Windows.UI.Notifications.Management;
using WindowsIsland.Core;

namespace WindowsIsland.Services;

public enum NotificationAccess
{
    /// <summary>The exe runs without package identity; the sparse package must be registered first.</summary>
    NoIdentity,

    /// <summary>The user (or policy) blocked notification access in Windows Settings.</summary>
    Denied,

    Allowed,

    Unavailable,
}

/// <summary>
/// Mirrors Windows toast notifications (WhatsApp, Teams, Outlook, Discord...) into the island.
/// Polls instead of relying on NotificationChanged, which is unreliable for non-UWP apps.
/// </summary>
public sealed class NotificationService
{
    private const int MaxBurst = 3;
    private const int MaxHistory = 20;
    private static readonly TimeSpan UnreadWindow = TimeSpan.FromMinutes(5);
    public static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);

    private readonly IslandController _controller;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<uint> _known = new();
    private readonly Dictionary<string, ImageSource?> _logos = new();
    private readonly List<NotificationItem> _history = new();
    private UserNotificationListener? _listener;
    private bool _polling;

    public NotificationService(IslandController controller)
    {
        _controller = controller;
        _poll.Tick += async (_, _) => await PollAsync();
    }

    /// <summary>Raised on the UI thread when the history changes.</summary>
    public event Action? Changed;

    public NotificationAccess Access { get; private set; } = NotificationAccess.NoIdentity;

    /// <summary>Most recent first. UI thread only.</summary>
    public IReadOnlyList<NotificationItem> History => _history;

    /// <summary>Something arrived recently that the user hasn't looked at in the island yet.</summary>
    public bool HasRecentUnread => _history.Any(n => !n.Read && DateTime.Now - n.ReceivedAt < UnreadWindow);

    public int UnreadCount => _history.Count(n => !n.Read);

    public void MarkAllRead()
    {
        if (_history.All(n => n.Read))
            return;
        foreach (var item in _history)
            item.Read = true;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _history.Clear();
        Changed?.Invoke();
    }

    /// <summary>Brings the sending app (WhatsApp, Teams...) to the front and drops the item.</summary>
    public void Open(NotificationItem item)
    {
        if (!string.IsNullOrEmpty(item.AppUserModelId))
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"shell:AppsFolder\\{item.AppUserModelId}");
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
        _history.Remove(item);
        _controller.Remove(ActivityId(item.Id));
        Changed?.Invoke();
    }

    public bool Enabled
    {
        get => _poll.IsEnabled;
        set
        {
            if (value && Access == NotificationAccess.Allowed)
                _poll.Start();
            else
                _poll.Stop();
        }
    }

    /// <summary>Must run on the UI thread: Windows may show a consent prompt.</summary>
    public async Task<NotificationAccess> StartAsync()
    {
        if (!PackageRegistration.HasIdentity)
            return Access = NotificationAccess.NoIdentity;
        try
        {
            _listener = UserNotificationListener.Current;
            var status = await _listener.RequestAccessAsync();
            if (status != UserNotificationListenerAccessStatus.Allowed)
                return Access = NotificationAccess.Denied;

            // Don't replay what is already sitting in the notification center.
            foreach (var existing in await _listener.GetNotificationsAsync(NotificationKinds.Toast))
                _known.Add(existing.Id);

            Access = NotificationAccess.Allowed;
            _poll.Start();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Access = NotificationAccess.Unavailable;
        }
        return Access;
    }

    private async Task PollAsync()
    {
        if (_polling || _listener is null)
            return;
        _polling = true;
        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var present = new HashSet<uint>();
            var arrived = new List<UserNotification>();
            foreach (var notification in notifications)
            {
                present.Add(notification.Id);
                if (_known.Add(notification.Id))
                    arrived.Add(notification);
            }

            // After sleep/resume many can arrive at once; only surface the newest few.
            foreach (var notification in arrived.OrderBy(n => n.CreationTime).TakeLast(MaxBurst))
                await ShowAsync(notification);

            // Dismissed in the Action Center (or by the app) → dismiss in the island too.
            foreach (uint gone in _known.Where(id => !present.Contains(id)).ToList())
            {
                _known.Remove(gone);
                _controller.Remove(ActivityId(gone));
                if (_history.RemoveAll(n => n.Id == gone) > 0)
                    Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task ShowAsync(UserNotification notification)
    {
        string appName = "", aumid = "";
        ImageSource? logo = null;
        try
        {
            aumid = notification.AppInfo.AppUserModelId ?? "";
            var display = notification.AppInfo.DisplayInfo;
            appName = display.DisplayName;
            if (!_logos.TryGetValue(appName, out logo))
            {
                logo = await ImageLoader.LoadAsync(display.GetLogo(new global::Windows.Foundation.Size(64, 64)), 64);
                _logos[appName] = logo;
            }
        }
        catch
        {
            // Some Win32 senders expose no AppInfo; show the text anyway.
        }

        if (appName.Equals("Windows Island", StringComparison.OrdinalIgnoreCase))
            return;

        var texts = new List<string>();
        try
        {
            var binding = notification.Notification.Visual.GetBinding("ToastGeneric");
            if (binding is not null)
                texts.AddRange(binding.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        if (texts.Count == 0 && string.IsNullOrEmpty(appName))
            return;

        string title = texts.FirstOrDefault() ?? appName;
        string? body = texts.Count > 1 ? string.Join(" · ", texts.Skip(1)) : null;

        _history.Insert(0, new NotificationItem(notification.Id, appName, aumid, logo, title, body, DateTime.Now));
        if (_history.Count > MaxHistory)
            _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
        Changed?.Invoke();

        _controller.Upsert(new IslandActivity
        {
            Id = ActivityId(notification.Id),
            Caption = appName,
            Title = title,
            Subtitle = body,
            Image = logo,
            Icon = "bell",
            Accent = Blue,
            Duration = TimeSpan.FromSeconds(6),
            Priority = 60,
            ExpandOnArrive = true,
            // Clicking the alert opens the app that sent it.
            ActionUrl = string.IsNullOrEmpty(aumid) ? null : $"shell:AppsFolder\\{aumid}",
            Source = "notification",
        });
    }

    private static string ActivityId(uint notificationId) => $"notification.{notificationId}";
}

public sealed record NotificationItem(
    uint Id,
    string AppName,
    string AppUserModelId,
    ImageSource? Logo,
    string Title,
    string? Body,
    DateTime ReceivedAt)
{
    public bool Read { get; set; }
}
