using System;
using System.IO;
using System.Text;
using JackiVpn.Domain.Agent;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// Connects Claude Code and Codex to the notch on the user's request: IBOX's hooks are added to their own settings
/// (a copy of the original file is kept next to it once), and removed again on request. Respects
/// <c>CLAUDE_CONFIG_DIR</c> and <c>CODEX_HOME</c>.
/// </summary>
public static class AgentHooksSetup
{
    public static string ClaudeSettingsPath =>
        Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".claude"), "settings.json");

    public static string CodexHooksPath =>
        Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".codex"), "hooks.json");

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>ibox-ctl.exe next to IBOX (JACKIVPN_CTL points a development build elsewhere).</summary>
    /// <summary>The hook program next to the app: ibox-ctl.exe (IBOX) or ibar-ctl.exe (IBAR).</summary>
    public static string CtlName { get; set; } = "ibox-ctl.exe";

    public static string? CtlPath
    {
        get
        {
            if (Environment.GetEnvironmentVariable("JACKIVPN_CTL") is { Length: > 0 } custom && File.Exists(custom))
            {
                return custom;
            }

            var next = Path.Combine(AppContext.BaseDirectory, CtlName);
            return File.Exists(next) ? next : null;
        }
    }

    public static bool ClaudeConnected => AgentHookConfig.ClaudeInstalled(Read(ClaudeSettingsPath));

    public static bool CodexConnected => AgentHookConfig.CodexInstalled(Read(CodexHooksPath));

    /// <summary>Adds the hooks; returns a problem to show, or null.</summary>
    public static string? ConnectClaude() => Change(ClaudeSettingsPath, json => CtlPath is { } ctl ? AgentHookConfig.ClaudeInstall(json, ctl) : null);

    public static string? DisconnectClaude() => Change(ClaudeSettingsPath, AgentHookConfig.ClaudeRemove);

    public static string? ConnectCodex() => Change(CodexHooksPath, json => CtlPath is { } ctl ? AgentHookConfig.CodexInstall(json, AgentHookConfig.CodexCommand(ctl)) : null);

    public static string? DisconnectCodex() => Change(CodexHooksPath, AgentHookConfig.CodexRemove);

    private static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Change(string path, Func<string?, string?> change)
    {
        if (CtlPath is null)
        {
            return $"{CtlName} was not found next to the app. Reinstall it to connect the assistants.";
        }

        try
        {
            var before = File.Exists(path) ? File.ReadAllText(path) : null;
            string? after;
            try
            {
                after = change(before);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
            {
                return $"{path} could not be read ({ex.Message}). It was left as it is.";
            }

            if (after is null || after == before)
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var backup = path + ".ibox-backup";
            if (before is not null && !File.Exists(backup))
            {
                File.Copy(path, backup);
            }

            var temp = path + ".ibox-tmp";
            File.WriteAllText(temp, after, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
