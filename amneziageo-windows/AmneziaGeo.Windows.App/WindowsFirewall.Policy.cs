using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Windows.App;

internal sealed partial class WindowsFirewall
{
    private sealed record NamePermit(RouteVerdict Verdict, long Expires, ulong Filter = 0);
    private readonly Dictionary<IPAddress, NamePermit> _namePermits = [];
    private readonly List<ulong> _blockedApps = [];

    internal static IPNetwork[] Ipv6PolicyBlocks(IReadOnlyList<string> ranges) =>
        ranges.Where(cidr => cidr.Contains(':')).Select(cidr => IPNetwork.Parse(RangeClaims.Key(cidr))).Distinct().ToArray();

    // Separate from the kill switch so explicit blocks take effect before the tunnel starts, in any Fleet role.
    internal IDisposable? InstallIpv6PolicyBlocks(IReadOnlyList<string> ranges)
    {
        var prefixes = Ipv6PolicyBlocks(ranges);
        if (prefixes.Length == 0)
        {
            return null;
        }

        var session = new FWPM_SESSION0 { flags = SessionFlagDynamic };
        var open = FwpmEngineOpen0(IntPtr.Zero, RpcCAuthnWinnt, IntPtr.Zero, ref session, out var engine);
        if (open != 0)
        {
            throw new InvalidOperationException($"Windows could not open the IPv6 block policy (0x{open:X8}).");
        }

        var dynamicSession = new Ipv6BlockSession(engine);
        var batched = false;
        try
        {
            batched = FwpmTransactionBegin0(engine, 0) == 0;
            var key = Guid.NewGuid();
            if (!batched || !CreateSublayer(engine, key, "AmneziaGeo IPv6 route blocks"))
            {
                throw new InvalidOperationException("Windows could not start the IPv6 block policy transaction.");
            }

            var mask = Marshal.AllocHGlobal(17); // FWP_V6_ADDR_AND_MASK.
            try
            {
                foreach (var prefix in prefixes)
                {
                    Marshal.Copy(prefix.BaseAddress.GetAddressBytes(), 0, mask, 16);
                    Marshal.WriteByte(mask, 16, (byte)prefix.PrefixLength);
                    var conditions = new[] { Condition(CondIpRemoteAddress, MatchEqual, FwpV6AddrMask, (ulong)mask) };
                    foreach (var layer in new[] { LayerAleAuthConnectV6, LayerAleAuthRecvAcceptV6 })
                    {
                        var result = AddRaw(engine, key, layer, WeightBlockList, ActionBlock, 0, conditions, $"Block IPv6 {prefix}", out _);
                        if (result != 0)
                        {
                            throw new InvalidOperationException($"Windows could not block IPv6 prefix {prefix} (0x{result:X8}).");
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(mask);
            }

            var commit = FwpmTransactionCommit0(engine);
            if (commit != 0)
            {
                throw new InvalidOperationException($"Windows could not activate the IPv6 block policy (0x{commit:X8}).");
            }

            batched = false;
            logger.LogDebug("[FIX] Installed {Count} explicit IPv6 block prefix(es)", prefixes.Length);
            return dynamicSession;
        }
        catch
        {
            if (batched)
            {
                FwpmTransactionAbort0(engine);
            }

            dynamicSession.Dispose();
            throw;
        }
    }

    private sealed class Ipv6BlockSession : SafeHandle
    {
        public Ipv6BlockSession(IntPtr engine) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(engine);

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle() => FwpmEngineClose0(handle) == 0;
    }

    // These filters are above app rules and below explicit address blocks.
    internal void SetPolicyNameVerdict(IPAddress address, RouteVerdict verdict, bool blocksApps, int ttlSeconds)
    {
        lock (_gate)
        {
            _namePermits.TryGetValue(address, out var old);
            if (verdict != RouteVerdict.Proxy && (verdict != RouteVerdict.Direct || !blocksApps))
            {
                if (old is not null)
                {
                    DeleteByIdLocked(old.Filter);
                    _namePermits.Remove(address);
                }

                return;
            }

            var expires = Environment.TickCount64 + Math.Max(1, ttlSeconds) * 1000L;
            if (old is not null && old.Verdict == verdict && old.Filter != 0)
            {
                _namePermits[address] = old with { Expires = expires };
                return;
            }

            if (old is not null)
            {
                DeleteByIdLocked(old.Filter);
            }

            var filter = AddNamePermitLocked(address, verdict);
            _namePermits[address] = new NamePermit(verdict, expires, filter);
        }
    }

    internal void RefreshPolicyNamePermits()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            foreach (var (address, rule) in _namePermits.ToArray())
            {
                if (rule.Expires <= now)
                {
                    DeleteByIdLocked(rule.Filter);
                    _namePermits.Remove(address);
                }
                else if (rule.Filter == 0 && _engine != IntPtr.Zero)
                {
                    _namePermits[address] = rule with { Filter = AddNamePermitLocked(address, rule.Verdict) };
                }
            }
        }
    }

    internal bool BlockPolicyApplications(IReadOnlyList<string> images)
    {
        lock (_gate)
        {
            foreach (var id in _blockedApps)
            {
                DeleteByIdLocked(id);
            }

            _blockedApps.Clear();
            foreach (var image in images)
            {
                if (_engine == IntPtr.Zero || FwpmGetAppIdFromFileName0(image, out var appId) != 0 || appId == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    var conditions = new[] { Condition(CondAleAppId, MatchEqual, FwpByteBlobType, (ulong)appId) };
                    foreach (var layer in new[] { LayerAleAuthConnectV4, LayerAleAuthConnectV6 })
                    {
                        if (AddRaw(_engine, layer, WeightOffTunnel, ActionBlock, 0, conditions, "Block a routed application", out var id) != 0)
                        {
                            return false;
                        }

                        _blockedApps.Add(id);
                    }
                }
                finally
                {
                    FwpmFreeMemory0(ref appId);
                }
            }

            return true;
        }
    }

    private ulong AddNamePermitLocked(IPAddress address, RouteVerdict verdict)
    {
        if (_engine == IntPtr.Zero || _tunnelLuid == 0)
        {
            return 0;
        }

        var ipv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        var layer = ipv6 ? LayerAleAuthConnectV6 : LayerAleAuthConnectV4;
        var mask = Marshal.AllocHGlobal(ipv6 ? 17 : 8);
        var luid = Marshal.AllocHGlobal(sizeof(ulong));
        try
        {
            if (ipv6)
            {
                Marshal.Copy(address.GetAddressBytes(), 0, mask, 16);
                Marshal.WriteByte(mask, 16, 128);
            }
            else if (GeoIpRanges.TryToNumeric(address, out var numeric))
            {
                Marshal.WriteInt32(mask, unchecked((int)numeric));
                Marshal.WriteInt32(mask, sizeof(uint), -1);
            }
            else
            {
                return 0;
            }

            var remote = Condition(CondIpRemoteAddress, MatchEqual, ipv6 ? FwpV6AddrMask : FwpV4AddrMask, (ulong)mask);
            if (verdict == RouteVerdict.Direct)
            {
                return AddRaw(_engine, layer, WeightThroughTunnel, ActionPermit, 0, [remote], "Allow a direct domain before application rules", out var direct) == 0 ? direct : 0;
            }

            Marshal.WriteInt64(luid, (long)_tunnelLuid);
            var adapter = Condition(CondIpLocalInterface, MatchEqual, FwpUint64, (ulong)luid);
            return AddRaw(_engine, layer, WeightThroughTunnel, ActionPermit, 0, [remote, adapter], "Allow a tunneled domain before application rules", out var proxy) == 0 ? proxy : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(mask);
            Marshal.FreeHGlobal(luid);
        }
    }
}
