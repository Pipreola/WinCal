using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace WinCal.Core.Helpers;

/// <summary>
/// 只读定位系统面板中明确标识的原生日历区域。
/// 不读取通知名称或内容；无法确认区域时不提供任何坐标兜底。
/// </summary>
public static class SystemCalendarRegionLocator
{
    private static readonly object QueryLock = new();
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);
    private static Task<Rect?>? _inFlight;
    private static IntPtr _inFlightHost;

    /// <summary>
    /// 返回经过验证、可覆盖原生日历且不遮挡通知的物理屏幕区域。
    /// UIA 查询在后台执行；同一窗口共享正在进行的查询。
    /// </summary>
    public static async Task<Rect?> FindAsync(IntPtr host)
    {
        if (!SystemCalendarInterceptor.IsSystemPanelVisible(host)) return null;

        Task<Rect?> query;
        lock (QueryLock)
        {
            if (_inFlight is { IsCompleted: false })
            {
                // provider 超时不能取消正在执行的 COM 调用，故一直保留槽位。
                // 即使其他窗口发起查询，也不能无限新增被阻塞的后台工作。
                if (_inFlightHost != host) return null;
                query = _inFlight;
            }
            else
            {
                _inFlightHost = host;
                query = _inFlight = Task.Run(() => FindCore(host));
            }
        }

        try
        {
            var region = await query.WaitAsync(QueryTimeout).ConfigureAwait(false);
            return SystemCalendarInterceptor.IsSystemPanelVisible(host) ? region : null;
        }
        catch (TimeoutException)
        {
            // 只结束本次等待；_inFlight 仍指向原任务，阻止后台查询重叠。
            return null;
        }
    }

    private static Rect? FindCore(IntPtr host)
    {
        try
        {
            if (!SystemCalendarInterceptor.IsSystemPanelVisible(host)) return null;

            var root = AutomationElement.FromHandle(host);
            if (root is null || !TryReadVisibleBounds(root, out var hostBounds)) return null;

            var calendar = FindById(root, "CalendarCenterGrid");
            var notifications = FindById(root, "NotificationCenterGrid");
            if (calendar is null || notifications is null ||
                !TryReadVisibleBounds(calendar, out var calendarBounds) ||
                !TryReadVisibleBounds(notifications, out var notificationBounds))
                return null;

            // 折叠后的日历容器可能仍然可见，必须确认容器内的日历本体可见。
            // 不接受其他任意子控件作为日历的替代标记。
            var calendarControl = FindById(calendar, "CalendarControl");
            if (calendarControl is null ||
                !TryReadVisibleBounds(calendarControl, out var controlBounds) ||
                !calendarBounds.Contains(controlBounds))
                return null;

            // 读取期间窗口若移动或已经收起，旧的子区域坐标不再可信。
            if (!SystemCalendarInterceptor.IsSystemPanelVisible(host) ||
                !TryReadVisibleBounds(root, out var currentHostBounds) || currentHostBounds != hostBounds)
                return null;

            var region = WindowPositionHelper.CalculateCalendarOverlayPosition(
                calendarBounds, notificationBounds, hostBounds);
            return region.IsEmpty ? null : region;
        }
        catch (ElementNotAvailableException) { return null; }
        catch (COMException) { return null; }
        catch (TimeoutException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static AutomationElement? FindById(AutomationElement parent, string automationId) =>
        parent.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    private static bool TryReadVisibleBounds(AutomationElement element, out Rect bounds)
    {
        // 仅读取几何与可见性，不读取 Name、Value 或任何通知文本。
        var current = element.Current;
        bounds = current.BoundingRectangle;
        return !current.IsOffscreen && !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 &&
               double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top) &&
               double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom);
    }
}
