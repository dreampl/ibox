using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace JackiVpn.Bar;

/// <summary>
/// Windows' own banner for apps the user moved to the notch: HKCU …\Notifications\Settings\&lt;app&gt;\ShowBanner = 0
/// (the documented per-app switch the Settings app writes). The notification itself stays in Windows' list, which is
/// what IBAR reads. IBAR marks the values it wrote (IbarSetBanner = 1) and only ever removes its own, so turning the
/// option off — or removing IBAR — puts back exactly what was there.
/// Measured on this Windows 11 build: written directly, the value is not picked up at once (the banner still showed
/// right after; whether it applies after signing in again was not tested), while the notification list kept
/// receiving the notifications.
/// </summary>
public static class Banners
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\";
    private const string Mark = "IbarSetBanner";

    /// <summary>
    /// Banner off for exactly the apps in <paramref name="quiet"/>; every other app in <paramref name="known"/> gets
    /// back what it had (only values IBAR wrote are ever touched). App ids may contain "\" (nested keys).
    /// </summary>
    public static void Sync(IReadOnlyCollection<string> quiet, IEnumerable<string> known)
    {
        ArgumentNullException.ThrowIfNull(quiet);
        ArgumentNullException.ThrowIfNull(known);
        foreach (var app in known.Concat(Marked()).Except(quiet, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            Restore(app);
        }

        foreach (var app in quiet)
        {
            Quiet(app);
        }
    }

    /// <summary>Apps whose banner IBAR turned off.</summary>
    public static IReadOnlyList<string> Marked()
    {
        var list = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root);
            foreach (var name in root?.GetSubKeyNames() ?? [])
            {
                using var app = root!.OpenSubKey(name);
                if (app?.GetValue(Mark) is int mark && mark == 1)
                {
                    list.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // nothing readable
        }

        return list;
    }

    private static void Quiet(string app)
    {
        if (!Valid(app))
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Root + app);
            if (key.GetValue("ShowBanner") is null)
            {
                // Only an app whose banner was on by default: the user's own choice is never overwritten.
                key.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
                key.SetValue(Mark, 1, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // stays as Windows has it
        }
    }

    private static void Restore(string app)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Root + app, writable: true);
            if (key?.GetValue(Mark) is int mark && mark == 1)
            {
                key.DeleteValue("ShowBanner", throwOnMissingValue: false);
                key.DeleteValue(Mark, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // stays
        }
    }

    private static bool Valid(string app) => app.Length is > 0 and < 200 && !app.Contains("..", StringComparison.Ordinal) && app.All(c => !char.IsControl(c));
}
