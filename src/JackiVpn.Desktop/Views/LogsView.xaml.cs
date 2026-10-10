using System;
using System.Windows.Controls;
using JackiVpn.Desktop.ViewModels;

namespace JackiVpn.Desktop.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _vm;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as LogsViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) =>
        {
            Attach(DataContext as LogsViewModel);
            ScrollToEnd();
        };
    }

    private void Attach(LogsViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.LinesAppended -= OnAppended;
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.LinesAppended += OnAppended;
        }
    }

    private void OnAppended(object? sender, EventArgs e)
    {
        if (_vm?.AutoScroll == true)
        {
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        if (List.Items.Count > 0)
        {
            List.ScrollIntoView(List.Items[^1]);
        }
    }
}
