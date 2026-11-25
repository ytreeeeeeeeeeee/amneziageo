namespace AmneziaGeo.Decl;

/// <summary>
/// Setting keys the agent and the store share.
/// </summary>
public static class StateKeys
{
    /// <summary>
    /// Name of the config the agent binds to.
    /// </summary>
    public const string SelectedTarget = "selected-target";

    /// <summary>
    /// Global routing selection as a JSON array. An absent setting means no choice has been made.
    /// </summary>
    public const string SelectedRoutingList = "selected-routing-list";

    /// <summary>
    /// The addresses the geo sources took from servers, a source name and an address per line.
    /// </summary>
    public const string OfferedSourceAddresses = "geo-offered-urls";
}
