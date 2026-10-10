using System.Security.Cryptography;
using FluentAssertions;
using JackiVpn.Domain.Profiles;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Subscriptions.Ownership;
using JackiVpn.Subscriptions.Sync;
using static JackiVpn.UnitTests.TestSupport;

namespace JackiVpn.UnitTests.Subscriptions;

public class MergerAndOwnershipTests
{
    private const string U1 = "11111111-2222-4333-8444-555555555555";
    private const string U2 = "99999999-2222-4333-8444-555555555555";

    [Fact]
    public void Merge_keeps_ids_and_favorites_and_classifies_changes()
    {
        var favorite = Link($"vless://{U1}@a.example.com:443?type=ws&path=%2Fa#A") with { Id = "id-a", User = new ProfileUserData { IsFavorite = true, CustomName = "Mine" } };
        var rotated = Link($"vless://{U1}@b.example.com:443?type=ws&path=%2Fb#B") with { Id = "id-b" };
        var gone = Link($"trojan://other@c.example.com:443#C") with { Id = "id-c" };

        var incoming = new[]
        {
            Link($"vless://{U1}@a.example.com:443?type=ws&path=%2Fa#A renamed by provider"),
            Link($"vless://{U2}@b.example.com:443?type=ws&path=%2Fb#B"),
            Link($"trojan://pw@d.example.com:443#D"),
            Link($"trojan://pw@d.example.com:443#D duplicate"),
        };

        var report = SubscriptionMerger.Plan([favorite, rotated, gone], incoming, Now);
        var plan = report.Plan;

        report.DuplicatesDropped.Should().Be(1);
        plan.Changed.Should().HaveCount(2);
        var a = plan.Changed.Single(p => p.Id == "id-a");
        a.User.IsFavorite.Should().BeTrue();
        a.User.CustomName.Should().Be("Mine");
        a.Name.Should().Be("A renamed by provider");
        var b = plan.Changed.Single(p => p.Id == "id-b");
        b.Credentials.Uuid.Should().Be(U2);
        b.SyncState.Should().Be(ProfileSyncState.Changed);
        plan.Added.Should().ContainSingle().Which.SyncState.Should().Be(ProfileSyncState.New);
        plan.DeletedIds.Should().Equal(["id-c"], "an unmarked node the provider dropped is deleted, not kept as a duplicate");
        plan.RemovedIds.Should().BeEmpty();
    }

    [Fact]
    public void Random_address_sni_path_and_short_id_on_every_fetch_never_grow_the_list()
    {
        // PasarGuard picks a random address / SNI / path / REALITY short-id from lists on every request.
        static ProxyProfile Fetch(int i) => Link(
            $"vless://{U1}@edge{i % 3}.example.com:443?security=reality&sni=s{i % 4}.example.com&pbk=PUBKEY&sid=0{i % 5}&type=tcp#DE Frankfurt");
        static ProxyProfile Fetch2(int i) => Link($"vless://{U1}@cdn{i % 2}.example.com:443?type=ws&path=%2Fp{i}&security=tls#🇳🇱 NL | {50 - i} GB left");

        IReadOnlyList<ProxyProfile> stored = [];
        for (var round = 0; round < 6; round++)
        {
            var plan = SubscriptionMerger.Plan(stored, [Fetch(round), Fetch2(round)], Now).Plan;
            stored = [.. plan.Added, .. plan.Changed, .. plan.Unchanged];
            plan.Deleted.Should().BeEmpty();
        }

        stored.Should().HaveCount(2);
    }

    [Fact]
    public void Leftover_duplicates_from_older_versions_are_cleaned_up_but_marked_nodes_stay()
    {
        var live = Link($"vless://{U1}@a.example.com:443?type=ws#A") with { Id = "live" };
        var leftover = Link($"vless://{U1}@old.example.com:443?type=ws&path=%2Fold#A old") with { Id = "dup", SyncState = ProfileSyncState.RemovedByProvider };
        var favorite = Link($"trojan://zz@f.example.com:443#Fav") with { Id = "fav", User = new ProfileUserData { IsFavorite = true } };

        var plan = SubscriptionMerger.Plan([leftover, live, favorite], [Link($"vless://{U1}@a.example.com:443?type=ws#A")], Now).Plan;

        plan.Unchanged.Single().Id.Should().Be("live", "the live node wins over an old leftover");
        plan.Deleted.Should().Equal("dup");
        plan.RemovedIds.Should().Equal("fav");
    }

    [Fact]
    public void Identical_payload_is_unchanged()
    {
        var existing = Link($"vless://{U1}@a.example.com:443?type=ws#A") with { Id = "x" };
        var plan = SubscriptionMerger.Plan([existing], [Link($"vless://{U1}@a.example.com:443?type=ws#A")], Now).Plan;

        plan.HasChanges.Should().BeFalse();
        plan.Unchanged.Single().Id.Should().Be("x");
    }

    [Fact]
    public void Domain_allow_list_ownership()
    {
        var policy = new OwnershipPolicy { AllowedProviderDomains = ["jacki-example.test"] };

        ProfileOwnershipVerifier.VerifySubscription("panel.jacki-example.test", true, new Dictionary<string, string>(), policy, Now)
            .Should().Be(new ProfileOwnership(true, OwnershipMethod.DomainAllowList, "Host panel.jacki-example.test is in the provider allow-list."));
        ProfileOwnershipVerifier.VerifySubscription("evil-jacki-example.test", true, new Dictionary<string, string>(), policy, Now).IsOwned.Should().BeFalse();
    }

    [Fact]
    public void Header_marker_requires_https_and_exact_value()
    {
        var policy = new OwnershipPolicy { MarkerHeaderName = "X-Jacki-Profile", MarkerHeaderValue = "v1" };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-jacki-profile"] = "v1" };

        ProfileOwnershipVerifier.VerifySubscription("any.example.com", true, headers, policy, Now).Method.Should().Be(OwnershipMethod.ProviderHeader);
        ProfileOwnershipVerifier.VerifySubscription("any.example.com", false, headers, policy, Now).IsOwned.Should().BeFalse();
        ProfileOwnershipVerifier.VerifySubscription("any.example.com", true, new Dictionary<string, string> { ["X-Jacki-Profile"] = "v2" }, policy, Now).IsOwned.Should().BeFalse();
        ProfileOwnershipVerifier.VerifySubscription("any.example.com", true, headers, policy with { HeaderRequiresAllowedDomain = true }, Now).IsOwned.Should().BeFalse();
    }

    [Fact]
    public void Signed_manifest_ownership()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var manifest = ProfileOwnershipVerifier.CreateManifest(["panel.jacki-example.test"], Now.AddDays(30), key);
        var policy = new OwnershipPolicy { ManifestPublicKeyPem = pem };
        var headers = new Dictionary<string, string> { [policy.ManifestHeaderName] = manifest };

        ProfileOwnershipVerifier.VerifySubscription("panel.jacki-example.test", true, headers, policy, Now).Method.Should().Be(OwnershipMethod.SignedManifest);
        ProfileOwnershipVerifier.VerifySubscription("other.example.com", true, headers, policy, Now).IsOwned.Should().BeFalse("manifest is bound to its hosts");
        ProfileOwnershipVerifier.VerifySubscription("panel.jacki-example.test", true, headers, policy, Now.AddDays(60)).IsOwned.Should().BeFalse("expired");

        var tampered = manifest[..^4] + (manifest[^4] == 'A' ? "BBBB" : "AAAA");
        ProfileOwnershipVerifier.VerifySubscription("panel.jacki-example.test", true, new Dictionary<string, string> { [policy.ManifestHeaderName] = tampered }, policy, Now).IsOwned.Should().BeFalse();

        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = ProfileOwnershipVerifier.CreateManifest(["panel.jacki-example.test"], Now.AddDays(30), otherKey);
        ProfileOwnershipVerifier.VerifySubscription("panel.jacki-example.test", true, new Dictionary<string, string> { [policy.ManifestHeaderName] = forged }, policy, Now).IsOwned.Should().BeFalse();
    }

    [Fact]
    public void Remark_text_never_grants_ownership_but_host_allow_list_does_for_single_configs()
    {
        var p = Link($"vless://{U1}@104.16.1.1:443?security=tls&sni=edge.jacki-example.test&type=ws&host=edge.jacki-example.test#Jacki official server");

        ProfileOwnershipVerifier.VerifySingleProfile(p, new OwnershipPolicy()).IsOwned.Should().BeFalse();
        ProfileOwnershipVerifier.VerifySingleProfile(p, new OwnershipPolicy { SingleConfigAllowedHosts = ["jacki-example.test"] })
            .Method.Should().Be(OwnershipMethod.SingleConfigHostAllowList);
    }
}
