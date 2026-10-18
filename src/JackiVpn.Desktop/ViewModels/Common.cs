using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using JackiVpn.Desktop.Services;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using JackiVpn.Application.Settings;

namespace JackiVpn.Desktop.ViewModels;

public static class Ui
{
    private static Dispatcher Dispatcher => System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <summary>Runs on the UI thread (inline when already there).</summary>
    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var d = Dispatcher;
        if (d.CheckAccess())
        {
            action();
        }
        else
        {
            d.BeginInvoke(action);
        }
    }
}


/// <summary>Dialogs requested by view models; implemented by the view layer.</summary>
public interface IDialogService
{
    Task<string?> PromptAsync(string title, string message, string? initial = null, bool multiline = false, bool monospace = false);

    bool Confirm(string title, string message, bool danger = false);

    void Info(string title, string message);

    string? OpenFile(string filter);

    string? SaveFile(string filter, string defaultName);

    RoutingRule? EditRule(RoutingRule? rule, IReadOnlyList<string> ruleSetTags);
}

/// <summary>Base for pages bound to <see cref="AppSettings"/>: every setter writes an updated immutable record.</summary>
public abstract class SettingsPageViewModel : ObservableObject
{
    protected SettingsPageViewModel(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
        store.Changed += (_, _) => Ui.Post(() => OnPropertyChanged(string.Empty));
    }

    protected SettingsStore Store { get; }

    protected AppSettings S => Store.Current;

    protected void Set(Func<AppSettings, AppSettings> change) => Store.Update(change);

    protected static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
}

public static class Names
{
    public static string Protocol(Domain.Profiles.ProxyProtocol p) => p switch
    {
        Domain.Profiles.ProxyProtocol.Vless => "VLESS",
        Domain.Profiles.ProxyProtocol.Vmess => "VMess",
        Domain.Profiles.ProxyProtocol.Trojan => "Trojan",
        Domain.Profiles.ProxyProtocol.Shadowsocks => "Shadowsocks",
        Domain.Profiles.ProxyProtocol.Socks => "SOCKS",
        Domain.Profiles.ProxyProtocol.Http => "HTTP",
        Domain.Profiles.ProxyProtocol.Hysteria2 => "Hysteria2",
        Domain.Profiles.ProxyProtocol.Tuic => "TUIC",
        Domain.Profiles.ProxyProtocol.WireGuard => "WireGuard",
        _ => p.ToString(),
    };

    public static string Protocol(string? name) =>
        Enum.TryParse<Domain.Profiles.ProxyProtocol>(name, out var p) ? Protocol(p) : name ?? "—";

    public static string Transport(Domain.Profiles.TransportType t) => t switch
    {
        Domain.Profiles.TransportType.WebSocket => "WebSocket",
        Domain.Profiles.TransportType.Grpc => "gRPC",
        Domain.Profiles.TransportType.HttpUpgrade => "HTTPUpgrade",
        Domain.Profiles.TransportType.Http2 => "HTTP/2",
        Domain.Profiles.TransportType.Quic => "QUIC",
        Domain.Profiles.TransportType.XHttp => "XHTTP",
        Domain.Profiles.TransportType.Kcp => "mKCP",
        _ => "TCP",
    };
}

public static class Choices
{
    public static IReadOnlyList<T> Of<T>()
        where T : struct, Enum => Enum.GetValues<T>();
}

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

internal static class VisibilityOf
{
    public static Visibility Bool(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
