using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace JackiVpn.Desktop.Views;

internal class DrawerDialog : Window
{
    private DispatcherFrame? _frame;
    private bool? _result;
    private bool _shown;
    public new bool? DialogResult { get => _result; set { _result = value; Close(); } }
    public new void Show()
    {
        if (_shown || MainWindow.Current is not { } main) { return; }
        _shown = true;
        var content = Content;
        Content = null;
        var panel = (content as ScrollViewer)?.Content as StackPanel ?? content as StackPanel;
        if (panel?.Children.Count > 0 && panel.Children[0] is TextBlock heading && heading.Text == Title) { heading.Visibility = Visibility.Collapsed; }
        var host = new ContentControl { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        host.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is Button { IsCancel: true }) { DialogResult = false; } }));
        host.Loaded += (_, _) => RaiseEvent(new RoutedEventArgs(LoadedEvent));
        host.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && e.OriginalSource is not TextBox { AcceptsReturn: true })
            {
                var button = FindDefault(host);
                if (button is not null && button.IsEnabled) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
            }
        };
        main.OpenDialog(Title, host, () =>
        {
            _shown = false;
            OnClosed(EventArgs.Empty);
            if (_frame is not null) { _frame.Continue = false; }
        });
    }
    public new bool? ShowDialog()
    {
        _frame = new DispatcherFrame();
        Show();
        if (_shown) { Dispatcher.PushFrame(_frame); }
        _frame = null;
        return _result;
    }
    private static Button? FindDefault(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Button { IsDefault: true } button) { return button; }
            if (FindDefault(child) is { } found) { return found; }
        }
        return null;
    }
    public new void Close() { if (_shown) { MainWindow.Current?.CloseDrawer(); } }
}
