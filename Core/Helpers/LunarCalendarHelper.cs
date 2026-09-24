using System.Globalization;

namespace WinCal.Core.Helpers;

/// <summary>
/// 日期副标题计算：农历日期、农历节日、公历节日、二十四节气
/// </summary>
public static class LunarCalendarHelper
{
    private static readonly ChineseLunisolarCalendar _calendar = new();

    // 农历月份名称
    private static readonly string[] _monthNames =
    {
        "", "正月", "二月", "三月", "四月", "五月", "六月",
        "七月", "八月", "九月", "十月", "冬月", "腊月"
    };

    // 农历日期名称
    private static readonly string[] _dayNames =
    {
        "", "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"
    };

    // 农历节日（月*100+日 -> 名称）
    private static readonly Dictionary<int, string> _lunarFestivals = new()
    {
        { 101, "春节" }, { 115, "元宵节" }, { 505, "端午节" },
        { 707, "七夕" }, { 815, "中秋节" }, { 909, "重阳节" },
        { 1208, "腊八节" },
    };

    // 公历节日（月*100+日 -> 名称）
    private static readonly Dictionary<int, string> _solarFestivals = new()
    {
        { 101, "元旦" }, { 214, "情人节" }, { 308, "妇女节" }, { 312, "植树节" },
        { 401, "愚人节" }, { 501, "劳动节" }, { 504, "青年节" }, { 601, "儿童节" },
        { 701, "建党节" }, { 801, "建军节" }, { 910, "教师节" }, { 918, "九·一八" },
        { 903, "抗战胜利日" }, { 930, "烈士纪念日" }, { 1001, "国庆节" },
        { 1024, "程序员节" }, { 1224, "平安夜" }, { 1225, "圣诞节" },
    };

    // 二十四节气名称，按公历月份顺序排列（每月两个）。
    // 序号 i 对应太阳黄经 (285 + 15*i) % 360 度：小寒 285°、立春 315°、春分 0°、清明 15°……
    private static readonly string[] _solarTermNames =
    {
        "小寒", "大寒", "立春", "雨水", "惊蛰", "春分",
        "清明", "谷雨", "立夏", "小满", "芒种", "夏至",
        "小暑", "大暑", "立秋", "处暑", "白露", "秋分",
        "寒露", "霜降", "立冬", "小雪", "大雪", "冬至"
    };

    // 每年 24 个节气的「日」缓存（键为年份），避免每个格子重复做天文计算
    private static readonly Dictionary<int, int[]> _solarTermDayCache = new();
    private static readonly object _cacheLock = new();

    /// <summary>
    /// 获取日期格子中日期数字下方显示的副标题。
    /// 优先级：农历节日 &gt; 公历节日 &gt; 节气 &gt; 农历初一显示月名 &gt; 农历日期
    /// （与系统日历一致：节日名比农历日期更有信息量）
    /// </summary>
    public static string GetLunarDateText(DateTime date)
    {
        try
        {
            // 农历节日优先级最高
            var lunarFestival = GetLunarFestival(date);
            if (!string.IsNullOrEmpty(lunarFestival))
                return lunarFestival;

            // 公历节日
            if (_solarFestivals.TryGetValue(date.Month * 100 + date.Day, out var solarFestival))
                return solarFestival;

            // 二十四节气
            var term = GetSolarTerm(date);
            if (!string.IsNullOrEmpty(term))
                return term;

            // 农历日期（初一显示月名）
            return GetLunarDayText(date);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 获取纯农历日期文本（初一显示月名，如「八月」；其余显示「十四」）
    /// </summary>
    public static string GetLunarDayText(DateTime date)
    {
        try
        {
            if (date < _calendar.MinSupportedDateTime || date > _calendar.MaxSupportedDateTime)
                return string.Empty;

            int month = _calendar.GetMonth(date);
            int day = _calendar.GetDayOfMonth(date);

            int leapMonth = _calendar.GetLeapMonth(_calendar.GetYear(date));
            bool isLeapMonth = leapMonth > 0 && month == leapMonth;
            int normalMonth = leapMonth > 0 && month >= leapMonth ? month - 1 : month;

            if (day == 1)
            {
                string prefix = isLeapMonth ? "闰" : "";
                return prefix + _monthNames[normalMonth];
            }

            return _dayNames[day];
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 获取农历节日名（含除夕：农历年最后一天，需按当年腊月天数判断）
    /// </summary>
    private static string GetLunarFestival(DateTime date)
    {
        if (date < _calendar.MinSupportedDateTime || date > _calendar.MaxSupportedDateTime)
            return string.Empty;

        int month = _calendar.GetMonth(date);
        int day = _calendar.GetDayOfMonth(date);
        int year = _calendar.GetYear(date);

        int leapMonth = _calendar.GetLeapMonth(year);
        bool isLeapMonth = leapMonth > 0 && month == leapMonth;
        int normalMonth = leapMonth > 0 && month >= leapMonth ? month - 1 : month;

        // 闰月不算节日（闰五月初五不是端午）
        if (isLeapMonth)
            return string.Empty;

        // 除夕：腊月最后一天（可能是廿九或三十，取决于该月天数）
        if (normalMonth == 12)
        {
            int daysInMonth = _calendar.GetDaysInMonth(year, month);
            if (day == daysInMonth)
                return "除夕";
        }

        return _lunarFestivals.GetValueOrDefault(normalMonth * 100 + day, string.Empty);
    }

    /// <summary>
    /// 判断该日期是否为二十四节气之一，返回节气名；否则返回空串。
    /// </summary>
    public static string GetSolarTerm(DateTime date)
    {
        if (date.Year < 1900 || date.Year > 2100)
            return string.Empty;

        var days = GetSolarTermDays(date.Year);

        // 每年 24 个节气按月分布，每月两个：序号 = (月-1)*2 + (0 或 1)
        for (int i = 0; i < 2; i++)
        {
            int index = (date.Month - 1) * 2 + i;
            if (days[index] == date.Day)
                return _solarTermNames[index];
        }

        return string.Empty;
    }

    /// <summary>
    /// 取得某年 24 个节气各自所在的「日」（带缓存）
    /// </summary>
    private static int[] GetSolarTermDays(int year)
    {
        lock (_cacheLock)
        {
            if (_solarTermDayCache.TryGetValue(year, out var cached))
                return cached;

            var days = new int[24];
            for (int i = 0; i < 24; i++)
                days[i] = CalculateSolarTermDay(year, i);

            // 缓存上限，防止长期运行时无限增长
            if (_solarTermDayCache.Count > 64)
                _solarTermDayCache.Clear();

            _solarTermDayCache[year] = days;
            return days;
        }
    }

    /// <summary>
    /// 计算指定年份第 index 个节气（0=小寒，按公历月序）所在的日。
    /// 节气定义为太阳视黄经达到特定角度的时刻，这里用二分法求解
    /// 「太阳黄经 == 目标角度」的时刻，精度远高于线性均值公式
    /// （后者在部分年份会偏差一天，例如 2026 年的雨水和芒种）。
    /// </summary>
    private static int CalculateSolarTermDay(int year, int index)
    {
        // 序号 → 目标黄经：小寒 285°，之后每个节气 +15°
        double targetLongitude = (285.0 + 15.0 * index) % 360.0;

        // 该节气必然落在这个公历月内，以月初到月末为搜索区间
        int month = index / 2 + 1;
        var lo = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var hi = lo.AddMonths(1);

        // 以「相对目标角度的差值」为单调函数做二分：
        // 区间起点差值为负、终点为正，中间存在唯一零点。
        // 循环不变式：lo 处尚未达到目标黄经，hi 处已达到或超过。
        for (int iter = 0; iter < 48; iter++)
        {
            var mid = lo.AddTicks((hi - lo).Ticks / 2);
            if (NormalizeDiff(SunApparentLongitude(mid) - targetLongitude) < 0)
                lo = mid;
            else
                hi = mid;
        }

        // 取 hi（已达到目标黄经的最早时刻）才是节气时刻本身；
        // 取 lo 会在节气恰好落在午夜后不久时退到前一天
        // （例如 2026 年雨水为东八区 02-19 00:05，取 lo 会算成 18 日）。
        return hi.AddHours(8).Day;
    }

    /// <summary>把角度差规范到 [-180, 180)，便于判断在目标角度之前还是之后</summary>
    private static double NormalizeDiff(double degrees)
    {
        degrees %= 360.0;
        if (degrees >= 180.0) degrees -= 360.0;
        if (degrees < -180.0) degrees += 360.0;
        return degrees;
    }

    /// <summary>
    /// 计算给定 UTC 时刻的太阳视黄经（度，0~360）。
    /// 采用低精度公式（误差约 0.01°，对应时间误差远小于一天），
    /// 足以确定节气所在的日期。
    /// </summary>
    private static double SunApparentLongitude(DateTime utc)
    {
        // 儒略世纪数（自 J2000.0 起）
        double jd = ToJulianDay(utc);
        double t = (jd - 2451545.0) / 36525.0;

        // 太阳几何平黄经
        double l0 = 280.46646 + t * (36000.76983 + t * 0.0003032);
        // 太阳平近点角
        double m = 357.52911 + t * (35999.05029 - t * 0.0001537);
        double mRad = m * Math.PI / 180.0;

        // 中心差
        double c = (1.914602 - t * (0.004817 + t * 0.000014)) * Math.Sin(mRad)
                 + (0.019993 - t * 0.000101) * Math.Sin(2 * mRad)
                 + 0.000289 * Math.Sin(3 * mRad);

        // 真黄经
        double trueLongitude = l0 + c;

        // 黄经章动与光行差修正
        double omega = 125.04 - 1934.136 * t;
        double apparent = trueLongitude
                        - 0.00569
                        - 0.00478 * Math.Sin(omega * Math.PI / 180.0);

        apparent %= 360.0;
        if (apparent < 0) apparent += 360.0;
        return apparent;
    }

    /// <summary>UTC 时刻转儒略日</summary>
    private static double ToJulianDay(DateTime utc)
    {
        int year = utc.Year;
        int month = utc.Month;
        double day = utc.Day
                   + utc.Hour / 24.0
                   + utc.Minute / 1440.0
                   + utc.Second / 86400.0
                   + utc.Millisecond / 86400000.0;

        if (month <= 2)
        {
            year -= 1;
            month += 12;
        }

        int a = year / 100;
        int b = 2 - a + a / 4;

        return Math.Floor(365.25 * (year + 4716))
             + Math.Floor(30.6001 * (month + 1))
             + day + b - 1524.5;
    }
}
