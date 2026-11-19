using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using JackiVpn.Application.Account;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// IBOX account (Telegram): the subscription mini app as native screens (usage, expiry, exit country, allow this
/// network, renew / add volume, prices, receipts) and the music library with a light player.
/// </summary>
internal static class AccountWindow
{
    public const int SubscriptionTab = 0;
    public const int MusicTab = 1;

    private static Window? _open;
    private static TabControl? _tabs;

    /// <summary>Music and the subscription manager are for signed-in users only: signed out, this asks to sign in first.</summary>
    public static void Open(IboxServices ibox, int tab = SubscriptionTab)
    {
        ArgumentNullException.ThrowIfNull(ibox);
        if (!ibox.SignedIn)
        {
            ShowSignIn(ibox, firstRun: false, then: () => Open(ibox, tab));
            return;
        }

        if (_open is { IsLoaded: true })
        {
            _tabs!.SelectedIndex = tab;
            _open.Activate();
            return;
        }

        var w = DialogService.CreateWindow("IBOX account", 620);
        w.SizeToContent = SizeToContent.Manual;
        w.Height = Math.Min(760, SystemParameters.WorkArea.Height * 0.9);
        w.ResizeMode = ResizeMode.CanResize;
        w.ShowInTaskbar = true;
        var tabs = new TabControl { Margin = new Thickness(12) };
        var sub = new ContentControl();
        var music = new ContentControl();
        tabs.Items.Add(new TabItem { Header = "Subscription", Content = new ScrollViewer { Content = sub, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = "Music", Content = music });
        tabs.SelectedIndex = tab;
        _tabs = tabs;
        w.Content = tabs;

        void RenderSub() => sub.Content = ibox.Account.SignedIn ? Subscription(ibox, RenderSub) : SignIn(ibox.Account.Login, "Sign in with Telegram to see and manage your subscription here: usage, expiry, servers and renewals.", r =>
        {
            ibox.SignIn(r);
            RenderSub();
        }, null);

        void RenderMusic() => music.Content = MusicPage(ibox, RenderMusic);

        EventHandler<AccountView> updated = (_, _) => w.Dispatcher.BeginInvoke(RenderSub);
        // Signed out (here, in Profiles, or by the server): music and the subscription manager close.
        EventHandler signedOut = (_, _) => w.Dispatcher.BeginInvoke(() => { if (!ibox.SignedIn) { w.Close(); } });
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
        w.Show();
    }

    // ------------------------------------------------------------------ sign-in
    private static Window? _signIn;

    /// <summary>
    /// The optional Telegram sign-in. On first run it says what signing in adds; "Not now" keeps IBOX fully usable
    /// with subscriptions added the normal way. <paramref name="then"/> runs after a successful sign-in.
    /// </summary>
    public static void ShowSignIn(IboxServices ibox, bool firstRun, Action? then = null)
    {
        ArgumentNullException.ThrowIfNull(ibox);
        if (_signIn is { IsLoaded: true })
        {
            _signIn.Activate();
            return;
        }

        var w = DialogService.CreateWindow("Sign in with Telegram", 520);
        w.ShowInTaskbar = true;
        var later = DialogService.Styled(new Button { Content = firstRun ? "Not now" : "Cancel", Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) }, "Button.Ghost");
        later.Click += (_, _) => w.Close();
        var extra = new StackPanel();
        extra.Children.Add(later);
        if (firstRun)
        {
            extra.Children.Add(Hint("Optional. Without it IBOX works as usual with any subscription you add. You can sign in or out later under Profiles."));
        }

        var why = "Signing in adds your IBOX music library and lets you manage your subscription inside IBOX: usage, expiry, servers and renewals.";
        w.Content = SignIn(ibox.Account.Login, why, r =>
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
        w.Show();
    }

    private static FrameworkElement SignIn(TelegramLogin login, string why, Action<LoginResult> done, FrameworkElement? extra)
    {
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Title("Sign in with Telegram"));
        root.Children.Add(Hint(why + " The first sign-in always goes through Telegram."));
        var state = Hint(string.Empty);
        var button = DialogService.Styled(new Button { Content = "Sign in with Telegram", Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) }, "Button.Primary");
        CancellationTokenSource? cts = null;
        button.Click += async (_, _) =>
        {
            cts?.Cancel();
            cts = new CancellationTokenSource();
            button.IsEnabled = false;
            try
            {
                var c = await login.StartAsync(IboxServices.DeviceName("Windows"), cts.Token);
                state.Text = $"Telegram opens with the IBOX bot. Check that it shows the code {c.Code}, then press Approve there.\nIf Telegram did not open: {c.Link}";
                OpenTelegram(c);
                done(await login.WaitAsync(c, new Progress<string>(s => { if (s == "seen") { state.Text = $"Code {c.Code}: now press Approve in Telegram."; } }), cts.Token));
            }
            catch (Exception e) when (e is LoginFailedException or ServiceUnreachableException or OperationCanceledException)
            {
                state.Text = e is OperationCanceledException ? string.Empty : e.Message;
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        root.Children.Add(button);
        root.Children.Add(state);
        if (extra is not null)
        {
            root.Children.Add(extra);
        }

        return root;
    }

    private static void OpenTelegram(LoginChallenge c)
    {
        foreach (var target in new[] { c.TelegramLink, c.Link })
        {
            if (target is null)
            {
                continue;
            }

            try
            {
                Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // tg:// not registered (no Telegram Desktop): the t.me link next
            }
        }
    }

    // ------------------------------------------------------------------ subscription
    private static FrameworkElement Subscription(IboxServices ibox, Action rerender)
    {
        var root = new StackPanel { Margin = new Thickness(16) };
        var view = ibox.Watcher.Current;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var signOut = new Button { Content = "Sign out", Padding = new Thickness(12, 4, 12, 4) };
        signOut.Click += async (_, _) =>
        {
            ibox.Watcher.Stop();
            await ibox.Account.SignOutAsync();
            rerender();
        };
        DockPanel.SetDock(signOut, Dock.Right);
        head.Children.Add(signOut);
        head.Children.Add(Title(view?.User is { } u ? "Signed in as " + u.Display : "Your subscription"));
        root.Children.Add(head);
        var status = Hint(string.Empty);
        root.Children.Add(status);
        if (view is null)
        {
            status.Text = "Loading…";
            _ = ibox.Watcher.RefreshAsync().ContinueWith(t => { if (t.Exception is not null) { status.Dispatcher.BeginInvoke(() => status.Text = t.Exception.InnerException?.Message); } }, TaskScheduler.Default);
            return root;
        }

        if (view.Subscriptions.Count == 0)
        {
            root.Children.Add(Hint("No VPN subscription is linked to this Telegram account. Ask support to link your Telegram username."));
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
            var card = new StackPanel();
            card.Children.Add(Section($"{c.Username} · {c.Server}" + (c.Multi ? " · multi-location" : string.Empty)));
            card.Children.Add(Line($"Status: {AccountText.Status(c.Status, false)}"));
            card.Children.Add(Line(c.Unlimited ? $"Used {AccountText.Bytes(c.UsedBytes)} (unlimited)" : $"Used {AccountText.Bytes(c.UsedBytes)} of {AccountText.Bytes(c.LimitBytes)}"));
            if (!c.Unlimited)
            {
                card.Children.Add(new ProgressBar { Maximum = 1, Value = c.UsedFraction, Height = 6, Margin = new Thickness(0, 4, 0, 4) });
            }

            card.Children.Add(Line(AccountText.Left(c, DateTimeOffset.Now, false) + (c.Expires is { } e ? $" (until {e.LocalDateTime:yyyy-MM-dd HH:mm})" : string.Empty)));
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (c.Link is { } link)
            {
                row.Children.Add(Small("Copy subscription link", () => { Clipboard.SetText(link.AbsoluteUri); status.Text = "Copied. Add it under Subscriptions."; }));
            }

            foreach (var (kind, values) in c.Options)
            {
                foreach (var v in values)
                {
                    row.Children.Add(Small(AccountText.Kind(kind, v, false), () => _ = Act(() => ibox.Account.RequestAsync(c.Lid, kind, v), "Request sent; the price and card number arrive here and in Telegram.")));
                }
            }

            card.Children.Add(row);
            if (c.Nex is { Member: true } nex)
            {
                card.Children.Add(Section("Nex"));
                if (nex.CanPickExit)
                {
                    var exits = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, DisplayMemberPath = "Label" };
                    var items = new List<NexExit> { new(string.Empty, "Automatic") };
                    items.AddRange(nex.Exits);
                    exits.ItemsSource = items;
                    exits.SelectedItem = items.FirstOrDefault(x => x.Backend == nex.Exit) ?? items[0];
                    exits.SelectionChanged += (_, _) =>
                    {
                        if (exits.SelectedItem is NexExit x && x.Backend != nex.Exit)
                        {
                            _ = Act(() => ibox.Account.NexExitAsync(c.Lid, x.Backend), "Exit country changed.");
                        }
                    };
                    card.Children.Add(Line("Exit country"));
                    card.Children.Add(exits);
                }

                var nr = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                nr.Children.Add(Small("Allow this network (L2TP / OpenVPN)", () => _ = Act(() => ibox.Account.NexAllowAsync(c.Lid), "This network's address is allowed. If it says you're on VPN, disconnect IBOX first.")));
                nr.Children.Add(Small("Nex configs", () => _ = ShowConfigs(ibox, c.Lid, status)));
                card.Children.Add(nr);
            }

            foreach (var r in c.Requests.Where(r => r.Pending))
            {
                var rr = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                rr.Children.Add(Line($"{AccountText.Kind(r.Kind, r.Value, false)}: " + (r.NeedsAccept ? $"price {r.Price:N0} toman" : r.Receipt ? "receipt sent, waiting for approval" : r.Pay is { Length: > 0 } ? "pay and send the receipt" : "waiting for the price")));
                if (r.NeedsAccept)
                {
                    rr.Children.Add(Small("Accept", () => _ = Act(() => ibox.Account.AcceptPriceAsync(r.Id), "Accepted; payment details follow.")));
                }

                if (r.NeedsReceipt && r.Pay is { Length: > 0 })
                {
                    rr.Children.Add(Small("Send receipt…", () =>
                    {
                        if (new DialogService().OpenFile("Images|*.jpg;*.jpeg;*.png") is { } path)
                        {
                            _ = Act(() => ibox.Account.SendReceiptAsync(r.Id, File.ReadAllBytes(path)), "Receipt sent.");
                        }
                    }));
                }

                rr.Children.Add(Small("Cancel", () => _ = Act(() => ibox.Account.CancelRequestAsync(r.Id), "Cancelled.")));
                card.Children.Add(rr);
                if (r.Pay is { Length: > 0 } pay && r.NeedsReceipt)
                {
                    card.Children.Add(DialogService.Styled(new TextBox { Text = pay, IsReadOnly = true, TextWrapping = TextWrapping.Wrap }, "TextBox.Mono"));
                }
            }

            root.Children.Add(new ClaySurface { Content = card, Margin = new Thickness(0, 8, 0, 8) });
        }

        root.Children.Add(Hint("Updates by itself when the server changes (renewal approved, volume added)."));
        return root;
    }

    private static async Task ShowConfigs(IboxServices ibox, string lid, TextBlock status)
    {
        var r = await ibox.Account.NexConfigsAsync(lid);
        if (!r.Ok)
        {
            status.Text = r.Error;
            return;
        }

        var w = DialogService.CreateWindow("Nex configs", 560);
        var root = new StackPanel { Margin = new Thickness(16) };
        foreach (var c in AccountText.Configs(r).OfType<System.Text.Json.Nodes.JsonObject>())
        {
            root.Children.Add(Section(c["tab"]?.ToString() ?? "Config"));
            if (c["note"]?.ToString() is { Length: > 0 } note)
            {
                root.Children.Add(Hint(note));
            }

            var text = c["key"]?.ToString() is { Length: > 0 } key ? key : c["conf"]?.ToString()
                ?? string.Join("\n", (c["fields"] as System.Text.Json.Nodes.JsonArray ?? []).Select(f => $"{f?[0]}: {f?[1]}"));
            root.Children.Add(DialogService.Styled(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, "TextBox.Mono"));
        }

        w.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        w.Show();
    }

    // ------------------------------------------------------------------ music
    private static FrameworkElement MusicPage(IboxServices ibox, Action rerender)
    {
        var m = ibox.Music;
        if (!m.SignedIn)
        {
            var pw = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            pw.Children.Add(Section("Or with your backup password"));
            var user = new TextBox();
            var pass = new PasswordBox();
            var err = Hint(string.Empty);
            pw.Children.Add(Line("Telegram @username or numeric id"));
            pw.Children.Add(user);
            pw.Children.Add(Line("Password"));
            pw.Children.Add(pass);
            pw.Children.Add(Small("Sign in", async () =>
            {
                try
                {
                    await m.PasswordLoginAsync(user.Text, pass.Password, IboxServices.DeviceName("Windows"));
                    rerender();
                }
                catch (Exception e) when (e is LoginFailedException or ServiceUnreachableException or ArgumentException)
                {
                    err.Text = e.Message;
                }
            }));
            pw.Children.Add(err);
            return new ScrollViewer { Content = SignIn(m.Login, "Sign in to IBOX music with Telegram to play your library here.", r => { m.Accept(r); rerender(); }, pw) };
        }

        if (m.NeedsPassword)
        {
            return PasswordForm(m, first: true, rerender);
        }

        var player = ibox.Player ??= new MusicPlayer(new Services.WpfAudioOutput(), (t, s) => m.SavePositionAsync(t, s));
        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var lists = new ComboBox { Width = 220 };
        var more = new WrapPanel();
        more.Children.Add(Small("Change password", () => { var w = DialogService.CreateWindow("Backup password", 420); w.Content = PasswordForm(m, false, () => w.Close()); w.Show(); }));
        more.Children.Add(Small("Sign out", async () => { if (player.IsPlaying) { await player.PlayPauseAsync(); } await m.SignOutAsync(); rerender(); }));
        DockPanel.SetDock(more, Dock.Right);
        top.Children.Add(more);
        top.Children.Add(lists);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var title = Section(player.Track?.Line ?? "Nothing playing");
        var seek = new Slider { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 4, 0, 4) };
        var time = Hint(string.Empty);
        var bar = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var play = new Button { Content = "▶", Width = 48, Margin = new Thickness(6, 0, 6, 0) };
        controls.Children.Add(Small("⏮", () => _ = player.PreviousAsync()));
        controls.Children.Add(play);
        controls.Children.Add(Small("⏭", () => _ = player.NextAsync()));
        play.Click += (_, _) => _ = player.PlayPauseAsync();
        bar.Children.Add(title);
        bar.Children.Add(seek);
        bar.Children.Add(time);
        bar.Children.Add(controls);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        var songs = new ListBox { DisplayMemberPath = nameof(MusicTrack.Line) };
        root.Children.Add(songs);

        var dragging = false;
        seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { dragging = false; player.Seek(TimeSpan.FromSeconds(seek.Value)); }));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (!root.IsLoaded)
            {
                return;
            }

            title.Text = player.Error ?? player.Track?.Line ?? "Nothing playing";
            play.Content = player.IsPlaying ? "⏸" : "▶";
            var d = player.Duration ?? TimeSpan.Zero;
            seek.Maximum = Math.Max(1, d.TotalSeconds);
            if (!dragging)
            {
                seek.Value = Math.Min(player.Position.TotalSeconds, seek.Maximum);
            }

            time.Text = $"{player.Position:m\\:ss} / {d:m\\:ss}";
            player.Tick();
        };
        timer.Start();
        root.Unloaded += (_, _) => timer.Stop();
        root.Loaded += (_, _) => timer.Start();

        MusicLibrary? lib = null;
        IReadOnlyList<MusicTrack> Pick() => lib is null ? [] : lists.SelectedIndex switch
        {
            0 => lib.Songs,
            1 => lib.Likes,
            var i when i - 2 < lib.Playlists.Count && i >= 2 => lib.Playlists[i - 2].Songs,
            _ => lib.Songs,
        };
        lists.SelectionChanged += (_, _) => songs.ItemsSource = Pick();
        songs.MouseDoubleClick += (_, _) =>
        {
            if (songs.SelectedIndex >= 0)
            {
                player.Play(Pick(), songs.SelectedIndex);
            }
        };
        _ = LoadAsync();
        return root;

        async Task LoadAsync()
        {
            time.Text = "Loading your library…";
            try
            {
                lib = await m.LibraryAsync();
                lists.ItemsSource = new[] { $"My songs ({lib.Songs.Count})", $"Liked ({lib.Likes.Count})" }.Concat(lib.Playlists.Select(p => $"{p.Name} ({p.Songs.Count})")).ToList();
                lists.SelectedIndex = 0;
                time.Text = lib.Resume is { } r ? $"Last played: {r.Line}" : string.Empty;
                if (lib.Resume is { } res && player.Track is null)
                {
                    title.Text = "Resume: " + res.Line;
                    play.Click += Resume;
                    void Resume(object s, RoutedEventArgs e)
                    {
                        play.Click -= Resume;
                        if (player.Track is null)
                        {
                            player.Play([res], 0, TimeSpan.FromSeconds(lib.ResumeAt));
                        }
                    }
                }
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

    private static FrameworkElement PasswordForm(MusicClient m, bool first, Action done)
    {
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Title(first ? "Choose a backup password" : "Change your backup password"));
        root.Children.Add(Hint(first
            ? "One time only. With it you can open your music in IBOX even when Telegram is not at hand. It is stored only as a hash on the server; the admin can reset it if you forget it."
            : "Enter your current password, or the recovery code the admin sent you."));
        PasswordBox? old = null;
        if (!first)
        {
            root.Children.Add(Line("Current password or recovery code"));
            root.Children.Add(old = new PasswordBox());
        }

        var a = new PasswordBox();
        var b = new PasswordBox();
        var err = Hint(string.Empty);
        root.Children.Add(Line("New password (8+ characters)"));
        root.Children.Add(a);
        root.Children.Add(Line("Again"));
        root.Children.Add(b);
        root.Children.Add(Small("Save", async () =>
        {
            if (a.Password != b.Password)
            {
                err.Text = "The two passwords are not the same.";
                return;
            }

            try
            {
                await m.SetPasswordAsync(a.Password, old?.Password);
                done();
            }
            catch (Exception e) when (e is ArgumentException or LoginFailedException or ServiceUnreachableException or InvalidOperationException)
            {
                err.Text = e.Message;
            }
        }));
        root.Children.Add(err);
        return root;
    }

    // ------------------------------------------------------------------ bits
    private static TextBlock Title(string t) => new() { Text = t, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };

    private static TextBlock Section(string t) => DialogService.Styled(new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) }, "Text.Section");

    private static TextBlock Line(string t) => DialogService.Styled(new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) }, "Text.Label");

    private static TextBlock Hint(string t) => DialogService.Styled(new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }, "Text.Hint");

    private static Button Small(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 6) };
        b.Click += (_, _) => click();
        return b;
    }

    private static Button Small(string text, Func<Task> click)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 6) };
        b.Click += async (_, _) => await click();
        return b;
    }
}
