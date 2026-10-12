using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Desktop.Composition;
using JackiVpn.Domain.Routing;
using JackiVpn.Domain.Settings;
using JackiVpn.Routing;

namespace JackiVpn.Desktop.ViewModels;

public sealed partial class RuleItem(RoutingRule rule, Action<RuleItem, bool> setEnabled) : ObservableObject
{
    public RoutingRule Rule { get; } = rule;

    public string Name => Rule.Name;

    public string Summary => string.Join("  ·  ", Rule.Conditions.Select(c => $"{Label(c.Type)}: {string.Join(", ", c.Values.Take(4))}{(c.Values.Count > 4 ? $" +{c.Values.Count - 4}" : string.Empty)}"))
        + (Rule.Invert ? "  (inverted)" : string.Empty);

    public string ActionText => Rule.Action.Type switch
    {
        RuleActionType.Proxy => "VPN",
        RuleActionType.Direct => "Direct",
        RuleActionType.Block => "Block",
        RuleActionType.HijackDns => "DNS",
        _ => Rule.Action.OutboundTag ?? "Outbound",
    };

    public string? PresetText => Rule.FromPreset is { } p ? $"from {p} preset" : null;

    public bool Enabled
    {
        get => Rule.Enabled;
        set => setEnabled(this, value);
    }

    public static string Label(RuleConditionType t) => t switch
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
        _ => t.ToString(),
    };
}

public sealed partial class RuleSetItem : ObservableObject
{
    [ObservableProperty]
    private string _status = "Not downloaded";

    public required RuleSetDefinition Definition { get; init; }

    public bool Declared { get; init; }

    public string Tag => Definition.Tag;

    public string Url => Definition.Url;

    public string Kind => Declared ? Definition.Format.ToString() : Definition.Format + " · auto (geo rule)";
}

public sealed partial class RoutingViewModel : SettingsPageViewModel
{
    private readonly AppServices _s;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private RuleItem? _selectedRule;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isUpdatingRuleSets;

    public RoutingViewModel(AppServices services, IDialogService dialogs)
        : base(services.Settings)
    {
        _s = services;
        _dialogs = dialogs;
        services.Settings.Changed += (_, _) => Ui.Post(Rebuild);
        services.RoutingWatcher.Applied += (_, ok) => Ui.Post(() => Message = ok
            ? "Applied to the running connection."
            : "Saved. The running connection could not take the change; it applies on the next connection.");
        Rebuild();
    }

    public ObservableCollection<RuleItem> Rules { get; } = [];

    public ObservableCollection<RuleSetItem> RuleSets { get; } = [];

    public IReadOnlyList<RoutingPreset> Presets { get; } = Choices.Of<RoutingPreset>();

    public IReadOnlyList<RuleActionType> FinalActions { get; } = [RuleActionType.Proxy, RuleActionType.Direct, RuleActionType.Block];

    public RoutingPreset Preset => S.Routing.Preset;

    public RuleActionType Final
    {
        get => S.Routing.Final;
        set
        {
            if (value == RuleActionType.Direct && S.Routing.Final != RuleActionType.Direct
                && !_dialogs.Confirm("Unmatched traffic goes direct", "Traffic that matches no rule will bypass the VPN. Only rules marked VPN will be tunneled. Continue?"))
            {
                OnPropertyChanged();
                return;
            }

            Set(s => s with { Routing = s.Routing with { Final = value } });
        }
    }

    public bool BypassLan
    {
        get => S.Routing.BypassLan;
        set => Set(s => s with { Routing = s.Routing with { BypassLan = value } });
    }

    public bool Sniff
    {
        get => S.Routing.Sniff;
        set => Set(s => s with { Routing = s.Routing with { Sniff = value } });
    }

    private void Rebuild()
    {
        var selectedId = SelectedRule?.Rule.Id;
        Rules.Clear();
        foreach (var r in S.Routing.Rules)
        {
            Rules.Add(new RuleItem(r, SetEnabled));
        }

        SelectedRule = Rules.FirstOrDefault(r => r.Rule.Id == selectedId);
        var statuses = RuleSets.ToDictionary(r => r.Tag, r => r.Status, StringComparer.Ordinal);
        RuleSets.Clear();
        foreach (var d in S.Routing.RuleSets)
        {
            RuleSets.Add(new RuleSetItem { Definition = d, Declared = true, Status = statuses.GetValueOrDefault(d.Tag, "Not checked") });
        }

        foreach (var d in RoutingPresets.ImpliedRuleSets(S.Routing.Rules, S.Routing.RuleSets.ToList()))
        {
            RuleSets.Add(new RuleSetItem { Definition = d, Declared = false, Status = statuses.GetValueOrDefault(d.Tag, "Not checked") });
        }
    }

    private void SetEnabled(RuleItem item, bool enabled) => UpdateRules(rules => rules.Select(r => r.Id == item.Rule.Id ? r with { Enabled = enabled } : r).ToList());

    private void UpdateRules(Func<List<RoutingRule>, List<RoutingRule>> change) =>
        Set(s => s with { Routing = s.Routing with { Rules = change(s.Routing.Rules.ToList()), Preset = RoutingPreset.Custom } });

    private IReadOnlyList<string> KnownTags() => S.Routing.RuleSets.Select(r => r.Tag).ToList();

    [RelayCommand]
    private void ApplyPreset(RoutingPreset preset)
    {
        if (preset == RoutingPreset.Custom)
        {
            return;
        }

        if (!_dialogs.Confirm("Apply preset", $"Replace the current rules with the {preset} preset? You can edit every rule afterwards (export first to keep a copy)."))
        {
            return;
        }

        var built = RoutingPresets.Build(preset);
        Set(s => s with
        {
            Routing = s.Routing with
            {
                Preset = preset,
                Rules = built.Rules,
                RuleSets = built.RuleSets,
                Final = built.Final,
            },
        });
        Message = $"{preset} preset applied.";
    }

    [RelayCommand]
    private void AddRule()
    {
        var rule = _dialogs.EditRule(null, KnownTags());
        if (rule is not null)
        {
            UpdateRules(rules =>
            {
                rules.Insert(0, rule);
                return rules;
            });
        }
    }

    [RelayCommand]
    private void EditRule(RuleItem? item)
    {
        if (item is null)
        {
            return;
        }

        var rule = _dialogs.EditRule(item.Rule, KnownTags());
        if (rule is not null)
        {
            UpdateRules(rules => rules.Select(r => r.Id == item.Rule.Id ? rule : r).ToList());
        }
    }

    [RelayCommand]
    private void DeleteRule(RuleItem? item)
    {
        if (item is not null && _dialogs.Confirm("Delete rule", $"Delete \"{item.Name}\"?", danger: true))
        {
            UpdateRules(rules => rules.Where(r => r.Id != item.Rule.Id).ToList());
        }
    }

    [RelayCommand]
    private void MoveUp(RuleItem? item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(RuleItem? item) => Move(item, 1);

    private void Move(RuleItem? item, int delta)
    {
        if (item is null)
        {
            return;
        }

        UpdateRules(rules =>
        {
            var i = rules.FindIndex(r => r.Id == item.Rule.Id);
            var j = i + delta;
            if (i >= 0 && j >= 0 && j < rules.Count)
            {
                (rules[i], rules[j]) = (rules[j], rules[i]);
            }

            return rules;
        });
    }

    [RelayCommand]
    private async Task AddRuleSetAsync()
    {
        var url = await _dialogs.PromptAsync("Add rule-set", "URL of a sing-box rule-set (.srs binary or .json source):");
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                _dialogs.Info("Rule-set", "Please enter an https:// URL.");
            }

            return;
        }

        var tag = await _dialogs.PromptAsync("Add rule-set", "Tag (used in rules, letters/digits/-/_):", Path.GetFileNameWithoutExtension(uri.AbsolutePath));
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
        Set(s => s with { Routing = s.Routing with { RuleSets = s.Routing.RuleSets.Where(r => r.Tag != def.Tag).Append(def).ToList() } });
    }

    [RelayCommand]
    private void RemoveRuleSet(RuleSetItem? item)
    {
        if (item is { Declared: true } && _dialogs.Confirm("Remove rule-set", $"Remove rule-set \"{item.Tag}\"? Rules using it will skip those conditions.", danger: true))
        {
            Set(s => s with { Routing = s.Routing with { RuleSets = s.Routing.RuleSets.Where(r => r.Tag != item.Tag).ToList() } });
        }
    }

    [RelayCommand]
    private async Task UpdateRuleSetsAsync()
    {
        IsUpdatingRuleSets = true;
        try
        {
            var defs = RuleSets.Select(r => r.Definition).ToList();
            var results = await Task.Run(() => _s.RuleSets.EnsureAsync(defs, force: true, _s.Lifetime));
            foreach (var r in results)
            {
                if (RuleSets.FirstOrDefault(x => x.Tag == r.Tag) is { } item)
                {
                    item.Status = r.Available
                        ? (r.UsedStaleCache ? $"Cached copy ({r.Error})" : $"Up to date · {Format.Bytes(r.Entry?.Size)} · {Format.When(r.Entry?.FetchedUtc)}")
                        : $"Unavailable: {r.Error}";
                }
            }

            Message = $"{results.Count(r => r.Available)} of {results.Count} rule-set(s) available.";
        }
        finally
        {
            IsUpdatingRuleSets = false;
        }
    }

    [RelayCommand]
    private void Export()
    {
        var path = _dialogs.SaveFile("Jacki routing rules|*.json", "jacki-routing.json");
        if (path is null)
        {
            return;
        }

        File.WriteAllText(path, RulesPortability.Export(new RulesDocument { Final = S.Routing.Final, Rules = S.Routing.Rules, RuleSets = S.Routing.RuleSets }));
        Message = "Rules exported.";
    }

    [RelayCommand]
    private void Import()
    {
        var path = _dialogs.OpenFile("Jacki routing rules|*.json|All files|*.*");
        if (path is null)
        {
            return;
        }

        var info = new FileInfo(path);
        if (info.Length > 4 * 1024 * 1024)
        {
            _dialogs.Info("Import", "The file is too large.");
            return;
        }

        var (doc, validation) = RulesPortability.Import(File.ReadAllText(path));
        if (doc is null || !validation.IsValid)
        {
            _dialogs.Info("Import failed", string.Join(Environment.NewLine, validation.Errors.Select(e => "• " + e.Message)));
            return;
        }

        if (_dialogs.Confirm("Import rules", $"Replace the current {S.Routing.Rules.Count} rule(s) with {doc.Rules.Count} imported rule(s)?"))
        {
            Set(s => s with { Routing = s.Routing with { Rules = doc.Rules, RuleSets = doc.RuleSets, Final = doc.Final, Preset = RoutingPreset.Custom } });
            Message = "Rules imported.";
        }
    }
}
