using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using JackiVpn.Application.Amnezia;
using Microsoft.Win32;

namespace JackiVpn.Desktop.Services;

public enum AmneziaOpenResult
{
    /// <summary>AmneziaVPN is not installed.</summary>
    Missing,

    /// <summary>The key was handed to AmneziaVPN through its registered <c>vpn:</c> link.</summary>
    Imported,

    /// <summary>AmneziaVPN was started; the key is on the clipboard for "Add server → paste".</summary>
    Opened,

    Failed,
}

/// <summary>
/// AmneziaVPN on Windows has no command line or IPC for other apps: no import switch and no connect switch. IBOX
/// uses what exists: the <c>vpn:</c> URL protocol when the installer registered it, otherwise it starts the app with
/// the key on the clipboard. Connecting stays a tap in AmneziaVPN.
/// </summary>
public static class AmneziaWindows
{
    public const string DownloadUrl = "https://amnezia.org/downloads";

    public static string? FindExecutable()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var app = uninstall.OpenSubKey(name);
                        if (app?.GetValue("DisplayName") is not string display || !display.StartsWith("AmneziaVPN", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (app.GetValue("InstallLocation") is string dir && Exe(dir) is { } fromDir)
                        {
                            return fromDir;
                        }

                        if (app.GetValue("DisplayIcon") is string icon && Path.GetDirectoryName(icon.Split(',')[0].Trim('"')) is { } iconDir && Exe(iconDir) is { } fromIcon)
                        {
                            return fromIcon;
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                }
            }
        }

        return Exe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AmneziaVPN"))
            ?? Exe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AmneziaVPN"));
    }

    /// <summary>True when the <c>vpn:</c> protocol opens AmneziaVPN (not some other program).</summary>
    public static bool HandlesVpnLinks()
    {
        try
        {
            using var command = Registry.ClassesRoot.OpenSubKey(@"vpn\shell\open\command");
            return command?.GetValue(null) is string line && line.Contains("amnezia", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>Copies the key, then imports it by link or starts AmneziaVPN.</summary>
    public static AmneziaOpenResult Open(AmneziaShareKey key, Action<string> copy)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(copy);
        var exe = FindExecutable();
        if (exe is null)
        {
            return AmneziaOpenResult.Missing;
        }

        copy(key.Key);
        if (HandlesVpnLinks())
        {
            try
            {
                Process.Start(new ProcessStartInfo(key.Key) { UseShellExecute = true })?.Dispose();
                return AmneziaOpenResult.Imported;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // A very long key can exceed the shell's URL limit; the clipboard path below still works.
            }
        }

        return Show() ? AmneziaOpenResult.Opened : AmneziaOpenResult.Failed;
    }

    /// <summary>Brings AmneziaVPN to the front (so the user can disconnect it there).</summary>
    public static bool Show()
    {
        if (FindExecutable() is not { } exe)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    public static void OpenDownloadPage() =>
        Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true })?.Dispose();

    public static bool TunnelUp()
    {
        try
        {
            return AmneziaDetector.AnyTunnelUp(NetworkInterface.GetAllNetworkInterfaces()
                .Select(n => new NetAdapterInfo(n.Name, n.Description, n.OperationalStatus == OperationalStatus.Up)));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static string? Exe(string dir)
    {
        var path = Path.Combine(dir.Trim().Trim('"'), "AmneziaVPN.exe");
        return File.Exists(path) ? path : null;
    }
}
