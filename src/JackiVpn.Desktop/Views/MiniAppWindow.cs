using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using JackiVpn.Application.Account;
using JackiVpn.Security;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace JackiVpn.Desktop.Views;

internal static class MiniAppWindow
{
    private sealed class Session(WebView2 web, int tab)
    {
        public WebView2 Web { get; } = web;
        public int Tab { get; } = tab;
        public Uri? Page { get; set; }
        public bool Disposed { get; set; }
    }
    private static readonly Dictionary<int, Session> Sessions = new();
    private static IboxServices? _account;
    private static bool? _available;
    public static event Action<bool, string>? PlaybackChanged;
    public static event Action<double, double>? PlaybackProgress;
    public static bool Playing { get; private set; }
    public static string TrackTitle { get; private set; } = "Music";
    public static bool Available
    {
        get
        {
            try { return (_available ??= !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString())); }
            catch (Exception e) when (e is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException) { return false; }
        }
    }

    public static bool TryOpen(IboxServices ibox, int tab)
    {
        if (!Available || MainWindow.Current is not { } window) { return false; }
        if (_account is null)
        {
            _account = ibox;
            ibox.SignInChanged += (_, _) => window.Dispatcher.BeginInvoke(() => { if (!ibox.SignedIn) { Clear(); } });
            window.Closed += (_, _) => Clear();
        }
        var created = !Sessions.TryGetValue(tab, out var session);
        if (created)
        {
            session = new Session(new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(5, 5, 5) }, tab);
            Sessions.Add(tab, session);
        }
        window.OpenDrawer(tab == AccountWindow.MusicTab ? "Music" : "My subscription", session!.Web, retain: true, edgeToEdge: true);
        if (created) { _ = StartAsync(ibox, session); }
        return true;
    }

    private static async Task StartAsync(IboxServices ibox, Session session)
    {
        try
        {
            var data = Path.Combine(AppPaths.Default().UserDataRoot, "webview");
            Directory.CreateDirectory(data);
            var environment = await CoreWebView2Environment.CreateAsync(null, data);
            if (session.Disposed) { return; }
            await session.Web.EnsureCoreWebView2Async(environment);
            if (session.Disposed) { return; }
            var core = session.Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.NavigationStarting += (_, e) =>
            {
                if (session.Page is null) { return; }
                var (action, target) = MiniAppPages.Classify(session.Page, e.Uri);
                if (action == MiniAppAction.None) { return; }
                e.Cancel = true;
                Act(ibox, session, action, target);
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) { Act(ibox, session, MiniAppAction.OpenOutside, uri); }
            };
            if (session.Tab == AccountWindow.MusicTab)
            {
                core.WebMessageReceived += (_, e) =>
                {
                    if (session.Disposed || session.Page is null || !Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || source.GetLeftPart(UriPartial.Authority) != session.Page.GetLeftPart(UriPartial.Authority)) { return; }
                    try
                    {
                        using var json = JsonDocument.Parse(e.WebMessageAsJson);
                        var root = json.RootElement;
                        if (root.GetProperty("kind").GetString() != "ibox-playback") { return; }
                        var title = root.GetProperty("title").GetString() ?? "Music";
                        SetPlayback(root.GetProperty("playing").GetBoolean(), title[..Math.Min(title.Length, 160)]);
                        PlaybackProgress?.Invoke(root.GetProperty("position").GetDouble(), root.GetProperty("duration").GetDouble());
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
                };
                await core.AddScriptToExecuteOnDocumentCreatedAsync(MediaBridge);
            }
            await LoadAsync(ibox, session);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            if (session.Disposed) { return; }
            Release(session);
            AccountWindow.OpenNative(ibox, session.Tab);
        }
    }

    private static async Task LoadAsync(IboxServices ibox, Session session)
    {
        var page = session.Tab == AccountWindow.MusicTab ? await MiniAppPages.MusicAsync(ibox) : MiniAppPages.Subscription(ibox);
        if (session.Disposed) { return; }
        if (page is null) { Release(session); AccountWindow.OpenNative(ibox, session.Tab); return; }
        session.Page = page;
        session.Web.CoreWebView2.Navigate(page.AbsoluteUri);
    }

    public static async Task TogglePlaybackAsync()
    {
        if (Sessions.TryGetValue(AccountWindow.MusicTab, out var session) && !session.Disposed && session.Web.CoreWebView2 is { } core)
        {
            try { await core.ExecuteScriptAsync("(() => { const a = [...document.querySelectorAll('audio,video')].find(a => !a.paused) || document.querySelector('audio,video'); if(a) { if(a.paused) a.play().catch(()=>{}); else a.pause(); } })()"); }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        }
    }
    private static void SetPlayback(bool playing, string title)
    {
        if (Playing == playing && TrackTitle == title) { return; }
        Playing = playing; TrackTitle = title; PlaybackChanged?.Invoke(playing, title);
    }
    private static void Release(Session session)
    {
        session.Disposed = true;
        MainWindow.Current?.ReleaseDrawer(session.Web);
        session.Web.Dispose(); Sessions.Remove(session.Tab);
        if (session.Tab == AccountWindow.MusicTab) { SetPlayback(false, "Music"); }
    }
    private static void Clear()
    {
        MainWindow.Current?.CloseDrawer();
        foreach (var session in new List<Session>(Sessions.Values)) { Release(session); }
    }
    private static void Act(IboxServices ibox, Session session, MiniAppAction action, Uri? target)
    {
        switch (action)
        {
            case MiniAppAction.OpenOutside when target is not null:
                BrowserDrawer.Open(target);
                break;
            case MiniAppAction.Close: MainWindow.Current?.CloseDrawer(); break;
            case MiniAppAction.SignedOut when session.Tab == AccountWindow.MusicTab:
                SetPlayback(false, "Music"); ibox.Tokens.Remove(MusicClient.TokenName); _ = LoadAsync(ibox, session); break;
            case MiniAppAction.SignedOut:
                MainWindow.Current?.CloseDrawer(); _ = ibox.Watcher.RefreshAsync(); break;
        }
    }

    private const string MediaBridge = """
        (() => {
          if (window.top !== window) return;
          let last = '';
          const report = () => {
            const media = [...document.querySelectorAll('audio,video')];
            const active = media.find(a => !a.paused && !a.ended);
            const metadata = navigator.mediaSession?.metadata;
            const state = {kind:'ibox-playback', playing:!!active, title:metadata?.title || 'Music', position:active?.currentTime || 0, duration:Number.isFinite(active?.duration) ? active.duration : 0};
            const text = JSON.stringify(state);
            if(text !== last) { last=text; window.chrome.webview.postMessage(state); }
          };
          ['play','pause','ended','loadedmetadata','emptied'].forEach(e => document.addEventListener(e,report,true));
          setInterval(report,1000);
        })();
        """;
}
