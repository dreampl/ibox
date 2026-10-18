using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace JackiVpn.Desktop.Views;

/// <summary>The island's shape for the lens, in pixels of the captured area (which is the notch window's own rectangle).</summary>
public readonly record struct GlassShape(int X, int Y, int Width, int Height, float Radius, float Strength, float Scale);

/// <summary>One processed picture: BGRA rows of <see cref="Width"/> × <see cref="Height"/>, and what it looked like.</summary>
public sealed class GlassFrame(byte[] pixels, int width, int height, int stride)
{
    public byte[] Pixels { get; } = pixels;

    public int Width { get; set; } = width;

    public int Height { get; set; } = height;

    public int Stride { get; set; } = stride;

    /// <summary>Average colour behind (B, G, R).</summary>
    public (byte B, byte G, byte R) Average { get; set; }

    /// <summary>Everything came back black: protected (DRM) content, which capture is not given.</summary>
    public bool Black { get; set; }
}

/// <summary>
/// Real liquid glass, outside WPF: Windows.Graphics.Capture hands the monitor's picture to the GPU only when it
/// changes (nothing arrives while the screen is still, up to the refresh rate while something moves); the area
/// behind the notch window is copied out on the GPU and a small Direct3D 11 compute shader bends it like the rim of
/// a thick convex lens — undistorted in the middle, compressed and pulled in from beyond the edge near the rim, red
/// and blue parting slightly there, with only a whisper of blur. The island itself is excluded from capture
/// (WDA_EXCLUDEFROMCAPTURE). Only the island's own rectangle is read back for WPF to show, and only when it differs
/// from the last one. Protected content captures as black: the caller then falls back to drawn glass.
/// Everything is released when the island closes.
/// </summary>
public sealed unsafe partial class GlassPipeline : IDisposable
{
    private const string Shader = """
        Texture2D<float4> Src : register(t0);
        SamplerState Lin : register(s0);
        RWTexture2D<unorm float4> Dst : register(u0);
        cbuffer P : register(b0) { float4 Body; float4 Shape; float4 Size; };

        float Box(float2 p, float2 b, float r) { float2 q = abs(p) - b + r; return length(max(q, 0)) + min(max(q.x, q.y), 0) - r; }
        float Sd(float2 px) {
            // The island hangs from the top edge: a rounded box whose upper half is above the screen.
            float2 c = float2(Body.x + Body.z * 0.5, Body.y);
            return Box(px - c, float2(Body.z * 0.5, Body.w), Shape.x);
        }
        float3 Tap(float2 px) {
            // A whisper of blur: four bilinear taps half a pixel apart (about a 1 px box).
            float2 o = Shape.w * Size.zw;
            float2 uv = px * Size.zw;
            return 0.25 * (Src.SampleLevel(Lin, uv + float2(-o.x, -o.y), 0).rgb + Src.SampleLevel(Lin, uv + float2(o.x, -o.y), 0).rgb
                         + Src.SampleLevel(Lin, uv + float2(-o.x, o.y), 0).rgb + Src.SampleLevel(Lin, uv + float2(o.x, o.y), 0).rgb);
        }

        [numthreads(8, 8, 1)]
        void main(uint3 id : SV_DispatchThreadID) {
            if (id.x >= (uint)Body.z || id.y >= (uint)Body.w) return;
            float2 px = Body.xy + float2(id.xy) + 0.5;
            float d = Sd(px);
            float2 e = float2(0.75, 0);
            float2 n = float2(Sd(px + e.xy) - Sd(px - e.xy), Sd(px + e.yx) - Sd(px - e.yx));
            n = n / max(length(n), 1e-4);
            float rim = Shape.z;
            float t = saturate(-d / rim);             // 0 on the edge, 1 where the rim ends
            float k = (1 - t) * (1 - t);             // bending grows towards the edge
            float bend = rim * 0.85 * Shape.y * k;   // look outwards: the rim gathers what lies beyond the edge
            float3 c;
            c.r = Tap(px + n * bend * 1.07).r;
            c.g = Tap(px + n * bend).g;
            c.b = Tap(px + n * bend * 0.93).b;
            // A faint brightening inside the rim, as light gathers in thick glass.
            c += 0.05 * Shape.y * k * k;
            // Written as B, G, R, A so the bytes read back in WPF's BGRA order.
            Dst[id.xy] = float4(saturate(c.b), saturate(c.g), saturate(c.r), 1);
        }
        """;

    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IidDxgiAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidSession3 = new("f2cdd966-22ae-5ea1-9596-3a289344c3be");

    private readonly Lock _gate = new();
    private readonly nint _monitor;
    private readonly int _ox;
    private readonly int _oy;
    private readonly int _w;
    private readonly int _h;
    private nint _device;
    private nint _context;
    private nint _src;
    private nint _srcView;
    private nint _dst;
    private nint _dstView;
    private nint _staging;
    private nint _constants;
    private nint _sampler;
    private nint _shader;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private GlassShape _shape;
    private bool _haveSource;
    private ulong _lastHash;
    private GlassFrame? _spare;
    private bool _disposed;

    /// <summary>
    /// Starts capturing the monitor <paramref name="monitor"/> for the area (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="width"/>, <paramref name="height"/>) given relative to that monitor's top-left, in pixels.
    /// </summary>
    private GlassPipeline(nint monitor, int x, int y, int width, int height)
    {
        _monitor = monitor;
        _ox = Math.Max(0, x);
        _oy = Math.Max(0, y);
        _w = Math.Max(8, width);
        _h = Math.Max(8, height);
    }

    /// <summary>A new picture is ready (raised on a capture thread, or on the caller's thread from <see cref="SetShape"/>).</summary>
    public event Action<GlassFrame>? Frame;

    /// <summary>Starts the pipeline, or returns null where Windows cannot (no GPU, capture unsupported).</summary>
    public static GlassPipeline? TryStart(nint monitor, int x, int y, int width, int height)
    {
        var pipeline = new GlassPipeline(monitor, x, y, width, height);
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
            {
                return null;
            }

            pipeline.Create();
            return pipeline;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException or UnauthorizedAccessException or NotSupportedException or InvalidCastException)
        {
            pipeline.Dispose();
            return null;
        }
    }

    /// <summary>The island moved or changed size: process the last picture again for the new shape.</summary>
    public void SetShape(GlassShape shape)
    {
        lock (_gate)
        {
            if (_disposed || shape == _shape)
            {
                return;
            }

            _shape = shape;
            if (_haveSource)
            {
                Process(force: true);
            }
        }
    }

    /// <summary>Gives a processed picture back for reuse (fewer allocations while it animates).</summary>
    public void Return(GlassFrame frame) => Interlocked.Exchange(ref _spare, frame);

    private void Create()
    {
        // Direct3D 11 on the default adapter, BGRA for capture.
        var levels = stackalloc int[] { 0xb000 /* 11_0 */ };
        Check(D3D11CreateDevice(0, 1 /* HARDWARE */, 0, 0x20 /* BGRA_SUPPORT */, levels, 1, 7, out _device, out _, out _context));

        Texture(ref _src, ref _srcView, 87 /* B8G8R8A8_UNORM */, 0 /* DEFAULT */, 0x8 /* SHADER_RESOURCE */, 0, uav: false);
        Texture(ref _dst, ref _dstView, 28 /* R8G8B8A8_UNORM */, 0, 0x80 /* UNORDERED_ACCESS */, 0, uav: true);
        nint none = 0;
        Texture(ref _staging, ref none, 28, 3 /* STAGING */, 0, 0x20000 /* CPU READ */, uav: false);

        var cb = new BufferDesc { ByteWidth = 48, Usage = 0, BindFlags = 0x4 /* CONSTANT */ };
        nint buffer;
        Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)Slot(_device, 3))(_device, &cb, 0, &buffer));
        _constants = buffer;

        var sd = new SamplerDesc { Filter = 0x15, AddressU = 3, AddressV = 3, AddressW = 3, MaxAnisotropy = 1, ComparisonFunc = 1, MaxLod = float.MaxValue };
        nint sampler;
        Check(((delegate* unmanaged[Stdcall]<nint, SamplerDesc*, nint*, int>)Slot(_device, 23))(_device, &sd, &sampler));
        _sampler = sampler;
        _shader = Compile();

        // Windows.Graphics.Capture on the same device.
        Check(Marshal.QueryInterface(_device, in IidDxgiDevice, out var dxgi));
        try
        {
            Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
            _winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
        }
        finally
        {
            Marshal.Release(dxgi);
        }

        _item = ItemForMonitor(_monitor);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, _item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        NoBorder(_session);
        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            if (frame.ContentSize.Width != _item!.Size.Width || frame.ContentSize.Height != _item.Size.Height)
            {
                sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, frame.ContentSize);
            }

            var surface = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
            try
            {
                Check(Marshal.QueryInterface(surface, in IidDxgiAccess, out var access));
                nint texture;
                var iid = IidTexture2D;
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(access, 3))(access, &iid, &texture));
                }
                finally
                {
                    Marshal.Release(access);
                }

                // Only the notch window's rectangle is copied out (on the GPU).
                var right = (uint)Math.Min(frame.ContentSize.Width, _ox + _w);
                var bottom = (uint)Math.Min(frame.ContentSize.Height, _oy + _h);
                if (right > _ox && bottom > _oy)
                {
                    var box = new Box { Left = (uint)_ox, Top = (uint)_oy, Front = 0, Right = right, Bottom = bottom, Back = 1 };
                    ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)Slot(_context, 46))(_context, _src, 0, 0, 0, 0, texture, 0, &box);
                    _haveSource = true;
                }

                Marshal.Release(texture);
            }
            finally
            {
                Marshal.Release(surface);
            }

            Process(force: false);
        }
    }

    /// <summary>Runs the lens over the island's rectangle and reads it back (under the lock).</summary>
    private void Process(bool force)
    {
        var s = _shape;
        var w = Math.Clamp(s.Width, 0, _w - Math.Max(0, s.X));
        var h = Math.Clamp(s.Height, 0, _h - Math.Max(0, s.Y));
        if (w < 4 || h < 4 || _shader == 0)
        {
            return;
        }

        var p = stackalloc float[12]
        {
            s.X, s.Y, w, h,
            s.Radius, s.Strength, 22 * s.Scale, 0.5f,
            _w, _h, 1f / _w, 1f / _h,
        };
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, void*, uint, uint, void>)Slot(_context, 48))(_context, _constants, 0, 0, p, 0, 0);
        var cs = _shader;
        var srv = _srcView;
        var uav = _dstView;
        var smp = _sampler;
        var cbuf = _constants;
        nint nothing = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Slot(_context, 69))(_context, cs, null, 0);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(_context, 67))(_context, 0, 1, &srv);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)Slot(_context, 68))(_context, 0, 1, &uav, null);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(_context, 70))(_context, 0, 1, &smp);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(_context, 71))(_context, 0, 1, &cbuf);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)Slot(_context, 41))(_context, (uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)Slot(_context, 68))(_context, 0, 1, &nothing, null);

        var box = new Box { Left = 0, Top = 0, Front = 0, Right = (uint)w, Bottom = (uint)h, Back = 1 };
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)Slot(_context, 46))(_context, _staging, 0, 0, 0, 0, _dst, 0, &box);
        Mapped map;
        if (((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)Slot(_context, 14))(_context, _staging, 0, 1 /* READ */, 0, &map) < 0)
        {
            return;
        }

        GlassFrame result;
        ulong hash = 1469598103934665603UL;
        try
        {
            var stride = w * 4;
            result = Interlocked.Exchange(ref _spare, null) is { } spare && spare.Pixels.Length >= stride * h ? spare : new GlassFrame(new byte[_w * _h * 4], w, h, stride);
            result.Width = w;
            result.Height = h;
            result.Stride = stride;
            long b = 0, g = 0, r = 0, n = 0;
            var max = 0;
            fixed (byte* dst = result.Pixels)
            {
                for (var y = 0; y < h; y++)
                {
                    var row = (byte*)map.Data + (y * map.RowPitch);
                    Buffer.MemoryCopy(row, dst + (y * stride), stride, stride);
                    if ((y & 3) == 0)
                    {
                        for (var x = 0; x < w; x += 7)
                        {
                            var o = row + (x * 4);
                            b += o[0];
                            g += o[1];
                            r += o[2];
                            n++;
                            max = Math.Max(max, Math.Max(o[0], Math.Max(o[1], o[2])));
                            hash = (hash ^ *(uint*)o) * 1099511628211UL;
                        }
                    }
                }
            }

            hash ^= (ulong)((w << 16) ^ h);
            n = Math.Max(1, n);
            result.Average = ((byte)(b / n), (byte)(g / n), (byte)(r / n));
            result.Black = max < 4;
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(_context, 15))(_context, _staging, 0);
        }

        if (!force && hash == _lastHash)
        {
            // Something changed elsewhere on the monitor; the picture behind the island did not.
            Interlocked.Exchange(ref _spare, result);
            return;
        }

        _lastHash = hash;
        Frame?.Invoke(result);
    }

    // ------------------------------------------------------------------ set-up helpers

    private void Texture(ref nint texture, ref nint view, uint format, uint usage, uint bind, uint cpu, bool uav)
    {
        var desc = new Texture2DDesc { Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1, Usage = usage, BindFlags = bind, CpuAccessFlags = cpu };
        nint t;
        Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, nint, nint*, int>)Slot(_device, 5))(_device, &desc, 0, &t));
        texture = t;
        if (bind == 0)
        {
            return;
        }

        nint v;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(_device, uav ? 8 : 7))(_device, t, 0, &v));
        view = v;
    }

    /// <summary>Compiles the lens once (the compiler library is unloaded right after).</summary>
    private nint Compile()
    {
        var library = NativeLibrary.Load("d3dcompiler_47.dll");
        try
        {
            var compile = (delegate* unmanaged[Stdcall]<byte*, nuint, byte*, nint, nint, byte*, byte*, uint, uint, nint*, nint*, int>)NativeLibrary.GetExport(library, "D3DCompile");
            var source = Encoding.ASCII.GetBytes(Shader);
            nint code = 0, errors = 0;
            int hr;
            fixed (byte* src = source)
            fixed (byte* entry = "main\0"u8)
            fixed (byte* target = "cs_5_0\0"u8)
            {
                hr = compile(src, (nuint)source.Length, null, 0, 0, entry, target, 1u << 15 /* OPTIMIZATION_LEVEL3 */, 0, &code, &errors);
            }

            if (errors != 0)
            {
                Marshal.Release(errors);
            }

            Check(hr);
            try
            {
                var pointer = ((delegate* unmanaged[Stdcall]<nint, nint>)Slot(code, 3))(code);
                var size = ((delegate* unmanaged[Stdcall]<nint, nuint>)Slot(code, 4))(code);
                nint shader;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)Slot(_device, 18))(_device, pointer, size, 0, &shader));
                return shader;
            }
            finally
            {
                Marshal.Release(code);
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private static GraphicsCaptureItem ItemForMonitor(nint monitor)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Check(WindowsCreateString(name, (uint)name.Length, out var hstring));
        try
        {
            Check(RoGetActivationFactory(hstring, in IidItemInterop, out var factory));
            try
            {
                nint item;
                var iid = IidItem;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(factory, 4))(factory, monitor, &iid, &item));
                try
                {
                    return GraphicsCaptureItem.FromAbi(item);
                }
                finally
                {
                    Marshal.Release(item);
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }

    /// <summary>No yellow capture border (Windows 11; older systems keep it and say so by failing quietly).</summary>
    private static void NoBorder(GraphicsCaptureSession session)
    {
        var self = ((WinRT.IWinRTObject)session).NativeObject.ThisPtr;
        if (Marshal.QueryInterface(self, in IidSession3, out var s3) == 0)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, byte, int>)Slot(s3, 7))(s3, 0);
            Marshal.Release(s3);
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    /// <summary>A COM method by its place in the object's table (the first argument is always the object).</summary>
    private static void* Slot(nint self, int slot) => (*(void***)self)[slot];

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            if (_pool is not null)
            {
                _pool.FrameArrived -= OnFrameArrived;
            }

            _session?.Dispose();
            _pool?.Dispose();
            (_winrtDevice as IDisposable)?.Dispose();
        }
        catch (COMException)
        {
            // already gone
        }

        foreach (var p in new[] { _shader, _sampler, _constants, _staging, _dstView, _dst, _srcView, _src, _context, _device })
        {
            if (p != 0)
            {
                Marshal.Release(p);
            }
        }

        _shader = _sampler = _constants = _staging = _dstView = _dst = _srcView = _src = _context = _device = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc
    {
        public uint ByteWidth;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
        public uint StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SamplerDesc
    {
        public uint Filter;
        public uint AddressU;
        public uint AddressV;
        public uint AddressW;
        public float MipLodBias;
        public uint MaxAnisotropy;
        public uint ComparisonFunc;
        public float Border0;
        public float Border1;
        public float Border2;
        public float Border3;
        public float MinLod;
        public float MaxLod;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Box
    {
        public uint Left;
        public uint Top;
        public uint Front;
        public uint Right;
        public uint Bottom;
        public uint Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped
    {
        public void* Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int* levels, uint count, uint sdk, out nint device, out int level, out nint context);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string source, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);
}
