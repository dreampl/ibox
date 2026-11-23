using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using JackiVpn.Desktop.ViewModels;

namespace JackiVpn.Desktop.Views;

public partial class ServersView : UserControl
{
    private readonly Grid _groups = new();
    private readonly Dictionary<string, int> _groupPages = new(StringComparer.Ordinal);
    private int _pageSize = 5;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private ServersViewModel? _vm;

    public ServersView()
    {
        InitializeComponent();
        var parent = (Grid)List.Parent;
        parent.Children.Remove(List);
        _scroll.Content = _groups;
        parent.Children.Insert(0, _scroll);
        _scroll.SizeChanged += (_, e) =>
        {
            var rows = Math.Clamp((int)((e.NewSize.Height - 110) / 68), 3, 12);
            if (rows != _pageSize) { _pageSize = rows; RenderGroups(); }
        };
        Loaded += (_, _) =>
        {
            if (DataContext is ServersViewModel vm && _vm is null)
            {
                _vm = vm;
                ((INotifyCollectionChanged)vm.View).CollectionChanged += OnGroupsChanged;
            }
            RenderGroups();
        };
        Unloaded += (_, _) =>
        {
            if (_vm is not null) { ((INotifyCollectionChanged)_vm.View).CollectionChanged -= OnGroupsChanged; _vm = null; }
        };
    }

    private void OnSubscriptionsClick(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Current?.DataContext is MainViewModel main) { main.ManageSubscriptionsCommand.Execute(null); }
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RenderGroups();

    private void RenderGroups()
    {
        _groups.Children.Clear();
        _groups.RowDefinitions.Clear();
        _groups.ColumnDefinitions.Clear();
        _groups.ColumnDefinitions.Add(new ColumnDefinition());
        _groups.ColumnDefinitions.Add(new ColumnDefinition());
        if (_vm?.View.Groups is not { } groups) { return; }
        var pageSize = _pageSize;
        var index = 0;
        foreach (CollectionViewGroup group in groups)
        {
            var key = group.Name?.ToString() ?? string.Empty;
            var page = Math.Clamp(_groupPages.GetValueOrDefault(key), 0, Math.Max(0, (group.ItemCount - 1) / pageSize));
            var list = new ListBox
            {
                ItemsSource = group.Items.Cast<ServerItem>().Skip(page * pageSize).Take(pageSize).ToArray(),
                ItemTemplate = List.ItemTemplate,
                Style = List.Style,
                ItemContainerStyle = List.ItemContainerStyle,
                Tag = _vm,
            };
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(list, true);
            list.SelectionChanged += (_, _) => { if (list.SelectedItem is ServerItem item && _vm is not null) { _vm.Selected = item; } };
            var panel = new DockPanel();
            var heading = new TextBlock { Text = $"{group.Name} · {group.ItemCount}", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 4, 8, 12), TextTrimming = TextTrimming.CharacterEllipsis };
            DockPanel.SetDock(heading, Dock.Top);
            panel.Children.Add(heading);
            if (group.ItemCount > pageSize)
            {
                var pager = new DockPanel { Margin = new Thickness(4, 8, 4, 0) };
                var previous = DialogService.Styled(new Button { Content = ButtonIcons.Content("previous", "Previous"), IsEnabled = page > 0, Padding = new Thickness(8, 6, 8, 6) }, "Button.Ghost");
                var next = DialogService.Styled(new Button { Content = ButtonIcons.Content("next", "Next"), IsEnabled = (page + 1) * pageSize < group.ItemCount, Padding = new Thickness(8, 6, 8, 6) }, "Button.Ghost");
                previous.Click += (_, _) => { _groupPages[key] = page - 1; RenderGroups(); };
                next.Click += (_, _) => { _groupPages[key] = page + 1; RenderGroups(); };
                DockPanel.SetDock(previous, Dock.Left);
                DockPanel.SetDock(next, Dock.Right);
                pager.Children.Add(previous);
                pager.Children.Add(next);
                pager.Children.Add(new TextBlock { Text = $"{page * pageSize + 1}–{Math.Min((page + 1) * pageSize, group.ItemCount)} / {group.ItemCount}", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                DockPanel.SetDock(pager, Dock.Bottom);
                panel.Children.Add(pager);
            }
            panel.Children.Add(list);
            if (index % 2 == 0) { _groups.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            var card = new Border { Child = panel, Margin = new Thickness(0, 0, 14, 18), VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(card, index % 2);
            Grid.SetRow(card, index / 2);
            _groups.Children.Add(card);
            index++;
        }
    }
}
