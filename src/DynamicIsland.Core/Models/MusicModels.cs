namespace DynamicIsland.Core.Models;

/// <summary>当前媒体会话的曲目快照。</summary>
public sealed record TrackInfo
{
    public static readonly TrackInfo Empty = new();

    public bool HasSession { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public bool IsPlaying { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// 这条 <see cref="Position"/> 快照的采集时刻。媒体轮询是 450ms 一次，
    /// 两次轮询之间 Position 是冻结的；界面侧用它加本地时钟外推，
    /// 歌词涂色和进度条才不会一顿一顿地跳。
    /// </summary>
    public DateTimeOffset PositionCapturedAt { get; init; }

    public string SourceAppId { get; init; } = string.Empty;
    public string SourceAppName { get; init; } = string.Empty;
    public byte[]? CoverBytes { get; init; }

    /// <summary>用于判断"是否换歌"的稳定键。</summary>
    public string IdentityKey => $"{SourceAppId}|{Title}|{Artist}|{Duration.TotalSeconds:F0}";

    public double ProgressRatio =>
        Duration.TotalMilliseconds > 0
            ? Math.Clamp(Position.TotalMilliseconds / Duration.TotalMilliseconds, 0, 1)
            : 0;
}

/// <summary>一行歌词。</summary>
public readonly record struct LyricLine(TimeSpan Time, string Text);

/// <summary>一首歌的完整歌词。</summary>
public sealed class LyricsDocument
{
    public string TrackKey { get; init; } = string.Empty;
    public bool IsSynced { get; init; }
    public IReadOnlyList<LyricLine> Lines { get; init; } = Array.Empty<LyricLine>();

    public static readonly LyricsDocument Empty = new();

    /// <summary>二分查找当前应高亮的行索引；无匹配返回 -1。</summary>
    public int IndexAt(TimeSpan position)
    {
        if (Lines.Count == 0)
        {
            return -1;
        }

        int low = 0;
        int high = Lines.Count - 1;
        int result = -1;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (Lines[mid].Time <= position)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }
}
