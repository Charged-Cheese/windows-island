using System.Runtime.InteropServices;

namespace DynamicIsland.Core.Interop;

/// <summary>通过 Core Audio API 控制系统主音量与静音。</summary>
internal sealed class AudioEndpoint : IDisposable
{
    private const int CLSCTX_ALL = 23;
    private const int eRender = 0;
    private const int eConsole = 0;

    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioEndpointVolume? _volume;

    public bool IsAvailable { get; private set; }

    public AudioEndpoint()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = _enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out _device);
            if (hr != 0 || _device is null)
            {
                return;
            }

            Guid iid = typeof(IAudioEndpointVolume).GUID;
            hr = _device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object volumeObj);
            if (hr != 0 || volumeObj is null)
            {
                return;
            }

            _volume = (IAudioEndpointVolume)volumeObj;
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    /// <summary>0.0 – 1.0</summary>
    public float GetVolume()
    {
        if (_volume is null)
        {
            return 0;
        }

        return _volume.GetMasterVolumeLevelScalar(out float level) == 0 ? level : 0;
    }

    public void SetVolume(float level)
    {
        if (_volume is null)
        {
            return;
        }

        level = Math.Clamp(level, 0f, 1f);
        _volume.SetMasterVolumeLevelScalar(level, Guid.Empty);
    }

    public bool GetMute()
    {
        if (_volume is null)
        {
            return false;
        }

        return _volume.GetMute(out bool mute) == 0 && mute;
    }

    public void SetMute(bool mute)
    {
        _volume?.SetMute(mute, Guid.Empty);
    }

    public void Dispose()
    {
        if (_volume is not null)
        {
            Marshal.ReleaseComObject(_volume);
            _volume = null;
        }

        if (_device is not null)
        {
            Marshal.ReleaseComObject(_device);
            _device = null;
        }

        if (_enumerator is not null)
        {
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    // ---------------- COM 声明 ----------------

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        int GetDevice(string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint channelCount);
        int SetMasterVolumeLevel(float levelDb, Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, Guid eventContext);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float levelDb, Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channel, float level, Guid eventContext);
        int GetChannelVolumeLevel(uint channel, out float levelDb);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        int GetVolumeStepInfo(out uint step, out uint stepCount);
        int VolumeStepUp(Guid eventContext);
        int VolumeStepDown(Guid eventContext);
        int QueryHardwareSupport(out uint hardwareSupportMask);
        int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }
}
