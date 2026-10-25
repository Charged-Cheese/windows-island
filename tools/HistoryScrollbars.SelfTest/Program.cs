using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;

// 加载真实的 XAML 模板验证，不启动主程序、不读取通知或剪贴板。
string root = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRepository();
var thread = new Thread(() => Run(root));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return Environment.ExitCode;

static string FindRepository()
{
    for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "src", "DynamicIsland", "MainWindow.xaml")))
            return dir.FullName;
    throw new InvalidOperationException("请传入仓库根目录。");
}

static void Run(string root)
{
    try
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(root, "src", "DynamicIsland", "MainWindow.xaml"));
        var appDocument = XDocument.Load(Path.Combine(root, "src", "DynamicIsland", "App.xaml"));
        var resourceXml = new XElement(wpf + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x),
            document.Root!.Element(wpf + "Window.Resources")!.Elements().Select(e => new XElement(e)));
        var brushXml = new XElement(wpf + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x),
            appDocument.Descendants(wpf + "SolidColorBrush").Select(e => new XElement(e)));
        var resources = (ResourceDictionary)XamlReader.Parse(resourceXml.ToString());
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(brushXml.ToString()));
        app.Resources.MergedDictionaries.Add(resources);
        var style = (Style)resources["HistoryScrollViewerStyle"];
        int passed = 0;

        foreach (string pageName in new[] { "NotificationPage", "ClipboardPage" })
        {
            var declaration = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == pageName)
                .Descendants(wpf + "ScrollViewer").Single();
            Require((string?)declaration.Attribute("VerticalScrollBarVisibility") == "Auto", "未启用溢出滚动条");
            Require((string?)declaration.Attribute("Style") == "{StaticResource HistoryScrollViewerStyle}", "未使用已验证模板");
            Require((string?)declaration.Attribute("PreviewMouseWheel") == "ContentScroll_PreviewMouseWheel", "滚轮切页入口被改变");
            Require((string?)declaration.Attribute("Thumb.DragStarted") == "HistoryScroll_DragStarted", "未连接拖动开始事件");
            Require((string?)declaration.Attribute("Thumb.DragCompleted") == "HistoryScroll_DragCompleted", "未连接拖动结束事件");
            Pass(pageName + "：滚轮和拖动事件连接正确");

            var items = new StackPanel();
            var viewer = new ScrollViewer { Width = 320, Height = 160, Style = style,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = items };
            // 只建立逻辑树以接收应用主题更新，不显示窗口。
            var host = new Window { Content = viewer };
            Layout(viewer);
            var bar = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
            Require(bar.Visibility == Visibility.Collapsed, "空列表仍显示滚动条");
            Require(Math.Abs(viewer.ViewportWidth - 320) < 1, "空列表保留了滚动条空隙");
            Pass(pageName + "：空列表隐藏滚动条且不留空隙");

            AddRows(items, 2);
            Layout(viewer);
            Require(bar.Visibility == Visibility.Collapsed && viewer.ScrollableHeight == 0, "短列表显示了滚动条");
            Pass(pageName + "：短列表不显示滚动条");

            AddRows(items, 28);
            Layout(viewer);
            bar.ApplyTemplate();
            Layout(viewer);
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            Require(bar.Visibility == Visibility.Visible && viewer.ScrollableHeight > 0, "长列表没有可用滚动条");
            Require(track.Orientation == Orientation.Vertical && track.IsDirectionReversed, "滑轨方向错误");
            Require(track.Thumb.ActualHeight >= 18 && track.Thumb.ActualHeight < viewer.ViewportHeight, "滑块尺寸错误");
            Require(Math.Abs(bar.Maximum - viewer.ScrollableHeight) < 0.1, "滚动范围未连接");
            Pass(pageName + "：溢出显示细滚动条，范围及滑块尺寸正确");

            track.Thumb.ApplyTemplate();
            var grip = (Border)track.Thumb.Template.FindName("Grip", track.Thumb);
            foreach (Color color in new[] { Color.FromRgb(167, 169, 184), Color.FromRgb(90, 97, 112) })
            {
                app.Resources["SecondaryTextBrush"] = new SolidColorBrush(color);
                Layout(viewer);
                Require(grip.Background is SolidColorBrush brush && brush.Color == color, "滑块未随主题资源更新");
            }
            Pass(pageName + "：深浅主题切换实时更新滑块颜色");

            int started = 0, completed = 0;
            viewer.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => started++));
            viewer.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => completed++));
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 25) { RoutedEvent = Thumb.DragDeltaEvent });
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 25, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Layout(viewer);
            Require(started == 1 && completed == 1, "拖动事件未冒泡到列表");
            Require(viewer.VerticalOffset > 0 && bar.Value > 0, "拖动滑块没有滚动内容");
            Pass(pageName + "：滑块拖动滚动内容并发出完整生命周期事件");

            ScrollBar.PageDownCommand.Execute(null, bar);
            Layout(viewer);
            double lower = viewer.VerticalOffset;
            ScrollBar.PageUpCommand.Execute(null, bar);
            Layout(viewer);
            Require(lower > viewer.VerticalOffset, "点击轨道的上下翻页命令失效");
            Pass(pageName + "：轨道上下翻页命令可用");

            viewer.ScrollToBottom();
            Layout(viewer);
            Require(Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) < 0.1, "列表末尾不可达");
            items.Children.Clear();
            Layout(viewer);
            Require(bar.Visibility == Visibility.Collapsed && viewer.VerticalOffset == 0, "清空后未复位");
            Pass(pageName + "：末尾可达，清空后复位并隐藏滚动条");
            host.Content = null;
            host.Close();

            void Pass(string name) { passed++; Console.WriteLine("[OK] " + name); }
        }

        Console.WriteLine($"历史列表滚动条检查：通过 {passed} 项。");
        app.Shutdown();
    }
    catch (Exception ex)
    {
        Console.WriteLine("[FAIL] " + ex);
        Environment.ExitCode = 1;
    }
}

static void Layout(FrameworkElement element)
{
    element.Measure(new Size(320, 160));
    element.Arrange(new Rect(0, 0, 320, 160));
    element.UpdateLayout();
    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    element.UpdateLayout();
}

static void AddRows(StackPanel panel, int count)
{
    for (int i = 0; i < count; i++)
        panel.Children.Add(new Border { Height = 32, Child = new TextBlock { Text = "历史条目 " + i } });
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
