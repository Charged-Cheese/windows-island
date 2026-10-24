using System.Diagnostics;
using System.Net.NetworkInformation;
using DynamicIsland.Core.Interop;
using DynamicIsland.Core.Models;

namespace DynamicIsland.Core.Services;

/// <summary>CPU / 内存 / 磁盘 / 网络实时采集。</summary>
public sealed class HardwareService : IDisposable
{
    private long _prevIdle;
    private long _prevKernel;
    private long _prevUser;
    private bool _hasCpuBaseline;

    private long _prevBytesReceived;
    private long _prevBytesSent;
    private bool _hasNetBaseline;
    private DateTime _prevSampleTime = DateTime.UtcNow;

    private PerformanceCounter[] _gpuCounters = Array.Empty<PerformanceCounter>();
    private string _gpuInstanceSignature = string.Empty;
    private DateTime _lastGpuRefresh = DateTime.MinValue;
    private bool _gpuCounterFailed;

    public HardwareStats Current { get; private set; } = HardwareStats.Empty;

    public event EventHandler<HardwareStats>? Updated;

    public HardwareService()
    {
        // 先探测类别是否存在，避免每次采样都抛异常
        try
        {
            _gpuCounterFailed = !PerformanceCounterCategory.Exists("GPU Engine");
        }
        catch
        {
            _gpuCounterFailed = true;
        }
    }

    public HardwareStats Sample()
    {
        double cpu = SampleCpu();
        var (usedGb, totalGb) = SampleMemory();
        var (downMbps, upMbps) = SampleNetwork();
        double diskPercent = SampleDisk();
        double gpu = SampleGpu();

        var stats = new HardwareStats
        {
            CpuUsage = cpu,
            MemoryUsedGb = usedGb,
            MemoryTotalGb = totalGb,
            GpuUsage = gpu,
            NetDownMbps = downMbps,
            NetUpMbps = upMbps,
            DiskUsedPercent = diskPercent,
            UpdatedAt = DateTimeOffset.Now
        };

        Current = stats;
        Updated?.Invoke(this, stats);
        return stats;
    }

    private double SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            return 0;
        }

        if (!_hasCpuBaseline)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _hasCpuBaseline = true;
            return 0;
        }

        long idleDelta = idle - _prevIdle;
        long kernelDelta = kernel - _prevKernel;
        long userDelta = user - _prevUser;

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        long total = kernelDelta + userDelta;
        if (total <= 0)
        {
            return 0;
        }

        double usage = (total - idleDelta) * 100.0 / total;
        return Math.Clamp(usage, 0, 100);
    }

    private static (double UsedGb, double TotalGb) SampleMemory()
    {
        var status = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            return (0, 0);
        }

        const double toGb = 1024d * 1024d * 1024d;
        double total = status.ullTotalPhys / toGb;
        double available = status.ullAvailPhys / toGb;
        return (total - available, total);
    }

    private (double DownMbps, double UpMbps) SampleNetwork()
    {
        try
        {
            long received = 0;
            long sent = 0;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var stats = nic.GetIPv4Statistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }

            var now = DateTime.UtcNow;
            double seconds = Math.Max(0.2, (now - _prevSampleTime).TotalSeconds);
            _prevSampleTime = now;

            if (!_hasNetBaseline)
            {
                _prevBytesReceived = received;
                _prevBytesSent = sent;
                _hasNetBaseline = true;
                return (0, 0);
            }

            double down = (received - _prevBytesReceived) * 8.0 / 1_000_000.0 / seconds;
            double up = (sent - _prevBytesSent) * 8.0 / 1_000_000.0 / seconds;

            _prevBytesReceived = received;
            _prevBytesSent = sent;

            return (Math.Max(0, down), Math.Max(0, up));
        }
        catch
        {
            return (0, 0);
        }
    }

    private static double SampleDisk()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            if (NativeMethods.GetDiskFreeSpaceEx(root, out _, out ulong total, out ulong free) && total > 0)
            {
                return (total - free) * 100.0 / total;
            }
        }
        catch
        {
            // 忽略
        }

        return 0;
    }

    private double SampleGpu()
    {
        if (_gpuCounterFailed)
        {
            return 0;
        }

        try
        {
            // 实例名是 "pid_xxx_luid_xxx_phys_xxx_eng_xxx_engtype_Xxx"，且会随进程启停变化。
            // 重建 PerformanceCounter 开销不小（每个实例一个 COM 对象），这里放慢到 30 秒，
            // 并只在实例集合真的变化时才重建，避免长期运行时内存持续增长。
            if ((DateTime.UtcNow - _lastGpuRefresh).TotalSeconds > 30)
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                string[] instances = category.GetInstanceNames();
                string signature = string.Join("|", instances.OrderBy(x => x, StringComparer.Ordinal));

                if (!string.Equals(signature, _gpuInstanceSignature, StringComparison.Ordinal))
                {
                    RefreshGpuCounters(instances);
                    _gpuInstanceSignature = signature;
                }

                _lastGpuRefresh = DateTime.UtcNow;
            }

            double total = 0;
            foreach (var counter in _gpuCounters)
            {
                total += counter.NextValue();
            }

            // 多个引擎/进程实例相加会超过 100，这里归一化到 0-100
            return Math.Clamp(total, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    private void RefreshGpuCounters(string[] instances)
    {
        try
        {
            foreach (var counter in _gpuCounters)
            {
                counter.Dispose();
            }

            var list = new List<PerformanceCounter>(instances.Length);
            foreach (string instance in instances)
            {
                try
                {
                    var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                    counter.NextValue();
                    list.Add(counter);
                }
                catch
                {
                    // 实例可能在枚举后消失，忽略
                }
            }

            _gpuCounters = list.ToArray();
        }
        catch
        {
            _gpuCounterFailed = true;
        }
    }

    public bool GpuAvailable => !_gpuCounterFailed;

    public void Dispose()
    {
        foreach (var counter in _gpuCounters)
        {
            counter.Dispose();
        }

        _gpuCounters = Array.Empty<PerformanceCounter>();
    }
}
