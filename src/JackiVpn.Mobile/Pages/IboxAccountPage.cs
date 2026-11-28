using JackiVpn.Application.Account;
using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.Platforms.Android;
using JackiVpn.Mobile.Services;

namespace JackiVpn.Mobile.Pages;

/// <summary>
/// IBOX account (Telegram) on Android: the subscription mini app as native screens (usage, expiry, exit country,
/// allow this network, renew / add volume, prices, receipts) and the music library with a light player that keeps
/// playing in the background. English and Persian.
/// </summary>
public sealed class IboxAccountPage : ContentPage
{
    private readonly MobileServices _s;
    private readonly ContentView _body = new();
    private readonly Button _subTab = new();
    private readonly Button _musicTab = new();
    private bool _music;
    private IDispatcherTimer? _timer;

    private readonly bool _firstRun;
    private Grid? _tabs;
    private CancellationTokenSource? _login;

    /// <summary>Signed out, the page is only the (optional) Telegram sign-in: music and the subscription manager need it.</summary>
    public IboxAccountPage(MobileServices services, bool music = false, bool firstRun = false)
    {
        _s = services;
        _music = music;
        _firstRun = firstRun;
        Title = Tx("IBOX account", "حساب IBOX");
        FlowDirection = L.Flow;
        _subTab.Text = Tx("Subscription", "اشتراک");
        _musicTab.Text = Tx("Music", "موزیک");
        _subTab.Clicked += (_, _) => { _music = false; Render(); };
        _musicTab.Clicked += (_, _) => { _music = true; Render(); };
        var tabs = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8, Margin = new Thickness(16, 12, 16, 0) };
        tabs.Add(_subTab, 0);
        tabs.Add(_musicTab, 1);
        _tabs = tabs;
        var root = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) } };
        root.Add(tabs, 0, 0);
        var manage = new Button { Text = Tx("Devices & Nex Family", "دستگاه‌ها و Nex Family"), Margin = new Thickness(16, 8) };
        manage.Clicked += async (_, _) => await Navigation.PushAsync(new AccountManagementPage(_s));
        root.Add(manage, 0, 1);
        root.Add(_body, 0, 2);
        Content = root;
    }

    private static string Tx(string en, string fa) => L.IsPersian ? fa : en;

    private static Style S(string key) => (Style)Microsoft.Maui.Controls.Application.Current!.Resources[key];

    private static Label Hint(string t) => new() { Text = t, Style = S("Hint"), LineBreakMode = LineBreakMode.WordWrap };

    private static Label Head(string t) => new() { Text = t, Style = S("Title"), FontSize = 18 };

    private static Label Val(string t) => new() { Text = t, Style = S("Value"), FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };

    private static Button Btn(string t, Func<Task> click, bool primary = false)
    {
        var b = new Button { Text = t, Margin = new Thickness(0, 4, 0, 0) };
        if (primary)
        {
            b.Style = S("Primary");
        }

        b.Clicked += async (_, _) => await click();
        return b;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _s.Ibox.Watcher.Updated += OnUpdated;
        _s.Ibox.SignInChanged += OnSignedOut;
        if (_s.Ibox.Account.SignedIn)
        {
            _s.Ibox.Watcher.Start();
        }

        Render();
    }

    protected override void OnDisappearing()
    {
        _login?.Cancel();
        _s.Ibox.Watcher.Updated -= OnUpdated;
        _s.Ibox.SignInChanged -= OnSignedOut;
        if (_firstRun)
        {
            _s.Ibox.MarkSignInOffered();
        }

        _s.Ibox.Watcher.Stop();            // no polling in the background; music keeps playing
        _timer?.Stop();
        base.OnDisappearing();
    }

    private void OnUpdated(object? sender, AccountView e) => MainThread.BeginInvokeOnMainThread(() => { if (!_music) { Render(); } });

    private void OnSignedOut(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(Render);

    private void Render()
    {
        _timer?.Stop();
        if (!_s.Ibox.SignedIn)
        {
            _tabs!.IsVisible = false;
            _body.Content = new ScrollView { Content = SignInOnly() };
            return;
        }

        _tabs!.IsVisible = true;
        _subTab.Opacity = _music ? 0.6 : 1;
        _musicTab.Opacity = _music ? 1 : 0.6;
        _body.Content = _music ? MusicView() : new ScrollView { Content = SubscriptionView() };
    }

    // ------------------------------------------------------------------ sign-in
    private View SignInOnly()
    {
        View? extra = null;
        if (_firstRun)
        {
            var col = new VerticalStackLayout { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            col.Add(Btn(Tx("Not now", "فعلاً نه"), async () =>
            {
                _s.Ibox.MarkSignInOffered();
                await Navigation.PopAsync();
            }));
            col.Add(Hint(Tx("Optional. Without it IBOX works as usual with any subscription you add. You can sign in or out later in Settings.",
                            "اختیاری است. بدون آن IBOX مثل همیشه با هر اشتراکی که اضافه کنی کار می‌کند. بعداً از تنظیمات می‌توانی وارد یا خارج شوی.")));
            extra = col;
        }

        return SignIn(_s.Ibox.Account.Login, Tx("Signing in adds your IBOX music library and lets you manage your subscription inside IBOX: usage, expiry, servers and renewals.",
                                                "با ورود، کتابخانهٔ موزیک IBOX و مدیریت اشتراک داخل خود IBOX (مصرف، انقضا، سرورها و تمدید) فعال می‌شود."), r =>
        {
            _s.Ibox.SignIn(r);
            Render();
        }, extra);
    }

    private View SignIn(TelegramLogin login, string why, Action<LoginResult> done, View? extra)
    {
        var state = Hint(string.Empty);
        var col = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(16) };
        col.Add(Head(Tx("Sign in with Telegram", "ورود با تلگرام")));
        col.Add(Hint(why + Tx(" The first sign-in always goes through Telegram.", " اولین ورود همیشه با تلگرام است.")));
        Button? button = null, viaBot = null;

        // Official first: Telegram's own sign-in page in the browser. Falls back to the bot when the server has none.
        async Task Go(bool official)
        {
            _login?.Cancel();
            _login?.Dispose();
            _login = new CancellationTokenSource();
            var ct = _login.Token;
            button!.IsEnabled = viaBot!.IsEnabled = false;
            try
            {
                var device = IboxServices.DeviceName("Android");
                state.Text = Tx("Preparing sign-in…", "در حال آماده‌سازی ورود…");
                var c = (official ? await login.StartOfficialAsync(device, ct) : null) ?? await login.StartAsync(device, ct);
                state.Text = c.Official
                    ? Tx("The browser opens Telegram's sign-in page. Sign in there, then come back to IBOX: it continues by itself.",
                         "مرورگر صفحهٔ ورود رسمی تلگرام را باز می‌کند. آنجا وارد شو و به IBOX برگرد؛ خودش ادامه می‌دهد.")
                    : Tx($"Telegram opens with the IBOX bot. Check that it shows the code {c.Code}, then press Approve there.",
                         $"تلگرام با ربات IBOX باز می‌شود. ببین کد {c.Code} را نشان می‌دهد، بعد آنجا «تایید» را بزن.");
                try
                {
                    var opened = c.TelegramLink is not null && await Launcher.Default.CanOpenAsync(c.TelegramLink)
                        && await Launcher.Default.OpenAsync(c.TelegramLink);
                    if (!opened && !await Launcher.Default.OpenAsync(c.Link))
                    {
                        throw new LoginFailedException(Tx("No app could open the sign-in link. Install a browser or Telegram and try again.",
                            "لینک ورود باز نشد. مرورگر یا تلگرام را نصب کن و دوباره تلاش کن."));
                    }
                }
                catch (Exception e) when (e is InvalidOperationException or NotSupportedException or global::Android.Content.ActivityNotFoundException)
                {
                    if (!await Launcher.Default.OpenAsync(c.Link))
                    {
                        throw new LoginFailedException(Tx("The sign-in link could not be opened. Try again.", "لینک ورود باز نشد. دوباره تلاش کن."));
                    }
                }

                var progress = new Progress<string>(s =>
                {
                    if (s == "retry")
                    {
                        state.Text = Tx("Connection interrupted. Retrying your sign-in…", "ارتباط قطع شد؛ در حال تلاش دوباره برای تکمیل ورود…");
                    }
                });
                var result = await login.WaitAsync(c, progress, ct);
                ct.ThrowIfCancellationRequested();
                done(result);
            }
            catch (Exception e) when (e is LoginFailedException or ServiceUnreachableException or OperationCanceledException or InvalidOperationException or HttpRequestException)
            {
                login.ReportFailure(e is OperationCanceledException);
                state.Text = e is OperationCanceledException ? Tx("Sign-in cancelled.", "ورود لغو شد.") : Security.SecretRedactor.Redact(e.Message);
            }
            finally
            {
                button!.IsEnabled = viaBot!.IsEnabled = true;
            }
        }

        button = Btn(Tx("Sign in with Telegram", "ورود با تلگرام"), () => Go(official: true), primary: true);
        viaBot = Btn(Tx("Use the IBOX bot instead", "ورود از طریق ربات IBOX"), () => Go(official: false));
        col.Add(button);
        col.Add(viaBot);
        col.Add(state);
        col.Add(Btn(Tx("Cancel sign-in", "لغو ورود"), () => { _login?.Cancel(); return Task.CompletedTask; }));
        if (extra is not null)
        {
            col.Add(extra);
        }

        return col;
    }

    // ------------------------------------------------------------------ subscription
    private View SubscriptionView()
    {
        var acc = _s.Ibox.Account;
        if (!acc.SignedIn)
        {
            return SignIn(acc.Login, Tx("Sign in with Telegram to see and manage your subscription here: usage, expiry, servers and renewals.",
                                       "با تلگرام وارد شو تا اشتراکت را همین‌جا ببینی و مدیریت کنی: مصرف، انقضا، سرورها و تمدید."), r =>
            {
                _s.Ibox.SignIn(r);
                Render();
            }, null);
        }

        var fa = L.IsPersian;
        var view = _s.Ibox.Watcher.Current;
        var status = Hint(view is null ? Tx("Loading…", "در حال دریافت…") : string.Empty);
        var col = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(16) };
        col.Add(Head(view?.User is { } u ? Tx("Signed in as ", "وارد شده: ") + u.Display : Tx("Your subscription", "اشتراک تو")));
        col.Add(status);
        if (view is null)
        {
            _ = Refresh();
            return col;
        }

        async Task Refresh()
        {
            try
            {
                await _s.Ibox.Watcher.RefreshAsync();
            }
            catch (Exception e) when (e is ServiceUnreachableException or InvalidOperationException)
            {
                status.Text = e.Message;
            }
        }

        async Task Act(Func<Task<ApiResult>> call, string ok)
        {
            status.Text = Tx("Working…", "در حال انجام…");
            try
            {
                var r = await call();
                status.Text = r.Ok ? ok : r.Error;
                await _s.Ibox.Watcher.RefreshAsync();
            }
            catch (Exception e) when (e is ServiceUnreachableException or InvalidOperationException or ArgumentException)
            {
                status.Text = e.Message;
            }
        }

        if (view.Subscriptions.Count == 0)
        {
            col.Add(Hint(Tx("No VPN subscription is linked to this Telegram account. Ask support to link your Telegram username.",
                           "هیچ اشتراکی به این تلگرام وصل نیست. از پشتیبانی بخواه نام کاربری تلگرامت را ثبت کند.")));
        }

        foreach (var c in view.Subscriptions)
        {
            var card = new VerticalStackLayout { Spacing = 4 };
            card.Add(new Label { Text = $"{c.Username} · {c.Server}" + (c.Multi ? Tx(" · multi-location", " · مولتی لوکیشن") : string.Empty), Style = S("Title"), FontSize = 16 });
            card.Add(Val(AccountText.Status(c.Status, fa)));
            card.Add(Val(L.N(c.Unlimited ? Tx($"Used {AccountText.Bytes(c.UsedBytes)} (unlimited)", $"مصرف {AccountText.Bytes(c.UsedBytes)} (نامحدود)")
                : Tx($"Used {AccountText.Bytes(c.UsedBytes)} of {AccountText.Bytes(c.LimitBytes)}", $"مصرف {AccountText.Bytes(c.UsedBytes)} از {AccountText.Bytes(c.LimitBytes)}"))));
            if (!c.Unlimited)
            {
                card.Add(new ProgressBar { Progress = c.UsedFraction });
            }

            card.Add(Val(L.N(AccountText.Left(c, DateTimeOffset.Now, fa))));
            if (c.Link is { } link)
            {
                card.Add(Btn(Tx("Copy subscription link", "کپی لینک اشتراک"), async () => { await Clipboard.Default.SetTextAsync(link.AbsoluteUri); status.Text = Tx("Copied.", "کپی شد."); }));
            }

            foreach (var (kind, values) in c.Options)
            {
                foreach (var v in values)
                {
                    card.Add(Btn(L.N(AccountText.Kind(kind, v, fa)), () => Act(() => _s.Ibox.Account.RequestAsync(c.Lid, kind, v),
                        Tx("Request sent; the price and card number arrive here and in Telegram.", "درخواست ثبت شد؛ قیمت و شماره کارت همین‌جا و در تلگرام می‌رسد."))));
                }
            }

            if (c.Nex is { Member: true } nex)
            {
                card.Add(new Label { Text = "Nex", Style = S("Title"), FontSize = 15, Margin = new Thickness(0, 8, 0, 0) });
                if (nex.CanPickExit)
                {
                    var items = new List<NexExit> { new(string.Empty, Tx("Automatic", "خودکار")) };
                    items.AddRange(nex.Exits);
                    var picker = new Picker { Title = Tx("Exit country", "کشور خروجی"), ItemsSource = items, ItemDisplayBinding = new Binding(nameof(NexExit.Label)) };
                    picker.SelectedItem = items.FirstOrDefault(x => x.Backend == nex.Exit) ?? items[0];
                    picker.SelectedIndexChanged += (_, _) =>
                    {
                        if (picker.SelectedItem is NexExit x && x.Backend != nex.Exit)
                        {
                            _ = Act(() => _s.Ibox.Account.NexExitAsync(c.Lid, x.Backend), Tx("Exit country changed.", "کشور خروجی عوض شد."));
                        }
                    };
                    card.Add(picker);
                }

                card.Add(Btn(Tx("Allow this network (L2TP / OpenVPN)", "ثبت اینترنت فعلی (L2TP / OpenVPN)"), () => Act(() => _s.Ibox.Account.NexAllowAsync(c.Lid),
                    Tx("This network's address is allowed. If it says you're on VPN, disconnect IBOX first.", "آدرس این اینترنت ثبت شد. اگر گفت به VPN وصلی، اول IBOX را قطع کن."))));
                card.Add(Btn(Tx("Nex configs", "کانفیگ‌های Nex"), () => ShowConfigs(c.Lid, status)));
            }

            foreach (var r in c.Requests.Where(r => r.Pending))
            {
                card.Add(Val(L.N(AccountText.Kind(r.Kind, r.Value, fa) + ": " + (r.NeedsAccept ? Tx($"price {r.Price:N0} toman", $"قیمت {r.Price:N0} تومان")
                    : r.Receipt ? Tx("receipt sent, waiting for approval", "فیش رسید، منتظر تایید")
                    : r.Pay is { Length: > 0 } ? Tx("pay and send the receipt", "واریز کن و عکس فیش را بفرست")
                    : Tx("waiting for the price", "منتظر قیمت")))));
                if (r.Pay is { Length: > 0 } pay && r.NeedsReceipt)
                {
                    card.Add(new Editor { Text = pay, IsReadOnly = true, AutoSize = EditorAutoSizeOption.TextChanges });
                }

                if (r.NeedsAccept)
                {
                    card.Add(Btn(Tx("Accept", "قبول"), () => Act(() => _s.Ibox.Account.AcceptPriceAsync(r.Id), Tx("Accepted; payment details follow.", "قبول شد؛ اطلاعات پرداخت می‌رسد."))));
                }

                if (r.NeedsReceipt && r.Pay is { Length: > 0 })
                {
                    card.Add(Btn(Tx("Send receipt photo", "ارسال عکس فیش"), async () =>
                    {
                        var photo = (await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 1 }))?.FirstOrDefault();
                        if (photo is null)
                        {
                            return;
                        }

                        await using var stream = await photo.OpenReadAsync();
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms);
                        await Act(() => _s.Ibox.Account.SendReceiptAsync(r.Id, ms.ToArray()), Tx("Receipt sent.", "فیش ارسال شد."));
                    }));
                }

                card.Add(Btn(Tx("Cancel request", "لغو درخواست"), () => Act(() => _s.Ibox.Account.CancelRequestAsync(r.Id), Tx("Cancelled.", "لغو شد."))));
            }

            col.Add(new Border { Style = S("Card"), Content = card });
        }

        col.Add(Hint(Tx("Updates by itself when the server changes (renewal approved, volume added).", "با هر تغییر روی سرور (تایید تمدید، افزایش حجم) خودش به‌روز می‌شود.")));
        col.Add(Btn(Tx("Sign out", "خروج"), async () =>
        {
            await _s.Ibox.SignOutAsync();
            Render();
        }));
        return col;
    }

    private async Task ShowConfigs(string lid, Label status)
    {
        var r = await _s.Ibox.Account.NexConfigsAsync(lid);
        if (!r.Ok)
        {
            status.Text = r.Error;
            return;
        }

        var col = new VerticalStackLayout { Spacing = 6, Padding = new Thickness(16) };
        foreach (var c in AccountText.Configs(r).OfType<System.Text.Json.Nodes.JsonObject>())
        {
            col.Add(new Label { Text = c["tab"]?.ToString() ?? "Config", Style = S("Title"), FontSize = 15 });
            if (c["note"]?.ToString() is { Length: > 0 } note)
            {
                col.Add(Hint(note));
            }

            var text = c["key"]?.ToString() is { Length: > 0 } key ? key : c["conf"]?.ToString()
                ?? string.Join("\n", (c["fields"] as System.Text.Json.Nodes.JsonArray ?? []).Select(f => $"{f?[0]}: {f?[1]}"));
            col.Add(new Editor { Text = text, IsReadOnly = true, HeightRequest = 110, FontFamily = "monospace" });
            col.Add(Btn(Tx("Copy", "کپی"), () => Clipboard.Default.SetTextAsync(text)));
        }

        await Navigation.PushAsync(new ContentPage { Title = Tx("Nex configs", "کانفیگ‌های Nex"), FlowDirection = L.Flow, Content = new ScrollView { Content = col } });
    }

    // ------------------------------------------------------------------ music
    private static DateTime _musicTried = DateTime.MinValue;

    private View MusicView()
    {
        var m = _s.Ibox.Music;
        var ibox = _s.Ibox;
        Action rerender = Render;
        if (!m.SignedIn && ibox.SignedIn && DateTime.UtcNow - _musicTried > TimeSpan.FromSeconds(30))
        {
            // Already signed in to IBOX: open music with the same account, no second sign-in.
            _musicTried = DateTime.UtcNow;
            _ = Connect();
            return new VerticalStackLayout { Padding = new Thickness(16), Children = { Hint(Tx("Opening your music…", "در حال باز کردن موزیک…")) } };

            async Task Connect()
            {
                await ibox.EnsureMusicAsync();
                rerender();
            }
        }

        if (!m.SignedIn)
        {
            var user = new Entry { Placeholder = Tx("Telegram @username or numeric id", "@نام کاربری یا شناسه عددی تلگرام") };
            var pass = new Entry { Placeholder = Tx("Password", "رمز"), IsPassword = true };
            var err = Hint(string.Empty);
            var pw = new VerticalStackLayout { Spacing = 6, Margin = new Thickness(0, 18, 0, 0) };
            pw.Add(new Label { Text = Tx("Or with your backup password", "یا با رمز پشتیبان"), Style = S("Title"), FontSize = 15 });
            pw.Add(user);
            pw.Add(pass);
            pw.Add(Btn(Tx("Sign in", "ورود"), async () =>
            {
                try
                {
                    await m.PasswordLoginAsync(user.Text ?? string.Empty, pass.Text ?? string.Empty, IboxServices.DeviceName("Android"));
                    Render();
                }
                catch (Exception e) when (e is LoginFailedException or ServiceUnreachableException or ArgumentException)
                {
                    err.Text = e.Message;
                }
            }));
            pw.Add(err);
            return new ScrollView { Content = SignIn(m.Login, Tx("Sign in to IBOX music with Telegram to play your library here.", "با تلگرام وارد موزیک IBOX شو تا آهنگ‌هایت را همین‌جا پخش کنی."), r => { m.Accept(r); Render(); }, pw) };
        }

        if (m.NeedsPassword)
        {
            return new ScrollView { Content = PasswordForm(m, true, Render) };
        }

        var player = _s.Ibox.Player ??= new MusicPlayer(new AndroidAudioOutput(global::Android.App.Application.Context), (t, s) => m.SavePositionAsync(t, s));
        var lists = new Picker { Title = Tx("List", "فهرست") };
        var songs = new CollectionView { SelectionMode = SelectionMode.Single };
        songs.ItemTemplate = new DataTemplate(() =>
        {
            var l = new Label { Padding = new Thickness(4, 10), Style = S("Value"), FontSize = 14 };
            l.SetBinding(Label.TextProperty, nameof(MusicTrack.Line));
            return l;
        });
        var title = new Label { Text = player.Track?.Line ?? Tx("Nothing playing", "چیزی پخش نمی‌شود"), Style = S("Title"), FontSize = 15, LineBreakMode = LineBreakMode.TailTruncation };
        var seek = new Slider { Minimum = 0, Maximum = 1 };
        var time = Hint(string.Empty);
        var play = new Button { Text = "▶", Style = S("Primary"), WidthRequest = 64 };
        play.Clicked += async (_, _) => await player.PlayPauseAsync();
        var prev = new Button { Text = "⏮" };
        prev.Clicked += async (_, _) => await player.PreviousAsync();
        var next = new Button { Text = "⏭" };
        next.Clicked += async (_, _) => await player.NextAsync();
        var seeking = false;
        seek.DragStarted += (_, _) => seeking = true;
        seek.DragCompleted += (_, _) =>
        {
            seeking = false;
            player.Seek(TimeSpan.FromSeconds(seek.Value));
        };
        var controls = new HorizontalStackLayout { Spacing = 12, HorizontalOptions = LayoutOptions.Center, FlowDirection = FlowDirection.LeftToRight, Children = { prev, play, next } };
        var bar = new VerticalStackLayout { Spacing = 4, Padding = new Thickness(16, 8), Children = { title, seek, time, controls } };
        var top = new VerticalStackLayout { Padding = new Thickness(16, 8, 16, 0), Spacing = 4, Children = { lists } };
        var more = new HorizontalStackLayout { Spacing = 8 };
        more.Add(Btn(Tx("Change password", "تغییر رمز"), () => Navigation.PushAsync(new ContentPage { Title = Tx("Backup password", "رمز پشتیبان"), FlowDirection = L.Flow, Content = new ScrollView { Content = PasswordForm(m, false, () => _ = Navigation.PopAsync()) } })));
        more.Add(Btn(Tx("Sign out", "خروج"), async () =>
        {
            if (player.IsPlaying)
            {
                await player.PlayPauseAsync();
            }

            await m.SignOutAsync();
            Render();
        }));
        top.Add(more);
        var root = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) } };
        root.Add(top, 0, 0);
        root.Add(songs, 0, 1);
        root.Add(bar, 0, 2);

        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) =>
        {
            title.Text = player.Error ?? player.Track?.Line ?? Tx("Nothing playing", "چیزی پخش نمی‌شود");
            play.Text = player.IsPlaying ? "⏸" : "▶";
            var d = player.Duration ?? TimeSpan.Zero;
            seek.Maximum = Math.Max(1, d.TotalSeconds);
            if (!seeking)
            {
                seek.Value = Math.Min(player.Position.TotalSeconds, seek.Maximum);
            }

            time.Text = L.N($"{player.Position:m\\:ss} / {d:m\\:ss}");
            player.Tick();
        };
        _timer.Start();

        MusicLibrary? lib = null;
        IReadOnlyList<MusicTrack> Pick() => lib is null ? [] : lists.SelectedIndex switch
        {
            1 => lib.Likes,
            var i when i >= 2 && i - 2 < lib.Playlists.Count => lib.Playlists[i - 2].Songs,
            _ => lib.Songs,
        };
        lists.SelectedIndexChanged += (_, _) => songs.ItemsSource = Pick();
        songs.SelectionChanged += (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is MusicTrack t)
            {
                var all = Pick();
                player.Play(all, all.ToList().IndexOf(t));
                songs.SelectedItem = null;
            }
        };
        _ = Load();
        return root;

        async Task Load()
        {
            time.Text = Tx("Loading your library…", "در حال دریافت آهنگ‌ها…");
            try
            {
                lib = await m.LibraryAsync();
                lists.ItemsSource = new[] { Tx("My songs", "آهنگ‌های من") + $" ({lib.Songs.Count})", Tx("Liked", "پسندیده‌ها") + $" ({lib.Likes.Count})" }
                    .Concat(lib.Playlists.Select(p => $"{p.Name} ({p.Songs.Count})")).ToList();
                lists.SelectedIndex = 0;
                time.Text = string.Empty;
            }
            catch (Exception e) when (e is LoginFailedException or ServiceUnreachableException or InvalidOperationException)
            {
                time.Text = e.Message;
                if (!m.SignedIn)
                {
                    Render();
                }
            }
        }
    }

    private static View PasswordForm(MusicClient m, bool first, Action done)
    {
        var old = new Entry { IsPassword = true, Placeholder = Tx("Current password or recovery code", "رمز فعلی یا کد بازیابی"), IsVisible = !first };
        var a = new Entry { IsPassword = true, Placeholder = Tx("New password (8+ characters)", "رمز جدید (حداقل ۸ کاراکتر)") };
        var b = new Entry { IsPassword = true, Placeholder = Tx("Again", "تکرار") };
        var err = Hint(string.Empty);
        var col = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(16) };
        col.Add(Head(first ? Tx("Choose a backup password", "یک رمز پشتیبان انتخاب کن") : Tx("Change your backup password", "تغییر رمز پشتیبان")));
        col.Add(Hint(first
            ? Tx("One time only. With it you can open your music in IBOX even when Telegram is not at hand. It is stored only as a hash on the server; the admin can reset it if you forget it.",
                "فقط یک بار. با آن وقتی تلگرام در دسترس نیست هم می‌توانی موزیکت را در IBOX باز کنی. روی سرور فقط هش آن نگه داشته می‌شود و اگر فراموشش کنی، مدیر می‌تواند ریستش کند.")
            : Tx("Enter your current password, or the recovery code the admin sent you.", "رمز فعلی یا کد بازیابی‌ای که مدیر فرستاده را وارد کن.")));
        col.Add(old);
        col.Add(a);
        col.Add(b);
        col.Add(Btn(Tx("Save", "ذخیره"), async () =>
        {
            if (a.Text != b.Text)
            {
                err.Text = Tx("The two passwords are not the same.", "دو رمز یکی نیستند.");
                return;
            }

            try
            {
                await m.SetPasswordAsync(a.Text ?? string.Empty, first ? null : old.Text);
                done();
            }
            catch (Exception e) when (e is ArgumentException or LoginFailedException or ServiceUnreachableException or InvalidOperationException)
            {
                err.Text = e.Message;
            }
        }, primary: true));
        col.Add(err);
        return col;
    }
}
