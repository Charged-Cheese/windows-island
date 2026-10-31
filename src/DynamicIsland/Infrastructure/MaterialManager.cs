namespace DynamicIsland.Infrastructure;

/// <summary>
/// 一套「材质」：和主题正交——主题决定色相，材质决定透明程度、雾面浓度、
/// 镜面高光、底部内阴影与轮廓线的轻重。WPF 在 AllowsTransparency 分层窗口里
/// 拿不到 DWM 的真模糊，所以「磨砂/玻璃」的观感由这几项配方叠出来：
/// 半透明底色 + 白雾（Tint）+ 顶部高光 + 底部内阴影 + 细轮廓。
/// </summary>
/// <param name="Opacity">底色不透明度：越低越透出桌面。</param>
/// <param name="Tint">白雾浓度：往白色混多少，磨砂档靠它发灰白而不是纯黑。</param>
/// <param name="Highlight">顶部镜面高光倍率。</param>
/// <param name="BottomShade">底部内阴影倍率。</param>
/// <param name="BorderScale">轮廓线（含边缘光）倍率。</param>
public sealed record IslandMaterial(
    string Id,
    string Label,
    string Description,
    double Opacity,
    double Tint,
    double Highlight,
    double BottomShade,
    double BorderScale);

/// <summary>
/// 材质切换：只记录当前档位，真正发资源由 <see cref="ThemeManager.Apply"/> 完成
/// （它同时持有主题色与材质配方，避免两边各写一半）。
/// </summary>
public static class MaterialManager
{
    // 顺序即设置窗口里的展示顺序：越往下越厚实。
    // 只改 alpha 的话，深色桌面上四档几乎看不出差别（底色本身就接近黑），
    // 所以磨砂档额外混入白雾，清透档保持纯净透光、靠更强的轮廓线立住形状。
    public static readonly IReadOnlyList<IslandMaterial> Materials = new[]
    {
        new IslandMaterial("clear", "清透", "几乎全透明，只留一层薄雾", 0.55, 0.00, 0.70, 0.40, 0.80),
        new IslandMaterial("glass", "玻璃", "通透玻璃，高光清晰", 0.80, 0.04, 1.00, 0.85, 1.00),
        new IslandMaterial("frosted", "磨砂", "更厚实，柔光扩散", 0.90, 0.13, 1.30, 1.10, 1.20),
        new IslandMaterial("solid", "实色", "接近不透明，安静收敛", 0.985, 0.00, 0.15, 0.45, 1.05),
    };

    public static string CurrentId { get; private set; } = "glass";

    public static IslandMaterial Current => Get(CurrentId);

    public static IslandMaterial Get(string? id)
        => Materials.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? Materials[1];

    /// <summary>设置档位（不碰资源，调用方随后要重新 <c>ThemeManager.Apply</c>）。</summary>
    public static void Select(string? id) => CurrentId = Get(id).Id;
}
