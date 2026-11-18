using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using JackiVpn.Application.Amnezia;
using JackiVpn.Desktop.Services;
using JackiVpn.Desktop.ViewModels;

namespace JackiVpn.Desktop.Views;

public partial class SubscriptionsView : UserControl
{
    public SubscriptionsView() => InitializeComponent();

    private async void OnNexManagementClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SubscriptionItem item } || DataContext is not SubscriptionsViewModel vm)
        {
            return;
        }

        try
        {
            var uri = await vm.NexManagementUriAsync(item);
            if (uri is not null)
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Launcher errors can contain the subscription URL.
        }

        vm.Message = "Could not open Nex subscription management.";
    }

    private async void OnNexAmneziaClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SubscriptionItem item } button || DataContext is not SubscriptionsViewModel vm)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            var configs = await vm.NexAmneziaConfigsAsync(item);
            if (configs.Count == 0)
            {
                vm.Message = "No AmneziaWG configurations are available for this subscription.";
                return;
            }

            var options = new ListBox
            {
                ItemsSource = configs.Select(config => $"{config.Location} {config.Label}".Trim()).ToArray(),
                SelectedIndex = 0,
                MaxHeight = 320,
                Margin = new Thickness(0, 10, 0, 10),
            };
            var open = new Button { Content = "Open in AmneziaVPN", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
            var copy = new Button { Content = "Copy key" };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(copy);
            buttons.Children.Add(open);
            var content = new StackPanel { Margin = new Thickness(18) };
            content.Children.Add(new TextBlock { Text = "Select a configuration. IBOX opens AmneziaVPN and imports it; then click Connect there.", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(options);
            content.Children.Add(buttons);
            var dialog = new Window
            {
                Title = "Nex · AmneziaWG",
                Owner = Window.GetWindow(this),
                Width = 430,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = content,
            };
            var launch = false;
            open.Click += (_, _) => { launch = true; dialog.DialogResult = options.SelectedIndex >= 0; };
            copy.Click += (_, _) => dialog.DialogResult = options.SelectedIndex >= 0;
            if (dialog.ShowDialog() != true || options.SelectedIndex < 0)
            {
                return;
            }

            var raw = configs[options.SelectedIndex].Key!;
            if (!AmneziaShareKey.TryParse(raw, out var key) || key is null)
            {
                Clipboard.SetText(raw);
                vm.Message = "AmneziaVPN key copied.";
                return;
            }

            vm.RememberAmneziaKey(key);
            if (!launch)
            {
                Clipboard.SetText(key.Key);
                vm.Message = "AmneziaVPN key copied.";
                return;
            }

            switch (AmneziaWindows.Open(key, Clipboard.SetText))
            {
                case AmneziaOpenResult.Imported:
                    vm.Message = "Sent to AmneziaVPN. Click Connect there.";
                    break;
                case AmneziaOpenResult.Opened:
                    vm.Message = "AmneziaVPN opened and the key is copied: Add server → paste the key, then Connect.";
                    break;
                case AmneziaOpenResult.Missing:
                    Clipboard.SetText(key.Key);
                    if (MessageBox.Show(Window.GetWindow(this), "AmneziaVPN is not installed. Open the official download page? The key is copied for later.", "AmneziaVPN", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    {
                        AmneziaWindows.OpenDownloadPage();
                    }

                    vm.Message = "AmneziaVPN key copied.";
                    break;
                default:
                    vm.Message = "Could not open AmneziaVPN. The key is copied: import it there.";
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            vm.Message = "Could not load or copy the AmneziaVPN key.";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnSyncDeviceClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current.MainWindow?.DataContext is MainViewModel main)
        {
            EcosystemView.ShowDialog(main.Ecosystem);
        }
    }
}
