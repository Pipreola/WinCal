using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WinCal.Core.Helpers;

/// <summary>Only observes focus and the known host's hide lifecycle; never reads UI content.</summary>
internal sealed class SystemPanelLifecycleObserver : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IntPtr _host;
    private readonly Action _stateChanged;
    private readonly object _sync = new();
    private GCHandle _callbackRoot;
    private IntPtr _foregroundHook, _visibilityHook;
    private bool _disposed, _queued;

    public SystemPanelLifecycleObserver(IntPtr host, Dispatcher dispatcher, Action stateChanged)
    {
        _host = host;
        _dispatcher = dispatcher;
        _stateChanged = stateChanged;
        WinEventDelegate callback = OnEvent;
        _callbackRoot = GCHandle.Alloc(callback);
        _foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 0);
        GetWindowThreadProcessId(host, out uint pid);
        if (pid != 0)
            _visibilityHook = SetWinEventHook(0x8001, 0x8017, IntPtr.Zero, callback, pid, 0, 0);
        if (_foregroundHook == IntPtr.Zero || _visibilityHook == IntPtr.Zero)
            Trace.WriteLine("WinCal dismissal hook unavailable; short visibility timer remains active.");
    }

    private void OnEvent(IntPtr hook, uint type, IntPtr hwnd, int objectId, int childId,
        uint thread, uint time)
    {
        try
        {
            if (type != 3 && (hwnd != _host || objectId != 0 || childId != 0 ||
                type is not (0x8001 or 0x8003 or 0x8017))) return;
            lock (_sync)
            {
                if (_disposed || _queued || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
                _queued = true;
            }
            _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                lock (_sync)
                {
                    _queued = false;
                    if (_disposed) return;
                }
                _stateChanged();
            }));
        }
        catch (Exception ex)
        {
            lock (_sync) _queued = false;
            Trace.WriteLine($"WinCal dismissal observer: {ex.GetType().Name}");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            bool foregroundUnhooked = _foregroundHook == IntPtr.Zero || UnhookWinEvent(_foregroundHook);
            bool visibilityUnhooked = _visibilityHook == IntPtr.Zero || UnhookWinEvent(_visibilityHook);
            // An unhook failure must leave its delegate alive for late native callbacks.
            if (foregroundUnhooked && visibilityUnhooked && _callbackRoot.IsAllocated) _callbackRoot.Free();
            _foregroundHook = _visibilityHook = IntPtr.Zero;
        }
    }

    private delegate void WinEventDelegate(IntPtr hook, uint type, IntPtr hwnd, int objectId,
        int childId, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(
        uint min, uint max, IntPtr module, WinEventDelegate callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
