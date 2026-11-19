using JackiVpn.Application.Automations;
using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.Services;

namespace JackiVpn.Mobile.Pages;

/// <summary>Settings › Automations: a switch per automation, their options and a log of what they did.</summary>
public sealed class AutomationsPage : ContentPage
{
    private readonly MobileServices _s;
    private readonly Label _log = new() { FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };

    public AutomationsPage(MobileServices services)
    {
        _s = services;
        Title = L.Get("Auto_Title");
        FlowDirection = L.Flow;
        Shell.SetNavBarIsVisible(this, true);
        var fa = L.IsPersian;
        var list = new VerticalStackLayout { Padding = new Thickness(20, 12, 20, 40), Spacing = 6 };
        list.Children.Add(Hint(L.Get("Auto_Hint")));
        foreach (var a in _s.Automations.Automations)
        {
            var id = a.Id;
            var toggle = new Switch { IsToggled = _s.Settings.Current.Automations.IsEnabled(id), VerticalOptions = LayoutOptions.Center };
            toggle.Toggled += (_, e) => _s.Settings.Update(x => x with { Automations = x.Automations.With(id, e.Value) });
            var row = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], Margin = new Thickness(0, 14, 0, 0) };
            row.Add(new VerticalStackLayout { Children = { new Label { Text = AutomationTexts.Title(id, fa), FontAttributes = FontAttributes.Bold, FontSize = 15 }, Hint(AutomationTexts.Hint(id, fa)) } }, 0);
            row.Add(toggle, 1);
            list.Children.Add(row);

            if (id == TrustedNetworksAutomation.AutomationId)
            {
                var names = new Entry { Text = string.Join(", ", _s.Settings.Current.Automations.TrustedNetworks), Placeholder = L.Get("Auto_Trusted") };
                names.Unfocused += (_, _) => _s.Settings.Update(x => x with
                {
                    Automations = x.Automations with { TrustedNetworks = (names.Text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList() },
                });
                list.Children.Add(names);
                list.Children.Add(Hint(L.Get("Auto_TrustedNote")));
            }
            else if (id == NightlyRefreshAutomation.AutomationId)
            {
                var remove = new CheckBox { IsChecked = _s.Settings.Current.Automations.RemoveDeadAfterDays > 0 };
                remove.CheckedChanged += (_, e) => _s.Settings.Update(x => x with { Automations = x.Automations with { RemoveDeadAfterDays = e.Value ? 3 : 0 } });
                list.Children.Add(new HorizontalStackLayout { Children = { remove, new Label { Text = L.Get("Auto_RemoveDead"), VerticalOptions = LayoutOptions.Center, FontSize = 13 } } });
            }
        }

        list.Children.Add(new Label { Text = L.Get("Auto_Log"), FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 20, 0, 4) });
        list.Children.Add(_log);
        Content = new ScrollView { Content = list };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _s.Automations.Log.Added += OnAdded;
        Render();
    }

    protected override void OnDisappearing()
    {
        _s.Automations.Log.Added -= OnAdded;
        base.OnDisappearing();
    }

    private void OnAdded(object? sender, AutomationLogEntry e) => MainThread.BeginInvokeOnMainThread(Render);

    private void Render()
    {
        var entries = _s.Automations.Log.Entries;
        _log.FlowDirection = FlowDirection.LeftToRight;
        _log.Text = entries.Count == 0 ? L.Get("Auto_Nothing") : string.Join(Environment.NewLine, entries.Reverse().Take(50).Select(AutomationTexts.Format));
    }

    private static Label Hint(string text) => new() { Text = text, FontSize = 12, Opacity = 0.7, LineBreakMode = LineBreakMode.WordWrap };
}
