using System.Net;
using FluentAssertions;
using JackiVpn.Application.Ecosystem;
using JackiVpn.Application.Subscriptions;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Infrastructure.Persistence;
using JackiVpn.IntegrationTests.Infrastructure;
using JackiVpn.Security;
using JackiVpn.Subscriptions.Fetching;
using JackiVpn.Subscriptions.Parsing;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.IntegrationTests.Subscriptions;

/// <summary>
/// Two devices (each with its own database) over real TCP on loopback: the receiver must see the same 6-digit code
/// as the sender, nothing is imported without its approval, and an accepted subscription is added on the receiver
/// with the receiver's own device ID (so the provider's device limit applies to it).
/// </summary>
public sealed class EcosystemTransferTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jackivpn-eco-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakePasarGuard _panel = new();
    private readonly SafeHttpFetcher _fetcher = new();

    public EcosystemTransferTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _panel.Dispose();
        _fetcher.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Prompt(bool accept) : IEcosystemPrompt
    {
        public IncomingOffer? Seen { get; private set; }

        public Task<bool> ConfirmIncomingAsync(IncomingOffer offer, CancellationToken ct)
        {
            Seen = offer;
            return Task.FromResult(accept);
        }
    }

    private (SubscriptionService Service, SqliteSubscriptionRepository Repo) Device(string name, string hwid)
    {
        var db = new JackiDatabase(Path.Combine(_dir, name + ".db"));
        var protector = new DpapiSecretProtector();
        var subs = new SqliteSubscriptionRepository(db, protector);
        var profiles = new SqliteProfileRepository(db, protector);
        var settings = new AppSettings();
        var service = new SubscriptionService(subs, profiles, () => _fetcher, new ConfigParserPipeline(), () => settings, NullLogger.Instance,
            device: () => new DeviceIdentity(hwid, "Android", "15", "Pixel"));
        return (service, subs);
    }

    private EcosystemService Endpoint(SubscriptionService service, SqliteSubscriptionRepository repo, IEcosystemPrompt prompt, string id, int port) =>
        new(service, repo, prompt, () => id, "Windows", id, NullLogger.Instance) { BindAddress = IPAddress.Loopback, ListenPort = port };

    [Fact]
    public async Task Accepted_subscription_is_added_on_the_other_device_after_matching_codes()
    {
        _panel.Head().Endpoint("sing_box", FakePasarGuard.SingBoxJson("DE", "NL"), contentType: "application/json");
        var (senderSubs, senderRepo) = Device("sender", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var (receiverSubs, receiverRepo) = Device("receiver", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var original = await senderSubs.AddAsync(_panel.BaseUrl, "Family", null, Ct);

        var prompt = new Prompt(accept: true);
        var port = NetHelpers.FreePort();
        await using var receiver = Endpoint(receiverSubs, receiverRepo, prompt, "receiver", port);
        receiver.Start();
        await using var sender = Endpoint(senderSubs, senderRepo, new Prompt(false), "sender", NetHelpers.FreePort());
        string? senderCode = null;

        var result = await sender.SendAsync(new EcosystemPeer("receiver", "Phone", "Android", IPAddress.Loopback, port, receiver.PublicKey!, DateTimeOffset.UtcNow),
            [original.Id], c => senderCode = c, Ct);

        result.Accepted.Should().BeTrue();
        result.Imported.Should().Be(1);
        prompt.Seen!.Code.Should().Be(senderCode, "both screens show the same code (a device in the middle would change it)");
        prompt.Seen.SubscriptionNames.Should().Equal("Family");
        var received = (await receiverRepo.GetAllAsync(Ct)).Should().ContainSingle().Subject;
        received.ProfileCount.Should().Be(2);
        (await receiverRepo.GetSourceUrlAsync(received.Id, Ct)).Should().Be(_panel.BaseUrl);
        // A third-party (non-official) provider never receives a device ID unless the user turns it on.
        _panel.RequestHeaders().Should().OnlyContain(h => !h.ContainsKey("x-hwid"));
    }

    [Fact]
    public async Task Scan_probe_finds_a_listening_device_and_the_transfer_works_with_its_answer()
    {
        _panel.Head().Endpoint("sing_box", FakePasarGuard.SingBoxJson("DE"), contentType: "application/json");
        var (senderSubs, senderRepo) = Device("sender", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var (receiverSubs, receiverRepo) = Device("receiver", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var original = await senderSubs.AddAsync(_panel.BaseUrl, "Family", null, Ct);
        var port = NetHelpers.FreePort();
        await using var receiver = Endpoint(receiverSubs, receiverRepo, new Prompt(accept: true), "receiver", port);
        receiver.Start().Should().BeTrue();
        await using var sender = Endpoint(senderSubs, senderRepo, new Prompt(false), "sender", NetHelpers.FreePort());

        var peer = await sender.ProbeAsync(IPAddress.Loopback, port, Ct);
        var nobody = await sender.ProbeAsync(IPAddress.Loopback, NetHelpers.FreePort(), Ct);

        nobody.Should().BeNull();
        peer.Should().NotBeNull();
        peer!.Id.Should().Be("receiver");
        peer.Platform.Should().Be("Windows");
        peer.PublicKey.Should().Equal(receiver.PublicKey);
        (await sender.SendAsync(peer, [original.Id], _ => { }, Ct)).Imported.Should().Be(1);
        (await receiverRepo.GetAllAsync(Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Declined_offer_sends_nothing()
    {
        _panel.Head().Endpoint("sing_box", FakePasarGuard.SingBoxJson("DE"), contentType: "application/json");
        var (senderSubs, senderRepo) = Device("sender", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var (receiverSubs, receiverRepo) = Device("receiver", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var original = await senderSubs.AddAsync(_panel.BaseUrl, "Family", null, Ct);

        var port = NetHelpers.FreePort();
        await using var receiver = Endpoint(receiverSubs, receiverRepo, new Prompt(accept: false), "receiver", port);
        receiver.Start();
        await using var sender = Endpoint(senderSubs, senderRepo, new Prompt(false), "sender", NetHelpers.FreePort());

        var result = await sender.SendAsync(new EcosystemPeer("receiver", "Phone", "Android", IPAddress.Loopback, port, receiver.PublicKey!, DateTimeOffset.UtcNow),
            [original.Id], _ => { }, Ct);

        result.Accepted.Should().BeFalse();
        (await receiverRepo.GetAllAsync(Ct)).Should().BeEmpty();
    }
}
