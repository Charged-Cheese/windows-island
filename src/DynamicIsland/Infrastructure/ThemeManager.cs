using System.Windows;
using System.Windows.Media;

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
            Background: Color.FromArgb(0xF2, 0x0A, 0x0B, 0x10),
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
            Background: Color.FromArgb(0xF3, 0x15, 0x16, 0x1A),
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
            Background: Color.FromArgb(0xFF, 0xF7, 0xF8, 0xFA),
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
        Set(resources, "AccentBrush", theme.Accent);
        Set(resources, "CardBrush", theme.Card);
        Set(resources, "CardBorderBrush", theme.CardBorder);
        Set(resources, "TrackBrush", theme.Track);
        Set(resources, "ControlBrush", theme.Control);
        Set(resources, "MenuBgBrush", theme.MenuBackground);
        Set(resources, "DividerBrush", theme.Divider);
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
}
