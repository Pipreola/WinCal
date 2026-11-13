using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

internal static class CalendarScreenshot
{
    // Capture only the already validated calendar card; exclude native notification contents.
    internal static void Save(Rect calendar, string path)
    {
        int width = (int)Math.Round(calendar.Width), height = (int)Math.Round(calendar.Height);
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(dc, bitmap);
        try
        {
            if (!BitBlt(dc, 0, 0, width, height, screen, (int)calendar.Left, (int)calendar.Top, 0x40CC0020))
                throw new IOException("Calendar screenshot failed.");
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            encoder.Save(file);
        }
        finally { SelectObject(dc, old); DeleteObject(bitmap); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen); }
    }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int sx, int sy, uint raster);
}
