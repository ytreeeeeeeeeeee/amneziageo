using AmneziaGeo.Decl;
using AmneziaGeo.Geo;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// The routing rules a connection runs under: the proxy bucket goes through the tunnel, the direct bucket stays
/// off it, the block bucket is refused. Split tunnels only the proxy bucket; otherwise everything but direct goes.
/// </summary>
internal sealed record TunnelRouting(
    bool Split,
    string ListName,
    IReadOnlyList<GeoRule> Rules,
    IReadOnlyList<string> ProxyRoutes,
    IReadOnlyList<GeoDomain> ProxyDomains,
    IReadOnlyList<string> DirectRoutes,
    IReadOnlyList<GeoDomain> DirectDomains,
    IReadOnlyList<string> BlockRoutes,
    IReadOnlyList<GeoDomain> BlockDomains,
    IReadOnlyList<string> TunnelApps,
    bool AllUdp = false)
{
    /// <summary>
    /// No list selected: a full tunnel by the config's own AllowedIPs.
    /// </summary>
    public static TunnelRouting None { get; } = new(false, string.Empty, [], [], [], [], [], [], [], []);

    public string? Stamp { get; init; }
    public RoutingList? List { get; init; }
    public IReadOnlyList<long> ListIds { get; init; } = [];
    public IReadOnlyList<string> BlockApps { get; init; } = [];
    public bool HasDomains => ProxyDomains.Count + DirectDomains.Count + BlockDomains.Count > 0;
    public bool SteerNames => TunnelApps.Count + DirectApps.Count > 0 || EffectiveAllUdp;

    public string? Unsupported(bool ipv6) => BlockApps.Count > 0
        ? "Linux routing does not support blocking an application by executable."
        : ipv6 && (SteerNames && HasDomains || ProxyRoutes.Concat(DirectRoutes).Concat(BlockRoutes).Any(range => range.Contains(':')))
            ? "Linux cannot enforce this routing policy with IPv6 enabled." : null;
    public bool EffectiveAllUdp => Split && AllUdp;

    public string? ReconnectReason(TunnelRouting next)
    {
        if (Split != next.Split || (Stamp is null) != (next.Stamp is null))
        {
            return "the routing mode changed";
        }

        if (EffectiveAllUdp != next.EffectiveAllUdp)
        {
            return "the all-UDP policy changed";
        }

        if (!Same(TunnelApps, next.TunnelApps) || !Same(DirectApps, next.DirectApps) || !Same(BlockApps, next.BlockApps))
        {
            return "application rules changed";
        }

        return null;

        static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
            new HashSet<string>(a, StringComparer.Ordinal).SetEquals(b);
    }

    /// <summary>
    /// The applications the list keeps past the tunnel.
    /// </summary>
    public IReadOnlyList<string> DirectApps { get; init; } = [];

    public static async Task<TunnelRouting> LoadAsync(IStateStore store, GeoConfigurator geo, string? name, CancellationToken ct)
    {
        if (name is not null && !await ConfigRouting.AllowedAsync(store, name, ct).ConfigureAwait(false))
        {
            return None;
        }

        var ids = await store.GetSelectedRoutingListsAsync(ct).ConfigureAwait(false);
        return FromPolicy(await geo.LoadPolicyAsync(ids, AppRulePlatform.Linux, ct: ct).ConfigureAwait(false));
    }

    public static TunnelRouting FromPolicy(RoutingPolicy? policy)
    {
        if (policy is null)
        {
            return None;
        }

        var list = policy.List;
        return new TunnelRouting(policy.Settings?.UseGlobalProxy != true, list.Name, list.Rules, list.Routes, list.Domains,
            list.DirectRoutes, list.DirectDomains, list.BlockRoutes, list.BlockDomains, list.Apps, policy.Settings?.AllUdp == true)
        {
            Stamp = policy.Stamp,
            List = list,
            ListIds = [.. policy.Sources.Select(source => source.Id)],
            DirectApps = list.DirectApps,
            BlockApps = policy.BlockApps,
        };
    }
}
