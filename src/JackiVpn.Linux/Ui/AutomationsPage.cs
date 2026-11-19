using Avalonia.Controls;
using Avalonia.Threading;
using JackiVpn.Application.Automations;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

/// <summary>Automations: an on/off switch per automation, their options and a log of what they did.</summary>
public sealed class AutomationsPage(Shell shell) : OptionsPage(shell)
{
    private readonly TextBlock _log = Hint(string.Empty);

    public override string Title => "Automations";

    public override string Glyph => Icons.Bolt;

    protected override bool AppliesLive => true;

    protected override Control Build()
    {
        var rows = new List<Control>();
        foreach (var a in S.Automations.Automations)
        {
            var id = a.Id;
            rows.Add(Row(AutomationTexts.Title(id), AutomationTexts.Hint(id),
                Toggle(Cur.Automations.IsEnabled(id), v => Set(x => x with { Automations = x.Automations.With(id, v) }))));
            if (id == TrustedNetworksAutomation.AutomationId)
            {
                rows.Add(Row("Trusted Wi-Fi names", "Separated by commas. Read with nmcli (NetworkManager).", Input(string.Join(", ", Cur.Automations.TrustedNetworks), v =>
                    Set(x => x with { Automations = x.Automations with { TrustedNetworks = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList() } }),
                    "Home, Office")));
            }
            else if (id == NightlyRefreshAutomation.AutomationId)
            {
                rows.Add(Row("Remove dead servers", "Delete servers that failed every test for 3 days (otherwise they are only listed in the log).",
                    Toggle(Cur.Automations.RemoveDeadAfterDays > 0, v => Set(x => x with { Automations = x.Automations with { RemoveDeadAfterDays = v ? 3 : 0 } }))));
            }
        }

        EventHandler<AutomationLogEntry> added = (_, _) => Dispatcher.UIThread.Post(RenderLog);
        S.Automations.Log.Added += added;
        Track(() => S.Automations.Log.Added -= added);
        RenderLog();
        return Stack(
            Header("Automations", "Small helpers that act for you. Each one is off until you turn it on, and everything they do is listed below."),
            Section("Automations", null, rows.ToArray()),
            Section("What they did", null, _log));
    }

    private void RenderLog()
    {
        var entries = S.Automations.Log.Entries;
        _log.Text = entries.Count == 0 ? "Nothing yet." : string.Join(Environment.NewLine, entries.Reverse().Take(50).Select(AutomationTexts.Format));
    }
}
