using System.Security.Cryptography;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>Builds a startup projection from the selected policy and the current tunnel assignments.</summary>
internal static class RoutingProjection
{
    public static async Task SaveAsync(IStateStore store, string config, TunnelRoutingProjection projection,
        ILogger logger, CancellationToken ct)
    {
        var list = projection.List;
        await store.SaveTunnelProjectionAsync(config, projection.Split, list.Routes, list.Domains, list.Apps,
            list.DirectRoutes, list.DirectDomains, list.BlockRoutes, list.BlockDomains,
            projection.ListIds.Count == 1 ? projection.ListIds[0] : null, ct, projection).ConfigureAwait(false);
        logger.LogInformation("{Config}: startup will use routing version {Version} from {Count} list(s)",
            config, projection.Stamp[..12], projection.ListIds.Count);
    }

    public static async Task<bool> RefreshAsync(IStateStore store, string config,
        Func<string, CancellationToken, Task<TunnelRoutingProjection?>> prepare, ILogger logger, CancellationToken ct)
    {
        var projection = await prepare(config, ct).ConfigureAwait(false);
        if (projection is null)
        {
            return false;
        }

        await SaveAsync(store, config, projection, logger, ct).ConfigureAwait(false);
        return true;
    }

    public static async Task<TunnelRoutingProjection> BuildAsync(IStateStore store, GeoConfigurator geo,
        TunnelDutyRoster roster, string config, ILogger logger, CancellationToken ct)
    {
        var takesRouting = await ConfigRouting.AllowedAsync(store, config, ct).ConfigureAwait(false);
        var ids = takesRouting ? await store.GetSelectedRoutingListsAsync(ct).ConfigureAwait(false) : null;
        logger.LogDebug("{Config}: preparing the routing projection for {Count} selected list(s)", config, ids?.Count ?? 0);
        var policy = await geo.LoadPolicyAsync(ids, AppRulePlatform.Windows, logger, ct).ConfigureAwait(false);
        if (policy is null)
        {
            var own = takesRouting ? await store.GetTunnelGeoAsync(config, ct).ConfigureAwait(false) : null;
            var rules = own?.Rules ?? [];
            var list = new RoutingList(0, string.Empty, rules, own?.Routes ?? [], own?.Domains ?? [], own?.Apps ?? [],
                [], [], [], [], []);
            var split = own?.GeoSplit ?? false;
            var input = Digest(new { takesRouting, split, list });
            return Create(input, [], null, split, list, GeoMaterializer.NamedRanges(rules, RouteRole.Proxy, list.Routes), [], []);
        }

        var assigned = await roster.AssignAsync(store, config, policy.RuleOrigins, ct).ConfigureAwait(false);
        var all = assigned.Select(item => item.Rule).ToArray();
        var whole = policy.ComposeShare(all, out var blockedApps);
        ValidateDirectApps(config, whole, policy.Settings?.UseGlobalProxy == true, policy.Settings?.AllUdp == true, logger);
        if (blockedApps.Any(app => AppRuleCover.Parse(app).Kind is "name" or "svc"))
        {
            throw new NotSupportedException("Windows application block rules require an executable path, directory or package.");
        }

        var mine = assigned.Where(item => item.Rule.Rule.Role != RouteRole.Proxy || item.Target == config)
            .Select(item => item.Rule).ToArray();
        var local = mine.Length == all.Length ? whole : policy.ComposeShare(mine, out _);

        // Equal proxy prefixes reached by several rules go to the first assignment (highest rank, then list ID).
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var domainOwners = new Dictionary<GeoDomain, string>();
        foreach (var item in assigned)
        {
            if (item.Rule.Rule.Role != RouteRole.Proxy || item.Target is not { } owner)
            {
                continue;
            }

            foreach (var route in item.Rule.Routes)
            {
                owners.TryAdd(RangeClaims.Key(route), owner);
            }

            foreach (var domain in item.Rule.Domains)
            {
                domainOwners.TryAdd(NameRules.Key(domain), owner);
            }
        }

        var localProxy = owners.Where(pair => pair.Value == config).Select(pair => pair.Key).ToArray();
        var remoteProxy = owners.Where(pair => pair.Value != config).Select(pair => pair.Key).ToArray();
        var direct = all.Where(rule => rule.Rule.Role == RouteRole.Direct).SelectMany(rule => rule.Routes).Distinct().ToArray();
        var blocked = all.Where(rule => rule.Rule.Role == RouteRole.Block).SelectMany(rule => rule.Routes).Distinct().ToArray();

        // Use the original widths on both sides. Carving already-settled fragments again could change their rank.
        local = local with
        {
            Routes = RangeClaims.Settle(localProxy, [.. direct, .. remoteProxy], blocked).Proxy.Distinct().ToArray(),
            Domains = whole.Domains.Where(domain => domainOwners.TryGetValue(domain, out var owner) && owner == config).ToArray(),
            DirectRoutes = whole.DirectRoutes,
            DirectDomains = whole.DirectDomains,
            DirectApps = whole.DirectApps,
            BlockRoutes = whole.BlockRoutes,
            BlockDomains = whole.BlockDomains,
        };
        var standing = RangeClaims.Intersect(GeoMaterializer.NamedRanges(local.Rules, RouteRole.Proxy), local.Routes);
        var offTunnel = RangeClaims.Intersect(GeoMaterializer.NamedRanges(whole.Rules, RouteRole.Direct), whole.DirectRoutes)
            .Concat(RangeClaims.Intersect(GeoMaterializer.NamedRanges(whole.Rules, RouteRole.Block), whole.BlockRoutes)).ToArray();
        return Create(policy.Stamp, [.. policy.Sources.Select(source => source.Id)], policy.Settings,
            policy.Settings?.UseGlobalProxy != true, local, standing, offTunnel, blockedApps);
    }

    internal static void ValidateDirectApps(string config, RoutingList list, bool full, bool allUdp, ILogger logger)
    {
        if (list.DirectApps.Count == 0 || (!full && !allUdp &&
            list.Domains.Count == 0 && list.Routes.Count == 0 && list.Apps.Count == 0))
        {
            return;
        }

        // shortcut: Windows routes are shared by destination, allow mixed policies when per-flow routing is available.
        const string reason = "Windows cannot safely combine direct application rules with VPN routing: applications can share a destination IP.";
        logger.LogError("[FIX] {Config}: {Reason}", config, reason);
        throw new NotSupportedException(reason);
    }

    private static TunnelRoutingProjection Create(string input, IReadOnlyList<long> ids, RoutingSettings? settings,
        bool split, RoutingList list, IReadOnlyList<string> standing, IReadOnlyList<string> offTunnel, IReadOnlyList<string> blockApps)
    {
        var stamp = Digest(new { input, list, standing, offTunnel, blockApps });
        return new TunnelRoutingProjection(input, stamp, ids, settings, split, list, standing, offTunnel, blockApps);
    }

    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
