using DynamicIsland.Core.Interop;

namespace DynamicIsland.Core.Services;

/// <summary>系统主音量控制。</summary>
public sealed class VolumeService : IDisposable
{
    private readonly AudioEndpoint _endpoint = new();

    public bool IsAvailable => _endpoint.IsAvailable;

    public float Volume => _endpoint.GetVolume();

    public bool IsMuted => _endpoint.GetMute();

    public void SetVolume(float level) => _endpoint.SetVolume(level);

    public void Adjust(float delta) => _endpoint.SetVolume(Volume + delta);

    public void ToggleMute() => _endpoint.SetMute(!IsMuted);

    public void Dispose() => _endpoint.Dispose();
}
