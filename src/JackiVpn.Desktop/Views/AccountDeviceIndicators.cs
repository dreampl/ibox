using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using JackiVpn.Application.Account;
using JackiVpn.Desktop.ViewModels;

namespace JackiVpn.Desktop.Views;

public sealed class AccountDeviceIndicators : WrapPanel
{
    private AccountControl? _control;
    private IboxServices? _ibox;
    public AccountDeviceIndicators()
    {
        Margin = new Thickness(0,0,0,8);
        Loaded += (_, _) =>
        {
            _ibox = (MainWindow.Current?.DataContext as MainViewModel)?.Ibox;
            if (_ibox is not null) { _ibox.ControlAttached += Attached; }
            Attach();
        };
        Unloaded += (_, _) => { if (_control is not null) { _control.Changed -= Changed; } _control = null; if (_ibox is not null) { _ibox.ControlAttached -= Attached; } _ibox = null; };
    }
    private void Attached(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Attach);
    private void Attach()
    {
        if (_control is not null) { _control.Changed -= Changed; }
        _control = _ibox?.Control;
        if (_control is not null) { _control.Changed += Changed; }
        Render();
    }
    private void Changed(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Render);
    private void Render()
    {
        Children.Clear();
        foreach (var device in _control?.Devices.Where(d => d.Connected) ?? [])
        {
            var glyph = new TextBlock { Text = device.Platform == "Android" ? "\uE8EA" : "\uE7F4", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 18 };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "B.Success");
            var button = new Button { Content = glyph, ToolTip = device.Tooltip, Margin = new Thickness(0,0,8,0), Padding = new Thickness(10,8,10,8) };
            System.Windows.Automation.AutomationProperties.SetName(button, device.Tooltip);
            button.Click += (_, _) => (MainWindow.Current?.DataContext as MainViewModel)?.ManageAccountCommand.Execute(null);
            Children.Add(button);
        }
        Visibility = Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
