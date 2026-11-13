using System.Runtime.CompilerServices;
using WinCal.Core.Helpers;

namespace WinCal;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // A recovery guardian only needs Win32 and IPC. Run it before constructing App,
        // loading XAML/theme dictionaries or initializing any WPF controls.
        if (NativeCalendarRegionLease.TryRunGuard(args)) return;
        RunApplication();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApplication()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
