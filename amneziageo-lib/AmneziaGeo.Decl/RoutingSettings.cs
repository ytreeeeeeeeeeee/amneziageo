namespace AmneziaGeo.Decl;

/// <summary>
/// Per-routing-list traffic policy. Exclusions: bypass entries off the tunnel. AllUdp: wrap all outbound UDP
/// through the tunnel. Mode: "split" applies the rules; "full" routes everything while keeping bypass.
/// Absent row = defaults. UseGlobalProxy: route everything through the tunnel except the Direct bucket (full);
/// off tunnels only the Proxy bucket (split). IPv6 is per-config now (ConfigTransport.UseIpv6), not per-list.
/// </summary>
public sealed record RoutingSettings(
    long ListId,
    string Exclusions,
    bool AllUdp,
    string Mode = "split",
    bool UseGlobalProxy = false)
{
    /// <summary>Combines list settings. A missing row does not supply an explicit exclusions override.</summary>
    public static RoutingSettings? Combine(long listId, IReadOnlyList<RoutingSettings> settings)
    {
        if (settings.Count == 0)
        {
            return null;
        }

        var full = settings.Any(setting => setting.UseGlobalProxy);
        var exclusions = settings.SelectMany(setting => ExclusionTokens(setting.Exclusions)).Distinct(StringComparer.Ordinal);
        return new RoutingSettings(listId, string.Join('\n', exclusions), settings.Any(setting => setting.AllUdp),
            full ? "full" : "split", full);
    }

    /// <summary>Individual addresses or domains stored in the exclusions field.</summary>
    public static string[] ExclusionTokens(string text) =>
        text.Split([',', ';', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
