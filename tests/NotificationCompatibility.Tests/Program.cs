using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WinCal.Core.Helpers;

// This executable tests geometry and a hidden HWND; it never shows a WPF window.
// It catches overlap, wrong-side placement, zero-clamping on other monitors,
// off-screen vertical placement, oversized panels, and accidental double scaling.
internal static class Program
{
    private const double Tolerance = 0.001;
    private static Func<Rect, Rect, Size, Rect>? _calculate;
    private static Func<Rect, Rect, Rect, Rect>? _calculateOverlay;

    [STAThread]
    private static int Main()
    {
        var method = typeof(WindowPositionHelper).GetMethod(
            "CalculateAlongsidePosition", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(Rect), typeof(Rect), typeof(Size) }, modifiers: null);
        if (method is null || method.ReturnType != typeof(Rect))
        {
            Console.Error.WriteLine("FAIL placement API: expected public static Rect " +
                "WindowPositionHelper.CalculateAlongsidePosition(Rect nativePanelBounds, Rect workArea, Size desiredSize).");
            return 1;
        }

        _calculate = method.CreateDelegate<Func<Rect, Rect, Size, Rect>>();
        var overlayMethod = typeof(WindowPositionHelper).GetMethod(
            "CalculateCalendarOverlayPosition", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(Rect), typeof(Rect), typeof(Rect) }, modifiers: null);
        if (overlayMethod is null || overlayMethod.ReturnType != typeof(Rect))
        {
            Console.Error.WriteLine("FAIL calendar overlay API: expected public static Rect " +
                "WindowPositionHelper.CalculateCalendarOverlayPosition(Rect calendarBounds, Rect notificationBounds, Rect hostBounds).");
            return 1;
        }
        _calculateOverlay = overlayMethod.CreateDelegate<Func<Rect, Rect, Rect, Rect>>();

        var cases = new (string Name, Action Run)[]
        {
            ("right native panel leaves miniCal on its left", RightNativePanel),
            ("left native panel leaves miniCal on its right", LeftNativePanel),
            ("negative X monitor retains negative coordinates", NegativeXMonitor),
            ("work area with taskbar at top contains miniCal", PositiveWorkAreaTop),
            ("monitor above primary retains negative Y", NegativeYMonitor),
            ("oversized height is limited to work area", OversizedHeight),
            ("narrow work area keeps complete panel on screen", NarrowWorkArea),
            ("oversized width is limited to work area", OversizedWidth),
            ("scaled desired dimensions use the same coordinate units", ScaledDimensions),
            ("hidden HWND prevents activation without changing foreground", HiddenWindowPreventsActivation),
            ("overlay exactly covers observed calendar and preserves notifications", ObservedCalendarOverlay),
            ("overlay preserves negative monitor coordinates", NegativeCoordinateOverlay),
            ("overlay preserves already scaled physical dimensions", ScaledCalendarOverlay),
            ("overlay rejects empty or zero-sized regions", EmptyOverlayRegions),
            ("overlay rejects calendar extending outside host", CalendarOutsideHost),
            ("overlay rejects notification extending outside host", NotificationOutsideHost),
            ("overlay rejects calendar overlapping notification", OverlappingOverlayRegions),
            ("overlay rejects non-finite region coordinates", NonFiniteOverlayRegions),
            ("calendar placement cache preserves validated coordinates", SystemCalendarPlacementCacheTests.PreservesPhysicalCoordinates),
            ("calendar placement cache rejects changed host and display state", SystemCalendarPlacementCacheTests.RejectsEnvironmentChanges),
            ("calendar placement cache clears unavailable native state", SystemCalendarPlacementCacheTests.ClearsUnavailableEnvironment),
            ("calendar placement cache rejects invalid calendar bounds", SystemCalendarPlacementCacheTests.RejectsInvalidBounds),
            ("calendar placement cache supports host-scoped and global invalidation", SystemCalendarPlacementCacheTests.InvalidatesEntries),
            ("calendar placement cache holds only the latest host", SystemCalendarPlacementCacheTests.ReplacesPreviousHost),
            ("opening and transient zero foreground do not dismiss", SystemPanelDismissalStateTests.OpeningAndZeroForegroundDoNotDismiss),
            ("focus loss starts one outgoing animation", SystemPanelDismissalStateTests.FocusLossStartsOnlyOneAnimation),
            ("native hide finishes dismissal exactly once", SystemPanelDismissalStateTests.NativeHideEndsDismissalOnce),
            ("outgoing animation preserves hole until native hide", SystemPanelDismissalStateTests.AnimationKeepsHoleUntilNativeHide),
            ("foreground return invalidates old animation", SystemPanelDismissalStateTests.ForegroundReturnCancelsOldAnimation),
            ("foreground return restores completed outgoing animation", SystemPanelDismissalStateTests.ForegroundReturnAfterAnimationStillRestores),
            ("disabled animations close immediately", SystemPanelDismissalStateTests.DisabledAnimationsHideImmediately),
            ("new session ignores old animation completion", SystemPanelDismissalStateTests.NewSessionRejectsOldCompletion)
        };

        var failures = 0;
        foreach (var test in cases)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"{cases.Length - failures}/{cases.Length} compatibility checks passed.");
        return failures == 0 ? 0 : 1;
    }

    private static Rect Calculate(Rect native, Rect work, Size size)
    {
        var result = _calculate!(native, work, size);
        Check(!result.IsEmpty && double.IsFinite(result.X) && double.IsFinite(result.Y) &&
            double.IsFinite(result.Width) && double.IsFinite(result.Height), "placement must be finite and nonempty");
        Check(result.Width > 0 && result.Height > 0, "placement must have positive dimensions");
        Check(result.Left >= work.Left - Tolerance && result.Top >= work.Top - Tolerance &&
            result.Right <= work.Right + Tolerance && result.Bottom <= work.Bottom + Tolerance,
            $"placement {result} must be inside work area {work}");
        return result;
    }

    private static void RightNativePanel()
    {
        var native = new Rect(1500, 80, 420, 960);
        var result = Calculate(native, new Rect(0, 0, 1920, 1040), new Size(340, 560));
        Check(result.Right <= native.Left - 8, "miniCal must sit left of native with at least 8 units gap");
        Equal(340, result.Width, "available width must preserve requested width");
        Equal(560, result.Height, "available height must preserve requested height");
    }

    private static void LeftNativePanel()
    {
        var native = new Rect(0, 80, 420, 960);
        var result = Calculate(native, new Rect(0, 0, 1920, 1040), new Size(340, 560));
        Check(result.Left >= native.Right + 8, "miniCal must use right-side space with at least 8 units gap");
        Equal(340, result.Width, "right-side placement must retain requested width");
    }

    private static void NegativeXMonitor()
    {
        var native = new Rect(-420, 80, 420, 960);
        var result = Calculate(native, new Rect(-1920, 0, 1920, 1040), new Size(340, 560));
        Check(result.Right <= native.Left - 8, "miniCal must remain left of native on the negative-X monitor");
        Check(result.Left < 0 && result.Right < 0, "valid negative monitor coordinates must not clamp to zero");
    }

    private static void PositiveWorkAreaTop()
    {
        var result = Calculate(new Rect(1500, 160, 420, 920),
            new Rect(0, 80, 1920, 1000), new Size(340, 1200));
        Check(result.Top >= 80, "panel must not cover the reserved area above the work area");
        Check(result.Height <= 1000, "height must use work-area height rather than absolute bottom");
    }

    private static void NegativeYMonitor()
    {
        var result = Calculate(new Rect(1500, -1000, 420, 960),
            new Rect(0, -1080, 1920, 1040), new Size(340, 560));
        Check(result.Top < 0 && result.Bottom <= -40, "panel must stay on the monitor above primary");
    }

    private static void OversizedHeight()
    {
        var result = Calculate(new Rect(1500, 0, 420, 600),
            new Rect(0, 0, 1920, 600), new Size(340, 1200));
        Equal(340, result.Width, "height limiting must not shrink a fitting width");
        Check(result.Height <= 600, "tall panel must be limited to the work area");
    }

    private static void NarrowWorkArea()
    {
        // Both requested panels cannot fit side by side; containment remains mandatory.
        var result = Calculate(new Rect(100, 0, 400, 600),
            new Rect(0, 0, 500, 600), new Size(340, 560));
        Check(result.Width <= 500 && result.Height <= 600, "narrow fallback must remain entirely visible");
    }

    private static void OversizedWidth()
    {
        var result = Calculate(new Rect(-200, 0, 200, 400),
            new Rect(-300, 0, 300, 400), new Size(700, 700));
        Check(result.Width <= 300 && result.Height <= 400, "both oversized dimensions must fit the work area");
    }

    private static void ScaledDimensions()
    {
        // Physical coordinates and a 150%-scaled popup use one common unit; the
        // geometry function must not apply an additional DPI multiplier.
        var native = new Rect(3780, 120, 660, 1320);
        var result = Calculate(native, new Rect(1920, 0, 2520, 1440), new Size(510, 840));
        Equal(510, result.Width, "150%-scaled width must be used exactly once");
        Equal(840, result.Height, "150%-scaled height must be used exactly once");
        Check(result.Right <= native.Left - 8, "scaled popup must still fit beside native");
    }

    private static void HiddenWindowPreventsActivation()
    {
        var window = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
        var foreground = GetForegroundWindow();
        try
        {
            WindowActivationHelper.PreventActivation(window);
            Check(!window.ShowActivated, "window must opt out of activation before HWND creation");

            // EnsureHandle fires SourceInitialized without displaying the window.
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            Check(hwnd != IntPtr.Zero, "hidden window must have a real HWND");
            Check(!window.IsVisible && !IsWindowVisible(hwnd), "the test window must remain hidden");
            Check((GetWindowLongPtr(hwnd, -20).ToInt64() & 0x08000000L) != 0,
                "SourceInitialized must set WS_EX_NOACTIVATE on the real HWND");

            // WM_MOUSEACTIVATE with HTCLIENT and WM_LBUTTONDOWN should keep the
            // click while preventing activation, unlike MA_NOACTIVATEANDEAT.
            var result = SendMessage(hwnd, 0x0021, IntPtr.Zero, new IntPtr(0x02010001));
            Check(result == new IntPtr(3), "WM_MOUSEACTIVATE must return MA_NOACTIVATE (3)");
            Check(GetForegroundWindow() == foreground, "creating and messaging the hidden HWND must not change foreground");
            Check(!window.IsVisible && !IsWindowVisible(hwnd), "messaging the window must not display it");
        }
        finally
        {
            window.Close();
        }
    }

    private static void ObservedCalendarOverlay()
    {
        var calendar = new Rect(2125, 770, 420, 595);
        var notification = new Rect(2125, 10, 420, 745);
        var actual = _calculateOverlay!(calendar, notification, new Rect(2110, 0, 450, 1380));
        Check(actual == calendar, "overlay must match the whole observed calendar exactly, without shifting or shrinking");
        Check(!actual.IntersectsWith(notification), "overlay must leave all notification content uncovered");
    }

    private static void NegativeCoordinateOverlay()
    {
        var calendar = new Rect(-435, -610, 420, 595);
        var actual = _calculateOverlay!(calendar, new Rect(-435, -1370, 420, 745),
            new Rect(-450, -1380, 450, 1380));
        Check(actual == calendar, "negative X/Y screen coordinates must be preserved exactly");
    }

    private static void ScaledCalendarOverlay()
    {
        var calendar = new Rect(3187.5, 1155, 630, 892.5);
        var actual = _calculateOverlay!(calendar, new Rect(3187.5, 15, 630, 1117.5),
            new Rect(3165, 0, 675, 2070));
        Check(actual == calendar, "already scaled physical coordinates must not be scaled or rounded again");
    }

    private static void EmptyOverlayRegions()
    {
        var calendar = new Rect(2125, 770, 420, 595);
        var notification = new Rect(2125, 10, 420, 745);
        var host = new Rect(2110, 0, 450, 1380);
        Check(_calculateOverlay!(Rect.Empty, notification, host).IsEmpty, "missing calendar must not produce an overlay");
        Check(_calculateOverlay!(calendar, Rect.Empty, host).IsEmpty, "missing notification region must fail safely");
        Check(_calculateOverlay!(calendar, notification, Rect.Empty).IsEmpty, "missing host must not produce an overlay");
        Check(_calculateOverlay!(new Rect(2125, 770, 0, 595), notification, host).IsEmpty,
            "zero-width calendar must not produce an overlay");
        Check(_calculateOverlay!(calendar, new Rect(2125, 10, 420, 0), host).IsEmpty,
            "zero-height notification must not produce an overlay");
        Check(_calculateOverlay!(calendar, notification, new Rect(2110, 0, 0, 1380)).IsEmpty,
            "zero-width host must not produce an overlay");
    }

    private static void CalendarOutsideHost()
    {
        var notification = new Rect(2125, 10, 420, 745);
        var host = new Rect(2110, 0, 450, 1380);
        Check(_calculateOverlay!(new Rect(2125, 770, 440, 595), notification, host).IsEmpty,
            "calendar crossing the host's right edge must not be clipped into an overlay");
        Check(_calculateOverlay!(new Rect(2125, 1000, 420, 595), notification, host).IsEmpty,
            "calendar below the host/screen region must not be shown off screen");
        Check(_calculateOverlay!(new Rect(-9000, 770, 420, 595), notification, host).IsEmpty,
            "stale calendar coordinates on another screen must not produce an overlay");
    }

    private static void NotificationOutsideHost()
    {
        Check(_calculateOverlay!(new Rect(2125, 770, 420, 595), new Rect(2125, -10, 420, 745),
            new Rect(2110, 0, 450, 1380)).IsEmpty,
            "untrusted notification bounds outside the host must fail safely");
    }

    private static void OverlappingOverlayRegions()
    {
        Check(_calculateOverlay!(new Rect(2125, 740, 420, 595), new Rect(2125, 10, 420, 745),
            new Rect(2110, 0, 450, 1380)).IsEmpty,
            "calendar intersecting notification content must not produce an overlay");
    }

    private static void NonFiniteOverlayRegions()
    {
        var calendar = new Rect(2125, 770, 420, 595);
        var notification = new Rect(2125, 10, 420, 745);
        var host = new Rect(2110, 0, 450, 1380);
        Check(_calculateOverlay!(new Rect(double.NaN, 770, 420, 595), notification, host).IsEmpty,
            "non-finite calendar bounds must fail safely");
        Check(_calculateOverlay!(calendar, new Rect(2125, 10, double.PositiveInfinity, 745), host).IsEmpty,
            "non-finite notification bounds must fail safely");
        Check(_calculateOverlay!(calendar, notification, new Rect(double.NegativeInfinity, 0, 450, 1380)).IsEmpty,
            "non-finite host bounds must fail safely");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private static void Equal(double expected, double actual, string message) =>
        Check(Math.Abs(expected - actual) <= Tolerance, $"{message}: expected {expected}, got {actual}");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
