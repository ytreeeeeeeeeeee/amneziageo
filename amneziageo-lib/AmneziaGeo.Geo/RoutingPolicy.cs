using System.Net;
using AmneziaGeo.Decl;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Geo;

/// <summary>One selected policy, compiled from the original rules of all participating lists.</summary>
public sealed class RoutingPolicy
{
    private readonly NameRules _names;
    private readonly Func<IPAddress, bool> _proxy, _direct, _block;
    private readonly AppRoutingRules.Identity[] _proxyApps, _directApps, _blockApps;
    private readonly AppRulePlatform _platform;

    internal RoutingPolicy(RoutingList list, RoutingSettings? settings, IReadOnlyList<RoutingListStamp> sources,
        IReadOnlyList<RoutingRuleOrigin> origins, IReadOnlyList<string> blockApps, string stamp, AppRulePlatform platform)
    {
        List = list;
        Settings = settings;
        Sources = sources;
        RuleOrigins = origins;
        BlockApps = blockApps;
        Stamp = stamp;
        _platform = platform;
        _names = NameRules.Of(list);
        _proxy = RangeClaims.Matcher(list.Routes);
        _direct = RangeClaims.Matcher(list.DirectRoutes);
        _block = RangeClaims.Matcher(list.BlockRoutes);
        _proxyApps = Identities(list.Apps);
        _directApps = Identities(list.DirectApps);
        _blockApps = Identities(blockApps);
    }

    public RoutingList List { get; }
    public RoutingSettings? Settings { get; }
    public IReadOnlyList<RoutingListStamp> Sources { get; }
    public IReadOnlyList<RoutingRuleOrigin> RuleOrigins { get; }
    public IReadOnlyList<string> BlockApps { get; }
    public string Stamp { get; }
    public int Weight => SplitBudget.Weight(List);

    /// <summary>Composes the assigned rules with the same precedence as the original policy.</summary>
    public RoutingList ComposeShare(IReadOnlyList<RuleUnfold> rules, out IReadOnlyList<string> blockApps) =>
        GeoConfigurator.ComposeList(List.Id, Sources, rules, _platform, out blockApps, null);

    /// <summary>Decides a connection, using DNS associations only when its own name is unknown.</summary>
    public RouteDecision Decide(IPAddress address, string? host, string? application, IEnumerable<string>? resolvedNames = null)
    {
        var range = _block(address) ? RouteVerdict.Block : _direct(address) ? RouteVerdict.Direct
            : _proxy(address) ? RouteVerdict.Proxy : RouteVerdict.None;
        var domain = RouteVerdict.None;
        var inferred = RouteVerdict.None;
        if (!string.IsNullOrWhiteSpace(host))
        {
            domain = _names.Verdict(host);
        }
        else if (resolvedNames is not null)
        {
            inferred = RouteDecision.Infer(resolvedNames.Select(_names.Verdict), out _);
        }

        return RouteDecision.Resolve(range, domain, inferred, AppVerdict(application));
    }

    private RouteVerdict AppVerdict(string? target)
    {
        if (string.IsNullOrEmpty(target) || AppRoutingRules.Normalize(target, _platform) is not { } app)
        {
            return RouteVerdict.None;
        }

        return _blockApps.Any(rule => rule.Covers(app)) ? RouteVerdict.Block
            : _directApps.Any(rule => rule.Covers(app)) ? RouteVerdict.Direct
            : _proxyApps.Any(rule => rule.Covers(app)) ? RouteVerdict.Proxy : RouteVerdict.None;
    }

    private AppRoutingRules.Identity[] Identities(IReadOnlyList<string> values) =>
        [.. values.Select(value => AppRoutingRules.Normalize(value, _platform)).OfType<AppRoutingRules.Identity>()];
}

/// <summary>The originating list and exact source rule, with its expansion in this policy.</summary>
public sealed record RoutingRuleOrigin(long ListId, GeoRule Original, RuleUnfold Unfolded)
{
    /// <summary>The normalized identity of a Windows rule, independent of its stored assignment key.</summary>
    public (GeoRuleKind Kind, string Value) WindowsKey()
    {
        var rule = Unfolded.Rule;
        var value = rule.Kind switch
        {
            GeoRuleKind.App when AppRoutingRules.Normalize(rule.Value, AppRulePlatform.Windows) is { } app => app.Kind + "=" + app.Value,
            GeoRuleKind.Cidr => RangeClaims.Key(rule.Value),
            GeoRuleKind.Domain => NameRules.Normalize(rule.Value),
            _ => rule.Value.Trim().ToLowerInvariant(),
        };
        return (rule.Kind, value);
    }
}
