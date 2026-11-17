using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
            var copy = new Button { Content = "Copy key", HorizontalAlignment = HorizontalAlignment.Right };
            var content = new StackPanel { Margin = new Thickness(18) };
            content.Children.Add(new TextBlock { Text = "Select a configuration, then import the copied key in AmneziaVPN.", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(options);
            content.Children.Add(copy);
            var dialog = new Window
            {
                Title = "Nex · AmneziaWG",
                Owner = Window.GetWindow(this),
                Width = 430,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = content,
            };
            copy.Click += (_, _) => dialog.DialogResult = options.SelectedIndex >= 0;
            if (dialog.ShowDialog() == true && options.SelectedIndex >= 0)
            {
                Clipboard.SetText(configs[options.SelectedIndex].Key!);
                vm.Message = "AmneziaVPN key copied.";
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
