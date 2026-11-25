using System.Globalization;

using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>
/// Routing lists in a bundle: which lists a selection picks, and which held list a block of the bundle is.
/// </summary>
public static class BundleLists
{
    /// <summary>
    /// Name a block without one lands under.
    /// </summary>
    public const string FallbackName = "Список";

    /// <summary>
    /// Picks the lists a selection names, each list once, with the word that picked it. A word that is the number
    /// of a list picks that list; any other word picks every list under that name. Words that pick nothing come
    /// back apart.
    /// </summary>
    public static (IReadOnlyList<(string Key, RoutingList List)> Picked, IReadOnlyList<string> Missing) Pick(
        IReadOnlyList<RoutingList> lists,
        IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentNullException.ThrowIfNull(keys);

        var picked = new List<(string Key, RoutingList List)>();
        var missing = new List<string>();
        var seen = new HashSet<long>();
        foreach (var key in keys)
        {
            var found = Found(lists, key);
            if (found.Count == 0)
            {
                missing.Add(key);
                continue;
            }

            foreach (var list in found)
            {
                if (seen.Add(list.Id))
                {
                    picked.Add((key, list));
                }
            }
        }

        return (picked, missing);
    }

    /// <summary>
    /// The held list a block of the bundle is: the one under the lasting id the block carries. A block without an
    /// id, or with one no list holds, is a list of its own.
    /// </summary>
    public static RoutingList? Held(PortableBundle.RoutingBlock block, IReadOnlyList<RoutingList> lists)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(lists);

        return string.IsNullOrEmpty(block.Id)
            ? null
            : lists.FirstOrDefault(list => string.Equals(list.Uid, block.Id, StringComparison.Ordinal));
    }

    /// <summary>
    /// The name a block of the bundle lands under.
    /// </summary>
    public static string NameOf(PortableBundle.RoutingBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var name = block.Name.Trim();
        return name.Length > 0 ? name : FallbackName;
    }

    /// <summary>Records the choice in a full backup when every selected list travels with it.</summary>
    public static async Task<IReadOnlyList<string>?> ExportSelectionAsync(
        IStateStore store, IReadOnlyList<(string Key, RoutingList List)> picked, bool full, CancellationToken ct = default)
    {
        if (!full)
        {
            return null;
        }

        var selected = await store.GetSelectedRoutingListsAsync(ct).ConfigureAwait(false);
        if (selected is null)
        {
            return null;
        }

        var uids = picked.Select(item => item.List).Where(list => selected.Contains(list.Id) && list.Uid.Length > 0)
            .Select(list => list.Uid).Order(StringComparer.Ordinal).ToArray();
        return uids.Length == selected.Count ? uids : null;
    }

    /// <summary>Resolves the whole choice before asking the platform to validate and install it.</summary>
    public static async Task<bool> RestoreSelectionAsync(
        IStateStore store, PortableBundle.Bundle bundle,
        Func<RoutingSelectionChange, CancellationToken, Task<IpcAck>> change,
        Action<string> info, Action<string> warn, Action<string> error,
        CancellationToken ct = default)
    {
        var uids = bundle.ActiveRoutingListIds
            ?? (bundle.ActiveRoutingListId is { Length: > 0 } uid ? [uid] : null);
        if (uids is null)
        {
            return true;
        }

        try
        {
            var ids = new List<long>();
            var unknown = 0;
            foreach (var identifier in uids.Distinct(StringComparer.Ordinal))
            {
                var list = string.IsNullOrEmpty(identifier)
                    ? null
                    : await store.GetRoutingListByUidAsync(identifier, ct).ConfigureAwait(false);
                if (list is null)
                {
                    unknown++;
                }
                else
                {
                    ids.Add(list.Id);
                }
            }

            if (unknown > 0)
            {
                warn($"Skipped the backup routing selection: {unknown} unknown list UID(s)");
                return true;
            }

            var ack = await change(new(RoutingSelectionChange.Replace, ids), ct).ConfigureAwait(false);
            if (!ack.Ok)
            {
                warn($"Skipped the backup routing selection: the change to {ids.Count} list(s) was refused");
                return true;
            }

            info($"Restored the backup routing selection: {ids.Count} list(s)");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error($"Could not restore the backup routing selection ({ex.GetType().Name})");
            return false;
        }
    }

    // The lists one word of a selection names.
    private static IReadOnlyList<RoutingList> Found(IReadOnlyList<RoutingList> lists, string key)
    {
        if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            && lists.FirstOrDefault(list => list.Id == id) is { } numbered)
        {
            return [numbered];
        }

        return [.. lists.Where(list => string.Equals(list.Name, key, StringComparison.Ordinal))];
    }
}
