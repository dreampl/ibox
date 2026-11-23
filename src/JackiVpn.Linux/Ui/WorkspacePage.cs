using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace JackiVpn.Linux.Ui;

public sealed class WorkspacePage(Shell shell, string title, string glyph, params Page[] pages) : Page(shell)
{
    private readonly List<Button> _buttons = [];
    private readonly ContentControl _host = new();
    private Page? _selected;
    public override string Title => title;
    public override string Glyph => glyph;
    protected override bool ScrollContent => false;

    protected override Control Build()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var page in pages)
        {
            var button = new Button { Content = page.Title, Padding = new Thickness(18, 11), BorderThickness = new Thickness(0) };
            button.Click += (_, _) => Select(page);
            _buttons.Add(button);
            bar.Children.Add(button);
        }
        var root = new DockPanel();
        var header = new Border
        {
            Background = new SolidColorBrush(Look.P.Card), CornerRadius = new CornerRadius(12), Padding = new Thickness(4),
            Margin = new Thickness(20, 4, 24, 16),
            Child = new ScrollViewer { Content = bar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_host);
        Select(pages[0]);
        return root;
    }

    public bool Contains(Page page) => pages.Contains(page);

    public void Select(Page page)
    {
        _selected = page;
        for (var i = 0; i < _buttons.Count; i++)
        {
            _buttons[i].Background = pages[i] == page ? new SolidColorBrush(Look.P.Accent, 0.18) : Brushes.Transparent;
            _buttons[i].Foreground = new SolidColorBrush(pages[i] == page ? Look.P.Text : Look.P.TextSecondary);
            _buttons[i].FontWeight = pages[i] == page ? FontWeight.SemiBold : FontWeight.Normal;
        }
        _host.Content = page;
        page.Show();
    }

    public override void OnShown() => (_selected ?? pages[0]).Show();

    public override void Reset()
    {
        foreach (var page in pages) { page.Reset(); }
        _buttons.Clear();
        _selected = null;
        _host.Content = null;
        base.Reset();
    }
}
