using System.Runtime.InteropServices;
using Mystral.Infrastructure.Audio;

namespace Mystral.Services;

public sealed class VolumeService : IDisposable
{
    private IAudioEndpointVolume? _endpoint;
    private bool _disposed;

    public VolumeService()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            if (device is not null)
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                device.Activate(ref iid, 0, IntPtr.Zero, out var activated);
                _endpoint = activated as IAudioEndpointVolume;
            }
        }
        catch
        {
            _endpoint = null;
        }
    }

    public bool IsAvailable => _endpoint is not null;

    public float Volume
    {
        get
        {
            if (_endpoint is null) return 0.5f;
            _endpoint.GetMasterVolumeLevelScalar(out var level);
            return level;
        }
        set
        {
            _endpoint?.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), Guid.Empty);
        }
    }

    public bool IsMuted
    {
        get
        {
            if (_endpoint is null) return false;
            _endpoint.GetMute(out var muted);
            return muted;
        }
        set
        {
            _endpoint?.SetMute(value, Guid.Empty);
        }
    }

    public void ToggleMute()
    {
        IsMuted = !IsMuted;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_endpoint is not null)
        {
            Marshal.ReleaseComObject(_endpoint);
            _endpoint = null;
        }
    }
}
