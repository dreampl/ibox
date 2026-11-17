using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using JackiVpn.Domain.Bar;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// Bluetooth batteries in the open island: one row of device icons with their batteries (no words; the name and the
/// percentage are in the tooltip). The row exists only while some connected device reports a battery.
/// </summary>
public sealed partial class NotchWindow
{
    private const double BatteryRowHeight = 34;

    private readonly WrapPanel _batteryRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };

    private bool ShowsBatteries => _batteryRow.Visibility == Visibility.Visible;

    /// <summary>The devices to show (empty = none, the row goes away).</summary>
    public void SetBatteries(IReadOnlyList<BatteryDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var shown = devices.Take(6).ToList();
        var glyphs = _batteryRow.Children.OfType<BatteryGlyph>().ToDictionary(g => g.DeviceId ?? string.Empty, StringComparer.Ordinal);
        _batteryRow.Children.Clear();
        foreach (var d in shown)
        {
            // The same device keeps its glyph, so its fill glides from the old level instead of starting empty.
            if (!glyphs.TryGetValue(d.Id, out var g))
            {
                g = new BatteryGlyph();
            }

            g.Show(d);
            g.Live = _expanded;
            _batteryRow.Children.Add(g);
        }

        var was = ShowsBatteries;
        _batteryRow.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (was != ShowsBatteries || shown.Count > 0)
        {
            Render();
        }
    }

    /// <summary>The endless battery animations follow the island: on while open, off while closed.</summary>
    private void SyncBatteryLife(double expansion)
    {
        if (!ShowsBatteries)
        {
            return;
        }

        var live = expansion > 0.6 && IsVisible;
        foreach (var g in _batteryRow.Children.OfType<BatteryGlyph>())
        {
            g.Live = live;
        }
    }
}
