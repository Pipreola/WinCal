using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace WinCal.Core.Helpers;

internal sealed record CalendarPlacementEnvironment(
    IntPtr Host, uint ProcessId, uint ThreadId, long ProcessStartTicks, Rect HostBounds,
    IntPtr Monitor, Rect MonitorBounds, Rect WorkArea, uint Dpi, string RegionFingerprint);

public sealed class SystemCalendarPlacementCache
{
    private readonly object _sync = new();
    private readonly Func<IntPtr, CalendarPlacementEnvironment?> _capture;
    private CalendarPlacementEnvironment? _environment;
    private Rect _bounds = Rect.Empty;

    public static SystemCalendarPlacementCache Shared { get; } = new();
    public SystemCalendarPlacementCache() : this(CaptureEnvironment) { }

    internal SystemCalendarPlacementCache(Func<IntPtr, CalendarPlacementEnvironment?> capture)
    {
        _capture = capture;
    }

    /// <summary>Require the stable native shape before permitting a region cut.</summary>
    public bool TryGet(IntPtr host, out Rect bounds)
        => TryGetCore(host, requireStableShape: true, out bounds);

    /// <summary>Reuse validated coordinates across a temporary native region animation.</summary>
    public bool TryGetPlacement(IntPtr host, out Rect bounds)
        => TryGetCore(host, requireStableShape: false, out bounds);

    private bool TryGetCore(IntPtr host, bool requireStableShape, out Rect bounds)
    {
        lock (_sync)
        {
            bounds = Rect.Empty;
            if (_environment is null) return false;
            var current = _environment.Host == host ? _capture(host) : null;
            if (_environment.Host != host || current is null ||
                current with { RegionFingerprint = _environment.RegionFingerprint } != _environment)
            {
                OverlayTiming.Write($"CACHE_MISS expected={_environment} current={current}");
                Clear();
                return false;
            }
            if (requireStableShape && current.RegionFingerprint != _environment.RegionFingerprint)
            {
                OverlayTiming.Write($"CACHE_SHAPE_PENDING expected={_environment.RegionFingerprint} current={current.RegionFingerprint}");
                return false;
            }
            bounds = _bounds;
            return true;
        }
    }

    /// <summary>Remember a rectangle already confirmed by UIA, before any native region cut.</summary>
    public bool Remember(IntPtr host, Rect validatedBounds)
    {
        lock (_sync)
        {
            var environment = _capture(host);
            if (host == IntPtr.Zero || environment is null || environment.Host != host ||
                environment.ProcessId == 0 || environment.ThreadId == 0 || environment.ProcessStartTicks <= 0 ||
                environment.Monitor == IntPtr.Zero || environment.Dpi == 0 ||
                string.IsNullOrEmpty(environment.RegionFingerprint) || environment.RegionFingerprint == "empty" ||
                !ValidRect(environment.HostBounds) || !ValidRect(environment.MonitorBounds) ||
                !ValidRect(environment.WorkArea) || !environment.MonitorBounds.Contains(environment.WorkArea) ||
                !ValidRect(validatedBounds) || !environment.HostBounds.Contains(validatedBounds))
            {
                Clear();
                return false;
            }
            _environment = environment;
            _bounds = validatedBounds;
            OverlayTiming.Write($"CACHE_REMEMBER bounds={validatedBounds} environment={environment}");
            return true;
        }
    }

    public void Invalidate(IntPtr host = default)
    {
        lock (_sync)
            if (host == IntPtr.Zero || _environment?.Host == host)
            {
                OverlayTiming.Write("CACHE_INVALIDATE");
                Clear();
            }
    }

    private void Clear()
    {
        _environment = null;
        _bounds = Rect.Empty;
    }

    private static bool ValidRect(Rect bounds) =>
        !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 &&
        double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top) &&
        double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom);

    private static CalendarPlacementEnvironment? CaptureEnvironment(IntPtr host)
    {
        try
        {
            if (host == IntPtr.Zero || !IsWindow(host) || !GetWindowRect(host, out var bounds) ||
                bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return null;
            uint thread = GetWindowThreadProcessId(host, out uint processId);
            if (thread == 0 || processId == 0) return null;
            var name = new StringBuilder(256);
            if (GetClassName(host, name, name.Capacity) == 0 || name.ToString() != "Windows.UI.Core.CoreWindow") return null;
            using var process = Process.GetProcessById((int)processId);
            if (process.ProcessName is not ("ShellExperienceHost" or "ShellHost")) return null;
            long start = process.StartTime.ToUniversalTime().Ticks;
            IntPtr monitor = MonitorFromWindow(host, 0);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
            uint dpi = GetDpiForWindow(host);
            string? fingerprint = CaptureRegionFingerprint(host);
            if (dpi == 0 || fingerprint is null ||
                GetWindowThreadProcessId(host, out uint currentProcessId) != thread || currentProcessId != processId) return null;
            return new CalendarPlacementEnvironment(host, processId, thread, start, bounds.ToRect(),
                monitor, info.Monitor.ToRect(), info.WorkArea.ToRect(), dpi, fingerprint);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private static string? CaptureRegionFingerprint(IntPtr host)
    {
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) return null;
        try
        {
            Marshal.SetLastPInvokeError(0);
            int kind = GetWindowRgn(host, region);
            if (kind == 0) return Marshal.GetLastPInvokeError() == 0 ? "none" : null;
            if (kind == 1) return "empty"; // Preserve validated geometry while the native shape is transient.
            uint size = GetRegionData(region, 0, null);
            if (size < 32 || size > 4 * 1024 * 1024) return null;
            var data = new byte[size];
            if (GetRegionData(region, size, data) != size) return null;
            return $"{kind}:{Convert.ToHexString(SHA256.HashData(data))}";
        }
        finally { DeleteObject(region); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern uint GetRegionData(IntPtr region, uint size, [Out] byte[]? data);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
}
