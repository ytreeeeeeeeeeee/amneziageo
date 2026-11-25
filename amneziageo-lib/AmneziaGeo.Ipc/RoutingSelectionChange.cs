using System.Globalization;

namespace AmneziaGeo.Ipc;

/// <summary>A change to the requested routing lists, applied against the agent's current selection.</summary>
public sealed record RoutingSelectionChange(string Action, IReadOnlyList<long> Ids)
{
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Replace = "replace";

    public static RoutingSelectionChange? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is not (Add or Remove or Replace)
            || (args[0] != Replace && args.Count != 2)
            || RoutingListIds.Parse([.. args.Skip(1)]) is not { } ids
            || ids.Any(id => id <= 0))
        {
            return null;
        }

        return new RoutingSelectionChange(args[0], ids);
    }

    public long[] Apply(IReadOnlyList<long> current) => (Action switch
    {
        Add => current.Concat(Ids),
        Remove => current.Except(Ids),
        Replace => Ids,
        _ => throw new InvalidOperationException("Unknown routing selection action."),
    }).Distinct().Order().ToArray();

    public IpcCommand LegacyCommand() => new(IpcContract.OpAssignRouting,
        [Action == Remove || Ids.Count == 0 ? "none" : Ids[0].ToString(CultureInfo.InvariantCulture)]);

    public IpcCommand Command() => new(IpcContract.OpChangeRoutingSelection,
        [Action, .. Ids.Select(id => id.ToString(CultureInfo.InvariantCulture))]);
}
