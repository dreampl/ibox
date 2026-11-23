using System;
using System.Diagnostics;
using Microsoft.Web.WebView2.Wpf;

namespace JackiVpn.Desktop.Views;

internal static class BrowserDrawer
{
    public static void Open(Uri uri, string title = "Link")
    {
        if (uri.Scheme == "tg")
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { }
            return;
        }
        if (uri.Scheme is not ("https" or "http") || MainWindow.Current is not { } main) { return; }
        var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(20,20,20) };
        web.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) { return; }
            web.CoreWebView2.NewWindowRequested += (_, request) => { request.Handled = true; if (Uri.TryCreate(request.Uri, UriKind.Absolute, out var target)) { Open(target, title); } };
        };
        main.OpenDrawer(title, web, () => web.Dispose(), edgeToEdge: true, width: 800);
        web.Source = uri;
    }
}
