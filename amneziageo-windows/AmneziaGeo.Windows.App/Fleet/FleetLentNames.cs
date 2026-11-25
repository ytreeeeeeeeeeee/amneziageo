using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// The names the tunnels standing alongside carry. A machine looks addresses up through one tunnel, so a name
/// matched by a rule riding another one arrives here: it is handed to the tunnel that owns it, and that one
/// resolves it and puts the addresses on its own path.
/// </summary>
internal sealed class FleetLentNames(IStateStore store, string tunnel, ILogger logger)
{
    // Answered when the owner carried nothing, so an empty reply stays what it has always been: no answer.
    private const string Nothing = "-";

    private volatile IReadOnlyList<FleetLentOwner> _owners = [];

    /// <summary>
    /// How many tunnels standing alongside carry names of their own.
    /// </summary>
    public int Count => _owners.Count;

    /// <summary>
    /// Re-reads which of them carries which names.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        var owners = new List<FleetLentOwner>();
        foreach (var peer in TunnelPaths.Peers(await store.GetSettingAsync(TunnelPaths.PeersKey(tunnel), ct)))
        {
            if (string.Equals(peer, tunnel, StringComparison.Ordinal))
            {
                continue;
            }

            var domains = (await store.GetActiveTunnelGeoAsync(peer, ct))?.Domains ?? [];
            if (domains.Count == 0)
            {
                continue;
            }

            owners.Add(new FleetLentOwner(peer, new NameRules(domains, [], [])));
            logger.LogInformation("{Peer}: {Count} name(s) are named by rules riding it, so this tunnel hands their lookups over instead of answering them", peer, domains.Count);
        }

        _owners = owners;
    }

    /// <summary>
    /// The tunnel a rule named the name on, or null.
    /// </summary>
    public string? Owner(string name, NameMatch? local) => OwnerOf(_owners, name, local);

    /// <summary>Chooses the best peer match when it outranks the local rule.</summary>
    public static string? OwnerOf(IReadOnlyList<FleetLentOwner> owners, string name, NameMatch? local = null)
    {
        var best = local;
        string? selected = null;
        foreach (var owner in owners)
        {
            if (owner.Matcher.Match(name) is { } match &&
                (best is null || NameRules.Compare(match, best.Value) > 0))
            {
                best = match;
                selected = owner.Tunnel;
            }
        }

        return selected;
    }

    /// <summary>
    /// Has the owner look the name up and carry its addresses; empty when it carried none.
    /// </summary>
    public async Task<IReadOnlyList<string>> CarryAsync(string owner, string name)
    {
        var answer = await Task.Run(() => RuntimeSnapshotPipe.Send(owner, RuntimeSnapshotPipe.Carry(name), logger));
        if (answer is null)
        {
            logger.LogWarning("[FIX] {Owner}: the Fleet DNS owner for {Name} did not answer", owner, name);
            return [];
        }

        if (string.IsNullOrWhiteSpace(answer) || answer.Trim() == Nothing)
        {
            return [];
        }

        return answer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>
/// One tunnel standing alongside and the names its own rules carry.
/// </summary>
/// <param name="Tunnel">The tunnel the names ride.</param>
/// <param name="Matcher">The names its rules matched.</param>
internal sealed record FleetLentOwner(string Tunnel, NameRules Matcher);
