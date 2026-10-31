using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core.Services;

namespace DynamicIsland.Infrastructure;

/// <summary>一套主题的调色板。颜色以 ARGB 保存，方便表达半透明表面。</summary>
public sealed record IslandTheme(
    string Id,
    string Label,
    Color Background,
    Color Border,
    Color Primary,
    Color Secondary,
    Color Tertiary,
    Color Accent,
    Color Card,
    Color CardBorder,
    Color Track,
    Color Control,
    Color MenuBackground,
    Color Divider);

/// <summary>
/// 主题切换：把 9 个基础 Brush + 3 个辅助 Brush 写回 Application.Resources，
/// XAML 里全部用 DynamicResource 引用，切换时即时生效（不必重建窗口）。
/// 配色是按本项目自己的蓝色主调做的三套，只借了“每套主题只有一组表面/文字 token”的思路。
/// </summary>
public static class ThemeManager
{
    public static readonly IReadOnlyList<IslandTheme> Themes = new[]
    {
        new IslandTheme(
            "obsidian", "曜石",
            Background: Color.FromArgb(0xE8, 0x0A, 0x0B, 0x10),
            Border: Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
            Primary: Color.FromRgb(0xFF, 0xFF, 0xFF),
            Secondary: Color.FromRgb(0xA9, 0xAB, 0xBA),
            Tertiary: Color.FromRgb(0x7B, 0x7D, 0x92),
            Accent: Color.FromRgb(0x6C, 0x8C, 0xFF),
            Card: Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF),
            CardBorder: Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF),
            Track: Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF),
            Control: Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
            MenuBackground: Color.FromArgb(0xFC, 0x0E, 0x0F, 0x15),
            Divider: Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),

        new IslandTheme(
            "graphite", "石墨",
            Background: Color.FromArgb(0xE9, 0x15, 0x16, 0x1C),
            Border: Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF),
            Primary: Color.FromRgb(0xF4, 0xF5, 0xF7),
            Secondary: Color.FromRgb(0xA8, 0xAB, 0xB5),
            Tertiary: Color.FromRgb(0x76, 0x7A, 0x86),
            Accent: Color.FromRgb(0x6E, 0xE7, 0xD8),
            Card: Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF),
            CardBorder: Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
            Track: Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF),
            Control: Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
            MenuBackground: Color.FromArgb(0xFC, 0x12, 0x13, 0x19),
            Divider: Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),

        new IslandTheme(
            "porcelain", "瓷白",
            Background: Color.FromArgb(0xF5, 0xF7, 0xF8, 0xFA),
            Border: Color.FromArgb(0x26, 0x00, 0x00, 0x00),
            Primary: Color.FromRgb(0x17, 0x1A, 0x21),
            Secondary: Color.FromRgb(0x52, 0x5A, 0x68),
            Tertiary: Color.FromRgb(0x7E, 0x84, 0x94),
            Accent: Color.FromRgb(0x3D, 0x6B, 0xAA),
            Card: Color.FromArgb(0x12, 0x00, 0x00, 0x00),
            CardBorder: Color.FromArgb(0x20, 0x00, 0x00, 0x00),
            Track: Color.FromArgb(0x26, 0x00, 0x00, 0x00),
            Control: Color.FromArgb(0x16, 0x00, 0x00, 0x00),
            MenuBackground: Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF),
            Divider: Color.FromArgb(0x18, 0x00, 0x00, 0x00)),
    };

    public static string CurrentId { get; private set; } = "obsidian";

    public static IslandTheme Current => Get(CurrentId);

    /// <summary>
    /// 当前实际生效的强调色。主题色只是基准；换歌后会被专辑封面的采样色覆盖，
    /// 所以歌词渐变、进度条这些拿颜色做计算的地方要读这个，而不是 <see cref="IslandTheme.Accent"/>。
    /// </summary>
    public static Color CurrentAccent => _currentAccent;

    // 注意：不能把可变（未冻结）的 Brush 放进 Application.Resources——WPF 会把资源里的
    // Freezable 冻结，之后改 Color 会抛「处于只读状态」。所以过渡期间每帧发布一个新的
    // 冻结 Brush，让 DynamicResource 引用者整体替换。
    private static Color _currentAccent = Themes[0].Accent;
    private static Color _accentTarget;
    private static bool _accentAnimating;
    private static double _accentAnimatingSeconds;
    private static double _accentElapsed;
    private static DispatcherTimer? _accentTimer;
    private static readonly Stopwatch AccentClock = new();

    public static IslandTheme Get(string? id)
        => Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];

    public static void Apply(string? id)
    {
        IslandTheme theme = Get(id);
        CurrentId = theme.Id;

        ResourceDictionary? resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        // 材质（清透/玻璃/磨砂/实色）只改这几项的浓度：底色透明度、轮廓线轻重，
        // 高光与底部内阴影在 PublishSurface 里缩放。
        IslandMaterial material = MaterialManager.Current;
        Set(resources, "IslandBgBrush", Tinted(theme.Background, material));
        Set(resources, "IslandBorderBrush", ScaleAlpha(theme.Border, material.BorderScale));
        Set(resources, "PrimaryTextBrush", theme.Primary);
        Set(resources, "SecondaryTextBrush", theme.Secondary);
        Set(resources, "TertiaryTextBrush", theme.Tertiary);
        SetAccent(theme.Accent);
        Set(resources, "CardBrush", theme.Card);
        Set(resources, "CardBorderBrush", theme.CardBorder);
        Set(resources, "TrackBrush", theme.Track);
        Set(resources, "ControlBrush", theme.Control);
        Set(resources, "MenuBgBrush", theme.MenuBackground);
        Set(resources, "DividerBrush", theme.Divider);
        PublishSurface(resources, theme);
    }

    /// <summary>
    /// 玻璃表面渐变随主题走：深色主题用「白高光 + 黑内阴影」，
    /// 浅色主题把底部阴影压淡，否则整块玻璃下半截会发脏。
    /// 这两个 Brush 被 MainWindow 用 DynamicResource 引用，切主题即时生效。
    /// </summary>
    private static void PublishSurface(ResourceDictionary resources, IslandTheme theme)
    {
        bool light = theme.Id == "porcelain";
        IslandMaterial material = MaterialManager.Current;

        var island = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        island.GradientStops.Add(new GradientStop(Color.FromArgb(Scale(light ? 0x55 : 0x3A, material.Highlight), 0xFF, 0xFF, 0xFF), 0));
        island.GradientStops.Add(new GradientStop(Color.FromArgb(Scale(light ? 0x24 : 0x12, material.Highlight), 0xFF, 0xFF, 0xFF), 0.035));
        island.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.12));
        island.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.66));
        island.GradientStops.Add(new GradientStop(Color.FromArgb(Scale(light ? 0x07 : 0x0E, material.BottomShade), 0x00, 0x00, 0x00), 0.90));
        island.GradientStops.Add(new GradientStop(Color.FromArgb(Scale(light ? 0x12 : 0x2A, material.BottomShade), 0x00, 0x00, 0x00), 1));
        island.Freeze();
        resources["IslandSurfaceBrush"] = island;

        // 浅色主题用「白色卡片」靠亮度浮起（黑色半透会在浅底上发灰发脏），
        // 深色主题仍用卡片色的两倍透明度做顶亮底暗。
        Color cardColor = light ? Colors.White : theme.Card;
        byte topAlpha = light ? (byte)0xE8 : (byte)Math.Min(0xFF, theme.Card.A * 1.35);
        byte bottomAlpha = light ? (byte)0xC8 : (byte)Math.Max(0, theme.Card.A * 0.55);
        var card = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        card.GradientStops.Add(new GradientStop(
            Color.FromArgb(topAlpha, cardColor.R, cardColor.G, cardColor.B), 0));
        card.GradientStops.Add(new GradientStop(
            Color.FromArgb(bottomAlpha, cardColor.R, cardColor.G, cardColor.B), 1));
        card.Freeze();
        resources["CardSurfaceBrush"] = card;
    }

    /// <summary>立即切到某个强调色（换主题时用，避免主题与封面色互相追赶）。</summary>
    public static void SetAccent(Color color)
    {
        StopAccentAnimation();
        PublishAccent(color);
    }

    /// <summary>
    /// 以指数逼近的方式切到目标强调色（默认半衰期 300ms），用于换歌时跟随封面取色。
    /// 用 DispatcherTimer 而不是 CompositionTarget.Rendering 驱动：
    /// 窗口空闲（没有动画/没有失效）时渲染回调会停，过渡会卡在半路，
    /// 之后所有换歌请求都被"正在过渡中"吞掉。
    /// </summary>
    public static void AnimateAccent(Color target, double halfLifeSeconds = 0.3)
    {
        _accentTarget = target;

        if (ColorsClose(_currentAccent, target))
        {
            StopAccentAnimation();
            PublishAccent(target);
            return;
        }

        if (_accentAnimating)
        {
            // 已经在过渡中：只换目标，保留当前颜色与进度，形成连续追赶
            return;
        }

        _accentAnimating = true;
        _accentAnimatingSeconds = 0;
        _accentElapsed = 0;
        AccentClock.Restart();
        StartAccentTimer();
        LogService.Info($"强调色过渡 → #{target.R:X2}{target.G:X2}{target.B:X2}");
    }

    private static void StartAccentTimer()
    {
        if (_accentTimer is null)
        {
            _accentTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _accentTimer.Tick += (_, _) => StepAccent();
        }

        _accentTimer.Start();
    }

    private static void StepAccent()
    {
        if (!_accentAnimating)
        {
            _accentTimer?.Stop();
            return;
        }

        double now = AccentClock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _accentAnimatingSeconds, 0.001, 0.12);
        _accentAnimatingSeconds = now;
        _accentElapsed += dt;

        // 指数半衰期：每 300ms 走完剩余距离的一半
        double k = 1 - Math.Pow(0.5, dt / 0.3);
        Color next = Lerp(_currentAccent, _accentTarget, k);
        PublishAccent(next);

        // 收敛、或超过 1.2 秒（拖太久就强制落位，避免长时间占用定时器）
        if (ColorsClose(next, _accentTarget) || _accentElapsed >= 1.2)
        {
            PublishAccent(_accentTarget);
            StopAccentAnimation();
            LogService.Info($"强调色过渡完成：#{_accentTarget.R:X2}{_accentTarget.G:X2}{_accentTarget.B:X2}");
        }
    }

    private static void StopAccentAnimation()
    {
        if (!_accentAnimating)
        {
            _accentTimer?.Stop();
            return;
        }

        _accentAnimating = false;
        _accentTimer?.Stop();
    }

    private static void PublishAccent(Color color)
    {
        _currentAccent = color;

        if (Application.Current?.Resources is { } resources)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources["AccentBrush"] = brush;
            resources["AccentBloomBrush"] = CreateBloom(color, _bloomStrength);
        }
    }

    /// <summary>accent bloom 的强度系数（0–1）。媒体页 0.12，其余页 0.07。</summary>
    private static double _bloomStrength = 0.07;

    /// <summary>
    /// 切换 bloom 强度：只在页面换类（媒体 ↔ 其他）时调用，内部会重发一次 brush。
    /// 用强度系数而不是换刷子类型，保证强调色过渡期间 bloom 也一起渐变。
    /// </summary>
    public static void SetBloomStrength(double strength)
    {
        strength = Math.Clamp(strength, 0, 1);
        if (Math.Abs(strength - _bloomStrength) < 0.001)
        {
            return;
        }

        _bloomStrength = strength;
        PublishAccent(_currentAccent);
    }

    /// <summary>
    /// 强调色光晕：圆心压在底部中段，往四周透明 —— 相当于「光从内容里透出来」，
    /// 而不是给整块面板染色。alpha 直接乘强度，折叠/展开都按比例缩放。
    /// </summary>
    private static Brush CreateBloom(Color accent, double strength)
    {
        var bloom = new RadialGradientBrush
        {
            Center = new System.Windows.Point(0.5, 0.96),
            GradientOrigin = new System.Windows.Point(0.5, 0.96),
            RadiusX = 0.78,
            RadiusY = 1.05
        };

        double peak = Math.Clamp(strength, 0, 1) * 255;
        bloom.GradientStops.Add(new GradientStop(
            Color.FromArgb((byte)Math.Round(peak * 0.55), accent.R, accent.G, accent.B), 0.0));
        bloom.GradientStops.Add(new GradientStop(
            Color.FromArgb((byte)Math.Round(peak * 0.16), accent.R, accent.G, accent.B), 0.46));
        bloom.GradientStops.Add(new GradientStop(
            Color.FromArgb(0, accent.R, accent.G, accent.B), 1.0));
        bloom.Freeze();
        return bloom;
    }

    private static bool ColorsClose(Color a, Color b)
        => Math.Abs(a.A - b.A) <= 1 && Math.Abs(a.R - b.R) <= 1
           && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1;

    private static Color Lerp(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(from.A + (to.A - from.A) * t),
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    /// <summary>按材质倍率缩放一个 alpha 通道（0x00–0xFF）。</summary>
    private static byte Scale(int alpha, double factor)
        => (byte)Math.Clamp((int)Math.Round(alpha * factor), 0, 255);

    /// <summary>
    /// 材质底色：先按 Tint 往白色混（磨砂档因此发灰白，而不是在深色桌面上
    /// 和别的档位一样黑），再套上材质的不透明度。
    /// </summary>
    private static Color Tinted(Color color, IslandMaterial material)
    {
        double t = Math.Clamp(material.Tint, 0, 1);
        byte r = (byte)Math.Round(color.R + (255 - color.R) * t);
        byte g = (byte)Math.Round(color.G + (255 - color.G) * t);
        byte b = (byte)Math.Round(color.B + (255 - color.B) * t);
        return Color.FromArgb(Scale(0xFF, material.Opacity), r, g, b);
    }

    private static Color ScaleAlpha(Color color, double factor)
        => Color.FromArgb(Scale(color.A, factor), color.R, color.G, color.B);
}
