# Notification compatibility geometry checks

This console harness links the real positioning and activation helpers without
NuGet packages. It tests placement and creates one hidden HWND on an STA thread;
it never displays a window.

Run from the repository root with the installed .NET 8 SDK:

```powershell
dotnet run --project tests/NotificationCompatibility.Tests/NotificationCompatibility.Tests.csproj
```

A missing placement API or failed geometry check returns exit code 1. The cases cover
left/right placement, negative monitor coordinates, nonzero work-area origins,
oversized panels, narrow displays, and dimensions already scaled to the target DPI.
The hidden-window check verifies `ShowActivated`, the actual `WS_EX_NOACTIVATE`
style, the `WM_MOUSEACTIVATE` response, and preservation of the foreground window.
Calendar-overlay checks require the result to match the native calendar's whole
rectangle exactly while leaving the notification region uncovered. Missing,
zero-sized, non-finite, overlapping, or outside-host regions must fail safely.
Negative monitor coordinates and physical dimensions already scaled for DPI are
preserved without rounding or extra scaling.

These checks do not validate native notification focus or dismissal behavior; that
requires a separate Windows interaction check.
