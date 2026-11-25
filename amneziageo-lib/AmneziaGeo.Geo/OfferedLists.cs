using System.Globalization;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>
/// What taking the geo sources of a server did.
/// </summary>
/// <param name="Added">The sources the store held none of.</param>
/// <param name="Moved">The sources that took the address the server names.</param>
public sealed record TakenSources(IReadOnlyList<GeoSource> Added, IReadOnlyList<GeoSource> Moved)
{
    /// <summary>
    /// Nothing taken.
    /// </summary>
    public static TakenSources None { get; } = new([], []);

    /// <summary>
    /// Whether anything was taken.
    /// </summary>
    public bool Any => Added.Count > 0 || Moved.Count > 0;

    /// <summary>
    /// The sources whose files are to be fetched.
    /// </summary>
    public IReadOnlyList<GeoSource> Fetch => [.. Added, .. Moved];
}

/// <summary>
/// What taking the routing lists of a server did.
/// </summary>
/// <param name="Added">How many lists were added.</param>
/// <param name="Waiting">How many lists got a newer version that waits for the owner.</param>
/// <param name="InUse">The lists put in use, null when none were.</param>
/// <param name="Taking">The lists the server keeps read-only whose newer version is to be taken at once.</param>
/// <param name="Marked">How many lists the server made read-only or let go of being so.</param>
/// <param name="KeptSelection">Whether defaults were offered and the existing selection was kept.</param>
/// <param name="Linked">How many existing local lists were matched by UID to a server preset.</param>
public sealed record TakenPresets(int Added, int Waiting, IReadOnlyList<long>? InUse, IReadOnlyList<long>? Taking = null, int Marked = 0, bool KeptSelection = false, int Linked = 0)
{
    /// <summary>
    /// Nothing taken.
    /// </summary>
    public static TakenPresets None { get; } = new(0, 0, null);

    /// <summary>
    /// The lists whose newer version is to be taken at once, none when there are none.
    /// </summary>
    public IReadOnlyList<long> TakeNow => Taking ?? [];

    /// <summary>
    /// Whether anything was taken.
    /// </summary>
    public bool Any => Added > 0 || Waiting > 0 || InUse is not null || TakeNow.Count > 0 || Marked > 0 || Linked > 0;
}

/// <summary>
/// The newer version of a routing list that waits for the owner, the way the commands of the agent take it.
/// </summary>
/// <param name="ListId">The routing list.</param>
/// <param name="Name">The name the list takes: the one of the version, unless another list carries it.</param>
/// <param name="Rules">The rules of the version, each led by what it does with the traffic.</param>
/// <param name="Exclusions">The exclusions the list keeps.</param>
/// <param name="AllUdp">Whether every UDP packet goes through the tunnel.</param>
/// <param name="Full">Whether everything goes through the tunnel but what goes directly.</param>
public sealed record WaitingList(long ListId, string Name, IReadOnlyList<string> Rules, string Exclusions, bool AllUdp, bool Full)
{
    /// <summary>
    /// The arguments of the command that saves the list.
    /// </summary>
    public IReadOnlyList<string> SaveArgs => [ListId.ToString(CultureInfo.InvariantCulture), Name, .. Rules];

    /// <summary>
    /// The arguments of the command that sets the traffic settings of the list.
    /// </summary>
    public IReadOnlyList<string> SettingsArgs =>
        [ListId.ToString(CultureInfo.InvariantCulture), Exclusions, AllUdp ? "on" : "off", Full ? "full" : "split", Full ? "on" : "off"];
}

/// <summary>
/// Puts in the store the geo sources and the routing lists a server of ours hands its clients.
/// </summary>
public static class OfferedLists
{
    // Imports and acceptance share a gate so a hello cannot replace the pending version during acceptance.
    internal static SemaphoreSlim VersionGate { get; } = new(1, 1);

    private const int MaxSourceName = 32;

    private const int MaxTakenAddresses = 128;

    /// <summary>
    /// Adds, after the ones held, the sources the store holds none of at their address, and moves a source held under
    /// the name of the server to the address the server names, once per address. A source the owner removed comes
    /// back, so the lists of the server keep the keys they name.
    /// </summary>
    public static async Task<TakenSources> TakeSourcesAsync(IStateStore store, IReadOnlyList<OfferedSource> offered, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(offered);

        if (offered.Count == 0)
        {
            return TakenSources.None;
        }

        var held = (await store.ListGeoSourcesAsync(ct).ConfigureAwait(false)).ToList();
        var taken = Lines(await store.GetSettingAsync(StateKeys.OfferedSourceAddresses, ct).ConfigureAwait(false));
        var known = taken.Count;
        var added = new List<GeoSource>();
        var moved = new List<GeoSource>();
        foreach (var source in offered)
        {
            if (held.Exists(row => string.Equals(row.Url, source.Url, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var same = Fits(source.Name) ? held.FindIndex(row => SameSource(row, source)) : -1;
            if (same >= 0)
            {
                var line = $"{source.Name}\t{source.Url}";
                if (!taken.Contains(line, StringComparer.OrdinalIgnoreCase))
                {
                    taken.Add(line);
                    var row = held[same] with { Url = source.Url };
                    await store.SaveGeoSourceAsync(row, ct).ConfigureAwait(false);
                    held[same] = row;
                    moved.Add(row);
                }

                continue;
            }

            var position = held.Count == 0 ? 1 : held.Max(row => row.Position) + 1;
            var name = Fits(source.Name) && !held.Exists(row => string.Equals(row.Name, source.Name, StringComparison.Ordinal))
                ? source.Name
                : GeoSourceNames.Free(held, source.Kind, position);
            var fresh = new GeoSource(name, source.Kind, source.Url, position);
            await store.SaveGeoSourceAsync(fresh, ct).ConfigureAwait(false);
            held.Add(fresh);
            added.Add(fresh);
        }

        if (taken.Count != known)
        {
            await store.SetSettingAsync(StateKeys.OfferedSourceAddresses, string.Join('\n', taken.TakeLast(MaxTakenAddresses)), ct).ConfigureAwait(false);
        }

        return new TakenSources(added, moved);
    }

    /// <summary>
    /// Takes the routing lists a server hands out. A list the store knows by the identifier of the server keeps what
    /// it holds, and a newer version waits for the owner, or is to be taken at once when the server keeps the list
    /// read-only; a list the store holds none of is added, and put in use when the server marks it so and no list is
    /// in use; a local list held under the same UID is tied to the list of the server. A server that names no
    /// identifiers has its lists added by name. A list the server makes read-only takes the version of the server when it holds
    /// another; a read-only list the server of the configuration no longer hands out becomes an ordinary one.
    /// </summary>
    public static async Task<TakenPresets> TakePresetsAsync(
        IStateStore store,
        GeoConfigurator geo,
        string config,
        IReadOnlyList<OfferedPreset> offered,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(geo);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(offered);

        if (offered.Count == 0)
        {
            var released = await ReleaseAsync(store, config, offered, await store.ListRoutingListOriginsAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

            return released == 0 ? TakenPresets.None : new TakenPresets(0, 0, null, Marked: released);
        }

        var lists = (await store.ListRoutingListsAsync(ct).ConfigureAwait(false)).ToList();
        var origins = (await store.ListRoutingListOriginsAsync(ct).ConfigureAwait(false)).ToList();
        var names = new HashSet<string>(lists.Select(list => list.Name), StringComparer.Ordinal);
        var added = 0;
        var waiting = 0;
        var marks = 0;
        var linked = 0;
        var defaults = new HashSet<long>();
        var taking = new List<long>();
        foreach (var preset in offered)
        {
            if (preset.Id.Length == 0)
            {
                if (names.Add(preset.Name))
                {
                    var legacyId = await AddListAsync(store, geo, preset.Name, preset.Rules, preset.AllUdp, preset.Full,
                        string.Empty, ct).ConfigureAwait(false);
                    if (preset.Default)
                    {
                        defaults.Add(legacyId);
                    }
                    added++;
                }

                continue;
            }

            var version = preset with { Source = preset.Source.Length > 0 ? preset.Source : config };
            var when = preset.Updated ?? DateTimeOffset.MinValue;
            var at = origins.FindIndex(one => string.Equals(one.PresetId, preset.Id, StringComparison.Ordinal));
            if (at >= 0)
            {
                var origin = origins[at];
                if (preset.Default && lists.Any(list => list.Id == origin.ListId))
                {
                    defaults.Add(origin.ListId);
                }

                var marked = origin with { ReadOnly = preset.ReadOnly, Config = config };
                marks += marked.ReadOnly == origin.ReadOnly ? 0 : 1;
                if (when > Newest(origin) && lists.Find(list => list.Id == origin.ListId) is { } held)
                {
                    var same = await SameAsync(store, held, version, ct).ConfigureAwait(false);
                    marked = same ? marked with { Updated = when, Pending = null } : marked with { Pending = version };
                    waiting += same || preset.ReadOnly ? 0 : 1;
                }
                else if (marked is { ReadOnly: true, Pending: null } && !origin.ReadOnly
                    && lists.Find(list => list.Id == origin.ListId) is { } kept
                    && !await SameAsync(store, kept, version, ct).ConfigureAwait(false))
                {
                    // A list made read-only takes the version of its server in place of what the owner changed.
                    marked = marked with { Pending = version };
                }

                if (marked != origin)
                {
                    origins[at] = marked;
                    await store.SetRoutingListOriginAsync(marked, ct).ConfigureAwait(false);
                }

                if (marked is { ReadOnly: true, Pending: not null })
                {
                    taking.Add(marked.ListId);
                }

                continue;
            }

            // A backup can restore the UID before the first hello supplies the origin.
            var restored = lists.Find(list => string.Equals(list.Uid, preset.Id, StringComparison.Ordinal)
                && origins.All(origin => origin.ListId != list.Id));
            var id = restored?.Id
                ?? await AddListAsync(store, geo, preset.Name, preset.Rules, preset.AllUdp, preset.Full, preset.Id, ct).ConfigureAwait(false);
            var pending = restored is not null && !await SameAsync(store, restored, version, ct).ConfigureAwait(false)
                ? version
                : null;
            var fresh = new RoutingListOrigin(id, preset.Id, version.Source, pending is null ? when : DateTimeOffset.MinValue,
                Pending: pending, ReadOnly: preset.ReadOnly, Config: config);
            await store.SetRoutingListOriginAsync(fresh, ct).ConfigureAwait(false);
            origins.Add(fresh);
            names.Add(preset.Name);
            if (restored is null)
            {
                added++;
            }
            else
            {
                linked++;
            }

            if (pending is not null)
            {
                if (preset.ReadOnly)
                {
                    taking.Add(id);
                }
                else
                {
                    waiting++;
                }
            }

            if (preset.Default)
            {
                defaults.Add(id);
            }
        }

        marks += await ReleaseAsync(store, config, offered, origins, ct).ConfigureAwait(false);

        IReadOnlyList<long>? inUse = null;
        if (defaults.Count > 0)
        {
            var initialized = false;
            var selected = await store.UpdateSelectedRoutingListsAsync(current =>
            {
                initialized = current is null;
                return initialized ? defaults.ToArray() : null;
            }, ct).ConfigureAwait(false);
            inUse = initialized ? selected : null;
        }

        return new TakenPresets(added, waiting, inUse, taking, marks, KeptSelection: defaults.Count > 0 && inUse is null, Linked: linked);
    }

    /// <summary>
    /// Returns the refusal of a command that changes or removes a list the server keeps read-only, null for any other
    /// list; the command names the list first.
    /// </summary>
    public static async Task<IpcAck?> ReadOnlyRefusalAsync(IStateStore store, IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0
            || !long.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id <= 0
            || await store.GetRoutingListOriginAsync(id, ct).ConfigureAwait(false) is not { ReadOnly: true } origin)
        {
            return null;
        }

        var name = (await store.GetRoutingListStampAsync(id, ct).ConfigureAwait(false))?.Name ?? id.ToString(CultureInfo.InvariantCulture);

        return new IpcAck(false, IpcMessage.Key("Agent_RoutingListReadOnly", name, origin.Source));
    }

    // A read-only list the server of the configuration handed out last and no longer hands out becomes an ordinary one;
    // returns how many did.
    private static async Task<int> ReleaseAsync(
        IStateStore store,
        string config,
        IReadOnlyList<OfferedPreset> offered,
        IReadOnlyList<RoutingListOrigin> origins,
        CancellationToken ct)
    {
        var handed = offered.Select(preset => preset.Id).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        var released = 0;
        foreach (var origin in origins.Where(one => one.ReadOnly && string.Equals(one.Config, config, StringComparison.Ordinal) && !handed.Contains(one.PresetId)))
        {
            await store.SetRoutingListOriginAsync(origin with { ReadOnly = false }, ct).ConfigureAwait(false);
            released++;
        }

        return released;
    }

    /// <summary>
    /// Returns the newer version of a routing list that waits for the owner, null when the list holds the newest.
    /// </summary>
    public static async Task<WaitingList?> WaitingAsync(IStateStore store, long listId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var origin = await store.GetRoutingListOriginAsync(listId, ct).ConfigureAwait(false);
        var lists = await store.ListRoutingListsAsync(ct).ConfigureAwait(false);
        if (origin?.Pending is not { } pending || lists.FirstOrDefault(list => list.Id == listId) is not { } held)
        {
            return null;
        }

        var settings = await store.GetRoutingSettingsAsync(listId, ct).ConfigureAwait(false);

        return new WaitingList(listId, pending.Name, pending.Rules, settings?.Exclusions ?? string.Empty, pending.AllUdp, pending.Full);
    }

    /// <summary>
    /// Applies the waiting version and settles it only after its rules and settings have both been saved.
    /// </summary>
    public static async Task<IpcAck> AcceptPendingAsync(
        IStateStore store,
        long listId,
        Func<WaitingList, Task<IpcAck>> apply,
        IpcAck missing,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(apply);

        await VersionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await WaitingAsync(store, listId, ct).ConfigureAwait(false) is not { } waiting)
            {
                return missing;
            }

            var result = await apply(waiting).ConfigureAwait(false);
            if (result.Ok)
            {
                await SettleAsync(store, listId, ct).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            VersionGate.Release();
        }
    }

    private static async Task SettleAsync(IStateStore store, long listId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var origin = await store.GetRoutingListOriginAsync(listId, ct).ConfigureAwait(false);
        if (origin?.Pending is not { } pending)
        {
            return;
        }

        await store.SetRoutingListOriginAsync(
            origin with
            {
                Source = pending.Source.Length > 0 ? pending.Source : origin.Source,
                Updated = pending.Updated ?? origin.Updated,
                Pending = null,
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a routing list with its rules and the way the rest of the traffic goes. Returns its number.
    /// </summary>
    public static async Task<long> AddListAsync(
        IStateStore store,
        GeoConfigurator geo,
        string name,
        IReadOnlyList<string> rules,
        bool allUdp,
        bool full,
        string uid,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(geo);

        var id = await geo.ApplyToRoutingListAsync(0, name, rules, uid, ct).ConfigureAwait(false);
        if (allUdp || full)
        {
            await store.SetRoutingSettingsAsync(new RoutingSettings(id, string.Empty, allUdp, full ? "full" : "split", full), ct).ConfigureAwait(false);
        }

        return id;
    }

    // A name the server gives a source is kept when it reads as one: lower-case letters, digits, dots and dashes.
    private static bool Fits(string name) =>
        name.Length is > 0 and <= MaxSourceName
        && char.IsAsciiLetterLower(name[0])
        && name.All(letter => char.IsAsciiLetterLower(letter) || char.IsAsciiDigit(letter) || letter is '.' or '-');

    private static bool SameSource(GeoSource row, OfferedSource source) =>
        string.Equals(row.Name, source.Name, StringComparison.Ordinal)
        && string.Equals(row.Kind, source.Kind, StringComparison.OrdinalIgnoreCase);

    private static List<string> Lines(string? text) =>
        [.. (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // The time of the newest version of the server the list knows of: the one it holds or the one that waits.
    private static DateTimeOffset Newest(RoutingListOrigin origin) =>
        origin.Pending?.Updated is { } waiting && waiting > origin.Updated ? waiting : origin.Updated;

    // Tells whether a list holds what a version of the server hands out.
    private static async Task<bool> SameAsync(IStateStore store, RoutingList list, OfferedPreset version, CancellationToken ct)
    {
        var settings = await store.GetRoutingSettingsAsync(list.Id, ct).ConfigureAwait(false);
        var offered = version.Rules.Select(GeoConfigurator.ParseRoleRule).OfType<GeoRule>().Select(GeoConfigurator.FormatWithRole);

        return string.Equals(list.Name, version.Name, StringComparison.Ordinal)
            && (settings?.AllUdp ?? false) == version.AllUdp
            && (settings?.UseGlobalProxy ?? false) == version.Full
            && list.Rules.Select(GeoConfigurator.FormatWithRole).SequenceEqual(offered, StringComparer.Ordinal);
    }

}
