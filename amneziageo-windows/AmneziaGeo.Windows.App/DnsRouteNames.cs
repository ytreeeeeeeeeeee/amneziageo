using System.Net;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Windows.App;

/// <summary>Short-lived evidence from DNS answers, grouped by address for routes without a connection name.</summary>
internal sealed class DnsRouteNames
{
    private const int MaxAddresses = 4096;
    private const int MaxNamesPerAddress = 64;
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, Dictionary<string, (RouteVerdict Verdict, long Until)>> _answers = [];

    public IReadOnlyList<RouteVerdict> Observe(IPAddress address, string name, RouteVerdict verdict, long now, int ttlSeconds)
    {
        lock (_gate)
        {
            if (!_answers.TryGetValue(address, out var names))
            {
                if (_answers.Count >= MaxAddresses)
                {
                    foreach (var old in _answers.Where(pair => pair.Value.Values.All(answer => answer.Until <= now)).Select(pair => pair.Key).ToArray())
                    {
                        _answers.Remove(old);
                    }

                    if (_answers.Count >= MaxAddresses)
                    {
                        return [];
                    }
                }

                names = new Dictionary<string, (RouteVerdict, long)>(StringComparer.OrdinalIgnoreCase);
                _answers.Add(address, names);
            }

            foreach (var old in names.Where(pair => pair.Value.Until <= now).Select(pair => pair.Key).ToArray())
            {
                names.Remove(old);
            }

            if (names.Count >= MaxNamesPerAddress && !names.ContainsKey(name))
            {
                return [RouteVerdict.None, RouteVerdict.Block];
            }

            names[name] = (verdict, now + Math.Max(1, ttlSeconds) * 1000L);
            return names.Values.Select(answer => answer.Verdict).Distinct().ToArray();
        }
    }
}
