using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>
/// The local routing lists a configuration's server permits and its fallback selection.
/// </summary>
/// <param name="Config">The configuration whose server allows none but its own lists.</param>
/// <param name="Lists">The lists of that server the device holds, in the order the server hands them out.</param>
/// <param name="Fallback">All available defaults, or the first permitted list if there are no defaults.</param>
public sealed record ListsLimit(string Config, IReadOnlyList<long> Lists, IReadOnlyList<long> Fallback)
{
    /// <summary>
    /// Whether a list may be in use; none may not.
    /// </summary>
    public bool Allows(long? listId) => listId is { } id && Lists.Contains(id);

    /// <summary>Whether every member is permitted and the set is nonempty.</summary>
    public bool AllowsSet(IReadOnlyList<long> ids) => ids.Count > 0 && ids.All(Lists.Contains);

    /// <summary>Whether the device has any of the allowed presets.</summary>
    public bool Available => Lists.Count > 0;
}

/// <summary>
/// Keeps the routing list in use among the lists of a server that allows none but its own.
/// </summary>
public static class ServerListsOnly
{
    /// <summary>
    /// Returns the limit the server sets. An empty limit means the server requires presets that are unavailable.
    /// </summary>
    public static async Task<ListsLimit?> OfAsync(IStateStore store, string config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        return await OfAsync(store, config, await store.GetConfigTextAsync(config, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the limit the server of a config with its text sets, null when it allows any list.
    /// </summary>
    public static async Task<ListsLimit?> OfAsync(IStateStore store, string config, string? text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);

        var offer = await ServerOfferStore.ReadAsync(store, config, text, ct).ConfigureAwait(false);
        if (!offer.PresetsOnly)
        {
            return null;
        }

        var origins = await OriginsAsync(store, ct).ConfigureAwait(false);
        var lists = new List<long>();
        var defaults = new List<long>();
        foreach (var preset in offer.Presets().Where(one => one.Id.Length > 0))
        {
            if (!origins.TryGetValue(preset.Id, out var listId) || lists.Contains(listId))
            {
                continue;
            }

            lists.Add(listId);
            if (preset.Default)
            {
                defaults.Add(listId);
            }
        }

        IReadOnlyList<long> fallback = defaults.Count > 0 ? defaults : lists.Take(1).ToArray();
        return new ListsLimit(config, lists, fallback);
    }

    /// <summary>
    /// Returns the limit the first of the configs whose server allows none but its own lists sets, null when none
    /// does.
    /// </summary>
    public static async Task<ListsLimit?> FirstAsync(IStateStore store, IEnumerable<string> configs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configs);

        foreach (var config in configs)
        {
            if (await OfAsync(store, config, ct).ConfigureAwait(false) is { } limit)
            {
                return limit;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the refusal of a command that puts in use a list the limit does not allow, or none, null when the
    /// limit allows it.
    /// </summary>
    public static IpcAck? Refusal(ListsLimit? limit, long? listId) =>
        RefusalOfSet(limit, listId is long id ? [id] : []);

    /// <summary>Checks every member of a choice, including attempts to clear the entire set.</summary>
    public static IpcAck? RefusalOfSet(ListsLimit? limit, IReadOnlyList<long> ids) =>
        limit is null || limit.AllowsSet(ids) ? null : Refused(limit);

    /// <summary>Validates and applies an IPC selection change against the latest stored choice.</summary>
    public static async Task<(IpcAck? Refusal, IReadOnlyList<long> Before, IReadOnlyList<long> After)> ChangeSelectionAsync(
        IStateStore store, ListsLimit? limit, RoutingSelectionChange? change, CancellationToken ct = default)
    {
        if (change is null)
        {
            return (new IpcAck(false, "invalid routing selection action or list ID"), [], []);
        }

        IReadOnlyList<long> before = [];
        IpcAck? refusal = null;
        try
        {
            var after = await store.UpdateSelectedRoutingListsAsync(current =>
            {
                before = current ?? [];
                var next = change.Apply(before);
                refusal = RefusalOfSet(limit, next);
                return refusal is not null || (change.Action != RoutingSelectionChange.Replace && before.SequenceEqual(next))
                    ? null : next;
            }, ct).ConfigureAwait(false);
            return (refusal, before, after ?? []);
        }
        catch (InvalidOperationException)
        {
            return (new IpcAck(false, "the routing selection contains an unknown or invalid list"), before, before);
        }
    }

    /// <summary>Removes a list without racing another change to the selected set.</summary>
    public static async Task<IpcAck?> RemoveAsync(IStateStore store, ListsLimit? limit, long id, CancellationToken ct)
    {
        if (limit is null || !limit.Allows(id))
        {
            await store.RemoveRoutingListAsync(id, ct).ConfigureAwait(false);
            return null;
        }

        var removed = await store.TryRemoveRoutingListAsync(id, selected =>
            selected is null || !selected.Contains(id) || selected.Any(other => other != id && limit.Allows(other)), ct)
            .ConfigureAwait(false);
        return removed ? null : Refused(limit);
    }

    /// <summary>Retains permitted members, or installs the fallback set if the permitted subset is empty.</summary>
    public static async Task<bool> EnforceSelectionAsync(IStateStore store, ListsLimit? limit, CancellationToken ct)
    {
        if (limit is not { Available: true })
        {
            return false;
        }

        var changed = false;
        await store.UpdateSelectedRoutingListsAsync(current =>
        {
            if (current is not null && limit.AllowsSet(current))
            {
                return null;
            }

            var allowed = current?.Where(limit.Lists.Contains).ToArray();
            changed = true;
            return allowed is { Length: > 0 } ? allowed : limit.Fallback;
        }, ct).ConfigureAwait(false);
        return changed;
    }

    /// <summary>Reason shown when a restricted connection has no local presets it can use.</summary>
    public const string UnavailableDetail = "The server requires routing presets that are not available on this device.";

    private static IpcAck Refused(ListsLimit limit) =>
        new(false, IpcMessage.Key("Agent_RoutingListsServerOnly", limit.Config));

    /// <summary>
    /// Returns the refusal of a command that keeps a stored config off the routing list where its server allows none
    /// but its own lists, null when it may.
    /// </summary>
    public static async Task<IpcAck?> RoutingOffRefusalAsync(IStateStore store, string config, bool useRouting, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        return await RoutingOffRefusalAsync(store, config, await store.GetConfigTextAsync(config, ct).ConfigureAwait(false), useRouting, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the refusal of a command that keeps a config with its text off the routing list where its server allows
    /// none but its own lists, null when it may.
    /// </summary>
    public static async Task<IpcAck?> RoutingOffRefusalAsync(IStateStore store, string config, string? text, bool useRouting, CancellationToken ct) =>
        !useRouting && (await ServerOfferStore.ReadAsync(store, config, text, ct).ConfigureAwait(false)).PresetsOnly
            ? new IpcAck(false, IpcMessage.Key("Agent_RoutingListsServerOnly", config))
            : null;

    /// <summary>
    /// Turns the routing list on for a stored config whose server allows none but its own lists; returns whether it
    /// was off.
    /// </summary>
    public static async Task<bool> RoutingOnAsync(IStateStore store, string config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        return await RoutingOnAsync(store, config, await store.GetConfigTextAsync(config, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the routing list on for a config with its text whose server allows none but its own lists; returns
    /// whether it was off.
    /// </summary>
    public static async Task<bool> RoutingOnAsync(IStateStore store, string config, string? text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);

        if (await store.GetConfigTransportAsync(config, ct).ConfigureAwait(false) is not { UseRouting: false } transport
            || !(await ServerOfferStore.ReadAsync(store, config, text, ct).ConfigureAwait(false)).PresetsOnly)
        {
            return false;
        }

        await store.SetConfigTransportAsync(transport with { UseRouting = true }, ct).ConfigureAwait(false);

        return true;
    }

    // The list each identifier of a server stands for.
    private static async Task<Dictionary<string, long>> OriginsAsync(IStateStore store, CancellationToken ct)
    {
        var origins = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var origin in await store.ListRoutingListOriginsAsync(ct).ConfigureAwait(false))
        {
            origins.TryAdd(origin.PresetId, origin.ListId);
        }

        return origins;
    }
}
