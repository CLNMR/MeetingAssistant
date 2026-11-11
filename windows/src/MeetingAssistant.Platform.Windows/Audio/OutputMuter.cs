using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MeetingAssistant.Platform.Windows.Audio;

/// Silences the default output while dictating and restores it exactly (spec 04 §1.6): mute,
/// never pause. Already muted → nothing. Else set the endpoint's mute flag; if that does not stick,
/// zero the master volume (if > 0) and remember it. Restore: unmute if muted by the flag; restore the
/// volume only if it is still 0 (the user did not change it meanwhile). Not thread-safe; call from
/// the dictation controller.
public sealed class OutputMuter
{
    MMDevice? _device;
    float? _savedVolume;
    bool _mutedByFlag;

    public bool IsActive => _device is not null;

    public void Mute()
    {
        if (_device is not null) return;
        MMDevice? dev = null;
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console)) return;
            dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            var vol = dev.AudioEndpointVolume;
            if (vol.Mute) { dev.Dispose(); return; }                     // already silent
            try { vol.Mute = true; } catch (COMException) { }
            if (vol.Mute)
            {
                _device = dev; _mutedByFlag = true;
                return;
            }
            var v = vol.MasterVolumeLevelScalar;
            if (v > 0)
            {
                vol.MasterVolumeLevelScalar = 0;
                _device = dev; _savedVolume = v;
                return;
            }
            dev.Dispose();
        }
        catch (COMException)
        {
            dev?.Dispose();
        }
    }

    public void Restore()
    {
        var dev = _device;
        if (dev is null) return;
        try
        {
            var vol = dev.AudioEndpointVolume;
            if (_mutedByFlag) vol.Mute = false;
            if (_savedVolume is float v && vol.MasterVolumeLevelScalar == 0) vol.MasterVolumeLevelScalar = v;
        }
        catch (COMException) { }
        finally
        {
            dev.Dispose();
            _device = null; _savedVolume = null; _mutedByFlag = false;
        }
    }
}
