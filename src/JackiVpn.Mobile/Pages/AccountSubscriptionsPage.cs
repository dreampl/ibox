using JackiVpn.Application.Account;
using JackiVpn.Application.Subscriptions;
using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.Services;
using JackiVpn.Security;

namespace JackiVpn.Mobile.Pages;

/// <summary>The subscriptions bought with the signed-in Telegram account: each one added to IBOX with one tap.</summary>
public sealed class AccountSubscriptionsPage : ContentPage
{
    private readonly MobileServices _s;
    private readonly VerticalStackLayout _list = new() { Spacing = 10, Padding = new Thickness(16) };

    public AccountSubscriptionsPage(MobileServices services)
    {
        _s = services;
        Title = Tx("From my Telegram account", "از حساب تلگرامم");
        FlowDirection = L.Flow;
        Content = new ScrollView { Content = _list };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    private static string Tx(string en, string fa) => L.IsPersian ? fa : en;

    private static Label Hint(string t) => new() { Text = t, Style = (Style)Microsoft.Maui.Controls.Application.Current!.Resources["Hint"], LineBreakMode = LineBreakMode.WordWrap };

    private async Task LoadAsync()
    {
        _list.Clear();
        _list.Add(Hint(Tx("Loading your subscriptions…", "در حال دریافت اشتراک‌ها…")));
        IReadOnlyList<AccountLink> links;
        try
        {
            links = await _s.AccountImport.ListAsync(_s.Lifetime);
        }
        catch (Exception e) when (e is InvalidOperationException or ServiceUnreachableException)
        {
            _list.Clear();
            _list.Add(Hint(e.Message));
            return;
        }

        _list.Clear();
        _list.Add(Hint(links.Count == 0
            ? Tx("No subscriptions with a link on this Telegram account yet.", "هنوز اشتراکی با لینک روی این حساب تلگرام نیست.")
            : Tx("The subscriptions you bought with Telegram. Add the ones you want in IBOX.", "اشتراک‌هایی که با تلگرام خریدی. هر کدام را می‌خواهی به IBOX اضافه کن.")));
        foreach (var l in links)
        {
            _list.Add(Row(l));
        }
    }

    private View Row(AccountLink l)
    {
        var c = l.Card;
        var used = c.Unlimited ? Tx("unlimited", "نامحدود") : $"{c.UsedBytes / 1073741824.0:0.#} / {c.LimitBytes / 1073741824.0:0.#} GB";
        var left = c.Left(DateTimeOffset.Now) is { } t
            ? (t.TotalDays >= 1 ? " · " + Tx($"{Math.Floor(t.TotalDays)} days left", $"{Math.Floor(t.TotalDays)} روز مانده") : " · " + Tx("ends today", "امروز تمام می‌شود"))
            : string.Empty;
        var status = Hint($"{c.Server} · {c.Status} · {used}{left}");
        var add = new Button
        {
            Text = l.Added ? Tx("Added", "اضافه شد") : Tx("Add", "افزودن"),
            IsEnabled = !l.Added,
            Style = (Style)Microsoft.Maui.Controls.Application.Current!.Resources["Pill"],
            VerticalOptions = LayoutOptions.Center,
        };
        add.Clicked += async (_, _) =>
        {
            add.IsEnabled = false;
            add.Text = Tx("Adding…", "در حال افزودن…");
            try
            {
                var sub = await _s.AccountImport.AddChosenAsync(c, _s.Lifetime);
                add.Text = Tx("Added", "اضافه شد");
                status.Text = sub.LastError is null
                    ? Tx($"Added with {sub.ProfileCount} server(s).", $"با {sub.ProfileCount} سرور اضافه شد.")
                    : Tx("Added, but the first update failed: ", "اضافه شد، ولی اولین به‌روزرسانی نشد: ") + SecretRedactor.Redact(sub.LastError);
            }
            catch (Exception e) when (e is SubscriptionException or Subscriptions.Fetching.FetchException or InvalidOperationException)
            {
                add.Text = Tx("Add", "افزودن");
                add.IsEnabled = true;
                status.Text = SecretRedactor.Redact(e.Message);
            }
        };
        var grid = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)], ColumnSpacing = 10 };
        grid.Add(new VerticalStackLayout
        {
            Spacing = 2,
            Children = { new Label { Text = string.IsNullOrWhiteSpace(c.Username) ? Tx("Subscription", "اشتراک") : c.Username, FontAttributes = FontAttributes.Bold }, status },
        }, 0, 0);
        grid.Add(add, 1, 0);
        return new Border { Padding = new Thickness(14, 10), StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 }, Content = grid };
    }
}
