using System.Diagnostics;
using System.IO;
using DynamicIsland.Core.Interop;

namespace DynamicIsland.Core.Services;

/// <summary>
/// 通知来源应用的解析：通知数据库里只有 handlerId（如 ntqq / WeChat.exe / AUMID），
/// 这里把它映射到真实可执行文件，用于取应用图标和点击跳转。
/// </summary>
public static class NotificationApps
{
    private sealed record CacheEntry(string? Path, DateTime ExpiresUtc);

    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>handlerId 中的常见缩写 → 进程名关键字。</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ntqq"] = "qq",
        ["qqnt"] = "qq",
        ["tim"] = "tim",
        ["wechat"] = "wechat",
        ["weixin"] = "wechat",
        ["wxwork"] = "wxwork",
        ["dingtalk"] = "dingtalk",
        ["telegram"] = "telegram",
        ["discord"] = "discord",
        ["spotify"] = "spotify",
        ["msedge"] = "msedge",
        ["edge"] = "msedge",
        ["chrome"] = "chrome",
        ["firefox"] = "firefox",
        ["cloudmusic"] = "cloudmusic",
        ["qqmusic"] = "qqmusic",
        ["kugou"] = "kugou",
        ["kuwo"] = "kuwo",
        ["vlc"] = "vlc",
        ["obs"] = "obs64",
        ["steam"] = "steam",
        ["outlook"] = "outlook",
        ["thunderbird"] = "thunderbird",
        ["potplayer"] = "potplayermini64",
        ["mpc"] = "mpc-hc64",
    };

    /// <summary>系统类通知没有可启动的应用窗口，直接按应用名展示。</summary>
    private static bool IsSystemHandler(string token)
        => token.StartsWith("app:system", StringComparison.OrdinalIgnoreCase)
           || token.StartsWith("microsoft.windows.", StringComparison.OrdinalIgnoreCase)
           || token is "system" or "windows";

    /// <summary>把 handlerId 规范化成用于匹配的短标识。</summary>
    internal static string Normalize(string appId)
    {
        string value = (appId ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        int bang = value.IndexOf('!');
        if (bang > 0)
        {
            value = value[..bang];
        }

        value = value.StartsWith("app:", StringComparison.OrdinalIgnoreCase) ? value[4..] : value;

        foreach (string prefix in new[] { "Microsoft.Windows.", "Microsoft." })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..];
                break;
            }
        }

        if (value.EndsWith("Handler", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^"Handler".Length];
        }

        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        return value.Trim();
    }

    /// <summary>解析通知来源的可执行文件路径；解析不到返回 null（UI 用首字母占位）。</summary>
    public static string? ResolveExecutable(string appId)
    {
        string token = Normalize(appId);
        if (token.Length == 0 || IsSystemHandler(token))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(token, out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
            {
                return entry.Path;
            }
        }

        string? path = FindProcessPath(token);

        lock (Gate)
        {
            Cache[token] = new CacheEntry(path, DateTime.UtcNow.AddMinutes(5));
        }

        return path;
    }

    private static string? FindProcessPath(string token)
    {
        string alias = Aliases.TryGetValue(token, out string? mapped) ? mapped : token;
        string[] needles = new[] { token, alias }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return null;
        }

        string? looseMatch = null;
        foreach (Process process in processes)
        {
            try
            {
                string name = process.ProcessName;
                if (name.Length < 2)
                {
                    continue;
                }

                foreach (string needle in needles)
                {
                    if (needle.Length < 2)
                    {
                        continue;
                    }

                    bool exact = string.Equals(name, needle, StringComparison.OrdinalIgnoreCase);
                    bool contains = name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                                    || needle.Contains(name, StringComparison.OrdinalIgnoreCase);

                    if (!exact && !contains)
                    {
                        continue;
                    }

                    string? file = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(file) || !File.Exists(file))
                    {
                        continue;
                    }

                    if (exact)
                    {
                        return file;
                    }

                    looseMatch ??= file;
                }
            }
            catch
            {
                // 受保护进程读不到 MainModule，跳过
            }
        }

        return looseMatch;
    }

    /// <summary>取通知来源应用图标；取不到返回 null。</summary>
    public static IconExtractor.RawIcon? ResolveIcon(string appId, int size = 64)
    {
        string? path = ResolveExecutable(appId);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return IconExtractor.FromPath(path, size);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>点击通知时尝试唤起来源应用。</summary>
    public static bool TryActivate(string appId)
    {
        string raw = (appId ?? string.Empty).Trim();
        if (raw.Length == 0 || IsSystemHandler(Normalize(raw)))
        {
            return false;
        }

        // 打包应用（AUMID）优先走 shell:AppsFolder，能正确激活 UWP / MSIX 应用
        if (raw.Contains('!'))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{raw}",
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                LogService.Warn("激活打包应用失败：" + ex.Message);
            }
        }

        string? path = ResolveExecutable(raw);
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn("启动通知来源应用失败：" + ex.Message);
            return false;
        }
    }
}
