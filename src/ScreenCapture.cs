using System;
using System.Drawing;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinDesktopCube;

/// <summary>
/// Grabs a region of the screen with GDI BitBlt. The cube overlay windows use
/// WDA_EXCLUDEFROMCAPTURE, so a capture taken while they are on top still sees
/// the real desktop underneath. That's how we get a live picture of the target desktop.
/// </summary>
internal static class ScreenCapture
{
    public static BitmapSource Capture(Rectangle r)
    {
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
        var bmi = new Win32.BITMAPINFOHEADER
        {
            biSize = 40, biWidth = r.Width, biHeight = -r.Height, // top-down
            biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        IntPtr dib = Win32.CreateDIBSection(screenDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = Win32.SelectObject(memDc, dib);
        try
        {
            Win32.BitBlt(memDc, 0, 0, r.Width, r.Height, screenDc, r.X, r.Y, Win32.SRCCOPY);
            int stride = r.Width * 4;
            var src = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgr32, null,
                                          bits, stride * r.Height, stride);
            src.Freeze();
            return src;
        }
        finally
        {
            Win32.SelectObject(memDc, old);
            Win32.DeleteObject(dib);
            Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
