using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinCal.Core.Helpers;

/// <summary>
/// 让只需鼠标操作的浮层保持不激活，避免原生通知中心因失焦关闭。
/// </summary>
internal static class WindowActivationHelper
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    public static void PreventActivation(Window window)
    {
        window.ShowActivated = false;
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var style = GetWindowLongPtr(hwnd, -20).ToInt64();
            SetWindowLongPtr(hwnd, -20, new IntPtr(style | 0x08000000L)); // WS_EX_NOACTIVATE
            HwndSource.FromHwnd(hwnd)?.AddHook(HandleMessage);
        };
    }

    private static IntPtr HandleMessage(
        IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE：仍然传递按钮点击。
        }
        return IntPtr.Zero;
    }
}
