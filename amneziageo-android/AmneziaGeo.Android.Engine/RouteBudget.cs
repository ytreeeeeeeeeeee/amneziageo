namespace AmneziaGeo.Android.Engine;

/// <summary>
/// How many routes a tunnel built without the relay carries.
/// </summary>
public static class RouteBudget
{
    // establish() hands the whole route table over in one Binder transaction: 80 bytes a route against a 1 MB parcel.
    private const int BytesPerRoute = 80;

    private const int ParcelBytes = 1024 * 1024;

    // Addresses one name is given room to come back with, so a rule set still fits once its names are resolved.
    private const int AddressesPerName = 8;

    /// <summary>
    /// Routes such a tunnel takes, leaving room for the addresses a session adds at connect.
    /// </summary>
    public const int Max = ParcelBytes / BytesPerRoute * 9 / 10;

    public static bool SupportsOwner(int api) => api >= 29;

    // The Binder parcel includes the package strings, DNS servers and the route lists.
    public static bool Fits(int routes, int names, IEnumerable<string> packages) =>
        ((long)routes + (long)names * AddressesPerName) * BytesPerRoute
        + packages.Sum(package => 16L + 2L * package.Length) <= Max * BytesPerRoute;

    /// <summary>
    /// Whether this many routes still fit once these names are resolved to addresses.
    /// </summary>
    public static bool Fits(int routes, int names) => Fits(routes, names, []);
}
