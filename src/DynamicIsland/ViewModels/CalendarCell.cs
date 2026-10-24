using DynamicIsland.Infrastructure;

namespace DynamicIsland.ViewModels;

/// <summary>日历页的一个日期格。</summary>
public sealed class CalendarCell : ObservableObject
{
    public DateTime Date { get; init; }

    public string Day => Date.Day.ToString();

    public bool IsToday { get; init; }

    public bool IsCurrentMonth { get; init; }

    /// <summary>是否被用户选中（今天默认选中）。</summary>
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
