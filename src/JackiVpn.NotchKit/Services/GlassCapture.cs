using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// Real liquid glass: the screen area behind the open island, read at one sixth of its size (the island itself is
/// excluded from capture), softened by a box blur and returned as a small frozen picture. A few kilobytes per frame;
/// called at most five times a second and only while the island is open.
/// </summary>
public static partial class GlassCapture
{
    // Half resolution: sharp enough that the glass reads as glass (only a whisper of blur), still a few hundred KB a second.
    private const int Downscale = 2;

    public static BitmapSource? Grab(int x, int y, int width, int height)
    {
        var w = Math.Max(4, width / Downscale);
        var h = Math.Max(4, height / Downscale);
        var screen = GetDC(0);
        if (screen == 0)
        {
            return null;
        }

        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(memory, bitmap);
        try
        {
            _ = SetStretchBltMode(memory, 4 /* HALFTONE */);
            if (!StretchBlt(memory, 0, 0, w, h, screen, x, y, width, height, 0x00CC0020 /* SRCCOPY */))
            {
                return null;
            }

            var info = new BitmapInfo { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
            var pixels = new byte[w * h * 4];
            if (GetDIBits(memory, bitmap, 0, (uint)h, pixels, ref info, 0) == 0)
            {
                return null;
            }

            Blur(pixels, w, h, 1);
            pixels = Refract(pixels, w, h);
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            source.Freeze();
            return source;
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            _ = ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// Light bending at the glass edge: towards the lower edge and the sides the picture is pulled inwards, more
    /// the closer to the rim (a thick lens), with red and blue bent slightly differently (a hint of dispersion).
    /// The middle stays undistorted.
    /// </summary>
    internal static byte[] Refract(byte[] src, int w, int h)
    {
        var dst = new byte[src.Length];
        for (var y = 0; y < h; y++)
        {
            var ny = (double)y / Math.Max(1, h - 1);
            var bottom = Smooth(0.5, 1, ny);
            for (var x = 0; x < w; x++)
            {
                var nx = Math.Min(x, w - 1 - x) / Math.Max(1.0, (w - 1) / 2.0);
                var side = Smooth(0.72, 1, 1 - nx);
                var dy = -bottom * bottom * 0.22 * h;
                var dx = (x < w / 2 ? 1 : -1) * side * side * 0.14 * w;
                var o = ((y * w) + x) * 4;
                dst[o] = Sample(src, w, h, x + (dx * 0.94), y + (dy * 0.94), 0);
                dst[o + 1] = Sample(src, w, h, x + dx, y + dy, 1);
                dst[o + 2] = Sample(src, w, h, x + (dx * 1.06), y + (dy * 1.06), 2);
                dst[o + 3] = 255;
            }
        }

        return dst;
    }

    private static double Smooth(double a, double b, double v)
    {
        var t = Math.Clamp((v - a) / (b - a), 0, 1);
        return t * t * (3 - (2 * t));
    }

    private static byte Sample(byte[] p, int w, int h, double x, double y, int c)
    {
        var xi = Math.Clamp((int)Math.Round(x), 0, w - 1);
        var yi = Math.Clamp((int)Math.Round(y), 0, h - 1);
        return p[(((yi * w) + xi) * 4) + c];
    }

    /// <summary>A separable box blur (horizontal, then vertical) of the colour channels.</summary>
    internal static void Blur(byte[] p, int w, int h, int radius)
    {
        var tmp = new byte[p.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0, n = 0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var xx = Math.Clamp(x + k, 0, w - 1);
                        sum += p[(((y * w) + xx) * 4) + c];
                        n++;
                    }

                    tmp[(((y * w) + x) * 4) + c] = (byte)(sum / n);
                }
            }
        }

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0, n = 0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var yy = Math.Clamp(y + k, 0, h - 1);
                        sum += tmp[(((yy * w) + x) * 4) + c];
                        n++;
                    }

                    p[(((y * w) + x) * 4) + c] = (byte)(sum / n);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPels;
        public int YPels;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint Colors;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint dc, int w, int h);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial int SetStretchBltMode(nint dc, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StretchBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfo info, uint usage);
}
