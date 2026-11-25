using System.Net;
using AmneziaGeo.Geo;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

// Used under the DNS router's rule lock, so replies and reloads commit their routes in the same order.
internal sealed class LinuxNameRoutes
{
    private const int MaxAddresses = 8192;
    private const int MaxNamesPerAddress = 64;
    private readonly Dictionary<IPAddress, Dictionary<string, long>> _names = [];
    private readonly Dictionary<IPAddress, RouteVerdict> _installed = [];

    public bool Observe(IPAddress address, string name, uint ttl, long now)
    {
        if (!_names.TryGetValue(address, out var names))
        {
            if (_names.Count >= MaxAddresses)
            {
                return false;
            }

            _names.Add(address, names = new(StringComparer.OrdinalIgnoreCase));
        }

        if (names.Count >= MaxNamesPerAddress && !names.ContainsKey(name))
        {
            return false;
        }

        names[name] = now + Math.Max(1L, ttl) * 1000;
        return true;
    }

    public bool Expire(long now)
    {
        var changed = false;
        foreach (var (address, names) in _names)
        {
            foreach (var name in names.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            {
                names.Remove(name);
                changed = true;
            }

            if (names.Count == 0)
            {
                _names.Remove(address);
            }
        }

        return changed;
    }

    public RouteVerdict VerdictOf(IPAddress address, NameRules rules) => _names.TryGetValue(address, out var names)
        ? RouteDecision.Infer(names.Keys.Select(rules.Verdict), out _) : RouteVerdict.None;

    public bool Apply(NameRules rules, Func<IReadOnlyList<string>, IReadOnlyList<string>, IReadOnlyList<string>, bool> replace,
        Func<IPAddress, bool> pinned)
    {
        var next = new Dictionary<IPAddress, RouteVerdict>();
        foreach (var address in _names.Keys)
        {
            var verdict = VerdictOf(address, rules);
            if (verdict != RouteVerdict.None && !pinned(address))
            {
                next.Add(address, verdict);
            }
        }

        if (_installed.Count == next.Count && next.All(pair => _installed.GetValueOrDefault(pair.Key) == pair.Value))
        {
            return true;
        }

        if (!replace(Of(RouteVerdict.Proxy), Of(RouteVerdict.Direct), Of(RouteVerdict.Block)))
        {
            return false;
        }

        _installed.Clear();
        foreach (var (address, verdict) in next)
        {
            _installed.Add(address, verdict);
        }

        return true;

        string[] Of(RouteVerdict verdict) => [.. next.Where(pair => pair.Value == verdict).Select(pair => pair.Key.ToString())];
    }
}
