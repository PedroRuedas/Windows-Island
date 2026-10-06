using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WindowsIsland.Services;

/// <summary>Watches the default output device volume, following the user when they switch devices.</summary>
public sealed class VolumeService : IMMNotificationClient, IDisposable
{
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private bool _registered;

    /// <summary>(level 0..1, muted). Raised on a COM thread.</summary>
    public event Action<float, bool>? Changed;

    public void Start()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
        _registered = true;
        Attach();
    }

    private void Attach()
    {
        lock (_gate)
        {
            Detach();
            try
            {
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
            }
            catch (Exception ex)
            {
                // No output device (e.g. headphones unplugged on a desktop without speakers).
                App.Log(ex);
                _device = null;
            }
        }
    }

    private void Detach()
    {
        if (_device is null)
            return;
        try
        {
            _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
            _device.Dispose();
        }
        catch
        {
            // Device already invalid.
        }
        _device = null;
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data) => Changed?.Invoke(data.MasterVolume, data.Muted);

    // Re-attaching inside the COM callback can deadlock, so hop to the thread pool.
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
            Task.Run(Attach);
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        lock (_gate)
            Detach();
        if (_registered)
            _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
    }
}
