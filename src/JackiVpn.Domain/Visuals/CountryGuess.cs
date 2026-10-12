namespace JackiVpn.Domain.Visuals;

/// <summary>
/// The country a server name points at, for its flag before IBOX has seen the real exit: a flag emoji
/// ("🇩🇪 Frankfurt"), a country name ("Germany", "آلمان") or a two-letter code as its own word ("DE-1", "[NL]").
/// Only countries the map knows; null when nothing matches.
/// </summary>
public static class CountryGuess
{
    private static readonly (string Code, string[] Names)[] Names =
    [
        ("US", ["united states", "usa", "america", "آمریکا", "امریکا"]),
        ("CA", ["canada", "کانادا"]),
        ("GB", ["united kingdom", "england", "london", "britain", "انگلیس", "انگلستان"]),
        ("DE", ["germany", "deutschland", "frankfurt", "آلمان"]),
        ("NL", ["netherlands", "holland", "amsterdam", "هلند"]),
        ("FR", ["france", "paris", "فرانسه"]),
        ("FI", ["finland", "helsinki", "فنلاند"]),
        ("SE", ["sweden", "stockholm", "سوئد"]),
        ("NO", ["norway", "نروژ"]),
        ("DK", ["denmark", "دانمارک"]),
        ("CH", ["switzerland", "zurich", "سوئیس"]),
        ("AT", ["austria", "vienna", "اتریش"]),
        ("IT", ["italy", "milan", "ایتالیا"]),
        ("ES", ["spain", "madrid", "اسپانیا"]),
        ("PL", ["poland", "warsaw", "لهستان"]),
        ("RO", ["romania", "رومانی"]),
        ("TR", ["turkey", "türkiye", "turkiye", "istanbul", "ترکیه"]),
        ("AE", ["emirates", "uae", "dubai", "امارات", "دبی"]),
        ("RU", ["russia", "moscow", "روسیه"]),
        ("UA", ["ukraine", "اوکراین"]),
        ("AM", ["armenia", "ارمنستان"]),
        ("GE", ["georgia", "tbilisi", "گرجستان"]),
        ("IR", ["iran", "tehran", "ایران", "تهران"]),
        ("IN", ["india", "هند"]),
        ("SG", ["singapore", "سنگاپور"]),
        ("HK", ["hong kong", "hongkong", "هنگ کنگ"]),
        ("JP", ["japan", "tokyo", "ژاپن"]),
        ("KR", ["korea", "seoul", "کره"]),
        ("AU", ["australia", "sydney", "استرالیا"]),
        ("LT", ["lithuania", "لیتوانی"]),
        ("LV", ["latvia", "لتونی"]),
        ("EE", ["estonia", "استونی"]),
        ("CZ", ["czech", "prague"]),
        ("HU", ["hungary", "مجارستان"]),
        ("BG", ["bulgaria", "بلغارستان"]),
        ("GR", ["greece", "یونان"]),
        ("CY", ["cyprus", "قبرس"]),
        ("QA", ["qatar", "قطر"]),
        ("SA", ["saudi", "عربستان"]),
        ("IL", ["israel"]),
        ("KZ", ["kazakhstan", "قزاقستان"]),
        ("BR", ["brazil", "برزیل"]),
    ];

    public static string? FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Flag emoji: two regional indicator symbols.
        var runes = name.EnumerateRunes().ToList();
        for (var i = 0; i + 1 < runes.Count; i++)
        {
            if (runes[i].Value is >= 0x1F1E6 and <= 0x1F1FF && runes[i + 1].Value is >= 0x1F1E6 and <= 0x1F1FF)
            {
                var code = new string([(char)('A' + runes[i].Value - 0x1F1E6), (char)('A' + runes[i + 1].Value - 0x1F1E6)]);
                return WorldMap.CountryPoint(code) is null ? null : code == "UK" ? "GB" : code;
            }
        }

        var lower = name.ToLowerInvariant();
        foreach (var (code, names) in Names)
        {
            if (names.Any(n => lower.Contains(n, StringComparison.Ordinal)))
            {
                return code;
            }
        }

        // A two-letter code standing alone ("DE-1", "[NL] fast", "us 2").
        foreach (var word in name.Split([' ', '-', '_', '|', '[', ']', '(', ')', '.', ',', '#', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length == 2 && word.All(char.IsAsciiLetterUpper))
            {
                var code = word == "UK" ? "GB" : word;
                if (WorldMap.CountryPoint(code) is not null)
                {
                    return code;
                }
            }
        }

        return null;
    }
}
