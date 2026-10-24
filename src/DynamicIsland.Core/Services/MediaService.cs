using DynamicIsland.Core.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Core.Services;

/// <summary>通过 Windows SMTC（GSMTC）接管系统当前媒体会话。</summary>
public sealed class MediaService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private List<GlobalSystemMediaTransportControlsSession> _sessions = new();
    private GlobalSystemMediaTransportControlsSession? _manualSession;
    private Timer? _timer;
    private string _cachedCoverKey = string.Empty;
    private byte[]? _cachedCover;

    public TrackInfo Current { get; private set; } = TrackInfo.Empty;

    /// <summary>当前可用的媒体会话数量（多个播放器同时打开时 > 1）。</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>曲目信息变化（含进度刷新）。</summary>
    public event EventHandler<TrackInfo>? Updated;

    /// <summary>换歌（identity 变化）。</summary>
    public event EventHandler<TrackInfo>? TrackChanged;

    public bool IsReady => _manager is not null;

    public async Task<bool> InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) =>
            {
                // 用户在其他播放器里开始播放时，放弃手动锁定，跟随系统当前会话
                _manualSession = null;
                _ = RefreshAsync();
            };
            await RefreshAsync();

            _timer = new Timer(_ => _ = RefreshAsync(), null, 1000, 450);
            LogService.Info("媒体服务已就绪（GSMTC）");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("媒体服务初始化失败", ex);
            return false;
        }
    }

    public async Task RefreshAsync()
    {
        if (_manager is null || !await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            RefreshSessions();

            var session = ResolveSession();
            if (session is null)
            {
                if (Current.HasSession)
                {
                    SetTrack(TrackInfo.Empty, force: true);
                }

                return;
            }

            _session = session;

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            bool playing = playback.PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            // GSMTC 的 Position 是播放器「上报那一刻」的快照，播放器通常每秒才上报一次，
            // 直接用会让歌词明显滞后。这里用 LastUpdatedTime 把上报之后的这段时间补上，
            // 得到与真实播放位置基本一致的插值。
            TimeSpan position = timeline.Position;
            if (playing)
            {
                var age = DateTimeOffset.Now - timeline.LastUpdatedTime;
                if (age > TimeSpan.Zero && age < TimeSpan.FromSeconds(10))
                {
                    position += age;
                }

                TimeSpan end = timeline.EndTime;
                if (end > TimeSpan.Zero && position > end)
                {
                    position = end;
                }
            }
            else if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            string appId = session.SourceAppUserModelId ?? string.Empty;
            string title = props?.Title ?? string.Empty;
            string artist = props?.Artist ?? string.Empty;
            string album = props?.AlbumTitle ?? string.Empty;

            string coverKey = $"{appId}|{title}|{artist}|{album}";
            byte[]? cover = _cachedCover;
            if (!string.Equals(coverKey, _cachedCoverKey, StringComparison.Ordinal))
            {
                cover = await TryReadThumbnailAsync(props);
                _cachedCoverKey = coverKey;
                _cachedCover = cover;
            }

            var track = new TrackInfo
            {
                HasSession = true,
                Title = string.IsNullOrWhiteSpace(title) ? "未知曲目" : title,
                Artist = artist,
                Album = album,
                IsPlaying = playing,
                Position = position,
                Duration = timeline.EndTime - timeline.StartTime,
                SourceAppId = appId,
                SourceAppName = ShortAppName(appId),
                CoverBytes = cover
            };

            SetTrack(track, force: false);
        }
        catch (Exception ex)
        {
            LogService.Warn($"刷新媒体信息失败：{ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<byte[]?> TryReadThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties? props)
    {
        try
        {
            if (props?.Thumbnail is null)
            {
                return null;
            }

            using IRandomAccessStreamWithContentType stream = await props.Thumbnail.OpenReadAsync();
            if (stream.Size == 0)
            {
                return null;
            }

            var buffer = new byte[stream.Size];
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(buffer);
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    private void RefreshSessions()
    {
        if (_manager is null)
        {
            return;
        }

        try
        {
            _sessions = _manager.GetSessions()?.ToList() ?? new List<GlobalSystemMediaTransportControlsSession>();
        }
        catch
        {
            _sessions = new List<GlobalSystemMediaTransportControlsSession>();
        }
    }

    /// <summary>优先使用用户手动切换的会话；该会话消失后回落到系统当前会话。</summary>
    private GlobalSystemMediaTransportControlsSession? ResolveSession()
    {
        if (_manager is null)
        {
            return null;
        }

        if (_manualSession is not null)
        {
            if (_sessions.Contains(_manualSession))
            {
                return _manualSession;
            }

            _manualSession = null;
        }

        return _manager.GetCurrentSession();
    }

    /// <summary>在多个播放器之间循环切换当前接管的会话。</summary>
    public void SelectNextSession()
    {
        RefreshSessions();
        if (_sessions.Count < 2)
        {
            return;
        }

        var current = ResolveSession();
        int index = current is null ? -1 : _sessions.IndexOf(current);
        _manualSession = _sessions[(index + 1) % _sessions.Count];
        LogService.Info($"切换媒体会话 → {_manualSession.SourceAppUserModelId}");
        _ = RefreshAsync();
    }

    private void SetTrack(TrackInfo track, bool force)
    {
        bool changed = !string.Equals(Current.IdentityKey, track.IdentityKey, StringComparison.Ordinal);
        Current = track;

        Updated?.Invoke(this, track);
        if (changed || force)
        {
            if (track.HasSession)
            {
                LogService.Info($"换歌：{track.Artist} - {track.Title}");
            }

            TrackChanged?.Invoke(this, track);
        }
    }

    private static string ShortAppName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return "未知来源";
        }

        try
        {
            int bang = appId.IndexOf('!');
            string trimmed = bang > 0 ? appId[..bang] : appId;
            int dot = trimmed.LastIndexOf('.');
            return dot > 0 && dot < trimmed.Length - 1 ? trimmed[(dot + 1)..] : trimmed;
        }
        catch
        {
            return appId;
        }
    }

    // ---------------- 播放控制 ----------------

    private GlobalSystemMediaTransportControlsSession? Session => _session ?? _manager?.GetCurrentSession();

    public async Task PlayPauseAsync()
    {
        var session = Session;
        if (session is null)
        {
            return;
        }

        try
        {
            bool playing = session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            if (playing)
            {
                await session.TryPauseAsync();
            }
            else
            {
                await session.TryPlayAsync();
            }

            await Task.Delay(120);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            LogService.Warn($"播放/暂停失败：{ex.Message}");
        }
    }

    public async Task NextAsync()
    {
        try
        {
            if (Session is { } session)
            {
                await session.TrySkipNextAsync();
                await Task.Delay(150);
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"下一首失败：{ex.Message}");
        }
    }

    public async Task PreviousAsync()
    {
        try
        {
            if (Session is { } session)
            {
                await session.TrySkipPreviousAsync();
                await Task.Delay(150);
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"上一首失败：{ex.Message}");
        }
    }

    public async Task<bool> SeekAsync(TimeSpan position)
    {
        try
        {
            if (Session is { } session)
            {
                // TimeSpan.Ticks 本身就是 100ns 单位，GSMTC 的 TryChangePlaybackPositionAsync
                // 也接收 100ns 单位 —— 不能再乘 100，否则会跳到 100 倍位置（表现为"直接拖到末尾"）。
                long ticks = Math.Max(0, position.Ticks);
                bool submitted = await session.TryChangePlaybackPositionAsync(ticks);
                await RefreshAsync();
                LogService.Info(
                    $"跳转进度：{(int)position.TotalMinutes}:{position.Seconds:00}（{(submitted ? "已提交" : "播放器未接受")}）");
                return submitted;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"跳转进度失败：{ex.Message}");
        }

        return false;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _gate.Dispose();
    }
}
