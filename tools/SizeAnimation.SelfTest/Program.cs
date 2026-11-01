using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Xml.Linq;
using DynamicIsland.Infrastructure;
using DynamicIsland.ViewModels;

string output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/size-stage5-selftest");
Directory.CreateDirectory(output);
Environment.SetEnvironmentVariable("DYNAMICISLAND_DATA_DIR", Path.Combine(output, "app-data"));
var thread = new Thread(() => Run(output));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return Environment.ExitCode;

static void Run(string output)
{
    var passed = new List<string>();
    Application? app = null;
    DynamicIsland.MainWindow? window = null;
    try
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
        }
        void Done(string message) { passed.Add(message); Console.WriteLine("[OK] " + message); }
        foreach (double hz in new[] { 30.0, 60, 120, 144, 240 })
        foreach (string style in new[] { "default", "smooth", "bouncy", "snappy" })
        {
            double now = 0;
            using var engine = new IslandSpringEngine(() => now);
            engine.ApplyStyle(style);
            engine.SetTarget(384, 176, 22);
            int finishes = 0;
            engine.SizeTransition += kind => { if (kind == "size_timed_finish") finishes++; };
            foreach (var destination in new[] { (460.0, 320.0, 28.0), (384.0, 156.0, 20.0), (384.0, 236.0, 22.0) })
            {
                double begin = now, fromW = engine.Width, fromH = engine.Height, fromR = engine.Radius;
                engine.SetTargetTimed(destination.Item1, destination.Item2, destination.Item3, 240);
                Check(engine.Width == fromW && engine.Height == fromH, "定时起点跳位");
                double previousH = fromH;
                while ((now - begin) * 1000 < 240)
                {
                    now += 1 / hz;
                    engine.Tick();
                    Check(engine.Width >= Math.Min(fromW, destination.Item1) - 1e-8 && engine.Width <= Math.Max(fromW, destination.Item1) + 1e-8, "宽度过冲");
                    Check(engine.Height >= Math.Min(fromH, destination.Item2) - 1e-8 && engine.Height <= Math.Max(fromH, destination.Item2) + 1e-8, "高度过冲");
                    Check(engine.Radius >= Math.Min(fromR, destination.Item3) - 1e-8 && engine.Radius <= Math.Max(fromR, destination.Item3) + 1e-8, "圆角过冲");
                    Check((engine.Height - previousH) * (destination.Item2 - fromH) >= -1e-8, "高度反向振荡");
                    previousH = engine.Height;
                }
                Check(engine.IsSizeSettled && !engine.IsSizeTimed && engine.HeightVelocity == 0, "到期残余速度/活动通道");
                Check(engine.Width == destination.Item1 && engine.Height == destination.Item2 && engine.Radius == destination.Item3, "到期未准确落位");
                double height = engine.Height;
                now += 1;
                engine.Tick();
                Check(engine.Height == height && !engine.IsRunning, "完成后余震/Rendering 未退订");
            }
            Check(finishes == 3, "完成事件重复/丢失");
            Done($"{hz}Hz/{style}: 三种尺寸单调、240ms+一帧内准确结束、无余震");
        }

        {
            double now = 10;
            using var engine = new IslandSpringEngine(() => now);
            engine.SetTargetTimed(384, 236, 22, 240);
            Check(engine.IsSizeSettled && engine.Width == 384 && !engine.IsRunning, "首次定时目标没有直接落位");
            var events = new List<string>();
            engine.SizeTransition += events.Add;
            engine.SetTargetTimed(460, 320, 28, 240);
            now += 0.08;
            engine.Tick();
            double width = engine.Width, height = engine.Height;
            engine.SetTargetTimed(384, 156, 20, 240);
            Check(engine.Width == width && engine.Height == height, "定时反向接管跳位");
            now += 0.08;
            engine.Tick();
            width = engine.Width; height = engine.Height;
            engine.SetTarget(248, 36, 18);
            Check(engine.Width == width && engine.Height == height && !engine.IsSizeTimed, "定时转弹簧跳位/未取消");
            now += 0.02;
            engine.Tick();
            width = engine.Width; height = engine.Height;
            engine.SetTargetTimed(460, 320, 28, 240);
            Check(engine.Width == width && engine.Height == height, "弹簧转定时跳位");
            now += 0.5; // A dropped frame must not extend the 240ms wall-clock deadline.
            engine.Tick();
            Check(engine.IsSizeSettled && engine.Height == 320, "掉帧延长定时通道");
            Check(events.Count(e => e == "size_timed_cancel") == 2 && events.Count(e => e == "size_timed_finish") == 1, "取消目标被补播/错误完成");
            Done("首次落位、双向模式接管、快速反向、掉帧不延时、旧目标不补播");

            engine.SetTargetTimed(384, 156, 20, 240);
            now += 0.239;
            engine.Tick();
            engine.NudgeY(-4);
            engine.Kick(26);
            now += 0.002;
            engine.Tick();
            Check(engine.IsSizeSettled && engine.Height == 156 && engine.IsRunning && engine.NudgeOffset != 0, "尺寸结束提前停掉轻弹/HUD 扰动尺寸");
            for (int i = 0; i < 200 && engine.IsRunning; i++) { now += 0.01; engine.Tick(); }
            Check(engine.IsSettled && !engine.IsRunning && engine.NudgeOffset == 0, "轻弹未正常收尾");
            Done("尺寸与轻弹独立完成，HUD 不重启尺寸余震");

            engine.SetTargetTimed(460, 320, 28, 240);
            now += 0.05;
            engine.Tick();
            engine.NudgeY(-4);
            engine.SnapTo(384, 176, 22);
            now += 1;
            engine.Tick();
            Check(engine.IsSettled && !engine.IsRunning && engine.Height == 176 && engine.NudgeOffset == 0, "降低动效未直接落位/旧目标恢复");
            Done("降低动效取消尺寸和轻弹，直接落位");
        }

        // Use the actual compiled MainWindow, without Loaded/HWND/background services.
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string repository = FindRepository();
        var source = XDocument.Load(Path.Combine(repository, "src/DynamicIsland/App.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(wpf + "ResourceDictionary",
            source.Root!.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => new XAttribute(a)),
            source.Root.Element(wpf + "Application.Resources")!.Elements().Select(e => new XElement(e)));
        resources.SetAttributeValue(XNamespace.Xmlns + "infra", "clr-namespace:DynamicIsland.Infrastructure;assembly=DynamicIsland");
        foreach (var element in resources.Descendants().Where(e => e.Name.NamespaceName == "clr-namespace:DynamicIsland.Infrastructure"))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:DynamicIsland.Infrastructure;assembly=DynamicIsland");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        window = new DynamicIsland.MainWindow();
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, Method(window, "Window_Loaded"));
        object spring = Field(window, "_spring").GetValue(window)!;
        Call(window, "ApplySystemMotionPreference", true);
        Call(window, "SetState", IslandState.Expanded);
        Call(spring, "SnapTo", 384.0, 176.0, 22.0);
        Call(window, "RequestTabTo", 5);
        Check((double)Property(spring, "TargetHeight") == 176 && (bool)Property(spring, "IsSizeTimed"), "点击远页未从第一步尺寸开始");
        for (int i = 0; i < 5; i++)
        {
            Field(window, "_tabPosition").SetValue(window, (double)Field(window, "_tabAnimTo").GetValue(window)!);
            Call(window, "FinishTabStep");
        }
        Check(window.ViewModel.ActiveTab == 5 && (double)Property(spring, "TargetHeight") == 320, "点击逐页最终尺寸错误");
        Done("实际窗口：点击跨五页按每步目标设置尺寸");

        Call(window, "StartWheelGlide", 4);
        Check((double)Property(spring, "TargetHeight") == 236 && (bool)Property(spring, "IsSizeTimed"), "滚轮未使用定时尺寸");
        Call(window, "SetState", IslandState.Collapsed);
        double collapsedWidth = (double)Property(spring, "TargetWidth");
        Call(window, "StartTabStep", 5);
        Check(!(bool)Property(spring, "IsSizeTimed") && (double)Property(spring, "TargetHeight") == 36 && (double)Property(spring, "TargetWidth") == collapsedWidth, "旧点击队列重新展开窗口");
        Call(window, "SetState", IslandState.Expanded);
        Call(window, "StartWheelGlide", 4);
        Call(window, "SetState", IslandState.Notification);
        double notificationHeight = (double)Property(spring, "TargetHeight");
        Call(window, "StartTabStep", 0);
        Check(!(bool)Property(spring, "IsSizeTimed") && (double)Property(spring, "TargetHeight") == notificationHeight, "通知尺寸被旧切页覆盖");
        Done("实际窗口：收起/重新展开/通知接管，旧请求不能覆盖状态机");

        Field(window, "_hovered").SetValue(window, false);
        Call(window, "SetState", IslandState.Expanded);
        Call(window, "StartWheelGlide", 4);
        Call(window, "HistoryScroll_DragStarted", window, new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
        Call(window, "RecomputeState");
        Check((IslandState)Field(window, "_state").GetValue(window)! == IslandState.Expanded && (bool)Property(spring, "IsSizeTimed"), "拖动滑块期间切页尺寸/展开状态被中断");
        Call(window, "HistoryScroll_DragCompleted", window, new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false));
        Check((IslandState)Field(window, "_state").GetValue(window)! == IslandState.Collapsed && !(bool)Property(spring, "IsSizeTimed"), "滑块松开后未恢复悬停收起/未取消尺寸");
        var menu = (FrameworkElement)window.FindName("ContextMenuPanel");
        menu.Visibility = Visibility.Visible;
        Call(window, "RecomputeState");
        Call(window, "StartWheelGlide", 5);
        Call(window, "RecomputeState");
        Check((IslandState)Field(window, "_state").GetValue(window)! == IslandState.Expanded && (bool)Property(spring, "IsSizeTimed"), "菜单打开时错误收起");
        menu.Visibility = Visibility.Collapsed;
        Call(window, "RecomputeState");
        Check((IslandState)Field(window, "_state").GetValue(window)! == IslandState.Collapsed && !(bool)Property(spring, "IsSizeTimed"), "菜单关闭后未恢复悬停规则");
        Done("实际窗口：历史滑块捕获/松开、右键菜单开关保留悬停状态规则");

        Call(window, "SetState", IslandState.Expanded);
        Call(window, "StartWheelGlide", 5);
        Call(window, "ApplySystemMotionPreference", false);
        Check((bool)Property(spring, "IsSettled") && (double)Field(window, "_tabPosition").GetValue(window)! == 5 && !(bool)Field(window, "_tabAnimating").GetValue(window)!, "中途关闭动效未同步落位");
        window.ViewModel.ActiveTab = 0;
        Check((double)Field(window, "_tabPosition").GetValue(window)! == 0 && (bool)Property(spring, "IsSettled") && (double)Property(spring, "TargetHeight") == 176, "关闭动效后点击队列未直接完成");
        Done("实际窗口：中途及后续降低动效，内容和尺寸同步直接落位");
        Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero && Field(window, "_trayIcon").GetValue(window) is null, "测试启动了真实服务/窗口");

        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { checks = passed.Count, passed,
            limits = "Deterministic clock/channel checks and compiled-window state integration; not compositor or physical mouse evidence." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Passed: {passed.Count}");
    }
    catch (Exception ex) { Console.WriteLine("[FAIL] " + ex); Environment.ExitCode = 1; }
    finally { window?.Close(); app?.Shutdown(); }
}

static BindingFlags Flags() => BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
static MethodInfo Method(object target, string name) => target.GetType().GetMethod(name, Flags())!;
static FieldInfo Field(object target, string name) => target.GetType().GetField(name, Flags())!;
static object Property(object target, string name) => target.GetType().GetProperty(name, Flags())!.GetValue(target)!;
static object? Call(object target, string name, params object[] values) => Method(target, name).Invoke(target, values);
static string FindRepository()
{
    for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        if (File.Exists(Path.Combine(folder.FullName, "src/DynamicIsland/App.xaml"))) return folder.FullName;
    throw new DirectoryNotFoundException("Repository not found");
}
