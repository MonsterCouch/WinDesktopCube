using System;
using System.Drawing;

namespace WinDesktopCube;

/// <summary>
/// A reusable GDI capture buffer for one monitor. Allocated once, so switching
/// doesn't churn ~14MB (per 3440x1440 frame) through the large-object heap and
/// the GPU doesn't have to create fresh textures every time.
///
/// The cube overlay windows use WDA_EXCLUDEFROMCAPTURE, so a capture taken while
/// they're on top still sees the real desktop underneath.
///
/// Not thread-safe: callers serialize Grab() and reads of <see cref="Bits"/>.
/// </summary>
internal sealed class MonitorCapture : IDisposable
{
    public Rectangle Bounds { get; }
    public IntPtr Bits { get; }
    public int Stride => Bounds.Width * 4;
    public int BufferSize => Stride * Bounds.Height;

    private readonly IntPtr _memDc, _dib, _oldObj;

    public MonitorCapture(Rectangle bounds)
    {
        Bounds = bounds;
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        try
        {
            _memDc = Win32.CreateCompatibleDC(screenDc);
            var bmi = new Win32.BITMAPINFOHEADER
            {
                biSize = 40, biWidth = bounds.Width, biHeight = -bounds.Height, // top-down
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            _dib = Win32.CreateDIBSection(screenDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            Bits = bits;
            _oldObj = Win32.SelectObject(_memDc, _dib);
        }
        finally { Win32.ReleaseDC(IntPtr.Zero, screenDc); }
    }

    /// <summary>Copy the monitor's current contents into <see cref="Bits"/>. Safe on a worker thread.</summary>
    public void Grab()
    {
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        try { Win32.BitBlt(_memDc, 0, 0, Bounds.Width, Bounds.Height, screenDc, Bounds.X, Bounds.Y, Win32.SRCCOPY); }
        finally { Win32.ReleaseDC(IntPtr.Zero, screenDc); }
    }

    public void Dispose()
    {
        Win32.SelectObject(_memDc, _oldObj);
        Win32.DeleteObject(_dib);
        Win32.DeleteDC(_memDc);
    }
}
