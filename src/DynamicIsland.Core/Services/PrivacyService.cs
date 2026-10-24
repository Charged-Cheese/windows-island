using Microsoft.Win32;

namespace DynamicIsland.Core.Services;

/// <summary>
/// 麦克风 / 摄像头隐私指示：读取 Windows 的 ConsentStore（与系统"正在使用"提示同源）。
/// 只有 start 时间存在、且没有 stop（或 stop 早于 start）时才算正在使用。
/// </summary>
public sealed class PrivacyService : IDisposable
{
    private const string BasePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    private Timer? _timer;
    private bool _micActive;
    private bool _cameraActive;

    public bool MicActive => _micActive;

    public bool CameraActive => _cameraActive;

    public bool AnyActive => _micActive || _cameraActive;

    public event Action? Changed;

    public void Start()
    {
        _timer?.Dispose();
        _timer = new Timer(_ => Poll(), null, 800, 1500);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Poll()
    {
        bool mic = Check("microphone");
        bool camera = Check("webcam");

        if (mic == _micActive && camera == _cameraActive)
        {
            return;
        }

        _micActive = mic;
        _cameraActive = camera;
        Changed?.Invoke();
    }

    private static bool Check(string store)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(BasePath + store);
            if (key is null)
            {
                return false;
            }

            return CheckSubKeys(key) || CheckNonPackaged(key);
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckNonPackaged(RegistryKey parent)
    {
        try
        {
            using RegistryKey? nonPackaged = parent.OpenSubKey("NonPackaged");
            return nonPackaged is not null && CheckSubKeys(nonPackaged);
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckSubKeys(RegistryKey parent)
    {
        foreach (string name in parent.GetSubKeyNames())
        {
            if (string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using RegistryKey? child = parent.OpenSubKey(name);
            if (child is null)
            {
                continue;
            }

            if (EntryInUse(child))
            {
                return true;
            }

            // NonPackaged 下面是可执行文件路径，再下一层才是时间戳
            foreach (string nested in child.GetSubKeyNames())
            {
                using RegistryKey? inner = child.OpenSubKey(nested);
                if (inner is not null && EntryInUse(inner))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool EntryInUse(RegistryKey key)
    {
        long start = ToLong(key.GetValue("LastUsedTimeStart"));
        long stop = ToLong(key.GetValue("LastUsedTimeStop"));

        // start=0 表示从未使用；stop=0 表示还没写入停止时间（正在使用）
        return start > 0 && (stop == 0 || start > stop);
    }

    private static long ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        byte[] bytes when bytes.Length == 8 => BitConverter.ToInt64(bytes, 0),
        _ => 0
    };

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
