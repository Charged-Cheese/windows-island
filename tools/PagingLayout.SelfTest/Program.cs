using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Xml.Linq;
using DynamicIsland.Core.Models;
using DynamicIsland.ViewModels;

// Render the compiled window offscreen: no Loaded event, HWND, service startup or real user data.
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/paging-layout-stage4");
Directory.CreateDirectory(output);
Environment.SetEnvironmentVariable("DYNAMICISLAND_DATA_DIR", Path.Combine(output, "app-data"));
var thread = new Thread(() => Run(output));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return Environment.ExitCode;

static void Run(string output)
{
    DynamicIsland.MainWindow? window = null;
    try
    {
        // Application construction schedules Startup even without Run(). Use a plain Application,
        // not DynamicIsland.App, and load only the real resource dictionary from source.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var source = XDocument.Load(Path.Combine(FindRepository(), "src", "DynamicIsland", "App.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resourceXml = new XElement(wpf + "ResourceDictionary",
            source.Root!.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => new XAttribute(a)),
            source.Root.Element(wpf + "Application.Resources")!.Elements().Select(e => new XElement(e)));
        resourceXml.SetAttributeValue(XNamespace.Xmlns + "infra", "clr-namespace:DynamicIsland.Infrastructure;assembly=DynamicIsland");
        foreach (var element in resourceXml.Descendants().Where(e => e.Name.NamespaceName == "clr-namespace:DynamicIsland.Infrastructure"))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:DynamicIsland.Infrastructure;assembly=DynamicIsland");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resourceXml.ToString());
        window = new DynamicIsland.MainWindow();
        var loaded = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(DynamicIsland.MainWindow).GetMethod("Window_Loaded", BindingFlags.NonPublic | BindingFlags.Instance)!);
        window.Loaded -= loaded;
        window.ViewModel.NextMonthCommand.Execute(null);
        window.ViewModel.PreviousMonthCommand.Execute(null);
        Require(window.ViewModel.CalendarDays.Count == 42, "日历夹具未生成完整六周日期");
        typeof(IslandViewModel).GetProperty(nameof(IslandViewModel.Title))!.SetValue(window.ViewModel, "测试曲目");
        typeof(IslandViewModel).GetProperty(nameof(IslandViewModel.Artist))!.SetValue(window.ViewModel, "媒体页布局验证");
        var cover = new DrawingImage(new GeometryDrawing(new LinearGradientBrush(Colors.CornflowerBlue, Colors.MediumPurple, 45),
            null, new RectangleGeometry(new Rect(0, 0, 46, 46))));
        typeof(IslandViewModel).GetProperty(nameof(IslandViewModel.Cover))!.SetValue(window.ViewModel, cover);
        typeof(IslandViewModel).GetProperty(nameof(IslandViewModel.HasMedia))!.SetValue(window.ViewModel, true);
        var root = (Grid)window.FindName("RootGrid");
        var island = (Border)window.FindName("Island");
        var expanded = (Grid)window.FindName("ExpandedContent");
        var viewport = (Grid)window.FindName("TabPagesViewport");
        var header = (Grid)window.FindName("MediaHeader");
        var body = (Grid)window.FindName("MediaBody");
        string[] names = ["MediaPage", "WeatherPage", "HardwarePage", "LauncherPage", "NotificationPage", "CalendarPage"];
        var pages = names.Select(name => (FrameworkElement)window.FindName(name)).ToArray();
        Require(pages[0].IsAncestorOf(header) && pages[0].IsAncestorOf(body), "媒体页头/正文没有同属媒体页");
        Require(pages.All(p => p.VerticalAlignment == VerticalAlignment.Top), "存在非顶部锚定页");
        expanded.Visibility = Visibility.Visible;
        expanded.Opacity = 1;
        ((TranslateTransform)expanded.RenderTransform).Y = 0;
        ((FrameworkElement)window.FindName("CollapsedContent")).Visibility = Visibility.Collapsed;
        ((FrameworkElement)window.FindName("MorphCover")).Visibility = Visibility.Collapsed;
        ((FrameworkElement)window.FindName("ExpandedCoverSlot")).Opacity = 1;
        for (int i = 0; i < 30; i++)
            window.ViewModel.Notifications.Add(new NotificationEntryViewModel(new NotificationItem(
                (uint)i, "布局测试", "test", $"第 {i + 1} 条测试通知", "固定尺寸列表布局与滚动范围验证", DateTimeOffset.Now)));
        typeof(IslandViewModel).GetProperty(nameof(IslandViewModel.NotificationCount))!.SetValue(window.ViewModel, 30);
        foreach (FrameworkElement page in pages)
        {
            page.Visibility = Visibility.Visible;
            page.Opacity = 1;
            if (page.RenderTransform is TransformGroup group)
            {
                ((ScaleTransform)group.Children[0]).ScaleX = ((ScaleTransform)group.Children[0]).ScaleY = 1;
                ((TranslateTransform)group.Children[1]).Y = 0;
            }
        }
        Layout(root, island, 460, 320);
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        Layout(root, island, 460, 320);
        var items = Descendants<ItemsControl>(pages[4]).First(i => ReferenceEquals(i.ItemsSource, window.ViewModel.Notifications));
        Require(items.Items.Count == 30 && items.ItemContainerGenerator.ContainerFromIndex(29) is FrameworkElement,
            "未生成 30 项通知的实际模板");
        int passed = 0;
        double maxAnchorError = 0;
        foreach (double dpi in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi));
            Layout(root, island, 460, 320);
            var reference = pages.Select(p => p.TranslatePoint(new Point(0, 0), root)).ToArray();
            var sizes = pages.Select(p => p.RenderSize).ToArray();
            double mediaBodyHeight = body.ActualHeight;
            foreach ((double width, double height) in new[] { (384.0, 236.0), (410.0, 278.0), (460.0, 320.0),
                         (384.0, 156.0), (300.0, 90.0), (384.0, 330.0), (248.0, 36.0) })
            {
                Layout(root, island, width, height);
                for (int i = 0; i < pages.Length; i++)
                {
                    Point actual = pages[i].TranslatePoint(new Point(), root);
                    double error = Math.Max(Math.Abs(actual.X - reference[i].X), Math.Abs(actual.Y - reference[i].Y));
                    maxAnchorError = Math.Max(maxAnchorError, error);
                    Require(error <= 1 / dpi + 0.01, $"{dpi:P0} {names[i]} 锚点漂移 {error:F3} DIP");
                    Require(pages[i].RenderSize == sizes[i], $"{dpi:P0} {names[i]} 随岛体改变尺寸");
                }
                Require(Math.Abs(body.ActualHeight - mediaBodyHeight) < 0.01, "媒体正文高度随岛体变化");
                passed++;
            }
            Point weatherBefore = pages[1].TranslatePoint(new Point(), root);
            header.Visibility = Visibility.Collapsed;
            Layout(root, island, 384, 176);
            Require(Math.Abs(pages[1].TranslatePoint(new Point(), root).Y - weatherBefore.Y) < 0.01,
                "媒体页头仍影响其他页锚点");
            header.Visibility = Visibility.Visible;
            Console.WriteLine($"[OK] {dpi:P0}: 六页尺寸/锚点、媒体正文、页头隔离");
        }

        VisualTreeHelper.SetRootDpi(root, new DpiScale(1, 1));
        foreach (var page in pages) page.Visibility = Visibility.Collapsed;
        pages[4].Visibility = pages[5].Visibility = Visibility.Visible;
        Layout(root, island, 384, 236);
        int contentSizeChanges = 0;
        foreach (var element in Descendants<FrameworkElement>(pages[4]).Prepend(pages[4]))
            element.SizeChanged += (_, _) => contentSizeChanges++;
        var fixedTimes = new List<double>();
        var stretchTimes = new List<double>();
        for (int trial = 0; trial < 3; trial++)
        {
            FixedLayout();
            AnimateLayout(20);
            contentSizeChanges = 0;
            fixedTimes.Add(AnimateLayout(160));
            Require(contentSizeChanges == 0, $"固定布局仍触发通知内容尺寸变化 {contentSizeChanges} 次");
            StretchLayout();
            AnimateLayout(20);
            stretchTimes.Add(AnimateLayout(160));
        }
        FixedLayout();
        Layout(root, island, 460, 320);
        var viewer = Descendants<ScrollViewer>(pages[4]).First(v => v.IsVisible || v.Visibility == Visibility.Visible);
        viewer.ScrollToBottom();
        Layout(root, island, 460, 320);
        Require(viewer.ScrollableHeight > 0 && Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) < 0.1,
            "固定页面后历史列表末尾不可达");

        // Static reference renders are artifacts, not desktop screenshots.
        for (int i = 0; i < pages.Length; i++)
        {
            foreach (var page in pages) page.Visibility = Visibility.Collapsed;
            pages[i].Visibility = Visibility.Visible;
            (double width, double height) = i == 5 ? (460, 320) : (384, i == 4 ? 236 : i == 3 ? 156 : 176);
            Layout(root, island, width, height);
            Save(root, Path.Combine(output, $"page-{i}.png"));
        }
        double[] positions = [4.1, 4.3, 4.5, 4.7, 4.9];
        foreach (double position in positions)
        {
            typeof(DynamicIsland.MainWindow).GetField("_tabPosition", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, position);
            typeof(DynamicIsland.MainWindow).GetMethod("ApplyTabTransition", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            double t = position - 4;
            Layout(root, island, 384 + 76 * t, 236 + 84 * t);
            Save(root, Path.Combine(output, $"dynamic-calendar-{position:F1}.png"));
        }
        window.ViewModel.ActiveTab = 1;
        Require(header.Visibility == Visibility.Visible, "切换 ActiveTab 隐藏了正在出场的媒体页头");
        Require(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero,
            "离屏测试意外显示窗口或创建 HWND");
        Require(typeof(DynamicIsland.MainWindow).GetField("_trayIcon", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) is null,
            "离屏测试意外执行了启动入口");
        var report = new { combinations = passed, dpi_scales = 5, pages = 6, notification_rows = 30,
            max_anchor_error_dip = maxAnchorError, fixed_content_size_changes_per_160_frames = 0,
            fixed_layout_ms_per_160_frames = fixedTimes, stretch_control_ms_per_160_frames = stretchTimes,
            note = "Offscreen layout-only A/B control using the same compiled XAML; excludes rasterization, shadow and compositor costs." };
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[OK] {passed} 尺寸/DPI组合；30项列表固定布局中 0 次内容尺寸变化；末尾可达");
        Console.WriteLine($"布局 A/B（160 帧，不含光栅）：fixed={Median(fixedTimes):F2}ms, stretch={Median(stretchTimes):F2}ms");

        void FixedLayout() => typeof(DynamicIsland.MainWindow).GetMethod("UpdateExpandedContentLayout", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        void StretchLayout()
        {
            viewport.Width = viewport.Height = double.NaN;
            viewport.HorizontalAlignment = HorizontalAlignment.Stretch;
            viewport.VerticalAlignment = VerticalAlignment.Stretch;
            foreach (var page in pages)
            {
                page.Width = page.Height = double.NaN;
                page.HorizontalAlignment = HorizontalAlignment.Stretch;
                page.VerticalAlignment = VerticalAlignment.Stretch;
            }
        }
        double AnimateLayout(int count)
        {
            var clock = Stopwatch.StartNew();
            for (int frame = 0; frame < count; frame++)
            {
                double t = (frame % 40) / 39.0;
                Layout(root, island, 384 + 76 * t, 236 + 84 * t);
            }
            return clock.Elapsed.TotalMilliseconds;
        }
        window.Close();
        app.Shutdown();
    }
    catch (Exception ex)
    {
        Console.WriteLine("[FAIL] " + ex);
        window?.Close();
        Environment.ExitCode = 1;
    }
}

static void Layout(FrameworkElement root, Border island, double width, double height)
{
    island.Width = width;
    island.Height = height;
    root.Measure(new Size(600, 470));
    root.Arrange(new Rect(0, 0, 600, 470));
    root.UpdateLayout();
}
static string FindRepository()
{
    for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "src", "DynamicIsland", "App.xaml"))) return dir.FullName;
    throw new InvalidOperationException("未找到仓库中的 App.xaml");
}
static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
{
    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
    {
        DependencyObject child = VisualTreeHelper.GetChild(root, i);
        if (child is T value) yield return value;
        foreach (T nested in Descendants<T>(child)) yield return nested;
    }
}
static void Save(Visual visual, string path)
{
    var bitmap = new RenderTargetBitmap(900, 705, 144, 144, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}
static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
