using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JackiVpn.Application.Account;
using JackiVpn.Security;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// The server's own mini apps (My subscription, Music) inside IBOX, signed in with the IBOX account: the same pages
/// Telegram shows, so they look the same and follow every change on the server. Needs the WebView2 runtime (part of
/// Windows 11); without it IBOX keeps its own account screens.
/// </summary>
internal static class MiniAppWindow
{
    private static readonly Color Ink = Color.FromRgb(5, 5, 5);
    private static bool? _available;
    private static Window? _open;
    private static WebView2? _web;
    private static ToggleButtonPair? _tabs;
    private static Uri? _page;
    private static int _tab;

    public static bool Available
    {
        get
        {
            try
            {
                _available ??= !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception e) when (e is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException)
            {
                _available = false;
            }

            return _available.Value;
        }
    }

    /// <summary>Opens (or switches) the mini app; false when WebView2 is missing and IBOX should use its own screens.</summary>
    public static bool TryOpen(IboxServices ibox, int tab)
    {
        if (!Available)
        {
            return false;
        }

        if (_open is { IsLoaded: true })
        {
            if (_tab != tab)
            {
                _ = LoadAsync(ibox, tab);
            }

            _open.Activate();
            return true;
        }

        var w = new Window
        {
            Title = "IBOX",
            Width = 480,
            Height = Math.Min(860, SystemParameters.WorkArea.Height * 0.92),
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Ink),
            ShowInTaskbar = true,
        };
        var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(5, 5, 5) };
        var tabs = new ToggleButtonPair("My subscription", "Music", i => _ = LoadAsync(ibox, i));
        var root = new DockPanel();
        DockPanel.SetDock(tabs.Panel, Dock.Top);
        root.Children.Add(tabs.Panel);
        root.Children.Add(web);
        w.Content = root;

        EventHandler signedOut = (_, _) => w.Dispatcher.BeginInvoke(() => { if (!ibox.SignedIn) { w.Close(); } });
        ibox.SignInChanged += signedOut;
        w.Closed += (_, _) =>
        {
            ibox.SignInChanged -= signedOut;
            web.Dispose();
            _open = null;
            _web = null;
            _tabs = null;
        };
        _open = w;
        _web = web;
        _tabs = tabs;
        w.Show();
        _ = StartAsync(ibox, web, tab);
        return true;
    }

    private static async Task StartAsync(IboxServices ibox, WebView2 web, int tab)
    {
        try
        {
            var data = Path.Combine(AppPaths.Default().UserDataRoot, "webview");
            Directory.CreateDirectory(data);
            await web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, data));
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            _available = false;
            _open?.Close();
            AccountWindow.OpenNative(ibox, tab);
            return;
        }

        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            if (_page is null)
            {
                return;
            }

            var (action, target) = MiniAppPages.Classify(_page, e.Uri);
            if (action == MiniAppAction.None)
            {
                return;
            }

            e.Cancel = true;
            Act(ibox, action, target);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u))
            {
                Act(ibox, MiniAppAction.OpenOutside, u);
            }
        };
        await LoadAsync(ibox, tab);
    }

    private static async Task LoadAsync(IboxServices ibox, int tab)
    {
        _tab = tab;
        _tabs?.Select(tab);
        if (_web?.CoreWebView2 is not { } core)
        {
            return;
        }

        var page = tab == AccountWindow.MusicTab ? await MiniAppPages.MusicAsync(ibox) : MiniAppPages.Subscription(ibox);
        if (page is null)
        {
            // Music could not open with the account (server or network): IBOX's own music screen has the other ways in.
            _open?.Close();
            AccountWindow.OpenNative(ibox, tab);
            return;
        }

        _page = page;
        core.Navigate(page.AbsoluteUri);
    }

    private static void Act(IboxServices ibox, MiniAppAction action, Uri? target)
    {
        switch (action)
        {
            case MiniAppAction.OpenOutside when target is not null:
                try
                {
                    Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // nothing registered for that link
                }

                break;
            case MiniAppAction.Close:
                _open?.Close();
                break;
            case MiniAppAction.SignedOut when _tab == AccountWindow.MusicTab:
                ibox.Tokens.Remove(MusicClient.TokenName);   // the music session ended: open a new one with the account
                _ = LoadAsync(ibox, _tab);
                break;
            case MiniAppAction.SignedOut:
                _open?.Close();
                _ = ibox.Watcher.RefreshAsync().ContinueWith(_ => { }, TaskScheduler.Default);   // signs out here when the server says so
                break;
        }
    }

    /// <summary>Two buttons on top, in the mini apps' own black and white.</summary>
    private sealed class ToggleButtonPair
    {
        private readonly Button[] _b = new Button[2];

        public ToggleButtonPair(string a, string b, Action<int> pick)
        {
            Panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 8) };
            for (var i = 0; i < 2; i++)
            {
                var k = i;
                _b[i] = new Button
                {
                    Content = i == 0 ? a : b,
                    Padding = new Thickness(18, 7, 18, 7),
                    Margin = new Thickness(4, 0, 4, 0),
                    BorderThickness = new Thickness(0),
                    FontSize = 13,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Template = Pill(),
                };
                _b[i].Click += (_, _) => pick(k);
                Panel.Children.Add(_b[i]);
            }
        }

        public StackPanel Panel { get; }

        public void Select(int i)
        {
            for (var k = 0; k < 2; k++)
            {
                _b[k].Background = new SolidColorBrush(k == i ? Colors.White : Color.FromRgb(28, 28, 28));
                _b[k].Foreground = new SolidColorBrush(k == i ? Colors.Black : Color.FromRgb(200, 200, 200));
            }
        }

        private static ControlTemplate Pill()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var text = new FrameworkElementFactory(typeof(ContentPresenter));
            text.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            border.AppendChild(text);
            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }
    }
}
