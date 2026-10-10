using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JackiVpn.Desktop.Services;
using JackiVpn.Desktop.ViewModels;
using JackiVpn.Domain.Routing;
using JackiVpn.Routing;
using Microsoft.Win32;

namespace JackiVpn.Desktop.Views;

/// <summary>Themed modal dialogs built from the shared styles (no extra XAML per dialog).</summary>
public sealed class DialogService : IDialogService
{
    private static Window? Owner => System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? System.Windows.Application.Current?.MainWindow;

    public Task<string?> PromptAsync(string title, string message, string? initial = null, bool multiline = false, bool monospace = false)
    {
        var box = new TextBox
        {
            Text = initial ?? string.Empty,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            Height = multiline ? 320 : double.NaN,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        };
        if (monospace)
        {
            box.SetResourceReference(FrameworkElement.StyleProperty, "TextBox.Mono");
        }

        var ok = Show(title, message, box, "OK", "Cancel", danger: false, width: multiline ? 720 : 480, focus: box);
        return Task.FromResult(ok ? box.Text : null);
    }

    public bool Confirm(string title, string message, bool danger = false) => Show(title, message, null, danger ? "Continue" : "Yes", "Cancel", danger, 460, null);

    public void Info(string title, string message) => Show(title, message, null, "OK", null, false, 480, null);

    public string? OpenFile(string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? SaveFile(string filter, string defaultName)
    {
        var dlg = new SaveFileDialog { Filter = filter, FileName = defaultName, OverwritePrompt = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public RoutingRule? EditRule(RoutingRule? rule, IReadOnlyList<string> ruleSetTags) => new RuleEditor(rule, ruleSetTags).Run(Owner);

    internal static bool Show(string title, string message, UIElement? content, string okText, string? cancelText, bool danger, double width, IInputElement? focus)
    {
        var window = CreateWindow(title, width);
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(Styled(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) }, null));
        if (!string.IsNullOrEmpty(message))
        {
            root.Children.Add(Styled(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxHeight = 420 }, "Text.Label"));
        }

        if (content is not null)
        {
            root.Children.Add(content);
        }

        var result = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        if (cancelText is not null)
        {
            var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            buttons.Children.Add(cancel);
        }

        var ok = new Button { Content = okText, IsDefault = content is not TextBox { AcceptsReturn: true }, MinWidth = 90 };
        ok.SetResourceReference(FrameworkElement.StyleProperty, danger ? "Button.Danger" : "Button.Primary");
        ok.Click += (_, _) =>
        {
            result = true;
            window.DialogResult = true;
        };
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        window.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.Loaded += (_, _) => (focus ?? ok).Focus();
        window.ShowDialog();
        return result;
    }

    internal static Window CreateWindow(string title, double width)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            MaxHeight = SystemParameters.WorkArea.Height * 0.9,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = Owner,
            ShowInTaskbar = false,
            UseLayoutRounding = true,
        };
        w.SetResourceReference(Control.BackgroundProperty, "B.Window");
        w.SetResourceReference(Control.ForegroundProperty, "B.Text");
        w.SetResourceReference(Control.FontFamilyProperty, "UiFont");
        w.FontSize = 13;
        w.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(w);
        return w;
    }

    internal static T Styled<T>(T element, string? style)
        where T : FrameworkElement
    {
        if (style is not null)
        {
            element.SetResourceReference(FrameworkElement.StyleProperty, style);
        }

        return element;
    }
}

/// <summary>Rule editor: name, action, invert and any number of typed conditions; validated before it closes.</summary>
internal sealed class RuleEditor
{
    private readonly RoutingRule? _original;
    private readonly IReadOnlyList<string> _tags;
    private readonly ObservableCollection<ConditionRow> _rows = [];

    public RuleEditor(RoutingRule? original, IReadOnlyList<string> tags)
    {
        _original = original;
        _tags = tags;
        foreach (var c in original?.Conditions ?? [new RuleCondition(RuleConditionType.DomainSuffix, [])])
        {
            _rows.Add(new ConditionRow { Type = c.Type, Values = string.Join(", ", c.Values) });
        }
    }

    public RoutingRule? Run(Window? owner)
    {
        var window = DialogService.CreateWindow(_original is null ? "Add rule" : "Edit rule", 640);
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = _original is null ? "Add rule" : "Edit rule", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

        var name = new TextBox { Text = _original?.Name ?? string.Empty };
        root.Children.Add(Label("Name"));
        root.Children.Add(name);

        var action = new ComboBox
        {
            ItemsSource = new[] { RuleActionType.Proxy, RuleActionType.Direct, RuleActionType.Block },
            SelectedItem = _original?.Action.Type is RuleActionType.Proxy or RuleActionType.Direct or RuleActionType.Block ? _original.Action.Type : RuleActionType.Direct,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        root.Children.Add(Label("Action (Proxy = through the VPN)"));
        root.Children.Add(action);

        root.Children.Add(Label("Conditions — values separated by commas. Destination conditions (domain, suffix, keyword, regex, ip, geoip, geosite, rule-set, private ip) match if any matches; others must all match."));
        var list = new ItemsControl { ItemsSource = _rows };
        var types = Enum.GetValues<RuleConditionType>().Where(t => t != RuleConditionType.NetworkType).ToArray();
        list.ItemTemplate = BuildRowTemplate(types);
        root.Children.Add(list);
        var add = new Button { Content = "Add condition", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        add.Click += (_, _) => _rows.Add(new ConditionRow { Type = RuleConditionType.Domain });
        root.Children.Add(add);

        var invert = DialogService.Styled(new CheckBox { IsChecked = _original?.Invert ?? false, Margin = new Thickness(0, 14, 0, 0), Content = new TextBlock { Text = "Invert (match everything except this)" } }, "Toggle");
        root.Children.Add(invert);

        if (_tags.Count > 0)
        {
            root.Children.Add(DialogService.Styled(new TextBlock { Text = "Available rule-sets: " + string.Join(", ", _tags) }, "Text.Hint"));
        }

        root.Children.Add(DialogService.Styled(new TextBlock { Text = "Examples — suffix: google.com · geosite: category-ir · geoip: ir · ip: 10.0.0.0/8 · process: telegram.exe · port: 443 · ports: 1000:2000" }, "Text.Hint"));

        var error = DialogService.Styled(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) }, "Text.Hint");
        error.SetResourceReference(TextBlock.ForegroundProperty, "B.Danger");
        root.Children.Add(error);

        RoutingRule? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) });
        var ok = DialogService.Styled(new Button { Content = "Save", MinWidth = 90 }, "Button.Primary");
        ok.Click += (_, _) =>
        {
            var rule = new RoutingRule
            {
                Id = _original?.Id ?? Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name.Text) ? "Rule" : name.Text.Trim(),
                Enabled = _original?.Enabled ?? true,
                Action = new RuleAction((RuleActionType)action.SelectedItem),
                Invert = invert.IsChecked == true,
                Conditions = _rows
                    .Select(r => new RuleCondition(r.Type, r.Type == RuleConditionType.PrivateIp ? ["true"] : r.Values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                    .Where(c => c.Values.Count > 0)
                    .ToList(),
                FromPreset = _original?.FromPreset,
                Description = _original?.Description,
            };
            var validation = RuleValidator.Validate(rule, _tags);
            if (!validation.IsValid)
            {
                error.Text = string.Join(Environment.NewLine, validation.Errors.Select(e => "• " + e.Message));
                return;
            }

            result = rule;
            window.DialogResult = true;
        };
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        window.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.Owner = owner;
        window.Loaded += (_, _) => Keyboard.Focus(name);
        window.ShowDialog();
        return result;
    }

    private static TextBlock Label(string text) => DialogService.Styled(new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 6) }, "Text.Label");

    private DataTemplate BuildRowTemplate(RuleConditionType[] types)
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 6));
        var c0 = new FrameworkElementFactory(typeof(ColumnDefinition));
        c0.SetValue(ColumnDefinition.WidthProperty, new GridLength(150));
        var c1 = new FrameworkElementFactory(typeof(ColumnDefinition));
        c1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var c2 = new FrameworkElementFactory(typeof(ColumnDefinition));
        c2.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        grid.AppendChild(c0);
        grid.AppendChild(c1);
        grid.AppendChild(c2);

        var combo = new FrameworkElementFactory(typeof(ComboBox));
        combo.SetValue(ItemsControl.ItemsSourceProperty, types);
        combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new System.Windows.Data.Binding(nameof(ConditionRow.Type)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        combo.SetValue(FrameworkElement.MinWidthProperty, 0.0);
        grid.AppendChild(combo);

        var values = new FrameworkElementFactory(typeof(TextBox));
        values.SetValue(Grid.ColumnProperty, 1);
        values.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 8, 0));
        values.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding(nameof(ConditionRow.Values)) { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        grid.AppendChild(values);

        var remove = new FrameworkElementFactory(typeof(Button));
        remove.SetValue(Grid.ColumnProperty, 2);
        remove.SetValue(ContentControl.ContentProperty, "Remove");
        remove.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is FrameworkElement { DataContext: ConditionRow row })
            {
                _rows.Remove(row);
            }
        }));
        grid.AppendChild(remove);
        return new DataTemplate { VisualTree = grid };
    }

    public sealed class ConditionRow
    {
        public RuleConditionType Type { get; set; }

        public string Values { get; set; } = string.Empty;
    }
}
