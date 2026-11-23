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

/// <summary>After a Telegram sign-in the account's subscription links land in IBOX by themselves, once.</summary>
public sealed class AccountSubscriptionImportTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jackivpn-import-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakePasarGuard _panel = new();
    private readonly SafeHttpFetcher _fetcher = new();

    public AccountSubscriptionImportTests() => Directory.CreateDirectory(_dir);

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

    private AccountView View(params string[] links) => new(null, links.Select((l, i) => new SubscriptionCard(
        "lid" + i, "user" + i, "active", "DE", 0, 0, null, new Uri(l), false, null,
        new Dictionary<string, IReadOnlyList<long>>(), [], [])).ToList(), null, new JsonObject());

    [Fact]
    public async Task Signed_in_account_links_are_added_once_and_a_deleted_one_stays_deleted()
    {
        _panel.Head().Endpoint("sing_box", FakePasarGuard.SingBoxJson("DE", "NL"), contentType: "application/json");
        var db = new JackiDatabase(Path.Combine(_dir, "app.db"));
        var protector = new DpapiSecretProtector();
        var subs = new SqliteSubscriptionRepository(db, protector);
        var settings = new AppSettings();
        var service = new SubscriptionService(subs, new SqliteProfileRepository(db, protector), () => _fetcher, new ConfigParserPipeline(), () => settings, NullLogger.Instance);
        using var ibox = new IboxServices(_dir, protector);
        var import = new AccountSubscriptionImport(ibox, service, subs, _dir);
        var view = View(_panel.BaseUrl, _panel.BaseUrl + "/");

        (await import.ImportAsync(view, Ct)).Should().Be(0, "signed out, nothing is imported");

        ibox.Tokens.Set(IboxAccount.TokenName, "t0ken");
        (await import.ImportAsync(view, Ct)).Should().Be(1, "the same link twice is one subscription");
        var all = await subs.GetAllAsync(Ct);
        all.Should().ContainSingle().Which.Name.Should().Be("IBOX · user0");

        (await import.ImportAsync(view, Ct)).Should().Be(0, "already present");

        await service.DeleteAsync(all[0].Id, Ct);
        (await import.ImportAsync(view, Ct)).Should().Be(0, "a subscription the user deleted is not brought back");
        (await subs.GetAllAsync(Ct)).Should().BeEmpty();
    }
}
