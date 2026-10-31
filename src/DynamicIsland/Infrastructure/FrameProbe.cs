using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using DynamicIsland.Core.Services;

namespace DynamicIsland.Infrastructure;

/// <summary>
/// 帧探针：排查折叠/展开、切页卡顿时用。
/// 只有设置了环境变量 DI_FRAME_LOG 才会挂到渲染循环上，正常使用零开销。
/// 输出 CSV：interval_ms, 时间戳。300 帧落一次盘，退出前补写剩余。
/// </summary>
public static class FrameProbe
{
    private const int FlushEvery = 300;

    private static readonly List<double> Pending = new(FlushEvery);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string? _path;
    private static double _lastSeconds;
    private static bool _logging;

    public static void StartIfRequested()
    {
        _path = Environment.GetEnvironmentVariable("DI_FRAME_LOG");
        if (string.IsNullOrWhiteSpace(_path))
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, "interval_ms\n", Encoding.UTF8);
            _lastSeconds = Clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnRendering;
            _logging = true;
            LogService.Info("帧探针已启用 → " + _path);
        }
        catch (Exception ex)
        {
            LogService.Warn("帧探针启动失败：" + ex.Message);
        }
    }

    public static void Stop()
    {
        if (!_logging)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _logging = false;
        Flush();
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        double now = Clock.Elapsed.TotalSeconds;
        double interval = (now - _lastSeconds) * 1000.0;
        _lastSeconds = now;

        // 第一帧和长时间挂起后的间隔没有比较意义
        if (interval <= 0 || interval > 1000)
        {
            return;
        }

        Pending.Add(interval);
        if (Pending.Count >= FlushEvery)
        {
            Flush();
        }
    }

    private static void Flush()
    {
        if (_path is null || Pending.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder(Pending.Count * 8);
        foreach (double value in Pending)
        {
            builder.Append(value.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');
        }

        Pending.Clear();

        try
        {
            File.AppendAllText(_path, builder.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogService.Warn("帧探针写盘失败：" + ex.Message);
            _logging = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }
}
