using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Where a session handed over by the gateway goes. The pair it was opened for names the program behind it, so a
/// rule on that program decides the whole session, and nothing of another program goes with it. The program
/// follows a recognized name, while an explicit address block stays in force. The way out is
/// pinned to an interface rather than left to the routing table, or a session meant to leave the tunnel would come
/// back into the adapter it was terminated on.
/// </summary>
internal sealed class GatewayProxyOutbound(
    Func<uint, bool?>? owner,
    bool unknownRidesTunnel,
    GeoIpRanges proxy,
    GeoIpRanges direct,
    GeoIpRanges block,
    Func<uint> tunnelInterface,
    Func<uint> physicalInterface,
    ILogger logger,
    Func<string, RouteVerdict>? nameRule = null,
    Func<IPAddress, RouteVerdict>? dnsRule = null) : IProxyOutbound, IDatagramOutbound
{
    private const int ConnectTimeoutMs = 8000;

    /// <inheritdoc/>
    public Task<(IProxyLink? Link, ProxyOutcome Outcome)> ConnectAsync(string host, int port, CancellationToken ct) =>
        ConnectAsync(host, port, null, ct);

    /// <inheritdoc/>
    public async Task<(IProxyLink? Link, ProxyOutcome Outcome)> ConnectAsync(string host, int port, IPEndPoint? source, CancellationToken ct)
    {
        var addresses = await ResolveAsync(host, ct).ConfigureAwait(false);
        foreach (var address in addresses)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            var numeric = Numeric(address);
            var pid = source is null || owner is null ? (uint?)null : TcpTableProbe.OwnerOf(source, new IPEndPoint(address, port));
            var decision = Decide(numeric, address, IPAddress.TryParse(host, out _) ? null : host, pid);
            if (decision == RouteVerdict.Block)
            {
                return (null, ProxyOutcome.Blocked);
            }

            var tunnelled = decision == RouteVerdict.Proxy || (decision == RouteVerdict.None && Tunnelled(numeric, pid));
            var link = await OpenAsync(address, port, tunnelled, ct).ConfigureAwait(false);
            if (link is not null)
            {
                return (link, ProxyOutcome.Ok);
            }
        }

        return (null, ProxyOutcome.Failed);
    }

    // Whether a session of a program no rule names rides the tunnel: a range of the rules answers, then the policy
    // for such a program.
    private bool Tunnelled(uint address, uint? pid)
    {
        if (proxy.Contains(address))
        {
            return true;
        }

        if (direct.Contains(address) || pid is not { } id)
        {
            return false;
        }

        logger.LogDebug("nothing names the program behind pid {Pid}, so its traffic {Verdict}", id,
            unknownRidesTunnel ? "rides the tunnel" : "leaves past it");
        return unknownRidesTunnel;
    }

    // Opens the destination on the interface the verdict names.
    private async Task<IProxyLink?> OpenAsync(IPAddress address, int port, bool tunnelled, CancellationToken ct)
    {
        var index = tunnelled ? tunnelInterface() : physicalInterface();
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (index > 0)
            {
                UnicastInterface.Pin(socket, index);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeoutMs);
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token).ConfigureAwait(false);
            return new GatewayProxyLink(socket);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "the session to {Address}:{Port} did not open on interface {Index}", address, port, index);
            socket.Dispose();
            return null;
        }
    }

    /// <inheritdoc/>
    public Socket? Open(IPEndPoint? source, IPEndPoint destination)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var numeric = Numeric(destination.Address);
        var pid = source is null || owner is null ? (uint?)null : TcpTableProbe.OwnerOfDatagram(source);
        var decision = Decide(numeric, destination.Address, null, pid);
        if (decision == RouteVerdict.Block)
        {
            return null;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            var tunnelled = decision == RouteVerdict.Proxy || (decision == RouteVerdict.None && Tunnelled(numeric, pid));
            var index = tunnelled ? tunnelInterface() : physicalInterface();
            if (index > 0)
            {
                UnicastInterface.Pin(socket, index);
            }

            return socket;
        }
        catch (SocketException ex)
        {
            logger.LogDebug(ex, "the flow to {Destination} did not open", destination);
            socket.Dispose();
            return null;
        }
    }

    private RouteVerdict Decide(uint numeric, IPAddress address, string? host, uint? pid)
    {
        var range = block.Contains(numeric) ? RouteVerdict.Block
            : direct.Contains(numeric) ? RouteVerdict.Direct
            : proxy.Contains(numeric) ? RouteVerdict.Proxy : RouteVerdict.None;
        var domain = host is not null ? nameRule?.Invoke(host) ?? RouteVerdict.None : RouteVerdict.None;
        var inferred = host is null ? dnsRule?.Invoke(address) ?? RouteVerdict.None : RouteVerdict.None;
        var app = pid is { } id ? Named(id) : null;
        if (nameRule is null)
        {
            return app is true ? RouteVerdict.Proxy : app is false ? RouteVerdict.Direct : range;
        }

        return RouteDecision.Resolve(range, domain, inferred, app == true ? RouteVerdict.Proxy : RouteVerdict.None).Verdict;
    }

    // The side a rule on the program behind a pid sends it; null where no rule names the program.
    private bool? Named(uint pid)
    {
        var side = owner!(pid);
        if (side is { } known)
        {
            logger.LogDebug("pid {Pid} is of a program the rules name, so its session {Verdict} whatever its address", pid,
                known ? "rides the tunnel" : "leaves past it");
        }

        return side;
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        try
        {
            return await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static uint Numeric(IPAddress address)
    {
        var octets = address.GetAddressBytes();
        return ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
    }

    /// <summary>
    /// Socket the session runs on; the proxy counts what it carries on its own.
    /// </summary>
    private sealed class GatewayProxyLink(Socket socket) : IProxyLink
    {
        /// <inheritdoc/>
        public Socket Socket { get; } = socket;

        /// <inheritdoc/>
        public void Count(int bytes)
        {
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Socket.Dispose();
        }
    }
}
