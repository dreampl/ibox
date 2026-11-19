using System.Text.Json.Nodes;
using FluentAssertions;
using JackiVpn.Application.Account;
using JackiVpn.Application.Subscriptions;
using JackiVpn.Domain.Settings;
using JackiVpn.Infrastructure.Persistence;
using JackiVpn.Security;
using JackiVpn.Subscriptions.Fetching;
using JackiVpn.Subscriptions.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JackiVpn.IntegrationTests.Subscriptions;

/// <summary>Picking a subscription from the Telegram account adds it, even one deleted earlier.</summary>
public sealed class AccountSubscriptionPickTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jackivpn-pick-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakePasarGuard _panel = new();
    private readonly SafeHttpFetcher _fetcher = new();

    public AccountSubscriptionPickTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public async Task Picked_subscription_is_added_and_shows_as_added()
    {
        _panel.Head().Endpoint("sing_box", FakePasarGuard.SingBoxJson("DE"), contentType: "application/json");
        var db = new JackiDatabase(Path.Combine(_dir, "app.db"));
        var protector = new DpapiSecretProtector();
        var subs = new SqliteSubscriptionRepository(db, protector);
        var settings = new AppSettings();
        var service = new SubscriptionService(subs, new SqliteProfileRepository(db, protector), () => _fetcher, new ConfigParserPipeline(), () => settings, NullLogger.Instance);
        using var ibox = new IboxServices(_dir, protector);
        var import = new AccountSubscriptionImport(ibox, service, subs, _dir);
        var card = new SubscriptionCard("L1", "ali_nl", "active", "NL", 0, 0, null, new Uri(_panel.BaseUrl), false, null,
            new Dictionary<string, IReadOnlyList<long>>(), [], []);
        var view = new AccountView(null, [card], null, new JsonObject());

        ibox.Tokens.Set(IboxAccount.TokenName, "t0ken");
        (await import.ImportAsync(view, Ct)).Should().Be(1);
        var first = (await subs.GetAllAsync(Ct)).Single();
        await service.DeleteAsync(first.Id, Ct);
        (await import.ImportAsync(view, Ct)).Should().Be(0, "deleted ones are not re-added automatically");

        var sub = await import.AddChosenAsync(card, Ct);
        sub.Name.Should().Be("IBOX · ali_nl");
        (await subs.GetAllAsync(Ct)).Should().ContainSingle("picking it by hand adds it back");
    }
}
