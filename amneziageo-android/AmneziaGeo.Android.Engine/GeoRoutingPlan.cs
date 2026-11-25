using System.Security.Cryptography;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// The rules one session routes by. Addresses go to the router as ranges and names stay as rules the resolved
/// answers are matched against - nothing is turned into system routes, so a name that moves keeps its verdict.
/// </summary>
public sealed record GeoRoutingPlan(
    IReadOnlyList<string> ProxyRoutes,
    IReadOnlyList<string> DirectRoutes,
    IReadOnlyList<string> BlockRoutes,
    IReadOnlyList<GeoDomain> ProxyDomains,
    IReadOnlyList<GeoDomain> DirectDomains,
    IReadOnlyList<GeoDomain> BlockDomains,
    bool FullTunnel,
    bool AllUdp,
    int TtlSeconds = 300)
{
    /// <summary>
    /// Applications the list names: what no other rule decides for them rides the tunnel.
    /// </summary>
    public IReadOnlyList<string> TunnelApps { get; init; } = [];
    public IReadOnlyList<string> DirectApps { get; init; } = [];
    public IReadOnlyList<string> BlockApps { get; init; } = [];
    public IReadOnlyList<long> ListIds { get; init; } = [];
    public string ListName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public bool UseConfigRoutes { get; init; }

    // All packets must reach the name/app decision before the system can bypass the tunnel.
    public bool FlowRules { get; init; }
    public bool HasFlowApps => TunnelApps.Count + DirectApps.Count + BlockApps.Count > 0;
    public bool RequestedAllUdp { get; init; }
    public bool SelectionPlan { get; init; }

    /// <summary>
    /// Whether every connection gets its own verdict; off leaves every destination to the route table.
    /// </summary>
    public bool UseRouter { get; init; } = true;

    /// <summary>
    /// Entries a list holds in the route table before the relay decides.
    /// </summary>
    public int SplitLimit { get; init; } = SplitBudget.DefaultLimit;

    /// <summary>
    /// Whether a part of the list is decided on the fly: the rules heavier than half the threshold and the ones past it.
    /// </summary>
    public bool Live { get; init; }

    /// <summary>
    /// The ranges the route table holds where a part of the list is decided on the fly.
    /// </summary>
    public IReadOnlyList<string> HeldRoutes { get; init; } = [];

    /// <summary>
    /// The names asked ahead and held in the route table where a part of the list is decided on the fly.
    /// </summary>
    public IReadOnlyList<GeoDomain> HeldDomains { get; init; } = [];

    /// <summary>
    /// What the held part of the list weighs against the threshold.
    /// </summary>
    public int HeldWeight { get; init; }

    /// <summary>
    /// What the part decided on the fly weighs.
    /// </summary>
    public int LiveWeight { get; init; }

    /// <summary>
    /// The ranges of these the route table holds: all of them while nothing is decided on the fly.
    /// </summary>
    public IReadOnlyList<string> Held(IReadOnlyList<string> ranges)
    {
        if (!Live)
        {
            return ranges;
        }

        var held = new HashSet<string>(HeldRoutes, StringComparer.OrdinalIgnoreCase);
        return [.. ranges.Where(held.Contains)];
    }

    /// <summary>
    /// The names of these asked ahead and held in the route table: all of them while nothing is decided on the fly.
    /// </summary>
    public IReadOnlyList<GeoDomain> Held(IReadOnlyList<GeoDomain> names)
    {
        if (!Live)
        {
            return names;
        }

        var held = new HashSet<GeoDomain>(HeldDomains);
        return [.. names.Where(held.Contains)];
    }

    public static (GeoRoutingPlan Plan, string Stamp, string? AppMode, string[] Apps, string[] Bypass) FromPolicy(
        RoutingPolicy? policy, bool useRouter, bool ipv6, int api, IReadOnlyList<string> inbound,
        int ttl, int limit, Func<string, bool> installed)
    {
        var plan = Full with { UseRouter = useRouter, TtlSeconds = ttl, SplitLimit = limit, UseConfigRoutes = policy is null };
        string? mode = null;
        string[] apps = [];
        string[] bypass = [];
        if (policy is not null)
        {
            var list = policy.List;
            var full = policy.Settings?.UseGlobalProxy == true;
            var requestedAllUdp = policy.Settings?.AllUdp == true;
            var udp = !full && requestedAllUdp;
            var proxy = Packages(list.Apps).Where(installed).ToArray();
            var direct = Packages(list.DirectApps).Where(installed).ToArray();
            var block = Packages(policy.BlockApps).Where(installed).ToArray();
            var ranges = list.Routes.Concat(inbound).Distinct(StringComparer.Ordinal).ToArray();
            var hasNames = list.Domains.Count + list.DirectDomains.Count + list.BlockDomains.Count > 0;
            var hasAddresses = ranges.Length + list.DirectRoutes.Count + list.BlockRoutes.Count > 0;
            var hasProxyApps = proxy.Length > 0 && (!full || hasNames || list.DirectRoutes.Count > 0);
            var hasDirectApps = direct.Length > 0 && (full || hasProxyApps || hasAddresses || hasNames || udp);
            var flow = udp || block.Length > 0 || (hasProxyApps && (hasDirectApps || hasAddresses || hasNames))
                || (hasDirectApps && (hasAddresses || hasNames));

            if (flow && (!useRouter || !RouteBudget.SupportsOwner(api)))
            {
                throw new NotSupportedException("Android routing requires the VPN router and Android 10 or later for these application or UDP rules.");
            }

            if (ipv6 && (flow || hasNames || ranges.Concat(list.DirectRoutes).Concat(list.BlockRoutes)
                    .Any(range => range.Contains(':', StringComparison.Ordinal))))
            {
                throw new NotSupportedException("Android cannot apply these routing rules with IPv6 enabled. Disable IPv6 for this profile.");
            }

            if (!flow)
            {
                if (!full && hasProxyApps)
                {
                    mode = "include";
                    apps = proxy;
                }
                else if (full)
                {
                    bypass = direct;
                }
            }

            var rules = policy.RuleOrigins.Select(origin => origin.Unfolded).Distinct().ToArray();
            var share = SplitBudget.Share(rules.Select(rule => rule.Weight).ToArray(), limit);
            var live = share.AnyLive && useRouter && RouteBudget.SupportsOwner(api);
            var held = rules.Where((_, index) => share.Held[index]).ToArray();
            plan = new GeoRoutingPlan(ranges, list.DirectRoutes, list.BlockRoutes, list.Domains, list.DirectDomains,
                list.BlockDomains, full || mode == "include", udp, ttl)
            {
                SelectionPlan = true,
                FlowRules = flow,
                RequestedAllUdp = requestedAllUdp,
                TunnelApps = flow && hasProxyApps ? proxy : [],
                DirectApps = flow ? direct : [],
                BlockApps = flow ? block : [],
                ListIds = policy.Sources.Select(source => source.Id).ToArray(),
                ListName = list.Name,
                UseRouter = useRouter,
                SplitLimit = limit,
                Live = live,
                HeldRoutes = live ? [.. held.SelectMany(rule => rule.Routes), .. inbound] : [],
                HeldDomains = live ? [.. held.SelectMany(rule => rule.Domains).Select(NameRules.Key)] : [],
                HeldWeight = share.HeldWeight,
                LiveWeight = share.LiveWeight,
            };
        }

        if (plan.SelectionPlan)
        {
            var routes = plan.FlowRules ? 1
                : SystemRoutes.Tunneled(plan.FullTunnel, plan.Held(plan.ProxyRoutes), plan.Held(plan.DirectRoutes), plan.BlockRoutes).Count;
            var names = plan.FlowRules ? 0
                : plan.Held(plan.ProxyDomains).Count + plan.Held(plan.DirectDomains).Count + plan.Held(plan.BlockDomains).Count;
            if (!RouteBudget.Fits(routes, names, apps.Concat(bypass)))
            {
                throw new NotSupportedException("The selected Android routing lists exceed the VPN Binder budget.");
            }
        }

        var inputs = JsonSerializer.SerializeToUtf8Bytes(new { Policy = policy?.Stamp, plan, mode, apps, bypass, ipv6, api });
        var version = Convert.ToHexString(SHA256.HashData(inputs));
        return (plan with { Version = version }, version, mode, apps, bypass);
    }

    public static RouteVerdict DecideFlow(RouteVerdict address, RouteVerdict domain, RouteVerdict hint,
        RouteVerdict app, bool knownOwner, RouteVerdict fallback)
    {
        var decision = RouteDecision.Resolve(address, domain, hint, app);
        if (!knownOwner && decision.Source is RouteDecisionSource.Default or RouteDecisionSource.Address
            && decision.Verdict != RouteVerdict.Block)
        {
            return RouteVerdict.Block;
        }

        return decision.Verdict == RouteVerdict.None ? fallback : decision.Verdict;
    }

    public RouteVerdict AppVerdict(string package) =>
        BlockApps.Contains(package, StringComparer.Ordinal) ? RouteVerdict.Block
        : DirectApps.Contains(package, StringComparer.Ordinal) ? RouteVerdict.Direct
        : TunnelApps.Contains(package, StringComparer.Ordinal) ? RouteVerdict.Proxy : RouteVerdict.None;

    public IReadOnlyList<string> RoutesForFamily(IReadOnlyList<string> routes, bool ipv6)
    {
        if (!ipv6)
        {
            return routes.Where(route => !route.Contains(':', StringComparison.Ordinal)).ToArray();
        }

        return !UseConfigRoutes && (!SelectionPlan || FullTunnel) ? [.. routes, "::/0"] : routes;
    }

    public (IReadOnlyList<GeoDomain> Proxy, IReadOnlyList<GeoDomain> Direct, IReadOnlyList<GeoDomain> Block) DnsNames()
    {
        var proxy = Held(ProxyDomains);
        var direct = Held(DirectDomains);
        var block = Held(BlockDomains);
        if (!FlowRules)
        {
            return (proxy, direct, block);
        }

        var rules = new NameRules(ProxyDomains, DirectDomains, BlockDomains);
        var names = proxy.Concat(direct).Concat(block)
            .Where(name => name.Kind is GeoDomainKind.Full or GeoDomainKind.Domain)
            .Select(name => NameRules.Key(name).Value)
            .Distinct(StringComparer.Ordinal)
            .ToLookup(rules.Verdict, name => new GeoDomain(GeoDomainKind.Full, name));
        return (names[RouteVerdict.Proxy].ToArray(), names[RouteVerdict.Direct].ToArray(), names[RouteVerdict.Block].ToArray());
    }

    public static IEnumerable<string> Packages(IEnumerable<string> rules) => rules
        .Where(rule => rule.StartsWith("pkg=", StringComparison.OrdinalIgnoreCase))
        .Select(rule => rule[4..].Trim()).Where(package => package.Length > 0)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    /// <summary>
    /// Everything through the tunnel, nothing listed.
    /// </summary>
    public static GeoRoutingPlan Full { get; } = new([], [], [], [], [], [], true, false);

    /// <summary>
    /// Whether any name has to be matched against resolved answers.
    /// </summary>
    public bool HasDomains => ProxyDomains.Count > 0 || DirectDomains.Count > 0 || BlockDomains.Count > 0;

    /// <summary>
    /// Whether any rule can change a destination's route.
    /// </summary>
    public bool HasRules => ProxyRoutes.Count > 0 || DirectRoutes.Count > 0 || BlockRoutes.Count > 0 || HasDomains;
}
