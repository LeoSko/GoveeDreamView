using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GoveeDreamView
{
    // Captures one monitor scaled down to a small grid via GDI StretchBlt (one framebuffer
    // read, box-averaged on the way down), then averages arbitrary normalised rectangles of
    // that grid so callers can sample any screen region (e.g. perimeter zones).
    internal sealed class ScreenCapture : IDisposable
    {
        private const int SRCCOPY = 0x00CC0020;
        private const int HALFTONE = 4;

        private readonly int _w;
        private readonly int _h;
        private readonly IntPtr _memDc;
        private readonly IntPtr _bmp;
        private readonly IntPtr _oldBmp;
        private readonly byte[] _bits;     // BGRA, _w * _h * 4
        private Rectangle _bounds;

        public ScreenCapture(int monitorIndex, int gridW = 32, int gridH = 18)
        {
            _w = gridW;
            _h = gridH;
            _bounds = MonitorBounds(monitorIndex);
            _bits = new byte[_w * _h * 4];

            IntPtr screen = GetDC(IntPtr.Zero);
            _memDc = CreateCompatibleDC(screen);
            _bmp = CreateCompatibleBitmap(screen, _w, _h); // colour-compatible with the screen, not the memDC
            _oldBmp = SelectObject(_memDc, _bmp);
            SetStretchBltMode(_memDc, HALFTONE);
            SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
            ReleaseDC(IntPtr.Zero, screen);
        }

        // Grabs the current monitor contents into the grid buffer.
        public void Capture()
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            StretchBlt(_memDc, 0, 0, _w, _h,
                       screen, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
                       SRCCOPY);
            ReleaseDC(IntPtr.Zero, screen);

            var bmi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                biWidth = _w,
                biHeight = -_h,   // negative => top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            GetDIBits(_memDc, _bmp, 0, (uint)_h, _bits, ref bmi, 0);
        }

        // Averages the grid cells inside the normalised rectangle [x0,y0)-(x1,y1) (0..1)
        // and writes R,G,B into rgb[off..off+2].
        public void AverageRegion(double x0, double y0, double x1, double y1, byte[] rgb, int off)
        {
            int gx0 = Clamp((int)(x0 * _w), 0, _w - 1);
            int gy0 = Clamp((int)(y0 * _h), 0, _h - 1);
            int gx1 = Clamp((int)Math.Ceiling(x1 * _w), gx0 + 1, _w);
            int gy1 = Clamp((int)Math.Ceiling(y1 * _h), gy0 + 1, _h);

            long r = 0, g = 0, b = 0;
            int count = 0;
            for (int gy = gy0; gy < gy1; gy++)
            {
                for (int gx = gx0; gx < gx1; gx++)
                {
                    int i = (gy * _w + gx) * 4;
                    b += _bits[i];
                    g += _bits[i + 1];
                    r += _bits[i + 2];
                    count++;
                }
            }
            rgb[off] = (byte)(r / count);
            rgb[off + 1] = (byte)(g / count);
            rgb[off + 2] = (byte)(b / count);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static Rectangle MonitorBounds(int index)
        {
            var screens = Screen.AllScreens;
            if (index < 0 || index >= screens.Length) index = 0;
            return screens[index].Bounds;
        }

        public void Dispose()
        {
            SelectObject(_memDc, _oldBmp);
            DeleteObject(_bmp);
            DeleteDC(_memDc);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDc);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hDc, int mode);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hDc, int x, int y, IntPtr prev);
        [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int xd, int yd, int wd, int hd,
            IntPtr src, int xs, int ys, int ws, int hs, int rop);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hDc, IntPtr hbm, uint start, uint lines,
            byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    }
}
