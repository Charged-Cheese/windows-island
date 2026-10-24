namespace DynamicIsland.Core.Services;

/// <summary>专注计时器（25 分钟专注 / 5 分钟休息）。</summary>
public sealed class FocusTimerService : IDisposable
{
    private Timer? _timer;
    private DateTime _endUtc;
    private bool _isBreak;

    public bool IsActive => _endUtc > DateTime.UtcNow;

    public bool IsBreak => _isBreak;

    public TimeSpan Remaining
    {
        get
        {
            TimeSpan left = _endUtc - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    public int TotalMinutes { get; private set; }

    /// <summary>每秒触发一次（UI 刷新）。</summary>
    public event Action? Tick;

    /// <summary>倒计时结束。</summary>
    public event Action? Finished;

    public void Start(int minutes, bool isBreak)
    {
        if (minutes <= 0)
        {
            return;
        }

        TotalMinutes = minutes;
        _isBreak = isBreak;
        _endUtc = DateTime.UtcNow.AddMinutes(minutes);

        _timer?.Dispose();
        _timer = new Timer(_ => OnTick(), null, 250, 1000);
        LogService.Info($"专注计时开始：{minutes} 分钟（{(isBreak ? "休息" : "专注")}）");
        Tick?.Invoke();
    }

    private void OnTick()
    {
        if (IsActive)
        {
            Tick?.Invoke();
            return;
        }

        _timer?.Dispose();
        _timer = null;
        _endUtc = default;
        LogService.Info("专注计时结束");
        Finished?.Invoke();
        Tick?.Invoke();
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _endUtc = default;
        Tick?.Invoke();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
