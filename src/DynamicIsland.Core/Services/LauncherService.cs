using System.ComponentModel;
using System.Diagnostics;
using DynamicIsland.Core.Interop;
using DynamicIsland.Core.Models;

namespace DynamicIsland.Core.Services;

/// <summary>桌面图标/快捷方式暂存与启动。</summary>
public sealed class LauncherService
{
    /// <summary>启动后 4 秒内没有出现目标进程时触发（程序崩溃、被拦截、快捷方式失效等）。</summary>
    public event Action<LauncherItem, string>? LaunchFailed;

    /// <summary>可接受的文件类型。</summary>
    public static bool IsSupported(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Directory.Exists(path))
        {
            return true;
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".lnk" or ".exe" or ".url" or ".bat" or ".cmd" or ".msc";
    }

    /// <summary>把拖入的路径转换成可暂存的条目；不支持的返回 null。</summary>
    public LauncherItem? CreateItem(string path)
    {
        try
        {
            path = Environment.ExpandEnvironmentVariables(path);
            if (!IsSupported(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                return null;
            }

            string name = Directory.Exists(path)
                ? new DirectoryInfo(path).Name
                : Path.GetFileNameWithoutExtension(path);

            string target = path;
            string args = string.Empty;
            string workDir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? string.Empty;

            if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = ShellLink.Resolve(path);
                if (resolved is not null && !string.IsNullOrWhiteSpace(resolved.TargetPath))
                {
                    target = resolved.TargetPath;
                    args = resolved.Arguments;
                    if (!string.IsNullOrWhiteSpace(resolved.WorkingDirectory))
                    {
                        workDir = resolved.WorkingDirectory;
                    }
                    else
                    {
                        // 回退启动 exe 时使用程序目录，不能沿用桌面快捷方式所在目录。
                        workDir = Path.GetDirectoryName(target) ?? string.Empty;
                    }
                }
            }

            return new LauncherItem
            {
                Name = string.IsNullOrWhiteSpace(name) ? "未命名" : name,
                TargetPath = target,
                Arguments = args,
                WorkingDirectory = workDir,
                SourcePath = path
            };
        }
        catch (Exception ex)
        {
            LogService.Warn($"解析快捷方式失败（{path}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>启动条目。</summary>
    public bool Launch(LauncherItem item) => Launch(item, out _);

    /// <summary>启动条目；失败时返回可供界面显示的原因，兼容已有配置。</summary>
    public bool Launch(LauncherItem item, out string? error)
    {
        error = null;
        try
        {
            string source = Environment.ExpandEnvironmentVariables(item.SourcePath ?? string.Empty);
            var startInfo = new ProcessStartInfo { UseShellExecute = true };

            if (Path.GetExtension(source).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(source))
            {
                // 交给 Windows 打开原快捷方式，保留参数、起始位置、提权及特殊 Shell 目标等语义。
                // 不重复传入已解析的参数，也不以旧配置覆盖快捷方式当前的工作目录。
                startInfo.FileName = source;
            }
            else
            {
                string target = Environment.ExpandEnvironmentVariables(item.TargetPath ?? string.Empty);
                if (string.IsNullOrWhiteSpace(target))
                {
                    error = "没有可用的启动目标，请重新拖入快捷方式。";
                    LogService.Warn($"启动失败：{item.Name}；{error}");
                    return false;
                }

                if (!File.Exists(target) && !Directory.Exists(target))
                {
                    error = $"找不到启动目标：{target}\n原快捷方式也不可用，请确认软件仍已安装，或重新拖入快捷方式。";
                    LogService.Warn($"启动失败：{item.Name}；{error}");
                    return false;
                }

                startInfo.FileName = target;
                startInfo.Arguments = item.Arguments ?? string.Empty;
                string workDir = Environment.ExpandEnvironmentVariables(item.WorkingDirectory ?? string.Empty);
                startInfo.WorkingDirectory = Directory.Exists(workDir)
                    ? workDir
                    : Directory.Exists(target) ? target : Path.GetDirectoryName(Path.GetFullPath(target)) ?? string.Empty;

                if (Path.GetExtension(source).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Info($"原快捷方式不可用，使用已保存目标：{item.Name} -> {target}");
                }
            }

            // 快照必须在启动之前：Shell 打开 .lnk 时目标进程可能瞬间就出现，
            // 放到启动之后再查会把目标自己当成"启动前已存在的实例"。
            HashSet<int> beforePids = SnapshotProcessIds(item.TargetPath);

            using var process = Process.Start(startInfo);
            // Shell 可能复用已有进程或委托其他进程打开，不能把返回 null 当作失败。
            LogService.Info($"已提交启动请求：{item.Name} -> {startInfo.FileName}");
            WatchLaunch(item, beforePids);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户取消提权时不再尝试绕过快捷方式启动。
            error = "启动已取消（未同意权限请求）。";
            LogService.Warn($"启动已取消：{item.Name}；Win32={ex.NativeErrorCode}");
            return false;
        }
        catch (Exception ex)
        {
            error = ex is Win32Exception native
                ? $"{ex.Message}（Windows 错误码 {native.NativeErrorCode}）"
                : ex.Message;
            LogService.Error($"启动失败：{item.Name}", ex);
            return false;
        }
    }

    private static HashSet<int> SnapshotProcessIds(string? targetPath)
    {
        var ids = new HashSet<int>();
        try
        {
            string? name = Path.GetFileNameWithoutExtension(targetPath);
            if (string.IsNullOrWhiteSpace(name))
            {
                return ids;
            }

            foreach (Process p in Process.GetProcessesByName(name))
            {
                ids.Add(p.Id);
                p.Dispose();
            }
        }
        catch
        {
            // 快照失败按空集合处理
        }

        return ids;
    }

    /// <summary>
    /// 启动后观察目标进程：4 秒内没有新进程出现，就通过 LaunchFailed 提示用户。
    /// 单实例程序（启动前已经在跑）不提示，避免把"唤起已有窗口"误报成失败。
    /// </summary>
    private void WatchLaunch(LauncherItem item, HashSet<int> before)
    {
        try
        {
            string? name = Path.GetFileNameWithoutExtension(item.TargetPath);
            if (string.IsNullOrWhiteSpace(name) || Directory.Exists(item.TargetPath))
            {
                return;
            }

            if (before.Count > 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(4000);

                    var startedPids = new List<int>();
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        if (!before.Contains(p.Id))
                        {
                            startedPids.Add(p.Id);
                        }

                        p.Dispose();
                    }

                    if (startedPids.Count == 0)
                    {
                        Report();
                        return;
                    }

                    // 进程出现了但很快退出（Electron 启动崩溃很常见）。
                    await Task.Delay(4000);
                    bool alive = false;
                    foreach (int pid in startedPids)
                    {
                        try
                        {
                            using Process p = Process.GetProcessById(pid);
                            if (!p.HasExited)
                            {
                                alive = true;
                            }
                        }
                        catch
                        {
                            // 进程已退出
                        }
                    }

                    if (!alive)
                    {
                        Report();
                    }
                }
                catch
                {
                    // 监测失败不影响启动
                }

                void Report()
                {
                    string message = $"{name} 没有启动，可能被安全软件拦截或程序自身异常；双击桌面快捷方式可确认。";
                    LogService.Warn($"启动提醒：{item.Name}；{message}");
                    LaunchFailed?.Invoke(item, message);
                }
            });
        }
        catch
        {
            // 监测失败不影响启动
        }
    }

    /// <summary>取图标原始像素（BGRA32），供 UI 层转 BitmapSource。</summary>
    public static IconExtractor.RawIcon? GetIcon(string path, int size = 128)
    {
        try
        {
            return IconExtractor.FromPath(path, size);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>在资源管理器中选中该文件。</summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else if (Directory.Exists(path))
            {
                Process.Start("explorer.exe", $"\"{path}\"");
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"打开资源管理器失败：{ex.Message}");
        }
    }
}
