using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using DynamicIsland.Core.Services;

namespace DynamicIsland;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    private const int WM_COPYDATA = 0x004A;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, ref CopyDataStruct lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 某些 Electron 宿主（包括 DSH 自己）会设置 ELECTRON_RUN_AS_NODE=1。
        // 这个变量会一直继承到图标坞启动的子进程，导致 Electron 应用以 Node 模式
        // 运行：点了图标没窗口、进程秒退（EchoMusic 退 9、VS Code 退 0）。
        // 在进程级清掉，保证从灵动岛启动的程序拿到正常的 GUI 环境。
        Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", null);
        Environment.SetEnvironmentVariable("ELECTRON_NO_ATTACH_CONSOLE", null);
        Environment.SetEnvironmentVariable("ELECTRON_ENABLE_LOGGING", null);
        Environment.SetEnvironmentVariable("VSCODE_PID", null);
        Environment.SetEnvironmentVariable("VSCODE_CWD", null);
        Environment.SetEnvironmentVariable("VSCODE_IPC_HOOK", null);

        // 日志要在任何服务（含窗口的托盘图标）之前就绪，否则早期日志会被丢弃
        LogService.Init();

        // 兜底：任何未处理的 UI 异常都不该让进程直接消失——
        // 之前弹簧算出负高度赋给 Height 抛异常，用户看到的就是"岛自己没了、后台也关了"。
        DispatcherUnhandledException += (_, args) =>
        {
            LogService.Warn("未处理异常（已拦截，UI 继续运行）：" + args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogService.Warn("后台未处理异常：" + args.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogService.Warn("未观察的任务异常：" + args.Exception);
            args.SetObserved();
        };

        _singleInstanceMutex = new Mutex(initiallyOwned: true, "DynamicIsland.SingleInstance.v1", out bool createdNew);
        if (!createdNew)
        {
            // 已有实例在跑：把它唤出来再退出。
            // 之前这里直接 Shutdown()，用户双击后什么都不发生，看起来像"程序打不开"。
            bool woken = WakeUpExistingInstance();
            LogService.Info(woken ? "检测到已有实例，已唤起并退出" : "检测到已有实例，但没有找到窗口");

            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>通过 WM_COPYDATA 让已运行的实例展开。</summary>
    private static bool WakeUpExistingInstance()
    {
        try
        {
            IntPtr hwnd = FindWindowW(null, "灵动岛");
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            const string command = "expand";
            IntPtr buffer = Marshal.StringToHGlobalUni(command);
            try
            {
                var data = new CopyDataStruct
                {
                    dwData = IntPtr.Zero,
                    cbData = command.Length * 2,
                    lpData = buffer
                };

                SendMessageW(hwnd, WM_COPYDATA, IntPtr.Zero, ref data);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
