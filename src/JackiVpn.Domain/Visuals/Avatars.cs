using System.Globalization;

namespace JackiVpn.Domain.Visuals;

/// <summary>One shape of an avatar (SVG path data on a 100 × 100 canvas); colours are ARGB, 0 = none.</summary>
public sealed record AvatarLayer(string Data, uint Fill, uint Stroke = 0, double StrokeWidth = 0);

/// <summary>A cartoon avatar: a round background and its shapes, drawn in order (clipped to the circle).</summary>
public sealed record AvatarArt(string Id, string Name, uint Background, IReadOnlyList<AvatarLayer> Layers);

/// <summary>
/// The profile pictures to choose from: ten flat cartoon characters, drawn as vectors (sharp at any size, the same on
/// Windows, Linux and Android, nothing to download).
/// </summary>
public static class Avatars
{
    private const uint Ink = 0xFF1F2937;
    private const uint Mouth = 0xFF7A2E2E;
    private const uint White = 0xFFFFFFFF;
    private const uint Blush = 0x55F87171;

    private static readonly (uint Skin, uint Shade)[] Skins =
    [
        (0xFFF8D5B8, 0xFFE3B394), // light
        (0xFFEBB58E, 0xFFD39A70), // medium
        (0xFFC68B63, 0xFFAD724D), // tan
        (0xFF8D5A3B, 0xFF744832), // dark
    ];

    public static IReadOnlyList<AvatarArt> All { get; } =
    [
        Boy(),
        Girl(),
        Bearded(),
        Hijab(),
        Curly(),
        Gamer(),
        Bun(),
        Grandpa(),
        Robot(),
        Cat(),
    ];

    public static AvatarArt? Find(string? id) => id is null ? null : All.FirstOrDefault(a => a.Id == id);

    /// <summary>The first avatar nobody uses yet (a new profile gets its own face).</summary>
    public static string NextFree(IEnumerable<string?> used)
    {
        var taken = used.Where(u => u is not null).ToHashSet(StringComparer.Ordinal);
        return All.FirstOrDefault(a => !taken.Contains(a.Id))?.Id ?? All[0].Id;
    }

    // ------------------------------------------------------------------ characters

    private static AvatarArt Boy()
    {
        var (skin, shade) = Skins[0];
        const uint hair = 0xFF5B3A26;
        return new("boy", "Sam", 0xFFBFDBFE,
        [
            Shirt(0xFF2563EB), Neck(shade), .. Ears(skin), Head(skin),
            new(P("M 31.5,47 C 30,30 39,23.5 50.5,23.5 C 62,23.5 70.5,30 68.5,47 C 67,40 63,36.5 58,36 C 53,32 45,32.5 40,35.5 C 36,37.5 33,41.5 31.5,47 Z"), hair),
            new(P("M 45,26 C 49,19 58,19.5 61,24.5 C 56,23 50,23.5 45,26 Z"), hair),
            .. Face(shade),
        ]);
    }

    private static AvatarArt Girl()
    {
        var (skin, shade) = Skins[1];
        const uint hair = 0xFF2B2118;
        return new("girl", "Mina", 0xFFFBCFE8,
        [
            new(P("M 30,52 C 26,30 37,19.5 50,19.5 C 63,19.5 74,30 70,52 C 72,62 74,72 72,82 C 64,86 36,86 28,82 C 26,72 28,62 30,52 Z"), hair),
            Shirt(0xFFEC4899), Neck(shade), Head(skin),
            new(P("M 31.5,46 C 31,31 40,24.5 50.5,24.5 C 61,24.5 69.5,31 68.5,46 C 65.5,38.5 59.5,34 52.5,33.5 C 47.5,37 40,41 31.5,46 Z"), hair),
            new(E(63, 31, 3.2, 2.2), 0xFFF472B6),
            .. Face(shade, lashes: true),
        ]);
    }

    private static AvatarArt Bearded()
    {
        var (skin, shade) = Skins[2];
        const uint hair = 0xFF1F1A17;
        return new("beard", "Reza", 0xFF99F6E4,
        [
            Shirt(0xFF0F766E), Neck(shade), .. Ears(skin), Head(skin),
            new(P("M 31.5,45 C 30.5,29.5 39.5,24 50.5,24 C 61.5,24 70,29.5 68.5,45 C 66,39 61,36.5 50,36.5 C 39,36.5 34,39 31.5,45 Z"), hair),
            new(P("M 32.5,50 C 33,64.5 41,72 50,72 C 59,72 67,64.5 67.5,50 C 65,56.5 61,59.5 56,60 C 53,58.5 47,58.5 44,60 C 39,59.5 35,56.5 32.5,50 Z"), hair),
            .. Face(shade, mouth: false),
            new(P("M 45,58.5 Q 50,61.5 55,58.5 Q 50,60.2 45,58.5 Z"), 0xFFE5A9A0),
            new(E(43.5, 48, 5.6, 5), 0x1AFFFFFF, Ink, 1.7),
            new(E(56.5, 48, 5.6, 5), 0x1AFFFFFF, Ink, 1.7),
            new(P("M 49.1,47.3 Q 50,46.4 50.9,47.3"), 0, Ink, 1.5),
        ]);
    }

    private static AvatarArt Hijab()
    {
        var (skin, shade) = Skins[0];
        return new("hijab", "Sara", 0xFFDDD6FE,
        [
            new(P("M 27,60 C 24,32 36,19 50,19 C 64,19 76,32 73,60 C 74,72 66,80 50,81 C 34,80 26,72 27,60 Z"), 0xFF7C3AED),
            new(P("M 16,100 C 18,80 32,72 50,74 C 68,72 82,80 84,100 Z"), 0xFF6D28D9),
            new(E(50, 48, 14.8, 17.6), skin),
            new(P("M 35.2,49 C 35,36.5 41.5,30 50,30 C 58.5,30 65,36.5 64.8,49"), 0, 0xFF8B5CF6, 2.2),
            .. Face(shade, lashes: true),
        ]);
    }

    private static AvatarArt Curly()
    {
        var (skin, shade) = Skins[3];
        const uint hair = 0xFF22160F;
        return new("curly", "Kian", 0xFFFED7AA,
        [
            Shirt(0xFFF97316), Neck(shade), .. Ears(skin), Head(skin),
            // One shape per curl: overlapping circles in one path would cut holes into each other.
            .. new (double X, double Y, double R)[] { (36, 33, 8), (43.5, 26.5, 8.5), (52.5, 24.5, 9), (61.5, 28, 8.5), (67.5, 37, 7), (32, 41, 6.5), (48, 32, 7), (57, 33, 6.5), (68.5, 45, 4.5), (31.5, 48, 4) }
                .Select(c => new AvatarLayer(E(c.X, c.Y, c.R, c.R), hair)),
            .. new (double X, double Y)[] { (40, 28), (50, 21), (60, 25), (35, 37) }.Select(c => new AvatarLayer(E(c.X, c.Y, 2.2, 1.4), 0x33FFFFFF)),
            .. Face(shade, ink: 0xFF111111),
        ]);
    }

    private static AvatarArt Gamer()
    {
        var (skin, shade) = Skins[1];
        return new("gamer", "Ali", 0xFFBBF7D0,
        [
            Shirt(0xFF15803D), Neck(shade), Head(skin),
            new(P("M 31.5,42 C 31,27 40,21.5 50.5,21.5 C 61,21.5 69.5,27 68.5,42 C 62,39.5 39,39.5 31.5,42 Z"), 0xFF22C55E),
            new(P("M 47,40.5 C 58,38.5 72,39.5 81,42.5 C 78,45.8 64,45.2 50,44.2 Z"), 0xFF166534),
            .. Face(shade),
            new(P("M 28.5,50 C 27.5,23 72.5,23 71.5,50"), 0, 0xFF111827, 3.2),
            new(Rounded(23, 43, 9, 15, 3), 0xFF111827),
            new(Rounded(68, 43, 9, 15, 3), 0xFF111827),
            new(P("M 28,56 C 28,64 34,66.5 40,64"), 0, 0xFF111827, 1.6),
            new(E(41, 63.8, 1.8, 1.4), 0xFF111827),
        ]);
    }

    private static AvatarArt Bun()
    {
        var (skin, shade) = Skins[2];
        const uint hair = 0xFF9A3412;
        return new("bun", "Nika", 0xFFFEF08A,
        [
            new(E(50, 18.5, 8.5, 7.5), hair),
            Shirt(0xFFCA8A04), Neck(shade), .. Ears(skin), Head(skin),
            new(P("M 31.5,47 C 30,30 39,24 50,24 C 61,24 70,30 68.5,47 C 66,37 59,32 50,32 C 41,32 34,37 31.5,47 Z"), hair),
            new(E(32, 56.5, 1.7, 1.7), 0xFFFBBF24),
            new(E(68, 56.5, 1.7, 1.7), 0xFFFBBF24),
            .. Face(shade, lashes: true),
        ]);
    }

    private static AvatarArt Grandpa()
    {
        var (skin, shade) = Skins[0];
        const uint grey = 0xFFCBD5E1;
        return new("grandpa", "Baba", 0xFFE2E8F0,
        [
            Shirt(0xFF475569), new(P("M 43,74 L 50,82 L 57,74 Z"), White), Neck(shade), .. Ears(skin), Head(skin),
            new(E(33.5, 41, 4.5, 7), grey),
            new(E(66.5, 41, 4.5, 7), grey),
            new(P("M 42,28 Q 50,23.5 58,28"), 0, grey, 2.2),
            new(P("M 43,32.5 Q 50,30.8 57,32.5"), 0, shade, 1),
            .. Face(shade, mouth: false),
            new(P("M 42,55.5 C 45,52 49,53 50,55 C 51,53 55,52 58,55.5 C 55,58.5 52,57.5 50,56.5 C 48,57.5 45,58.5 42,55.5 Z"), grey),
            new(P("M 46,60 Q 50,62 54,60"), 0, Mouth, 1.4),
            new(E(43.5, 48, 4.8, 4.4), 0, 0xFF64748B, 1.3),
            new(E(56.5, 48, 4.8, 4.4), 0, 0xFF64748B, 1.3),
            new(P("M 48.3,47.6 Q 50,46.8 51.7,47.6"), 0, 0xFF64748B, 1.2),
        ]);
    }

    private static AvatarArt Robot()
    {
        return new("robot", "Bolt", 0xFFA5F3FC,
        [
            new(P("M 22,100 C 23,84 34,77 50,77 C 66,77 77,84 78,100 Z"), 0xFF64748B),
            new(Rounded(45, 64, 10, 14, 1), 0xFF475569),
            new(P("M 50,26 L 50,16.5"), 0, 0xFF64748B, 2.2),
            new(E(50, 14.5, 3.2, 3.2), 0xFFF43F5E),
            new(E(27.5, 47, 3, 5.5), 0xFF64748B),
            new(E(72.5, 47, 3, 5.5), 0xFF64748B),
            new(Rounded(28, 26, 44, 42, 10), 0xFF94A3B8),
            new(Rounded(33, 33.5, 34, 27, 7), 0xFF0F172A),
            new(E(43, 45.5, 4.2, 4.2), 0xFF22D3EE),
            new(E(57, 45.5, 4.2, 4.2), 0xFF22D3EE),
            new(E(44.2, 44.3, 1.2, 1.2), White),
            new(E(58.2, 44.3, 1.2, 1.2), White),
            new(P("M 43,53.5 Q 50,58 57,53.5"), 0, 0xFF22D3EE, 2),
            new(Rounded(42, 84, 16, 7, 2), 0xFF22D3EE),
        ]);
    }

    private static AvatarArt Cat()
    {
        const uint fur = 0xFFFB923C;
        const uint line = 0xFF7C2D12;
        return new("cat", "Pishi", 0xFFFECDD3,
        [
            new(P("M 24,100 C 26,82 38,74 50,74 C 62,74 74,82 76,100 Z"), fur),
            new(P("M 29,40 L 32,15.5 L 47,30 Z"), fur),
            new(P("M 71,40 L 68,15.5 L 53,30 Z"), fur),
            new(P("M 32.5,33 L 34.2,21 L 42.5,29 Z"), 0xFFFDA4AF),
            new(P("M 67.5,33 L 65.8,21 L 57.5,29 Z"), 0xFFFDA4AF),
            new(E(50, 50, 23, 20.5), fur),
            new(P("M 45.5,33.5 L 47,39.5 M 50,32 L 50,38.5 M 54.5,33.5 L 53,39.5"), 0, 0xFFEA580C, 1.8),
            new(E(50, 59, 10.5, 7.5), 0xFFFFEDD5),
            new(E(41.5, 48.5, 2.7, 3.4), Ink),
            new(E(58.5, 48.5, 2.7, 3.4), Ink),
            new(E(42.4, 47.4, 0.9, 0.9), White),
            new(E(59.4, 47.4, 0.9, 0.9), White),
            new(P("M 47.5,55.5 L 52.5,55.5 L 50,58.5 Z"), 0xFFF472B6),
            new(P("M 50,58.5 Q 47.5,62.5 44.5,61 M 50,58.5 Q 52.5,62.5 55.5,61"), 0, line, 1.3),
            new(P("M 38,57.5 L 26,55.5 M 38,60.5 L 26.5,62 M 62,57.5 L 74,55.5 M 62,60.5 L 73.5,62"), 0, line, 0.9),
            new(E(36, 55, 3, 1.8), Blush),
            new(E(64, 55, 3, 1.8), Blush),
        ]);
    }

    // ------------------------------------------------------------------ parts

    private static AvatarLayer Shirt(uint color) => new(P("M 16,100 C 17,82 31,73 50,73 C 69,73 83,82 84,100 Z"), color);

    private static AvatarLayer Neck(uint shade) => new(P("M 43,60 L 57,60 L 57,74 C 54,77 46,77 43,74 Z"), shade);

    private static AvatarLayer Head(uint skin) => new(E(50, 46, 18, 21), skin);

    private static AvatarLayer[] Ears(uint skin) => [new(E(32, 48, 4, 5.5), skin), new(E(68, 48, 4, 5.5), skin)];

    /// <summary>Eyes with a shine, brows, nose, a smile and cheeks, for a head at (50, 46).</summary>
    private static AvatarLayer[] Face(uint shade, bool mouth = true, bool lashes = false, uint ink = Ink)
    {
        var parts = new List<AvatarLayer>
        {
            new(E(43.5, 48, 2.3, 2.8), ink),
            new(E(56.5, 48, 2.3, 2.8), ink),
            new(E(44.3, 47, 0.8, 0.8), White),
            new(E(57.3, 47, 0.8, 0.8), White),
            new(P("M 39.5,42.5 Q 43.5,40.2 47.5,42"), 0, ink, 1.6),
            new(P("M 52.5,42 Q 56.5,40.2 60.5,42.5"), 0, ink, 1.6),
            new(P("M 50,50 Q 48.3,54.5 50.8,55.2"), 0, shade, 1.4),
            new(E(39.5, 55, 3, 1.8), Blush),
            new(E(60.5, 55, 3, 1.8), Blush),
        };
        if (lashes)
        {
            parts.Add(new(P("M 40.8,46.2 L 39.4,45.2 M 59.2,46.2 L 60.6,45.2"), 0, ink, 1.1));
        }

        if (mouth)
        {
            parts.Add(new(P("M 44.5,58 Q 50,63.5 55.5,58 Q 50,60 44.5,58 Z"), Mouth));
        }

        return [.. parts];
    }

    private static string P(string data) => data;

    /// <summary>An ellipse as path data.</summary>
    private static string E(double cx, double cy, double rx, double ry) => string.Create(CultureInfo.InvariantCulture,
        $"M {cx - rx},{cy} A {rx},{ry} 0 1 0 {cx + rx},{cy} A {rx},{ry} 0 1 0 {cx - rx},{cy} Z ");

    /// <summary>A rounded rectangle as path data.</summary>
    private static string Rounded(double x, double y, double w, double h, double r) => string.Create(CultureInfo.InvariantCulture,
        $"M {x + r},{y} L {x + w - r},{y} A {r},{r} 0 0 1 {x + w},{y + r} L {x + w},{y + h - r} A {r},{r} 0 0 1 {x + w - r},{y + h} L {x + r},{y + h} A {r},{r} 0 0 1 {x},{y + h - r} L {x},{y + r} A {r},{r} 0 0 1 {x + r},{y} Z");
}
