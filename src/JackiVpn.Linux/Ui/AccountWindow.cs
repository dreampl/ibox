using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using JackiVpn.Application.Account;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

/// <summary>
/// IBOX account (Telegram) on Linux: the subscription mini app as native screens and the music library with a light
/// player (mpv). Same features as the Windows window.
/// </summary>
internal static class AccountWindow
{
    public const int SubscriptionTab = 0;
    public const int MusicTab = 1;

    private static Window? _open;
    private static TabControl? _tabs;

    /// <summary>Music and the subscription manager are for signed-in users only: signed out, this asks to sign in first.</summary>
    public static void Open(Window owner, IboxServices ibox, int tab = SubscriptionTab)
    {
        if (!ibox.SignedIn)
        {
            ShowSignIn(owner, ibox, firstRun: false, then: () => Open(owner, ibox, tab));
            return;
        }

        if (_open is not null)
        {
            _tabs!.SelectedIndex = tab;
            _open.Activate();
            return;
        }

        var w = new Window { Title = "IBOX account", Width = 620, Height = 720, Background = B(CardBg), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var sub = new ContentControl();
        var music = new ContentControl();
        _tabs = new TabControl
        {
            Margin = new Thickness(12),
            Items =
            {
                new TabItem { Header = "Subscription", Content = new ScrollViewer { Content = sub } },
                new TabItem { Header = "Music", Content = music },
            },
            SelectedIndex = tab,
        };
        w.Content = _tabs;

        void RenderSub() => sub.Content = ibox.Account.SignedIn ? Subscription(w, ibox, RenderSub)
            : SignIn(ibox.Account.Login, "Sign in with Telegram to see and manage your subscription here: usage, expiry, servers and renewals.", r =>
            {
                ibox.SignIn(r);
                RenderSub();
            }, null);

        void RenderMusic() => music.Content = MusicPage(w, ibox, RenderMusic);

        EventHandler<AccountView> updated = (_, _) => Dispatcher.UIThread.Post(RenderSub);
        // Signed out (here, in Profiles, or by the server): music and the subscription manager close.
        EventHandler signedOut = (_, _) => Dispatcher.UIThread.Post(() => { if (!ibox.SignedIn) { w.Close(); } });
        ibox.Watcher.Updated += updated;
        ibox.SignInChanged += signedOut;
        w.Closed += (_, _) =>
        {
            ibox.Watcher.Updated -= updated;
            ibox.SignInChanged -= signedOut;
            _open = null;
            _tabs = null;
        };
        if (ibox.Account.SignedIn)
        {
            ibox.Watcher.Start();
        }

        RenderSub();
        RenderMusic();
        _open = w;
        w.Show(owner);
    }

    private static TextBlock W(TextBlock t)
    {
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    private static StackPanel Col(params Control[] c)
    {
        var s = new StackPanel { Spacing = 6, Margin = new Thickness(16) };
        s.Children.AddRange(c);
        return s;
    }

    private static Window? _signIn;

    /// <summary>
    /// The optional Telegram sign-in. On first run it says what signing in adds; "Not now" keeps IBOX fully usable
    /// with subscriptions added the normal way. <paramref name="then"/> runs after a successful sign-in.
    /// </summary>
    public static void ShowSignIn(Window owner, IboxServices ibox, bool firstRun, Action? then = null)
    {
        if (_signIn is not null)
        {
            _signIn.Activate();
            return;
        }

        var w = new Window { Title = "Sign in with Telegram", Width = 520, SizeToContent = SizeToContent.Height, Background = B(CardBg), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var extra = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        extra.Children.Add(Ghost(firstRun ? "Not now" : "Cancel", w.Close));
        if (firstRun)
        {
            extra.Children.Add(W(Hint("Optional. Without it IBOX works as usual with any subscription you add. You can sign in or out later from your profile.")));
        }

        w.Content = SignIn(ibox.Account.Login, "Signing in adds your IBOX music library and lets you manage your subscription inside IBOX: usage, expiry, servers and renewals.", r =>
        {
            ibox.SignIn(r);
            w.Close();
            then?.Invoke();
        }, extra);
        w.Closed += (_, _) =>
        {
            ibox.MarkSignInOffered();
            _signIn = null;
        };
        _signIn = w;
        w.Show(owner);
    }

    private static Control SignIn(TelegramLogin login, string why, Action<LoginResult> done, Control? extra)
    {
        var state = W(Hint(string.Empty));
        CancellationTokenSource? cts = null;
        Button? button = null, viaBot = null;
        button = Primary("Sign in with Telegram", () => _ = Go(official: true));
        viaBot = Ghost("Use the IBOX bot instead", () => _ = Go(official: false));

        // Official first: Telegram's own sign-in page in the browser. Falls back to the bot when the server has none.
        async Task Go(bool official)
        {
            cts?.Cancel();
            cts = new CancellationTokenSource();
            button!.IsEnabled = viaBot!.IsEnabled = false;
            try
            {
                var device = IboxServices.DeviceName("Linux");
                var c = (official ? await login.StartOfficialAsync(device, cts.Token) : null) ?? await login.StartAsync(device, cts.Token);
                state.Text = c.Official
                    ? $"Your browser opens Telegram's sign-in page. Sign in there, then come back: IBOX continues by itself.\nIf the browser did not open: {c.Link}"
                    : $"Telegram opens with the IBOX bot. Check that it shows the code {c.Code}, then press Approve there.\nIf Telegram did not open: {c.Link}";
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", (c.TelegramLink ?? c.Link).AbsoluteUri) { UseShellExecute = false })?.Dispose();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // the link is on screen
                }

                done(await login.WaitAsync(c, new Progress<string>(s =>
                {
                    if (s == "seen")
                    {
                        state.Text = c.Official ? "Checking your Telegram sign-in…" : $"Code {c.Code}: now press Approve in Telegram.";
                    }
                }), cts.Token));
            }
            catch (Exception e) when (e is LoginFailedException or ServiceUnreachableException or OperationCanceledException)
            {
                state.Text = e is OperationCanceledException ? string.Empty : e.Message;
            }
            finally
            {
                button!.IsEnabled = viaBot!.IsEnabled = true;
            }
        }

        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(button);
        buttons.Children.Add(viaBot);
        var col = Col(T("Sign in with Telegram", 17, FontWeight.SemiBold), W(Hint(why + " The first sign-in always goes through Telegram.")), buttons, state);
        if (extra is not null)
        {
            col.Children.Add(extra);
        }

        return col;
    }

    private static Control Subscription(Window w, IboxServices ibox, Action rerender)
    {
        var view = ibox.Watcher.Current;
        var status = W(Hint(view is null ? "Loading…" : string.Empty));
        var root = Col(
            new DockPanel
            {
                Children =
                {
                    Ghost("Sign out", () => _ = Out()),
                    T(view?.User is { } u ? "Signed in as " + u.Display : "Your subscription", 17, FontWeight.SemiBold),
                },
            },
            status);
        DockPanel.SetDock(((DockPanel)root.Children[0]).Children[0], Dock.Right);
        async Task Out()
        {
            ibox.Watcher.Stop();
            await ibox.Account.SignOutAsync();
            rerender();
        }

        if (view is null)
        {
            _ = ibox.Watcher.RefreshAsync().ContinueWith(t => { if (t.Exception is not null) { Dispatcher.UIThread.Post(() => status.Text = t.Exception.InnerException?.Message); } }, TaskScheduler.Default);
            return root;
        }

        if (view.Subscriptions.Count == 0)
        {
            root.Children.Add(W(Hint("No VPN subscription is linked to this Telegram account. Ask support to link your Telegram username.")));
        }

        async Task Act(Func<Task<ApiResult>> call, string ok)
        {
            status.Text = "Working…";
            try
            {
                var r = await call();
                status.Text = r.Ok ? ok : r.Error;
                await ibox.Watcher.RefreshAsync();
            }
            catch (Exception e) when (e is ServiceUnreachableException or InvalidOperationException or ArgumentException)
            {
                status.Text = e.Message;
            }
        }

        foreach (var c in view.Subscriptions)
        {
            var card = new StackPanel { Spacing = 4 };
            card.Children.Add(T($"{c.Username} · {c.Server}" + (c.Multi ? " · multi-location" : string.Empty), 15, FontWeight.SemiBold));
            card.Children.Add(Caption("Status: " + AccountText.Status(c.Status, false)));
            card.Children.Add(Caption(c.Unlimited ? $"Used {AccountText.Bytes(c.UsedBytes)} (unlimited)" : $"Used {AccountText.Bytes(c.UsedBytes)} of {AccountText.Bytes(c.LimitBytes)}"));
            if (!c.Unlimited)
            {
                card.Children.Add(new ProgressBar { Maximum = 1, Value = c.UsedFraction, Height = 6 });
            }

            card.Children.Add(Caption(AccountText.Left(c, DateTimeOffset.Now, false) + (c.Expires is { } e ? $" (until {e.LocalDateTime:yyyy-MM-dd HH:mm})" : string.Empty)));
            var row = new WrapPanel();
            if (c.Link is { } link)
            {
                row.Children.Add(Outline("Copy subscription link", () => _ = w.Clipboard?.SetTextAsync(link.AbsoluteUri)));
            }

            foreach (var (kind, values) in c.Options)
            {
                foreach (var v in values)
                {
                    row.Children.Add(Outline(AccountText.Kind(kind, v, false), () => _ = Act(() => ibox.Account.RequestAsync(c.Lid, kind, v), "Request sent; the price and card number arrive here and in Telegram.")));
                }
            }

            card.Children.Add(row);
            if (c.Nex is { Member: true } nex)
            {
                card.Children.Add(T("Nex", 14, FontWeight.SemiBold));
                if (nex.CanPickExit)
                {
                    var items = new List<(string, string)> { (string.Empty, "Automatic") };
                    items.AddRange(nex.Exits.Select(x => (x.Backend, x.Label)));
                    card.Children.Add(Row("Exit country", null, Combo(items, nex.Exit, x => { if (x != nex.Exit) { _ = Act(() => ibox.Account.NexExitAsync(c.Lid, x), "Exit country changed."); } })));
                }

                card.Children.Add(new WrapPanel
                {
                    Children =
                    {
                        Outline("Allow this network (L2TP / OpenVPN)", () => _ = Act(() => ibox.Account.NexAllowAsync(c.Lid), "This network's address is allowed. If it says you're on VPN, disconnect IBOX first.")),
                        Outline("Nex configs", () => _ = Configs(w, ibox, c.Lid, status)),
                    },
                });
            }

            foreach (var r in c.Requests.Where(r => r.Pending))
            {
                var rr = new WrapPanel();
                rr.Children.Add(Caption($"{AccountText.Kind(r.Kind, r.Value, false)}: " + (r.NeedsAccept ? $"price {r.Price:N0} toman " : r.Receipt ? "receipt sent, waiting for approval " : r.Pay is { Length: > 0 } ? "pay and send the receipt " : "waiting for the price ")));
                if (r.NeedsAccept)
                {
                    rr.Children.Add(Outline("Accept", () => _ = Act(() => ibox.Account.AcceptPriceAsync(r.Id), "Accepted; payment details follow.")));
                }

                if (r.NeedsReceipt && r.Pay is { Length: > 0 })
                {
                    rr.Children.Add(Outline("Send receipt…", () => _ = Receipt(r.Id)));
                }

                rr.Children.Add(Outline("Cancel", () => _ = Act(() => ibox.Account.CancelRequestAsync(r.Id), "Cancelled.")));
                card.Children.Add(rr);
                if (r.Pay is { Length: > 0 } pay && r.NeedsReceipt)
                {
                    card.Children.Add(new TextBox { Text = pay, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
                }
            }

            root.Children.Add(Card(card));
        }

        root.Children.Add(W(Hint("Updates by itself when the server changes (renewal approved, volume added).")));
        return root;

        async Task Receipt(string rid)
        {
            var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.jpg", "*.jpeg", "*.png"] }] });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                await Act(() => ibox.Account.SendReceiptAsync(rid, File.ReadAllBytes(path)), "Receipt sent.");
            }
        }
    }

    private static async Task Configs(Window owner, IboxServices ibox, string lid, TextBlock status)
    {
        var r = await ibox.Account.NexConfigsAsync(lid);
        if (!r.Ok)
        {
            status.Text = r.Error;
            return;
        }

        var col = Col();
        foreach (var c in AccountText.Configs(r).OfType<System.Text.Json.Nodes.JsonObject>())
        {
            col.Children.Add(T(c["tab"]?.ToString() ?? "Config", 14, FontWeight.SemiBold));
            if (c["note"]?.ToString() is { Length: > 0 } note)
            {
                col.Children.Add(W(Hint(note)));
            }

            var text = c["key"]?.ToString() is { Length: > 0 } key ? key : c["conf"]?.ToString()
                ?? string.Join("\n", (c["fields"] as System.Text.Json.Nodes.JsonArray ?? []).Select(f => $"{f?[0]}: {f?[1]}"));
            col.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120, FontFamily = new FontFamily("monospace") });
        }

        new Window { Title = "Nex configs", Width = 560, Height = 640, Background = B(CardBg), Content = new ScrollViewer { Content = col } }.Show(owner);
    }

    private static Control MusicPage(Window w, IboxServices ibox, Action rerender)
    {
        var m = ibox.Music;
        if (!m.SignedIn)
        {
            var user = new TextBox { PlaceholderText = "Telegram @username or numeric id" };
            var pass = new TextBox { PasswordChar = '•', PlaceholderText = "Password" };
            var err = W(Hint(string.Empty));
            var pw = new StackPanel { Spacing = 6, Margin = new Thickness(0, 18, 0, 0), Children = { T("Or with your backup password", 14, FontWeight.SemiBold), user, pass, Outline("Sign in", () => _ = Pw()), err } };
            async Task Pw()
            {
                try
                {
                    await m.PasswordLoginAsync(user.Text ?? string.Empty, pass.Text ?? string.Empty, IboxServices.DeviceName("Linux"));
                    rerender();
                }
                catch (Exception e) when (e is LoginFailedException or ServiceUnreachableException or ArgumentException)
                {
                    err.Text = e.Message;
                }
            }

            return new ScrollViewer { Content = SignIn(m.Login, "Sign in to IBOX music with Telegram to play your library here.", r => { m.Accept(r); rerender(); }, pw) };
        }

        if (m.NeedsPassword)
        {
            return PasswordForm(m, true, rerender);
        }

        var player = ibox.Player ??= new MusicPlayer(new MpvAudioOutput(), (t, s) => m.SavePositionAsync(t, s));
        var lists = new ComboBox { MinWidth = 220 };
        var songs = new ListBox();
        var title = T(player.Track?.Line ?? "Nothing playing", 14, FontWeight.SemiBold);
        var seek = new Slider { Minimum = 0, Maximum = 1 };
        var time = Hint(string.Empty);
        var play = Primary("▶", () => _ = player.PlayPauseAsync());
        var bar = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                title, seek, time,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 8, Children = { Outline("⏮", () => _ = player.PreviousAsync()), play, Outline("⏭", () => _ = player.NextAsync()) } },
            },
        };
        var top = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        Ghost("Change password", () => new Window { Title = "Backup password", Width = 420, SizeToContent = SizeToContent.Height, Background = B(CardBg), Content = PasswordForm(m, false, () => { }) }.Show(w)),
                        Ghost("Sign out", () => _ = Out()),
                    },
                },
                lists,
            },
        };
        DockPanel.SetDock(top.Children[0], Dock.Right);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bar, Dock.Bottom);
        var root = new DockPanel { Margin = new Thickness(16), Children = { top, bar, songs } };
        async Task Out()
        {
            if (player.IsPlaying)
            {
                await player.PlayPauseAsync();
            }

            await m.SignOutAsync();
            rerender();
        }

        var userSeeking = false;
        seek.PointerPressed += (_, _) => userSeeking = true;
        seek.PointerReleased += (_, _) =>
        {
            userSeeking = false;
            player.Seek(TimeSpan.FromSeconds(seek.Value));
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            title.Text = player.Error ?? player.Track?.Line ?? "Nothing playing";
            play.Content = player.IsPlaying ? "⏸" : "▶";
            var d = player.Duration ?? TimeSpan.Zero;
            seek.Maximum = Math.Max(1, d.TotalSeconds);
            if (!userSeeking)
            {
                seek.Value = Math.Min(player.Position.TotalSeconds, seek.Maximum);
            }

            time.Text = $"{player.Position:m\\:ss} / {d:m\\:ss}";
            player.Tick();
        };
        root.AttachedToVisualTree += (_, _) => timer.Start();
        root.DetachedFromVisualTree += (_, _) => timer.Stop();

        MusicLibrary? lib = null;
        IReadOnlyList<MusicTrack> Pick() => lib is null ? [] : lists.SelectedIndex switch
        {
            1 => lib.Likes,
            var i when i >= 2 && i - 2 < lib.Playlists.Count => lib.Playlists[i - 2].Songs,
            _ => lib.Songs,
        };
        lists.SelectionChanged += (_, _) => songs.ItemsSource = Pick().Select(s => s.Line).ToList();
        songs.DoubleTapped += (_, _) =>
        {
            if (songs.SelectedIndex >= 0)
            {
                player.Play(Pick(), songs.SelectedIndex);
            }
        };
        _ = Load();
        return root;

        async Task Load()
        {
            time.Text = "Loading your library…";
            try
            {
                lib = await m.LibraryAsync();
                lists.ItemsSource = new[] { $"My songs ({lib.Songs.Count})", $"Liked ({lib.Likes.Count})" }.Concat(lib.Playlists.Select(p => $"{p.Name} ({p.Songs.Count})")).ToList();
                lists.SelectedIndex = 0;
                time.Text = lib.Resume is { } r ? $"Last played: {r.Line}" : string.Empty;
            }
            catch (Exception e) when (e is LoginFailedException or ServiceUnreachableException or InvalidOperationException)
            {
                time.Text = e.Message;
                if (!m.SignedIn)
                {
                    rerender();
                }
            }
        }
    }

    private static Control PasswordForm(MusicClient m, bool first, Action done)
    {
        var old = new TextBox { PasswordChar = '•', PlaceholderText = "Current password or recovery code", IsVisible = !first };
        var a = new TextBox { PasswordChar = '•', PlaceholderText = "New password (8+ characters)" };
        var b = new TextBox { PasswordChar = '•', PlaceholderText = "Again" };
        var err = W(Hint(string.Empty));
        return Col(
            T(first ? "Choose a backup password" : "Change your backup password", 17, FontWeight.SemiBold),
            W(Hint(first
                ? "One time only. With it you can open your music in IBOX even when Telegram is not at hand. It is stored only as a hash on the server; the admin can reset it if you forget it."
                : "Enter your current password, or the recovery code the admin sent you.")),
            old, a, b,
            Primary("Save", () => _ = Save()),
            err);

        async Task Save()
        {
            if (a.Text != b.Text)
            {
                err.Text = "The two passwords are not the same.";
                return;
            }

            try
            {
                await m.SetPasswordAsync(a.Text ?? string.Empty, first ? null : old.Text);
                err.Text = "Saved.";
                done();
            }
            catch (Exception e) when (e is ArgumentException or LoginFailedException or ServiceUnreachableException or InvalidOperationException)
            {
                err.Text = e.Message;
            }
        }
    }
}
