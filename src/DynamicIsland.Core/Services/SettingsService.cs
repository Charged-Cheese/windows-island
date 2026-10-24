using System.Text.Json;
using DynamicIsland.Core.Models;

namespace DynamicIsland.Core.Services;

/// <summary>应用数据目录。可用环境变量 DYNAMICISLAND_DATA_DIR 覆盖（便于测试与便携部署）。</summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string LocalRoot { get; } = ResolveLocalRoot();

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LyricsCacheDir => Path.Combine(LocalRoot, "lyrics");
    public static string IconCacheDir => Path.Combine(LocalRoot, "icons");
    public static string LogFile => Path.Combine(LocalRoot, "island.log");

    private static string ResolveRoot()
    {
        string? custom = Environment.GetEnvironmentVariable("DYNAMICISLAND_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return custom;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");
    }

    private static string ResolveLocalRoot()
    {
        string? custom = Environment.GetEnvironmentVariable("DYNAMICISLAND_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return Path.Combine(custom, "local");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LyricsCacheDir);
        Directory.CreateDirectory(IconCacheDir);
    }
}

/// <summary>用户配置。</summary>
public sealed class AppSettings
{
    public string CityName { get; set; } = "北京";
    public double Latitude { get; set; } = 39.9042;
    public double Longitude { get; set; } = 116.4074;

    public bool EnableWeather { get; set; } = true;
    public bool EnableNotifications { get; set; } = true;
    public bool EnableLyrics { get; set; } = true;
    public bool EnableHardware { get; set; } = true;

    /// <summary>歌词整体延迟补偿（毫秒，正值=歌词提前）。</summary>
    public double LyricsOffsetMs { get; set; } = 0;

    /// <summary>滚轮调音量时的步进（0.01–0.2）。</summary>
    public double VolumeStep { get; set; } = 0.04;

    /// <summary>折叠态歌名过长时是否跑马灯滚动。</summary>
    public bool MarqueeTitle { get; set; } = true;

    /// <summary>新通知到达时是否自动弹出通知卡。</summary>
    public bool ShowNotificationCard { get; set; } = true;

    /// <summary>折叠胶囊的空间有限：歌词优先，否则显示歌名。</summary>
    public bool ShowLyricsInPill { get; set; } = true;

    /// <summary>折叠胶囊是否显示时钟。</summary>
    public bool ShowPillClock { get; set; } = true;

    /// <summary>麦克风 / 摄像头隐私指示点。</summary>
    public bool EnablePrivacyDots { get; set; } = true;

    /// <summary>全屏应用时自动隐藏岛。</summary>
    public bool EnableFullscreenHide { get; set; } = true;

    /// <summary>电源 / USB / 键盘锁 / 勿扰等系统浮层。</summary>
    public bool EnableSystemAlerts { get; set; } = true;

    /// <summary>蓝牙设备连接 / 断开提示。</summary>
    public bool EnableBluetooth { get; set; } = true;

    /// <summary>剪贴板缩略预览与历史。</summary>
    public bool EnableClipboard { get; set; } = true;

    public bool LaunchAtStartup { get; set; }

    /// <summary>外观主题：obsidian / graphite / porcelain。</summary>
    public string Theme { get; set; } = "obsidian";

    /// <summary>动画风格：default / smooth / bouncy / snappy。</summary>
    public string AnimationStyle { get; set; } = "default";

    public List<LauncherItem> LauncherItems { get; set; } = new();
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            AppPaths.EnsureCreated();
            if (File.Exists(AppPaths.SettingsFile))
            {
                string json = File.ReadAllText(AppPaths.SettingsFile);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取配置失败，使用默认值：{ex.Message}");
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            LogService.Warn($"保存配置失败：{ex.Message}");
        }
    }
}
