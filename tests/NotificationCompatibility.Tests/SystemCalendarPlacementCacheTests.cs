using System.Windows;
using WinCal.Core.Helpers;

internal static class SystemCalendarPlacementCacheTests
{
    private static readonly IntPtr Host = new(101);
    private static readonly Rect Calendar = new(-630, -700, 600, 620);

    private static CalendarPlacementEnvironment Baseline => new(
        Host, 20, 30, 40, new Rect(-650, -1300, 650, 1300),
        new IntPtr(50), new Rect(-2560, -1440, 2560, 1440),
        new Rect(-2560, -1440, 2560, 1400), 192, "original-region");

    internal static void PreservesPhysicalCoordinates()
    {
        int reads = 0;
        var cache = new SystemCalendarPlacementCache(_ => { reads++; return Baseline; });
        Check(!cache.TryGet(Host, out _), "empty cache must miss");
        Check(!TryGetPlacement(cache, Host, out _), "empty placement cache must miss");
        Check(reads == 0, "empty cache must not query native metadata");
        Check(cache.Remember(Host, Calendar), "validated negative-coordinate placement must be remembered");
        Check(cache.TryGet(Host, out var result), "unchanged native environment must hit");
        Check(result == Calendar, "physical high-DPI coordinates must be returned without additional scaling");
        Check(TryGetPlacement(cache, Host, out var placement) && placement == Calendar,
            "placement lookup must preserve validated physical coordinates");
    }

    internal static void RejectsEnvironmentChanges()
    {
        var changes = new (string Name, CalendarPlacementEnvironment Environment)[]
        {
            ("recreated HWND", Baseline with { Host = new IntPtr(102) }),
            ("recreated process", Baseline with { ProcessStartTicks = 41 }),
            ("process ID", Baseline with { ProcessId = 21 }),
            ("thread ID", Baseline with { ThreadId = 31 }),
            ("host position", Baseline with { HostBounds = new Rect(-649, -1300, 650, 1300) }),
            ("host dimensions", Baseline with { HostBounds = new Rect(-650, -1300, 651, 1300) }),
            ("monitor identity", Baseline with { Monitor = new IntPtr(51) }),
            ("monitor dimensions", Baseline with { MonitorBounds = new Rect(-2560, -1440, 2560, 1441) }),
            ("work area", Baseline with { WorkArea = new Rect(-2560, -1440, 2560, 1399) }),
            ("DPI", Baseline with { Dpi = 144 })
        };
        foreach (var change in changes)
        foreach (bool placementFirst in new[] { false, true })
        {
            CalendarPlacementEnvironment? current = Baseline;
            var cache = new SystemCalendarPlacementCache(_ => current);
            Check(cache.Remember(Host, Calendar), "baseline must be cacheable");
            current = change.Environment;
            if (placementFirst)
                Check(!TryGetPlacement(cache, Host, out _), $"{change.Name} must invalidate a placement lookup");
            else
                Check(!cache.TryGet(Host, out _), $"{change.Name} must invalidate a strict lookup");
            current = Baseline;
            Check(!cache.TryGet(Host, out _), $"{change.Name} must permanently clear the old entry");
            Check(!TryGetPlacement(cache, Host, out _), $"{change.Name} must not retain stale geometry");
        }
        PreservesTransientRegionChanges();
    }

    private static void PreservesTransientRegionChanges()
    {
        foreach (string fingerprint in new[] { "changed-region", "empty" })
        {
            CalendarPlacementEnvironment? current = Baseline;
            var cache = new SystemCalendarPlacementCache(_ => current);
            Check(cache.Remember(Host, Calendar), "stable native shape must be cacheable");
            current = Baseline with { RegionFingerprint = fingerprint };
            Check(!cache.TryGet(Host, out var strict) && strict.IsEmpty,
                $"transient shape {fingerprint} must never pass the strict region check");
            Check(TryGetPlacement(cache, Host, out var placement) && placement == Calendar,
                $"transient shape {fingerprint} must preserve the validated geometry");
            Check(!cache.TryGet(Host, out _), "a placement read must not replace the stable shape baseline");
            current = Baseline;
            Check(cache.TryGet(Host, out var restored) && restored == Calendar,
                "strict lookup must hit again after the original native shape returns");
        }
    }

    internal static void ClearsUnavailableEnvironment()
    {
        CalendarPlacementEnvironment? current = Baseline;
        var cache = new SystemCalendarPlacementCache(_ => current);
        Check(cache.Remember(Host, Calendar), "baseline must be cacheable");
        current = null;
        Check(!cache.TryGet(Host, out _), "missing or hidden native host metadata must miss");
        current = Baseline;
        Check(!cache.TryGet(Host, out _), "unavailable native state must clear old placement");
    }

    internal static void RejectsInvalidBounds()
    {
        var invalid = new[]
        {
            Rect.Empty, new Rect(-630, -700, 0, 620),
            new Rect(-700, -700, 600, 620),
            new Rect(double.NaN, -700, 600, 620),
            new Rect(-630, -700, double.PositiveInfinity, 620)
        };
        foreach (var bounds in invalid)
        {
            var cache = new SystemCalendarPlacementCache(_ => Baseline);
            Check(cache.Remember(Host, Calendar), "baseline must be cacheable");
            Check(!cache.Remember(Host, bounds), "invalid or out-of-host bounds must be rejected");
            Check(!cache.TryGet(Host, out _), "failed validation must not retain stale placement");
        }
        var zeroDpi = new SystemCalendarPlacementCache(_ => Baseline with { Dpi = 0 });
        Check(!zeroDpi.Remember(Host, Calendar), "unknown DPI must prevent caching");
        CalendarPlacementEnvironment current = Baseline;
        var emptyShape = new SystemCalendarPlacementCache(_ => current);
        Check(emptyShape.Remember(Host, Calendar), "stable shape must initially be remembered");
        current = Baseline with { RegionFingerprint = "empty" };
        Check(!emptyShape.Remember(Host, Calendar), "empty animation shape must not become a stable baseline");
        current = Baseline;
        Check(!TryGetPlacement(emptyShape, Host, out _), "rejecting an empty baseline must clear its old entry");
    }

    internal static void InvalidatesEntries()
    {
        var cache = new SystemCalendarPlacementCache(_ => Baseline);
        Check(cache.Remember(Host, Calendar), "baseline must be cacheable");
        cache.Invalidate(new IntPtr(999));
        Check(cache.TryGet(Host, out _), "invalidating another host must preserve this entry");
        cache.Invalidate(Host);
        Check(!cache.TryGet(Host, out _), "matching host invalidation must clear placement");
        Check(cache.Remember(Host, Calendar), "entry can be remembered again");
        cache.Invalidate();
        Check(!cache.TryGet(Host, out _), "global invalidation must clear placement");
    }

    internal static void ReplacesPreviousHost()
    {
        var second = new IntPtr(102);
        var cache = new SystemCalendarPlacementCache(host => Baseline with { Host = host });
        Check(cache.Remember(Host, Calendar), "first host must be cacheable");
        Check(cache.Remember(second, Calendar), "second host must replace first");
        Check(cache.TryGet(second, out _), "latest host must hit");
        Check(!cache.TryGet(Host, out _), "previous host must miss");
        Check(!cache.TryGet(second, out _), "host mismatch must clear the single entry");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool TryGetPlacement(SystemCalendarPlacementCache cache, IntPtr host, out Rect bounds)
    {
        var method = typeof(SystemCalendarPlacementCache).GetMethod("TryGetPlacement",
            new[] { typeof(IntPtr), typeof(Rect).MakeByRefType() });
        Check(method is not null && method.ReturnType == typeof(bool), "public TryGetPlacement API is required");
        object?[] arguments = { host, Rect.Empty };
        bool found = (bool)method!.Invoke(cache, arguments)!;
        bounds = (Rect)arguments[1]!;
        return found;
    }
}
