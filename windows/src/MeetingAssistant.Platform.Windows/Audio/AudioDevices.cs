using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MeetingAssistant.Platform.Windows.Audio;

/// An input device for the "Microphone" picker. <see cref="Id"/> is the WASAPI endpoint id, stored
/// in the preference `inputDeviceUID` (spec 01-recording, "Microphone").
public sealed record AudioInputDevice(string Id, string Name);

/// Lists and classifies audio endpoints (spec 01-recording, "Microphone" and the floating window's
/// Bluetooth rule).
public static class AudioDevices
{
    static readonly PropertyKey EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    /// Active capture endpoints, without virtual call-app devices ("Microsoft Teams Audio", "ZoomAudioDevice").
    public static List<AudioInputDevice> InputDevices()
    {
        var list = new List<AudioInputDevice>();
        using var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (d)
            {
                string name;
                try { name = d.FriendlyName; } catch (COMException) { name = d.ID; }
                if (IsVirtualCallDevice(name)) continue;
                list.Add(new AudioInputDevice(d.ID, name));
            }
        }
        return list;
    }

    /// Names of virtual devices of call apps, which are never offered or used.
    public static bool IsVirtualCallDevice(string name) =>
        name.Contains("Microsoft Teams Audio", StringComparison.Ordinal) || name.Contains("ZoomAudioDevice", StringComparison.Ordinal);

    /// The default input endpoint id (role Console), or null when there is none.
    public static string? DefaultInputId()
    {
        using var en = new MMDeviceEnumerator();
        if (!en.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)) return null;
        using var d = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        return d.ID;
    }

    /// The chosen device while it is an active, offered input; otherwise the default input.
    public static string? Resolve(string? preferredId)
    {
        if (!string.IsNullOrEmpty(preferredId) && InputDevices().Any(d => d.Id == preferredId)) return preferredId;
        return DefaultInputId();
    }

    /// Bus enumerator of the endpoint's device: "BTHENUM"/"BTHLEDevice" (Bluetooth), "USB", "HDAUDIO"
    /// (built-in), or null when unknown.
    public static string? Enumerator(string endpointId)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var d = en.GetDevice(endpointId);
            var props = d.Properties;
            return props.Contains(EnumeratorName) ? props[EnumeratorName].Value as string : null;
        }
        catch (COMException) { return null; }
    }

    public static bool IsBluetooth(string endpointId) =>
        Enumerator(endpointId) is { } e && (e.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) || e.Contains("BLUETOOTH", StringComparison.OrdinalIgnoreCase));

    /// For always-on listening: a Bluetooth input would switch the headset into call mode, so use
    /// another mic instead — built-in (HDAUDIO and other non-USB buses) first, then USB; the
    /// Bluetooth one only when nothing else exists. Returns the id to use (null = default).
    public static string? NonBluetoothInput(string? preferredId)
    {
        var dev = preferredId ?? DefaultInputId();
        if (dev is null || !IsBluetooth(dev)) return preferredId;
        var candidates = InputDevices().Where(d => !IsBluetooth(d.Id)).ToList();
        string? Bus(string id) => Enumerator(id)?.ToUpperInvariant();
        return candidates.FirstOrDefault(d => Bus(d.Id) != "USB")?.Id
               ?? candidates.FirstOrDefault()?.Id
               ?? preferredId;
    }
}

/// Forwards endpoint notifications (device added/removed/state, default device, format property)
/// to a callback. The callback must only schedule work: audio calls inside a notification can deadlock.
internal sealed class DeviceNotifications : IMMNotificationClient, IDisposable
{
    /// PKEY_AudioEngine_DeviceFormat: the shared-mode format of an endpoint changed.
    static readonly PropertyKey DeviceFormat = new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);
    readonly MMDeviceEnumerator _enumerator = new();
    readonly Action<string, string?> _changed;   // (what, deviceId)
    bool _registered;

    public DeviceNotifications(Action<string, string?> changed)
    {
        _changed = changed;
        _registered = _enumerator.RegisterEndpointNotificationCallback(this) == 0;
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed("state", deviceId);
    public void OnDeviceAdded(string pwstrDeviceId) => _changed("added", pwstrDeviceId);
    public void OnDeviceRemoved(string deviceId) => _changed("removed", deviceId);
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (role == Role.Console) _changed(flow == DataFlow.Render ? "defaultRender" : "defaultCapture", defaultDeviceId);
    }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        if (key.formatId == DeviceFormat.formatId && key.propertyId == DeviceFormat.propertyId) _changed("format", pwstrDeviceId);
    }

    public void Dispose()
    {
        if (_registered) { _enumerator.UnregisterEndpointNotificationCallback(this); _registered = false; }
        _enumerator.Dispose();
    }
}
