using System.Text.RegularExpressions;

namespace JackiVpn.Domain.Subscriptions;

public static partial class NexSubscriptionUrl
{
    public static bool TryParse(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || candidate.IdnHost != "sub.jackidrive.ir"
            || candidate.Port != OfficialProviders.IranEntryPort
            || candidate.UserInfo.Length != 0
            || candidate.Query.Length != 0
            || candidate.Fragment.Length != 0
            || !ComboPath().IsMatch(candidate.AbsolutePath))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    [GeneratedRegex(@"\A/sub/_jp/nx/[a-f0-9]{16,48}/?\z")]
    private static partial Regex ComboPath();
}
