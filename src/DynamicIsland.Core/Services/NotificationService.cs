using System.Runtime.InteropServices;
using System.Xml.Linq;
using DynamicIsland.Core.Models;
using Microsoft.Data.Sqlite;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Core.Services;

/// <summary>通知来源。</summary>
public enum NotificationBackend
{
    None,
    /// <summary>WinRT UserNotificationListener（需要打包身份才有授权）。</summary>
    WinRtListener,
    /// <summary>直接只读 Windows 通知数据库（未打包应用的可用方案）。</summary>
    WpnDatabase
}

/// <summary>监听 Windows 系统通知（Toast）。</summary>
public sealed class NotificationService : IDisposable
{
    /// <summary>应用模型 ID：未打包应用也带上，便于系统识别授权主体。</summary>
    public const string AppUserModelId = "DynamicIsland.Desktop";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    private const string WpnDatabaseRelativePath = @"Microsoft\Windows\Notifications\wpndatabase.db";

    private readonly HashSet<uint> _seen = new();
    private UserNotificationListener? _listener;
    private Timer? _timer;
    private bool _polling;
    private string _connectionString = string.Empty;

    public NotificationBackend Backend { get; private set; } = NotificationBackend.None;

    public UserNotificationListenerAccessStatus AccessStatus { get; private set; }
        = UserNotificationListenerAccessStatus.Unspecified;

    public bool IsReady => Backend != NotificationBackend.None;

    public string? LastStatus { get; private set; }

    public List<NotificationItem> History { get; } = new();

    /// <summary>收到一条新通知（已去重）。</summary>
    public event EventHandler<NotificationItem>? NotificationReceived;

    public static void EnsureAppUserModelId()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch
        {
            // 忽略
        }
    }

    public async Task<bool> InitializeAsync()
    {
        EnsureAppUserModelId();

        // 1) 优先 WinRT 监听器（仅打包应用会被授权）
        if (await TryWinRtAsync())
        {
            Backend = NotificationBackend.WinRtListener;
            LastStatus = "WinRT 监听器（已授权）";
            LogService.Info("通知后端：WinRT UserNotificationListener");
            StartTimer();
            return true;
        }

        // 2) 回退：直接读通知数据库
        if (TryInitDatabase())
        {
            Backend = NotificationBackend.WpnDatabase;
            LastStatus = "通知数据库（只读）";
            LogService.Info("通知后端：wpndatabase.db（只读）");

            // 先把历史通知标记为已见，避免启动时刷屏
            try
            {
                foreach (var existing in QueryDatabase(since: DateTimeOffset.MinValue, limit: 40))
                {
                    _seen.Add(existing.Id);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn("读取历史通知失败：" + ex.Message);
            }

            StartTimer();
            return true;
        }

        Backend = NotificationBackend.None;
        LastStatus = "不可用";
        return false;
    }

    private async Task<bool> TryWinRtAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            AccessStatus = await _listener.RequestAccessAsync();

            if (AccessStatus == UserNotificationListenerAccessStatus.Allowed)
            {
                var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                foreach (var item in existing)
                {
                    _seen.Add(item.Id);
                }

                return true;
            }

            LogService.Info($"WinRT 通知授权被拒（{AccessStatus}），改用数据库后端");
        }
        catch (Exception ex)
        {
            LogService.Info("WinRT 通知监听不可用：" + ex.Message);
        }

        _listener = null;
        return false;
    }

    private bool TryInitDatabase()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                WpnDatabaseRelativePath);

            if (!File.Exists(path))
            {
                LastStatus = "通知数据库不存在";
                return false;
            }

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = false
            }.ToString();

            // 试连一次
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM Notification";
            command.ExecuteScalar();
            return true;
        }
        catch (Exception ex)
        {
            LastStatus = "通知数据库不可读：" + ex.Message;
            LogService.Warn(LastStatus);
            return false;
        }
    }

    private void StartTimer()
    {
        _timer?.Dispose();
        _timer = new Timer(_ => _ = PollAsync(), null, 1200, 1800);
    }

    private async Task PollAsync()
    {
        if (_polling)
        {
            return;
        }

        _polling = true;
        try
        {
            switch (Backend)
            {
                case NotificationBackend.WinRtListener:
                    await PollWinRtAsync();
                    break;
                case NotificationBackend.WpnDatabase:
                    PollDatabase();
                    break;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("轮询通知失败：" + ex.Message);
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task PollWinRtAsync()
    {
        if (_listener is null)
        {
            return;
        }

        var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
        foreach (var notification in notifications)
        {
            if (!_seen.Add(notification.Id))
            {
                continue;
            }

            var item = ToItem(notification);
            if (item is not null)
            {
                Publish(item);
            }
        }

        if (_seen.Count > 400)
        {
            var alive = notifications.Select(n => n.Id).ToHashSet();
            _seen.IntersectWith(alive);
        }
    }

    private void PollDatabase()
    {
        foreach (var item in QueryDatabase(since: DateTimeOffset.Now.AddHours(-2), limit: 20))
        {
            if (!_seen.Add(item.Id))
            {
                continue;
            }

            Publish(item);
        }
    }

    private void Publish(NotificationItem item)
    {
        History.Insert(0, item);
        if (History.Count > 50)
        {
            History.RemoveAt(History.Count - 1);
        }

        LogService.Info($"新通知：{item.AppName} / {item.DisplayTitle}");
        NotificationReceived?.Invoke(this, item);
    }

    private List<NotificationItem> QueryDatabase(DateTimeOffset since, int limit)
    {
        var result = new List<NotificationItem>();
        if (string.IsNullOrEmpty(_connectionString))
        {
            return result;
        }

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT n.Id, n.Payload, n.ArrivalTime, h.PrimaryId
                FROM Notification n
                LEFT JOIN NotificationHandler h ON h.RecordId = n.HandlerId
                ORDER BY n.ArrivalTime DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                long id = reader.GetInt64(0);
                string payload = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                long arrivalRaw = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                string handlerId = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);

                DateTimeOffset arrival = FromFileTime(arrivalRaw);
                if (arrival < since)
                {
                    continue;
                }

                var (title, body) = ParseToastPayload(payload);
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
                {
                    continue;
                }

                result.Add(new NotificationItem(
                    (uint)Math.Clamp(id, 0, uint.MaxValue),
                    FriendlyAppName(handlerId),
                    handlerId,
                    title,
                    body,
                    arrival));
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("查询通知数据库失败：" + ex.Message);
        }

        return result;
    }

    private static DateTimeOffset FromFileTime(long value)
    {
        try
        {
            if (value <= 0)
            {
                return DateTimeOffset.Now;
            }

            return new DateTimeOffset(DateTime.FromFileTimeUtc(value)).ToLocalTime();
        }
        catch
        {
            return DateTimeOffset.Now;
        }
    }

    /// <summary>从 toast XML 里取标题与正文。</summary>
    internal static (string Title, string Body) ParseToastPayload(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return (string.Empty, string.Empty);
        }

        try
        {
            var doc = XDocument.Parse(xml);
            var texts = doc.Descendants()
                .Where(e => e.Name.LocalName is "text" or "title" || e.Name.LocalName.EndsWith("text", StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Value ?? string.Empty).Trim())
                .Where(s => s.Length > 0)
                .ToList();

            if (texts.Count == 0)
            {
                return (string.Empty, string.Empty);
            }

            if (texts.Count == 1)
            {
                return (texts[0], string.Empty);
            }

            return (texts[0], string.Join(" ", texts.Skip(1)));
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>把 handler 标识整理成可读应用名。</summary>
    internal static string FriendlyAppName(string handlerId)
    {
        if (string.IsNullOrWhiteSpace(handlerId))
        {
            return "系统通知";
        }

        // 常见形式：Microsoft.Windows.XxxHandler / ntqq / WeChat.exe / app:system
        string value = handlerId;

        if (value.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Microsoft.Windows.".Length..];
        }
        else if (value.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Microsoft.".Length..];
        }

        if (value.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
        {
            return "系统";
        }

        if (value.EndsWith("Handler", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^"Handler".Length];
        }

        return string.IsNullOrWhiteSpace(value) ? "系统通知" : value;
    }

    private static NotificationItem? ToItem(UserNotification notification)
    {
        try
        {
            string appName = "未知应用";
            string appId = string.Empty;

            try
            {
                var appInfo = notification.AppInfo;
                if (appInfo is not null)
                {
                    appName = appInfo.DisplayInfo?.DisplayName ?? appInfo.AppUserModelId ?? appName;
                    appId = appInfo.AppUserModelId ?? string.Empty;
                }
            }
            catch
            {
                // AppInfo 可能因权限受限抛异常
            }

            string title = string.Empty;
            string body = string.Empty;

            var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements();
            if (texts is { Count: > 0 })
            {
                title = texts[0]?.Text ?? string.Empty;
                body = string.Join(" ", texts.Skip(1).Select(t => t?.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
            }

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            return new NotificationItem(
                notification.Id,
                string.IsNullOrWhiteSpace(appName) ? "通知" : appName,
                appId,
                title,
                body,
                DateTimeOffset.Now);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取数据库里已有的通知（用于历史列表与自检，不影响去重集合）。</summary>
    public List<NotificationItem> GetExisting(int limit = 10)
        => Backend == NotificationBackend.WpnDatabase
            ? QueryDatabase(DateTimeOffset.Now.AddDays(-7), limit)
            : History.Take(limit).ToList();

    /// <summary>本地推送一条通知（供脚本 / 其他程序调用）。</summary>
    public void PushLocal(string appName, string title, string body)
    {
        var item = new NotificationItem(
            (uint)Environment.TickCount64,
            string.IsNullOrWhiteSpace(appName) ? "本地消息" : appName,
            "local",
            title,
            body,
            DateTimeOffset.Now);

        _seen.Add(item.Id);
        Publish(item);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
