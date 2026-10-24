using System.Globalization;

namespace DynamicIsland.Infrastructure;

/// <summary>
/// 农历显示。数据来自 .NET 内置的 <see cref="ChineseLunisolarCalendar"/>（纯算法表，1901–2100），
/// 不联网、不伪造；超出范围时返回空串，界面自动隐藏该行。
/// </summary>
public static class LunarCalendar
{
    private static readonly ChineseLunisolarCalendar Calendar = new();

    private static readonly string[] MonthNames =
    {
        "正月", "二月", "三月", "四月", "五月", "六月",
        "七月", "八月", "九月", "十月", "冬月", "腊月"
    };

    private static readonly string[] DayNames =
    {
        "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"
    };

    public static readonly int MinYear = Calendar.MinSupportedDateTime.Year;
    public static readonly int MaxYear = Calendar.MaxSupportedDateTime.Year;

    /// <summary>返回如「九月十三」「闰六月初一」；不支持或越界时返回空串。</summary>
    public static string Describe(DateTime date)
    {
        try
        {
            if (date.Year < MinYear || date.Year > MaxYear)
            {
                return string.Empty;
            }

            int year = Calendar.GetYear(date);
            int month = Calendar.GetMonth(date);
            int day = Calendar.GetDayOfMonth(date);

            int leapMonth = Calendar.GetLeapMonth(year);
            bool isLeap = leapMonth > 0 && month == leapMonth;
            int normalMonth = leapMonth > 0 && month >= leapMonth ? month - 1 : month;

            if (normalMonth is < 1 or > 12 || day is < 1 or > 30)
            {
                return string.Empty;
            }

            string monthText = (isLeap ? "闰" : string.Empty) + MonthNames[normalMonth - 1];
            return monthText + DayNames[day - 1];
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }
}
