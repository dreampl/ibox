using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Notifications;
using JackiVpn.Domain.Connection;
using JackiVpn.Linux;

const string Usage = """
    IBOX for Linux

      jackivpn                                  open the window (desktop)
      jackivpn add <subscription-url> [name]    add a subscription (PasarGuard, Marzban, plain links)
      jackivpn import <link-or-file>            import vless:// vmess:// trojan:// ss:// hy2:// … links
      jackivpn update                           refresh every subscription
      jackivpn list                             numbered server list
      jackivpn connect [number] [--tun] [--no-desktop-proxy]
                                                 no number = smart (fastest working server)
                                                 proxy mode (default): 127.0.0.1:10808 + GNOME proxy
                                                 --tun: whole system
      jackivpn remove-subscription <number>
      jackivpn ctl status|connect|disconnect|…  control the running window (see: jackivpn ctl help)
      jackivpn mcp                              MCP server for AI agents (talks to the running window)

    Ctrl+C disconnects.
    """;

if (args is ["helper", ..])
{
    // Started by pkexec when the app opens: the session's privileged helper (see LinuxPrivilege).
    return await JackiVpn.Linux.LinuxPrivilege.RunHelperAsync();
}

if (args.Length == 0 || args[0] == "gui")
{
    if (Environment.GetEnvironmentVariable("DISPLAY") is null && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null)
    {
        Console.WriteLine(Usage);
        return 0;
    }

    return AppBuilder.Configure<JackiVpn.Linux.Ui.GuiApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

// AI agents and scripts: control the running window over its local pipe (MCP server, or one-shot commands).
if (args[0] == "mcp")
{
    return await JackiVpn.Agent.AgentCli.RunAsync(["mcp"], Console.Out, Console.Error, "jackivpn ctl");
}

if (args[0] == "ctl")
{
    return await JackiVpn.Agent.AgentCli.RunAsync(args[1..], Console.Out, Console.Error, "jackivpn ctl");
}

if (args[0] is "help" or "-h" or "--help")
{
    Console.WriteLine(Usage);
    return 0;
}

using var lifetime = new CancellationTokenSource();
var ct = lifetime.Token;
await using var s = new LinuxServices(new ConsoleSink(), () => !args.Contains("--no-desktop-proxy"));
await s.InitializeAsync(ct);

switch (args[0])
{
    case "add" when args.Length >= 2:
    {
        var sub = await s.SubscriptionService.AddAsync(args[1], args.Length > 2 ? args[2] : null, null, ct);
        await s.SubscriptionService.RefreshAsync(sub.Id, ct);
        Console.WriteLine($"Added \"{sub.Name}\": {(await s.Profiles.GetBySubscriptionAsync(sub.Id, ct)).Count} servers.");
        return 0;
    }

    case "import" when args.Length >= 2:
    {
        var result = File.Exists(args[1]) ? await s.ProfileService.ImportFileAsync(args[1], ct) : await s.ProfileService.ImportTextAsync(string.Join('\n', args.Skip(1)), ct);
        Console.WriteLine($"Imported {result.Imported.Count} servers ({result.Issues.Count} skipped).");
        return 0;
    }

    case "update":
        foreach (var sub in await s.Subscriptions.GetAllAsync(ct))
        {
            try
            {
                await s.SubscriptionService.RefreshAsync(sub.Id, ct);
                Console.WriteLine($"{sub.Name}: {(await s.Profiles.GetBySubscriptionAsync(sub.Id, ct)).Count} servers");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"{sub.Name}: {JackiVpn.Security.SecretRedactor.Redact(ex.Message)}");
            }
        }

        return 0;

    case "list":
    {
        var all = await s.ServersAsync(ct);
        var names = await s.SubscriptionNamesAsync(ct);
        string? group = null;
        for (var i = 0; i < all.Count; i++)
        {
            var name = all[i].Origin.SubscriptionId is { } sid && names.TryGetValue(sid, out var n) ? n : "Manual";
            if (name != group)
            {
                Console.WriteLine($"\n[{name}]");
                group = name;
            }

            Console.WriteLine($"  {i + 1,3}. {all[i].DisplayName}  ({all[i].Protocol}, {all[i].Transport.Type})");
        }

        return 0;
    }

    case "remove-subscription" when args.Length >= 2 && int.TryParse(args[1], out var number):
    {
        var subs = await s.Subscriptions.GetAllAsync(ct);
        if (number < 1 || number > subs.Count)
        {
            Console.Error.WriteLine("No such subscription.");
            return 1;
        }

        await s.SubscriptionService.DeleteAsync(subs[number - 1].Id, ct);
        Console.WriteLine($"Removed \"{subs[number - 1].Name}\".");
        return 0;
    }

    case "connect":
        return await ConnectAsync();

    default:
        Console.WriteLine(Usage);
        return 1;
}

async Task<int> ConnectAsync()
{
    var all = await s.ServersAsync(ct);
    if (all.Count == 0)
    {
        Console.Error.WriteLine("No servers yet. Add a subscription first: jackivpn add <url>");
        return 1;
    }

    var pick = args.Skip(1).FirstOrDefault(a => int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    string? profileId = null;
    if (pick is not null)
    {
        var index = int.Parse(pick, CultureInfo.InvariantCulture) - 1;
        if (index < 0 || index >= all.Count)
        {
            Console.Error.WriteLine("No such server number (see: jackivpn list).");
            return 1;
        }

        profileId = all[index].Id;
    }

    var mode = args.Contains("--tun") ? ConnectionMode.Tun : ConnectionMode.SystemProxy;
    s.Settings.Update(x => x with { Connection = x.Connection with { Mode = mode, SelectedProfileId = profileId ?? x.Connection.SelectedProfileId } });

    var last = (ConnectionState?)null;
    var lastIp = (string?)null;
    s.Orchestrator.StatusChanged += (_, st) =>
    {
        if (st.State == last && st.PublicIp == lastIp)
        {
            return;
        }

        last = st.State;
        lastIp = st.PublicIp;
        var where = st.State == ConnectionState.Connected
            ? $" — {st.ProfileName}" + (st.ExitCountryName is { } c ? $", exit {c}" : string.Empty) + (st.PublicIp is { } ip ? $" ({ip})" : string.Empty)
              + (st.Mode == ConnectionMode.SystemProxy ? $"; proxy 127.0.0.1:{st.LocalProxyPort}" : "; whole system (TUN)")
            : st.LastError is { } e ? $" — {e}" : string.Empty;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {st.State}{where}");
    };

    var stop = new TaskCompletionSource();
    using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.TrySetResult(); });
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.TrySetResult(); });

    await s.PrepareTunAsync(ct);
    if (!await s.Orchestrator.ConnectAsync(new ConnectRequest { ProfileId = profileId, Smart = profileId is null }, ct))
    {
        Console.Error.WriteLine("Could not connect. Details: " + s.LogsDir + (mode == ConnectionMode.Tun ? " (TUN needs the package's network permission for sing-box, or sudo)" : string.Empty));
        return 2;
    }

    Console.WriteLine("Connected. Ctrl+C to disconnect.");
    await stop.Task;
    Console.WriteLine("Disconnecting…");
    await s.Orchestrator.DisconnectAsync(CancellationToken.None);
    return 0;
}

internal sealed class ConsoleSink : INotificationSink
{
    public void Show(AppNotification notification) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {notification.Title}: {notification.Message}");
}
