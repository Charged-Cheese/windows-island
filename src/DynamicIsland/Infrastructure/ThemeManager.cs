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
            Border: Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF),
            Primary: Color.FromRgb(0xFF, 0xFF, 0xFF),
            Secondary: Color.FromRgb(0xA7, 0xA9, 0xB8),
            Tertiary: Color.FromRgb(0x63, 0x65, 0x7A),
            Accent: Color.FromRgb(0x6C, 0x8C, 0xFF),
            Card: Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF),
            CardBorder: Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
            Track: Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
            Control: Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
            MenuBackground: Color.FromArgb(0xFA, 0x13, 0x13, 0x1A),
            Divider: Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),

        new IslandTheme(
            "graphite", "石墨",
            Background: Color.FromArgb(0xE9, 0x15, 0x16, 0x1A),
            Border: Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
            Primary: Color.FromRgb(0xF4, 0xF5, 0xF7),
            Secondary: Color.FromRgb(0xA3, 0xA6, 0xB0),
            Tertiary: Color.FromRgb(0x62, 0x66, 0x72),
            Accent: Color.FromRgb(0x6E, 0xE7, 0xD8),
            Card: Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
            CardBorder: Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF),
            Track: Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
            Control: Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF),
            MenuBackground: Color.FromArgb(0xFA, 0x17, 0x18, 0x1D),
            Divider: Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),

        new IslandTheme(
            "porcelain", "瓷白",
            Background: Color.FromArgb(0xF5, 0xF7, 0xF8, 0xFA),
            Border: Color.FromArgb(0x22, 0x00, 0x00, 0x00),
            Primary: Color.FromRgb(0x17, 0x1A, 0x21),
            Secondary: Color.FromRgb(0x5A, 0x61, 0x70),
            Tertiary: Color.FromRgb(0x8A, 0x90, 0xA0),
            Accent: Color.FromRgb(0x3D, 0x6B, 0xAA),
            Card: Color.FromArgb(0x10, 0x00, 0x00, 0x00),
            CardBorder: Color.FromArgb(0x18, 0x00, 0x00, 0x00),
            Track: Color.FromArgb(0x22, 0x00, 0x00, 0x00),
            Control: Color.FromArgb(0x14, 0x00, 0x00, 0x00),
            MenuBackground: Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF),
            Divider: Color.FromArgb(0x14, 0x00, 0x00, 0x00)),
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

        Set(resources, "IslandBgBrush", theme.Background);
        Set(resources, "IslandBorderBrush", theme.Border);
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
        }
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
}
