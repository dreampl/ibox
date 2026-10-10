using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JackiVpn.Cores.Generation;

/// <summary>
/// Preserved unknown source fields are merged back into generated configs, but only after removing keys
/// that could reference files, other outbounds or processes. This matters most in the service, where the
/// core runs as SYSTEM: a smuggled <c>certificate_path</c> or <c>log.output</c> would be a file
/// read/write primitive.
/// </summary>
public static partial class ExtensionSanitizer
{
    public static JsonObject? Sanitize(JsonObject? source, ICollection<string>? dropped = null)
    {
        if (source is null)
        {
            return null;
        }

        var copy = new JsonObject();
        foreach (var (key, value) in source)
        {
            if (IsDangerousKey(key))
            {
                dropped?.Add(key);
                continue;
            }

            copy[key] = value switch
            {
                JsonObject o => Sanitize(o, dropped),
                JsonArray a => SanitizeArray(a, dropped),
                _ => value?.DeepClone(),
            };
        }

        return copy.Count == 0 ? null : copy;
    }

    private static JsonArray SanitizeArray(JsonArray a, ICollection<string>? dropped)
    {
        var copy = new JsonArray();
        foreach (var item in a)
        {
            copy.Add(item switch
            {
                JsonObject o => Sanitize(o, dropped),
                _ => item?.DeepClone(),
            });
        }

        return copy;
    }

    public static bool IsDangerousKey(string key) => DangerousRegex().IsMatch(key);

    /// <summary>Merges <paramref name="extensions"/> into <paramref name="target"/> without overriding generated keys.</summary>
    public static void MergeInto(JsonObject target, JsonObject? extensions, ICollection<string>? dropped = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        var clean = Sanitize(extensions, dropped);
        if (clean is null)
        {
            return;
        }

        foreach (var (k, v) in clean.ToList())
        {
            if (!target.ContainsKey(k))
            {
                clean.Remove(k);
                target[k] = v;
            }
        }
    }

    [GeneratedRegex(@"(^|_|-)(path|file|files|dir|directory|output|script|exec|command|cmd)$|^(detour|tag|dialerProxy|dialer_proxy|bind_interface|routing_mark|inet4_bind_address|inet6_bind_address|netns|external_controller|external_ui|cache_file|uri_params|vmess_json|xhttp_extra_raw)$|path$|Path$|File$|Dir$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousRegex();
}
