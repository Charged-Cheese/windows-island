namespace DynamicIsland.Core.Models;

/// <summary>天气快照（Open-Meteo）。</summary>
public sealed record WeatherInfo
{
    public static readonly WeatherInfo Empty = new();

    public bool Ok { get; init; }
    public string LocationName { get; init; } = string.Empty;
    public double Temperature { get; init; }
    public double FeelsLike { get; init; }
    public int Humidity { get; init; }
    public double WindSpeed { get; init; }
    public int WeatherCode { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Icon { get; init; } = "☁";
    public IReadOnlyList<DailyForecast> Daily { get; init; } = Array.Empty<DailyForecast>();
    public DateTimeOffset UpdatedAt { get; init; }

    public static (string Description, string Icon) DescribeCode(int code) => code switch
    {
        0 => ("晴", "☀"),
        1 or 2 => ("多云", "⛅"),
        3 => ("阴", "☁"),
        45 or 48 => ("雾", "🌫"),
        >= 51 and <= 57 => ("毛毛雨", "🌦"),
        >= 61 and <= 67 => ("雨", "🌧"),
        >= 71 and <= 77 => ("雪", "❄"),
        >= 80 and <= 82 => ("阵雨", "🌦"),
        85 or 86 => ("阵雪", "🌨"),
        >= 95 => ("雷雨", "⛈"),
        _ => ("未知", "☁")
    };
}

public sealed record DailyForecast(DateTime Date, int WeatherCode, double MaxTemperature, double MinTemperature)
{
    public string Description => WeatherInfo.DescribeCode(WeatherCode).Description;
    public string Icon => WeatherInfo.DescribeCode(WeatherCode).Icon;
}

/// <summary>硬件实时指标。</summary>
public sealed record HardwareStats
{
    public static readonly HardwareStats Empty = new();

    public double CpuUsage { get; init; }
    public double MemoryUsedGb { get; init; }
    public double MemoryTotalGb { get; init; }
    public double GpuUsage { get; init; }
    public double NetDownMbps { get; init; }
    public double NetUpMbps { get; init; }
    public double DiskUsedPercent { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public double MemoryPercent => MemoryTotalGb > 0 ? MemoryUsedGb / MemoryTotalGb * 100 : 0;
}

/// <summary>一条系统通知。</summary>
public sealed record NotificationItem(
    uint Id,
    string AppName,
    string AppId,
    string Title,
    string Body,
    DateTimeOffset ReceivedAt)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? AppName : Title;
}

/// <summary>图标坞中的一个已暂存快捷方式。</summary>
public sealed class LauncherItem
{
    public string Name { get; set; } = string.Empty;

    /// <summary>文件/文件夹目标；对于 .lnk 是解析后的回退目标，优先打开 SourcePath 中的原快捷方式。</summary>
    public string TargetPath { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>原始拖入的路径（.lnk 文件本身）。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>图标缓存文件名（存在 %LOCALAPPDATA%\DynamicIsland\icons 下）。</summary>
    public string IconFile { get; set; } = string.Empty;

    public override string ToString() => $"{Name} -> {TargetPath}";
}
