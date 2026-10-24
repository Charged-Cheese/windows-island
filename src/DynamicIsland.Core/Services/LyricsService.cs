using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicIsland.Core.Models;

namespace DynamicIsland.Core.Services;

/// <summary>
/// 歌词获取与解析（LRCLIB，免 Key）。
///
/// 与早期实现的区别：
/// 1. 同时接受 syncedLyrics 和 plainLyrics —— 很多曲目只有纯文本，之前会被直接判成“无歌词”。
/// 2. 多级查询：原始元数据 → 不带 duration → 清洗标题 + 主艺人 → 搜索（按标题/艺人/时长评分）。
/// 3. 时长容差：播放器上报的 duration 会跳（加载中 120s、真实 191s），不能拿它做硬匹配。
/// 4. 标题/艺人清洗：去掉 (Remastered)、(Live)、feat.、- Topic 等噪音。
/// 5. 内存 + 磁盘缓存 + 失败负缓存 + 并发去重。
/// </summary>
public sealed class LyricsService
{
    private static readonly Regex TimeTagRegex = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly HttpClient Http = CreateClient();

    private static readonly string[] kNoiseWords =
    {
        "remaster", "remastered", "live", "demo", "edit", "version", "acoustic",
        "instrumental", "explicit", "clean", "deluxe", "bonus", "mono", "stereo",
        "radio", "mix", "remix", "soundtrack", "ost", "from the", "feat", "ft.", "featuring"
    };

    private readonly Dictionary<string, LyricsDocument> _memoryCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _negativeCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<LyricsDocument>> _inflight = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public string? LastStatus { get; private set; }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/0.2 (Windows)");
        return client;
    }

    public async Task<LyricsDocument> GetLyricsAsync(TrackInfo track, CancellationToken ct = default)
    {
        if (!track.HasSession || string.IsNullOrWhiteSpace(track.Title))
        {
            return LyricsDocument.Empty;
        }

        string key = BuildCacheKey(track);

        Task<LyricsDocument> task;
        bool owner = false;
        lock (_gate)
        {
            if (_memoryCache.TryGetValue(key, out LyricsDocument? cached))
            {
                return cached;
            }

            if (_negativeCache.TryGetValue(key, out DateTime retryAt) && retryAt > DateTime.UtcNow)
            {
                return LyricsDocument.Empty;
            }

            if (!_inflight.TryGetValue(key, out task!))
            {
                task = FetchCoreAsync(track, key, ct);
                _inflight[key] = task;
                owner = true;
            }
        }

        if (!owner)
        {
            return await task;
        }

        try
        {
            return await task;
        }
        finally
        {
            lock (_gate)
            {
                _inflight.Remove(key);
            }
        }
    }

    private async Task<LyricsDocument> FetchCoreAsync(TrackInfo track, string key, CancellationToken ct)
    {
        // 1) 磁盘缓存优先：重启后无需联网
        if (TryReadDiskCache(key, track.Duration, out LyricsDocument? diskDoc))
        {
            lock (_gate)
            {
                _memoryCache[key] = diskDoc;
            }

            LastStatus = "磁盘缓存命中";
            LogService.Info($"歌词（{track.Title}）：{LastStatus}，共 {diskDoc.Lines.Count} 行");
            return diskDoc;
        }

        string cleanTitle = CleanTitle(track.Title);
        string mainArtist = MainArtist(track.Artist);
        int durationSec = (int)Math.Round(track.Duration.TotalSeconds);

        var candidates = new List<Candidate>();
        var statuses = new List<string>();
        bool networkOk = true;

        async Task Query(string url, bool trusted)
        {
            (string? body, string status) = await GetAsync(url, ct);
            statuses.Add(status);
            if (body is null)
            {
                if (status == "429" || status.StartsWith("HTTP5") || status.StartsWith("异常"))
                {
                    networkOk = false;
                }

                return;
            }

            candidates.AddRange(ParseCandidates(body, trusted));
        }

        string titleQ = Uri.EscapeDataString(track.Title);
        string artistQ = Uri.EscapeDataString(track.Artist);
        string cleanTitleQ = Uri.EscapeDataString(cleanTitle);
        string mainArtistQ = Uri.EscapeDataString(mainArtist);
        string albumQ = Uri.EscapeDataString(track.Album ?? string.Empty);

        // 2) 精确 get：先带 album/duration，再退到不带 duration（播放器时长经常不准）
        if (durationSec > 0)
        {
            await Query($"https://lrclib.net/api/get?track_name={titleQ}&artist_name={artistQ}&album_name={albumQ}&duration={durationSec}", trusted: true);
        }

        if (candidates.Count == 0)
        {
            await Query($"https://lrclib.net/api/get?track_name={titleQ}&artist_name={artistQ}", trusted: true);
        }

        // 3) 清洗标题 + 主艺人再 get
        if (candidates.Count == 0 && (cleanTitle != track.Title || mainArtist != track.Artist))
        {
            await Query($"https://lrclib.net/api/get?track_name={cleanTitleQ}&artist_name={mainArtistQ}", trusted: true);
        }

        // 4) 搜索：标题 + 艺人，按评分挑
        if (!HasUsableSynced(candidates, cleanTitle, mainArtist, durationSec))
        {
            await Query($"https://lrclib.net/api/search?q={Uri.EscapeDataString(cleanTitle + " " + mainArtist)}", trusted: false);
        }

        // 5) 只按标题搜索（艺人别名/错写）
        if (!HasUsableSynced(candidates, cleanTitle, mainArtist, durationSec))
        {
            await Query($"https://lrclib.net/api/search?track_name={cleanTitleQ}", trusted: false);
        }

        Candidate? pick = PickBest(candidates, cleanTitle, mainArtist, durationSec);
        if (pick is null)
        {
            LastStatus = $"未命中（{string.Join(" / ", statuses)}）";
            if (networkOk)
            {
                lock (_gate)
                {
                    _negativeCache[key] = DateTime.UtcNow.AddMinutes(5);
                }
            }
            else
            {
                lock (_gate)
                {
                    _negativeCache[key] = DateTime.UtcNow.AddSeconds(30);
                }
            }

            LogService.Info($"歌词（{track.Title}）：{LastStatus}");
            return LyricsDocument.Empty;
        }

        LyricsDocument document = BuildDocument(key, pick, track.Duration);
        lock (_gate)
        {
            _memoryCache[key] = document;
        }

        TryPersistDiskCache(key, pick);
        LastStatus = $"命中（{(!string.IsNullOrWhiteSpace(pick.Synced) ? "同步歌词" : "纯文本歌词")}，{document.Lines.Count} 行）";
        LogService.Info($"歌词（{track.Title}）：{LastStatus}");
        return document;
    }

    // ---------------- 候选解析 / 评分 ----------------

    private sealed class Candidate
    {
        public string Title { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
        public string Album { get; init; } = string.Empty;
        public double Duration { get; init; }
        public bool Instrumental { get; init; }
        public string? Synced { get; init; }
        public string? Plain { get; init; }
        public bool Trusted { get; init; }
    }

    private static List<Candidate> ParseCandidates(string json, bool trusted)
    {
        var result = new List<Candidate>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in doc.RootElement.EnumerateArray())
                {
                    Add(item);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                Add(doc.RootElement);
            }
        }
        catch
        {
            // 忽略格式错误
        }

        return result;

        void Add(JsonElement item)
        {
            bool instrumental = item.TryGetProperty("instrumental", out JsonElement inst) &&
                                inst.ValueKind == JsonValueKind.True;
            if (instrumental)
            {
                return;
            }

            string? synced = ReadString(item, "syncedLyrics");
            string? plain = ReadString(item, "plainLyrics");
            if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain))
            {
                return;
            }

            result.Add(new Candidate
            {
                Title = ReadString(item, "trackName") ?? ReadString(item, "name") ?? string.Empty,
                Artist = ReadString(item, "artistName") ?? string.Empty,
                Album = ReadString(item, "albumName") ?? string.Empty,
                Duration = item.TryGetProperty("duration", out JsonElement dur) && dur.ValueKind == JsonValueKind.Number
                    ? dur.GetDouble()
                    : 0,
                Instrumental = instrumental,
                Synced = string.IsNullOrWhiteSpace(synced) ? null : synced,
                Plain = string.IsNullOrWhiteSpace(plain) ? null : plain,
                Trusted = trusted
            });
        }
    }

    private static string? ReadString(JsonElement item, string name)
        => item.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    private static bool HasUsableSynced(List<Candidate> candidates, string cleanTitle, string mainArtist, int durationSec)
        => candidates.Any(c => !string.IsNullOrWhiteSpace(c.Synced));

    private static Candidate? PickBest(List<Candidate> candidates, string cleanTitle, string mainArtist, int durationSec)
    {
        Candidate? best = null;
        int bestScore = int.MinValue;

        foreach (Candidate c in candidates)
        {
            int durDiff = -1;
            if (durationSec > 0 && c.Duration > 0)
            {
                durDiff = (int)Math.Abs(Math.Round(c.Duration) - durationSec);
            }

            int score = 0;
            if (!c.Trusted)
            {
                int titleMatch = TextMatch(c.Title, cleanTitle);
                if (titleMatch == 0)
                {
                    continue;
                }

                if (durDiff > 10)
                {
                    continue;
                }

                int artistMatch = string.IsNullOrWhiteSpace(mainArtist) ? 1 : TextMatch(c.Artist, mainArtist);
                if (artistMatch == 0 && !(durDiff >= 0 && durDiff <= 3))
                {
                    continue;
                }

                score += titleMatch * 30 + artistMatch * 20;
            }

            if (durDiff >= 0)
            {
                score += durDiff <= 2 ? 40 : durDiff <= 5 ? 25 : durDiff <= 10 ? 5 : 0;
            }

            if (!string.IsNullOrWhiteSpace(c.Synced))
            {
                score += 20;
            }

            if (best is null || score > bestScore)
            {
                best = c;
                bestScore = score;
            }
        }

        return best;
    }

    // ---------------- 文本清洗 / 匹配 ----------------

    private static bool HasNoiseWord(string text)
    {
        string lower = text.ToLowerInvariant();
        return kNoiseWords.Any(w => lower.Contains(w));
    }

    private static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        string result = Regex.Replace(title, @"[\(\[]([^\)\]]*)[\)\]]", m =>
            HasNoiseWord(m.Groups[1].Value) ? " " : m.Value);

        int bar = result.IndexOf(" | ", StringComparison.Ordinal);
        if (bar > 0)
        {
            result = result[..bar];
        }

        for (int i = 0; i < 3; i++)
        {
            int dash = result.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash <= 0)
            {
                break;
            }

            if (!HasNoiseWord(result[(dash + 3)..]))
            {
                break;
            }

            result = result[..dash];
        }

        return result.Trim();
    }

    private static string MainArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        string result = Regex.Replace(artist, @"\s*-\s*Topic$", string.Empty, RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\s*VEVO$", string.Empty, RegexOptions.IgnoreCase);

        int cut = result.Length;
        foreach (string sep in new[] { ",", ";", "/", " feat", " ft.", " ft ", " featuring ", " x ", " × " })
        {
            int pos = result.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (pos > 0)
            {
                cut = Math.Min(cut, pos);
            }
        }

        return result[..cut].Trim();
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(c);
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    private static int TextMatch(string a, string b)
    {
        string na = Normalize(a);
        string nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0)
        {
            return 0;
        }

        if (na == nb)
        {
            return 2;
        }

        return na.Contains(nb) || nb.Contains(na) ? 1 : 0;
    }

    // ---------------- 时间轴 / 文档 / 缓存 ----------------

    private static LyricsDocument BuildDocument(string key, Candidate pick, TimeSpan duration)
    {
        if (!string.IsNullOrWhiteSpace(pick.Synced))
        {
            List<LyricLine> lines = ParseLrc(pick.Synced);
            if (lines.Count > 0)
            {
                return new LyricsDocument { TrackKey = key, IsSynced = true, Lines = lines };
            }
        }

        if (!string.IsNullOrWhiteSpace(pick.Plain))
        {
            List<LyricLine> lines = BuildPlainLines(pick.Plain, duration);
            return new LyricsDocument { TrackKey = key, IsSynced = false, Lines = lines };
        }

        return new LyricsDocument { TrackKey = key, IsSynced = false };
    }

    /// <summary>纯文本歌词没有时间轴：按行均匀铺到整首时长，至少能跟着播放滚动。</summary>
    private static List<LyricLine> BuildPlainLines(string text, TimeSpan duration)
    {
        List<string> raw = text
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (raw.Count == 0)
        {
            return new List<LyricLine>();
        }

        double total = duration.TotalSeconds > 0 ? duration.TotalSeconds : raw.Count * 3.0;
        double lead = Math.Min(3.0, total * 0.08);
        double span = Math.Max(1.0, total - lead - 1.0);

        var lines = new List<LyricLine>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            double seconds = lead + span * i / raw.Count;
            lines.Add(new LyricLine(TimeSpan.FromSeconds(seconds), raw[i]));
        }

        return lines;
    }

    public static List<LyricLine> ParseLrc(string lrc)
    {
        var result = new List<LyricLine>();

        foreach (string rawLine in lrc.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            var matches = TimeTagRegex.Matches(line);
            if (matches.Count == 0)
            {
                continue;
            }

            string text = TimeTagRegex.Replace(line, string.Empty).Trim();
            if (text.Length == 0)
            {
                // 空行也保留，用于段落间隔
                text = " ";
            }

            foreach (Match match in matches)
            {
                int minutes = int.Parse(match.Groups[1].Value);
                int seconds = int.Parse(match.Groups[2].Value);
                int fractionMs = 0;

                if (match.Groups[3].Success)
                {
                    string frac = match.Groups[3].Value;
                    fractionMs = frac.Length switch
                    {
                        1 => int.Parse(frac) * 100,
                        2 => int.Parse(frac) * 10,
                        _ => int.Parse(frac[..Math.Min(3, frac.Length)])
                    };
                }

                var time = TimeSpan.FromMilliseconds(minutes * 60_000 + seconds * 1000 + fractionMs);
                result.Add(new LyricLine(time, text));
            }
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    private static string BuildCacheKey(TrackInfo track)
        => $"{track.SourceAppId}|{track.Title}|{track.Artist}".ToLowerInvariant();

    private static string CacheFilePath(string key) => Path.Combine(AppPaths.LyricsCacheDir, SafeFileName(key) + ".lrc");

    private static void TryPersistDiskCache(string key, Candidate pick)
    {
        try
        {
            AppPaths.EnsureCreated();
            string header = !string.IsNullOrWhiteSpace(pick.Synced) ? "#SYNCED\n" : "#PLAIN\n";
            string body = !string.IsNullOrWhiteSpace(pick.Synced) ? pick.Synced! : pick.Plain!;
            File.WriteAllText(CacheFilePath(key), header + body);
        }
        catch
        {
            // 缓存失败不影响显示
        }
    }

    private static bool TryReadDiskCache(string key, TimeSpan duration, out LyricsDocument document)
    {
        document = LyricsDocument.Empty;
        try
        {
            string path = CacheFilePath(key);
            if (!File.Exists(path))
            {
                return false;
            }

            string content = File.ReadAllText(path);
            if (content.StartsWith("#SYNCED\n", StringComparison.Ordinal))
            {
                List<LyricLine> lines = ParseLrc(content["#SYNCED\n".Length..]);
                if (lines.Count > 0)
                {
                    document = new LyricsDocument { TrackKey = key, IsSynced = true, Lines = lines };
                    return true;
                }
            }
            else if (content.StartsWith("#PLAIN\n", StringComparison.Ordinal))
            {
                List<LyricLine> lines = BuildPlainLines(content["#PLAIN\n".Length..], duration);
                if (lines.Count > 0)
                {
                    document = new LyricsDocument { TrackKey = key, IsSynced = false, Lines = lines };
                    return true;
                }
            }
            else
            {
                // 兼容旧缓存：直接是 lrc 文本
                List<LyricLine> lines = ParseLrc(content);
                if (lines.Count > 0)
                {
                    document = new LyricsDocument { TrackKey = key, IsSynced = true, Lines = lines };
                    return true;
                }
            }
        }
        catch
        {
            // 读缓存失败走网络
        }

        return false;
    }

    private static async Task<(string? Body, string Status)> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, "404");
            }

            if ((int)response.StatusCode == 429)
            {
                return (null, "429");
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"HTTP{(int)response.StatusCode}");
            }

            return (await response.Content.ReadAsStringAsync(ct), "200");
        }
        catch (Exception ex)
        {
            return (null, "异常:" + ex.Message);
        }
    }

    public void ResetCaches()
    {
        lock (_gate)
        {
            _memoryCache.Clear();
            _negativeCache.Clear();
        }
    }

    private static string SafeFileName(string input)
    {
        var buffer = new char[Math.Min(input.Length, 96)];
        int index = 0;
        foreach (char c in input)
        {
            if (index >= buffer.Length)
            {
                break;
            }

            buffer[index++] = char.IsLetterOrDigit(c) ? c : '_';
        }

        ulong hash = 14695981039346656037;
        foreach (char c in input)
        {
            hash = (hash ^ c) * 1099511628211;
        }

        return $"{new string(buffer, 0, index)}_{hash:x16}";
    }
}
