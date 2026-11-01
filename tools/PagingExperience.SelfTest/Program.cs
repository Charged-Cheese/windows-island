using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using DynamicIsland.Core.Models;
using DynamicIsland.Core.Services;
using DynamicIsland.Infrastructure;
using DynamicIsland.ViewModels;

string output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/paging-experience-stage6");
Directory.CreateDirectory(output);
Environment.SetEnvironmentVariable("DYNAMICISLAND_DATA_DIR", Path.Combine(output, "app-data"));
var thread = new Thread(() => Run(output));
thread.SetApartmentState(ApartmentState.STA);
thread.Start(); thread.Join();
return Environment.ExitCode;

static void Run(string output)
{
    Application? app = null;
    DynamicIsland.MainWindow? window = null;
    try
    {
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var source = XDocument.Load(Path.Combine(FindRepository(), "src/DynamicIsland/App.xaml"));
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
        var root = (Grid)window.FindName("RootGrid");
        var island = (Border)window.FindName("Island");
        var expanded = (Grid)window.FindName("ExpandedContent");
        string[] names = ["MediaPage", "WeatherPage", "HardwarePage", "LauncherPage", "NotificationPage", "CalendarPage"];
        var pages = names.Select(n => (FrameworkElement)window.FindName(n)).ToArray();
        window.ViewModel.NextMonthCommand.Execute(null);
        window.ViewModel.PreviousMonthCommand.Execute(null);
        Require(window.ViewModel.CalendarDays.Count == 42, "六行日历夹具不完整");
        Set(window.ViewModel, "Title", "超长曲目名称与 Long Track Title — media clipping and transition fixture");
        Set(window.ViewModel, "Artist", "演唱者 / Long artist name");
        Set(window.ViewModel, "LyricCurrent", "歌词、封面与页头必须跟随同一个媒体页移动");
        Set(window.ViewModel, "LyricNext", "下一行歌词内容用于检查淡入淡出");
        Set(window.ViewModel, "HasLyrics", true);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 48, 48));
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(Render(drawing, 48, 48)));
        using var pngStream = new MemoryStream(); png.Save(pngStream);
        Set(window.ViewModel, "Cover", BitmapFrame.Create(new MemoryStream(pngStream.ToArray())));
        window.ViewModel.ClipboardItems.Add(new ClipboardEntryViewModel(new ClipboardEntry(ClipboardKind.Image, "", pngStream.ToArray(), DateTimeOffset.Now)));
        Require(window.ViewModel.ClipboardItems[0].Thumbnail != null, "剪贴板图片夹具解码失败");
        expanded.Visibility = Visibility.Visible; expanded.Opacity = 1;
        ((TranslateTransform)expanded.RenderTransform).Y = 0;
        ((FrameworkElement)window.FindName("CollapsedContent")).Visibility = Visibility.Collapsed;
        ((FrameworkElement)window.FindName("MorphCover")).Visibility = Visibility.Collapsed;
        ((FrameworkElement)window.FindName("ExpandedCoverSlot")).Opacity = 1;
        foreach (string child in new[] { "MediaHeader", "MediaBody", "ExpandedCoverSlot", "ExpandedTitleText", "MediaControls" })
            Require(pages[0].IsAncestorOf((DependencyObject)window.FindName(child)), child + " 不属于媒体页");

        int combinations = 0;
        var contrast = new List<object>();
        foreach (var theme in ThemeManager.Themes)
        foreach (var material in MaterialManager.Materials)
        foreach (string style in new[] { "default", "smooth", "bouncy", "snappy" })
        foreach (bool media in new[] { false, true })
        foreach (bool populated in new[] { false, true })
        {
            MaterialManager.Select(material.Id); ThemeManager.Apply(theme.Id);
            window.ViewModel.Settings.AnimationStyle = style;
            Call(window, "ApplyMotionStyle");
            Set(window.ViewModel, "HasMedia", media);
            expanded.Visibility = Visibility.Visible; expanded.Opacity = 1; expanded.IsHitTestVisible = true;
            ((FrameworkElement)window.FindName("CollapsedContent")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)window.FindName("MorphCover")).Visibility = Visibility.Collapsed;
            ((TranslateTransform)expanded.RenderTransform).Y = 0;
            window.ViewModel.Notifications.Clear();
            if (populated)
                for (uint i = 0; i < 30; i++) window.ViewModel.Notifications.Add(new NotificationEntryViewModel(
                    new NotificationItem(i, "动画测试", "fixture", $"通知 {i + 1}", "长标题、30条列表与透明页面命中检查", DateTimeOffset.Now)));
            Set(window.ViewModel, "NotificationCount", populated ? 30 : 0);
            foreach (string dynamicMode in new[] { "notifications", "clipboard" })
            {
                window.ViewModel.DynamicMode = dynamicMode;
                foreach (double position in new[] { 0.0, 0.01, 0.5, 0.99, 1, 3.5, 4, 4.5, 5 })
                {
                    Field(window, "_tabPosition").SetValue(window, position);
                    Field(window, "_tabAnimTo").SetValue(window, Math.Ceiling(position));
                    Call(window, "ApplyTabTransition");
                    Layout(root, island, position >= 4.5 ? 460 : 384, position >= 4.5 ? 320 : 236);
                    Require(pages.Count(p => p.Visibility == Visibility.Visible) is >= 1 and <= 2, "可见页数异常/空白");
                    foreach (var page in pages)
                        if (page.Visibility != Visibility.Visible || page.Opacity == 0)
                            Require(!page.IsHitTestVisible && page.Opacity == 0, "透明页仍可点击或保留旧透明度");
                    if (position == Math.Floor(position))
                        Require(pages[(int)position].Opacity == 1 && pages[(int)position].IsHitTestVisible && pages.Count(p => p.IsHitTestVisible) == 1,
                            $"终态点击目标不唯一/不清晰: {position}, " + string.Join(",", pages.Select(p => $"{p.Name}:{p.Opacity}/{p.IsHitTestVisible}")));
                    if (position == 0.5)
                    {
                        double opacity = pages[0].Opacity;
                        Call(window, "AnimateTrackTitle");
                        Require(pages[0].Opacity == opacity && !pages[0].HasAnimatedProperties, "换歌动画覆盖整页透明度");
                    }
                }
            }
            combinations++;
            if (style == "default" && media && populated)
            {
                window.ViewModel.DynamicMode = "notifications";
                double[] signals = new double[2];
                int index = 0;
                foreach (bool baseline in new[] { true, false })
                {
                    var motionField = Field(window, "_pagingMotion");
                    motionField.SetValue(window, Activator.CreateInstance(motionField.FieldType,
                        700.0, 52.0, 240.0, false, baseline ? 1.45 : 1.0, baseline ? 0.02 : 0.002));
                    Field(window, "_tabPosition").SetValue(window, 4.5);
                    Field(window, "_tabAnimTo").SetValue(window, 5.0);
                    Call(window, "ApplyTabTransition");
                    Layout(root, island, 422, 278);
                    var image = Render(root, 600, 470);
                    Save(image, Path.Combine(output, $"{theme.Id}-{material.Id}-{(baseline ? "before" : "after")}.png"));
                    foreach (var page in pages) page.Visibility = Visibility.Collapsed;
                    var background = Render(root, 600, 470);
                    signals[index++] = Contrast(image, background);
                }
                Require(signals[1] > signals[0], "实际像素相对背景的内容对比没有提高");
                contrast.Add(new { theme = theme.Id, material = material.Id, before = signals[0], after = signals[1], ratio = signals[1] / signals[0] });
                // Restore the actual shipping profile for the remaining integration checks.
                Field(window, "_pagingMotion").SetValue(window, Activator.CreateInstance(Field(window, "_pagingMotion").FieldType, 1200.0, 68.0, 200.0, true, 1.0, 0.002));
            }
        }

        int timingCases = 0;
        var clickCurves = new List<object>();
        foreach (double duration in new[] { 200.0, 240 })
        foreach (bool cubic in new[] { false, true })
        {
            var motion = new PagingMotion(1200, 68, duration, cubic, 1, 0.002);
            double lastDeviation = 0, firstHidden = duration;
            for (int ms = 0; ms <= duration; ms++)
            {
                double t = ms / duration, progress = motion.ClickProgress(t);
                Require(progress >= 0 && progress <= 1, "点击曲线越界");
                if ((1 - progress) * 34 > 0.5) lastDeviation = ms;
                if (motion.Opacity(progress) == 0) firstHidden = Math.Min(firstHidden, ms);
                if (t > 0.01 && t < 0.99)
                {
                    const double epsilon = 1e-6;
                    double numericalSpeed = (motion.ClickProgress(t + epsilon) - motion.ClickProgress(t - epsilon)) / (2 * epsilon * duration / 1000);
                    Require(Math.Abs(numericalSpeed - motion.ClickSpeed(t)) < 0.00001, "点击接管的解析速度与位置曲线不一致");
                }
            }
            clickCurves.Add(new { duration_ms = duration, curve = cubic ? "Cubic" : "Quint",
                simulated_tail_0_5dip_ms = lastDeviation, simulated_old_page_hidden_ms = firstHidden });
        }
        foreach (double hz in new[] { 30.0, 60, 90, 120, 144, 240 })
        foreach (double amplitude in new[] { 1.0, 3, 5 })
        {
            double position = 0, velocity = 0, lastDeviation = 0;
            for (double t = 1 / hz; t < 1; t += 1 / hz)
            {
                (position, velocity) = PagingMotion.Default.Advance(position, velocity, amplitude, 1 / hz);
                if (Math.Abs(position - amplitude) * 34 > 0.5) lastDeviation = t;
                Require(position >= 0 && position <= amplitude + 0.0001, "滚轮曲线产生可见过冲");
            }
            Require(lastDeviation <= (amplitude == 1 ? 0.2 : 0.3), "未达到固定 0.5DIP 尾延迟目标");
            timingCases++;
        }
        Require(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "离屏测试意外显示窗口");
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { combinations,
            themes = 3, materials = 4, styles = 4, media_states = 2, history_states = 2, timingCases, contrast, clickCurves,
            limits = "Offscreen compiled-XAML rendering and synthetic curve samples; RGB contrast sum against identical page-free scene is not whole-display brightness or physical mouse acceptance." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[OK] {combinations} 主题/材质/风格/媒体/历史组合，透明命中/终态/换歌检查；{contrast.Count} 组真实像素 A/B；{timingCases} 组刷新率/行程模拟");
    }
    catch (Exception ex) { Console.WriteLine("[FAIL] " + ex); Environment.ExitCode = 1; }
    finally { window?.Close(); app?.Shutdown(); }
}

static BindingFlags Flags() => BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
static MethodInfo Method(object target, string name) => target.GetType().GetMethod(name, Flags())!;
static FieldInfo Field(object target, string name) => target.GetType().GetField(name, Flags())!;
static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Flags())!.SetValue(target, value);
static object? Call(object target, string name, params object[] values) => Method(target, name).Invoke(target, values);
static void Layout(FrameworkElement root, Border island, double width, double height)
{
    island.Width = width; island.Height = height;
    root.Measure(new Size(600, 470)); root.Arrange(new Rect(0, 0, 600, 470)); root.UpdateLayout();
}
static RenderTargetBitmap Render(Visual visual, int width, int height)
{
    var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); result.Render(visual); return result;
}
static void Save(BitmapSource bitmap, string path)
{
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path); encoder.Save(stream);
}
static double Contrast(BitmapSource image, BitmapSource background)
{
    var a = new byte[image.PixelWidth * image.PixelHeight * 4]; var b = new byte[a.Length];
    image.CopyPixels(a, image.PixelWidth * 4, 0); background.CopyPixels(b, image.PixelWidth * 4, 0);
    double value = 0;
    for (int i = 0; i < a.Length; i++) if (i % 4 != 3) value += Math.Abs(a[i] - b[i]);
    return value;
}
static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
static string FindRepository()
{
    for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "src/DynamicIsland/App.xaml"))) return dir.FullName;
    throw new DirectoryNotFoundException("Repository not found");
}
