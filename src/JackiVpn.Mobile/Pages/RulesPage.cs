using JackiVpn.Domain.Routing;
using JackiVpn.Mobile.Controls;
using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.Services;
using JackiVpn.Routing;

namespace JackiVpn.Mobile.Pages;

/// <summary>
/// Settings › Rules: the same rules as on Windows and Linux (domain, suffix, keyword, IP, geoip/geosite, port and
/// per-app), each Direct / through the VPN / Block, with order, on/off, edit, delete and clipboard import/export.
/// Every save is validated first and reaches the running connection through the routing watcher.
/// </summary>
public sealed class RulesPage : ContentPage
{
    private static readonly (RuleConditionType Type, string Key)[] Types =
    [
        (RuleConditionType.DomainSuffix, "Cond_Suffix"),
        (RuleConditionType.Domain, "Cond_Domain"),
        (RuleConditionType.DomainKeyword, "Cond_Keyword"),
        (RuleConditionType.IpCidr, "Cond_Ip"),
        (RuleConditionType.GeoIp, "Cond_GeoIp"),
        (RuleConditionType.GeoSite, "Cond_GeoSite"),
        (RuleConditionType.Port, "Cond_Port"),
        (RuleConditionType.AppPackage, "Cond_App"),
    ];

    private readonly MobileServices _s;
    private readonly VerticalStackLayout _rows = new() { Spacing = 8 };

    public RulesPage(MobileServices services)
    {
        _s = services;
        Title = L.Get("Rules_Title");
        FlowDirection = L.Flow;
        Shell.SetNavBarIsVisible(this, true);
        var buttons = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        buttons.Children.Add(Btn(L.Get("Rules_Add"), () => EditAsync(null)));
        buttons.Children.Add(Btn(L.Get("Rules_Import"), ImportAsync));
        buttons.Children.Add(Btn(L.Get("Rules_Export"), ExportAsync));
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(20, 12, 20, 40),
                Children = { new Label { Text = L.Get("Rules_Hint"), FontSize = 12, Opacity = 0.7 }, buttons, _rows },
            },
        };
    }

    private IReadOnlyList<RoutingRule> Rules => _s.Settings.Current.Routing.Rules;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Render();
    }

    private void Render()
    {
        _rows.Children.Clear();
        if (Rules.Count == 0)
        {
            _rows.Children.Add(new Label { Text = L.Get("Rules_None"), FontSize = 13, Opacity = 0.7 });
        }

        for (var i = 0; i < Rules.Count; i++)
        {
            var index = i;
            var rule = Rules[i];
            var on = new Switch { IsToggled = rule.Enabled, VerticalOptions = LayoutOptions.Center };
            on.Toggled += (_, e) => Save(Rules.Select((r, j) => j == index ? r with { Enabled = e.Value } : r).ToList(), render: false);
            var summary = string.Join(" · ", rule.Conditions.Select(c => string.Join(", ", c.Values.Take(3)) + (c.Values.Count > 3 ? "…" : string.Empty)));
            var text = new VerticalStackLayout
            {
                Children =
                {
                    new Label { Text = rule.Name, FontAttributes = FontAttributes.Bold, FontSize = 14 },
                    new Label { Text = $"{ActionName(rule.Action.Type)} — {summary}", FontSize = 12, Opacity = 0.7, LineBreakMode = LineBreakMode.TailTruncation, FlowDirection = FlowDirection.LeftToRight },
                },
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => _ = MenuAsync(index);
            text.GestureRecognizers.Add(tap);
            var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], Padding = new Thickness(12, 8) };
            grid.Add(text, 0);
            grid.Add(on, 1);
            _rows.Children.Add(new Border { Content = grid, StrokeThickness = 1, Stroke = Colors.Gray.WithAlpha(0.3f), StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 } });
        }
    }

    private async Task MenuAsync(int index)
    {
        var choice = await Sheets.PickAsync<string?>(Rules[index].Name,
            [new(L.Get("Rules_Edit"), "edit"), new(L.Get("Rules_Up"), "up"), new(L.Get("Rules_Down"), "down"), new(L.Get("Rules_Delete"), "delete")], null);
        var list = Rules.ToList();
        switch (choice)
        {
            case "edit":
                await EditAsync(index);
                return;
            case "up" when index > 0:
                (list[index - 1], list[index]) = (list[index], list[index - 1]);
                break;
            case "down" when index < list.Count - 1:
                (list[index + 1], list[index]) = (list[index], list[index + 1]);
                break;
            case "delete":
                list.RemoveAt(index);
                break;
            default:
                return;
        }

        Save(list);
    }

    /// <summary>A rule with one condition (as the phone editor offers); rules with more conditions keep the others.</summary>
    private async Task EditAsync(int? index)
    {
        var original = index is { } i ? Rules[i] : null;
        var first = original?.Conditions.FirstOrDefault();
        var type = await Sheets.PickAsync<RuleConditionType?>(L.Get("Rules_Type"), Types.Select(t => new SheetOption<RuleConditionType?>(L.Get(t.Key), t.Type)).ToList(), first?.Type);
        if (type is null)
        {
            return;
        }

        var values = await DisplayPromptAsync(L.Get("Rules_Values"), L.Get(Types.First(t => t.Type == type).Key), L.Get("Ok"), L.Get("Cancel"),
            initialValue: first?.Type == type ? string.Join(", ", first.Values) : string.Empty);
        if (string.IsNullOrWhiteSpace(values))
        {
            return;
        }

        var actions = type == RuleConditionType.AppPackage
            ? new List<SheetOption<RuleActionType?>> { new(L.Get("Act_Direct"), RuleActionType.Direct), new(L.Get("Act_Proxy"), RuleActionType.Proxy) }
            : [new(L.Get("Act_Direct"), RuleActionType.Direct), new(L.Get("Act_Proxy"), RuleActionType.Proxy), new(L.Get("Act_Block"), RuleActionType.Block)];
        var action = await Sheets.PickAsync<RuleActionType?>(L.Get("Rules_Action"), actions, original?.Action.Type);
        if (action is null)
        {
            return;
        }

        var parsed = values.Split([',', '،', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var name = await DisplayPromptAsync(L.Get("Rules_Name"), null, L.Get("Ok"), L.Get("Cancel"), initialValue: original?.Name ?? parsed[0]);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var condition = new RuleCondition(type.Value, parsed);
        var rule = (original ?? new RoutingRule { Id = "m-" + Guid.NewGuid().ToString("N")[..10], Name = name }) with
        {
            Name = name.Trim(),
            Action = new RuleAction(action.Value),
            Conditions = original is null || type == RuleConditionType.AppPackage ? [condition] : [condition, .. original.Conditions.Skip(1)],
        };
        var list = Rules.ToList();
        if (index is { } at)
        {
            list[at] = rule;
        }
        else
        {
            list.Insert(0, rule);
        }

        var check = RuleValidator.Validate(rule, _s.Settings.Current.Routing.RuleSets.Select(r => r.Tag).ToList());
        if (!check.IsValid)
        {
            await DisplayAlertAsync(L.Get("Rules_Invalid"), string.Join("\n", check.Errors.Select(e => e.Message)), L.Get("Ok"));
            return;
        }

        Save(list);
    }

    private async Task ImportAsync()
    {
        var text = await Clipboard.Default.GetTextAsync();
        var (doc, validation) = RulesPortability.Import(text ?? string.Empty);
        if (doc is null || !validation.IsValid)
        {
            await DisplayAlertAsync(L.Get("Rules_Invalid"), string.Join("\n", validation.Errors.Take(5).Select(e => e.Message)), L.Get("Ok"));
            return;
        }

        _s.Settings.Update(x => x with { Routing = x.Routing with { Rules = doc.Rules, RuleSets = doc.RuleSets, Final = doc.Final, Preset = RoutingPreset.Custom } });
        Render();
        await DisplayAlertAsync(L.Get("Rules_Title"), L.F("Rules_Imported", doc.Rules.Count), L.Get("Ok"));
    }

    private async Task ExportAsync()
    {
        var r = _s.Settings.Current.Routing;
        await Clipboard.Default.SetTextAsync(RulesPortability.Export(new RulesDocument { Rules = r.Rules, RuleSets = r.RuleSets, Final = r.Final }));
        await DisplayAlertAsync(L.Get("Rules_Title"), L.Get("Rules_Copied"), L.Get("Ok"));
    }

    private void Save(List<RoutingRule> rules, bool render = true)
    {
        _s.Settings.Update(x => x with { Routing = x.Routing with { Rules = rules, Preset = RoutingPreset.Custom } });
        if (render)
        {
            Render();
        }
    }

    private static string ActionName(RuleActionType a) => a switch
    {
        RuleActionType.Direct => L.Get("Act_Direct"),
        RuleActionType.Block => L.Get("Act_Block"),
        _ => L.Get("Act_Proxy"),
    };

    private static Button Btn(string text, Func<Task> action)
    {
        var b = new Button { Text = text, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(14, 6), FontSize = 13 };
        b.Clicked += (_, _) => _ = action();
        return b;
    }
}
