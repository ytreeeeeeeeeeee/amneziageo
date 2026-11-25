namespace AmneziaGeo.Decl;

/// <summary>The immutable routing policy installed at the start of one Windows tunnel.</summary>
public sealed record TunnelRoutingProjection(
    string InputStamp,
    string Stamp,
    IReadOnlyList<long> ListIds,
    RoutingSettings? Settings,
    bool Split,
    RoutingList List,
    IReadOnlyList<string> NamedProxyRoutes,
    IReadOnlyList<string> NamedOffTunnelRoutes,
    IReadOnlyList<string> BlockApps)
{
    /// <summary>The projection as seen by consumers of the legacy tunnel geo contract.</summary>
    public TunnelGeo Geo(string name) => new(name, Split, List.Rules, List.Routes, List.Domains, List.Apps,
        List.DirectRoutes, List.DirectDomains, List.BlockRoutes, List.BlockDomains);
}
