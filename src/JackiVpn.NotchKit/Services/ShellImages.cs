using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// Pictures from the Windows shell, the same Explorer shows: a thumbnail for images, videos and documents, the icon
/// for folders, apps and anything else — also for installed apps by their app id (<c>shell:AppsFolder\…</c>).
/// Made on one background thread of its own (shell extensions want a single-threaded apartment, and a large video
/// must never hold up the notch); results are frozen and cached.
/// </summary>
public static partial class ShellImages
{
    private static readonly BlockingCollection<Action> Work = [];
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<Thread> Worker = new(() =>
    {
        var thread = new Thread(() =>
        {
            foreach (var job in Work.GetConsumingEnumerable())
            {
                try
                {
                    job();
                }
                catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
                {
                    // a picture is optional
                }
            }
        })
        { IsBackground = true, Name = "IBOX shell images" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    });

    /// <summary>The picture for a file, folder or <c>shell:AppsFolder\…</c> item (null when there is none).</summary>
    public static Task<ImageSource?> GetAsync(string path, int size)
    {
        var key = size + "|" + path;
        if (Cache.TryGetValue(key, out var cached))
        {
            return Task.FromResult(cached);
        }

        var done = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Worker.Value;
        Work.Add(() =>
        {
            ImageSource? image = null;
            try
            {
                image = Load(path, size);
            }
            finally
            {
                Cache[key] = image;
                done.TrySetResult(image);
            }
        });
        return done.Task;
    }

    /// <summary>Forget a path's pictures (the file changed).</summary>
    public static void Forget(string path)
    {
        foreach (var key in Cache.Keys)
        {
            if (key.EndsWith("|" + path, StringComparison.OrdinalIgnoreCase))
            {
                Cache.TryRemove(key, out _);
            }
        }
    }

    /// <summary>The app id (AUMID) of an installed app whose name is one of <paramref name="names"/>, from the Start menu's list.</summary>
    public static Task<string?> FindAppAsync(params string[] names)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Worker.Value;
        Work.Add(() =>
        {
            string? found = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                dynamic? shell = shellType is null ? null : Activator.CreateInstance(shellType);
                dynamic? folder = shell?.NameSpace("shell:AppsFolder");
                if (folder is not null)
                {
                    var items = folder.Items();
                    int count = items.Count;
                    for (var i = 0; i < count; i++)
                    {
                        var item = items.Item(i);
                        string name = item.Name;
                        if (Array.Exists(names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            found = item.Path;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
            {
                // not found
            }
            finally
            {
                done.TrySetResult(found);
            }
        });
        return done.Task;
    }

    private static ImageSource? Load(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
        {
            return null;
        }

        try
        {
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, 0 /* SIIGBF_RESIZETOFIT */, out var hbitmap) != 0 || hbitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return FromHBitmap(hbitmap);
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>The bitmap with its transparency (WPF's own conversion drops the alpha channel of shell bitmaps).</summary>
    private static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        var dib = default(DibSection);
        if (GetObject(hbitmap, Marshal.SizeOf<DibSection>(), ref dib) == 0 || dib.Bits == IntPtr.Zero || dib.BitsPixel != 32)
        {
            var plain = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            plain.Freeze();
            return plain;
        }

        var width = dib.Width;
        var height = Math.Abs(dib.Height);
        var stride = dib.WidthBytes;
        var pixels = new byte[stride * height];
        Marshal.Copy(dib.Bits, pixels, 0, pixels.Length);
        if (dib.HeaderHeight > 0)
        {
            // Bottom-up rows: flip.
            var row = new byte[stride];
            for (var y = 0; y < height / 2; y++)
            {
                Buffer.BlockCopy(pixels, y * stride, row, 0, stride);
                Buffer.BlockCopy(pixels, (height - 1 - y) * stride, pixels, y * stride, stride);
                Buffer.BlockCopy(row, 0, pixels, (height - 1 - y) * stride, stride);
            }
        }

        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DibSection
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
        public int HeaderSize;
        public int HeaderWidth;
        public int HeaderHeight;
        public ushort HeaderPlanes;
        public ushort HeaderBitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint Mask0;
        public uint Mask1;
        public uint Mask2;
        public IntPtr Section;
        public uint Offset;
    }

#pragma warning disable SYSLIB1054 // COM interface out parameter: classic interop
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, ref DibSection dib);
#pragma warning restore SYSLIB1054

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);
}
