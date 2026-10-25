using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Core.Models;
using DynamicIsland.Core.Services;
using DynamicIsland.Infrastructure;
using DynamicIsland.Interop;
using DynamicIsland.ViewModels;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    private const int WM_COPYDATA = 0x004A;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_DEVICECHANGE = 0x0219;
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int HotkeyToggleId = 0x4D41;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkD = 0x44;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Win32Rect rcMonitor;
        public Win32Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }
    // 固定窗口尺寸，必须 ≥ 展开态
    private const double WindowWidth = 600;
    private const double WindowHeight = 470;

    private const double CollapsedWidth = 204;
    private const double CollapsedHeight = 36;
    private const double CollapsedWeatherWidth = 248;

    // 媒体页的默认展开尺寸；其余页面按 TabSizes 单独给高度
    private const double ExpandedWidth = 380;
    private const double ExpandedHeight = 184;

    private const double ExpandedRadius = 26.0;

    /// <summary>
    /// 每个页面自己的面板尺寸。以前所有页面共用 380×184，还要被顶部媒体条占掉 62px，
    /// 日历只能挤在剩下的 ~100px 里，六行日期几乎叠在一起。
    /// </summary>
    private static readonly (double Width, double Height)[] TabSizes =
    {
        (ExpandedWidth, ExpandedHeight),  // 0 媒体
        (ExpandedWidth, 156),             // 1 天气
        (ExpandedWidth, 184),             // 2 硬件
        (ExpandedWidth, 156),             // 3 图标坞
        (ExpandedWidth, 220),             // 4 通知
        (ExpandedWidth, 196),             // 5 歌词
        (ExpandedWidth, 220),             // 6 剪贴板
        (452, 356)                        // 7 日历：左侧月历 + 右侧详情
    };

    /// <summary>音乐 / 歌词页才在顶部显示媒体条；其它功能页整块让给内容。</summary>
    private static bool TabShowsMedia(int tab) => tab is 0 or 5;

    private static (double Width, double Height) SizeForTab(int tab)
        => tab >= 0 && tab < TabSizes.Length ? TabSizes[tab] : TabSizes[0];

    private const double NotificationWidth = 420;
    private const double NotificationHeight = 92;

    private readonly IslandViewModel _viewModel = new();
    private readonly DispatcherTimer _notificationTimer;
    private readonly DispatcherTimer _autoCollapseTimer;
    private readonly DispatcherTimer _volumeHudTimer;
    private readonly DispatcherTimer _spectrumTimer;
    private readonly Border[] _spectrumBars;
    private readonly Random _random = new();
    private readonly IslandSpringEngine _spring = new();
    private TrayIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;

    private readonly DispatcherTimer _systemAlertTimer;
    private readonly DispatcherTimer _clipboardHudTimer;
    private readonly DispatcherTimer _fullscreenTimer;
    private IntPtr _hwnd;
    private bool _manualHidden;
    private bool _fullscreenHidden;
    private bool _fullscreenOverride;
    private bool _clipboardIgnoreNext;

    // 页面切换：一个连续的弹簧位置驱动 5 个页面和右侧分页点
    private readonly Stopwatch _tabClock = Stopwatch.StartNew();
    private readonly FrameworkElement[] _tabPages;
    private readonly Border[] _tabDots;
    private double _tabPosition;
    private double _tabVelocity;
    private double _tabTarget;
    private double _tabLastSeconds;
    private bool _tabAnimating;
    private double _tabStiffness = 215.0;
    private double _tabDamping = 11.0;

    private IslandState _state = IslandState.Collapsed;
    private IslandState _stateBeforeNotification = IslandState.Collapsed;
    private Rect _islandHitRect = Rect.Empty;
    private bool _hovered;

    public MainWindow()
    {
        ThemeManager.Apply(_viewModel.Settings.Theme);
        InitializeComponent();

        Width = WindowWidth;
        Height = WindowHeight;
        DataContext = _viewModel;

        _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.2) };
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer.Stop();
            SetState(_stateBeforeNotification);
        };

        _autoCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _autoCollapseTimer.Tick += (_, _) =>
        {
            _autoCollapseTimer.Stop();

            // 子元素捕获鼠标（拖进度条/音量条）时 WPF 可能补发 MouseLeave，
            // 只要鼠标实际还在岛上就不能收起。
            if (Island.IsMouseOver)
            {
                _hovered = true;
                return;
            }

            RecomputeState();
        };

        _volumeHudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _volumeHudTimer.Tick += (_, _) =>
        {
            _volumeHudTimer.Stop();
            HideVolumeHud();
        };

        _systemAlertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
        _systemAlertTimer.Tick += (_, _) =>
        {
            _systemAlertTimer.Stop();
            FadeOutHud(SystemAlertHud);
        };

        _clipboardHudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _clipboardHudTimer.Tick += (_, _) =>
        {
            _clipboardHudTimer.Stop();
            FadeOutHud(ClipboardHud);
        };

        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();

        _spring.Updated += OnSpringUpdated;
        ApplyMotionStyle();

        _tabPages = new FrameworkElement[]
        {
            MediaPage, WeatherPage, HardwarePage, LauncherPage,
            NotificationPage, LyricsPage, ClipboardPage, CalendarPage
        };
        _tabDots = new Border[]
        {
            TabDotIndicator0, TabDotIndicator1, TabDotIndicator2, TabDotIndicator3,
            TabDotIndicator4, TabDotIndicator5, TabDotIndicator6, TabDotIndicator7
        };
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyTabTransition();

        _spectrumBars = new[] { Spectrum0, Spectrum1, Spectrum2, Spectrum3, Spectrum4, Spectrum5 };
        _spectrumTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
        _spectrumTimer.Tick += (_, _) => UpdateSpectrum();
        _spectrumTimer.Start();

        _viewModel.NotificationArrived += OnNotificationArrived;
        _viewModel.NotificationArrived += (_, _) => Dispatcher.BeginInvoke(UpdateIslandHitRect);
        _viewModel.SystemAlertRequested += OnSystemAlertRequested;
        _viewModel.ClipboardCaptured += OnClipboardCaptured;
        _viewModel.ClipboardCopyRequested += OnClipboardCopyRequested;

        // 设置面板里改动画风格后即时生效
        _viewModel.SettingsApplied += ApplyMotionStyle;
    }

    public IslandViewModel ViewModel => _viewModel;

    /// <summary>
    /// 设置面板的动画风格：换算成刚度 / 阻尼乘数，同时作用于岛体尺寸弹簧与切页弹簧。
    /// smooth 更接近临界阻尼（不回弹），bouncy 更容易 overshoot，snappy 更快。
    /// 切页弹簧的基础值取接近临界（300/33，稳定时间约 240ms），
    /// 保证滚轮翻页是柔和的滑动而不是来回弹。
    /// </summary>
    private void ApplyMotionStyle()
    {
        string style = _viewModel.Settings.AnimationStyle;
        _spring.ApplyStyle(style);

        (_tabStiffness, _tabDamping) = style switch
        {
            "smooth" => (300.0, 45.5),
            "bouncy" => (330.0, 23.1),
            "snappy" => (450.0, 41.3),
            _ => (300.0, 33.0)
        };
    }

    /// <summary>
    /// 封面 morph：跟随岛体高度在「折叠 24 圆」与「展开 46 圆角方形」之间连续插值。
    /// 圆角固定 12，尺寸连续变大时形状自然从圆形过渡到圆角方形。
    /// </summary>
    private void UpdateMorphCover(double height)
    {
        if (MorphCover is null)
        {
            return;
        }

        // 音乐/歌词页才有封面位；展开状态下切到其它功能页要把封面一起收掉
        bool hideForPage = _state == IslandState.Expanded && !TabShowsMedia(_viewModel.ActiveTab);
        if (_state == IslandState.Notification || hideForPage
            || !_viewModel.HasMedia || _viewModel.Cover is null)
        {
            MorphCover.Visibility = Visibility.Collapsed;
            return;
        }

        double progress = Math.Clamp(
            (height - CollapsedHeight) / (ExpandedHeight - CollapsedHeight), 0, 1);

        MorphCover.Visibility = Visibility.Visible;
        MorphCover.Width = 24 + (46 - 24) * progress;
        MorphCover.Height = MorphCover.Width;
        MorphCover.Margin = new Thickness(
            7 + (12 - 7) * progress,     // 折叠封面左边距 7 → 展开封面左边距 12
            6 + (11 - 6) * progress,     // 垂直同样从 6 抬到 11
            0,
            0);
    }

    // ---------------- 生命周期 ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _hwnd = hwnd;

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        if (HwndSource.FromHwnd(hwnd) is { } source)
        {
            source.AddHook(WndProc);
        }
    }

    private void SetupTrayIcon(IntPtr hwnd)
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            _trayIcon = new TrayIcon(hwnd, iconPath, "灵动岛 · 贴顶常驻")
            {
                OnLeftClick = () => SetState(_state == IslandState.Expanded ? IslandState.Collapsed : IslandState.Expanded),
                OnToggleExpand = () => SetState(_state == IslandState.Expanded ? IslandState.Collapsed : IslandState.Expanded),
                OnQuit = QuitApplication,
                GetAutoStart = () => AutoStartService.IsEnabled,
                SetAutoStart = enabled =>
                {
                    if (enabled)
                    {
                        AutoStartService.Enable();
                    }
                    else
                    {
                        AutoStartService.Disable();
                    }
                },
                OnShowLog = () => LauncherService.RevealInExplorer(AppPaths.LogFile)
            };
        }
        catch (Exception ex)
        {
            LogService.Warn("托盘图标初始化失败：" + ex.Message);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionAtTopCenter();
        Island.SizeChanged += (_, _) => UpdateIslandHitRect();
        LayoutUpdated += (_, _) => UpdateIslandHitRect();

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateIslandHitRect));

        // 托盘图标必须在窗口真正显示之后再注册：Shell_NotifyIcon 对尚未显示的窗口会返回失败
        _spring.SetTarget(CollapsedWidth, CollapsedHeight, 18);
        SetupTrayIcon(new WindowInteropHelper(this).Handle);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        bool hotkey = RegisterHotKey(hwnd, HotkeyToggleId, ModControl | ModAlt, VkD);
        LogService.Info(hotkey ? "全局快捷键已注册：Ctrl+Alt+D" : "全局快捷键注册失败（可能被占用）");
        AddClipboardFormatListener(hwnd);
        _fullscreenTimer.Start();
        StartPrivacyPulse();

        _ = InitializeServicesAsync();
    }

    /// <summary>隐私点的呼吸感，避免静态小圆点看起来像渲染残留。</summary>
    private void StartPrivacyPulse()
    {
        var pulse = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        MicDot.BeginAnimation(OpacityProperty, pulse);
        CameraDot.BeginAnimation(OpacityProperty, pulse);
    }

    private async Task InitializeServicesAsync()
    {
        try
        {
            await _viewModel.InitializeAsync();
            ApplyInitialState();
        }
        catch (Exception ex)
        {
            Core.Services.LogService.Error("初始化失败", ex);
        }
    }

    private void ApplyInitialState()
    {
        SetState(IslandState.Collapsed);
    }

    private void PositionAtTopCenter()
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = Math.Round((screenWidth - WindowWidth) / 2.0);
        Top = 0;
    }

    // ---------------- 穿透 ----------------

    private const double HitPadX = 18;
    // 顶部补边要大一些：贴顶窗口的内容实际从物理 y≈2 开始渲染，
    // 留 8 逻辑像素余量保证「屏幕最顶上那一两像素」也在命中区内，鼠标放进去就能展开。
    private const double HitPadTop = 8;
    private const double HitPadBottom = 14;

    private void UpdateIslandHitRect() => UpdateIslandHitRectFor(Island.Width, Island.Height);

    /// <summary>
    /// 用岛体当前的 DIP 尺寸直接计算屏幕命中矩形（物理像素）。
    /// 不能用 Island.ActualWidth —— 弹簧每帧刚设置 Width 时布局还没跑，ActualWidth 仍是旧值，
    /// 会让命中区域滞留在折叠态大小，表现为"能看见但点不到、鼠标穿透过去"。
    /// </summary>
    private void UpdateIslandHitRectFor(double dipWidth, double dipHeight)
    {
        if (Island is null)
        {
            return;
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var windowRect))
        {
            return;
        }

        try
        {
            var source = PresentationSource.FromVisual(this);
            double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

            // 岛体在窗口内水平居中、贴着窗口顶部
            double offsetXdip = (ActualWidth - dipWidth) / 2.0;

            double left = windowRect.Left + (offsetXdip - HitPadX) * scale;
            double top = windowRect.Top + (-HitPadTop) * scale;
            double width = Math.Max(1, (dipWidth + HitPadX * 2) * scale);
            double height = Math.Max(1, (dipHeight + HitPadTop + HitPadBottom) * scale);

            var hit = new Rect(left, top, width, height);

            // 右键菜单会伸出岛体范围；菜单打开时必须把菜单矩形并进命中区，
            // 否则菜单下半部分会被 WM_NCHITTEST 判成透明，点了直接穿透到桌面。
            if (ContextMenuPanel is { Visibility: Visibility.Visible })
            {
                ContextMenuPanel.UpdateLayout();
                double menuLeft = Canvas.GetLeft(ContextMenuPanel);
                double menuTop = Canvas.GetTop(ContextMenuPanel);
                double menuWidth = ContextMenuPanel.ActualWidth > 0 ? ContextMenuPanel.ActualWidth : 176;
                double menuHeight = ContextMenuPanel.ActualHeight > 0 ? ContextMenuPanel.ActualHeight : 380;

                hit.Union(new Rect(
                    windowRect.Left + menuLeft * scale,
                    windowRect.Top + menuTop * scale,
                    menuWidth * scale,
                    menuHeight * scale));
            }

            if (LauncherMenuLayer is { Visibility: Visibility.Visible })
            {
                LauncherMenuPanel.UpdateLayout();
                double menuLeft = Canvas.GetLeft(LauncherMenuPanel);
                double menuTop = Canvas.GetTop(LauncherMenuPanel);
                double menuWidth = LauncherMenuPanel.ActualWidth > 0 ? LauncherMenuPanel.ActualWidth : 134;
                double menuHeight = LauncherMenuPanel.ActualHeight > 0 ? LauncherMenuPanel.ActualHeight : 76;

                hit.Union(new Rect(
                    windowRect.Left + menuLeft * scale,
                    windowRect.Top + menuTop * scale,
                    menuWidth * scale,
                    menuHeight * scale));
            }

            _islandHitRect = hit;
        }
        catch
        {
            // 窗口销毁中
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_trayIcon?.ProcessMessage(msg, lParam) == true)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_NCHITTEST)
        {
            long lp = lParam.ToInt64();
            int screenX = (short)(lp & 0xFFFF);
            int screenY = (short)((lp >> 16) & 0xFFFF);

            if (!_islandHitRect.IsEmpty &&
                !_islandHitRect.Contains(new Point(screenX, screenY)))
            {
                handled = true;
                return new IntPtr(NativeMethods.HTTRANSPARENT);
            }
        }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyToggleId)
        {
            ToggleVisibilityHotkey();
            handled = true;
            return IntPtr.Zero;
        }
        else if (msg == WM_DEVICECHANGE)
        {
            int change = wParam.ToInt32();
            if (change == DBT_DEVICEARRIVAL)
            {
                _viewModel.NotifyUsbDevice(true);
            }
            else if (change == DBT_DEVICEREMOVECOMPLETE)
            {
                _viewModel.NotifyUsbDevice(false);
            }
        }
        else if (msg == WM_CLIPBOARDUPDATE)
        {
            HandleClipboardUpdate();
        }
        else if (msg == WM_COPYDATA)
        {
            try
            {
                var data = Marshal.PtrToStructure<CopyDataStruct>(lParam);
                string command = Marshal.PtrToStringUni(data.lpData, data.cbData / 2)?.Trim() ?? string.Empty;
                HandleCommand(command);
                handled = true;
                return new IntPtr(1);
            }
            catch (Exception ex)
            {
                Core.Services.LogService.Warn("处理外部命令失败：" + ex.Message);
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 外部控制命令（WM_COPYDATA，供脚本 / 快捷键 / 自测使用）：
    /// expand | collapse | toggle | tab:N | notify:标题|正文 | quit
    /// </summary>
    private void HandleCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        Core.Services.LogService.Info("收到命令：" + command);

        string[] parts = command.Split('|');
        string head = parts[0].Trim().ToLowerInvariant();

        switch (head)
        {
            case "expand":
                SetState(IslandState.Expanded);
                break;
            case "collapse":
                SetState(IslandState.Collapsed);
                break;
            case "toggle":
                SetState(_state == IslandState.Expanded ? IslandState.Collapsed : IslandState.Expanded);
                break;
            case "clearlauncher":
                _viewModel.ClearLaunchers();
                break;
            case "pin":
                // 已改为纯悬停模式，固定功能移除
                break;
            case "quit":
                QuitApplication();
                break;
            default:
                if (head.StartsWith("launcher:", StringComparison.Ordinal))
                {
                    // 路径可能含大小写，必须从原始命令取
                    string path = command["launcher:".Length..].Trim();
                    if (_viewModel.AddLauncherPaths(new[] { path }) > 0)
                    {
                        _viewModel.ActiveTab = 3;
                        SetState(IslandState.Expanded);
                    }
                }
                else if (head.StartsWith("tab:", StringComparison.Ordinal) &&
                    int.TryParse(head[4..], out int index))
                {
                    _viewModel.ActiveTab = Math.Clamp(index, 0, 7);
                    if (_viewModel.ActiveTab == 4)
                    {
                        _viewModel.SyncNotificationHistory();
                    }

                    SetState(IslandState.Expanded);
                }
                else if (head.StartsWith("notify:", StringComparison.Ordinal))
                {
                    // 格式：notify:标题|正文
                    string title = command["notify:".Length..].Split('|')[0].Trim();
                    string body = parts.Length > 1 ? parts[1].Trim() : "这是一条来自灵动岛的测试消息";
                    ShowTestNotification("测试应用", title, body);
                }
                else if (head.StartsWith("volume:", StringComparison.Ordinal) &&
                         float.TryParse(head[7..], out float value))
                {
                    _viewModel.SetVolumeAbsolute(value);
                }

                break;
        }
    }

    private void ShowTestNotification(string app, string title, string body)
    {
        // 与真实通知保持同一套打断规则：正在拖动 / 菜单打开 / 拖图标时不弹卡
        if (_viewModel.IsSeeking
            || ContextMenuPanel.Visibility == Visibility.Visible
            || LauncherMenuLayer.Visibility == Visibility.Visible
            || _launcherDragging)
        {
            return;
        }

        // 先记下当前状态再推通知：PushTestNotification 会同步触发 NotificationArrived，
        // 那边已经把状态切成 Notification，之后再读 _state 会把"通知态"当成恢复目标，
        // 通知卡就永远收不回去了。
        _stateBeforeNotification = _state;
        _viewModel.PushTestNotification(app, title, body);
        SetState(IslandState.Notification);
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    // ---------------- 状态机 ----------------

    private void SetState(IslandState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        _viewModel.State = state;

        (double width, double height, double radius) = state switch
        {
            IslandState.Expanded => (SizeForTab(_viewModel.ActiveTab).Width,
                                     SizeForTab(_viewModel.ActiveTab).Height,
                                     ExpandedRadius),
            IslandState.Notification => (NotificationWidth, NotificationHeight, 20.0),
            _ => (CollapsedWidthFor(_viewModel.HasMedia), CollapsedHeight, 18.0)
        };

        UpdateContent(state);
        _spring.SetTarget(width, height, radius);
        UpdateIslandHitRect();

        if (state != IslandState.Collapsed)
        {
            StopMarquee();
        }
        else
        {
            // 岛收起时不能再留着一个悬在桌面上的菜单
            HideLauncherMenu();
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateMarquee));
    }

    /// <summary>折叠态宽度：有媒体时按标题，无媒体时要放下时钟/日期/天气。</summary>
    private static double CollapsedWidthFor(bool hasMedia) => hasMedia ? CollapsedWidth : CollapsedWeatherWidth;

    /// <summary>按「悬停 / 固定 / 菜单 / 通知」重新计算应该处于哪种形态。</summary>
    private void RecomputeState()
    {
        if (_notificationTimer.IsEnabled)
        {
            SetState(IslandState.Notification);
            return;
        }

        // 纯悬停模式：不再有「固定展开」，只要鼠标不在岛上（且菜单没开）就收起
        bool keepExpanded = _hovered
            || ContextMenuPanel.Visibility == Visibility.Visible
            || LauncherMenuLayer.Visibility == Visibility.Visible;
        SetState(keepExpanded ? IslandState.Expanded : IslandState.Collapsed);
    }

    /// <summary>
    /// 切换内容层。折叠/展开两块常驻，按岛体高度交叉淡入；通知态独占一层。
    /// </summary>
    private void UpdateContent(IslandState state)
    {
        if (state == IslandState.Notification)
        {
            CollapsedContent.Visibility = Visibility.Collapsed;
            ExpandedContent.Visibility = Visibility.Collapsed;
            NotificationContent.Visibility = Visibility.Visible;
            AnimateNotificationCard();
            return;
        }

        NotificationContent.Visibility = Visibility.Collapsed;
        UpdateExpandedContentLayout();
        ApplyContentFade(_spring.Height);
    }

    /// <summary>按当前页设置媒体条和展开内容的布局。</summary>
    private void UpdateExpandedContentLayout()
    {
        if (MediaHeader is null)
        {
            return;
        }

        MediaHeader.Visibility = TabShowsMedia(_viewModel.ActiveTab)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 图标坞按最终展开尺寸排版，外层岛体只负责裁剪。
        // 快速开关时不能让图标跟着每一帧的岛宽重新挤压、换行。
        bool launcher = _viewModel.ActiveTab == 3;
        (double width, double height) = SizeForTab(_viewModel.ActiveTab);
        ExpandedContent.Width = launcher
            ? width - Island.BorderThickness.Left - Island.BorderThickness.Right
            : double.NaN;
        ExpandedContent.Height = launcher
            ? height - Island.BorderThickness.Top - Island.BorderThickness.Bottom
            : double.NaN;
        ExpandedContent.HorizontalAlignment = launcher ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        ExpandedContent.VerticalAlignment = launcher ? VerticalAlignment.Top : VerticalAlignment.Stretch;
    }

    /// <summary>通知卡入场：从上方略小开始落位，避免硬切。</summary>
    private void AnimateNotificationCard()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(260);

        NotificationContent.Opacity = 1;
        NotificationScale.ScaleX = 0.92;
        NotificationScale.ScaleY = 0.92;
        NotificationShift.Y = -8;

        NotificationScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, duration) { EasingFunction = ease });
        NotificationScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, duration) { EasingFunction = ease });
        NotificationShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, duration) { EasingFunction = ease });
    }

    /// <summary>按高度交叉淡入折叠/展开内容，避免变形时硬切。</summary>
    private void ApplyContentFade(double height)
    {
        if (_state == IslandState.Notification)
        {
            return;
        }

        double collapsed = Math.Clamp((84 - height) / 34, 0, 1);
        double expanded = Math.Clamp((height - 62) / 74, 0, 1);

        if (_viewModel.ActiveTab == 3)
        {
            // 图标在面板接近成形后再淡入。宽、高都要满足进度要求，
            // 避免沿用固定高度阈值时，岛还很窄图标却已经完全显示。
            // 使用当前尺寸而非重播定时动画，中途反向时透明度保持连续。
            (double targetWidth, double targetHeight) = SizeForTab(3);
            double collapsedWidth = CollapsedWidthFor(_viewModel.HasMedia);
            double widthProgress = (_spring.Width - collapsedWidth) / (targetWidth - collapsedWidth);
            double heightProgress = (height - CollapsedHeight) / (targetHeight - CollapsedHeight);
            expanded = Math.Clamp((Math.Min(widthProgress, heightProgress) - 0.8) / 0.2, 0, 1);
        }

        // smoothstep：线性淡入在两端会有灰影
        collapsed = collapsed * collapsed * (3 - 2 * collapsed);
        expanded = expanded * expanded * (3 - 2 * expanded);

        CollapsedContent.Opacity = collapsed;
        CollapsedContent.Visibility = collapsed > 0.002 ? Visibility.Visible : Visibility.Collapsed;
        CollapsedContent.IsHitTestVisible = collapsed > 0.35;

        ExpandedContent.Opacity = expanded;
        ExpandedContent.Visibility = expanded > 0.002 ? Visibility.Visible : Visibility.Collapsed;
        ExpandedContent.IsHitTestVisible = expanded > 0.6;
        ExpandedShift.Y = (1 - expanded) * 7;
    }

    /// <summary>弹簧每帧回调：直接驱动岛体尺寸、圆角与轻弹位移。</summary>
    private void OnSpringUpdated(double width, double height, double radius)
    {
        if (Island is null)
        {
            return;
        }

        Island.Width = Math.Max(0, width);
        Island.Height = Math.Max(0, height);
        Island.CornerRadius = new CornerRadius(Math.Max(0, radius));
        IslandOffset.Y = _spring.NudgeOffset;

        if (VolumeHud is not null)
        {
            PositionHuds();
        }

        ApplyContentFade(height);
        UpdateMorphCover(height);

        // 折叠宽度是在弹簧里连续变化的：必须等尺寸稳定后再量一次标题可用宽度，
        // 否则会拿到"上一形态"的宽度，跑马灯永远算不出溢出。
        if (_spring.IsSettled && _state == IslandState.Collapsed)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateMarquee));
        }

        // 每帧同步命中区，保证「看得见的部分」就是「点得到的部分」
        UpdateIslandHitRectFor(width, height);
    }

    private void OnNotificationArrived(object? sender, NotificationItem item)
    {
        Dispatcher.Invoke(() =>
        {
            // 以前是「只要岛处于展开态就不弹卡」，结果用户悬停在岛上等消息时，
            // 通知只会进历史、界面上毫无反应（测试者最容易正好是这个姿势）。
            // 现在只在真正操作岛的时候才不打断：拖动进度/音量、菜单打开、拖图标。
            bool interacting = _viewModel.IsSeeking
                || ContextMenuPanel.Visibility == Visibility.Visible
                || LauncherMenuLayer.Visibility == Visibility.Visible
                || _launcherDragging;

            if (interacting)
            {
                return;
            }

            if (!_viewModel.Settings.ShowNotificationCard)
            {
                return;
            }

            if (_state != IslandState.Notification)
            {
                _stateBeforeNotification = _state;
            }

            SetState(IslandState.Notification);
            _notificationTimer.Stop();
            _notificationTimer.Start();
        });
    }

    // ---------------- 交互 ----------------

    private void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        e.Handled = true;
        HideContextMenu();
        HideLauncherMenu();

        if (_state == IslandState.Notification)
        {
            _notificationTimer.Stop();
            SetState(IslandState.Expanded);
            return;
        }

        // 纯悬停模式：点击只用来关闭菜单 / 结束通知预览，不再改变展开状态
        RecomputeState();
    }

    private void Island_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowContextMenu();
    }

    private void ShowContextMenu()
    {
        Point mouse = Mouse.GetPosition(RootGrid);

        MenuAutoStartText.Text = AutoStartService.IsEnabled ? "开机自启 ✓" : "开机自启";
        UpdateThemeMenu();

        ContextMenuPanel.Visibility = Visibility.Visible;

        double left = Math.Clamp(mouse.X - 88, 8, Math.Max(8, ActualWidth - 196));
        double top = Math.Clamp(mouse.Y + 14, 8, Math.Max(8, ActualHeight - 374));
        Canvas.SetLeft(ContextMenuPanel, left);
        Canvas.SetTop(ContextMenuPanel, top);

        UpdateLayout();
        UpdateIslandHitRect();
        _autoCollapseTimer.Stop();
    }

    private void HideContextMenu()
    {
        ContextMenuPanel.Visibility = Visibility.Collapsed;
        UpdateIslandHitRect();

        // 菜单关掉后重新评估：鼠标已经不在岛上就收起，还在岛上就保持展开，
        // 避免"关掉菜单后只剩一片黑"或"菜单导致被动固定展开"。
        RecomputeState();
    }

    private void MenuAutoStart_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (AutoStartService.IsEnabled)
        {
            AutoStartService.Disable();
        }
        else
        {
            AutoStartService.Enable();
        }

        HideContextMenu();
    }

    private void MenuTheme_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        ThemeManager.Apply(id);
        _viewModel.Settings.Theme = ThemeManager.CurrentId;
        _viewModel.RefreshAccent();
        _viewModel.SaveSettings();
        UpdateThemeMenu();
        LogService.Info($"主题切换：{ThemeManager.Current.Label}");
    }

    private void UpdateThemeMenu()
    {
        if (MenuThemeObsidianText is null)
        {
            return;
        }

        MenuThemeObsidianText.Text = ThemeManager.CurrentId == "obsidian" ? "曜石  ✓" : "曜石";
        MenuThemeGraphiteText.Text = ThemeManager.CurrentId == "graphite" ? "石墨  ✓" : "石墨";
        MenuThemePorcelainText.Text = ThemeManager.CurrentId == "porcelain" ? "瓷白  ✓" : "瓷白";
    }

    private void MenuLog_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        LauncherService.RevealInExplorer(AppPaths.LogFile);
        HideContextMenu();
    }

    private void MenuSettings_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenSettings();
    }

    private void OpenSettings()
    {
        HideContextMenu();

        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        try
        {
            // 岛是 Topmost 工具窗，设置窗口必须挂在它下面（Owner），否则会被岛盖住顶部。
            _settingsWindow = new SettingsWindow(_viewModel) { Owner = this };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            LogService.Warn("打开设置窗口失败：" + ex.Message);
        }
    }

    private void MenuRefreshWeather_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.RefreshWeatherCommand.Execute(null);
        HideContextMenu();
    }

    private void MenuClearLaunchers_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.ClearLaunchers();
        HideContextMenu();
    }

    private void MenuOpenDataDir_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            AppPaths.EnsureCreated();
            Process.Start("explorer.exe", AppPaths.Root);
        }
        catch (Exception ex)
        {
            LogService.Warn("打开数据目录失败：" + ex.Message);
        }

        HideContextMenu();
    }

    private void MenuQuit_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        QuitApplication();
    }

    // ---------------- 日历 ----------------

    /// <summary>胶囊上的音乐状态图标：直接切回音乐面板。</summary>
    private void PillMusic_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.ActiveTab = 0;
        SetState(IslandState.Expanded);
    }

    /// <summary>胶囊上的日历入口：切到日历页并展开（不经过悬停判定）。</summary>
    private void PillCalendar_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.ActiveTab = 7;
        SetState(IslandState.Expanded);
    }

    /// <summary>右键菜单里的日历入口：直接切到日历页并保持展开。</summary>
    private void MenuCalendar_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.ActiveTab = 7;
        HideContextMenu();
        SetState(IslandState.Expanded);
    }

    /// <summary>月份切换的轻微淡入 + 4px 归位，避免日期网格硬切。</summary>
    private void AnimateCalendarGrid()
    {
        if (CalendarGrid is null)
        {
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(170);

        CalendarGrid.BeginAnimation(OpacityProperty, null);
        CalendarGrid.Opacity = 0.25;
        CalendarGrid.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, duration) { EasingFunction = ease });

        var shift = CalendarGrid.RenderTransform as TranslateTransform ?? new TranslateTransform();
        CalendarGrid.RenderTransform = shift;
        shift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(4, 0, duration) { EasingFunction = ease });
    }

    private void CalendarPrev_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.PreviousMonthCommand.Execute(null);
        AnimateCalendarGrid();
    }

    private void CalendarNext_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.NextMonthCommand.Execute(null);
        AnimateCalendarGrid();
    }

    private void CalendarToday_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.CalendarTodayCommand.Execute(null);
        AnimateCalendarGrid();
    }

    private void CalendarDay_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: CalendarCell cell })
        {
            _viewModel.SelectDayCommand.Execute(cell);
        }
    }

    /// <summary>
    /// 退出应用。App 配的是 ShutdownMode.OnExplicitShutdown，
    /// 只 Close() 窗口会留下一个看不见的进程——单实例互斥还会挡住下次启动，
    /// 用户只能去任务管理器结束。先关窗口走清理，再显式结束消息循环。
    /// </summary>
    private void QuitApplication()
    {
        Close();
        Application.Current?.Shutdown();
    }

    private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        HandleWheel(e.Delta);
    }

    /// <summary>
    /// 图标坞 / 通知页内部有 ScrollViewer，它会先吞掉滚轮（Handled=true），
    /// 导致这两页滚不动切页。这里在预览阶段统一截获，优先切页。
    /// </summary>
    private void ContentScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        HandleWheel(e.Delta);
    }

    private void HandleWheel(int delta)
    {
        // 展开态滚轮切页（与右侧分页点同一套 6 个页面），折叠态仍调音量。
        if (_state == IslandState.Expanded)
        {
            int step = delta > 0 ? -1 : 1;
            int next = Math.Clamp(_viewModel.ActiveTab + step, 0, 7);
            if (next != _viewModel.ActiveTab)
            {
                _viewModel.ActiveTab = next;
                if (next == 4)
                {
                    _viewModel.SyncNotificationHistory();
                }
            }

            return;
        }

        float volumeStep = (float)Math.Clamp(_viewModel.Settings.VolumeStep, 0.01, 0.2);
        _viewModel.ChangeVolumeBy(delta > 0 ? volumeStep : -volumeStep);
        ShowVolumeHud();
    }

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _autoCollapseTimer.Stop();
        _hovered = true;

        if (_state != IslandState.Collapsed)
        {
            return;
        }

        // 立即展开（不做悬停延迟）
        ExpandFromCollapsed();
    }

    /// <summary>
    /// 窗口级兜底：命中区比岛体本身大（左右 18、顶部 8、底部 14 逻辑像素），
    /// 鼠标停在「屏幕最顶那一两像素」时可能还没进入 Island 元素，
    /// 这里让窗口范围内的进入事件也触发展开，避免贴顶缝隙处"鼠标进去了但岛不动"。
    /// </summary>
    private void RootGrid_MouseEnter(object sender, MouseEventArgs e)
    {
        _autoCollapseTimer.Stop();
        _hovered = true;

        if (_state == IslandState.Collapsed)
        {
            ExpandFromCollapsed();
        }
    }

    /// <summary>延迟展开：鼠标已进入且仍处于折叠态时才执行。</summary>
    private void ExpandFromCollapsed()
    {
        if (!_hovered || _state != IslandState.Collapsed)
        {
            return;
        }

        // 岛正在收起的过程中又移回来：直接反向目标、保留当前速度。
        // 这种情况下绝不能再 Kick，否则速度会被强行改成正向，产生顿挫/过冲。
        // 判定要用「当前页面」的展开高度：日历页是 356，媒体页是 184。
        bool shrinking = _spring.Height < SizeForTab(_viewModel.ActiveTab).Height - 1
                         || _spring.HeightVelocity < -5;

        SetState(IslandState.Expanded);

        if (shrinking)
        {
            _spring.NudgeY(1.2);
            return;
        }

        // 已经稳稳收在胶囊状态：给一点"被顶开"的冲量和下沉回弹
        _spring.Kick(70);
        _spring.NudgeY(2.5);
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _hovered = false;

        if (_notificationTimer.IsEnabled ||
            ContextMenuPanel.Visibility == Visibility.Visible ||
            LauncherMenuLayer.Visibility == Visibility.Visible)
        {
            return;
        }

        // 拖放 / 图标拖拽 / 鼠标其实还在岛体（子元素捕获）时保持展开
        if (DropHint.Visibility == Visibility.Visible || _launcherDragging || Island.IsMouseOver)
        {
            return;
        }

        // 立即收起：不再等 280ms。弹簧本身保留了速度，
        // 所以在收起途中把鼠标放回来会从当前尺寸/速度自然反向。
        RecomputeState();
    }

    private void PlayButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.TogglePlayCommand.Execute(null);
    }

    private void NextButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.NextCommand.Execute(null);
    }

    private void PrevButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.PreviousCommand.Execute(null);
    }

    private void MuteButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.ToggleMuteCommand.Execute(null);
        ShowVolumeHud();
    }

    private bool _volumeDragging;
    private bool _volumeDragMoved;
    private double _volumeDragStartX;

    private void VolumeTrack_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _volumeDragging = true;
        _volumeDragMoved = false;
        _volumeDragStartX = e.GetPosition(VolumeTrack).X;
        VolumeTrack.CaptureMouse();
    }

    private void VolumeTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_volumeDragging)
        {
            return;
        }

        double x = e.GetPosition(VolumeTrack).X;
        if (!_volumeDragMoved && Math.Abs(x - _volumeDragStartX) > 3)
        {
            _volumeDragMoved = true;
        }

        if (!_volumeDragMoved)
        {
            return;
        }

        e.Handled = true;
        double width = Math.Max(1, VolumeTrack.ActualWidth);
        _viewModel.SetVolumeAbsolute((float)Math.Clamp(x / width, 0, 1));
        ShowVolumeHud();
    }

    private void VolumeTrack_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_volumeDragging)
        {
            return;
        }

        _volumeDragging = false;
        VolumeTrack.ReleaseMouseCapture();

        if (!_volumeDragMoved)
        {
            // 单击（没有拖动）切换静音
            e.Handled = true;
            _viewModel.ToggleMuteCommand.Execute(null);
            ShowVolumeHud();
        }
    }

    // ---------------- 音量浮层 ----------------

    /// <summary>折叠态滚轮调音量时，用一层轻量 HUD 显示当前音量。</summary>
    private void ShowVolumeHud()
    {
        if (VolumeHud is null)
        {
            return;
        }

        VolumeHud.Margin = new Thickness(0, _spring.Height + 10, 0, 0);
        VolumeHud.Visibility = Visibility.Visible;
        VolumeHud.BeginAnimation(OpacityProperty, null);
        VolumeHud.Opacity = 1;
        _volumeHudTimer.Stop();
        _volumeHudTimer.Start();
    }

    private void HideVolumeHud()
    {
        if (VolumeHud is null || VolumeHud.Visibility != Visibility.Visible)
        {
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(280));
        fade.Completed += (_, _) =>
        {
            if (VolumeHud.Opacity < 0.05)
            {
                VolumeHud.Visibility = Visibility.Collapsed;
            }
        };
        VolumeHud.BeginAnimation(OpacityProperty, fade);
    }

    // ---------------- 折叠态跑马灯 ----------------

    /// <summary>歌名超长时循环滚动；短标题、展开态和非折叠态保持静止。</summary>
    private void UpdateMarquee()
    {
        if (TitleClip is null || CollapsedTitleText is null || TitleShift is null)
        {
            return;
        }

        StopMarquee();

        if (!_viewModel.Settings.MarqueeTitle ||
            _state != IslandState.Collapsed ||
            string.IsNullOrWhiteSpace(_viewModel.CollapsedText))
        {
            return;
        }

        CollapsedTitleText.Width = double.NaN;
        CollapsedTitleText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double natural = CollapsedTitleText.DesiredSize.Width;
        double viewport = TitleClip.ActualWidth;

        if (viewport <= 1 || natural <= viewport + 1)
        {
            return;
        }

        CollapsedTitleText.Width = natural;
        double overflow = natural - viewport + 4;
        double scroll = Math.Clamp(overflow / 42.0, 1.8, 5.5);

        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(scroll + 3.2),
            RepeatBehavior = RepeatBehavior.Forever
        };

        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.9))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.9 + scroll))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4 + scroll))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.9 + scroll))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.0 + scroll))));

        TitleShift.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void StopMarquee()
    {
        if (TitleShift is null || CollapsedTitleText is null)
        {
            return;
        }

        TitleShift.BeginAnimation(TranslateTransform.XProperty, null);
        TitleShift.X = 0;
        CollapsedTitleText.Width = double.NaN;
    }

    /// <summary>换歌时标题轻微淡入，避免文字硬切。</summary>
    private void AnimateTrackTitle()
    {
        var fade = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(320));
        CollapsedTitleText?.BeginAnimation(OpacityProperty, fade);
        ExpandedTitleText?.BeginAnimation(OpacityProperty, fade);
    }

    private void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int index))
        {
            _viewModel.ActiveTab = index;

            if (index == 4)
            {
                _viewModel.SyncNotificationHistory();
            }

            if (_state != IslandState.Expanded)
            {
                SetState(IslandState.Expanded);
            }
        }
    }

    // ---------------- 页面切换弹簧动画 ----------------

    private void NotificationCard_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _notificationTimer.Stop();
        _viewModel.SyncNotificationHistory();
        _viewModel.ActiveTab = 4;
        SetState(IslandState.Expanded);
    }

    private void Notification_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: NotificationEntryViewModel entry })
        {
            entry.OpenCommand.Execute(null);
        }
    }

    private void ClearNotifications_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.ClearNotifications();
    }

    private void Clipboard_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: ClipboardEntryViewModel entry })
        {
            _viewModel.CopyClipboardCommand.Execute(entry);
        }
    }

    private void ClearClipboard_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.ClearClipboardCommand.Execute(null);
    }

    private void FocusStart_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int minutes))
        {
            _viewModel.StartFocusCommand.Execute(minutes);
            HideContextMenu();
        }
    }

    private void FocusStop_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.StopFocusCommand.Execute(null);
        HideContextMenu();
    }

    // ---------------- 系统浮层 / 剪贴板 / 全屏隐藏 ----------------

    private void OnSystemAlertRequested(SystemAlert alert)
        => Dispatcher.BeginInvoke(new Action(() => ShowSystemAlert(alert)));

    private void ShowSystemAlert(SystemAlert alert)
    {
        AlertIcon.Text = alert.Glyph;
        AlertTitle.Text = alert.Title;
        AlertDetail.Text = alert.Detail;

        PositionHuds();
        HideClipboardHud();
        HideVolumeHud();

        SystemAlertHud.Visibility = Visibility.Visible;
        SystemAlertHud.BeginAnimation(OpacityProperty, null);
        SystemAlertHud.Opacity = 1;
        AlertShift.Y = -7;
        AlertShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        _systemAlertTimer.Stop();
        _systemAlertTimer.Start();
        _spring.Kick(26);
    }

    private void HideSystemAlert()
    {
        _systemAlertTimer.Stop();
        SystemAlertHud.BeginAnimation(OpacityProperty, null);
        SystemAlertHud.Opacity = 0;
        SystemAlertHud.Visibility = Visibility.Collapsed;
    }

    private void FadeOutHud(FrameworkElement hud)
    {
        if (hud.Visibility != Visibility.Visible)
        {
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) =>
        {
            if (hud.Opacity < 0.05)
            {
                hud.Visibility = Visibility.Collapsed;
            }
        };
        hud.BeginAnimation(OpacityProperty, fade);
    }

    private void OnClipboardCaptured()
        => Dispatcher.BeginInvoke(new Action(ShowClipboardHud));

    private void ShowClipboardHud()
    {
        ClipboardEntryViewModel? latest = _viewModel.ClipboardItems.FirstOrDefault();
        if (latest is null)
        {
            return;
        }

        ClipIcon.Visibility = latest.IsText ? Visibility.Visible : Visibility.Collapsed;
        ClipThumb.Visibility = latest.IsImage ? Visibility.Visible : Visibility.Collapsed;
        ClipThumb.Source = latest.Thumbnail;
        ClipPreview.Text = latest.IsText ? latest.Preview : "图片已复制到剪贴板";
        ClipMeta.Text = latest.IsText ? $"文本 · {latest.Meta}" : $"图片 · {latest.Meta}";

        PositionHuds();
        HideSystemAlert();
        HideVolumeHud();

        ClipboardHud.Visibility = Visibility.Visible;
        ClipboardHud.BeginAnimation(OpacityProperty, null);
        ClipboardHud.Opacity = 1;
        ClipShift.Y = -7;
        ClipShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        _clipboardHudTimer.Stop();
        _clipboardHudTimer.Start();
        _spring.NudgeY(1.5);
    }

    private void HideClipboardHud()
    {
        _clipboardHudTimer.Stop();
        ClipboardHud.BeginAnimation(OpacityProperty, null);
        ClipboardHud.Opacity = 0;
        ClipboardHud.Visibility = Visibility.Collapsed;
    }

    private void OnClipboardCopyRequested(ClipboardEntry entry)
    {
        try
        {
            _clipboardIgnoreNext = true;

            if (entry.Kind == ClipboardKind.Text)
            {
                Clipboard.SetText(entry.Text);
            }
            else if (entry.ImagePng is { Length: > 0 } png)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(png);
                image.EndInit();
                image.Freeze();
                Clipboard.SetImage(image);
            }

            LogService.Info("已复制到剪贴板：" + (entry.Kind == ClipboardKind.Text ? "文本" : "图片"));
        }
        catch (Exception ex)
        {
            LogService.Warn("写回剪贴板失败：" + ex.Message);
        }
    }

    private void HandleClipboardUpdate()
    {
        if (!_viewModel.Settings.EnableClipboard)
        {
            return;
        }

        if (_clipboardIgnoreNext)
        {
            _clipboardIgnoreNext = false;
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ReadClipboard));
    }

    private void ReadClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                BitmapSource? image = Clipboard.GetImage();
                if (image is not null)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var stream = new MemoryStream();
                    encoder.Save(stream);
                    _viewModel.AddClipboardImage(stream.ToArray());
                }

                return;
            }

            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _viewModel.AddClipboardText(text);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("读取剪贴板失败：" + ex.Message);
        }
    }

    private void PositionHuds()
    {
        double top = _spring.Height + 10;
        if (VolumeHud is not null)
        {
            VolumeHud.Margin = new Thickness(0, top, 0, 0);
        }

        if (SystemAlertHud is not null)
        {
            SystemAlertHud.Margin = new Thickness(0, top, 0, 0);
        }

        if (ClipboardHud is not null)
        {
            ClipboardHud.Margin = new Thickness(0, top, 0, 0);
        }
    }

    // ---------------- 全屏自动隐藏 / 全局快捷键 ----------------

    private void CheckFullscreen()
    {
        if (!_viewModel.Settings.EnableFullscreenHide)
        {
            if (_fullscreenHidden)
            {
                _fullscreenHidden = false;
                Show();
            }

            return;
        }

        bool fullscreen = IsForegroundFullscreen();

        if (fullscreen)
        {
            if (!_manualHidden && !_fullscreenOverride && !_fullscreenHidden)
            {
                _fullscreenHidden = true;
                Hide();
                LogService.Info("检测到全屏应用，已自动隐藏灵动岛");
            }
        }
        else
        {
            _fullscreenOverride = false;
            if (_fullscreenHidden)
            {
                _fullscreenHidden = false;
                Show();
                LogService.Info("全屏结束，灵动岛已恢复");
            }
        }
    }

    private bool IsForegroundFullscreen()
    {
        try
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == _hwnd)
            {
                return false;
            }

            var className = new StringBuilder(64);
            GetClassNameW(foreground, className, className.Capacity);
            string cls = className.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                return false;
            }

            if (!GetWindowRect(foreground, out Win32Rect rect))
            {
                return false;
            }

            // 普通最大化窗口也覆盖屏幕，但它不是全屏应用；真正的全屏（F11 / 游戏）
            // 没有 WS_MAXIMIZE 样式。这个判断避免把最大化浏览器当成全屏视频。
            const int GwlStyle = -16;
            const long WsMaximize = 0x01000000L;
            long style = NativeMethods.GetWindowLongPtr(foreground, GwlStyle).ToInt64();
            if ((style & WsMaximize) != 0)
            {
                return false;
            }

            IntPtr monitor = MonitorFromWindow(foreground, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            Win32Rect screen = info.rcMonitor;
            return rect.Left <= screen.Left && rect.Top <= screen.Top &&
                   rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
        }
        catch
        {
            return false;
        }
    }

    private void ToggleVisibilityHotkey()
    {
        if (Visibility != Visibility.Visible || _manualHidden || _fullscreenHidden)
        {
            _manualHidden = false;
            _fullscreenHidden = false;
            _fullscreenOverride = true;
            Show();
            UpdateIslandHitRect();
            LogService.Info("快捷键：显示灵动岛");
        }
        else
        {
            _manualHidden = true;
            Hide();
            LogService.Info("快捷键：隐藏灵动岛");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IslandViewModel.ActiveTab))
        {
            // 切页同时换面板尺寸：日历要 384 高，通知要 236，音乐只要 184
            UpdateExpandedContentLayout();
            if (_state == IslandState.Expanded)
            {
                (double width, double height) = SizeForTab(_viewModel.ActiveTab);
                _spring.SetTarget(width, height, ExpandedRadius);
                UpdateIslandHitRect();
            }

            StartTabTransition(_viewModel.ActiveTab);
            return;
        }

        if (e.PropertyName == nameof(IslandViewModel.Title))
        {
            AnimateTrackTitle();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateMarquee));
            return;
        }

        if (e.PropertyName == nameof(IslandViewModel.CollapsedText))
        {
            // 歌词换行 / 设置变化：只重排跑马灯。
            // 这里绝不能再播标题淡入，否则每次歌词刷新都会闪一下。
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateMarquee));
            return;
        }

        if (e.PropertyName == nameof(IslandViewModel.HasMedia))
        {
            if (_state == IslandState.Collapsed)
            {
                _spring.SetTarget(CollapsedWidthFor(_viewModel.HasMedia), CollapsedHeight, 18);
            }

            // 媒体出现/消失时封面 morph 层要跟着显示或隐藏
            UpdateMorphCover(_spring.Height);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateMarquee));
        }
    }

    private void StartTabTransition(int index)
    {
        // 淡入动画会覆盖 Opacity 的本地值。切页前移除旧时钟，
        // 让弹簧重新接管所有页面，避免快速切换时旧页仍被动画显示出来。
        foreach (FrameworkElement page in _tabPages)
        {
            page.BeginAnimation(UIElement.OpacityProperty, null);
        }

        double target = Math.Clamp(index, 0, _tabPages.Length - 1);
        _tabTarget = target;
        ApplyTabTransition();

        // 跨多页（例如日历 7 → 媒体 0）：如果还走"连续位置弹簧"，中间会依次闪过
        // 天气、硬件、图标坞……实测肉眼可见。这种跳转直接落位，只保留尺寸弹簧。
        if (Math.Abs(_tabPosition - target) > 1.001)
        {
            if (_tabAnimating)
            {
                CompositionTarget.Rendering -= OnTabRendering;
                _tabAnimating = false;
            }

            _tabPosition = target;
            _tabVelocity = 0;
            ApplyTabTransition();

            // 直接落位没有"滑入"，补一次短淡入，避免内容硬切
            FrameworkElement page = _tabPages[(int)target];
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                // 结束后恢复 ApplyTabTransition 写入的基础值；不使用 Completed
                // 回调强制设为 1，否则过期回调会把已经离开的页面重新显示。
                FillBehavior = FillBehavior.Stop
            };
            page.BeginAnimation(UIElement.OpacityProperty, fade);
            return;
        }

        if (_tabAnimating)
        {
            return;
        }

        if (Math.Abs(_tabPosition - target) < 0.0005)
        {
            _tabPosition = target;
            _tabVelocity = 0;
            ApplyTabTransition();
            return;
        }

        _tabAnimating = true;
        _tabLastSeconds = _tabClock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnTabRendering;
    }

    private void OnTabRendering(object? sender, EventArgs e)
    {
        double now = _tabClock.Elapsed.TotalSeconds;
        double dt = now - _tabLastSeconds;
        _tabLastSeconds = now;
        dt = Math.Clamp(dt, 0.001, 0.05);

        // 欠阻尼弹簧：越过目标再回弹，才有"果冻"手感；临界阻尼只会显得发死。
        // 参数按本项目调过（参考项目是 250/31 近临界，这里刻意更 Q 一点）；
        // 具体数值由设置里的动画风格（标准/顺滑/弹跳/干脆）换算。
        double stiffness = _tabStiffness;
        double damping = _tabDamping;

        int steps = Math.Max(1, (int)Math.Ceiling(dt / 0.004));
        double sub = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            double force = (_tabTarget - _tabPosition) * stiffness - _tabVelocity * damping;
            _tabVelocity += force * sub;
            _tabPosition += _tabVelocity * sub;
        }

        double max = _tabPages.Length - 1;
        if (_tabPosition < 0)
        {
            _tabPosition = 0;
            if (_tabVelocity < 0)
            {
                _tabVelocity = 0;
            }
        }
        else if (_tabPosition > max)
        {
            _tabPosition = max;
            if (_tabVelocity > 0)
            {
                _tabVelocity = 0;
            }
        }

        ApplyTabTransition();

        if (Math.Abs(_tabPosition - _tabTarget) < 0.002 && Math.Abs(_tabVelocity) < 0.02)
        {
            _tabPosition = _tabTarget;
            _tabVelocity = 0;
            ApplyTabTransition();
            _tabAnimating = false;
            CompositionTarget.Rendering -= OnTabRendering;
        }
    }

    /// <summary>
    /// 把连续的 tab 位置落到画面上：每个页面按"与当前位置的距离"水平滑动、缩放、淡出；
    /// 分页点同样连续变化，速度快时拉长一点，像被甩出去再弹回来。
    /// </summary>
    private void ApplyTabTransition()
    {
        if (_tabPages.Length == 0)
        {
            return;
        }

        double speed = Math.Abs(_tabVelocity);

        for (int i = 0; i < _tabPages.Length; i++)
        {
            double distance = Math.Min(Math.Abs(i - _tabPosition), 1.0);
            double eased = distance * distance * (3 - 2 * distance);

            double opacity = Math.Pow(1 - eased, 1.45);
            if (opacity < 0.02)
            {
                opacity = 0;
            }

            double scale = 1 - 0.075 * eased;

            // 水平滑动：下一页从右边进来、上一页往左边出去，方向与滚轮一致。
            // 位移只作用在页面自己的 RenderTransform 上，不参与布局，不会挤压内容。
            double offsetX = (i - _tabPosition) * 36;

            FrameworkElement page = _tabPages[i];
            page.Opacity = opacity;
            page.IsHitTestVisible = opacity > 0.55;

            if (page.RenderTransform is TransformGroup group &&
                group.Children.Count >= 2 &&
                group.Children[0] is ScaleTransform scaleTransform &&
                group.Children[1] is TranslateTransform translate)
            {
                scaleTransform.ScaleX = scale;
                scaleTransform.ScaleY = scale;
                translate.X = offsetX;
                translate.Y = 0;
            }
        }

        for (int i = 0; i < _tabDots.Length; i++)
        {
            double distance = Math.Min(Math.Abs(i - _tabPosition), 1.0);
            double eased = distance * distance * (3 - 2 * distance);
            double stretch = Math.Min(speed * 0.8, 3.0) * (1 - eased);

            _tabDots[i].Height = 3.5 + 6.5 * (1 - eased) + stretch;
            _tabDots[i].Opacity = 0.25 + 0.65 * (1 - eased);
        }
    }

    private readonly Dictionary<string, DateTime> _lastLaunchTimes = new();
    private Point _launcherDragStart;
    private LauncherEntryViewModel? _launcherDragCandidate;
    private LauncherEntryViewModel? _launcherDragTarget;
    private bool _launcherDragging;
    private LauncherEntryViewModel? _launcherMenuTarget;

    private void Launcher_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.Tag is not LauncherEntryViewModel entry)
        {
            return;
        }

        _launcherDragCandidate = entry;
        _launcherDragTarget = null;
        _launcherDragStart = e.GetPosition(this);
        HideLauncherMenu();

        // 拖拽期间释放鼠标捕获会让 WPF 补发 MouseLeave，这里主动维持展开态
        _hovered = true;
        _autoCollapseTimer.Stop();

        // 捕获鼠标，保证拖出图标范围后仍能收到 MouseMove / MouseUp
        element.CaptureMouse();
    }

    private void Launcher_MouseMove(object sender, MouseEventArgs e)
    {
        if (_launcherDragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point position = e.GetPosition(this);
        if (!_launcherDragging)
        {
            if (Math.Abs(position.X - _launcherDragStart.X) < 5 &&
                Math.Abs(position.Y - _launcherDragStart.Y) < 5)
            {
                return;
            }

            _launcherDragging = true;
        }

        _launcherDragTarget = FindLauncherAt(position);
    }

    private LauncherEntryViewModel? FindLauncherAt(Point point)
    {
        HitTestResult? hit = VisualTreeHelper.HitTest(RootGrid, point);
        DependencyObject? current = hit?.VisualHit;
        while (current is not null)
        {
            if (current is FrameworkElement { Tag: LauncherEntryViewModel entry })
            {
                return entry;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void Launcher_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement)
        {
            ((UIElement)sender).ReleaseMouseCapture();
        }

        if (_launcherDragging)
        {
            _launcherDragging = false;
            LauncherEntryViewModel? target = FindLauncherAt(e.GetPosition(this)) ?? _launcherDragTarget;
            LogService.Info($"图标拖拽结束：{_launcherDragCandidate?.Name} → {target?.Name ?? "无"}");

            if (_launcherDragCandidate is { } dragged && target is not null &&
                !ReferenceEquals(dragged, target))
            {
                _viewModel.MoveLauncher(dragged, target);
            }

            _launcherDragCandidate = null;
            _launcherDragTarget = null;
            return;
        }

        _launcherDragCandidate = null;
        _launcherDragTarget = null;

        if (sender is not FrameworkElement { Tag: LauncherEntryViewModel entry } element)
        {
            return;
        }

        // 防重：曾经出现过一次点击被派发几十次、把同一个程序拉起 30 次的情况。
        // 同一条目 900ms 内只允许启动一次；相同 exe 的不同快捷方式可以带不同参数。
        string key = string.IsNullOrWhiteSpace(entry.Item.SourcePath)
            ? entry.Item.TargetPath + "\n" + entry.Item.Arguments
            : entry.Item.SourcePath;
        if (_lastLaunchTimes.TryGetValue(key, out DateTime last) &&
            (DateTime.Now - last).TotalMilliseconds < 900)
        {
            LogService.Warn($"忽略重复的图标点击：{entry.Name}");
            return;
        }

        _lastLaunchTimes[key] = DateTime.Now;
        LogService.Info($"图标被点击：{entry.Name}");

        // 按下反馈：轻微缩放，避免"点了没反应"的错觉（启动大型应用本身要几秒）
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        if (element.RenderTransform is not ScaleTransform)
        {
            element.RenderTransform = new ScaleTransform(1, 1);
        }

        if (element.RenderTransform is ScaleTransform scale)
        {
            var squash = new DoubleAnimation(0.86, TimeSpan.FromMilliseconds(90))
            {
                AutoReverse = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, squash);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, squash);
        }

        _viewModel.LaunchCommand.Execute(entry);
    }

    private void Launcher_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: LauncherEntryViewModel entry })
        {
            return;
        }

        _launcherMenuTarget = entry;
        HideContextMenu();

        Point mouse = Mouse.GetPosition(RootGrid);
        LauncherMenuLayer.Visibility = Visibility.Visible;

        // 菜单尽量留在岛体内（面板尺寸随页面变化），避免像弹到桌面一样突兀
        (double pageWidth, double pageHeight) = SizeForTab(_viewModel.ActiveTab);
        double islandLeft = (ActualWidth - pageWidth) / 2.0;
        double left = Math.Clamp(mouse.X - 60, islandLeft + 4, islandLeft + pageWidth - 138);
        double top = Math.Clamp(mouse.Y + 8, 4, Math.Max(4, pageHeight - 84));
        Canvas.SetLeft(LauncherMenuPanel, left);
        Canvas.SetTop(LauncherMenuPanel, top);

        UpdateLayout();
        UpdateIslandHitRect();
    }

    private void HideLauncherMenu()
    {
        if (LauncherMenuLayer is null)
        {
            return;
        }

        LauncherMenuLayer.Visibility = Visibility.Collapsed;
        _launcherMenuTarget = null;
        UpdateIslandHitRect();
    }

    private void LauncherMenu_Reveal_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_launcherMenuTarget is { } entry)
        {
            string path = System.IO.File.Exists(entry.Item.SourcePath)
                ? entry.Item.SourcePath
                : entry.Item.TargetPath;
            LauncherService.RevealInExplorer(path);
        }

        HideLauncherMenu();
    }

    private void LauncherMenu_Remove_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_launcherMenuTarget is { } entry)
        {
            _viewModel.RemoveLauncherCommand.Execute(entry);
        }

        HideLauncherMenu();
    }

    private void SwitchSource_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.SwitchSourceCommand.Execute(null);
    }

    private void UpdateSpectrum()
    {
        if (_state != IslandState.Expanded ||
            _viewModel.ActiveTab != 0 ||
            !_viewModel.IsPlaying ||
            _viewModel.HasLyrics)
        {
            return;
        }

        foreach (Border bar in _spectrumBars)
        {
            double target = 5 + _random.NextDouble() * 21;
            var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            bar.BeginAnimation(FrameworkElement.HeightProperty, animation);
        }
    }

    // ---------------- 拖放 ----------------

    private void Island_DragEnter(object sender, DragEventArgs e)
    {
        bool hasFiles = false;
        try
        {
            hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        }
        catch (Exception ex)
        {
            LogService.Warn("DragEnter 读取数据格式失败：" + ex.Message);
        }

        LogService.Info($"DragEnter 到达窗口，FileDrop={hasFiles}");

        if (!hasFiles)
        {
            return;
        }

        DropHint.Visibility = Visibility.Visible;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        // 拖动经过时先把岛展开：折叠胶囊只有 36px 高，很难精确丢进去。
        // 展开后整个岛体都是投放区，松手成功率更高。
        if (_state == IslandState.Collapsed)
        {
            SetState(IslandState.Expanded);
            _spring.Kick(90);
            _spring.NudgeY(2.5);
        }
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Island_DragLeave(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
    }

    private void Island_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
        LogService.Info("Drop 事件触发");

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            LogService.Warn("Drop 不含 FileDrop");
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            LogService.Warn("Drop 路径为空");
            return;
        }

        LogService.Info($"Drop 收到 {paths.Length} 个路径：{string.Join(", ", paths)}");

        int added = _viewModel.AddLauncherPaths(paths);
        if (added > 0)
        {
            _viewModel.ActiveTab = 3;
            SetState(IslandState.Expanded);
        }
    }

    // ---------------- 进度条拖动 ----------------

    private void Progress_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.BeginSeek(CalcProgressRatio(e));
        ProgressTrack.CaptureMouse();
    }

    private void Progress_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_viewModel.IsSeeking || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        e.Handled = true;
        _viewModel.UpdateSeek(CalcProgressRatio(e));
    }

    private void Progress_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsSeeking)
        {
            return;
        }

        e.Handled = true;
        ProgressTrack.ReleaseMouseCapture();
        _viewModel.EndSeek(CalcProgressRatio(e));
    }

    private double CalcProgressRatio(MouseEventArgs e)
    {
        double width = ProgressTrack.ActualWidth;
        if (width <= 1)
        {
            return 0;
        }

        Point position = e.GetPosition(ProgressTrack);
        return Math.Clamp(position.X / width, 0, 1);
    }

    protected override void OnClosed(EventArgs e)
    {
        _notificationTimer.Stop();
        _autoCollapseTimer.Stop();
        _volumeHudTimer.Stop();
        _systemAlertTimer.Stop();
        _clipboardHudTimer.Stop();
        _fullscreenTimer.Stop();
        _spectrumTimer.Stop();

        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyToggleId);
            RemoveClipboardFormatListener(_hwnd);
        }

        _viewModel.SystemAlertRequested -= OnSystemAlertRequested;
        _viewModel.ClipboardCaptured -= OnClipboardCaptured;
        _viewModel.ClipboardCopyRequested -= OnClipboardCopyRequested;
        _settingsWindow?.Close();
        _settingsWindow = null;
        _spring.Dispose();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_tabAnimating)
        {
            CompositionTarget.Rendering -= OnTabRendering;
            _tabAnimating = false;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
