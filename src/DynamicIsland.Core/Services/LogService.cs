namespace DynamicIsland.Core.Services;

/// <summary>极简文件日志，便于排查后台服务问题。</summary>
public static class LogService
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static string? LastError { get; private set; }

    public static void Init()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            AppPaths.EnsureCreated();
            _initialized = true;
            Info("==== 启动 ====");
        }
        catch
        {
            // 日志不可用不应影响主流程
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        LastError = message;
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");
    }

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        if (!_initialized)
        {
            return;
        }

        lock (Sync)
        {
            try
            {
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
