using System.Text;
using DynamicIsland.Core.Models;
using DynamicIsland.Core.Services;

Console.OutputEncoding = Encoding.UTF8;

// 启动器专项测试在独立临时目录运行，不读取正式配置或触发其他服务。
if (args.Length > 0 && args[0] == "--launcher-test")
{
    return LauncherTests.Run();
}

// 专项测试启动的子进程：记录实际收到的参数和工作目录后退出。
if (args.Length >= 2 && args[0] == "--launcher-probe")
{
    string payload = System.Text.Json.JsonSerializer.Serialize(new
    {
        Arguments = args.Skip(2).ToArray(),
        WorkingDirectory = Environment.CurrentDirectory
    });
    File.WriteAllText(args[1] + ".tmp", payload);
    File.Move(args[1] + ".tmp", args[1]);
    return 0;
}

LogService.Init();

int passed = 0;
int failed = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok)
    {
        passed++;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  [OK]   ");
    }
    else
    {
        failed++;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("  [FAIL] ");
    }

    Console.ResetColor();
    Console.WriteLine($"{name}{(string.IsNullOrWhiteSpace(detail) ? "" : " — " + detail)}");
}

Console.WriteLine();
Console.WriteLine("========== 灵动岛服务层自检 ==========");
Console.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine($"日志：{AppPaths.LogFile}");
Console.WriteLine();

// ---------- 1. 配置 ----------
Console.WriteLine("【1/7】配置与目录");
try
{
    AppPaths.EnsureCreated();
    Console.WriteLine($"         Root      = {AppPaths.Root} (exists={Directory.Exists(AppPaths.Root)})");
    Console.WriteLine($"         LyricsDir = {AppPaths.LyricsCacheDir} (exists={Directory.Exists(AppPaths.LyricsCacheDir)})");
    Console.WriteLine($"         IconDir   = {AppPaths.IconCacheDir} (exists={Directory.Exists(AppPaths.IconCacheDir)})");

    string probe = Path.Combine(AppPaths.Root, "probe.txt");
    File.WriteAllText(probe, "probe-" + DateTime.Now.Ticks);
    Check("直接写入测试", File.Exists(probe), probe);

    var settings = new SettingsService();
    settings.Load();
    settings.Save();
    Check("配置读写", File.Exists(AppPaths.SettingsFile), AppPaths.SettingsFile);
    Check("数据目录", Directory.Exists(AppPaths.LyricsCacheDir) && Directory.Exists(AppPaths.IconCacheDir));
}
catch (Exception ex)
{
    Check("配置读写", false, $"{ex.GetType().Name}: {ex.Message}");
    Console.WriteLine("         路径诊断：Roaming=" + Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    Console.WriteLine("                   Local=" + Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}

// ---------- 2. 音量 ----------
Console.WriteLine();
Console.WriteLine("【2/7】系统音量（Core Audio）");
try
{
    using var volume = new VolumeService();
    if (volume.IsAvailable)
    {
        float before = volume.Volume;
        Check("音量服务可用", true, $"当前 {before * 100:F0}%，静音={volume.IsMuted}");

        float probe = before > 0.5f ? before - 0.05f : before + 0.05f;
        volume.SetVolume(probe);
        Thread.Sleep(120);
        float after = volume.Volume;
        Check("音量写入生效", Math.Abs(after - probe) < 0.05f, $"{before * 100:F0}% -> {after * 100:F0}%");
        volume.SetVolume(before);
    }
    else
    {
        Check("音量服务可用", false, "未找到默认输出设备");
    }
}
catch (Exception ex)
{
    Check("音量服务", false, ex.Message);
}

// ---------- 3. 媒体 ----------
Console.WriteLine();
Console.WriteLine("【3/7】媒体会话（GSMTC）");
TrackInfo track = TrackInfo.Empty;
try
{
    using var media = new MediaService();
    bool init = await media.InitializeAsync();
    Check("媒体服务初始化", init);

    await Task.Delay(1200);
    await media.RefreshAsync();
    track = media.Current;

    if (track.HasSession)
    {
        Check("检测到播放会话", true,
            $"{track.SourceAppName} | {track.Artist} - {track.Title} | " +
            $"{(track.IsPlaying ? "播放中" : "暂停")} {track.Position:mm\\:ss}/{track.Duration:mm\\:ss}");
        Check("封面已加载", track.CoverBytes is { Length: > 0 },
            track.CoverBytes is { Length: > 0 } ? $"{track.CoverBytes.Length / 1024} KB" : "无封面");
    }
    else
    {
        Check("检测到播放会话", false, "当前没有正在播放的媒体（请开一个播放器再测）");
    }
}
catch (Exception ex)
{
    Check("媒体服务", false, ex.Message);
}

// ---------- 4. 歌词 ----------
Console.WriteLine();
Console.WriteLine("【4/7】歌词（LRCLIB）");
try
{
    var lyrics = new LyricsService();

    // 先验证链路：用一首确定存在的歌（与当前是否在放歌无关）
    var probeTrack = new TrackInfo
    {
        HasSession = true,
        Title = "晴天",
        Artist = "周杰伦",
        Album = "叶惠美",
        Duration = TimeSpan.FromSeconds(269)
    };

    var doc = await lyrics.GetLyricsAsync(probeTrack);
    Check("歌词请求链路", doc.Lines.Count > 0 || lyrics.LastStatus?.Contains("命中") == true,
        $"{probeTrack.Artist} - {probeTrack.Title} :: {lyrics.LastStatus}");
    Check("LRC 解析", doc.Lines.Count > 0, $"{doc.Lines.Count} 行，同步={doc.IsSynced}");

    if (doc.Lines.Count > 3)
    {
        Console.WriteLine($"         前 3 行：{doc.Lines[0].Time:mm\\:ss} {doc.Lines[0].Text}");
        Console.WriteLine($"                   {doc.Lines[1].Time:mm\\:ss} {doc.Lines[1].Text}");
        Console.WriteLine($"                   {doc.Lines[2].Time:mm\\:ss} {doc.Lines[2].Text}");
        int idx = doc.IndexAt(TimeSpan.FromSeconds(60));
        Check("时间轴定位", idx >= 0, $"60s 处 -> 第 {idx + 1} 行「{(idx >= 0 ? doc.Lines[idx].Text : "")}」");
    }
}
catch (Exception ex)
{
    Check("歌词服务", false, ex.Message);
}

// ---------- 5. 天气 ----------
Console.WriteLine();
Console.WriteLine("【5/7】天气（Open-Meteo）");
try
{
    var weather = new WeatherService();
    var info = await weather.GetAsync(force: true);
    Check("天气请求", info.Ok, weather.LastStatus ?? "");
    if (info.Ok)
    {
        Check("天气数据完整", info.Temperature != 0,
            $"{info.LocationName} {info.Icon} {info.Description} {info.Temperature:F0}°C " +
            $"体感{info.FeelsLike:F0}° 湿度{info.Humidity}% 风{info.WindSpeed:F1}km/h");
        Check("三日预报", info.Daily.Count >= 3, string.Join(" | ",
            info.Daily.Take(3).Select(d => $"{d.Date:MM-dd} {d.Icon}{d.MinTemperature:F0}~{d.MaxTemperature:F0}°")));
    }
}
catch (Exception ex)
{
    Check("天气服务", false, ex.Message);
}

// ---------- 6. 硬件 ----------
Console.WriteLine();
Console.WriteLine("【6/7】硬件监控");
try
{
    using var hardware = new HardwareService();
    hardware.Sample();                 // 第一次建立基线
    await Task.Delay(1100);
    var stats = hardware.Sample();     // 第二次才有差值

    Check("CPU 采集", stats.CpuUsage >= 0, $"{stats.CpuUsage:F1}%");
    Check("内存采集", stats.MemoryTotalGb > 0,
        $"{stats.MemoryUsedGb:F1}/{stats.MemoryTotalGb:F1} GB（{stats.MemoryPercent:F0}%）");
    Check("磁盘采集", stats.DiskUsedPercent > 0, $"系统盘占用 {stats.DiskUsedPercent:F1}%");
    Check("网络采集", stats.NetDownMbps >= 0 && stats.NetUpMbps >= 0,
        $"↓{stats.NetDownMbps:F2} Mbps ↑{stats.NetUpMbps:F2} Mbps");
    Check("GPU 采集", true, hardware.GpuAvailable ? $"可用（{stats.GpuUsage:F1}%）" : "不可用（性能计数器缺失，界面将隐藏该行）");
}
catch (Exception ex)
{
    Check("硬件服务", false, ex.Message);
}

// ---------- 7. 通知 + 图标 ----------
Console.WriteLine();
Console.WriteLine("【7/7】通知监听与图标提取");
try
{
    using var notification = new NotificationService();
    bool ok = await notification.InitializeAsync();
    Check("通知监听就绪", ok, $"后端={notification.Backend} | {notification.LastStatus}");

    if (ok)
    {
        var existing = notification.GetExisting(5);
        Check("通知内容解析", existing.Count > 0, $"读到 {existing.Count} 条历史");
        foreach (var n in existing.Take(3))
        {
            Console.WriteLine($"         [{n.AppName}] {n.DisplayTitle} {(n.Body.Length > 40 ? n.Body[..40] + "…" : n.Body)}");
        }
    }
}
catch (Exception ex)
{
    Check("通知服务", false, ex.Message);
}

try
{
    var launcher = new LauncherService();
    string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
    var item = launcher.CreateItem(explorer);
    Check("快捷方式解析", item is not null, item?.ToString() ?? "失败");

    var icon = LauncherService.GetIcon(explorer, 64);
    Check("图标提取", icon is not null && icon.Bgra.Length > 0,
        icon is null ? "失败" : $"{icon.Width}×{icon.Height}，{icon.Bgra.Length / 1024} KB BGRA");

    // 桌面第一个快捷方式
    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    var lnk = Directory.EnumerateFiles(desktop, "*.lnk").FirstOrDefault();
    if (lnk is not null)
    {
        var linkItem = launcher.CreateItem(lnk);
        Check(".lnk 目标解析", linkItem is not null && !string.IsNullOrWhiteSpace(linkItem.TargetPath),
            linkItem is null ? "失败" : $"{Path.GetFileName(lnk)} -> {linkItem.TargetPath}");
    }
    else
    {
        Console.WriteLine("         桌面没有 .lnk 文件，跳过该项");
    }
}
catch (Exception ex)
{
    Check("图标/启动器", false, ex.Message);
}

// ---------- 汇总 ----------
Console.WriteLine();
Console.WriteLine("========== 自检结果 ==========");
Console.ForegroundColor = failed == 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
Console.WriteLine($"通过 {passed} 项，失败 {failed} 项");
Console.ResetColor();
Console.WriteLine();

return failed == 0 ? 0 : 1;
