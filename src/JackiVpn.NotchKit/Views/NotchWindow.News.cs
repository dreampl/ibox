using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JackiVpn.Domain.Agent;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// The provider's news in the open island: the newest unread message (title, text, how many more) and "Mark as read",
/// which clears it on every device of that user. The row exists only while something is unread.
/// </summary>
public sealed partial class NotchWindow
{
    private const double NewsRowHeight = 84;

    private readonly Border _newsRow = new()
    {
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 8, 0, 0),
        Padding = new Thickness(10, 7, 10, 7),
        CornerRadius = new CornerRadius(12),
        Background = new SolidColorBrush(Color.FromArgb(0x26, 0x5E, 0xB8, 0xFF)),
        Height = NewsRowHeight - 8,
    };

    private readonly TextBlock _newsTitle = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _newsText = new() { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _newsMore = new() { FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), VerticalAlignment = VerticalAlignment.Center };
    private string? _newsId;

    /// <summary>"Mark as read" was pressed for this news id.</summary>
    public event EventHandler<string>? NewsRead;

    private bool ShowsNews => _newsRow.Visibility == Visibility.Visible;

    private void BuildNewsRow()
    {
        var grid = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition(), new RowDefinition { Height = GridLength.Auto } } };
        var head = new DockPanel();
        var mark = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x5E, 0xB8, 0xFF)), Margin = new Thickness(0, 1, 6, 0) };
        DockPanel.SetDock(mark, Dock.Left);
        head.Children.Add(mark);
        head.Children.Add(_newsTitle);
        grid.Children.Add(head);
        Grid.SetRow(_newsText, 1);
        grid.Children.Add(_newsText);
        var foot = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var read = PanelButton("Mark as read · خواندم", Color.FromRgb(0x2A, 0x2F, 0x3A), Colors.White, () =>
        {
            if (_newsId is { } id)
            {
                NewsRead?.Invoke(this, id);
            }
        }, "Clear this message / پاک کردن این پیام");
        read.Margin = new Thickness(0);
        DockPanel.SetDock(read, Dock.Right);
        foot.Children.Add(read);
        foot.Children.Add(_newsMore);
        Grid.SetRow(foot, 2);
        grid.Children.Add(foot);
        _newsRow.Child = grid;
    }

    /// <summary>Unread news, newest first (empty = the row goes away).</summary>
    public void SetNews(IReadOnlyList<AgentNews> unread)
    {
        ArgumentNullException.ThrowIfNull(unread);
        var was = ShowsNews;
        var first = unread.FirstOrDefault();
        _newsId = first?.Id;
        if (first is not null)
        {
            _newsTitle.Text = first.Title ?? (first.Level == "warn" ? "Notice · اطلاعیه" : "News · خبر");
            _newsText.Text = first.Text;
            var rtl = NotchBridge.IsRtl(first.Title + " " + first.Text);
            _newsTitle.FlowDirection = _newsText.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            _newsRow.ToolTip = first.Text;
            _newsMore.Text = unread.Count > 1
                ? $"+{unread.Count - 1} more · بیشتر"
                : first.AtUtc.ToLocalTime().ToString("MMM d, HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        _newsRow.Visibility = first is null ? Visibility.Collapsed : Visibility.Visible;
        if (was != ShowsNews || first is not null)
        {
            Render();
        }
    }
}
