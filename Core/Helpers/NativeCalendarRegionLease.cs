using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace WinCal.Core.Helpers;

/// <summary>
/// Temporarily removes a verified calendar rectangle from the native host's window region.
/// A separate instance of this executable holds the original region before any mutation.
/// </summary>
public sealed class NativeCalendarRegionLease : IDisposable
{
    private const string GuardArgument = "--native-calendar-region-guard";
    private const int MaximumFrameBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan IpcTimeout = TimeSpan.FromSeconds(3);
    private static readonly SemaphoreSlim LeaseSlot = new(1, 1);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly object WarmSync = new();
    private static Task<GuardConnection>? WarmTask;
    private static bool WarmStopping;
    private static bool ActiveLease;
    private readonly object _sync = new();
    private RegionSnapshot _snapshot;
    private readonly NamedPipeServerStream _pipe;
    private readonly Process _guard;
    private GuardConnection? _connection;
    private readonly CancellationTokenSource _healthCancellation = new();
    private bool _disposed;
    private bool _healthy;
    private bool _armed;
    private bool _ownsSlot = true;
    private bool _mayHaveChanged;
    private Task? _disposeTask;

    private NativeCalendarRegionLease(RegionSnapshot snapshot, NamedPipeServerStream pipe, Process guard)
    {
        _snapshot = snapshot;
        _pipe = pipe;
        _guard = guard;
    }

    /// <summary>Start one connected, unarmed guardian in the background.</summary>
    public static void WarmGuardian()
    {
        lock (WarmSync)
        {
            if (WarmStopping || ActiveLease || WarmTask is not null) return;
            var task = Task.Run(StartGuardianAsync);
            WarmTask = task;
            _ = ObserveWarmTaskAsync(task);
        }
    }

    /// <summary>Stop the idle guardian pool; an active lease remains responsible for itself.</summary>
    public static void StopWarmGuardian()
    {
        Task<GuardConnection>? task;
        lock (WarmSync)
        {
            WarmStopping = true;
            task = WarmTask;
            WarmTask = null;
        }
        if (task is not null) _ = DisposeIdleConnectionAsync(task);
    }

    /// <summary>Run this before normal application startup; true means this invocation was a guardian.</summary>
    public static bool TryRunGuard(string[] args)
    {
        if (args.Length == 0 || args[0] != GuardArgument) return false;
        try
        {
            if (args.Length != 5 || !int.TryParse(args[3], out int parentId) ||
                !long.TryParse(args[4], out long parentStart)) return true;
            // This entry point intentionally skips WPF, including its DPI initialization.
            // Keep Win32 snapshots in physical pixels on every async worker thread.
            if (!SetProcessDpiAwarenessContext(new IntPtr(-4)) &&
                GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) <= 0)
                return true;
            // A previous main process may have died while its guardian is still restoring.
            // Keep ownership on this synchronous thread across the async guardian lifetime.
            using var regionMutex = new Mutex(false, @"Local\WinCal.NativeRegion");
            bool ownsMutex;
            try { ownsMutex = regionMutex.WaitOne(IpcTimeout); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) return true;
            try { RunGuardAsync(args[1], args[2], parentId, parentStart).GetAwaiter().GetResult(); }
            finally { regionMutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"WinCal native region guard: {ex.GetType().Name}");
        }
        return true;
    }

    public static async Task<NativeCalendarRegionLease?> TryCreateAsync(
        IntPtr host, Rect physicalCalendarBounds, CancellationToken cancellationToken = default,
        bool reuseCachedPlacement = false)
    {
        if (!await LeaseSlot.WaitAsync(0, cancellationToken)) return null;
        lock (WarmSync) ActiveLease = true;
        NativeCalendarRegionLease? lease = null;
        var timing = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SystemCalendarInterceptor.IsSystemPanelVisible(host)) return null;
            if (reuseCachedPlacement)
            {
                var settling = Stopwatch.StartNew();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!SystemCalendarInterceptor.IsSystemPanelVisible(host) ||
                        !SystemCalendarPlacementCache.Shared.TryGetPlacement(host, out var placement) ||
                        placement != physicalCalendarBounds) return null;
                    if (SystemCalendarPlacementCache.Shared.TryGet(host, out var stableBounds) &&
                        stableBounds == physicalCalendarBounds) break;
                    if (settling.ElapsedMilliseconds >= 350)
                    {
                        OverlayTiming.Write($"LEASE shape timeout ms={settling.ElapsedMilliseconds}");
                        return null;
                    }
                    await Task.Delay(16, cancellationToken);
                }
                OverlayTiming.Write($"LEASE shape stable ms={settling.ElapsedMilliseconds}");
            }
            if (!TryCaptureSnapshot(host, out var snapshot) || !ValidBounds(snapshot!, physicalCalendarBounds)) return null;
            Task<GuardConnection> connectionTask;
            lock (WarmSync)
            {
                if (WarmStopping) return null;
                // A failed prewarm has already closed its pipe and awaited its child exit.
                connectionTask = WarmTask is { IsFaulted: false, IsCanceled: false } ready
                    ? ready : Task.Run(StartGuardianAsync);
                WarmTask = null;
            }
            // Do not abandon a startup task when this preparation is cancelled. Transfer the
            // completed connection to the lease first, so disposal always closes its pipe.
            GuardConnection connection = await connectionTask;
            if (++connection.SessionCount > 1)
                OverlayTiming.Write($"GUARD_REUSED guard={connection.Guard.Id} session={connection.SessionCount}");
            OverlayTiming.Write($"LEASE connected guard={connection.Guard.Id} ms={timing.ElapsedMilliseconds}");
            var pipe = connection.Pipe;
            var guard = connection.Guard;
            lease = new NativeCalendarRegionLease(snapshot!, pipe, guard) { _connection = connection };
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(IpcTimeout);
            // Once ARM may have reached the child, it can restore on EOF even if its reply is lost.
            // Keep the lease slot until that child disarms or exits in every handshake failure path.
            lock (lease._sync) lease._armed = true;
            await WriteFrameAsync(pipe, new GuardMessage { Command = "ARM", Token = connection.Token, Snapshot = snapshot }, timeout.Token);
            var acknowledgement = await ReadFrameAsync(pipe, timeout.Token);
            OverlayTiming.Write($"LEASE armed guard={guard.Id} ms={timing.ElapsedMilliseconds}");
            if (acknowledgement?.Command != "ARMED" || acknowledgement.Token != connection.Token || guard.HasExited ||
                acknowledgement.Snapshot is not { } freshSnapshot || !MatchesIdentity(freshSnapshot) ||
                freshSnapshot.Host != snapshot!.Host || freshSnapshot.ProcessStartTicks != snapshot.ProcessStartTicks)
                throw new IOException("Guardian did not arm.");

            lock (lease._sync)
            {
                lease._armed = true;
                lease._healthy = true;
                lease._snapshot = freshSnapshot;
            }
            if (!SystemCalendarInterceptor.IsSystemPanelVisible(host) || !OriginalRegionMatches(freshSnapshot))
            {
                lease.Dispose();
                return null;
            }
            // A repeated open only checks the cached native identity/geometry/shape here;
            // it does not wait for another UIA layout query. New layouts are still verified.
            Rect? currentBounds = reuseCachedPlacement
                ? SystemCalendarPlacementCache.Shared.TryGet(host, out var cachedBounds) ? cachedBounds : null
                : await SystemCalendarRegionLocator.FindAsync(host).WaitAsync(timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (currentBounds != physicalCalendarBounds)
            {
                lease.Dispose();
                return null;
            }
            if (!lease.Update(physicalCalendarBounds))
            {
                lease.Dispose();
                return null;
            }
            _ = lease.MonitorGuardAsync();
            return lease;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"WinCal native region lease creation failed: {ex.GetType().Name}");
            if (lease != null) lease.Dispose();
            return null;
        }
        finally
        {
            if (lease is null) CompleteLease();
        }
    }

    private static async Task<GuardConnection> StartGuardianAsync()
    {
        var timing = Stopwatch.StartNew();
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
            throw new IOException("Guardian executable is unavailable.");
        string token = Guid.NewGuid().ToString("N");
        string pipeName = $"WinCal.NativeRegion.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? guard = null;
        try
        {
            using var parent = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add(GuardArgument);
            start.ArgumentList.Add(pipeName);
            start.ArgumentList.Add(token);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            guard = Process.Start(start) ?? throw new IOException("Guardian did not start.");
            OverlayTiming.Write($"WARM started guard={guard.Id}");
            WarmMessageSerialization();
            using var timeout = new CancellationTokenSource(IpcTimeout);
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint clientPid) || clientPid != guard.Id || guard.HasExited)
                throw new IOException("Unexpected guardian connection.");
            OverlayTiming.Write($"WARM ready guard={guard.Id} ms={timing.ElapsedMilliseconds}");
            return new GuardConnection(pipe, guard, token);
        }
        catch
        {
            // No ARM was sent, so EOF can only shut down an idle child. Confirm exit before
            // permitting a replacement guardian to contend for the global region mutex.
            pipe.Dispose();
            if (guard is not null)
            {
                try { await guard.WaitForExitAsync().ConfigureAwait(false); }
                finally { guard.Dispose(); }
            }
            throw;
        }
    }

    private static async Task ObserveWarmTaskAsync(Task<GuardConnection> task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex)
        {
            Trace.WriteLine($"WinCal native region prewarm failed: {ex.GetType().Name}");
            lock (WarmSync)
                if (ReferenceEquals(WarmTask, task)) WarmTask = null;
        }
    }

    private static async Task DisposeIdleConnectionAsync(Task<GuardConnection> task)
    {
        try
        {
            GuardConnection connection = await task.ConfigureAwait(false);
            connection.Pipe.Dispose();
            try { await connection.Guard.WaitForExitAsync().ConfigureAwait(false); }
            finally { connection.Guard.Dispose(); }
        }
        catch (Exception ex) { Trace.WriteLine($"WinCal idle guardian shutdown: {ex.GetType().Name}"); }
    }

    private static void CompleteLease()
    {
        lock (WarmSync)
        {
            ActiveLease = false;
            LeaseSlot.Release();
            WarmGuardian();
        }
    }

    public bool IsHealthy
    {
        get
        {
            lock (_sync)
            {
                if (_disposed || !_healthy || !_armed) return false;
                try { return !_guard.HasExited && MatchesIdentity(_snapshot); }
                catch (InvalidOperationException) { return false; }
            }
        }
    }

    /// <summary>Recompute the hole from the immutable original shape; never subtract from an already cut shape.</summary>
    public bool Update(Rect bounds)
    {
        lock (_sync)
        {
            if (_disposed || !_healthy || !_armed) return false;
            try
            {
                if (_guard.HasExited || !SystemCalendarInterceptor.IsSystemPanelVisible(new IntPtr(_snapshot.Host)) ||
                    !MatchesIdentity(_snapshot) || !ValidBounds(_snapshot, bounds))
                    return FailAndRestore();
                if (!GetWindowRect(new IntPtr(_snapshot.Host), out var hostBounds) ||
                    hostBounds.Width != _snapshot.Width || hostBounds.Height != _snapshot.Height)
                    return FailAndRestore();

                IntPtr original = CreateOriginalRegion(_snapshot, false);
                IntPtr hole = IntPtr.Zero;
                IntPtr clipped = IntPtr.Zero;
                try
                {
                    if (original == IntPtr.Zero) return FailAndRestore();
                    int left = (int)Math.Floor(bounds.Left - hostBounds.Left);
                    int top = (int)Math.Floor(bounds.Top - hostBounds.Top);
                    int right = (int)Math.Ceiling(bounds.Right - hostBounds.Left);
                    int bottom = (int)Math.Ceiling(bounds.Bottom - hostBounds.Top);
                    hole = CreateRectRgn(left, top, right, bottom);
                    clipped = CreateRectRgn(0, 0, 0, 0);
                    if (hole == IntPtr.Zero || clipped == IntPtr.Zero || CombineRgn(clipped, original, hole, 4) == 0)
                        return FailAndRestore();
                    _mayHaveChanged = true;
                    if (_guard.HasExited || SetWindowRgn(new IntPtr(_snapshot.Host), clipped, true) == 0)
                        return FailAndRestore();
                    clipped = IntPtr.Zero; // Successful SetWindowRgn transfers HRGN ownership to the system.
                    return true;
                }
                finally
                {
                    if (original != IntPtr.Zero) DeleteObject(original);
                    if (hole != IntPtr.Zero) DeleteObject(hole);
                    if (clipped != IntPtr.Zero) DeleteObject(clipped);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"WinCal native region update failed: {ex.GetType().Name}");
                return FailAndRestore();
            }
        }
    }

    private bool FailAndRestore()
    {
        _healthy = false;
        if (_mayHaveChanged) Restore(_snapshot);
        return false;
    }

    private async Task MonitorGuardAsync()
    {
        try
        {
            await _guard.WaitForExitAsync(_healthCancellation.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed) return;
                _healthy = false;
                if (_mayHaveChanged) Restore(_snapshot);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.WriteLine($"WinCal native region guardian monitor failed: {ex.GetType().Name}");
            lock (_sync)
            {
                if (!_disposed) { _healthy = false; if (_mayHaveChanged) Restore(_snapshot); }
            }
        }
    }

    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();

    /// <summary>Restore immediately; let guardian acknowledgements finish off the UI thread.</summary>
    public Task DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null) return _disposeTask;
            _disposed = true;
            _healthy = false;
            _healthCancellation.Cancel();
            bool restored = false;
            try { restored = !_mayHaveChanged || Restore(_snapshot); }
            catch (Exception ex) { Trace.WriteLine($"WinCal native region restore: {ex.GetType().Name}"); }
            return _disposeTask = Task.Run(() => FinishDispose(restored));
        }
    }

    private void FinishDispose(bool restored)
    {
        bool disarmed = false;
        if (_armed && restored)
        {
            try
            {
                using var timeout = new CancellationTokenSource(DisposeTimeout);
                WriteFrameAsync(_pipe, new GuardMessage { Command = "DISARM", Unmodified = !_mayHaveChanged }, timeout.Token).GetAwaiter().GetResult();
                var reply = ReadFrameAsync(_pipe, timeout.Token).GetAwaiter().GetResult();
                if (reply?.Command == "DISARMED") { _armed = false; disarmed = true; }
            }
            catch (Exception ex) { Trace.WriteLine($"WinCal native region disarm: {ex.GetType().Name}"); }
        }
        // Only a fully acknowledged DISARM is an unambiguous boundary for another session.
        // Keep the Process alive: the next lease will install its own cancellable monitor.
        if (disarmed && TryReturnGuardian())
        {
            _healthCancellation.Dispose();
            return;
        }
        // Without DISARM, EOF tells the guardian to retry restoration itself.
        _pipe.Dispose();
        bool exited = false;
        try
        {
            exited = _guard.HasExited || _guard.WaitForExit(500);
            if (!exited)
                Trace.WriteLine("WinCal native region guardian did not exit within the restoration deadline.");
        }
        catch (Exception ex) { Trace.WriteLine($"WinCal guardian exit wait: {ex.GetType().Name}"); }
        if (exited)
        {
            ReleaseSlot();
            _guard.Dispose();
        }
        else
        {
            // A guardian still handling EOF must finish before another lease can touch this host.
            _ = ReleaseSlotAfterGuardExitAsync();
        }
        _healthCancellation.Dispose();
    }

    private bool TryReturnGuardian()
    {
        if (_connection is null || !_ownsSlot || _armed) return false;
        lock (WarmSync)
        {
            // Returning and shutdown take ownership under the same lock. Stop will either
            // take this idle connection, or make this lease close it through the EOF path.
            if (WarmStopping || WarmTask is not null || !ActiveLease) return false;
            try
            {
                if (_guard.HasExited || !_pipe.IsConnected) return false;
            }
            catch (InvalidOperationException) { return false; }
            WarmTask = Task.FromResult(_connection);
            _ownsSlot = false;
            ActiveLease = false;
            OverlayTiming.Write($"GUARD_RETURNED guard={_guard.Id}");
            LeaseSlot.Release();
            return true;
        }
    }

    private void ReleaseSlot()
    {
        if (!_ownsSlot) return;
        _ownsSlot = false;
        CompleteLease();
    }

    private async Task ReleaseSlotAfterGuardExitAsync()
    {
        try
        {
            await _guard.WaitForExitAsync().ConfigureAwait(false);
            lock (_sync) ReleaseSlot();
        }
        catch (Exception ex)
        {
            // Fail closed: do not permit a new lease while the old guardian's state is unknown.
            Trace.WriteLine($"WinCal guardian exit could not be confirmed: {ex.GetType().Name}");
        }
        finally { _guard.Dispose(); }
    }

    private static async Task RunGuardAsync(string pipeName, string token, int parentId, long parentStart)
    {
        if (!pipeName.StartsWith("WinCal.NativeRegion.", StringComparison.Ordinal) || token.Length != 32) return;
        using var parent = Process.GetProcessById(parentId);
        if (parent.StartTime.ToUniversalTime().Ticks != parentStart || parent.HasExited) return;
        WarmMessageSerialization();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var startupTimeout = new CancellationTokenSource(IpcTimeout);
        await pipe.ConnectAsync(startupTimeout.Token).ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint serverPid) || serverPid != parentId) return;
        using var lifetime = new CancellationTokenSource();
        try
        {
            Task parentExit = parent.WaitForExitAsync(lifetime.Token);
            while (!parentExit.IsCompleted)
            {
                // A standby may remain idle for the whole application lifetime. Each successful
                // DISARM discards its old snapshot before waiting for the next ARM.
                Task<GuardMessage?> armCommand = ReadFrameAsync(pipe, lifetime.Token);
                if (await Task.WhenAny(parentExit, armCommand).ConfigureAwait(false) != armCommand || parentExit.IsCompleted) return;
                var arm = await armCommand.ConfigureAwait(false);
                var requestedSnapshot = arm?.Snapshot;
                if (arm?.Command != "ARM" || arm.Token != token || requestedSnapshot == null || !MatchesIdentity(requestedSnapshot)) return;
                // The mutex remains held across sessions. Always capture the current original
                // region after ARM rather than reusing any previous session's snapshot.
                if (!TryCaptureSnapshot(new IntPtr(requestedSnapshot.Host), out var snapshot) || snapshot is null ||
                    snapshot.ProcessStartTicks != requestedSnapshot.ProcessStartTicks ||
                    snapshot.ThreadId != requestedSnapshot.ThreadId) return;
                IntPtr backup = CreateOriginalRegion(snapshot, true);
                if (snapshot.HadRegion && backup == IntPtr.Zero) return;
                if (backup != IntPtr.Zero) DeleteObject(backup);

                bool armed = true;
                try
                {
                    using var armTimeout = new CancellationTokenSource(IpcTimeout);
                    await WriteFrameAsync(pipe, new GuardMessage { Command = "ARMED", Token = token, Snapshot = snapshot }, armTimeout.Token).ConfigureAwait(false);
                    Task<GuardMessage?> command = ReadFrameAsync(pipe, lifetime.Token);
                    Task completed = await Task.WhenAny(parentExit, command).ConfigureAwait(false);
                    if (completed == command)
                    {
                        GuardMessage? message = await command.ConfigureAwait(false);
                        if (message?.Command == "DISARM")
                        {
                            // Cancelled preparation never changed the host; otherwise confirm
                            // main restored the original before allowing another session.
                            if (message.Unmodified || OriginalRegionMatches(snapshot))
                            {
                                armed = false;
                                using var disarmTimeout = new CancellationTokenSource(IpcTimeout);
                                await WriteFrameAsync(pipe, new GuardMessage { Command = "DISARMED" }, disarmTimeout.Token).ConfigureAwait(false);
                            }
                        }
                    }
                }
                finally
                {
                    if (armed)
                    {
                        // Ambiguous protocol/EOF/parent death restores before releasing the mutex.
                        // This connection exits instead of being reused after recovery.
                        while (!Restore(snapshot))
                            await Task.Delay(250).ConfigureAwait(false);
                    }
                }
                if (armed) return;
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        finally { lifetime.Cancel(); }
    }

    private static bool TryCaptureSnapshot(IntPtr host, out RegionSnapshot? snapshot)
    {
        snapshot = null;
        if (!IsWindow(host) || !GetWindowRect(host, out var bounds) || bounds.Width <= 0 || bounds.Height <= 0) return false;
        uint tid = GetWindowThreadProcessId(host, out uint pid);
        var className = new StringBuilder(256);
        if (pid == 0 || tid == 0 || GetClassName(host, className, className.Capacity) == 0 ||
            className.ToString() != "Windows.UI.Core.CoreWindow") return false;
        using var process = Process.GetProcessById((int)pid);
        if (process.ProcessName is not ("ShellExperienceHost" or "ShellHost")) return false;
        var result = new RegionSnapshot
        {
            Host = host.ToInt64(), ProcessId = pid, ThreadId = tid,
            ProcessStartTicks = process.StartTime.ToUniversalTime().Ticks,
            ClassName = className.ToString(), Width = bounds.Width, Height = bounds.Height
        };
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) return false;
        try
        {
            Marshal.SetLastPInvokeError(0);
            int kind = GetWindowRgn(host, region);
            int regionError = Marshal.GetLastPInvokeError();
            if (kind == 0 && regionError != 0) return false;
            if (kind == 1) return false; // An empty native shape is an animation/transient state.
            result.HadRegion = kind != 0;
            if (result.HadRegion)
            {
                uint size = GetRegionData(region, 0, null);
                if (size < 32 || size > MaximumFrameBytes / 2) return false;
                var data = new byte[size];
                if (GetRegionData(region, size, data) != size || !ValidRegionData(data)) return false;
                result.RegionData = data;
            }
        }
        finally { DeleteObject(region); }
        if (!MatchesIdentity(result)) return false;
        snapshot = result;
        return true;
    }

    private static bool MatchesIdentity(RegionSnapshot snapshot)
    {
        try
        {
            var host = new IntPtr(snapshot.Host);
            if (!IsWindow(host) || GetWindowThreadProcessId(host, out uint pid) != snapshot.ThreadId || pid != snapshot.ProcessId) return false;
            var name = new StringBuilder(256);
            if (GetClassName(host, name, name.Capacity) == 0 || name.ToString() != snapshot.ClassName ||
                snapshot.ClassName != "Windows.UI.Core.CoreWindow") return false;
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName is "ShellExperienceHost" or "ShellHost" &&
                process.StartTime.ToUniversalTime().Ticks == snapshot.ProcessStartTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static bool ValidBounds(RegionSnapshot snapshot, Rect calendar)
    {
        if (calendar.IsEmpty || calendar.Width <= 0 || calendar.Height <= 0 ||
            !double.IsFinite(calendar.Left) || !double.IsFinite(calendar.Top) ||
            !double.IsFinite(calendar.Right) || !double.IsFinite(calendar.Bottom) ||
            !GetWindowRect(new IntPtr(snapshot.Host), out var bounds)) return false;
        return new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height).Contains(calendar);
    }

    private static bool ValidRegionData(byte[] data)
    {
        if (data.Length < 32 || BitConverter.ToUInt32(data, 0) != 32 || BitConverter.ToUInt32(data, 4) != 1) return false;
        uint count = BitConverter.ToUInt32(data, 8), size = BitConverter.ToUInt32(data, 12);
        return (ulong)count * 16 <= (ulong)data.Length - 32 && size <= data.Length - 32;
    }

    private static IntPtr CreateOriginalRegion(RegionSnapshot snapshot, bool preserveNoRegion)
    {
        if (!snapshot.HadRegion)
            return preserveNoRegion ? IntPtr.Zero : CreateRectRgn(0, 0, snapshot.Width, snapshot.Height);
        return snapshot.RegionData is { } data && ValidRegionData(data)
            ? ExtCreateRegion(IntPtr.Zero, (uint)data.Length, data) : IntPtr.Zero;
    }

    private static bool Restore(RegionSnapshot snapshot)
    {
        if (!MatchesIdentity(snapshot)) return true; // The original target no longer exists; never touch a replacement.
        if (!GetWindowRect(new IntPtr(snapshot.Host), out var bounds)) return false;
        // A region captured at the old DPI/size would clip a newly enlarged notification panel.
        // Let the resized native window display its complete new extent instead.
        if (bounds.Width != snapshot.Width || bounds.Height != snapshot.Height)
            return SetWindowRgn(new IntPtr(snapshot.Host), IntPtr.Zero, true) != 0;
        IntPtr original = CreateOriginalRegion(snapshot, true);
        if (snapshot.HadRegion && original == IntPtr.Zero) return false;
        if (SetWindowRgn(new IntPtr(snapshot.Host), original, true) != 0) return true;
        if (original != IntPtr.Zero) DeleteObject(original);
        return false;
    }

    private static bool OriginalRegionMatches(RegionSnapshot snapshot)
    {
        if (!MatchesIdentity(snapshot)) return true;
        IntPtr current = CreateRectRgn(0, 0, 0, 0), original = IntPtr.Zero;
        if (current == IntPtr.Zero) return false;
        try
        {
            Marshal.SetLastPInvokeError(0);
            int kind = GetWindowRgn(new IntPtr(snapshot.Host), current);
            if (kind == 0 && Marshal.GetLastPInvokeError() != 0) return false;
            if (!GetWindowRect(new IntPtr(snapshot.Host), out var bounds)) return false;
            if (bounds.Width != snapshot.Width || bounds.Height != snapshot.Height) return kind == 0;
            if (!snapshot.HadRegion) return kind == 0;
            original = CreateOriginalRegion(snapshot, true);
            return kind != 0 && original != IntPtr.Zero && EqualRgn(current, original);
        }
        finally
        {
            DeleteObject(current);
            if (original != IntPtr.Zero) DeleteObject(original);
        }
    }

    private static void WarmMessageSerialization()
    {
        // Initialize JSON metadata and first-use code before the idle connection is consumed.
        // This is a managed template only; no native snapshot is read or applied.
        var template = new GuardMessage
        {
            Command = "ARM",
            Token = new string('0', 32),
            Snapshot = new RegionSnapshot
            {
                ClassName = "Windows.UI.Core.CoreWindow",
                Width = 1,
                Height = 1,
                HadRegion = true,
                RegionData = new byte[32]
            }
        };
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(template);
        _ = JsonSerializer.Deserialize<GuardMessage>(data);
    }

    private static async Task WriteFrameAsync(Stream pipe, GuardMessage message, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(message);
        if (data.Length > MaximumFrameBytes) throw new IOException("Region snapshot is too large.");
        await pipe.WriteAsync(BitConverter.GetBytes(data.Length), token).ConfigureAwait(false);
        await pipe.WriteAsync(data, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<GuardMessage?> ReadFrameAsync(Stream pipe, CancellationToken token)
    {
        byte[] header = new byte[4];
        await pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaximumFrameBytes) throw new IOException("Invalid region frame.");
        byte[] data = new byte[length];
        await pipe.ReadExactlyAsync(data, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<GuardMessage>(data);
    }

    private sealed record GuardConnection(NamedPipeServerStream Pipe, Process Guard, string Token)
    {
        public int SessionCount { get; set; }
    }

    private sealed class GuardMessage
    {
        public string Command { get; set; } = "";
        public string? Token { get; set; }
        public RegionSnapshot? Snapshot { get; set; }
        public bool Unmodified { get; set; }
    }

    private sealed class RegionSnapshot
    {
        public long Host { get; set; }
        public uint ProcessId { get; set; }
        public uint ThreadId { get; set; }
        public long ProcessStartTicks { get; set; }
        public string ClassName { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public bool HadRegion { get; set; }
        public byte[]? RegionData { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
    [DllImport("gdi32.dll")] private static extern bool EqualRgn(IntPtr first, IntPtr second);
    [DllImport("gdi32.dll")] private static extern uint GetRegionData(IntPtr region, uint size, [Out] byte[]? data);
    [DllImport("gdi32.dll")] private static extern IntPtr ExtCreateRegion(IntPtr transform, uint size, byte[] data);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
}
