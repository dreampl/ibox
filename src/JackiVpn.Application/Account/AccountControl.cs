using System.Text.Json;
using System.Text.Json.Nodes;
using JackiVpn.Application.Connection;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Connection;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Account;

public sealed record AccountDevice(string Sid, string Name, string Platform, bool Me, bool Online, bool RemoteAllowed, string State, string? ProfileId, string? ProfileName, DateTimeOffset? ConnectedSince, IReadOnlyList<DeviceConfig> Configs)
{
    public string? LastCommandResult { get; init; }
    public bool Connected => Online && State == "Connected";
    public string Detail => $"{Name} · {Platform} · {(Online ? State : "Offline")} · {ProfileName ?? "No config"}";
    public string Tooltip => Detail + (ConnectedSince is { } time ? $" · connected {time.ToLocalTime():g}" : "") + (LastCommandResult is { Length: > 0 } result ? " · Last command: " + result : "");
}
public sealed record DeviceConfig(string Id, string Name);

public sealed class AccountControl : IDisposable
{
    private readonly IboxServices _ibox;
    private readonly IProfileRepository _profiles;
    private readonly ConnectionOrchestrator _connection;
    private readonly ILogger? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _pulse = new(1, 1);
    private readonly string _platform;
    private readonly string _version;
    private readonly Dictionary<string, string> _acks;
    private bool _disposed;
    public AccountControl(IboxServices ibox, IProfileRepository profiles, ConnectionOrchestrator connection, string platform, string version, ILogger? log = null)
    {
        _ibox = ibox; _profiles = profiles; _connection = connection; _platform = platform; _version = version; _log = log;
        try { _acks = JsonSerializer.Deserialize<Dictionary<string,string>>(ibox.Tokens.Get("control-acks") ?? "{}") ?? []; }
        catch (JsonException) { _acks = []; }
        _ = RunAsync(_stop.Token);
    }
    public bool AllowRemote
    {
        get => _ibox.Tokens.Get("allow-remote-control") == "yes";
        set { _ibox.Tokens.Set("allow-remote-control", value ? "yes" : "no"); Changed?.Invoke(this, EventArgs.Empty); }
    }
    public IReadOnlyList<AccountDevice> Devices { get; private set; } = [];
    public string? Problem { get; private set; }
    public event EventHandler? Changed;
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var response = await _ibox.Account.ControlAsync("state", null, ct).ConfigureAwait(false);
        Read(response);
    }
    public Task<ApiResult> CommandAsync(string sid, string action, string? profileId = null, CancellationToken ct = default) =>
        _ibox.Account.ControlAsync("command", new { sid, action, profile_id = profileId }, ct);
    public Task<ApiResult> FamilyAsync(object? request = null, CancellationToken ct = default) =>
        _ibox.Account.ControlAsync("family", request, ct);
    private void Read(ApiResult response)
    {
        if (!response.Ok)
        {
            Devices = Devices.Select(device => device with { Online = false }).ToArray();
            Problem = response.Error; Changed?.Invoke(this, EventArgs.Empty); return;
        }
        Problem = null;
        Devices = (response.Body?["devices"] as JsonArray ?? []).OfType<JsonObject>().Select(ParseDevice).ToArray();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public static AccountDevice ParseDevice(JsonObject item) => new(
        Text(item,"sid"), Text(item,"name"), Text(item,"platform"), Flag(item,"me"), Flag(item,"online"), Flag(item,"allow_remote"), Text(item,"state"),
        item["profile_id"]?.ToString(), item["profile_name"]?.ToString(), Timestamp(item["connected_since"]),
        (item["configs"] as JsonArray ?? []).OfType<JsonObject>().Select(x => new DeviceConfig(Text(x,"id"), Text(x,"name"))).ToArray()) { LastCommandResult = item["last_command_result"]?.ToString() };
    private static DateTimeOffset? Timestamp(JsonNode? node) => DateTimeOffset.TryParse(node?.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var time) ? time : null;
    private static string Text(JsonObject item, string key) => item[key]?.ToString() ?? "";
    private static bool Flag(JsonObject item, string key) => item[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_ibox.SignedIn) { await PulseAsync(ct).ConfigureAwait(false); }
                else if (Devices.Count > 0) { Devices = []; Changed?.Invoke(this, EventArgs.Empty); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is ServiceUnreachableException or InvalidOperationException or HttpRequestException or IOException or JsonException)
            {
                Devices = Devices.Select(device => device with { Online = false }).ToArray();
                Problem = "Account control is temporarily unavailable.";
                _log?.LogDebug("Account control update failed ({Kind}).", ex.GetType().Name);
                Changed?.Invoke(this, EventArgs.Empty);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
    private async Task PulseAsync(CancellationToken ct)
    {
        await _pulse.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var status = _connection.Status;
            var profiles = await _profiles.GetAllAsync(ct).ConfigureAwait(false);
            var result = await _ibox.Account.ControlAsync("pulse", new
            {
                name = IboxServices.DeviceName(_platform), platform = _platform, version = _version,
                allow_remote = AllowRemote, state = status.State.ToString(), profile_id = status.ProfileId,
                profile_name = status.ProfileName, connected_since = status.ConnectedSinceUtc,
                configs = profiles.Take(200).Select(p => new { id = p.Id, name = p.DisplayName }),
                acknowledgements = _acks.Select(p => new { id = p.Key, result = p.Value }).ToArray(),
            }, ct).ConfigureAwait(false);
            if (result.SignedOut) { await _connection.DisconnectAsync(ct).ConfigureAwait(false); Devices = []; Changed?.Invoke(this, EventArgs.Empty); return; }
            Read(result);
            if (!result.Ok) { return; }
            foreach (var command in (result.Body?["commands"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var id = Text(command,"id");
                if (id.Length is 0 or > 100 || _acks.ContainsKey(id)) { continue; }
                var expiry = Timestamp(command["expires"]);
                var answer = "rejected";
                if (AllowRemote && expiry > DateTimeOffset.UtcNow && expiry <= DateTimeOffset.UtcNow.AddMinutes(3))
                {
                    // Persist before execution: a restart must never replay a connect/disconnect command.
                    _acks[id] = "received"; SaveAcks();
                    try
                    {
                        var action = Text(command,"action");
                        if (action == "disconnect") { await _connection.DisconnectAsync(ct).ConfigureAwait(false); answer = "done"; }
                        else if (action == "connect")
                        {
                            var profileId = command["profile_id"]?.ToString();
                            if (profileId is not null && profiles.Any(p => p.Id == profileId))
                            {
                                if (_connection.State == ConnectionState.Connected) { await _connection.DisconnectAsync(ct).ConfigureAwait(false); }
                                answer = await _connection.ConnectAsync(new ConnectRequest { ProfileId = profileId, Smart = false }, ct).ConfigureAwait(false) ? "done" : "connection_failed";
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { answer = "failed"; }
                }
                _acks[id] = answer; SaveAcks();
            }
        }
        finally { _pulse.Release(); }
    }
    private void SaveAcks()
    {
        while (_acks.Count > 128) { _acks.Remove(_acks.Keys.First()); }
        _ibox.Tokens.Set("control-acks", JsonSerializer.Serialize(_acks));
    }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true; _stop.Cancel(); _stop.Dispose();
    }
}
