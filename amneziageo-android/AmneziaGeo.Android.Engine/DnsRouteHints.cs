using System.Net;
using AmneziaGeo.Geo;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Android.Engine;

/// <summary>DNS evidence shared by the TCP relay and the UDP packet router for one VPN session.</summary>
internal sealed class DnsRouteHints(NameRules names, int ttlSeconds, Func<long>? clock = null)
{
    private const int MaxAddresses = 8192;
    private readonly object _gate = new();
    private readonly Dictionary<uint, long[]> _observed = [];
    private Dictionary<uint, RouteVerdict> _initial = [];
    private long _initialDeadline;

    public void SetInitial(NameAnswers answers)
    {
        var initial = new Dictionary<uint, RouteVerdict>();
        Add(answers.Proxy, RouteVerdict.Proxy);
        Add(answers.Direct, RouteVerdict.Direct);
        Add(answers.Block, RouteVerdict.Block);
        lock (_gate)
        {
            _initial = initial;
            _initialDeadline = Now() + 1000L * Math.Max(ttlSeconds, 1);
        }

        void Add(IEnumerable<string> ranges, RouteVerdict verdict)
        {
            foreach (var range in ranges)
            {
                if (IPNetwork.TryParse(range, out var network) && network.PrefixLength == 32
                    && GeoIpRanges.TryToNumeric(network.BaseAddress, out var address))
                {
                    if (!initial.TryAdd(address, verdict) && initial[address] != verdict)
                    {
                        initial[address] = RouteVerdict.None;
                    }
                }
            }
        }
    }

    public void Observe(uint address, string name, uint ttl)
    {
        var verdict = names.Verdict(name);
        var until = Now() + 1000L * Math.Clamp(ttl, 1U, (uint)Math.Max(ttlSeconds, 1));
        lock (_gate)
        {
            if (!_observed.TryGetValue(address, out var deadlines))
            {
                if (_observed.Count == MaxAddresses)
                {
                    _observed.Remove(_observed.Keys.First());
                }

                _observed[address] = deadlines = new long[(int)RouteVerdict.Block + 1];
            }

            deadlines[(int)verdict] = Math.Max(deadlines[(int)verdict], until);
        }
    }

    public RouteVerdict Lookup(uint address)
    {
        var now = Now();
        lock (_gate)
        {
            var result = RouteVerdict.None;
            var known = now < _initialDeadline && _initial.TryGetValue(address, out result);
            if (_observed.TryGetValue(address, out var deadlines))
            {
                for (var i = 0; i < deadlines.Length; ++i)
                {
                    if (deadlines[i] <= now)
                    {
                        continue;
                    }

                    var verdict = (RouteVerdict)i;
                    if (known && verdict != result)
                    {
                        return RouteVerdict.None;
                    }

                    known = true;
                    result = verdict;
                }
            }

            return result;
        }
    }

    private long Now() => clock?.Invoke() ?? Environment.TickCount64;
}
