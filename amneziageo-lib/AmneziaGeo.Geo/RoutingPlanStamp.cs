using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Stamp of everything a session plan is built from: an equal stamp means the plan already written still fits.
/// </summary>
public static class RoutingPlanStamp
{
    // Rises with the shape of the plan, so a plan left by an older build is never taken for a fitting one.
    private const int Format = 3;

    /// <summary>
    /// Returns the stamp of a list with its settings and the session around it.
    /// </summary>
    public static string Of(RoutingListStamp? list, RoutingSettings? settings, IReadOnlyList<string> inbound,
        bool useRouter, bool perApp, int ttlSeconds, int splitLimit)
    {
        var text = new StringBuilder();
        Number(text, Format);
        Number(text, list?.Id ?? 0);
        Number(text, list?.Generation ?? 0);
        foreach (var rule in list?.Rules ?? [])
        {
            text.Append('|').Append(rule.Kind).Append(':').Append(rule.Role).Append(':').Append(rule.Value);
        }

        text.Append('|').Append(settings?.Exclusions ?? string.Empty)
            .Append('|').Append(settings is { AllUdp: true })
            .Append('|').Append(settings is { UseGlobalProxy: true })
            .Append('|').Append(settings?.Mode ?? string.Empty)
            .Append('|').Append(string.Join(',', inbound))
            .Append('|').Append(useRouter)
            .Append('|').Append(perApp);
        Number(text, ttlSeconds);
        Number(text, splitLimit);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Stamps an already compiled policy in its tunnel and resolver context.</summary>
    public static string OfPolicy(RoutingPolicy? policy, IReadOnlyList<string> inbound,
        bool useRouter, bool perApp, int ttlSeconds, int splitLimit) =>
        Hash(new { Format, Policy = policy?.Stamp, inbound, useRouter, perApp, ttlSeconds, splitLimit });

    internal static string ForPolicy(RoutingList list, RoutingSettings? settings, IReadOnlyList<RoutingListStamp> sources,
        IReadOnlyList<RoutingRuleOrigin> origins, AppRulePlatform platform, IReadOnlyList<GeoSource> geoSources,
        IReadOnlyList<GeoFileMetadata> geoFiles) =>
        Hash(new
        {
            Format,
            list,
            settings,
            Sources = sources.Select(source => new { source.Id, source.Generation }),
            Rules = origins.Select(origin => new { origin.ListId, origin.Original, origin.Unfolded.Rule }),
            Expansions = origins.Select(origin => origin.Unfolded).Distinct(),
            platform,
            GeoSources = geoSources.OrderBy(source => source.Position).ThenBy(source => source.Name, StringComparer.Ordinal)
                .Select(source => new { source.Name, source.Kind, source.Position }),
            Files = geoFiles.OrderBy(file => file.Name, StringComparer.Ordinal).Select(file => new { file.Name, file.Sha256 }),
        });

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static void Number(StringBuilder text, long value)
    {
        text.Append('|').Append(value.ToString(CultureInfo.InvariantCulture));
    }
}
