using System;
using Microsoft.Win32;

namespace JackiVpn.Desktop.Services;

/// <summary>Per-user "start with Windows" via HKCU\...\Run (no admin, no scheduled task).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string MinimizedArgument = "--minimized";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Domain.ProductInfo.ProductName) is string value
            && value.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled && Environment.ProcessPath is { } exe)
        {
            key.SetValue(Domain.ProductInfo.ProductName, $"\"{exe}\" {MinimizedArgument}", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(Domain.ProductInfo.ProductName, throwOnMissingValue: false);
        }
    }
}
