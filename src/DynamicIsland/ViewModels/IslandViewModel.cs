using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Core.Models;
using DynamicIsland.Core.Services;
using DynamicIsland.Infrastructure;

namespace DynamicIsland.ViewModels;

public enum IslandState
{
    Collapsed,
    Expanded,
    Notification
}

public sealed class IslandViewModel : ObservableObject, IDisposable
{
    private readonly MediaService _media = new();
    private readonly LyricsService _lyrics = new();
    private readonly WeatherService _weather = new();
    private readonly HardwareService _hardware = new();
    private readonly NotificationService _notifications = new();
    private readonly VolumeService _volume = new();
    private readonly LauncherService _launcher = new();
    private readonly SettingsService _settingsService = new();
    private readonly PrivacyService _privacy = new();
    private readonly SystemEventService _systemEvents = new();
    private readonly BluetoothService _bluetooth = new();
    private readonly ClipboardService _clipboard = new();
    private readonly FocusTimerService _focus = new();

    private readonly DispatcherTimer _fastTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _launcherErrorTimer;
    private string _launcherErrorText = string.Empty;
    private bool _hasLauncherError;

    private LyricsDocument _lyricsDocument = LyricsDocument.Empty;
    private string _lyricsTrackKey = string.Empty;
    private byte[]? _lastCoverBytes;
    private ImageSource? _coverImage;
    private Color? _coverAccentRaw;     // 未按主题钳制的采样色
    private Color? _coverAccent;        // 已按当前主题钳制的强调色
    private bool _disposed;
    private bool _initialized;
    private bool _bluetoothStarted;
    private DateTime _lastCalendarDate = DateTime.MinValue;
    private string _lastCollapsedText = string.Empty;
    private volatile bool _hardwareEnabled = true;
    private volatile bool _weatherEnabled = true;

    public IslandViewModel()
    {
        _settingsService.Load();
        Settings = _settingsService.Current;

        _fastTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
        _fastTimer.Tick += (_, _) => TickFast();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => TickClock();

        _launcherErrorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _launcherErrorTimer.Tick += (_, _) =>
        {
            _launcherErrorTimer.Stop();
            HasLauncherError = false;
            LauncherErrorText = string.Empty;
        };

        _launcher.LaunchFailed += OnLauncherLaunchFailed;

        TogglePlayCommand = new RelayCommand(async () => await _media.PlayPauseAsync());
        NextCommand = new RelayCommand(async () => await _media.NextAsync());
        PreviousCommand = new RelayCommand(async () => await _media.PreviousAsync());
        ToggleMuteCommand = new RelayCommand(ToggleMute);
        VolumeUpCommand = new RelayCommand(() => AdjustVolume(+0.05f));
        VolumeDownCommand = new RelayCommand(() => AdjustVolume(-0.05f));
        RefreshWeatherCommand = new RelayCommand(async () => await RefreshWeatherAsync(force: true));
        SelectTabCommand = new RelayCommand(parameter => SelectTab(parameter));
        LaunchCommand = new RelayCommand(parameter => Launch(parameter));
        RemoveLauncherCommand = new RelayCommand(parameter => RemoveLauncher(parameter));
        ClearNotificationsCommand = new RelayCommand(_ => ClearNotifications());
        SwitchSourceCommand = new RelayCommand(_ => _media.SelectNextSession());
        StartFocusCommand = new RelayCommand(parameter => StartFocus(parameter));
        StopFocusCommand = new RelayCommand(_ => StopFocus());
        CopyClipboardCommand = new RelayCommand(parameter => CopyClipboard(parameter));
        ClearClipboardCommand = new RelayCommand(_ => ClearClipboard());
        PreviousMonthCommand = new RelayCommand(() => ShiftCalendarMonth(-1));
        NextMonthCommand = new RelayCommand(() => ShiftCalendarMonth(1));
        CalendarTodayCommand = new RelayCommand(GoToCalendarToday);
        SelectDayCommand = new RelayCommand(parameter => SelectDay(parameter));
    }

    public AppSettings Settings { get; }

    // ---------------- 状态 ----------------

    private IslandState _state = IslandState.Collapsed;
    public IslandState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    private int _activeTab;
    public int ActiveTab
    {
        get => _activeTab;
        set
        {
            if (SetProperty(ref _activeTab, value))
            {
                OnPropertyChanged(nameof(IsMediaTab));
                OnPropertyChanged(nameof(IsWeatherTab));
                OnPropertyChanged(nameof(IsHardwareTab));
                OnPropertyChanged(nameof(IsLauncherTab));
                OnPropertyChanged(nameof(IsNotificationTab));
                OnPropertyChanged(nameof(IsLyricsTab));
            }
        }
    }

    public bool IsMediaTab => ActiveTab == 0;
    public bool IsWeatherTab => ActiveTab == 1;
    public bool IsHardwareTab => ActiveTab == 2;
    public bool IsLauncherTab => ActiveTab == 3;
    public bool IsNotificationTab => ActiveTab == 4;
    public bool IsLyricsTab => ActiveTab == 5;

    // ---------------- 时钟 ----------------

    private string _clockText = "--:--";
    public string ClockText
    {
        get => _clockText;
        private set => SetProperty(ref _clockText, value);
    }

    private string _dateText = string.Empty;
    public string DateText
    {
        get => _dateText;
        private set => SetProperty(ref _dateText, value);
    }

    // ---------------- 媒体 ----------------

    private bool _hasMedia;
    public bool HasMedia
    {
        get => _hasMedia;
        private set => SetProperty(ref _hasMedia, value);
    }

    private bool _canSwitchSource;
    public bool CanSwitchSource
    {
        get => _canSwitchSource;
        private set => SetProperty(ref _canSwitchSource, value);
    }

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    private string _artist = string.Empty;
    public string Artist
    {
        get => _artist;
        private set => SetProperty(ref _artist, value);
    }

    private ImageSource? _cover;
    public ImageSource? Cover
    {
        get => _cover;
        private set => SetProperty(ref _cover, value);
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayGlyph));
            }
        }
    }

    public string PlayGlyph => IsPlaying ? "\uE769" : "\uE768";

    private string _positionText = "0:00";
    public string PositionText
    {
        get => _positionText;
        private set => SetProperty(ref _positionText, value);
    }

    private string _durationText = "0:00";
    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    private string _remainText = "-0:00";
    public string RemainText
    {
        get => _remainText;
        private set => SetProperty(ref _remainText, value);
    }

    private double _progressRatio;
    public double ProgressRatio
    {
        get => _progressRatio;
        private set => SetProperty(ref _progressRatio, value);
    }

    private string _volumeText = "0%";
    public string VolumeText
    {
        get => _volumeText;
        private set => SetProperty(ref _volumeText, value);
    }

    private double _volumeRatio;
    public double VolumeRatio
    {
        get => _volumeRatio;
        private set => SetProperty(ref _volumeRatio, value);
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        private set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(VolumeGlyph));
            }
        }
    }

    public string VolumeGlyph => IsMuted || VolumeRatio <= 0.001 ? "\uE74F" : "\uE767";

    // ---------------- 歌词 ----------------

    private bool _hasLyrics;
    public bool HasLyrics
    {
        get => _hasLyrics;
        private set => SetProperty(ref _hasLyrics, value);
    }

    private string _lyricPrevious = string.Empty;
    public string LyricPrevious
    {
        get => _lyricPrevious;
        private set => SetProperty(ref _lyricPrevious, value);
    }

    private string _lyricCurrent = string.Empty;
    public string LyricCurrent
    {
        get => _lyricCurrent;
        private set => SetProperty(ref _lyricCurrent, value);
    }

    private string _lyricNext = string.Empty;
    public string LyricNext
    {
        get => _lyricNext;
        private set => SetProperty(ref _lyricNext, value);
    }

    private string _lyricNext2 = string.Empty;
    public string LyricNext2
    {
        get => _lyricNext2;
        private set => SetProperty(ref _lyricNext2, value);
    }

    /// <summary>当前歌词行内的播放进度（0-1），用于 karaoke 擦除。</summary>
    private double _lyricLineProgress;
    public double LyricLineProgress
    {
        get => _lyricLineProgress;
        private set => SetProperty(ref _lyricLineProgress, value);
    }

    /// <summary>当前歌词行的渐变画刷：已唱部分强调色，未唱部分次要色。</summary>
    private Brush _lyricCurrentBrush = Brushes.White;
    public Brush LyricCurrentBrush
    {
        get => _lyricCurrentBrush;
        private set => SetProperty(ref _lyricCurrentBrush, value);
    }

    public bool IsSyncedLyrics => _lyricsDocument.IsSynced;

    // ---------------- 天气 ----------------

    private bool _hasWeather;
    public bool HasWeather
    {
        get => _hasWeather;
        private set => SetProperty(ref _hasWeather, value);
    }

    private string _weatherIcon = "\u2601";
    public string WeatherIcon
    {
        get => _weatherIcon;
        private set => SetProperty(ref _weatherIcon, value);
    }

    private string _weatherTemperature = "--°";
    public string WeatherTemperature
    {
        get => _weatherTemperature;
        private set => SetProperty(ref _weatherTemperature, value);
    }

    private string _weatherDescription = string.Empty;
    public string WeatherDescription
    {
        get => _weatherDescription;
        private set => SetProperty(ref _weatherDescription, value);
    }

    private string _weatherDetail = string.Empty;
    public string WeatherDetail
    {
        get => _weatherDetail;
        private set => SetProperty(ref _weatherDetail, value);
    }

    private string _weatherForecast = string.Empty;
    public string WeatherForecast
    {
        get => _weatherForecast;
        private set => SetProperty(ref _weatherForecast, value);
    }

    private string _weatherLocation = string.Empty;
    public string WeatherLocation
    {
        get => _weatherLocation;
        private set => SetProperty(ref _weatherLocation, value);
    }

    // ---------------- 硬件 ----------------

    private double _cpuRatio;
    public double CpuRatio
    {
        get => _cpuRatio;
        private set => SetProperty(ref _cpuRatio, value);
    }

    private string _cpuText = "--%";
    public string CpuText
    {
        get => _cpuText;
        private set => SetProperty(ref _cpuText, value);
    }

    private double _memoryRatio;
    public double MemoryRatio
    {
        get => _memoryRatio;
        private set => SetProperty(ref _memoryRatio, value);
    }

    private string _memoryText = "--";
    public string MemoryText
    {
        get => _memoryText;
        private set => SetProperty(ref _memoryText, value);
    }

    private double _gpuRatio;
    public double GpuRatio
    {
        get => _gpuRatio;
        private set => SetProperty(ref _gpuRatio, value);
    }

    private string _gpuText = "--%";
    public string GpuText
    {
        get => _gpuText;
        private set => SetProperty(ref _gpuText, value);
    }

    private bool _hasGpu;
    public bool HasGpu
    {
        get => _hasGpu;
        private set => SetProperty(ref _hasGpu, value);
    }

    private double _diskRatio;
    public double DiskRatio
    {
        get => _diskRatio;
        private set => SetProperty(ref _diskRatio, value);
    }

    private string _diskText = "--%";
    public string DiskText
    {
        get => _diskText;
        private set => SetProperty(ref _diskText, value);
    }

    private string _networkText = "↓0 B/s ↑0 B/s";
    public string NetworkText
    {
        get => _networkText;
        private set => SetProperty(ref _networkText, value);
    }

    // 迷你曲线：保留最近 28 个采样点，硬件页用 Polyline 画 sparkline
    private const int HistoryCapacity = 28;
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _memoryHistory = new();
    private readonly Queue<double> _gpuHistory = new();
    private readonly Queue<double> _diskHistory = new();

    private PointCollection _cpuHistoryPoints = new();
    public PointCollection CpuHistoryPoints
    {
        get => _cpuHistoryPoints;
        private set => SetProperty(ref _cpuHistoryPoints, value);
    }

    private PointCollection _memoryHistoryPoints = new();
    public PointCollection MemoryHistoryPoints
    {
        get => _memoryHistoryPoints;
        private set => SetProperty(ref _memoryHistoryPoints, value);
    }

    private PointCollection _gpuHistoryPoints = new();
    public PointCollection GpuHistoryPoints
    {
        get => _gpuHistoryPoints;
        private set => SetProperty(ref _gpuHistoryPoints, value);
    }

    private PointCollection _diskHistoryPoints = new();
    public PointCollection DiskHistoryPoints
    {
        get => _diskHistoryPoints;
        private set => SetProperty(ref _diskHistoryPoints, value);
    }

    // ---------------- 通知 ----------------

    private string _notificationApp = string.Empty;
    public string NotificationApp
    {
        get => _notificationApp;
        private set => SetProperty(ref _notificationApp, value);
    }

    private string _notificationTitle = string.Empty;
    public string NotificationTitle
    {
        get => _notificationTitle;
        private set => SetProperty(ref _notificationTitle, value);
    }

    private string _notificationBody = string.Empty;
    public string NotificationBody
    {
        get => _notificationBody;
        private set => SetProperty(ref _notificationBody, value);
    }

    private ImageSource? _notificationIcon;
    public ImageSource? NotificationIcon
    {
        get => _notificationIcon;
        private set
        {
            if (SetProperty(ref _notificationIcon, value))
            {
                OnPropertyChanged(nameof(HasNotificationIcon));
            }
        }
    }

    public bool HasNotificationIcon => NotificationIcon is not null;

    private string _notificationInitial = "?";
    public string NotificationInitial
    {
        get => _notificationInitial;
        private set => SetProperty(ref _notificationInitial, value);
    }

    private int _notificationCount;
    public int NotificationCount
    {
        get => _notificationCount;
        private set
        {
            if (SetProperty(ref _notificationCount, value))
            {
                OnPropertyChanged(nameof(HasNotifications));
            }
        }
    }

    public bool HasNotifications => _notificationCount > 0;

    /// <summary>通知后端状态（通知页顶部提示用）。</summary>
    public string NotificationStatusText => _notifications.Backend switch
    {
        NotificationBackend.WinRtListener => "系统通知 · 已授权",
        NotificationBackend.WpnDatabase => "系统通知 · 数据库只读",
        _ => "系统通知不可用"
    };

    public bool NotificationAccessOk => _notifications.IsReady;

    public ObservableCollection<NotificationEntryViewModel> Notifications { get; } = new();

    // ---------------- 系统感知：隐私点 / 系统浮层 / 蓝牙 / 剪贴板 / 专注 / 日历 ----------------

    /// <summary>需要弹一条岛下浮层（电源、USB、蓝牙、专注结束等）。</summary>
    public event Action<SystemAlert>? SystemAlertRequested;

    /// <summary>复制了内容，需要弹剪贴板缩略预览。</summary>
    public event Action? ClipboardCaptured;

    /// <summary>请求把某条记录写回系统剪贴板（实际 Clipboard 操作在窗口层）。</summary>
    public event Action<ClipboardEntry>? ClipboardCopyRequested;

    /// <summary>设置面板改动已生效（窗口层据此重读动画风格等界面参数）。</summary>
    public event Action? SettingsApplied;

    private bool _privacyMicActive;
    public bool PrivacyMicActive
    {
        get => _privacyMicActive;
        private set
        {
            if (SetProperty(ref _privacyMicActive, value))
            {
                OnPropertyChanged(nameof(PrivacyVisible));
            }
        }
    }

    private bool _privacyCameraActive;
    public bool PrivacyCameraActive
    {
        get => _privacyCameraActive;
        private set
        {
            if (SetProperty(ref _privacyCameraActive, value))
            {
                OnPropertyChanged(nameof(PrivacyVisible));
            }
        }
    }

    public bool PrivacyVisible => Settings.EnablePrivacyDots && (PrivacyMicActive || PrivacyCameraActive);

    /// <summary>折叠胶囊里显示的一行文字：有同步歌词时优先显示歌词。</summary>
    public string CollapsedText =>
        Settings.ShowLyricsInPill && HasLyrics && !string.IsNullOrWhiteSpace(LyricCurrent)
            ? LyricCurrent
            : Title;

    /// <summary>
    /// 只在折叠文字真正变化时才通知。媒体轮询是 200ms 一次，
    /// 如果每轮都通知，界面会不停重播标题淡入动画，表现为歌名闪烁。
    /// </summary>
    private void NotifyCollapsedTextChanged()
    {
        string current = CollapsedText;
        if (string.Equals(current, _lastCollapsedText, StringComparison.Ordinal))
        {
            return;
        }

        _lastCollapsedText = current;
        OnPropertyChanged(nameof(CollapsedText));
    }

    public bool ShowPillClock => Settings.ShowPillClock;

    /// <summary>专注计时进行中时，胶囊上让位给倒计时。</summary>
    public bool ShowClockInPill => ShowPillClock && !FocusActive;

    // 专注计时
    public bool FocusActive => _focus.IsActive;

    public bool FocusIsBreak => _focus.IsBreak;

    public string FocusText
    {
        get
        {
            TimeSpan left = _focus.Remaining;
            return _focus.IsActive ? $"{(int)left.TotalMinutes}:{left.Seconds:00}" : string.Empty;
        }
    }

    public string FocusCaption => _focus.IsBreak ? "休息" : "专注";

    // 剪贴板历史
    public ObservableCollection<ClipboardEntryViewModel> ClipboardItems { get; } = new();

    public bool HasClipboard => ClipboardItems.Count > 0;

    public string ClipboardStatus => ClipboardItems.Count == 0
        ? "复制文字或图片后会出现在这里"
        : $"{ClipboardItems.Count} 条记录 · 点击可复制回剪贴板";

    // 日历
    public ObservableCollection<CalendarCell> CalendarDays { get; } = new();

    // 当前显示的是哪个月（1 号），与"今天"分开：翻月不会改变今天
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selectedDate = DateTime.Today;

    private string _calendarTitle = string.Empty;
    public string CalendarTitle
    {
        get => _calendarTitle;
        private set => SetProperty(ref _calendarTitle, value);
    }

    /// <summary>标题右侧的选中日期文案。</summary>
    public string CalendarSelectedText
        => _selectedDate.ToString("M月d日 dddd", new System.Globalization.CultureInfo("zh-CN"));

    public string CalendarWeekdays => "一 二 三 四 五 六 日";

    // ---------------- 图标坞 ----------------

    public ObservableCollection<LauncherEntryViewModel> Launchers { get; } = new();

    private bool _hasLaunchers;
    public bool HasLaunchers
    {
        get => _hasLaunchers;
        private set => SetProperty(ref _hasLaunchers, value);
    }

    // ---------------- 命令 ----------------

    public RelayCommand TogglePlayCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public RelayCommand VolumeUpCommand { get; }
    public RelayCommand VolumeDownCommand { get; }
    public RelayCommand RefreshWeatherCommand { get; }
    public RelayCommand SelectTabCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand RemoveLauncherCommand { get; }
    public RelayCommand ClearNotificationsCommand { get; }
    public RelayCommand SwitchSourceCommand { get; }
    public RelayCommand StartFocusCommand { get; }
    public RelayCommand StopFocusCommand { get; }
    public RelayCommand CopyClipboardCommand { get; }
    public RelayCommand ClearClipboardCommand { get; }
    public RelayCommand PreviousMonthCommand { get; }
    public RelayCommand NextMonthCommand { get; }
    public RelayCommand CalendarTodayCommand { get; }
    public RelayCommand SelectDayCommand { get; }

    public event EventHandler<NotificationItem>? NotificationArrived;

    // ---------------- 初始化 ----------------

    public async Task InitializeAsync()
    {
        // 防重入：Window.Loaded 在某些情况下（DPI 变化、显示设置改动）会再次触发，
        // 若不拦截会重新加载配置、清空图标坞，用户刚拖进去的图标会莫名消失。
        if (_initialized)
        {
            LogService.Info("InitializeAsync 重复调用已忽略");
            return;
        }

        _initialized = true;

        NotificationService.EnsureAppUserModelId();
        LogService.Init();

        _hardwareEnabled = Settings.EnableHardware;
        _weatherEnabled = Settings.EnableWeather;

        await _media.InitializeAsync();
        _media.TrackChanged += OnTrackChanged;
        _media.Updated += OnMediaUpdated;

        // 首曲兜底：InitializeAsync 内部的第一次 Refresh 会在订阅之前触发 TrackChanged，
        // 不主动补一刀的话，启动时正在播放的歌会一直显示“暂无歌词”。
        if (_media.Current.HasSession)
        {
            _ = LoadLyricsAsync(_media.Current);
        }

        _weather.CityName = Settings.CityName;
        _weather.Latitude = Settings.Latitude;
        _weather.Longitude = Settings.Longitude;

        _notifications.NotificationReceived += OnNotificationReceived;
        await _notifications.InitializeAsync();
        OnPropertyChanged(nameof(NotificationStatusText));
        OnPropertyChanged(nameof(NotificationAccessOk));

        LoadLaunchers();
        ConfigureSystemServices();

        TickClock();
        _fastTimer.Start();
        _clockTimer.Start();

        UpdateVolume();

        _ = Task.Run(HardwareLoopAsync);
        _ = RefreshWeatherAsync(force: true);
    }

    private void TickClock()
    {
        var now = DateTime.Now;
        ClockText = now.ToString("HH:mm");
        DateText = now.ToString("M月d日 dddd", new System.Globalization.CultureInfo("zh-CN"));

        if (now.Date != _lastCalendarDate)
        {
            RefreshCalendar();
        }
    }

    private void TickFast()
    {
        UpdateMedia();
        UpdateVolume();
    }

    private void OnMediaUpdated(object? sender, TrackInfo track) => RunOnUi(UpdateMedia);

    private void OnTrackChanged(object? sender, TrackInfo track)
    {
        _ = LoadLyricsAsync(track);
    }

    private void UpdateMedia()
    {
        var track = _media.Current;
        if (!track.HasSession)
        {
            if (HasMedia)
            {
                HasMedia = false;
                Cover = null;
                _coverAccent = null;
                _coverAccentRaw = null;
                ThemeManager.AnimateAccent(ThemeManager.Current.Accent);
                HasLyrics = false;
                PositionText = "0:00";
                DurationText = "0:00";
                RemainText = "-0:00";
                ProgressRatio = 0;
            }

            return;
        }

        HasMedia = true;
        Title = track.Title;
        Artist = string.IsNullOrWhiteSpace(track.Artist) ? track.SourceAppName : track.Artist;
        IsPlaying = track.IsPlaying;
        CanSwitchSource = _media.SessionCount > 1;
        NotifyCollapsedTextChanged();

        if (!ReferenceEquals(track.CoverBytes, _lastCoverBytes))
        {
            _lastCoverBytes = track.CoverBytes;
            _coverImage = ToImageSource(track.CoverBytes);
            Cover = _coverImage;

            // 强调色跟随封面：换歌时从旧色指数过渡到新色（约 300ms 半衰期）
            _coverAccentRaw = AccentSampler.SampleAverage(track.CoverBytes);
            _coverAccent = _coverAccentRaw is { } raw
                ? AccentSampler.FitToTheme(raw, ThemeManager.Current)
                : null;
            ThemeManager.AnimateAccent(_coverAccent ?? ThemeManager.Current.Accent);
        }

        if (_isSeeking)
        {
            // 拖动进度条期间，界面以手指位置为准，不被轮询结果拉回
            ApplySeekPreview();
            return;
        }

        if (TryHoldSeekPosition(track))
        {
            // 松手后播放器还没上报新位置：先保持选中位置，避免被旧值拉回
            return;
        }

        // 用本地时钟把 450ms 轮询之间的位置补上：进度条和歌词涂色才是连续前进的，
        // 否则每 0.45 秒才跳一段，看起来就是"歌词跟不上"。
        TimeSpan position = EffectivePosition(track);
        PositionText = FormatTime(position);
        DurationText = FormatTime(track.Duration);
        RemainText = FormatRemain(track.Duration - position);
        ProgressRatio = track.Duration > TimeSpan.Zero
            ? Math.Clamp(position.TotalMilliseconds / track.Duration.TotalMilliseconds, 0, 1)
            : 0;
        UpdateLyricLines(track, position);
    }

    /// <summary>
    /// 在两次媒体轮询（450ms）之间用本地时钟外推播放位置。
    /// 不做单调钳制：seek 后退本来就该允许回退，播放器上报抖动最多让涂色退一两帧，肉眼看不出来。
    /// </summary>
    private static TimeSpan EffectivePosition(TrackInfo track)
    {
        if (!track.IsPlaying || track.PositionCapturedAt == default)
        {
            return track.Position;
        }

        TimeSpan age = DateTimeOffset.Now - track.PositionCapturedAt;
        if (age <= TimeSpan.Zero || age > TimeSpan.FromSeconds(3))
        {
            return track.Position;
        }

        TimeSpan position = track.Position + age;
        if (track.Duration > TimeSpan.Zero && position > track.Duration)
        {
            position = track.Duration;
        }

        return position;
    }

    private void UpdateLyricLines(TrackInfo track, TimeSpan? positionOverride = null)
    {
        var document = _lyricsDocument;
        if (document is null || document.Lines.Count == 0)
        {
            if (HasLyrics)
            {
                HasLyrics = false;
            }

            return;
        }

        if (!HasLyrics)
        {
            HasLyrics = true;
        }

        var position = (positionOverride ?? track.Position) + TimeSpan.FromMilliseconds(Settings.LyricsOffsetMs);
        int index = document.IndexAt(position);
        if (index < 0)
        {
            index = 0;
        }

        var lines = document.Lines;
        LyricCurrent = lines[index].Text;
        LyricPrevious = index > 0 ? lines[index - 1].Text : string.Empty;
        LyricNext = index + 1 < lines.Count ? lines[index + 1].Text : string.Empty;
        LyricNext2 = index + 2 < lines.Count ? lines[index + 2].Text : string.Empty;

        // karaoke：当前行按"行内进度"做强调色擦除
        var start = lines[index].Time;
        var end = index + 1 < lines.Count ? lines[index + 1].Time : start + TimeSpan.FromSeconds(4);
        double progress = 0;
        if (end > start)
        {
            progress = Math.Clamp((position - start).TotalMilliseconds / (end - start).TotalMilliseconds, 0, 1);
        }

        LyricLineProgress = progress;
        LyricCurrentBrush = BuildLyricBrush(progress);
        NotifyCollapsedTextChanged();
    }

    /// <summary>
    /// 当前行渐变：已唱部分用强调色，未唱部分用次要色；
    /// 擦除边界再叠一条提亮的柔和光带，做出「唱过去」的扫光痕迹。
    /// </summary>
    private static Brush BuildLyricBrush(double progress)
    {
        Color accent = ThemeManager.CurrentAccent;
        Color secondary = ThemeManager.Current.Secondary;

        if (progress <= 0.002)
        {
            var plain = new SolidColorBrush(secondary);
            plain.Freeze();
            return plain;
        }

        if (progress >= 0.998)
        {
            var done = new SolidColorBrush(accent);
            done.Freeze();
            return done;
        }

        Color glow = MixColor(accent, Colors.White, 0.72);
        const double band = 0.045;   // 光带宽度：擦除边界往前的一小段
        const double fade = 0.022;   // 光带之后到未唱色的过渡
        double left = Math.Max(0, progress - band);
        double right = Math.Min(1, progress + fade);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        brush.GradientStops.Add(new GradientStop(accent, 0));
        brush.GradientStops.Add(new GradientStop(accent, left));
        brush.GradientStops.Add(new GradientStop(glow, progress));
        brush.GradientStops.Add(new GradientStop(secondary, right));
        brush.GradientStops.Add(new GradientStop(secondary, 1));
        brush.Freeze();
        return brush;
    }

    private static Color MixColor(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(from.A + (to.A - from.A) * t),
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    private async Task LoadLyricsAsync(TrackInfo track)
    {
        if (!Settings.EnableLyrics || !track.HasSession)
        {
            return;
        }

        string lyricsKey = BuildLyricsKey(track);
        if (string.Equals(_lyricsTrackKey, lyricsKey, StringComparison.Ordinal))
        {
            return;
        }

        _lyricsTrackKey = lyricsKey;
        _lyricsDocument = LyricsDocument.Empty;

        try
        {
            var document = await _lyrics.GetLyricsAsync(track);
            RunOnUi(() =>
            {
                if (string.Equals(_lyricsTrackKey, lyricsKey, StringComparison.Ordinal))
                {
                    _lyricsDocument = document;
                    HasLyrics = document.Lines.Count > 0;
                    UpdateMedia();
                }
            });
        }
        catch (Exception ex)
        {
            LogService.Warn("加载歌词失败：" + ex.Message);
        }
    }

    /// <summary>歌词缓存键不含 duration：播放器上报的时长会跳动，含时长会导致重复请求。</summary>
    private static string BuildLyricsKey(TrackInfo track)
        => $"{track.SourceAppId}|{track.Title}|{track.Artist}";

    private async Task RefreshWeatherAsync(bool force)
    {
        if (!_weatherEnabled)
        {
            return;
        }

        try
        {
            var info = await _weather.GetAsync(force);
            RunOnUi(() =>
            {
                if (!info.Ok)
                {
                    return;
                }

                HasWeather = true;
                WeatherIcon = info.Icon;
                WeatherTemperature = $"{info.Temperature:F0}°";
                WeatherDescription = info.Description;
                WeatherLocation = info.LocationName;
                WeatherDetail = $"体感 {info.FeelsLike:F0}° · 湿度 {info.Humidity}% · 风 {info.WindSpeed:F0} km/h";
                WeatherForecast = string.Join("   ",
                    info.Daily.Take(3).Select(d =>
                        $"{d.Date:M/d} {d.Description} {d.MinTemperature:F0}~{d.MaxTemperature:F0}°"));
            });
        }
        catch (Exception ex)
        {
            LogService.Warn("刷新天气失败：" + ex.Message);
        }
    }

    private async Task HardwareLoopAsync()
    {
        _hardware.Sample(); // 建立基线
        while (!_disposed)
        {
            if (_hardwareEnabled)
            {
                try
                {
                    var stats = _hardware.Sample();
                    RunOnUi(() => ApplyHardware(stats));
                }
                catch (Exception ex)
                {
                    LogService.Warn("硬件采样失败：" + ex.Message);
                }
            }

            await Task.Delay(2000).ConfigureAwait(false);
        }
    }

    private void ApplyHardware(HardwareStats stats)
    {
        CpuRatio = stats.CpuUsage / 100;
        CpuText = $"{stats.CpuUsage:F0}%";

        MemoryRatio = stats.MemoryPercent / 100;
        MemoryText = $"{stats.MemoryUsedGb:F1}/{stats.MemoryTotalGb:F1} GB";

        HasGpu = _hardware.GpuAvailable;
        GpuRatio = stats.GpuUsage / 100;
        GpuText = $"{stats.GpuUsage:F0}%";

        DiskRatio = stats.DiskUsedPercent / 100;
        DiskText = $"{stats.DiskUsedPercent:F0}%";

        NetworkText = $"↓{FormatRate(stats.NetDownMbps)} ↑{FormatRate(stats.NetUpMbps)}";

        PushHistory(_cpuHistory, CpuRatio);
        PushHistory(_memoryHistory, MemoryRatio);
        PushHistory(_gpuHistory, GpuRatio);
        PushHistory(_diskHistory, DiskRatio);

        CpuHistoryPoints = BuildPoints(_cpuHistory);
        MemoryHistoryPoints = BuildPoints(_memoryHistory);
        GpuHistoryPoints = BuildPoints(_gpuHistory);
        DiskHistoryPoints = BuildPoints(_diskHistory);
    }

    private static void PushHistory(Queue<double> queue, double value)
    {
        queue.Enqueue(Math.Clamp(value, 0, 1));
        while (queue.Count > HistoryCapacity)
        {
            queue.Dequeue();
        }
    }

    /// <summary>把 0-1 的采样序列映射成 44×14 的折线坐标。</summary>
    private static PointCollection BuildPoints(Queue<double> queue)
    {
        var points = new PointCollection();
        if (queue.Count == 0)
        {
            return points;
        }

        const double width = 44;
        const double height = 14;
        double step = queue.Count > 1 ? width / (queue.Count - 1) : 0;
        int index = 0;
        foreach (double value in queue)
        {
            double y = height - 1 - Math.Clamp(value, 0, 1) * (height - 2);
            points.Add(new Point(index * step, y));
            index++;
        }

        points.Freeze();
        return points;
    }

    private void UpdateVolume()
    {
        if (!_volume.IsAvailable)
        {
            return;
        }

        VolumeRatio = _volume.Volume;
        IsMuted = _volume.IsMuted;
        VolumeText = $"{(int)Math.Round(VolumeRatio * 100)}%";
    }

    private void ToggleMute()
    {
        _volume.ToggleMute();
        UpdateVolume();
    }

    private void AdjustVolume(float delta)
    {
        _volume.Adjust(delta);
        UpdateVolume();
    }

    public void ChangeVolumeBy(float delta)
    {
        _volume.Adjust(delta);
        UpdateVolume();
    }

    // ---------------- 进度条拖动 ----------------

    private bool _isSeeking;
    private double _seekRatio;

    // 松手后到播放器真正上报新位置之间，界面先保持在用户选中的位置。
    // 否则每隔 450ms 的轮询会先把旧位置画回去，过一会儿才跳到目标——表现为「缩回去等一会」。
    private bool _seekHold;
    private TimeSpan _seekHoldTarget;
    private DateTime _seekHoldStartedUtc;
    private bool _seekHoldWasPlaying;
    private string _seekHoldTrackKey = string.Empty;

    public bool IsSeeking
    {
        get => _isSeeking;
        private set => SetProperty(ref _isSeeking, value);
    }

    public void BeginSeek(double ratio)
    {
        if (!HasMedia)
        {
            return;
        }

        IsSeeking = true;
        _seekHold = false;
        _seekRatio = Math.Clamp(ratio, 0, 1);
        ApplySeekPreview();
    }

    public void UpdateSeek(double ratio)
    {
        if (!_isSeeking)
        {
            return;
        }

        _seekRatio = Math.Clamp(ratio, 0, 1);
        ApplySeekPreview();
    }

    /// <summary>松手：提交跳转。</summary>
    public void EndSeek(double ratio)
    {
        if (!_isSeeking)
        {
            return;
        }

        _seekRatio = Math.Clamp(ratio, 0, 1);
        IsSeeking = false;

        var track = _media.Current;
        if (!track.HasSession || track.Duration <= TimeSpan.Zero)
        {
            ApplySeekPreview();
            return;
        }

        var target = TimeSpan.FromTicks((long)(track.Duration.Ticks * _seekRatio));

        // 立刻显示选中位置，并保持到播放器确认（或超时）为止
        _seekHold = true;
        _seekHoldTarget = target;
        _seekHoldStartedUtc = DateTime.UtcNow;
        _seekHoldWasPlaying = track.IsPlaying;
        _seekHoldTrackKey = track.IdentityKey;

        ApplySeekPreview();
        LogService.Info($"进度跳转：{FormatTime(target)} / {FormatTime(track.Duration)}");

        _ = SeekAndConfirmAsync(target, track.IdentityKey);
    }

    private async Task SeekAndConfirmAsync(TimeSpan target, string trackKey)
    {
        bool submitted = await _media.SeekAsync(target);
        if (submitted)
        {
            return;
        }

        // 播放器拒收：立刻放弃过渡态，回到真实进度
        RunOnUi(() =>
        {
            if (!_seekHold || !string.Equals(_seekHoldTrackKey, trackKey, StringComparison.Ordinal))
            {
                return;
            }

            _seekHold = false;
            UpdateMedia();
        });
    }

    private void ApplySeekPreview()
    {
        var track = _media.Current;
        ProgressRatio = _seekRatio;
        var position = TimeSpan.FromTicks((long)(track.Duration.Ticks * _seekRatio));
        PositionText = FormatTime(position);
        RemainText = FormatRemain(track.Duration - position);
        UpdateLyricLines(track, position);
    }

    /// <summary>
    /// 松手后的过渡期：保持选中位置（播放中则继续按时间前进），
    /// 直到媒体位置追平目标、换歌或超时。返回 true 表示本帧仍由过渡态接管。
    /// </summary>
    private bool TryHoldSeekPosition(TrackInfo track)
    {
        if (!_seekHold)
        {
            return false;
        }

        if (!string.Equals(track.IdentityKey, _seekHoldTrackKey, StringComparison.Ordinal))
        {
            _seekHold = false;
            return false;
        }

        var elapsed = DateTime.UtcNow - _seekHoldStartedUtc;
        var expected = _seekHoldTarget + (_seekHoldWasPlaying && track.IsPlaying ? elapsed : TimeSpan.Zero);
        if (track.Duration > TimeSpan.Zero && expected > track.Duration)
        {
            expected = track.Duration;
        }

        // 给播放器一个上报周期（约 300ms）再比对，避免用松手瞬间尚未刷新的旧位置误判到位
        bool reached = elapsed >= TimeSpan.FromMilliseconds(300)
            && Math.Abs((track.Position - expected).TotalMilliseconds) <= 1200;

        if (reached)
        {
            _seekHold = false;
            return false;
        }

        if (elapsed >= TimeSpan.FromSeconds(4))
        {
            _seekHold = false;
            LogService.Warn(
                $"进度跳转未确认，恢复真实进度（目标 {FormatTime(_seekHoldTarget)}，实际 {FormatTime(track.Position)}）");
            return false;
        }

        ProgressRatio = track.Duration > TimeSpan.Zero
            ? Math.Clamp(expected.TotalMilliseconds / track.Duration.TotalMilliseconds, 0, 1)
            : 0;
        PositionText = FormatTime(expected);
        DurationText = FormatTime(track.Duration);
        RemainText = FormatRemain(track.Duration - expected);
        UpdateLyricLines(track, expected);
        return true;
    }

    /// <summary>设置绝对音量（0-1）。</summary>
    public void SetVolumeAbsolute(float value)
    {
        _volume.SetVolume(value);
        UpdateVolume();
    }

    /// <summary>推一条测试通知（自测 / 脚本联调用）。</summary>
    public void PushTestNotification(string app, string title, string body)
    {
        var item = new NotificationItem(
            (uint)Environment.TickCount64,
            app,
            "test",
            title,
            body,
            DateTimeOffset.Now);

        var entry = new NotificationEntryViewModel(item);
        NotificationApp = entry.AppName;
        NotificationTitle = entry.DisplayTitle;
        NotificationBody = entry.Body;
        NotificationIcon = entry.Icon;
        NotificationInitial = entry.Initial;
        Notifications.Insert(0, entry);
        NotificationCount = Notifications.Count;
        NotificationArrived?.Invoke(this, item);
    }

    /// <summary>设置窗口中改动后调用：把配置同步到各个服务并立即生效。</summary>
    public void ApplySettings()
    {
        _weather.CityName = Settings.CityName;
        _weather.Latitude = Settings.Latitude;
        _weather.Longitude = Settings.Longitude;

        _hardwareEnabled = Settings.EnableHardware;
        _weatherEnabled = Settings.EnableWeather;

        if (!Settings.EnableWeather)
        {
            HasWeather = false;
        }
        else
        {
            _ = RefreshWeatherAsync(force: true);
        }

        if (!Settings.EnableLyrics)
        {
            _lyricsDocument = LyricsDocument.Empty;
            _lyricsTrackKey = string.Empty;
            HasLyrics = false;
        }
        else if (_media.Current.HasSession)
        {
            _lyricsTrackKey = string.Empty;
            _ = LoadLyricsAsync(_media.Current);
        }

        _settingsService.Save();
        LogService.Info($"设置已应用：主题={Settings.Theme}，城市={Settings.CityName}，" +
                        $"天气={Settings.EnableWeather}，硬件={Settings.EnableHardware}，" +
                        $"歌词={Settings.EnableLyrics}，通知={Settings.EnableNotifications}");

        if (Settings.EnablePrivacyDots)
        {
            _privacy.Start();
        }
        else
        {
            _privacy.Stop();
        }

        if (Settings.EnableBluetooth)
        {
            StartBluetoothIfNeeded();
        }
        else
        {
            _bluetooth.Stop();
        }

        OnPropertyChanged(nameof(PrivacyVisible));
        OnPropertyChanged(nameof(ShowPillClock));
        OnPropertyChanged(nameof(ShowClockInPill));
        NotifyCollapsedTextChanged();
        SettingsApplied?.Invoke();
    }

    /// <summary>
    /// 重新计算强调色目标：有封面用封面采样色（按当前主题重新钳制），否则回到主题强调色。
    /// 换主题后必须调用一次，否则强调色会停在按上一套主题亮度校正过的颜色上。
    /// </summary>
    public void RefreshAccent()
    {
        _coverAccent = _coverAccentRaw is { } raw
            ? AccentSampler.FitToTheme(raw, ThemeManager.Current)
            : null;
        ThemeManager.AnimateAccent(_coverAccent ?? ThemeManager.Current.Accent);
    }

    private void SelectTab(object? parameter)
    {
        if (parameter is int index)
        {
            ActiveTab = index;
        }
        else if (parameter is string text && int.TryParse(text, out int parsed))
        {
            ActiveTab = parsed;
        }
    }

    // ---------------- 图标坞 ----------------

    public void LoadLaunchers()
    {
        Launchers.Clear();
        foreach (var item in Settings.LauncherItems)
        {
            Launchers.Add(new LauncherEntryViewModel(item, _launcher));
        }

        HasLaunchers = Launchers.Count > 0;
    }

    /// <summary>接收拖入的文件/快捷方式，返回成功加入的数量。</summary>
    public int AddLauncherPaths(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            var item = _launcher.CreateItem(path);
            if (item is null)
            {
                continue;
            }

            if (Settings.LauncherItems.Any(x =>
                    string.Equals(x.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Settings.LauncherItems.Add(item);
            Launchers.Add(new LauncherEntryViewModel(item, _launcher));
            added++;
        }

        if (added > 0)
        {
            _settingsService.Save();
            HasLaunchers = true;
            LogService.Info($"图标坞新增 {added} 项，共 {Launchers.Count} 项");
        }

        return added;
    }

    /// <summary>图标坞的启动失败提示（4 秒后自动消失）。</summary>
    public bool HasLauncherError
    {
        get => _hasLauncherError;
        private set => SetProperty(ref _hasLauncherError, value);
    }

    public string LauncherErrorText
    {
        get => _launcherErrorText;
        private set => SetProperty(ref _launcherErrorText, value);
    }

    private void OnLauncherLaunchFailed(LauncherItem item, string message)
    {
        RunOnUi(() =>
        {
            LauncherErrorText = message;
            HasLauncherError = true;
            _launcherErrorTimer.Stop();
            _launcherErrorTimer.Start();
        });
    }

    private void Launch(object? parameter)
    {
        if (parameter is LauncherEntryViewModel entry)
        {
            if (!_launcher.Launch(entry.Item, out string? error))
            {
                string message = $"无法启动“{entry.Name}”。\n\n{error}\n\n详细记录：{AppPaths.LogFile}";
                if (Application.Current?.MainWindow is { } owner)
                {
                    MessageBox.Show(owner, message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show(message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }

    /// <summary>清空图标坞。</summary>
    public void ClearLaunchers()
    {
        Settings.LauncherItems.Clear();
        Launchers.Clear();
        _settingsService.Save();
        HasLaunchers = false;
    }

    private void RemoveLauncher(object? parameter)
    {
        if (parameter is not LauncherEntryViewModel entry)
        {
            return;
        }

        Settings.LauncherItems.Remove(entry.Item);
        Launchers.Remove(entry);
        _settingsService.Save();
        HasLaunchers = Launchers.Count > 0;
    }

    /// <summary>拖拽排序：把 from 移动到 to 的位置（两个集合保持一致并持久化）。</summary>
    public void MoveLauncher(LauncherEntryViewModel from, LauncherEntryViewModel to)
    {
        int fromIndex = Launchers.IndexOf(from);
        int toIndex = Launchers.IndexOf(to);
        if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex)
        {
            return;
        }

        Launchers.Move(fromIndex, toIndex);
        Settings.LauncherItems.Remove(from.Item);
        int insertIndex = Math.Clamp(toIndex, 0, Settings.LauncherItems.Count);
        Settings.LauncherItems.Insert(insertIndex, from.Item);
        _settingsService.Save();
        LogService.Info($"图标坞排序：{from.Name} → 位置 {insertIndex + 1}");
    }

    // ---------------- 系统感知 / 剪贴板 / 专注 / 日历 ----------------

    private void ConfigureSystemServices()
    {
        _privacy.Changed += () => RunOnUi(() =>
        {
            PrivacyMicActive = _privacy.MicActive;
            PrivacyCameraActive = _privacy.CameraActive;
        });

        if (Settings.EnablePrivacyDots)
        {
            _privacy.Start();
        }

        _systemEvents.AlertRequested += alert => RunOnUi(() =>
        {
            if (Settings.EnableSystemAlerts)
            {
                SystemAlertRequested?.Invoke(alert);
            }
        });
        _systemEvents.Start();

        _bluetooth.AlertRequested += alert => RunOnUi(() =>
        {
            if (Settings.EnableBluetooth)
            {
                SystemAlertRequested?.Invoke(alert);
            }
        });
        StartBluetoothIfNeeded();

        _clipboard.ItemAdded += _ => RunOnUi(() =>
        {
            if (Settings.EnableClipboard)
            {
                ClipboardCaptured?.Invoke();
            }
        });
        _clipboard.Changed += () => RunOnUi(RefreshClipboardList);

        _focus.Tick += () => RunOnUi(NotifyFocusChanged);
        _focus.Finished += () => RunOnUi(() =>
        {
            NotifyFocusChanged();
            if (Settings.EnableSystemAlerts)
            {
                SystemAlertRequested?.Invoke(new SystemAlert("\uE823", "专注结束", "起来活动一下，喝杯水吧"));
            }
        });

        RefreshCalendar();
        RefreshClipboardList();
    }

    private void StartBluetoothIfNeeded()
    {
        if (!Settings.EnableBluetooth || _bluetoothStarted)
        {
            return;
        }

        _bluetooth.Start();
        _bluetoothStarted = true;
    }

    public void AddClipboardText(string text) => _clipboard.AddText(text);

    public void AddClipboardImage(byte[] png) => _clipboard.AddImage(png);

    /// <summary>自身写回剪贴板前调用，避免把回填当成新的复制。</summary>
    public void MarkClipboardWrite() => _clipboard.IgnoreNext = true;

    public void NotifyUsbDevice(bool arrived) => _systemEvents.NotifyUsbDevice(arrived);

    private void RefreshClipboardList()
    {
        ClipboardItems.Clear();
        foreach (ClipboardEntry entry in _clipboard.Items)
        {
            ClipboardItems.Add(new ClipboardEntryViewModel(entry));
        }

        OnPropertyChanged(nameof(HasClipboard));
        OnPropertyChanged(nameof(ClipboardStatus));
    }

    private void CopyClipboard(object? parameter)
    {
        if (parameter is ClipboardEntryViewModel entry)
        {
            ClipboardCopyRequested?.Invoke(entry.Entry);
        }
    }

    private void ClearClipboard()
    {
        _clipboard.Clear();
        RefreshClipboardList();
    }

    private void StartFocus(object? parameter)
    {
        int minutes = parameter switch
        {
            int value => value,
            string text when int.TryParse(text, out int parsed) => parsed,
            _ => 25
        };

        _focus.Start(minutes, minutes <= 10);
        NotifyFocusChanged();
    }

    private void StopFocus()
    {
        _focus.Stop();
        NotifyFocusChanged();
    }

    private void NotifyFocusChanged()
    {
        OnPropertyChanged(nameof(FocusActive));
        OnPropertyChanged(nameof(FocusText));
        OnPropertyChanged(nameof(FocusIsBreak));
        OnPropertyChanged(nameof(FocusCaption));
        OnPropertyChanged(nameof(ShowClockInPill));
    }

    private void RefreshCalendar()
    {
        DateTime today = DateTime.Today;
        DateTime first = new(_calendarMonth.Year, _calendarMonth.Month, 1);
        int offset = ((int)first.DayOfWeek + 6) % 7; // 周一为第一列
        DateTime start = first.AddDays(-offset);

        CalendarDays.Clear();
        for (int i = 0; i < 42; i++)
        {
            DateTime day = start.AddDays(i);
            CalendarDays.Add(new CalendarCell
            {
                Date = day,
                IsToday = day == today,
                IsCurrentMonth = day.Month == first.Month && day.Year == first.Year,
                IsSelected = day == _selectedDate
            });
        }

        CalendarTitle = $"{first:yyyy年M月}";
        OnPropertyChanged(nameof(CalendarSelectedText));
        _lastCalendarDate = today;
    }

    private void ShiftCalendarMonth(int delta)
    {
        _calendarMonth = _calendarMonth.AddMonths(delta);
        RefreshCalendar();
    }

    private void GoToCalendarToday()
    {
        _calendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _selectedDate = DateTime.Today;
        RefreshCalendar();
    }

    private void SelectDay(object? parameter)
    {
        if (parameter is not CalendarCell cell)
        {
            return;
        }

        _selectedDate = cell.Date.Date;

        // 点到上/下月的日期：顺手把日历翻到那个月
        if (_selectedDate.Month != _calendarMonth.Month || _selectedDate.Year != _calendarMonth.Year)
        {
            _calendarMonth = new DateTime(_selectedDate.Year, _selectedDate.Month, 1);
            RefreshCalendar();
            return;
        }

        foreach (CalendarCell item in CalendarDays)
        {
            item.IsSelected = item.Date == _selectedDate;
        }

        OnPropertyChanged(nameof(CalendarSelectedText));
    }

    // ---------------- 通知 ----------------

    private void OnNotificationReceived(object? sender, NotificationItem item)
    {
        if (!Settings.EnableNotifications)
        {
            return;
        }

        RunOnUi(() =>
        {
            var entry = new NotificationEntryViewModel(item);
            NotificationApp = entry.AppName;
            NotificationTitle = entry.DisplayTitle;
            NotificationBody = entry.Body;
            NotificationIcon = entry.Icon;
            NotificationInitial = entry.Initial;
            NotificationCount = _notifications.History.Count;

            Notifications.Insert(0, entry);
            while (Notifications.Count > 30)
            {
                Notifications.RemoveAt(Notifications.Count - 1);
            }

            NotificationArrived?.Invoke(this, item);
        });
    }

    /// <summary>清空通知列表（数据库本身只读，只清界面与内存历史）。</summary>
    public void ClearNotifications()
    {
        Notifications.Clear();
        _notifications.History.Clear();
        NotificationCount = 0;
    }

    /// <summary>从数据库同步历史通知（展开通知页时调用）。</summary>
    public void SyncNotificationHistory()
    {
        if (Notifications.Count > 0)
        {
            return;
        }

        foreach (var item in _notifications.GetExisting(20))
        {
            var entry = new NotificationEntryViewModel(item);
            Notifications.Add(entry);
        }

        NotificationCount = Notifications.Count;
    }

    // ---------------- 工具 ----------------

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private static ImageSource? ToImageSource(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatTime(TimeSpan value)
        => value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";

    private static string FormatRemain(TimeSpan value)
        => "-" + FormatTime(value < TimeSpan.Zero ? TimeSpan.Zero : value);

    private static string FormatRate(double mbps)
    {
        if (mbps >= 1)
        {
            return $"{mbps:F1}M/s";
        }

        return $"{mbps * 1000:F0}K/s";
    }

    /// <summary>把当前设置写回磁盘（主题切换等场景）。</summary>
    public void SaveSettings() => _settingsService.Save();

    public void Dispose()
    {
        _disposed = true;
        _fastTimer.Stop();
        _clockTimer.Stop();
        _launcherErrorTimer.Stop();
        _launcher.LaunchFailed -= OnLauncherLaunchFailed;
        _media.TrackChanged -= OnTrackChanged;
        _media.Updated -= OnMediaUpdated;
        _notifications.NotificationReceived -= OnNotificationReceived;
        _media.Dispose();
        _notifications.Dispose();
        _hardware.Dispose();
        _volume.Dispose();
        _privacy.Dispose();
        _systemEvents.Dispose();
        _bluetooth.Dispose();
        _focus.Dispose();
        _settingsService.Save();
    }
}
