using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DynamicIsland.Interop;

/// <summary>原生托盘图标（Shell_NotifyIcon），避免引入 WinForms。</summary>
internal sealed class TrayIcon : IDisposable
{
    public const int CallbackMessage = 0x8000 + 1;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hinst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly IntPtr _hwnd;
    private readonly int _id = 1;
    private IntPtr _hIcon;
    private ContextMenu? _menu;
    private bool _added;

    public TrayIcon(IntPtr hwnd, string iconPath, string tooltip)
    {
        _hwnd = hwnd;

        if (File.Exists(iconPath))
        {
            _hIcon = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        }

        if (_hIcon == IntPtr.Zero)
        {
            // 图标加载失败时退回系统默认图标，保证托盘仍然可用
            _hIcon = LoadIconW(IntPtr.Zero, new IntPtr(32512));
            Core.Services.LogService.Warn($"托盘图标加载失败，退回系统图标（path={iconPath}）");
        }

        var data = BuildData(tooltip);
        _added = Shell_NotifyIconW(NIM_ADD, ref data);

        if (!_added)
        {
            int err = Marshal.GetLastWin32Error();
            Core.Services.LogService.Warn(
                $"Shell_NotifyIcon 失败：err={err} hwnd=0x{hwnd.ToInt64():X} hIcon=0x{_hIcon.ToInt64():X} cbSize={data.cbSize}");
        }

        Log(tooltip, _added);
    }

    public Action? OnLeftClick { get; set; }
    public Action? OnToggleExpand { get; set; }
    public Action? OnQuit { get; set; }
    public Func<bool>? GetAutoStart { get; set; }
    public Action<bool>? SetAutoStart { get; set; }
    public Action? OnShowLog { get; set; }

    private NOTIFYICONDATA BuildData(string tooltip) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = _id,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    /// <summary>返回 true 表示消息已处理。</summary>
    public bool ProcessMessage(int msg, IntPtr lParam)
    {
        if (msg != CallbackMessage)
        {
            return false;
        }

        int evt = lParam.ToInt32() & 0xFFFF;
        switch (evt)
        {
            case WM_LBUTTONUP:
                OnLeftClick?.Invoke();
                break;
            case WM_LBUTTONDBLCLK:
                OnToggleExpand?.Invoke();
                break;
            case WM_RBUTTONUP:
                ShowMenu();
                break;
        }

        return true;
    }

    private void ShowMenu()
    {
        _menu ??= BuildMenu();
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var expand = new MenuItem { Header = "展开 / 收起灵动岛" };
        expand.Click += (_, _) => OnToggleExpand?.Invoke();
        menu.Items.Add(expand);

        var autoStart = new MenuItem
        {
            Header = "开机自启",
            IsCheckable = true,
            IsChecked = GetAutoStart?.Invoke() ?? false
        };
        autoStart.Click += (_, _) => SetAutoStart?.Invoke(autoStart.IsChecked);
        menu.Items.Add(autoStart);

        menu.Items.Add(new Separator());

        var log = new MenuItem { Header = "打开日志" };
        log.Click += (_, _) => OnShowLog?.Invoke();
        menu.Items.Add(log);

        menu.Items.Add(new Separator());

        var quit = new MenuItem { Header = "退出" };
        quit.Click += (_, _) => OnQuit?.Invoke();
        menu.Items.Add(quit);

        return menu;
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_added)
        {
            return;
        }

        var data = BuildData(tooltip);
        data.uFlags = NIF_TIP;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private static void Log(string tooltip, bool ok)
    {
        if (ok)
        {
            Core.Services.LogService.Info($"托盘图标注册成功（{tooltip}）");
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            Core.Services.LogService.Warn($"托盘图标注册失败（{tooltip}），Win32 错误 {err}");
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = BuildData(string.Empty);
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
