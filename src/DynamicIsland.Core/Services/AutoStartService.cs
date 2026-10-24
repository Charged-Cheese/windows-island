using Microsoft.Win32;

namespace DynamicIsland.Core.Services;

/// <summary>开机自启（当前用户 Run 键，无需管理员）。</summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DynamicIsland";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool Enable()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key?.SetValue(ValueName, $"\"{exe}\"");
            LogService.Info("已开启开机自启：" + exe);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("开启开机自启失败", ex);
            return false;
        }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            LogService.Info("已关闭开机自启");
        }
        catch (Exception ex)
        {
            LogService.Error("关闭开机自启失败", ex);
        }
    }
}
