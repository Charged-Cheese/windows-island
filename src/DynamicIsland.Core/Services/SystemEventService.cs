using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DynamicIsland.Core.Services;

/// <summary>一条系统提示（电源、USB、键盘锁、蓝牙、专注结束等）。</summary>
public sealed record SystemAlert(string Glyph, string Title, string Detail);

/// <summary>
/// 系统事件感知：电源 / 电池、Caps Lock / Num Lock、勿扰模式。
/// USB 由 MainWindow 的 WM_DEVICECHANGE 转发进来。
/// </summary>
public sealed class SystemEventService : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private const int VkCapsLock = 0x14;
    private const int VkNumLock = 0x90;

    private const string NotificationsKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    private Timer? _timer;
    private bool _powerInitialized;
    private bool _keysInitialized;
    private bool _dndInitialized;

    private bool _onAc = true;
    private int _batteryPercent = 100;
    private bool _lowBatteryReported;
    private bool _capsLock;
    private bool _numLock;
    private bool _doNotDisturb;

    public event Action<SystemAlert>? AlertRequested;

    public void Start()
    {
        _timer?.Dispose();
        _timer = new Timer(_ => Poll(), null, 1000, 1000);
    }

    private void Poll()
    {
        PollPower();
        PollLockKeys();
        PollDoNotDisturb();
    }

    private void PollPower()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus status))
        {
            return;
        }

        bool onAc = status.ACLineStatus == 1;
        int percent = status.BatteryLifePercent == 255 ? _batteryPercent : status.BatteryLifePercent;

        if (!_powerInitialized)
        {
            _onAc = onAc;
            _batteryPercent = percent;
            _powerInitialized = true;
            return;
        }

        if (onAc != _onAc)
        {
            _onAc = onAc;
            _lowBatteryReported = false;
            Raise(new SystemAlert(
                onAc ? "\uE83E" : "\uE83F",
                onAc ? "电源已连接" : "已切换到电池供电",
                onAc ? $"正在充电 · {percent}%" : $"剩余 {percent}%"));
        }

        if (percent != _batteryPercent)
        {
            _batteryPercent = percent;
            if (!onAc && percent <= 20 && !_lowBatteryReported)
            {
                _lowBatteryReported = true;
                Raise(new SystemAlert("\uE7BA", "电量低", $"剩余 {percent}%，建议连接电源"));
            }
        }
    }

    private void PollLockKeys()
    {
        bool caps = (GetKeyState(VkCapsLock) & 0x0001) != 0;
        bool num = (GetKeyState(VkNumLock) & 0x0001) != 0;

        if (!_keysInitialized)
        {
            _capsLock = caps;
            _numLock = num;
            _keysInitialized = true;
            return;
        }

        if (caps != _capsLock)
        {
            _capsLock = caps;
            Raise(new SystemAlert(
                caps ? "\uE8FA" : "\uE8F9",
                "大写锁定",
                caps ? "已开启" : "已关闭"));
        }

        if (num != _numLock)
        {
            _numLock = num;
            Raise(new SystemAlert("\uE8EF", "数字小键盘", num ? "已开启" : "已关闭"));
        }
    }

    private void PollDoNotDisturb()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(NotificationsKey);
            object? value = key?.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED");
            bool dnd = value is int i && i == 0;
            if (!_dndInitialized)
            {
                _doNotDisturb = dnd;
                _dndInitialized = true;
                return;
            }

            if (dnd != _doNotDisturb)
            {
                _doNotDisturb = dnd;
                Raise(new SystemAlert("\uE708", dnd ? "专注助手已开启" : "专注助手已关闭",
                    dnd ? "通知将保持静默" : "通知恢复正常"));
            }
        }
        catch
        {
            // 某些系统版本没有该值，忽略
        }
    }

    /// <summary>USB 等设备变化（由窗口消息转发）。</summary>
    public void NotifyUsbDevice(bool arrived)
    {
        Raise(new SystemAlert(
            arrived ? "\uE88E" : "\uE8CC",
            arrived ? "USB 设备已连接" : "USB 设备已移除",
            arrived ? "可以在资源管理器中查看" : "设备已安全断开"));
    }

    private void Raise(SystemAlert alert) => AlertRequested?.Invoke(alert);

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}

