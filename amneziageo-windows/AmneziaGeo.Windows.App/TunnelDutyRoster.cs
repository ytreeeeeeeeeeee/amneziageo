using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc.Fleet;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Answers what each tunnel is on the hook for. One tunnel on a machine holds every duty; a set of them hands
/// the duties out itself.
/// </summary>
internal class TunnelDutyRoster
{
    /// <summary>
    /// The duties of the named tunnel.
    /// </summary>
    public virtual TunnelDuties For(string name)
    {
        return TunnelDuties.Sole;
    }

    /// <summary>
    /// Takes the named tunnel down when its server asks for it; false where the tunnel is the only one and its own
    /// supervisor takes it down.
    /// </summary>
    public virtual bool ServerAskedDown(string name)
    {
        return false;
    }

    /// <summary>
    /// The tunnels a sweep must leave standing alongside the named one.
    /// </summary>
    public virtual IReadOnlyCollection<string> Standing(string name)
    {
        return [name];
    }

    /// <summary>
    /// The rules of a list the named tunnel carries. The only tunnel on a machine carries every one of them bar
    /// the ones addressed to another configuration, which go where their fallback sends them.
    /// </summary>
    public virtual async Task<IReadOnlyList<GeoRule>> ShareAsync(IStateStore store, string name, long listId, IReadOnlyList<GeoRule> rules, CancellationToken ct)
    {
        var targets = FleetTargets.Parse(await store.GetSettingAsync(FleetKeys.Targets, ct).ConfigureAwait(false));
        return Alone(name, listId, rules, targets);
    }

    /// <summary>Resolves the original assignment of every rule before taking a tunnel's share.</summary>
    public virtual async Task<IReadOnlyList<(RuleUnfold Rule, string? Target)>> AssignAsync(
        IStateStore store, string name, IReadOnlyList<RoutingRuleOrigin> origins, CancellationToken ct)
    {
        var targets = FleetTargets.Parse(await store.GetSettingAsync(FleetKeys.Targets, ct).ConfigureAwait(false));
        return Assign(origins, key => targets.TryGetValue(key, out var route) ? route : RuleRoute.Default,
            target => Resolve(target, name), name);
    }

    protected static IReadOnlyList<(RuleUnfold Rule, string? Target)> Assign(
        IReadOnlyList<RoutingRuleOrigin> origins, Func<string, RuleRoute> targetOf, Func<RuleTarget, string?> resolve,
        string? automatic)
    {
        var assigned = new List<(RuleUnfold Rule, string? Target)>();
        assigned.AddRange(origins.Where(origin => origin.Unfolded.Rule.Role != RouteRole.Proxy)
            .Select(origin => (origin.Unfolded, (string?)null)));

        // Group the effective rule, but read the target by the exact original key saved by the user.
        var candidates = origins.Where(origin => origin.Unfolded.Rule.Role == RouteRole.Proxy).Select(origin =>
        {
            var route = targetOf(FleetTargets.Key(origin.ListId, GeoConfigurator.Format(origin.Original)));
            var target = route.Target.IsAuto ? null : resolve(route.Target);
            var fallback = route.Fallback.IsAuto ? null : resolve(route.Fallback);
            return (Origin: origin, Target: target ?? fallback ?? automatic, Rank: target is not null ? 2 : fallback is not null ? 1 : 0);
        });
        foreach (var group in candidates.OrderByDescending(item => item.Rank).ThenBy(item => item.Origin.ListId)
            .GroupBy(item => item.Origin.WindowsKey()))
        {
            var winner = group.First();
            var rule = winner.Origin.Unfolded;
            switch (winner.Target)
            {
                case RuleTarget.Block:
                    assigned.Add((rule with { Rule = rule.Rule with { Role = RouteRole.Block } }, null));
                    break;
                case RuleTarget.Direct:
                    assigned.Add((rule with { Rule = rule.Rule with { Role = RouteRole.Direct } }, null));
                    break;
                case not null:
                    assigned.Add((rule, winner.Target));
                    break;
            }
        }

        return assigned;
    }

    /// <summary>
    /// The share of the only tunnel on a machine: a rule addressed to another configuration takes its fallback, and
    /// one whose both ends name other configurations leaves the tunnel.
    /// </summary>
    public static IReadOnlyList<GeoRule> Alone(string name, long listId, IReadOnlyList<GeoRule> rules, IReadOnlyDictionary<string, RuleRoute> targets)
    {
        if (targets.Count == 0)
        {
            return rules;
        }

        var share = new List<GeoRule>(rules.Count);
        var moved = false;
        foreach (var rule in rules)
        {
            if (rule.Role != RouteRole.Proxy
                || !targets.TryGetValue(FleetTargets.Key(listId, GeoConfigurator.Format(rule)), out var route))
            {
                share.Add(rule);
                continue;
            }

            var rides = Resolve(route.Target, name) ?? Resolve(route.Fallback, name);
            if (string.Equals(rides, name, StringComparison.Ordinal))
            {
                share.Add(rule);
                continue;
            }

            moved = true;
            if (rides == RuleTarget.Block)
            {
                share.Add(rule with { Role = RouteRole.Block });
            }
            else if (rides == RuleTarget.Direct)
            {
                share.Add(rule with { Role = RouteRole.Direct });
            }
        }

        return moved ? share : rules;
    }

    // One end of a rule on a machine running a single tunnel.
    private static string? Resolve(RuleTarget target, string name)
    {
        return target.Mode switch
        {
            RuleTarget.Block => RuleTarget.Block,
            RuleTarget.Direct => RuleTarget.Direct,
            RuleTarget.Server => string.Equals(target.Name, name, StringComparison.Ordinal) ? name : null,
            _ => name,
        };
    }
}
