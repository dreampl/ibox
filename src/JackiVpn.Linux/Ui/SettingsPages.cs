using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using JackiVpn.Application.Connectivity;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using JackiVpn.Routing;
using Microsoft.Extensions.Logging;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

/// <summary>Settings pages share: read the current settings, write a change, say when it applies.</summary>
public abstract class OptionsPage(Shell shell) : Page(shell)
{
    protected AppSettings Cur => S.Settings.Current;

    protected void Set(Func<AppSettings, AppSettings> change)
    {
        S.Settings.Update(change);
        if (!AppliesLive && S.Orchestrator.Status.State == ConnectionState.Connected && !_hinted)
        {
            _hinted = true;
            Shell.Toast("Saved", "Applies the next time you connect.");
        }
    }

    private bool _hinted;

    /// <summary>True when the page's changes reach the running connection by themselves (routing).</summary>
    protected virtual bool AppliesLive => false;

    protected static StackPanel Stack(params Control[] children)
    {
        var p = new StackPanel { Spacing = 16 };
        foreach (var c in children)
        {
            p.Children.Add(c);
        }

        return p;
    }
}

public sealed class RoutingPage : OptionsPage
{
    private readonly StackPanel _rules = new() { Spacing = 6 };
    private readonly StackPanel _ruleSets = new() { Spacing = 6 };

    public RoutingPage(Shell shell)
        : base(shell)
    {
        S.RoutingWatcher.Applied += (_, ok) => Dispatcher.UIThread.Post(() => Shell.Toast(
            "Routing",
            ok ? "Applied to the running connection." : "Saved. The running connection could not take the change; it applies on the next connection.",
            warning: !ok));
    }

    protected override bool AppliesLive => true;

    public override string Title => "Routing";

    public override string Glyph => Icons.Routing;

    protected override Control Build()
    {
        var preset = EnumCombo(Cur.Routing.Preset, p =>
        {
            if (p == RoutingPreset.Custom)
            {
                return;
            }

            var built = RoutingPresets.Build(p);
            Set(x => x with { Routing = x.Routing with { Preset = p, Rules = built.Rules, RuleSets = built.RuleSets, Final = built.Final } });
            Render();
        });
        var final = Combo([(RuleActionType.Proxy, "Through the VPN"), (RuleActionType.Direct, "Direct"), (RuleActionType.Block, "Block")], Cur.Routing.Final,
            v => Set(x => x with { Routing = x.Routing with { Final = v } }));
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { Ghost("Add rule-set…", () => _ = AddRuleSetAsync()), Primary("Add rule", () => _ = EditRuleAsync(null)) },
        };
        return Stack(
            Header("Routing", "Rules are checked top to bottom; the first match wins.", actions),
            Section("Preset", "Rule = everything through VPN except BitTorrent; Global = everything through VPN; Direct = nothing through VPN; Iran friendly = Iranian sites and IPs direct, the rest through VPN (every preset rule is editable).",
                Row("Preset", null, preset),
                Row("Unmatched traffic", "What happens to traffic that matches no rule.", final),
                Row("Keep local network direct", "Printers, NAS and router pages stay reachable (private IP ranges bypass the VPN).", Toggle(Cur.Routing.BypassLan, v => Set(x => x with { Routing = x.Routing with { BypassLan = v } }))),
                Row("Detect domains from traffic (sniffing)", "Lets domain rules work even when apps connect by IP.", Toggle(Cur.Routing.Sniff, v => Set(x => x with { Routing = x.Routing with { Sniff = v } })))),
            Section("Rules", null, _rules),
            Section("Rule-sets", "Downloaded, verified and cached locally; the last good copy is used if an update fails.", _ruleSets));
    }

    public override void OnShown() => Render();

    private void Render()
    {
        _rules.Children.Clear();
        var rules = Cur.Routing.Rules;
        if (rules.Count == 0)
        {
            _rules.Children.Add(Hint("No rules: everything follows the unmatched-traffic action."));
        }

        foreach (var rule in rules)
        {
            var action = rule.Action.Type switch { RuleActionType.Direct => ("Direct", Success), RuleActionType.Block => ("Block", Danger), RuleActionType.Proxy => ("VPN", Accent), _ => (Spaced(rule.Action.Type.ToString()), Text2) };
            var toggle = Toggle(rule.Enabled, v => UpdateRules(list => list.Select(r => r.Id == rule.Id ? r with { Enabled = v } : r).ToList()));
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    Ghost("Edit", () => _ = EditRuleAsync(rule)),
                    Ghost("↑", () => Move(rule, -1)),
                    Ghost("↓", () => Move(rule, 1)),
                    Ghost("Delete", () => _ = DeleteRuleAsync(rule)),
                },
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 12 };
            grid.Children.Add(toggle);
            var summary = rule.Description ?? string.Join(" · ", rule.Conditions.Select(c => $"{ConditionName(c.Type)}: {string.Join(", ", c.Values.Take(3))}{(c.Values.Count > 3 ? "…" : string.Empty)}"));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { T(rule.Name + (rule.Invert ? "  (inverted)" : string.Empty), 13, FontWeight.SemiBold), Hint(summary) } };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var badge = Badge(action.Item1, action.Item2);
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
            Grid.SetColumn(buttons, 3);
            grid.Children.Add(buttons);
            var row = new Border { Background = B(InputBg), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8), Child = grid };
            row.DoubleTapped += (_, _) => _ = EditRuleAsync(rule);
            _rules.Children.Add(row);
        }

        _ruleSets.Children.Clear();
        foreach (var set in Cur.Routing.RuleSets)
        {
            _ruleSets.Children.Add(new Border
            {
                Background = B(InputBg),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8),
                Child = new StackPanel { Children = { T(set.Tag, 13, FontWeight.SemiBold), Hint(set.Description ?? set.Url) } },
            });
        }

        if (Cur.Routing.RuleSets.Count == 0)
        {
            _ruleSets.Children.Add(Hint("No rule-sets in use."));
        }
    }

    /// <summary>Any manual change makes the routing "Custom" (a preset can be applied again later).</summary>
    private void UpdateRules(Func<List<RoutingRule>, List<RoutingRule>> change)
    {
        Set(x => x with { Routing = x.Routing with { Preset = RoutingPreset.Custom, Rules = change(x.Routing.Rules.ToList()) } });
        Render();
    }

    private void Move(RoutingRule rule, int delta) => UpdateRules(list =>
    {
        var i = list.FindIndex(r => r.Id == rule.Id);
        var j = i + delta;
        if (i >= 0 && j >= 0 && j < list.Count)
        {
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    });

    private async Task DeleteRuleAsync(RoutingRule rule)
    {
        if (await Shell.ConfirmAsync("Delete rule", $"Delete \"{rule.Name}\"?", "Delete"))
        {
            UpdateRules(list => list.Where(r => r.Id != rule.Id).ToList());
        }
    }

    private async Task EditRuleAsync(RoutingRule? rule)
    {
        var tags = Cur.Routing.RuleSets.Select(r => r.Tag).ToList();
        if (await RuleEditorDialog.ShowAsync(Shell, rule, tags) is not { } edited)
        {
            return;
        }

        UpdateRules(list =>
        {
            if (rule is null)
            {
                list.Insert(0, edited);
            }
            else
            {
                list = list.Select(r => r.Id == rule.Id ? edited : r).ToList();
            }

            return list;
        });
    }

    private async Task AddRuleSetAsync()
    {
        var url = await Shell.PromptAsync("Add rule-set", "URL of a sing-box rule-set (.srs binary or .json source):", null);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Shell.Toast("Rule-set", "Please enter an https:// URL.", warning: true);
            return;
        }

        var tag = await Shell.PromptAsync("Add rule-set", "Tag (used in rules, letters/digits/-/_):", System.IO.Path.GetFileNameWithoutExtension(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        var def = new RuleSetDefinition
        {
            Tag = tag.Trim(),
            Url = uri.AbsoluteUri,
            Format = uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? RuleSetFormat.Source : RuleSetFormat.Binary,
        };
        Set(x => x with { Routing = x.Routing with { RuleSets = x.Routing.RuleSets.Where(r => r.Tag != def.Tag).Append(def).ToList() } });
        Render();
    }

    internal static string ConditionName(RuleConditionType type) => type switch
    {
        RuleConditionType.Domain => "domain",
        RuleConditionType.DomainSuffix => "suffix",
        RuleConditionType.DomainKeyword => "keyword",
        RuleConditionType.DomainRegex => "regex",
        RuleConditionType.IpCidr => "ip",
        RuleConditionType.GeoIp => "geoip",
        RuleConditionType.GeoSite => "geosite",
        RuleConditionType.RuleSet => "rule-set",
        RuleConditionType.PrivateIp => "private ip",
        RuleConditionType.Protocol => "protocol",
        RuleConditionType.Port => "port",
        RuleConditionType.PortRange => "ports",
        RuleConditionType.ProcessName => "process",
        RuleConditionType.ProcessPath => "process path",
        RuleConditionType.Network => "network",
        _ => type.ToString(),
    };
}

/// <summary>Rule editor (as on Windows): name, action, invert and any number of typed conditions, validated before it closes.</summary>
internal static class RuleEditorDialog
{
    public static async Task<RoutingRule?> ShowAsync(Window owner, RoutingRule? original, IReadOnlyList<string> tags)
    {
        var types = Enum.GetValues<RuleConditionType>().Where(t => t != RuleConditionType.NetworkType).ToArray();
        var rows = new StackPanel { Spacing = 6 };
        void AddRow(RuleConditionType type, string values)
        {
            var combo = new ComboBox { ItemsSource = types.Select(RoutingPage.ConditionName).ToList(), SelectedIndex = Array.IndexOf(types, type), Width = 150 };
            var box = new TextBox { Text = values, PlaceholderText = "values, separated by commas" };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(combo);
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            var remove = Ghost("✕", () => rows.Children.Remove(row));
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            row.Tag = (combo, box);
            rows.Children.Add(row);
        }

        foreach (var c in original?.Conditions ?? [new RuleCondition(RuleConditionType.DomainSuffix, [])])
        {
            AddRow(c.Type, string.Join(", ", c.Values));
        }

        var name = new TextBox { Text = original?.Name ?? string.Empty, PlaceholderText = "e.g. Banks direct" };
        RuleActionType[] actions = [RuleActionType.Proxy, RuleActionType.Direct, RuleActionType.Block];
        var action = new ComboBox
        {
            ItemsSource = new[] { "Through the VPN", "Direct", "Block" },
            SelectedIndex = Math.Max(0, Array.IndexOf(actions, original?.Action.Type ?? RuleActionType.Direct)),
            Width = 200,
        };
        var invert = new CheckBox { IsChecked = original?.Invert ?? false, Content = "Invert (match everything except this)" };
        var error = new TextBlock { Foreground = B(Danger), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var dialog = new Window
        {
            Title = original is null ? "Add rule" : "Edit rule",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Background = B(CardBg),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        RoutingRule? result = null;
        var save = Primary("Save", () =>
        {
            var rule = new RoutingRule
            {
                Id = original?.Id ?? Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name.Text) ? "Rule" : name.Text.Trim(),
                Enabled = original?.Enabled ?? true,
                Action = new RuleAction(actions[Math.Max(0, action.SelectedIndex)]),
                Invert = invert.IsChecked == true,
                Conditions = rows.Children.OfType<Grid>()
                    .Select(r => ((ComboBox Combo, TextBox Box))r.Tag!)
                    .Select(t =>
                    {
                        var type = types[Math.Max(0, t.Combo.SelectedIndex)];
                        return new RuleCondition(type, type == RuleConditionType.PrivateIp ? ["true"] : (t.Box.Text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    })
                    .Where(c => c.Values.Count > 0)
                    .ToList(),
                FromPreset = original?.FromPreset,
                Description = original?.Description,
            };
            var validation = RuleValidator.Validate(rule, tags);
            if (!validation.IsValid)
            {
                error.Text = string.Join(Environment.NewLine, validation.Errors.Select(e => "• " + e.Message));
                return;
            }

            result = rule;
            dialog.Close();
        });
        var content = new StackPanel
        {
            Margin = new Thickness(22, 18),
            Spacing = 10,
            Children =
            {
                T(original is null ? "Add rule" : "Edit rule", 17, FontWeight.SemiBold),
                Caption("Name"), name,
                Caption("Action"), action,
                Caption("Conditions — destination conditions (domain, suffix, keyword, regex, ip, geoip, geosite, rule-set, private ip) match if any matches; the others must all match."),
                rows,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { Ghost("Add condition", () => AddRow(RuleConditionType.Domain, string.Empty)) } },
                invert,
                Hint(tags.Count > 0 ? "Available rule-sets: " + string.Join(", ", tags) : "No rule-sets declared (add one on the Routing page)."),
                Hint("Examples — suffix: google.com · geosite: category-ir · geoip: ir · ip: 10.0.0.0/8 · process: telegram-desktop · port: 443 · ports: 1000:2000"),
                error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { Ghost("Cancel", () => dialog.Close()), save } },
            },
        };
        dialog.Content = new ScrollViewer { Content = content, MaxHeight = 760 };
        await dialog.ShowDialog(owner);
        return result;
    }
}

public sealed class DnsPage(Shell shell) : OptionsPage(shell)
{
    private readonly StackPanel _choice = new() { Spacing = 10 };
    private readonly StackPanel _results = new() { Spacing = 4 };
    private readonly TextBlock _status = Hint(string.Empty);
    private bool _searching;

    public override string Title => "DNS";

    public override string Glyph => Icons.Dns;

    private readonly StackPanel _adapters = new() { Spacing = 10 };

    public override void OnShown()
    {
        RefreshChoice();
        _ = Guard(RefreshAdaptersAsync);
    }

    /// <summary>Each adapter: what it uses now, a ready-made pair or typed servers, Automatic and Restore.</summary>
    private async Task RefreshAdaptersAsync()
    {
        IReadOnlyList<Domain.Network.AdapterDns> list;
        try
        {
            list = await S.AdapterDns.ListAsync(CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            _adapters.Children.Clear();
            _adapters.Children.Add(Hint(ex.Message));
            return;
        }

        _adapters.Children.Clear();
        if (list.Count == 0)
        {
            _adapters.Children.Add(Hint("No Wi-Fi or Ethernet adapters found."));
        }

        var presets = AdapterDnsPresets.All(Cur.Dns.Custom);
        foreach (var a in list)
        {
            var adapter = a;
            var servers = Input(adapter.Automatic ? null : string.Join(", ", adapter.Servers), _ => { }, "e.g. 1.1.1.1, 8.8.8.8", 230);
            var preset = new ComboBox { ItemsSource = presets.Select(p => p.Name).ToList(), PlaceholderText = "Ready-made…", MinWidth = 170 };
            preset.SelectionChanged += (_, _) =>
            {
                if (preset.SelectedIndex >= 0)
                {
                    servers.Text = string.Join(", ", presets[preset.SelectedIndex].Servers);
                }
            };
            var message = Hint(string.Empty);
            message.IsVisible = false;

            async Task Change(IReadOnlyList<string> list)
            {
                try
                {
                    if (await S.AdapterDns.SetAsync(adapter, list, CancellationToken.None) is { } problem)
                    {
                        message.Text = problem;
                        message.IsVisible = true;
                        return;
                    }

                    Shell.Toast("DNS", $"{adapter.Name}: " + (list.Count == 0 ? "automatic again." : "now uses " + string.Join(", ", list) + "."));
                    await RefreshAdaptersAsync();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    message.Text = ex.Message;
                    message.IsVisible = true;
                }
            }

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
            {
                preset,
                servers,
                Primary("Set", () => _ = Guard(() => Domain.Network.AdapterDnsChange.Parse(servers.Text) is { Count: > 0 } typed ? Change(typed) : Task.CompletedTask)),
                Ghost("Automatic", () => _ = Guard(() => Change([]))),
            } };
            if (S.AdapterDns.CanRestore(adapter.Id))
            {
                actions.Children.Add(Ghost("Restore", () => _ = Guard(async () =>
                {
                    await S.AdapterDns.RestoreAsync(adapter, CancellationToken.None);
                    await RefreshAdaptersAsync();
                })));
            }

            var now = (adapter.Automatic ? "Automatic (from the network)" : "Set by hand") + (adapter.Servers.Count > 0 ? ": " + string.Join(", ", adapter.Servers) : string.Empty);
            _adapters.Children.Add(Row($"{adapter.Name} · {adapter.Kind}{(adapter.Up ? string.Empty : " · not connected")}", now, actions));
            _adapters.Children.Add(message);
        }
    }

    /// <summary>Smart / network's own / manual, the manual pick, own resolvers and the search.</summary>
    private void RefreshChoice()
    {
        var s = Cur;
        var selection = DnsChoice.Selection(s);
        var known = S.DnsFinder.AllCandidates();
        _choice.Children.Clear();
        _choice.Children.Add(Row("DNS to use", "Smart = the fastest clean resolver found on each network.", Combo(
            [(DnsSelection.Smart, "Smart"), (DnsSelection.System, "Network's own"), (DnsSelection.Manual, "Manual")],
            selection,
            v =>
            {
                if (v != DnsChoice.Selection(Cur))
                {
                    Set(x => DnsChoice.Select(x, v, S.DnsFinder.BestForNetwork()));
                    RefreshChoice();
                }
            })));
        var inUse = selection switch
        {
            DnsSelection.Smart => S.DnsFinder.BestLabel() is { } best ? best + " (best on this network)" : "the network's own DNS until a search finds a better one",
            DnsSelection.System => "the DNS servers of your network adapter",
            _ => DnsChoice.Describe(s.Dns.Bootstrap, known),
        };
        _choice.Children.Add(T("In use: " + inUse, 12.5, FontWeight.SemiBold));

        if (selection == DnsSelection.Manual)
        {
            var items = known.Select(c => (c.ToEndpoint(), c.Label + (c.Custom ? " (yours)" : c.Iranian ? " (Iran)" : string.Empty))).ToList();
            var pick = Combo(items, s.Dns.Bootstrap, ep =>
            {
                if (ep != Cur.Dns.Bootstrap)
                {
                    Set(x => DnsChoice.Use(x, ep));
                    RefreshChoice();
                }
            });
            pick.MinWidth = 360;
            _choice.Children.Add(Row("Choose from the list", null, pick));
        }

        // Add your own
        var name = Input(null, _ => { }, "name", 130);
        var address = Input(null, _ => { }, "IP address", 170);
        var doh = new CheckBox { Content = "DoH" };
        var host = Input(null, _ => { }, "DoH host (optional)", 170);
        var error = Hint(string.Empty);
        error.Foreground = B(Danger);
        error.IsVisible = false;
        var add = Primary("Add and use", () =>
        {
            var resolver = new CustomDnsResolver
            {
                Name = string.IsNullOrWhiteSpace(name.Text) ? address.Text?.Trim() ?? string.Empty : name.Text.Trim(),
                Address = address.Text?.Trim() ?? string.Empty,
                Transport = doh.IsChecked == true ? DnsTransport.Https : DnsTransport.Udp,
                Host = doh.IsChecked == true && !string.IsNullOrWhiteSpace(host.Text) ? host.Text.Trim() : null,
            };
            var (next, problem) = DnsChoice.AddCustom(Cur, resolver);
            if (problem is not null)
            {
                error.Text = problem;
                error.IsVisible = true;
                return;
            }

            Set(_ => DnsChoice.Use(next, resolver.ToEndpoint()));
            RefreshChoice();
            Shell.Toast("DNS", $"Added and now using {resolver.Name}.");
        });
        _choice.Children.Add(Row("Add your own DNS", "Plain or encrypted (DoH); added to the list and to the search.", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { name, address, doh, host, add } }));
        _choice.Children.Add(error);
        foreach (var c in s.Dns.Custom)
        {
            var label = DnsCandidate.From(c).Label;
            _choice.Children.Add(Row(label, null, Ghost("Remove", () =>
            {
                Set(x => DnsChoice.RemoveCustom(x, c.Name));
                RefreshChoice();
            })));
        }

        _choice.Children.Add(Row("Also use it for sites that go direct", "Sites your rules send outside the VPN are looked up with this DNS (when the Direct resolver below is Local).",
            Toggle(s.Dns.UseChosenForDirect, v => Set(x => x with { Dns = x.Dns with { UseChosenForDirect = v } }))));
        if (S.Orchestrator.Status.State == ConnectionState.Connected)
        {
            _choice.Children.Add(Row("Use the new DNS on this connection", "Restarts the connection on the same server.", Ghost("Apply now", () => _ = Guard(async () =>
            {
                var ok = await S.RoutingWatcher.ApplyNowAsync("DNS changed");
                Shell.Toast("DNS", ok ? "Applied to the running connection." : "Could not apply now; used from the next connection.", warning: !ok);
            }))));
        }

        FillResults(S.DnsFinder.Last);
    }

    private void FillResults(IReadOnlyList<DnsResult> results)
    {
        _results.Children.Clear();
        foreach (var r in results)
        {
            var c = r.Candidate;
            var verdict = !r.Reachable ? "No answer" : r.Clean ? "Clean" : r.Note?.StartsWith("intercepted", StringComparison.Ordinal) == true ? "Intercepted" : "Filtered";
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(Badge(verdict, r.Reachable && r.Clean ? Success : Danger));
            line.Children.Add(T(r.LatencyMs is { } ms ? $"{ms:0} ms" : "—", 12));
            if (r.Reachable && r.Clean)
            {
                line.Children.Add(Ghost("Use this", () =>
                {
                    Set(x => DnsChoice.Use(x, c.ToEndpoint()));
                    RefreshChoice();
                    Shell.Toast("DNS", "Now using " + c.Label + ".");
                }));
            }

            _results.Children.Add(Row(c.Label + (c.Iranian ? " (Iran)" : string.Empty), null, line));
        }
    }

    private async Task SearchAsync()
    {
        if (_searching)
        {
            return;
        }

        if (!S.Rescue.CanProbeDirectly())
        {
            Shell.Toast("DNS search", "Disconnect first: in TUN mode the test would measure the VPN, not your network.", warning: true);
            return;
        }

        _searching = true;
        _status.Text = "Asking every resolver (a few seconds)…";
        try
        {
            var results = await Task.Run(() => S.DnsFinder.SearchAsync(CancellationToken.None));
            var best = results.FirstOrDefault(r => r.Reachable && r.Clean);
            _status.Text = best is null ? "No resolver answered honestly here." : $"Fastest clean: {best.Candidate.Label} ({best.LatencyMs:0} ms).";
            RefreshChoice();
        }
        finally
        {
            _searching = false;
        }
    }

    protected override Control Build()
    {
        var search = Section("DNS search", "Tests international, Iranian and your own resolvers from this network: speed, and whether they answer honestly or with the filter's block page.",
            Row("Find the best DNS here", null, Primary("Search", () => _ = Guard(SearchAsync))), _status, _results);
        var d = Cur.Dns;
        var leak = Hint("This mode can reveal the sites you visit to your ISP or network (DNS leak).");
        leak.Foreground = B(Warning);
        leak.IsVisible = d.Mode is DnsMode.System or DnsMode.Local;
        var mode = EnumCombo(d.Mode, v =>
        {
            Set(x => x with { Dns = x.Dns with { Mode = v } });
            leak.IsVisible = v is DnsMode.System or DnsMode.Local;
        });
        return Stack(
            Header("DNS", "How domain names are resolved while connected. The default keeps every lookup encrypted and inside the tunnel."),
            Section("DNS mode", null, Row("Mode", null, mode), leak),
            Section("Which DNS", "The resolver used outside the tunnel: for VPN server addresses, and for direct sites if you turn that on. Names inside the tunnel use the encrypted resolver.", _choice),
            search,
            Section("Network adapters", "The DNS servers the system uses on each adapter (NetworkManager). Automatic takes them from the router again; IBOX remembers the original so you can restore it.",
                Row("Read the adapters again", null, Ghost("Refresh", () => _ = Guard(RefreshAdaptersAsync))), _adapters),
            Section("Resolvers", "Local = the resolvers configured in the system.",
                Resolver("Remote (through the VPN)", d.Remote, e => Set(x => x with { Dns = x.Dns with { Remote = e } })),
                Resolver("Direct (for traffic routed outside the VPN)", d.Direct, e => Set(x => x with { Dns = x.Dns with { Direct = e } })),
                Resolver("Bootstrap (only resolves VPN server names)", d.Bootstrap, e => Set(x => x with { Dns = x.Dns with { Bootstrap = e } }))),
            Section("Options", null,
                Row("Cache DNS answers in the core", null, Toggle(d.CacheEnabled, v => Set(x => x with { Dns = x.Dns with { CacheEnabled = v } }))),
                Row("Fake IP (TUN)", "Answers tunneled names instantly with placeholder addresses; the server resolves the real address. Faster first connections; a few apps dislike it.", Toggle(d.FakeIpEnabled, v => Set(x => x with { Dns = x.Dns with { FakeIpEnabled = v } })))));
    }

    private static Grid Resolver(string label, DnsServerEndpoint ep, Action<DnsServerEndpoint> changed)
    {
        var current = ep;
        var transport = EnumCombo(ep.Transport, v => changed(current = current with { Transport = v }));
        transport.MinWidth = 110;
        var server = Input(ep.Server, v => changed(current = current with { Server = string.IsNullOrWhiteSpace(v) ? null : v.Trim() }), "server", 170);
        var path = Input(ep.Path, v => changed(current = current with { Path = string.IsNullOrWhiteSpace(v) ? null : v.Trim() }), "/dns-query", 110);
        return Row(label, null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { transport, server, path } });
    }
}

public sealed class NetworkPage(Shell shell) : OptionsPage(shell)
{
    public override string Title => "Network";

    public override string Glyph => Icons.Network;

    protected override Control Build()
    {
        var c = Cur.Connection;
        var mode = Combo([(ConnectionMode.Tun, "TUN (all apps)"), (ConnectionMode.SystemProxy, "System proxy")], c.Mode, v => Set(x => x with { Connection = x.Connection with { Mode = v } }));
        return Stack(
            Header("Network", "Connection mode and network options."),
            Section("Connection mode", "TUN captures all traffic through a virtual network device (sing-box gets the network permission when the package is installed). System proxy only covers apps that honor the desktop proxy settings (GNOME/Ubuntu: set automatically while connected).",
                Row("Mode", null, mode),
                Row("IPv6", "Auto enables IPv6 only when the server is proven to carry it; otherwise IPv6 is blocked inside the tunnel so it cannot leak.", EnumCombo(Cur.Ipv6, v => Set(x => x with { Ipv6 = v }))),
                Row("TUN MTU", "1280–9000. Lower it if some sites stall.", Number(c.TunMtu, 1280, 9000, v => Set(x => x with { Connection = x.Connection with { TunMtu = (int)v } }), 10)),
                Row("Reconnect attempts", "Retries with backoff before giving up (smart mode switches server after the first).", Number(c.MaxReconnectAttempts, 0, 10, v => Set(x => x with { Connection = x.Connection with { MaxReconnectAttempts = (int)v } })))),
            Section("System proxy", null,
                Row("Also set the desktop proxy in TUN mode", "Some apps (Claude, …) check the system proxy and treat \"no proxy\" as \"no VPN\". Their traffic is tunneled either way; this only satisfies the check (GNOME/Ubuntu).", Toggle(c.SystemProxyInTun, v => Set(x => x with { Connection = x.Connection with { SystemProxyInTun = v } }))),
                Row("Local port", "Default 10808 (HTTP + SOCKS). Keep it fixed: some apps remember the proxy address from when they started.", Number(c.LocalProxyPort, 1024, 65535, v => Set(x => x with { Connection = x.Connection with { LocalProxyPort = (int)v } })))),
            Section("When a server is blocked", "Tried automatically when no server answers; the way that works is remembered for this network.",
                Row("Encrypted DNS for server names", "Resolves server addresses over DNS-over-HTTPS when the network's DNS is poisoned or blocked.", Toggle(Cur.Connectivity.SecureDnsRescue, v => Set(x => x with { Connectivity = x.Connectivity with { SecureDnsRescue = v } }))),
                Row("Find clean Cloudflare addresses", "For CDN (WebSocket / gRPC / HTTP) configs whose usual address is filtered: tests Cloudflare ranges with the config's own TLS name.", Toggle(Cur.Connectivity.CleanIpScan, v => Set(x => x with { Connectivity = x.Connectivity with { CleanIpScan = v } }))),
                Row("Try other TLS fingerprints", "When a REALITY/TLS handshake is blocked, tries the other browser fingerprints.", Toggle(Cur.Connectivity.FingerprintRotation, v => Set(x => x with { Connectivity = x.Connectivity with { FingerprintRotation = v } })))),
            Section("Kill switch", "Not available on Linux yet: if the VPN drops, traffic is not blocked. Use TUN mode with smart connect for automatic failover."));
    }
}

public sealed class AdvancedPage(Shell shell) : OptionsPage(shell)
{
    public override string Title => "Advanced";

    public override string Glyph => Icons.Advanced;

    protected override Control Build()
    {
        var p = Cur.Performance;
        var f = Cur.Smart.Failover;
        var probe = Cur.Probe;
        return Stack(
            Header("Advanced", "Engines, multiplexing, smart selection and server tests. Defaults are tuned for stability."),
            Section("Speed & handshake", "Base ping is set by the distance to the server. These settings remove the extra waiting on top of it.",
                Row("Auto-tune each server", "Measures TLS record vs TCP fragmentation, WebSocket 0-RTT and MUX on real handshakes and keeps the fastest variant that works.", Toggle(p.AutoTune, v => Set(x => x with { Performance = x.Performance with { AutoTune = v } }))),
                Row("Fast DNS (FakeIP, TUN)", "Tunneled names are answered instantly and resolved by the server.", Toggle(Cur.Dns.FakeIpEnabled, v => Set(x => x with { Dns = x.Dns with { FakeIpEnabled = v } }))),
                Row("QUIC / HTTP3", "Auto blocks QUIC through TCP-based servers so browsers switch to TCP at once.", EnumCombo(p.Quic, v => Set(x => x with { Performance = x.Performance with { Quic = v } }))),
                Row("Default TLS fingerprint", "Used only for servers whose config names none.", Combo([("chrome", "Chrome"), ("firefox", "Firefox"), ("safari", "Safari"), ("edge", "Edge"), ("random", "Random"), (string.Empty, "None")], p.DefaultFingerprint, v => Set(x => x with { Performance = x.Performance with { DefaultFingerprint = v } }))),
                Row("TUN network stack", "Mixed (recommended): system TCP stack for TCP, gVisor for UDP.", EnumCombo(p.TunStack, v => Set(x => x with { Performance = x.Performance with { TunStack = v } }))),
                Row("Gaming & streaming optimization", "While a game or stream runs, background tests and automatic server moves pause.", EnumCombo(p.Activity, v => Set(x => x with { Performance = x.Performance with { Activity = v } })))),
            Section("Engines", "Auto uses sing-box, and Xray (chained behind sing-box) only for features sing-box lacks (XHTTP, …).",
                Row("Preferred engine", null, EnumCombo(Cur.Connection.Core, v => Set(x => x with { Connection = x.Connection with { Core = v } }))),
                Row("Multiplexing (MUX)", "Auto turns MUX on only after a benchmark shows it works and is faster. Never used with XTLS Vision or UDP-based protocols.", EnumCombo(Cur.Connection.Mux, v => Set(x => x with { Connection = x.Connection with { Mux = v } })))),
            Section("Smart selection & failover", null,
                Row("Jacki Smart Connect: move to clearly better servers", "Only between servers verified as Jacki infrastructure. Other servers are only switched when they fail.", Toggle(Cur.Smart.ProactiveSwitching, v => Set(x => x with { Smart = x.Smart with { ProactiveSwitching = v } }))),
                Row("Health check every (s)", null, Number(f.HealthCheckInterval.TotalSeconds, 10, 600, v => Set(x => x with { Smart = x.Smart with { Failover = x.Smart.Failover with { HealthCheckInterval = TimeSpan.FromSeconds(v) } } }))),
                Row("Failures before switching", null, Number(f.ConsecutiveFailuresToFailover, 1, 10, v => Set(x => x with { Smart = x.Smart with { Failover = x.Smart.Failover with { ConsecutiveFailuresToFailover = (int)v } } }))),
                Row("Minimum time on a server (min)", null, Number(f.MinDwell.TotalMinutes, 0, 60, v => Set(x => x with { Smart = x.Smart with { Failover = x.Smart.Failover with { MinDwell = TimeSpan.FromMinutes(v) } } }))),
                Row("Max switches per hour", null, Number(f.MaxSwitchesPerHour, 1, 60, v => Set(x => x with { Smart = x.Smart with { Failover = x.Smart.Failover with { MaxSwitchesPerHour = (int)v } } })))),
            Section("Server tests", null,
                Row("Real data check", "Also downloads 64 KB through each server: on throttled networks a connection can finish the handshake and then freeze after a few KB.", Toggle(probe.DataCheck, v => Set(x => x with { Probe = x.Probe with { DataCheck = v } }))),
                Row("Timeout (s)", null, Number(probe.Timeout.TotalSeconds, 2, 60, v => Set(x => x with { Probe = x.Probe with { Timeout = TimeSpan.FromSeconds(v) } }))),
                Row("Samples per server", null, Number(probe.SamplesPerNode, 1, 10, v => Set(x => x with { Probe = x.Probe with { SamplesPerNode = (int)v } }))),
                Row("Parallel tests (1–6)", null, Number(probe.MaxConcurrency, 1, 6, v => Set(x => x with { Probe = x.Probe with { MaxConcurrency = (int)v } }))),
                Row("Reuse results for (min)", null, Number(probe.CacheTtl.TotalMinutes, 0, 120, v => Set(x => x with { Probe = x.Probe with { CacheTtl = TimeSpan.FromMinutes(v) } }))),
                Row("Measure throughput (downloads ~1 MB per server)", null, Toggle(probe.ThroughputTest, v => Set(x => x with { Probe = x.Probe with { ThroughputTest = v } }))),
                Row("Show public IP after connecting", "Asks Cloudflare through the VPN.", Toggle(probe.CheckPublicIp, v => Set(x => x with { Probe = x.Probe with { CheckPublicIp = v } })))),
            Section("Subscription requests", null,
                Row("User-Agent", null, Input(Cur.Subscriptions.UserAgent, v => Set(x => x with { Subscriptions = x.Subscriptions with { UserAgent = string.IsNullOrWhiteSpace(v) ? x.Subscriptions.UserAgent : v.Trim() } })))));
    }
}

public sealed class SettingsPage(Shell shell) : OptionsPage(shell)
{
    public override string Title => "Settings";

    public override string Glyph => Icons.Settings;

    protected override Control Build() => Stack(
        Header("Settings", "Startup, notifications and logging."),
        Section("Startup", null,
            Row("Connect automatically on start", "Uses smart selection or your default server, as configured.", Toggle(Cur.Startup.AutoConnect, v => Set(x => x with { Startup = x.Startup with { AutoConnect = v } })))),
        Section("Notifications", null,
            Row("Warn when subscription traffic reaches (%)", null, Number(Cur.Notifications.TrafficWarningPercent, 50, 100, v => Set(x => x with { Notifications = x.Notifications with { TrafficWarningPercent = (int)v } })))),
        Section("Logging & privacy", null,
            Row("Log level", "Debug adds core output (still redacted). Use only while troubleshooting.", EnumCombo(Cur.Logging.MinimumLevel, v => Set(x => x with { Logging = x.Logging with { MinimumLevel = v } }))),
            Row("Keep log files for (days)", null, Number(Cur.Logging.RetainDays, 1, 90, v => Set(x => x with { Logging = x.Logging with { RetainDays = (int)v } }))),
            Row("Telemetry", "Off. IBOX does not collect or send usage data.", new TextBlock { Text = "Off", Foreground = B(Text2) }),
            Caption("Data folder: " + S.DataDir)));
}

public sealed class LogsPage(Shell shell) : Page(shell)
{
    private readonly SelectableTextBlock _text = new() { FontFamily = new FontFamily("monospace"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = B(Text2) };
    private readonly TextBox _search = new() { PlaceholderText = "Search logs", Width = 260 };
    private readonly ScrollViewer _scroll = new() { Height = 560 };
    private ToggleSwitch? _autoScroll;
    private DateTimeOffset _clearedAt = DateTimeOffset.MinValue;

    public override string Title => "Logs";

    public override string Glyph => Icons.Logs;

    protected override Control Build()
    {
        _autoScroll = Toggle(true, _ => { });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                Ghost("Copy", () => _ = CopyAsync()),
                Ghost("Open folder", OpenFolder),
                Ghost("Clear view", () =>
                {
                    _clearedAt = DateTimeOffset.UtcNow;
                    Render();
                }),
            },
        };
        _search.TextChanged += (_, _) => Render();
        S.LogBuffer.Appended += (_, _) => Dispatcher.UIThread.Post(Render, DispatcherPriority.Background);
        _scroll.Content = _text;
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Header("Logs", "Secrets (subscription tokens, UUIDs, passwords, keys) are redacted before anything is written or shown.", actions),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _search, Caption("Auto-scroll"), _autoScroll } },
                Card(_scroll, new Thickness(14, 10)),
            },
        };
    }

    public override void OnShown() => Render();

    private IEnumerable<string> Lines()
    {
        var q = _search.Text?.Trim();
        return S.LogBuffer.Snapshot()
            .Where(r => r.TimestampUtc >= _clearedAt)
            .Select(r => $"{r.TimestampUtc.ToLocalTime():HH:mm:ss} {Level(r.Level)} {r.Category.Replace("JackiVpn.", string.Empty, StringComparison.Ordinal)}: {r.Message}")
            .Where(l => string.IsNullOrEmpty(q) || l.Contains(q, StringComparison.OrdinalIgnoreCase))
            .TakeLast(800);
    }

    private void Render()
    {
        if (!IsVisible)
        {
            return;
        }

        _text.Text = string.Join('\n', Lines());
        if (_autoScroll?.IsChecked == true)
        {
            _scroll.ScrollToEnd();
        }
    }

    private async Task CopyAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(string.Join('\n', Lines()));
            Shell.Toast("Copied", "The visible log lines are on the clipboard.");
        }
    }

    private void OpenFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", S.LogsDir) { UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Shell.Toast("Logs folder", S.LogsDir);
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Error or LogLevel.Critical => "ERR",
        LogLevel.Warning => "WRN",
        LogLevel.Information => "INF",
        _ => "DBG",
    };
}

public sealed class AboutPage(Shell shell) : Page(shell)
{
    public override string Title => "About";

    public override string Glyph => Icons.About;

    protected override Control Build()
    {
        var version = typeof(AboutPage).Assembly.GetName().Version?.ToString(3) ?? "?";
        var components = new StackPanel { Spacing = 4 };
        try
        {
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cores", "manifest.json")));
            foreach (var (name, core) in manifest!["cores"]!.AsObject())
            {
                components.Children.Add(Caption($"{name} {core?["version"]}   ·   {core?["license"]}   ·   {core?["source"]}"));
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or NullReferenceException or InvalidOperationException)
        {
            components.Children.Add(Hint("Core manifest not found."));
        }

        components.Children.Add(Caption($".NET {Environment.Version} · Avalonia UI"));
        return new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Header("About"),
                Card(new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        T("IBOX", 22, FontWeight.SemiBold),
                        Caption("Version " + version + " · Linux"),
                        Hint("Install folder: " + AppContext.BaseDirectory),
                        Hint("Data folder: " + S.DataDir),
                    },
                }),
                Section("Privacy", null, Caption("No accounts, no telemetry. Subscription links and credentials are stored encrypted on this computer; logs are redacted.")),
                Section("Components", null, components),
            },
        };
    }
}
