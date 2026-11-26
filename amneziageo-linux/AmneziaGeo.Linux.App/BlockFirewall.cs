using System.ComponentModel;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Refuses what this machine sends to the ranges the list blocks. Own nftables table; the rules of other software
/// are never touched.
/// </summary>
internal static class BlockFirewall
{
    private const string RoutingUpdateTable = "amneziageo-routing-update";

    /// <summary>
    /// Refuses the ranges from the first packet and returns whether the table stands; no ranges take it down.
    /// </summary>
    public static async Task<bool> ApplyAsync(IReadOnlyList<string> ranges, AgentLog log, CancellationToken ct)
    {
        var path = Path.Combine(AgentPaths.Root, "block.nft");
        try
        {
            await File.WriteAllTextAsync(path, SteeringRules.Refusals(ranges, exemptApps: false), ct).ConfigureAwait(false);
            var applied = await Shell.RunAsync("nft", ct, "-f", path).ConfigureAwait(false);
            if (applied.ExitCode != 0)
            {
                log.Warn("routing", $"the blocked ranges are refused only once a connection to them is seen: {applied.Output}");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            log.Warn("routing", $"the blocked ranges are refused only once a connection to them is seen: {ex.Message}");
            return false;
        }

        log.Info("routing", $"{ranges.Count} blocked range(s) are refused from the first packet, whatever sends it");
        return true;
    }

    // Rules, peer routes and DNS marks are separate kernel operations. Hold egress while they disagree.
    internal static async Task HoldRoutingUpdateAsync()
    {
        var path = Path.Combine(AgentPaths.Root, "routing-update.nft");
        await File.WriteAllTextAsync(path, $$"""
            table inet {{RoutingUpdateTable}} {
                chain output {
                    type filter hook output priority filter; policy drop;
                    oifname "lo" accept
                }
            }
            """).ConfigureAwait(false);
        var result = await Shell.RunAsync("nft", CancellationToken.None, "-f", path).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"Could not hold outgoing traffic during the routing update: {result.Output}");
        }
    }

    internal static async Task<bool> ReleaseRoutingUpdateAsync()
    {
        var result = await Shell.RunAsync("nft", CancellationToken.None, "delete", "table", "inet", RoutingUpdateTable)
            .ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    /// <summary>
    /// Drops the table.
    /// </summary>
    public static async Task RemoveAsync(CancellationToken ct)
    {
        try
        {
            await Shell.RunAsync("nft", ct, "delete", "table", "inet", SteeringRules.BlockTable).ConfigureAwait(false);
        }
        catch (Win32Exception)
        {
        }
    }
}
