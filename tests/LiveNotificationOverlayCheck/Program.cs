using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

// Runs against the actual WinCal EXE on an unlocked desktop. Never changes native window state
// except invoking the taskbar clock. Reads notification geometry only, never its contents.
internal static class Program
{
    private static StreamWriter? _log;
    private static IntPtr _host, _popup;
    private static bool _captured, _initialOpen, _nextAttempted, _cursorSaved;
    private static string? _month;
    private static N.POINT _cursor;
    private static int? _crashProcessId;
    private static IntPtr _originalRegion;
    private static bool _rapidToggle;
    private static string? _startProduct;
    private static int _mainProcessId;
    private sealed record Target(IntPtr Handle, Rect Next, Rect Previous, string Month);
    private sealed record Layout(IntPtr Host, Target Popup, Rect Calendar, Rect Notification);

    [STAThread]
    private static int Main(string[] args)
    {
        N.SetProcessDpiAwarenessContext(new IntPtr(-4));
        using var log = new StreamWriter(Path.Combine(ProjectDirectory(), "last-run.log"), false) { AutoFlush = true };
        _log = log;
        _rapidToggle = args.Contains("--rapid-toggle");
        if (args.Length == 2 && args[0] == "--start-product") _startProduct = Path.GetFullPath(args[1]);
        if (args.Length == 2 && args[0] == "--crash-product" && int.TryParse(args[1], out int crashId))
            _crashProcessId = crashId;
        int exit = 0;
        try { Run(); Log("PASS live overlay acceptance"); }
        catch (Exception ex) { Log($"FAIL {ex.GetType().Name}: {ex.Message}"); exit = 1; }
        finally
        {
            try
            {
                if (_nextAttempted && _month is not null && Visible(_popup))
                {
                    var target = ReadPopup(_popup);
                    if (target is not null && target.Month != _month)
                    {
                        Click(target.Previous);
                        WaitMonth(value => value == _month);
                    }
                }
            }
            catch (Exception ex) { Log($"FAIL month cleanup: {ex.Message}"); exit = 1; }
            try
            {
                if (_captured && (Host() != IntPtr.Zero) != _initialOpen)
                {
                    Clock();
                    Wait(() => (Host() != IntPtr.Zero) == _initialOpen, 4000, "restore clock state");
                }
                if (_captured) Log($"RESTORED clockOpen={_initialOpen}");
            }
            catch (Exception ex) { Log($"FAIL clock cleanup: {ex.Message}"); exit = 1; }
            if (_cursorSaved && !N.SetCursorPos(_cursor.X, _cursor.Y)) { Log("FAIL cursor restoration"); exit = 1; }
            if (_originalRegion != IntPtr.Zero) N.DeleteObject(_originalRegion);
            Log($"EXIT {exit}");
        }
        return exit;
    }

    private static void Run()
    {
        Check(N.GetForegroundWindow() != IntPtr.Zero, "Interactive desktop unavailable; unlock Windows first.");
        Check(_startProduct is not null || Process.GetProcessesByName("WinCal").Length > 0, "Start the WinCal build under test first.");
        _cursorSaved = N.GetCursorPos(out _cursor);
        _initialOpen = Host() != IntPtr.Zero;
        _captured = true;
        Log($"INITIAL clockOpen={_initialOpen}");
        if (_initialOpen)
        {
            Clock();
            Wait(() => Host() == IntPtr.Zero, 4000, "close existing panel before testing its open event");
        }
        if (_crashProcessId is not null)
        {
            // Snapshot the restored shape before opening so the real application's guardian
            // can be checked against an independent HRGN after its main process is killed.
            Clock();
            Wait(() => Host() != IntPtr.Zero, 4000, "open native once to identify the immersive HWND");
            var hiddenHost = Host();
            Thread.Sleep(600);
            Clock();
            Wait(() => !Visible(hiddenHost), 4000, "close native before taking the restoration snapshot");
            Thread.Sleep(400);
            _originalRegion = N.CreateRectRgn(0, 0, 0, 0);
            Check(hiddenHost != IntPtr.Zero && N.GetWindowRgn(hiddenHost, _originalRegion) > 1,
                "Cannot snapshot a restored native shape before crash testing.");
        }
        Clock();
        if (_startProduct is not null)
        {
            Check(Process.GetProcessesByName("WinCal").Length == 0, "Cold-start test requires all WinCal instances to be closed first.");
            Wait(() => Host() != IntPtr.Zero, 4000, "native must be open before starting WinCal");
            Thread.Sleep(700);
            using var started = Process.Start(new ProcessStartInfo(_startProduct)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            });
            Check(started is not null, "Cold-start application failed to launch.");
            Log("COLD_START nativeAlreadyOpen=True");
        }
        var layout = StableLayout();
        _host = layout.Host; _popup = layout.Popup.Handle; _month = layout.Popup.Month;
        N.GetWindowThreadProcessId(_popup, out uint verifiedMainPid);
        _mainProcessId = (int)verifiedMainPid;
        Log($"LAYOUT host=0x{_host.ToInt64():X} popup=0x{_popup.ToInt64():X} calendar={layout.Calendar}");
        Hits(layout.Calendar, _popup, "calendar");
        Hits(layout.Notification, _host, "notification");
        NativeForeground();
        if (_crashProcessId is int processId)
        {
            CrashProduct(processId, layout);
            return;
        }
        _nextAttempted = true;
        Click(layout.Popup.Next);
        WaitMonth(value => value == NextMonth(_month!));
        Log("MONTH changed=True");
        NativeForeground();
        Hits(layout.Calendar, _popup, "calendar_after_next");
        Hits(layout.Notification, _host, "notification_after_next");
        Click((ReadPopup(_popup) ?? throw new InvalidOperationException("Popup disappeared.")).Previous);
        WaitMonth(value => value == _month);
        _nextAttempted = false;
        Log("MONTH restored=True");
        NativeForeground();
        _nextAttempted = true;
        Wheel(layout.Calendar, -120, _popup);
        WaitMonth(value => value == NextMonth(_month!));
        NativeForeground();
        Wheel(layout.Calendar, 120, _popup);
        WaitMonth(value => value == _month);
        _nextAttempted = false;
        Log("WHEEL changedAndRestored=True");
        NativeForeground();
        ExerciseNotifications(layout.Notification);
        CalendarScreenshot.Save(layout.Calendar, Path.Combine(ProjectDirectory(), "obj", "calendar-overlay.png"));
        Clock();
        Wait(() => !Visible(_host) && !Visible(_popup), 4000, "closing native must hide miniCal");
        Log("CLOSE nativeHidden=True popupHidden=True");
        if (_rapidToggle) RapidToggle();
    }

    private static void RapidToggle()
    {
        var original = N.CreateRectRgn(0, 0, 0, 0);
        var current = N.CreateRectRgn(0, 0, 0, 0);
        try
        {
            WaitForIdleGuardian("guardian returns to a bounded idle state before rapid test");
            Check(N.GetWindowRgn(_host, original) > 1, "No restored native region before rapid test.");
            foreach (int delay in new[] { 50, 220, 450, 80, 800 })
            {
                var guardsBefore = GuardianIds(ReadWinCalFamily());
                Clock();
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < delay)
                {
                    var family = ReadWinCalFamily();
                    Check(IsBoundedGuardianFamily(family),
                        "Rapid toggle must retain only the main process and at most one of its guardians.");
                    Thread.Sleep(20);
                }
                Clock();
                Wait(() => !Visible(_host) && !Windows().Any(h => Visible(h) && ProcessName(h) == "WinCal"),
                    4000, "rapid close must not leave a popup");
                WaitForIdleGuardian("cancelled guardian returns to idle without accumulating processes");
                Wait(() => N.GetWindowRgn(_host, current) != 0 && N.EqualRgn(original, current),
                    4000, "rapid close restores the exact original native region");
                var familyAfter = ReadWinCalFamily();
                Check(IsBoundedGuardianFamily(familyAfter), "Guardian family became unbounded after native region restoration.");
                var guardsAfter = GuardianIds(familyAfter);
                // DISARMED may return the same guardian PID to idle. A replacement
                // after a guardian failure is equally valid; neither may accumulate.
                Log($"RAPID delayMs={delay} nativeAndPopupHidden=True originalRestored=True guardianFamilyBounded=True " +
                    $"guardianReused={guardsBefore.Overlaps(guardsAfter)} guardianPids={string.Join(',', guardsAfter)} " +
                    $"processCount={familyAfter.Count} maxIdleGuards=1");
            }
        }
        finally { N.DeleteObject(original); N.DeleteObject(current); }
    }

    private static HashSet<int> GuardianIds(Dictionary<int, uint> family) => family
        .Where(pair => pair.Key != _mainProcessId && pair.Value == _mainProcessId)
        .Select(pair => pair.Key).ToHashSet();

    private static bool IsBoundedGuardianFamily(Dictionary<int, uint> family) =>
        family.ContainsKey(_mainProcessId) && family.Count <= 2 &&
        family.All(pair => pair.Key == _mainProcessId || pair.Value == _mainProcessId);

    private static void WaitForIdleGuardian(string description) =>
        Wait(() => IsBoundedGuardianFamily(ReadWinCalFamily()), 4000, description);

    private static Dictionary<int, uint> ReadWinCalFamily()
    {
        var winCalIds = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("WinCal"))
            using (process) winCalIds.Add(process.Id);
        var result = new Dictionary<int, uint>();
        IntPtr snapshot = N.CreateToolhelp32Snapshot(2, 0);
        Check(snapshot != new IntPtr(-1), "Cannot inspect WinCal guardian parent identities.");
        try
        {
            var entry = new N.PROCESSENTRY32 { Size = (uint)Marshal.SizeOf<N.PROCESSENTRY32>() };
            Check(N.Process32First(snapshot, ref entry), "Cannot enumerate WinCal guardian process identities.");
            do
            {
                if (winCalIds.Contains((int)entry.Id)) result[(int)entry.Id] = entry.ParentId;
            } while (N.Process32Next(snapshot, ref entry));
        }
        finally { N.CloseHandle(snapshot); }
        return result;
    }

    private static void CrashProduct(int processId, Layout layout)
    {
        N.GetWindowThreadProcessId(_popup, out uint popupPid);
        Check(popupPid == processId, "Crash target does not own the verified WinCal popup.");
        using var product = Process.GetProcessById(processId);
        Check(product.ProcessName == "WinCal", "Unexpected crash target process.");
        var guardiansBeforeCrash = GuardianIds(ReadWinCalFamily());
        var current = N.CreateRectRgn(0, 0, 0, 0);
        try
        {
            var t = Stopwatch.StartNew();
            product.Kill();
            Check(product.WaitForExit(3000), "Product did not exit.");
            Wait(() => N.GetWindowRgn(_host, current) != 0 && N.EqualRgn(current, _originalRegion),
                4000, "guardian must restore the exact original native region after main process crash");
            Wait(() =>
            {
                var remaining = ReadWinCalFamily();
                return !remaining.Keys.Any(guardiansBeforeCrash.Contains) &&
                    remaining.All(pair => pair.Value != processId);
            }, 4000, "all active and idle guardians must exit after the main process crashes");
            Log($"CRASH realMainTerminated=True exactOriginalRestored=True allGuardiansExited=True elapsedMs={t.ElapsedMilliseconds}");
            Hits(layout.Calendar, _host, "calendar_restored_after_crash");
            Hits(layout.Notification, _host, "notifications_after_crash");
            NativeForeground();
        }
        finally { N.DeleteObject(current); }
    }

    private static void ExerciseNotifications(Rect notification)
    {
        var header = Uia(() => NotificationRoot().FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "NotificationsTextBlock"))?.Current.BoundingRectangle ?? Rect.Empty);
        Check(notification.Contains(header), "Notification header is unavailable; refusing an arbitrary notification click.");
        Click(header, _host);
        NativeForeground();
        Check(ReadPopup(_popup)?.Month == _month, "Notification header click affected WinCal.");
        var before = ReadNotificationScroll();
        try
        {
            Wheel(notification, before?.Vertical >= 95 ? 120 : -120, _host);
            Thread.Sleep(300);
            NativeForeground();
            Check(ReadPopup(_popup)?.Month == _month, "Wheel over notifications reached WinCal.");
            var after = ReadNotificationScroll();
            bool moved = before is not null && after is not null && Math.Abs(before.Value.Vertical - after.Value.Vertical) > .01;
            Log($"NOTIFICATION headerClick=True nativePreserved=True scrollable={before is not null} scrollMoved={moved}");
            if (before is not null) Check(moved, "Scrollable notifications did not receive the physical wheel input.");
        }
        finally
        {
            if (before is { } original) Uia(() =>
            {
                var pattern = NotificationScroll();
                pattern?.SetScrollPercent(original.Horizontal, original.Vertical);
                return true;
            });
        }
        Hits(notification, _host, "notification_after_input");
    }

    private static AutomationElement NotificationRoot() => AutomationElement.FromHandle(_host).FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, "NotificationCenterGrid"))
        ?? throw new InvalidOperationException("Notification region disappeared.");

    private static ScrollPattern? NotificationScroll()
    {
        var root = NotificationRoot();
        foreach (AutomationElement element in root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true)))
            if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) && ((ScrollPattern)pattern).Current.VerticallyScrollable)
                return (ScrollPattern)pattern;
        return null;
    }

    private static (double Horizontal, double Vertical)? ReadNotificationScroll() => Uia(() =>
    {
        var pattern = NotificationScroll();
        return pattern is null ? ((double Horizontal, double Vertical)?)null :
            (pattern.Current.HorizontalScrollPercent, pattern.Current.VerticalScrollPercent);
    });

    private static Layout StableLayout()
    {
        var timeout = Stopwatch.StartNew();
        Rect previous = Rect.Empty;
        while (timeout.ElapsedMilliseconds < 12000)
        {
            IntPtr host = Host();
            if (host != IntPtr.Zero)
            {
                var parts = Uia(() =>
                {
                    var root = AutomationElement.FromHandle(host);
                    Rect Find(string id) => root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, id))?.Current.BoundingRectangle ?? Rect.Empty;
                    return (Calendar: Find("CalendarCenterGrid"), Notification: Find("NotificationCenterGrid"));
                });
                foreach (var hwnd in Windows().Where(h => Visible(h) && ProcessName(h) == "WinCal"))
                {
                    var target = ReadPopup(hwnd);
                    if (target is null || parts.Calendar.IsEmpty || parts.Notification.IsEmpty) continue;
                    Rect bounds = Bounds(hwnd);
                    var center = new N.POINT { X = (int)(bounds.Left + bounds.Width / 2), Y = (int)(bounds.Top + bounds.Height / 2) };
                    if (Close(bounds, parts.Calendar) && Close(bounds, previous) &&
                        N.GetAncestor(N.WindowFromPoint(center), 2) == hwnd)
                        return new Layout(host, target, parts.Calendar, parts.Notification);
                    previous = bounds;
                }
            }
            Thread.Sleep(200);
        }
        throw new InvalidOperationException("No unobstructed stable WinCal overlay matching CalendarCenterGrid within 12 seconds.");
    }

    private static Target? ReadPopup(IntPtr hwnd) => Uia(() =>
    {
        if (!Visible(hwnd) || ProcessName(hwnd) != "WinCal") return (Target?)null;
        var root = AutomationElement.FromHandle(hwnd);
        AutomationElement? Find(string id) => root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        var next = Find("NextButton"); var previous = Find("PrevButton"); var title = Find("MonthTitle");
        return next is null || previous is null || title is null ? null :
            new Target(hwnd, next.Current.BoundingRectangle, previous.Current.BoundingRectangle, title.Current.Name);
    });

    private static void WaitMonth(Func<string, bool> condition) =>
        Wait(() => condition((ReadPopup(_popup) ?? throw new InvalidOperationException("Popup disappeared.")).Month),
            3000, "physical click must change/restore month");

    private static void Hits(Rect area, IntPtr expected, string label)
    {
        Check(!area.IsEmpty && area.Width > 0 && area.Height > 0, $"Missing {label} bounds.");
        double x = Math.Min(16, area.Width / 5), y = Math.Min(16, area.Height / 5);
        var points = new[] { new Point(area.X + area.Width / 2, area.Y + area.Height / 2),
            new Point(area.Left + x, area.Top + y), new Point(area.Right - x, area.Top + y),
            new Point(area.Left + x, area.Bottom - y), new Point(area.Right - x, area.Bottom - y) };
        bool all = true;
        foreach (var p in points)
        {
            var point = new N.POINT { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) };
            var root = N.GetAncestor(N.WindowFromPoint(point), 2);
            bool matched = root == expected; all &= matched;
            Log($"HIT {label} point=({point.X},{point.Y}) root=0x{root.ToInt64():X} matched={matched}");
        }
        Check(all, $"{label}: actual desktop hit testing found an obstructing or wrong window.");
    }

    private static void Click(Rect bounds, IntPtr expected = default)
    {
        if (expected == IntPtr.Zero) expected = _popup;
        Check(!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0, "Button has no clickable bounds.");
        int x = (int)Math.Round(bounds.X + bounds.Width / 2), y = (int)Math.Round(bounds.Y + bounds.Height / 2);
        Check(N.GetAncestor(N.WindowFromPoint(new N.POINT { X = x, Y = y }), 2) == expected,
            "Button is covered; refusing to click another window.");
        int left = N.GetSystemMetrics(76), top = N.GetSystemMetrics(77);
        Mouse((int)Math.Round((x - left) * 65535d / Math.Max(1, N.GetSystemMetrics(78) - 1)),
            (int)Math.Round((y - top) * 65535d / Math.Max(1, N.GetSystemMetrics(79) - 1)), 0xC001);
        Thread.Sleep(70);
        Check(N.GetCursorPos(out var point) && bounds.Contains(point.X, point.Y), "Cursor missed button.");
        Check(N.GetAncestor(N.WindowFromPoint(point), 2) == expected, "Button became obstructed before mouse down.");
        Mouse(0, 0, 2);
        try { Thread.Sleep(60); } finally { Mouse(0, 0, 4); }
        Thread.Sleep(150);
    }

    private static string NextMonth(string month)
    {
        var date = DateTime.ParseExact(month, "yyyy年M月", System.Globalization.CultureInfo.InvariantCulture).AddMonths(1);
        return $"{date.Year}年{date.Month}月";
    }

    private static void Wheel(Rect bounds, int delta, IntPtr expected)
    {
        int x = (int)(bounds.Left + bounds.Width / 2), y = (int)(bounds.Top + bounds.Height / 3);
        Check(N.GetAncestor(N.WindowFromPoint(new N.POINT { X = x, Y = y }), 2) == expected,
            "Calendar wheel target is obstructed.");
        Check(N.SetCursorPos(x, y), "Could not position wheel input.");
        Thread.Sleep(80);
        var input = new[] { new N.INPUT { Data = new N.UNION { Mouse = new N.MOUSE { Flags = 0x800, Data = unchecked((uint)delta) } } } };
        Check(N.SendInput(1, input, Marshal.SizeOf<N.INPUT>()) == 1, "Wheel input failed.");
        Thread.Sleep(150);
    }

    private static void Mouse(int x, int y, uint flags)
    {
        var input = new[] { new N.INPUT { Data = new N.UNION { Mouse = new N.MOUSE { X = x, Y = y, Flags = flags } } } };
        Check(N.SendInput(1, input, Marshal.SizeOf<N.INPUT>()) == 1, $"SendInput failed: {Marshal.GetLastWin32Error()}.");
        Log($"INPUT flags=0x{flags:X} position=({x},{y}) foreground=0x{N.GetForegroundWindow():X}");
    }

    private static void NativeForeground()
    {
        Check(Visible(_host), "Native notifications closed during WinCal interaction.");
        IntPtr foreground = N.GetForegroundWindow();
        Check(foreground == _host, $"Native panel lost foreground during WinCal interaction: hwnd=0x{foreground.ToInt64():X}, process={ProcessName(foreground)}.");
    }

    private static void Clock() => Uia(() =>
    {
        IntPtr taskbar = N.FindWindow("Shell_TrayWnd", null);
        Check(taskbar != IntPtr.Zero, "Primary taskbar unavailable.");
        var root = AutomationElement.FromHandle(taskbar);
        var matches = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.OmniButton"));
        var choices = new List<(AutomationElement Element, string Id, double Right)>();
        foreach (AutomationElement element in matches)
        {
            var current = element.Current; var rect = current.BoundingRectangle;
            if (!current.IsOffscreen && !rect.IsEmpty && rect.Width > 10 && rect.Height > 10 && current.ClassName == "SystemTray.OmniButton")
                choices.Add((element, current.AutomationId, rect.Right));
        }
        var clock = choices.OrderByDescending(c => c.Id == "SystemTrayIcon").ThenByDescending(c => c.Right).FirstOrDefault();
        Check(clock.Element is not null, "Exact taskbar clock unavailable.");
        Check(clock.Element!.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern), "Clock has no InvokePattern.");
        ((InvokePattern)pattern).Invoke();
        return true;
    });

    private static IntPtr Host() => Windows().Where(h => Visible(h) && ClassName(h) == "Windows.UI.Core.CoreWindow" &&
        ProcessName(h) is "ShellExperienceHost" or "ShellHost" && Bounds(h).Width > 200 && Bounds(h).Height > 100)
        .OrderByDescending(h => h == N.GetForegroundWindow()).FirstOrDefault();

    private static HashSet<IntPtr> Windows()
    {
        var handles = new HashSet<IntPtr>();
        N.EnumWindows((h, _) => { handles.Add(h); return true; }, IntPtr.Zero);
        var foreground = N.GetForegroundWindow(); if (foreground != IntPtr.Zero) handles.Add(foreground);
        foreach (string name in new[] { "ShellExperienceHost", "ShellHost" })
        foreach (var process in Process.GetProcessesByName(name))
        using (process)
        {
            try { foreach (ProcessThread thread in process.Threads)
                N.EnumThreadWindows((uint)thread.Id, (h, _) => { handles.Add(h); return true; }, IntPtr.Zero); }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
        }
        return handles;
    }

    private static bool Visible(IntPtr h) => h != IntPtr.Zero && N.IsWindowVisible(h) &&
        N.DwmGetWindowAttribute(h, 14, out uint cloak, sizeof(uint)) == 0 && cloak == 0;
    private static Rect Bounds(IntPtr h) => N.GetWindowRect(h, out var r) && r.Right > r.Left && r.Bottom > r.Top ?
        new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : Rect.Empty;
    private static string ProcessName(IntPtr h)
    {
        N.GetWindowThreadProcessId(h, out uint pid);
        try { using var process = Process.GetProcessById((int)pid); return process.ProcessName; } catch { return "unavailable"; }
    }
    private static string ClassName(IntPtr h) { var s = new StringBuilder(256); N.GetClassName(h, s, s.Capacity); return s.ToString(); }
    private static bool Close(Rect a, Rect b) => !a.IsEmpty && !b.IsEmpty && Math.Abs(a.X - b.X) <= 2 &&
        Math.Abs(a.Y - b.Y) <= 2 && Math.Abs(a.Width - b.Width) <= 2 && Math.Abs(a.Height - b.Height) <= 2;
    private static void Wait(Func<bool> condition, int milliseconds, string description)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { if (condition()) return; Thread.Sleep(100); }
        throw new InvalidOperationException($"Timed out: {description}.");
    }
    private static T Uia<T>(Func<T> action) => Task.Run(action).WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Log(string message) { string line = $"{DateTime.Now:HH:mm:ss.fff} {message}"; _log?.WriteLine(line); Console.WriteLine(line); }
    private static string ProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null) { if (File.Exists(Path.Combine(dir.FullName, "LiveNotificationOverlayCheck.csproj"))) return dir.FullName; dir = dir.Parent; }
        throw new DirectoryNotFoundException("Live test project directory unavailable.");
    }

    private static class N
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint Type; public UNION Data; }
        [StructLayout(LayoutKind.Explicit)] public struct UNION { [FieldOffset(0)] public MOUSE Mouse; [FieldOffset(0)] public KEY Key; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSE { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] public struct KEY { public ushort KeyCode, Scan; public uint Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct PROCESSENTRY32
        {
            public uint Size, Usage, Id; public UIntPtr Heap; public uint Module, Threads, ParentId;
            public int Priority; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string File;
        }
        public delegate bool Callback(IntPtr hwnd, IntPtr data);
        [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr data);
        [DllImport("user32.dll")] public static extern bool EnumThreadWindows(uint thread, Callback callback, IntPtr data);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string? name);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out uint value, int size);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] input, int size);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] public static extern bool EqualRgn(IntPtr first, IntPtr second);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr region);
        [DllImport("kernel32.dll")] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] public static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] public static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    }
}
