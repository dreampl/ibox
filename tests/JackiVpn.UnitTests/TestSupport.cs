using JackiVpn.Domain.Profiles;
using JackiVpn.Subscriptions.Parsing;
using JackiVpn.Subscriptions.Parsing.Uris;

namespace JackiVpn.UnitTests;

internal static class TestSupport
{
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static ParseContext Ctx => new() { NowUtc = Now };

    public static ProxyProfile Link(string link) => ShareLinkParser.Parse(link, Ctx);

    public static ParseResult Parse(string text) => new ConfigParserPipeline().Parse(text, Ctx);

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    public static string B64Url(string s) => B64(s).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
