using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;

// 使用真实图标坞的 ItemsPanel / ItemTemplate，不读取配置或启动任何应用。
string root = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRepository();
var thread = new Thread(() => Run(root));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return Environment.ExitCode;

static string FindRepository()
{
    for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "src", "DynamicIsland", "MainWindow.xaml"))) return dir.FullName;
    throw new InvalidOperationException("请传入仓库根目录。");
}

static void Run(string root)
{
    try
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(root, "src", "DynamicIsland", "MainWindow.xaml"));
        var page = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "LauncherPage");
        var declaration = page.Descendants(wpf + "ItemsControl").Single();
        var itemXml = new XElement(declaration);
        itemXml.Add(new XAttribute(XNamespace.Xmlns + "x", x));
        string[] events = { "PreviewMouseLeftButtonDown", "MouseMove", "MouseLeftButtonUp", "MouseRightButtonUp" };
        var expectedHandlers = new[] { "Launcher_PreviewMouseLeftButtonDown", "Launcher_MouseMove", "Launcher_Click", "Launcher_RightClick" };
        for (int i = 0; i < events.Length; i++)
            Require(declaration.Descendants(wpf + "Border").Single().Attribute(events[i])?.Value == expectedHandlers[i], "图标交互入口被改变");
        foreach (var attribute in itemXml.DescendantsAndSelf().Attributes().Where(a => events.Contains(a.Name.LocalName)).ToArray()) attribute.Remove();
        Require(page.Descendants(wpf + "ScrollViewer").Single().Attribute("PreviewMouseWheel")?.Value == "ContentScroll_PreviewMouseWheel", "滚轮切页入口被改变");

        int passed = 0;
        foreach (double dpi in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        foreach (int count in new[] { 6, 7, 12, 13, 18 })
        {
            var items = (ItemsControl)XamlReader.Parse(itemXml.ToString());
            var fixture = new LauncherFixture();
            for (int i = 0; i < count; i++) fixture.Launchers.Add(new LauncherStub(i));
            items.DataContext = fixture;
            var viewer = new ScrollViewer { Content = items, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
            var host = new Border { Child = viewer, BorderThickness = new Thickness(1), UseLayoutRounding = true };
            // 模板生成后才有完整视觉树，随后把 DPI 应用到真实的图标容器。
            Layout(host, 324);
            ApplyDpi(host, dpi);
            double[]? rowPositions = null;
            // 跨过原来的第七列临界宽度，包含缩小、回弹、再次展开。
            foreach (double width in new[] { 322.0, 321.0, 320.0, 322.7, 324.0, 318.0, 321.3, 322.0 })
            {
                Layout(host, width + 2);
                Require(Math.Abs(VisualTreeHelper.GetDpi(items).DpiScaleX - dpi) < 0.0001, "DPI 未应用到图标布局");
                var panel = FindPanel(items) ?? throw new InvalidOperationException("未生成图标面板");
                var slots = CheckRows(panel, count);
                if (rowPositions is null) rowPositions = slots.Select(s => s.Y).ToArray();
                else Require(slots.Select(s => s.Y).Zip(rowPositions).All(pair => Math.Abs(pair.First - pair.Second) < 0.01),
                    $"{dpi:P0} / {count} / 宽 {width}：行位置变化 {string.Join(',', rowPositions)} -> {string.Join(',', slots.Select(s => s.Y))}");
            }
            fixture.Launchers.Move(count - 1, 0);
            Layout(host, 324);
            ApplyDpi(host, dpi);
            CheckRows(FindPanel(items)!, count);
            for (int i = 0; i < count; i++)
                Require(ReferenceEquals(((FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(i)).DataContext, fixture.Launchers[i]), "排序后图标与条目不一致");
            fixture.Launchers.RemoveAt(count - 1);
            Layout(host, 324);
            ApplyDpi(host, dpi);
            CheckRows(FindPanel(items)!, count - 1);
            fixture.Launchers.Add(new LauncherStub(99));
            Layout(host, 324);
            ApplyDpi(host, dpi);
            CheckRows(FindPanel(items)!, count);
            fixture.Launchers.Clear();
            Layout(host, 324);
            Require(items.Items.Count == 0, "清空后仍有图标");
            Console.WriteLine($"[OK] {dpi:P0} / {count} 个图标：固定六列、反复变宽、排序、增删和清空");
            passed++;
        }
        Console.WriteLine($"图标坞布局检查：通过 {passed} 组。");
    }
    catch (Exception ex)
    {
        Console.WriteLine("[FAIL] " + ex);
        Environment.ExitCode = 1;
    }
}

static Rect[] CheckRows(Panel panel, int count)
{
    Require(panel.Children.Count == count, "图标数量不符");
    var slots = panel.Children.Cast<FrameworkElement>().Select(LayoutInformation.GetLayoutSlot).ToArray();
    for (int i = 0; i < count; i++)
    {
        int rowStart = i / 6 * 6;
        Require(Math.Abs(slots[i].Y - slots[rowStart].Y) < 0.01, $"第 {i + 1} 个图标不在预期行");
        if (i >= 6) Require(slots[i].Y > slots[i - 6].Y + 1, $"第 {i + 1} 个图标未稳定换至下一行");
        if (i % 6 > 0) Require(slots[i].X > slots[i - 1].X, $"第 {i + 1} 个图标与前一项重叠");
        Require(Math.Abs(slots[i].X - slots[i % 6].X) < 0.01, $"第 {i + 1} 个图标列位置错乱");
    }
    return slots;
}

static Panel? FindPanel(DependencyObject root)
{
    if (root is Panel { IsItemsHost: true } panel) return panel;
    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        if (FindPanel(VisualTreeHelper.GetChild(root, i)) is { } result) return result;
    return null;
}

static void Layout(FrameworkElement element, double width)
{
    element.Measure(new Size(width, 112));
    element.Arrange(new Rect(0, 0, width, 112));
    element.UpdateLayout();
    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    element.UpdateLayout();
}

static void InvalidateLayout(DependencyObject root)
{
    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        InvalidateLayout(VisualTreeHelper.GetChild(root, i));
    if (root is FrameworkElement element) element.InvalidateMeasure();
}

static void ApplyDpi(FrameworkElement root, double dpi)
{
    // 没有显示窗口的测试树没有 HWND 自动给新容器传播 DPI，须显式刷新完整视觉树。
    VisualTreeHelper.SetRootDpi(root, new DpiScale(1, 1));
    VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi));
    InvalidateLayout(root);
    Layout(root, 324);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

public sealed class LauncherFixture
{
    public ObservableCollection<LauncherStub> Launchers { get; } = new();
}

public sealed record LauncherStub(int Id)
{
    public string Name => "图标 " + Id;
    public ImageSource? Icon => null;
}
