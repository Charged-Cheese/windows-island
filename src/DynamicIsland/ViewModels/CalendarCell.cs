namespace DynamicIsland.ViewModels;

/// <summary>日历页的一个日期格。</summary>
public sealed class CalendarCell
{
    public string Day { get; init; } = string.Empty;

    public bool IsToday { get; init; }

    public bool IsCurrentMonth { get; init; }
}
