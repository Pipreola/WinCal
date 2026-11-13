using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinCal.Core.Helpers;

/// <summary>
/// 弹出窗口定位工具：使用 Win32 API 获取正确的显示器工作区，处理 DPI 缩放
/// </summary>
public static class WindowPositionHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// 校验 UI Automation 返回的卡片边界，仅信任宿主内部且不与通知重叠的日历区域。
    /// 原样保留物理坐标，禁止缩小、平移或猜测一个可能挡住通知的位置。
    /// </summary>
    public static Rect CalculateCalendarOverlayPosition(
        Rect calendarBounds, Rect notificationBounds, Rect hostBounds)
    {
        static bool IsValid(Rect rect) => !rect.IsEmpty && rect.Width > 0 && rect.Height > 0 &&
            double.IsFinite(rect.Left) && double.IsFinite(rect.Top) &&
            double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom);

        if (!IsValid(calendarBounds) || !IsValid(notificationBounds) || !IsValid(hostBounds) ||
            !hostBounds.Contains(calendarBounds) || !hostBounds.Contains(notificationBounds) ||
            calendarBounds.IntersectsWith(notificationBounds))
            return Rect.Empty;

        return calendarBounds;
    }

    /// <summary>
    /// 只移动和缩放我们自己的窗口，覆盖原生日历卡片，保留上方原生通知。
    /// </summary>
    public static void PositionOverSystemCalendar(Window window, Rect physicalBounds)
    {
        if (physicalBounds.IsEmpty || physicalBounds.Width <= 0 || physicalBounds.Height <= 0)
            return;

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        // 先移到目标显示器，再按窗口当前 DPI 设置 WPF 尺寸。
        const uint noSizeNoZOrderNoActivate = 0x0001 | 0x0004 | 0x0010;
        SetWindowPos(hwnd, IntPtr.Zero,
            (int)Math.Round(physicalBounds.Left), (int)Math.Round(physicalBounds.Top),
            0, 0, noSizeNoZOrderNoActivate);

        double dpi = GetDpiForWindow(hwnd);
        double scale = dpi > 0 ? dpi / 96.0 : 1;
        window.MaxWidth = double.PositiveInfinity;
        window.MaxHeight = double.PositiveInfinity;
        window.Width = physicalBounds.Width / scale;
        window.Height = physicalBounds.Height / scale;

        // SWP_NOZORDER | SWP_NOACTIVATE：保持原生通知中心的前台焦点。
        SetWindowPos(hwnd, IntPtr.Zero,
            (int)Math.Round(physicalBounds.Left), (int)Math.Round(physicalBounds.Top),
            (int)Math.Round(physicalBounds.Width), (int)Math.Round(physicalBounds.Height),
            0x0004 | 0x0010);
    }

    /// <summary>
    /// 在同一种坐标单位中计算并排位置。优先放在系统面板左侧，其次右侧；
    /// 屏幕过窄时仍保证整个自定义面板在目标显示器工作区内。
    /// </summary>
    public static Rect CalculateAlongsidePosition(Rect nativePanelBounds, Rect workArea, Size desiredSize)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0 ||
            desiredSize.IsEmpty || desiredSize.Width <= 0 || desiredSize.Height <= 0)
        {
            throw new ArgumentException("Window and work area dimensions must be positive.");
        }

        const double gap = 8;
        double margin = Math.Min(gap, Math.Min(workArea.Width, workArea.Height) / 4);
        double width = Math.Min(desiredSize.Width, workArea.Width - margin * 2);
        double height = Math.Min(desiredSize.Height, workArea.Height - margin * 2);
        double minLeft = workArea.Left + margin;
        double maxLeft = workArea.Right - margin - width;
        double left = nativePanelBounds.Left - gap - width;

        if (left < minLeft)
        {
            double rightSide = nativePanelBounds.Right + gap;
            left = rightSide <= maxLeft ? rightSide : Math.Clamp(left, minLeft, maxLeft);
        }

        left = Math.Clamp(left, minLeft, maxLeft);
        double top = Math.Clamp(nativePanelBounds.Bottom - height,
            workArea.Top + margin, workArea.Bottom - margin - height);
        return new Rect(left, top, width, height);
    }

    /// <summary>
    /// 跟随原生通知/日历面板定位，不激活任何窗口。
    /// Win32 的物理坐标直接用于 SetWindowPos，避免混合 DPI 和负坐标显示器换算错误。
    /// </summary>
    public static void PositionAlongsideSystemPanel(Window window, IntPtr systemPanel)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var source = PresentationSource.FromVisual(window);
        if (hwnd == IntPtr.Zero || source?.CompositionTarget == null ||
            !GetWindowRect(systemPanel, out var panelRect))
            return;

        var monitor = MonitorFromWindow(systemPanel, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var transform = source.CompositionTarget.TransformToDevice;
        double scaleX = transform.M11;
        double scaleY = transform.M22;
        double workWidth = info.rcWork.Right - info.rcWork.Left;
        double workHeight = info.rcWork.Bottom - info.rcWork.Top;
        if (workWidth <= 0 || workHeight <= 0 || panelRect.Right <= panelRect.Left ||
            panelRect.Bottom <= panelRect.Top)
            return;

        double margin = Math.Min(8, Math.Min(workWidth, workHeight) / 4);
        window.MaxHeight = Math.Min(window.MaxHeight, (workHeight - margin * 2) / scaleY);
        window.MaxWidth = Math.Min(window.MaxWidth, (workWidth - margin * 2) / scaleX);

        double width = window.ActualWidth;
        double height = window.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var bounds = CalculateAlongsidePosition(
            new Rect(panelRect.Left, panelRect.Top,
                panelRect.Right - panelRect.Left, panelRect.Bottom - panelRect.Top),
            new Rect(info.rcWork.Left, info.rcWork.Top, workWidth, workHeight),
            new Size(width * scaleX, height * scaleY));

        // SWP_NOZORDER | SWP_NOACTIVATE：移动我们自己的窗口，保持原生通知的焦点。
        SetWindowPos(hwnd, IntPtr.Zero,
            (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top),
            (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), 0x0004 | 0x0010);
    }

    /// <summary>
    /// 将窗口定位到任务栏右下角上方，同时限制最大高度不超出屏幕。
    /// 必须在窗口 Show() 之后调用（需要窗口句柄和 DPI 信息）。
    /// </summary>
    public static void PositionNearTaskbar(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget == null) return;

        // DPI 缩放因子：物理像素 → DIPs
        double dpiScaleX = source.CompositionTarget.TransformFromDevice.M11;
        double dpiScaleY = source.CompositionTarget.TransformFromDevice.M22;

        // 获取窗口所在显示器的句柄
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var hMonitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };

        double workRight, workBottom, workTop;

        if (GetMonitorInfo(hMonitor, ref monitorInfo))
        {
            // Win32 返回的是物理像素，转为 DIPs
            workRight = monitorInfo.rcWork.Right * dpiScaleX;
            workBottom = monitorInfo.rcWork.Bottom * dpiScaleY;
            workTop = monitorInfo.rcWork.Top * dpiScaleY;
        }
        else
        {
            // 回退
            var workArea = SystemParameters.WorkArea;
            workRight = workArea.Right;
            workBottom = workArea.Bottom;
            workTop = workArea.Top;
        }

        // 可用高度（工作区顶部到底部）
        double availableHeight = workBottom - workTop;
        double margin = 8; // 底部留白

        // 限制窗口最大高度不超过可用高度
        double maxHeight = availableHeight - margin;
        if (window.MaxHeight > 0 && window.MaxHeight < maxHeight)
        {
            // XAML 中设置的 MaxHeight 更小则尊重它
            maxHeight = window.MaxHeight;
        }
        window.MaxHeight = maxHeight;

        // 使用窗口实际渲染尺寸
        double windowWidth = window.ActualWidth;
        double windowHeight = window.ActualHeight;

        if (windowWidth <= 0 || windowHeight <= 0)
        {
            window.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            windowWidth = window.DesiredSize.Width;
            windowHeight = window.DesiredSize.Height;
        }

        // 确保不会超出最大高度
        windowHeight = Math.Min(windowHeight, maxHeight);

        double left = workRight - windowWidth - 12;
        double top = workBottom - windowHeight - margin;

        if (left < 0) left = 4;
        if (top < workTop) top = workTop;

        window.Left = left;
        window.Top = top;
    }
}
