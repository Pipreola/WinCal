using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace WinCal.Core.Helpers;

/// <summary>
/// 观察 Windows 系统日历 / 通知面板的显示，触发 miniCal 日历替换流程。
/// 此类只读取原生窗口状态，不隐藏、激活或修改原生窗口。
/// </summary>
public sealed class SystemCalendarInterceptor : IDisposable
{
    private readonly object _stateLock = new();
    private readonly Dispatcher _dispatcher;
    private IntPtr _hook;
    private GCHandle _callbackHandle;
    private Action<IntPtr>? _showPopupCallback;
    private bool _disposed;
    private bool _notificationQueued;
    private IntPtr _pendingHost;

    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint EVENT_OBJECT_UNCLOAK = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const uint DWMWA_CLOAKED = 14;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, uint attribute, out uint value, int valueSize);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

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

    private delegate void WinEventDelegate(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime);

    public SystemCalendarInterceptor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>
    /// 钩子安装成功时，点击任务栏时钟可作为隐藏托盘图标后的入口。
    /// </summary>
    public bool IsHookInstalled
    {
        get { lock (_stateLock) return !_disposed && _hook != IntPtr.Zero; }
    }

    /// <summary>
    /// 开始观察系统面板，将其原生窗口句柄传给 miniCal 的显示回调。
    /// 重复调用只更新回调，不重复安装钩子。
    /// </summary>
    public void Start(Action<IntPtr> showPopupCallback)
    {
        ArgumentNullException.ThrowIfNull(showPopupCallback);

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _showPopupCallback = showPopupCallback;
            if (_hook != IntPtr.Zero) return;

            var callback = new WinEventDelegate(WinEventProc);
            _callbackHandle = GCHandle.Alloc(callback);
            try
            {
                // WinEvent 仅支持连续范围，在回调中筛选显示、解除隐藏和位置变化。
                _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_UNCLOAK,
                    IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT);
                if (_hook == IntPtr.Zero)
                {
                    Debug.WriteLine($"WinCal: System panel observer hook failed: {Marshal.GetLastWin32Error()}");
                    _callbackHandle.Free();
                }
            }
            catch
            {
                if (_callbackHandle.IsAllocated) _callbackHandle.Free();
                throw;
            }
        }
        // Single-file startup can finish after the user's clock click. Observe an already open
        // native foreground panel too; waiting only for the next SHOW would miss this session.
        QueuePanelShown(GetForegroundWindow());
    }

    private void WinEventProc(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime)
    {
        try
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero ||
                eventType is not (EVENT_OBJECT_SHOW or EVENT_OBJECT_UNCLOAK or EVENT_OBJECT_LOCATIONCHANGE))
                return;

            lock (_stateLock)
            {
                if (_disposed || _hook == IntPtr.Zero) return;
            }

            QueuePanelShown(hwnd);
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _notificationQueued = false;
                _pendingHost = IntPtr.Zero;
            }
            Debug.WriteLine($"WinCal: System panel observer callback failed: {ex.Message}");
        }
    }

    private void QueuePanelShown(IntPtr hwnd)
    {
        if (!IsSystemPanelWindow(hwnd)) return;
        lock (_stateLock)
        {
            if (_disposed || _hook == IntPtr.Zero || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
            _pendingHost = hwnd;
            if (_notificationQueued) return;
            _notificationQueued = true;
        }
        // 合并同一轮 SHOW / UNCLOAK / LOCATIONCHANGE，也用于补上启动前已发生的显示。
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(NotifyPanelShown));
    }

    private void NotifyPanelShown()
    {
        IntPtr host;
        Action<IntPtr>? callback;
        lock (_stateLock)
        {
            _notificationQueued = false;
            host = _pendingHost;
            _pendingHost = IntPtr.Zero;
            if (_disposed) return;
            callback = _showPopupCallback;
        }

        // 原生面板可能在 dispatcher 执行前已收起、cloaked 或销毁；禁止迟到的显示。
        if (IsSystemPanelVisible(host) && IsSystemPanelWindow(host))
        {
            lock (_stateLock)
            {
                if (_disposed) return;
            }
            callback?.Invoke(host);
        }
    }

    /// <summary>
    /// 判断原生面板句柄是否仍存在、可见且未被 DWM cloak。
    /// 状态无法确认时返回 false，由调用方收起跟随面板。
    /// </summary>
    public static bool IsSystemPanelVisible(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;

        try
        {
            return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out uint cloaked, sizeof(uint)) == 0 &&
                   cloaked == 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool IsSystemPanelWindow(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0) return false;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!string.Equals(process.ProcessName, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(process.ProcessName, "ShellHost", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }

        var className = new StringBuilder(256);
        if (GetClassName(hwnd, className, className.Capacity) == 0 ||
            !string.Equals(className.ToString(), "Windows.UI.Core.CoreWindow", StringComparison.Ordinal))
            return false;

        if (!GetWindowRect(hwnd, out var bounds)) return false;
        var monitor = MonitorFromWindow(hwnd, 0 /* MONITOR_DEFAULTTONULL */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;

        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        int monitorWidth = info.rcMonitor.Right - info.rcMonitor.Left;
        int monitorHeight = info.rcMonitor.Bottom - info.rcMonitor.Top;
        // 排除零尺寸、越出显示器及全屏 Shell 窗口，避免把其他系统 UI 当成通知面板。
        if (width <= 200 || height <= 100 || width > monitorWidth * 0.8 || height > monitorHeight)
            return false;

        const int edgeTolerance = 64;
        bool rightAligned = Math.Abs((long)bounds.Right - info.rcWork.Right) <= edgeTolerance ||
                            Math.Abs((long)bounds.Right - info.rcMonitor.Right) <= edgeTolerance;
        bool bottomAligned = Math.Abs((long)bounds.Bottom - info.rcWork.Bottom) <= edgeTolerance ||
                             Math.Abs((long)bounds.Bottom - info.rcMonitor.Bottom) <= edgeTolerance;
        return rightAligned && bottomAligned;
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _showPopupCallback = null;
            _pendingHost = IntPtr.Zero;
            _notificationQueued = false;

            bool unhooked = _hook == IntPtr.Zero || UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            if (unhooked)
            {
                if (_callbackHandle.IsAllocated) _callbackHandle.Free();
            }
            else
            {
                // 若系统拒绝解除钩子，保留委托根引用，避免原生回调落到已回收内存。
                // _disposed 仍会使残留回调立即返回。
                Debug.WriteLine($"WinCal: System panel observer unhook failed: {Marshal.GetLastWin32Error()}");
            }
        }
    }
}
