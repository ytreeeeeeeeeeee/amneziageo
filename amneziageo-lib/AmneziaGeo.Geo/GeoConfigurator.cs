using System.Net;
using AmneziaGeo.Decl;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Geo;

/// <summary>
/// Applies and enumerates per-config geo split-tunnel settings, shared by the agent command channel.
/// </summary>
public sealed class GeoConfigurator(IStateStore store, IGeoFileStore files)
{
    /// <summary>
    /// Materializes the rule tokens and persists the geo settings for a config. Counts the tokens it cannot parse.
    /// </summary>
    public async Task<(int Rules, int Routes, int Domains, int Skipped)> ApplyAsync(string name, bool on, IReadOnlyList<string> ruleTokens, CancellationToken ct = default)
    {
        var rules = new List<GeoRule>();
        var skipped = 0;
        foreach (var token in ruleTokens)
        {
            var rule = ParseRule(token);
            if (rule is null)
            {
                skipped++;
                continue;
            }

            rules.Add(rule);
        }

        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct).ConfigureAwait(false), files);
        var (routes, domains, apps) = GeoMaterializer.Materialize(rules, index);
        await store.SaveTunnelGeoAsync(new TunnelGeo(name, on, rules, routes, domains, apps), ct).ConfigureAwait(false);
        return (rules.Count, routes.Count, domains.Count, skipped);
    }

    /// <summary>
    /// Materializes the role-tagged rule tokens and saves them as a shared routing list. When listId is 0
    /// a new list is created; otherwise the list with that id is replaced. Returns the row id.
    /// </summary>
    public Task<long> ApplyToRoutingListAsync(long listId, string name, IReadOnlyList<string> ruleTokens, CancellationToken ct = default)
    {
        return ApplyToRoutingListAsync(listId, name, ruleTokens, string.Empty, ct);
    }

    /// <summary>
    /// Saves the rule tokens as a routing list; a new list takes the lasting id given, an empty one makes its own.
    /// </summary>
    public async Task<long> ApplyToRoutingListAsync(long listId, string name, IReadOnlyList<string> ruleTokens, string uid, CancellationToken ct = default)
    {
        var rules = new List<GeoRule>();
        foreach (var token in ruleTokens)
        {
            var rule = ParseRoleRule(token);
            if (rule is not null)
            {
                rules.Add(rule);
            }
        }

        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct).ConfigureAwait(false), files);
        return await store.SaveRoutingListAsync(MaterializeRoutingList(listId, name, rules, index) with { Uid = uid }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Expands the role-tagged rule tokens without storing anything, so a draft can be measured before it is saved.
    /// </summary>
    public async Task<RoutingList> MaterializeDraftAsync(IReadOnlyList<string> ruleTokens, CancellationToken ct = default)
    {
        var rules = new List<GeoRule>();
        foreach (var token in ruleTokens)
        {
            var rule = ParseRoleRule(token);
            if (rule is not null)
            {
                rules.Add(rule);
            }
        }

        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct).ConfigureAwait(false), files);
        return MaterializeRoutingList(0, string.Empty, rules, index);
    }

    /// <summary>
    /// Unfolds each rule apart, in the order given, so the share each one brings to the route table is known.
    /// </summary>
    public async Task<IReadOnlyList<RuleUnfold>> UnfoldEachAsync(IReadOnlyList<GeoRule> rules, CancellationToken ct = default)
    {
        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct).ConfigureAwait(false), files);
        var unfolded = new List<RuleUnfold>(rules.Count);
        foreach (var rule in rules)
        {
            var (routes, domains, _) = GeoMaterializer.Materialize([rule], index);
            unfolded.Add(new RuleUnfold(rule, routes, domains));
        }

        return unfolded;
    }

    /// <summary>
    /// Unfolds each role-tagged rule token apart, so a draft is weighed before it is saved; a token that reads as no
    /// rule is left out.
    /// </summary>
    public Task<IReadOnlyList<RuleUnfold>> UnfoldTokensAsync(IReadOnlyList<string> ruleTokens, CancellationToken ct = default) =>
        UnfoldEachAsync([.. ruleTokens.Select(ParseRoleRule).OfType<GeoRule>()], ct);

    /// <summary>Builds a policy from the stored selection; an empty choice leaves the config's own routes.</summary>
    public async Task<RoutingPolicy?> LoadSelectedPolicyAsync(ILogger? log = null, CancellationToken ct = default) =>
        await LoadPolicyAsync(await store.GetSelectedRoutingListsAsync(ct).ConfigureAwait(false), log: log, ct: ct)
            .ConfigureAwait(false);

    /// <summary>Compiles original rules, keeping their list IDs even where the effective rules overlap.</summary>
    public async Task<RoutingPolicy?> LoadPolicyAsync(IReadOnlyList<long>? ids, AppRulePlatform? appPlatform = null,
        ILogger? log = null, CancellationToken ct = default)
    {
        if (ids is not { Count: > 0 })
        {
            return null;
        }

        var sources = new List<RoutingListStamp>();
        var settings = new List<RoutingSettings>();
        foreach (var id in ids.Distinct().Order())
        {
            if (await store.GetRoutingListStampAsync(id, ct).ConfigureAwait(false) is not { } source)
            {
                continue;
            }

            sources.Add(source);
            if (await store.GetRoutingSettingsAsync(id, ct).ConfigureAwait(false) is { } setting)
            {
                settings.Add(setting);
            }
        }

        if (sources.Count == 0)
        {
            return null;
        }

        var platform = appPlatform ?? AppRoutingRules.CurrentPlatform;
        var finds = await AppFinds.ReadAsync(store, ct).ConfigureAwait(false);
        var geoSources = await store.ListGeoSourcesAsync(ct).ConfigureAwait(false);
        var geoFiles = await store.ListGeoFilesAsync(ct).ConfigureAwait(false);
        var index = GeoIndex.Load(geoSources, files, geoFiles.Select(file => file.Name).ToHashSet(StringComparer.Ordinal));
        var origins = new List<RoutingRuleOrigin>();
        var unfolded = new Dictionary<GeoRule, RuleUnfold>();
        foreach (var source in sources)
        {
            foreach (var (original, effective) in AppFinds.ResolveRules(source.Rules, finds))
            {
                AddOrigin(source.Id, original, effective);
            }
        }

        foreach (var setting in settings)
        {
            foreach (var token in RoutingSettings.ExclusionTokens(setting.Exclusions))
            {
                var slash = token.IndexOf('/');
                var address = slash < 0 ? token : token[..slash];
                var rule = new GeoRule(IPAddress.TryParse(address, out _) ? GeoRuleKind.Cidr : GeoRuleKind.Domain,
                    token, RouteRole.Direct);
                AddOrigin(setting.ListId, rule, rule);
            }
        }

        var idOfPolicy = sources.Count == 1 ? sources[0].Id : 0;
        var list = ComposeList(idOfPolicy, sources, unfolded.Values.ToArray(), platform, out var blockApps, log);
        var combined = RoutingSettings.Combine(idOfPolicy, settings);
        var stamp = RoutingPlanStamp.ForPolicy(list, combined, sources, origins, platform, geoSources, geoFiles);
        return new RoutingPolicy(list, combined, sources, origins, blockApps, stamp, platform);

        void AddOrigin(long id, GeoRule original, GeoRule effective)
        {
            if (effective.Kind == GeoRuleKind.App && AppRoutingRules.Normalize(effective.Value, platform) is null)
            {
                return;
            }

            if (!unfolded.TryGetValue(effective, out var expansion))
            {
                var (routes, domains, _) = GeoMaterializer.Materialize([effective], index);
                expansion = new RuleUnfold(effective, routes, domains);
                unfolded.Add(effective, expansion);
            }

            origins.Add(new RoutingRuleOrigin(id, original, expansion));
        }
    }

    private static RoutingList ComposeList(long id, IReadOnlyList<RoutingListStamp> sources, IReadOnlyList<RuleUnfold> rules,
        AppRulePlatform platform, out IReadOnlyList<string> blockApps, ILogger? log)
    {
        var proxy = BucketRanges(RouteRole.Proxy);
        var direct = BucketRanges(RouteRole.Direct);
        var block = BucketRanges(RouteRole.Block);
        var settled = RangeClaims.Settle(proxy, direct, block);
        var ranges = (Proxy: settled.Proxy.Distinct(StringComparer.Ordinal).ToArray(),
            Direct: settled.Direct.Distinct(StringComparer.Ordinal).ToArray(),
            Block: settled.Block.Distinct(StringComparer.Ordinal).ToArray());
        var nameClaims = rules.SelectMany(rule => rule.Domains.Select(domain => (Domain: NameRules.Key(domain), rule.Rule.Role)))
            .Distinct().ToArray();
        var domains = nameClaims.GroupBy(claim => claim.Domain).Select(group => group.MaxBy(claim => claim.Role)).ToArray();

        var appClaims = rules.Where(rule => rule.Rule.Kind == GeoRuleKind.App)
            .Select(rule => (Rule: rule.Rule, Key: AppRoutingRules.Normalize(rule.Rule.Value, platform)!.Value))
            .DistinctBy(claim => (claim.Key, claim.Rule.Role)).ToArray();
        var apps = appClaims.GroupBy(claim => claim.Key).Select(group => group.MaxBy(claim => claim.Rule.Role)).ToArray();
        var blocked = apps.Where(claim => claim.Rule.Role == RouteRole.Block).ToArray();
        var directApps = apps.Where(claim => claim.Rule.Role == RouteRole.Direct
            && !blocked.Any(higher => higher.Key.Covers(claim.Key))).ToArray();
        var tunneled = apps.Where(claim => claim.Rule.Role == RouteRole.Proxy
            && !directApps.Any(higher => higher.Key.Covers(claim.Key))
            && !blocked.Any(higher => higher.Key.Covers(claim.Key))).ToArray();
        var proxyApps = tunneled.Select(claim => claim.Rule.Value).ToArray();
        blockApps = blocked.Select(claim => claim.Rule.Value).ToArray();

        log?.LogDebug("Compiled routing policy from {Lists} lists and {Rules} rules using current geodata and applications. "
            + "Ranges {InputRanges} to {Ranges}; names {InputNames} to {Names}; app rules {InputApps} to {Apps}",
            sources.Count, rules.Count, proxy.Length + direct.Length + block.Length,
            ranges.Proxy.Length + ranges.Direct.Length + ranges.Block.Length, rules.Sum(rule => rule.Domains.Count),
            domains.Length, rules.Count(rule => rule.Rule.Kind == GeoRuleKind.App),
            proxyApps.Length + directApps.Length + blockApps.Count);

        if (log?.IsEnabled(LogLevel.Debug) == true)
        {
            log.LogDebug("Routing conflicts included {Ranges} range, {Names} name and {Apps} application overlaps",
                !proxy.SequenceEqual(settled.Proxy) || !direct.SequenceEqual(settled.Direct) || !block.SequenceEqual(settled.Block),
                domains.Length != nameClaims.Length, proxyApps.Length + directApps.Length + blockApps.Count != appClaims.Length);
        }

        var effective = rules.Where(rule => rule.Rule.Kind != GeoRuleKind.App).Select(rule => rule.Rule)
            .Concat(tunneled.Concat(directApps).Concat(blocked).Select(claim => claim.Rule)).ToArray();
        return new RoutingList(id, string.Join(", ", sources.Select(source => source.Name)), effective,
            ranges.Proxy, BucketDomains(RouteRole.Proxy), proxyApps, ranges.Direct, BucketDomains(RouteRole.Direct),
            directApps.Select(claim => claim.Rule.Value).ToArray(), ranges.Block, BucketDomains(RouteRole.Block));

        string[] BucketRanges(RouteRole role) => rules.Where(rule => rule.Rule.Role == role)
            .SelectMany(rule => rule.Routes).Select(RangeClaims.Key).Distinct(StringComparer.Ordinal).ToArray();

        GeoDomain[] BucketDomains(RouteRole role) =>
            domains.Where(claim => claim.Role == role).Select(claim => claim.Domain).ToArray();
    }

    // Bumped whenever a rule token starts covering something else, so stored lists are rebuilt against the new
    // expansion instead of keeping what an older version wrote.
    private const string MaterializerVersion = "4";

    private const string MaterializerVersionKey = "geo-materializer";

    /// <summary>
    /// Rebuilds every stored list when the expansion changed since they were materialized. Returns whether
    /// anything was rebuilt; a run with no readable geo database is skipped, or the lists would come back empty.
    /// </summary>
    public async Task<bool> RematerializeIfStaleAsync(CancellationToken ct = default)
    {
        var stored = await store.GetSettingAsync(MaterializerVersionKey, ct).ConfigureAwait(false);
        if (string.Equals(stored, MaterializerVersion, StringComparison.Ordinal))
        {
            return false;
        }

        var sources = await store.ListGeoSourcesAsync(ct).ConfigureAwait(false);
        if (!sources.Any(HasFile))
        {
            return false;
        }

        await RematerializeAllRoutingListsAsync(ct).ConfigureAwait(false);
        await store.SetSettingAsync(MaterializerVersionKey, MaterializerVersion, ct).ConfigureAwait(false);
        return true;
    }

    private bool HasFile(GeoSource source)
    {
        using var stream = files.OpenRead(source.Name);
        return stream is not null;
    }

    /// <summary>
    /// Re-materializes every stored routing list against the current geo sources. Called after
    /// geo sources are added, removed, or refreshed.
    /// </summary>
    public async Task RematerializeAllRoutingListsAsync(CancellationToken ct = default)
    {
        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct).ConfigureAwait(false), files);
        var lists = await store.ListRoutingListsAsync(ct).ConfigureAwait(false);
        foreach (var list in lists)
        {
            await store.SaveRoutingListAsync(MaterializeRoutingList(list.Id, list.Name, list.Rules, index), ct).ConfigureAwait(false);
        }
    }

    // Materializes a list's rules into the role buckets.
    private static RoutingList MaterializeRoutingList(long id, string name, IReadOnlyList<GeoRule> rules, GeoIndex index)
    {
        var proxy = GeoMaterializer.Materialize(rules.Where(r => r.Role == RouteRole.Proxy).ToList(), index);
        var direct = GeoMaterializer.Materialize(rules.Where(r => r.Role == RouteRole.Direct).ToList(), index);
        var block = GeoMaterializer.Materialize(rules.Where(r => r.Role == RouteRole.Block).ToList(), index);

        var (proxyRoutes, directRoutes, blockRoutes) = RangeClaims.Settle(proxy.Routes, direct.Routes, block.Routes);
        return new RoutingList(id, name, rules, proxyRoutes, proxy.Domains, proxy.Apps,
            directRoutes, direct.Domains, direct.Apps, blockRoutes, block.Domains);
    }

    /// <summary>
    /// Returns the available geo categories as prefixed rule tokens (geosite:* and geoip:*).
    /// </summary>
    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default)
    {
        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct).ConfigureAwait(false), files);
        var tokens = new List<string>();
        foreach (var category in index.Categories())
        {
            tokens.Add($"geosite:{category.ToLowerInvariant()}");
        }

        foreach (var country in index.Countries())
        {
            tokens.Add($"geoip:{country.ToLowerInvariant()}");
        }

        return tokens;
    }

    /// <summary>
    /// Returns every entry a geosite / geoip rule expands to. Any other rule kind carries its own value and has
    /// nothing to expand.
    /// </summary>
    public async Task<IReadOnlyList<string>> EntriesAsync(string token, CancellationToken ct = default)
    {
        var rule = ParseRule(token);
        if (rule is null || rule.Kind is not (GeoRuleKind.GeoSite or GeoRuleKind.GeoIp))
        {
            return [];
        }

        var index = GeoIndex.Load(await store.ListGeoSourcesAsync(ct).ConfigureAwait(false), files);
        var routes = new List<string>();
        var domains = new List<GeoDomain>();
        GeoMaterializer.Expand(StripPrefix(rule.Value), index, routes, domains);
        routes.AddRange(domains.Select(FormatDomain));
        return routes;
    }

    /// <summary>
    /// Renders a geosite entry in v2ray rule notation: a suffix match is bare, other kinds carry their prefix.
    /// </summary>
    public static string FormatDomain(GeoDomain domain) => domain.Kind switch
    {
        GeoDomainKind.Full => $"full:{domain.Value}",
        GeoDomainKind.Regex => $"regexp:{domain.Value}",
        GeoDomainKind.Plain => $"keyword:{domain.Value}",
        _ => domain.Value,
    };

    // Drops a repeated "geosite:" / "geoip:" prefix left inside a rule value.
    private static string StripPrefix(string value)
    {
        var colon = value.IndexOf(':');
        return colon >= 0 ? value[(colon + 1)..] : value;
    }

    /// <summary>
    /// Parses a rule token like "geosite:openai" or "domain:example.com" into a typed rule.
    /// </summary>
    public static GeoRule? ParseRule(string text)
    {
        var colon = text.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        var value = text[(colon + 1)..];
        var kind = text[..colon].ToLowerInvariant() switch
        {
            "geosite" => GeoRuleKind.GeoSite,
            "geoip" => GeoRuleKind.GeoIp,
            "domain" => GeoRuleKind.Domain,
            "cidr" => GeoRuleKind.Cidr,
            "app" => GeoRuleKind.App,
            _ => (GeoRuleKind?)null,
        };
        return kind is null ? null : new GeoRule(kind.Value, value);
    }

    /// <summary>
    /// Formats a typed rule back into its prefixed token form.
    /// </summary>
    public static string Format(GeoRule rule)
    {
        var prefix = rule.Kind switch
        {
            GeoRuleKind.GeoSite => "geosite",
            GeoRuleKind.GeoIp => "geoip",
            GeoRuleKind.Domain => "domain",
            GeoRuleKind.App => "app",
            _ => "cidr",
        };
        return $"{prefix}:{rule.Value}";
    }

    /// <summary>
    /// Parses a role-tagged token ("direct|geoip:ru", "block|domain:x"); a bare token defaults to the Proxy role.
    /// </summary>
    public static GeoRule? ParseRoleRule(string text)
    {
        var (role, token) = SplitRole(text);
        var rule = ParseRule(token);
        return rule is null ? null : rule with { Role = role };
    }

    /// <summary>
    /// Formats a typed rule with its role prefix ("proxy|geosite:openai").
    /// </summary>
    public static string FormatWithRole(GeoRule rule) => $"{RoleToken(rule.Role)}|{Format(rule)}";

    private static string RoleToken(RouteRole role) => role switch
    {
        RouteRole.Direct => "direct",
        RouteRole.Block => "block",
        _ => "proxy",
    };

    // Splits an optional "<role>|" prefix off a token; an unknown/absent prefix means the whole text is Proxy.
    private static (RouteRole Role, string Token) SplitRole(string text)
    {
        var bar = text.IndexOf('|');
        if (bar > 0)
        {
            var role = text[..bar].ToLowerInvariant() switch
            {
                "proxy" => RouteRole.Proxy,
                "direct" => RouteRole.Direct,
                "block" => RouteRole.Block,
                "exclude" => RouteRole.Direct,
                _ => (RouteRole?)null,
            };
            if (role is not null)
            {
                return (role.Value, text[(bar + 1)..]);
            }
        }

        return (RouteRole.Proxy, text);
    }
}
