using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;

namespace JackiVpn.Desktop.ViewModels;

/// <summary>Human-readable sizes, speeds and durations (shared by IBOX and the notch).</summary>
public static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public const string Unknown = "Unknown";

    public static string Bytes(long? bytes)
    {
        if (bytes is not { } b)
        {
            return Unknown;
        }

        double v = b;
        var i = 0;
        while (v >= 1024 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return i == 0 ? $"{b} B" : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", Inv) + " " + Units[i];
    }

    public static string Speed(long bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Duration(TimeSpan? span)
    {
        if (span is not { } t)
        {
            return Unknown;
        }

        if (t < TimeSpan.Zero)
        {
            return "Expired";
        }

        return t.TotalDays >= 1
            ? $"{(int)t.TotalDays}d {t.Hours}h"
            : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
    }

    public static string Clock(TimeSpan t) => t.TotalHours >= 1
        ? string.Create(Inv, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
        : string.Create(Inv, $"{t.Minutes:00}:{t.Seconds:00}");

    public static string Ms(double? ms) => ms is { } v ? v.ToString("0", Inv) + " ms" : "—";

    public static string When(DateTimeOffset? utc) => utc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Inv) : "Never";

    public static string Lines(IEnumerable<string> values) => string.Join(Environment.NewLine, values);

    public static string[] SplitLines(string? text) =>
        (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
