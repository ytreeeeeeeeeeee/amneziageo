using System.ComponentModel;
using System.Globalization;
using AmneziaGeo.Geo;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Sends what leaves for the ranges a list keeps direct past a tunnel that carries everything, from the first packet
/// and whatever sends it. The ranges stand as routes of a table of their own, looked up after the routes of the
/// machine narrower than a half of the address space and before the tunnel. Own routing table and rules; the routes
/// of other software are never touched.
/// </summary>
internal sealed class DirectPath
{
    internal delegate Task<(int ExitCode, string Output)> IpRunner(CancellationToken ct, params string[] args);

    /// <summary>
    /// The routing table the ranges stand in.
    /// </summary>
    public const string Table = "51821";

    private const int OwnRulePriority = 5100;
    private const int RangeRulePriority = 5101;
    private const string Fallback = "the direct ranges go past the tunnel only once a connection to them is seen";

    private readonly string _gateway;
    private readonly string _device;
    private readonly AgentLog _log;
    private readonly string _batchPath;
    private readonly IpRunner _runIp;
    private IReadOnlyList<string> _standing = [];
    private bool _ruled;
    private bool _standingNeedsReinstall;

    /// <summary>
    /// ctor
    /// </summary>
    public DirectPath(string gateway, string device, AgentLog log)
        : this(gateway, device, log, Path.Combine(AgentPaths.Root, "direct.routes"), RunIpAsync)
    {
    }

    internal DirectPath(string gateway, string device, AgentLog log, string batchPath, IpRunner runIp)
    {
        _gateway = gateway;
        _device = device;
        _log = log;
        _batchPath = batchPath;
        _runIp = runIp;
    }

    /// <summary>
    /// Squares the standing routes with the ranges and returns whether they stand; no ranges take the path down.
    /// </summary>
    public async Task<bool> ApplyAsync(IReadOnlyList<string> ranges, CancellationToken ct)
    {
        if (ranges.Count == 0)
        {
            if (!await ShedAsync([], ct).ConfigureAwait(false))
            {
                return false;
            }

            await LowerAsync(ct).ConfigureAwait(false);
            return true;
        }

        var (added, removed) = StandingRanges.Diff(_standing, ranges);
        var reinstall = _standingNeedsReinstall;
        var toInstall = reinstall ? ranges : added;
        if (_ruled && toInstall.Count == 0 && removed.Count == 0)
        {
            return true;
        }

        if (!_ruled && !await RaiseAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        var first = _standing.Count == 0;
        if (toInstall.Count > 0 && !await RunAsync(SteeringRules.Bypasses(toInstall, _gateway, _device, Table), false, ct).ConfigureAwait(false))
        {
            await LowerAsync(ct).ConfigureAwait(false);
            return false;
        }

        if (removed.Count > 0)
        {
            if (added.Count > 0)
            {
                // The additions already stand and must be removable if the withdrawal fails.
                _standing = [.. _standing, .. added];
            }

            if (!await WithdrawAsync(removed, ct).ConfigureAwait(false))
            {
                return false;
            }
        }

        _standing = [.. ranges];
        _standingNeedsReinstall = false;
        if (reinstall)
        {
            _log.Info("routing", $"reinstalled {ranges.Count} direct range(s) after a failed withdrawal");
        }
        else
        {
            _log.Info("routing", first
                ? $"what leaves for the {ranges.Count} direct range(s) of the list goes past the tunnel from the first packet, through {_gateway} on {_device}"
                : $"direct ranges past the tunnel squared: {added.Count} added, {removed.Count} removed");
        }

        return true;
    }

    /// <summary>
    /// The ranges standing in the table.
    /// </summary>
    public IReadOnlyList<string> Standing => _standing;

    /// <summary>
    /// The standing ranges the list given keeps.
    /// </summary>
    public IReadOnlyList<string> Staying(IReadOnlyList<string> ranges)
    {
        var (_, removed) = StandingRanges.Diff(_standing, ranges);
        var gone = new HashSet<string>(removed, StringComparer.Ordinal);
        return [.. _standing.Where(range => !gone.Contains(range))];
    }

    /// <summary>
    /// Takes down the routes of the ranges that left the list; the new ones wait for the next squaring.
    /// </summary>
    public async Task<bool> ShedAsync(IReadOnlyList<string> ranges, CancellationToken ct)
    {
        var (_, removed) = StandingRanges.Diff(_standing, ranges);
        if (!_ruled || removed.Count == 0)
        {
            return true;
        }

        var staying = Staying(ranges);
        if (!await WithdrawAsync(removed, ct).ConfigureAwait(false))
        {
            return false;
        }

        _standing = staying;
        _log.Info("routing", $"direct ranges past the tunnel squared: 0 added, {removed.Count} removed");
        return true;
    }

    private async Task<bool> WithdrawAsync(IReadOnlyList<string> removed, CancellationToken ct)
    {
        // A failed or cancelled ip batch can already have removed some of the recorded routes.
        var needsReinstall = _standingNeedsReinstall;
        _standingNeedsReinstall = true;
        if (!await RunAsync(SteeringRules.Withdrawals(removed, Table), true, ct).ConfigureAwait(false))
        {
            return false;
        }

        _standingNeedsReinstall = needsReinstall;
        return true;
    }

    /// <summary>
    /// Drops the routing rules and the routing table.
    /// </summary>
    public static Task RemoveAsync(CancellationToken ct) => RemoveAsync(ct, RunIpAsync);

    private static async Task RemoveAsync(CancellationToken ct, IpRunner runIp)
    {
        try
        {
            await DeleteRulesAsync(runIp, ct, "lookup", Table, "priority", Priority(RangeRulePriority)).ConfigureAwait(false);
            await DeleteRulesAsync(runIp, ct, "lookup", "main", "suppress_prefixlength", "1", "priority", Priority(OwnRulePriority)).ConfigureAwait(false);
            await runIp(ct, "route", "flush", "table", Table).ConfigureAwait(false);
        }
        catch (Win32Exception)
        {
        }
    }

    // Stands the two lookups ahead of the routes of the machine: its own narrower routes first, then the ranges.
    private async Task<bool> RaiseAsync(CancellationToken ct)
    {
        await RemoveAsync(ct, _runIp).ConfigureAwait(false);
        string[][] steps =
        [
            ["rule", "add", "lookup", "main", "suppress_prefixlength", "1", "priority", Priority(OwnRulePriority)],
            ["rule", "add", "lookup", Table, "priority", Priority(RangeRulePriority)],
        ];
        try
        {
            foreach (var step in steps)
            {
                var (exitCode, output) = await _runIp(ct, step).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    _log.Warn("routing", $"{Fallback}: ip {string.Join(' ', step)} failed: {output}");
                    await RemoveAsync(ct, _runIp).ConfigureAwait(false);
                    return false;
                }
            }
        }
        catch (Win32Exception ex)
        {
            _log.Warn("routing", $"{Fallback}: {ex.Message}");
            return false;
        }

        _ruled = true;
        return true;
    }

    // Takes the path down where it stands.
    private async Task LowerAsync(CancellationToken ct)
    {
        if (!_ruled && _standing.Count == 0)
        {
            return;
        }

        await RemoveAsync(ct, _runIp).ConfigureAwait(false);
        _ruled = false;
        _standing = [];
        _standingNeedsReinstall = false;
        _log.Info("routing", "no direct range of the list stands past the tunnel any more");
    }

    // Hands iproute2 the lines in one run; a forced run goes on past a line that fails.
    private async Task<bool> RunAsync(string lines, bool force, CancellationToken ct)
    {
        try
        {
            await File.WriteAllTextAsync(_batchPath, lines, ct).ConfigureAwait(false);
            var (exitCode, output) = force
                ? await _runIp(ct, "-force", "-batch", _batchPath).ConfigureAwait(false)
                : await _runIp(ct, "-batch", _batchPath).ConfigureAwait(false);
            if (exitCode != 0)
            {
                _log.Warn("routing", $"{Fallback}: {output.Split('\n')[0]}");
            }

            return exitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _log.Warn("routing", $"{Fallback}: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                File.Delete(_batchPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // Deletes every routing rule of the kind, the ones an earlier run left behind included.
    private static async Task DeleteRulesAsync(IpRunner runIp, CancellationToken ct, params string[] rule)
    {
        var deleted = true;
        while (deleted)
        {
            var (exitCode, _) = await runIp(ct, ["rule", "del", .. rule]).ConfigureAwait(false);
            deleted = exitCode == 0;
        }
    }

    private static Task<(int ExitCode, string Output)> RunIpAsync(CancellationToken ct, params string[] args) =>
        Shell.RunAsync("ip", ct, args);

    private static string Priority(int priority) => priority.ToString(CultureInfo.InvariantCulture);
}
