namespace WinCal.Core.Models;

/// <summary>
/// 放假 / 补班标记（来源于日历事件标题中的「(休)」「(班)」后缀）
/// </summary>
public enum HolidayKind
{
    /// <summary>普通日期</summary>
    None,

    /// <summary>放假（法定假日或调休放假）</summary>
    Holiday,

    /// <summary>补班（调休上班日）</summary>
    Workday
}

/// <summary>
/// 单天数据模型（日期 + 事件 + 农历/节日 + 休假标记）
/// </summary>
public record CalendarDay(
    DateTime Date,
    bool IsCurrentMonth,
    bool IsToday,
    bool HasEvents,
    string LunarDate,
    List<CalendarEvent> Events,
    HolidayKind HolidayKind = HolidayKind.None
);
