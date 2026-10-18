using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace JackiVpn.Bar;

/// <summary>
/// The browser extension's native host: registered for this user only (HKCU), pointing at the host manifests in
/// IBAR's own folder — each manifest allows exactly one extension (the Chromium one by its fixed id, the Firefox one
/// by its id). Re-registered by IBAR itself at start (from where it is installed), removed on uninstall.
/// </summary>
public static class NativeHost
{
    public const string Name = "com.jacki.ibar";

    /// <summary>The fixed id of the Chromium extension (from the public key in its manifest).</summary>
    public const string ChromiumId = "nhcbmjechdfcppfefbmhmanmbepahnpi";

    public const string FirefoxId = "ibar-downloads@jacki.app";

    private static readonly string[] ChromiumKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts\" + Name, // Chrome, and Opera (which reads Chrome's)
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + Name,
        @"Software\Chromium\NativeMessagingHosts\" + Name,
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + Name,
    ];

    private const string FirefoxKey = @"Software\Mozilla\NativeMessagingHosts\" + Name;

    /// <summary>IBAR's folder holds the host and its manifests (an installed copy, or a test folder).</summary>
    public static bool Available(string folder) =>
        File.Exists(Path.Combine(folder, "ibar-host.exe")) && File.Exists(Path.Combine(folder, Name + ".json")) && File.Exists(Path.Combine(folder, Name + ".firefox.json"));

    /// <summary>Points the browsers at this folder's host. True when every key is in place.</summary>
    public static bool Register(string folder)
    {
        if (!Available(folder))
        {
            return false;
        }

        var ok = true;
        foreach (var key in ChromiumKeys)
        {
            ok &= Set(key, Path.Combine(folder, Name + ".json"));
        }

        return Set(FirefoxKey, Path.Combine(folder, Name + ".firefox.json")) && ok;
    }

    public static void Unregister()
    {
        foreach (var key in new List<string>(ChromiumKeys) { FirefoxKey })
        {
            try
            {
                Registry.CurrentUser.DeleteSubKey(key, throwOnMissingSubKey: false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // stays
            }
        }
    }

    /// <summary>Where each browser's registration points now (for the panel: registered, elsewhere, missing).</summary>
    public static string? RegisteredManifest(bool firefox = false)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(firefox ? FirefoxKey : ChromiumKeys[0]);
            return key?.GetValue(null) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static bool Set(string key, string manifest)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(key);
            if (!string.Equals(k.GetValue(null) as string, manifest, StringComparison.OrdinalIgnoreCase))
            {
                k.SetValue(null, manifest);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// "Install extension": the unpacked extension's folder opens, and the browser's extensions page so it can be
    /// loaded (stores are not used; Chrome, Edge and Opera load it with "Load unpacked", Firefox from its .xpi).
    /// </summary>
    public static void OpenInstall(string folder, string browser)
    {
        var chromium = Path.Combine(folder, "extensions", "chromium");
        var xpi = Path.Combine(folder, "extensions", "ibar-downloads-firefox.xpi");
        try
        {
            if (browser == "firefox")
            {
                Start(TaskSourcesExe("firefox"), "about:addons");
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + xpi + "\"") { UseShellExecute = false });
                return;
            }

            Start(TaskSourcesExe(browser), browser == "edge" ? "edge://extensions" : browser == "opera" ? "opera://extensions" : "chrome://extensions");
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + chromium + "\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // that browser is not installed
        }
    }

    private static string? TaskSourcesExe(string browser) => Sources.TaskSources.ExeOf(browser);

    private static void Start(string? exe, string page)
    {
        if (exe is null)
        {
            return;
        }

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add(page);
        Process.Start(psi);
    }
}
