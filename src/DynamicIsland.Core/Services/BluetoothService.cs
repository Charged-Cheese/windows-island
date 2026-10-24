using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace DynamicIsland.Core.Services;

/// <summary>蓝牙设备连接 / 断开监听（WinRT DeviceWatcher），尽量读取电量。</summary>
public sealed class BluetoothService : IDisposable
{
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private DeviceWatcher? _watcher;
    private DateTime _startedUtc;

    /// <summary>设备变化提示（标题带设备名，副标题带类别/电量）。</summary>
    public event Action<SystemAlert>? AlertRequested;

    public void Start()
    {
        try
        {
            string selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            _watcher = DeviceInformation.CreateWatcher(selector);
            _watcher.Added += OnAdded;
            _watcher.Removed += OnRemoved;
            _watcher.EnumerationCompleted += (_, _) => _startedUtc = DateTime.UtcNow;
            _watcher.Start();
            _startedUtc = DateTime.UtcNow;
            LogService.Info("蓝牙设备监听已启动");
        }
        catch (Exception ex)
        {
            LogService.Warn("蓝牙监听启动失败：" + ex.Message);
        }
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        try
        {
            // 启动后的第一轮枚举是"已经连着的设备"，不打扰用户
            if ((DateTime.UtcNow - _startedUtc).TotalSeconds < 8)
            {
                return;
            }

            string name = string.IsNullOrWhiteSpace(info.Name) ? "蓝牙设备" : info.Name;
            _names[info.Id] = name;

            _ = Task.Run(async () =>
            {
                int battery = await TryReadBatteryAsync(info.Id);
                string category = Classify(name);
                string detail = battery >= 0
                    ? $"{category} · 电量 {battery}%"
                    : category;

                AlertRequested?.Invoke(new SystemAlert("\uE702", $"{name} 已连接", detail));
            });
        }
        catch (Exception ex)
        {
            LogService.Warn("处理蓝牙连接事件失败：" + ex.Message);
        }
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        try
        {
            if ((DateTime.UtcNow - _startedUtc).TotalSeconds < 8)
            {
                return;
            }

            string name = _names.TryGetValue(update.Id, out string? cached) ? cached : "蓝牙设备";
            _names.Remove(update.Id);
            AlertRequested?.Invoke(new SystemAlert("\uE702", $"{name} 已断开", "蓝牙设备已断开连接"));
        }
        catch
        {
            // 忽略
        }
    }

    private static string Classify(string name)
    {
        string lower = name.ToLowerInvariant();

        if (ContainsAny(lower, "耳机", "headphone", "airpods", "buds", "earbud", "headset"))
        {
            return "蓝牙耳机";
        }

        if (ContainsAny(lower, "音箱", "speaker", "soundbar", "sound"))
        {
            return "蓝牙音箱";
        }

        if (ContainsAny(lower, "鼠标", "mouse", "trackpad"))
        {
            return "蓝牙鼠标";
        }

        if (ContainsAny(lower, "键盘", "keyboard"))
        {
            return "蓝牙键盘";
        }

        if (ContainsAny(lower, "手机", "phone", "iphone", "android", "pixel"))
        {
            return "蓝牙手机";
        }

        if (ContainsAny(lower, "watch", "手表", "band", "手环"))
        {
            return "可穿戴设备";
        }

        return "蓝牙设备";
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>尝试通过 BLE GATT 电池服务读取电量；读不到返回 -1。</summary>
    private static async Task<int> TryReadBatteryAsync(string deviceId)
    {
        try
        {
            using BluetoothLEDevice device = await BluetoothLEDevice.FromIdAsync(deviceId);
            if (device is null)
            {
                return -1;
            }

            GattDeviceServicesResult services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success)
            {
                return -1;
            }

            GattDeviceService? batteryService = services.Services
                .FirstOrDefault(s => s.Uuid == GattServiceUuids.Battery);
            if (batteryService is null)
            {
                return -1;
            }

            GattCharacteristicsResult characteristics =
                await batteryService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (characteristics.Status != GattCommunicationStatus.Success)
            {
                return -1;
            }

            GattCharacteristic? level = characteristics.Characteristics
                .FirstOrDefault(c => c.Uuid == GattCharacteristicUuids.BatteryLevel);
            if (level is null)
            {
                return -1;
            }

            GattReadResult read = await level.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status != GattCommunicationStatus.Success || read.Value is null)
            {
                return -1;
            }

            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(read.Value);
            return Math.Clamp((int)reader.ReadByte(), 0, 100);
        }
        catch
        {
            return -1;
        }
    }

    public void Stop()
    {
        try
        {
            if (_watcher is { Status: DeviceWatcherStatus.Started })
            {
                _watcher.Stop();
            }
        }
        catch
        {
            // 忽略
        }
    }

    public void Dispose()
    {
        try
        {
            if (_watcher is not null)
            {
                _watcher.Added -= OnAdded;
                _watcher.Removed -= OnRemoved;
                if (_watcher.Status == DeviceWatcherStatus.Started)
                {
                    _watcher.Stop();
                }
            }
        }
        catch
        {
            // 忽略
        }

        _watcher = null;
    }
}
