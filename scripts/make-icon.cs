// Renders the IBOX app icon (src/JackiVpn.Desktop/Assets/jacki.ico, multi-size) and a 512 px PNG preview from
// the vector geometry in assets/brand (no binary design sources in the repository).
//   dotnet run scripts/make-icon.cs -- <output.ico>
// Sizes ≥ 40 px show the wordmark; smaller ones the "O" box with its blue pixel (the wordmark is unreadable there).
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var output = Path.GetFullPath(args.Length > 0 ? args[0] : "jacki.ico");
var white = new SolidColorBrush(Colors.White);
var blue = new SolidColorBrush(Color.FromRgb(0x3D, 0x9B, 0xFF));
string[] word =
[
    "M1,0H13V127H1Z M19,0H29V127H19Z M38,0H45V127H38Z M62,0H68V127H62Z",
    "F0 M96 0H176Q190 0 190 14V50H204Q218 50 218 64V97Q218 127 188 127H96ZM123 27H163V50H123ZM123 73H191V100H123Z",
    "M272 0H368V127H250Q242 127 242 119V30H269V100H341V27H272Z",
    "F1 M416 0H448L514 127H482Z M376 127L479 0H511L408 127Z",
];
const string box = "M272 0H368V127H250Q242 127 242 119V30H269V100H341V27H272Z";

byte[] Png(int size)
{
    var v = new DrawingVisual();
    using (var dc = v.RenderOpen())
    {
        dc.DrawRoundedRectangle(Brushes.Black, null, new Rect(0, 0, size, size), size * 0.22, size * 0.22);
        if (size >= 40)
        {
            var s = size * 0.8 / 515;
            dc.PushTransform(new TranslateTransform((size - (515 * s)) / 2, (size - (127 * s)) / 2));
            dc.PushTransform(new ScaleTransform(s, s));
            foreach (var d in word)
            {
                dc.DrawGeometry(white, null, Geometry.Parse(d));
            }
        }
        else
        {
            var s = size * 0.66 / 127;
            dc.PushTransform(new TranslateTransform((size / 2.0) - (305 * s), (size - (127 * s)) / 2));
            dc.PushTransform(new ScaleTransform(s, s));
            dc.DrawGeometry(white, null, Geometry.Parse(box));
        }

        dc.DrawRectangle(blue, null, new Rect(309, 53, 22, 22));
    }

    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(v);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

// Small frames as 32-bit DIBs (every Windows API reads them); 256 px as PNG.
byte[] Dib(int size)
{
    var png = Png(size);
    var decoder = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    var src = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
    var stride = size * 4;
    var pixels = new byte[stride * size];
    src.CopyPixels(pixels, stride, 0);
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40);
    w.Write(size);
    w.Write(size * 2);
    w.Write((short)1);
    w.Write((short)32);
    w.Write(0);
    w.Write(0);
    w.Write(0);
    w.Write(0);
    w.Write(0);
    w.Write(0);
    for (var y = size - 1; y >= 0; y--)
    {
        w.Write(pixels, y * stride, stride);
    }

    var maskStride = ((size + 31) / 32) * 4;
    w.Write(new byte[maskStride * size]);
    return ms.ToArray();
}

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var frames = sizes.Select(s => s >= 256 ? Png(s) : Dib(s)).ToArray();
using (var fs = File.Create(output))
using (var w = new BinaryWriter(fs))
{
    w.Write((short)0);
    w.Write((short)1);
    w.Write((short)sizes.Length);
    var offset = 6 + (16 * sizes.Length);
    for (var i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(frames[i].Length);
        w.Write(offset);
        offset += frames[i].Length;
    }

    foreach (var f in frames)
    {
        w.Write(f);
    }
}

File.WriteAllBytes(Path.ChangeExtension(output, ".png"), Png(512));
Console.WriteLine(output);