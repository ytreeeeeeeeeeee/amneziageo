using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Geo;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Answers the machine's lookups and decides per name, and per address the answer carries, whether the
/// destination goes through the tunnel or stays on the physical path.
/// </summary>
internal sealed class DnsRouter : IDisposable
{
    private const int Port = 53;
    private const int UpstreamTimeoutMs = 4000;
    private const int ConnectionIdleMs = 15000;
    private const int BufferSize = 4096;
    private const int TcpBacklog = 16;
    private const int MaxMessageLength = 65535;

    // Addresses kept with the name that answered them; past this the whole set is dropped and filled again.
    private const int MaxNames = 8192;

    private readonly bool _split;
    private readonly bool _stripV6;
    private volatile NameRules _rules;
    private readonly object _ruleGate = new();
    private readonly LinuxNameRoutes _nameRoutes = new();
    private readonly AppTunnel? _nameSteering;
    private readonly string? _endpoint;
    private readonly IReadOnlyList<IPAddress> _tunnelResolvers;
    private volatile IReadOnlyList<IPAddress> _lanResolvers;
    // The resolvers another VPN or network answers the names under its suffixes with, the longest suffix first.
    private volatile IReadOnlyList<(string Suffix, IReadOnlyList<IPAddress> Pool)> _suffixPools = [];
    // Whether the names through the tunnel are left to the network of the machine, asked by the routing table.
    private readonly bool _network;
    private readonly RoutingCache _routes;
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);

    // Names a block rule refused, which no address of theirs is ever held for.
    private readonly RefusedNames _refused = new();

    // How the resolvers behind the tunnel are asked, and the transport that answered last.
    private readonly NameUpstream _tunnelAsk;
    private readonly DohResolver? _doh;
    private readonly AgentLog _log;
    private readonly CancellationTokenSource _cts = new();
    private Socket? _udp;
    private Socket? _tcp;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public DnsRouter(TunnelRouting routing, bool stripV6, IReadOnlyList<IPAddress> tunnelResolvers, IReadOnlyList<IPAddress> lanResolvers, RoutingCache routes, AgentLog log, string dnsTransport = AmneziaGeo.Ipc.DnsTransports.Auto, bool network = false, AppTunnel? nameSteering = null, string? endpoint = null)
    {
        _split = routing.Split;
        _stripV6 = stripV6;
        _rules = new NameRules(routing.ProxyDomains, routing.DirectDomains, routing.BlockDomains);
        _tunnelResolvers = tunnelResolvers;
        _lanResolvers = lanResolvers;
        _network = network;
        _nameSteering = nameSteering;
        _endpoint = endpoint;
        _routes = routes;
        _log = log;
        _doh = AmneziaGeo.Ipc.DnsTransports.Of(dnsTransport) == AmneziaGeo.Ipc.DnsTransports.Plain
            ? null
            : new DohResolver(DohResolver.DefaultUrl, DohResolver.DefaultAddress, TimeSpan.FromMilliseconds(UpstreamTimeoutMs * 2));
        _tunnelAsk = new NameUpstream(
            dnsTransport,
            (query, ct) => DemandAsync(query, overTcp: false, ct),
            (query, ct) => DemandAsync(query, overTcp: true, ct),
            _doh is null ? null : (query, ct) => _doh.AskAsync(query, ct),
            line => _log.Info("dns", line));
    }

    /// <summary>
    /// Address the router listens on.
    /// </summary>
    public static IPAddress Listen { get; } = IPAddress.Parse("127.0.0.71");

    public bool CanRouteDomains => _udp is not null && _tcp is not null;

    /// <summary>
    /// The name that last answered with the address, empty when none did.
    /// </summary>
    public string NameOf(string address)
    {
        return _names.GetValueOrDefault(address, string.Empty);
    }

    /// <summary>
    /// The names a block rule refused no longer ago than the window, each with the seconds since its refusal.
    /// </summary>
    public IReadOnlyList<(string Name, int IdleSeconds)> Refused(int ttlSeconds)
    {
        return _refused.Fresh(ttlSeconds);
    }

    /// <summary>
    /// The verdict the name rules give an address by the name it was resolved from; None when no name settles it.
    /// </summary>
    public RouteVerdict VerdictOf(IPAddress address)
    {
        lock (_ruleGate)
        {
            return _nameSteering is null ? _nameRoutes.VerdictOf(address, _rules) : RouteVerdict.None;
        }
    }

    /// <summary>
    /// Takes the edited rules over; the next lookup is answered by them.
    /// </summary>
    public bool ApplyRules(TunnelRouting routing)
    {
        lock (_ruleGate)
        {
            var next = new NameRules(routing.ProxyDomains, routing.DirectDomains, routing.BlockDomains);
            if (!ApplyNameRoutes(next))
            {
                return false;
            }

            _rules = next;
            _log.Debug("dns", $"name rules reloaded: {routing.ProxyDomains.Count} tunneled, {routing.DirectDomains.Count} direct, {routing.BlockDomains.Count} refused");
            return true;
        }
    }

    /// <summary>
    /// Takes the resolvers of the network of the machine as they stand now.
    /// </summary>
    public void UpdateLan(IReadOnlyList<IPAddress> lanResolvers)
    {
        _lanResolvers = lanResolvers;
    }

    /// <summary>
    /// Takes the resolvers another VPN or network answers the names under its suffixes with; returns whether they moved.
    /// </summary>
    public bool UpdateSuffixPools(IReadOnlyList<(string Suffix, IReadOnlyList<IPAddress> Pool)> pools)
    {
        var sorted = SuffixPools.Normalize(pools);
        if (SuffixPools.Same(_suffixPools, sorted))
        {
            return false;
        }

        _suffixPools = sorted;
        return true;
    }

    /// <summary>
    /// The resolvers that answer the names under the suffix the name falls under; null where no suffix claims it.
    /// </summary>
    internal IReadOnlyList<IPAddress>? PoolOf(string name)
    {
        return SuffixPools.PoolOf(_suffixPools, name);
    }

    /// <summary>
    /// Binds the listeners and starts serving; false when the address is taken.
    /// </summary>
    public bool Start()
    {
        try
        {
            var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(Listen, Port));
            _udp = udp;
        }
        catch (SocketException ex)
        {
            _log.Error("dns", $"binding {Listen}:{Port} failed", ex);
            return false;
        }

        // A client whose answer did not fit a datagram asks the same question again over TCP.
        try
        {
            var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            tcp.Bind(new IPEndPoint(Listen, Port));
            tcp.Listen(TcpBacklog);
            _tcp = tcp;
            _ = Task.Run(() => ServeTcpAsync(_cts.Token));
        }
        catch (SocketException ex)
        {
            _log.Warn("dns", $"listening on {Listen}:{Port} over TCP failed, so a truncated answer has nowhere to go: {ex.Message}");
        }

        _ = Task.Run(() => ServeUdpAsync(_cts.Token));
        if (_nameSteering is not null)
        {
            _ = SweepNameRoutesAsync(_cts.Token);
        }
        var ranges = _routes.RangeCounts;
        _log.Info("dns", $"name router on {Listen}:{Port}, {ranges.Proxy} proxy range(s), {ranges.Direct} direct range(s), {ranges.Block} blocked range(s){(_stripV6 ? ", addresses over IPv6 withheld from tunneled names" : string.Empty)}");
        return true;
    }

    // Receives queries and answers each one without holding up the next.
    private async Task ServeUdpAsync(CancellationToken ct)
    {
        var socket = _udp;
        if (socket is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var buffer = new byte[BufferSize];
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
                _ = Task.Run(() => AnswerDatagramAsync(socket, buffer, received.ReceivedBytes, received.RemoteEndPoint, ct), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log.Warn("dns", $"receive failed: {ex.Message}");
            }
        }
    }

    private async Task AnswerDatagramAsync(Socket socket, byte[] query, int length, EndPoint client, CancellationToken ct)
    {
        try
        {
            var answer = await ResolveAsync(query, length, overTcp: false, ct).ConfigureAwait(false);
            if (answer is not null)
            {
                await socket.SendToAsync(answer, SocketFlags.None, client, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("dns", "answering a lookup failed", ex);
        }
    }

    // Accepts the connections a client falls back to, serving every query it sends over one.
    private async Task ServeTcpAsync(CancellationToken ct)
    {
        var listener = _tcp;
        if (listener is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => AnswerStreamAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log.Warn("dns", $"accepting a connection failed: {ex.Message}");
            }
        }
    }

    private async Task AnswerStreamAsync(Socket client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(ConnectionIdleMs);
                    var query = await ReadFramedAsync(client, idle.Token).ConfigureAwait(false);
                    if (query is null)
                    {
                        return;
                    }

                    var answer = await ResolveAsync(query, query.Length, overTcp: true, ct).ConfigureAwait(false);
                    if (answer is null)
                    {
                        return;
                    }

                    await WriteFramedAsync(client, answer, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("dns", "answering a lookup over TCP failed", ex);
        }
    }

    // Decides the query, forwards it to the resolver that fits, and routes what the answer resolved to.
    private async Task<byte[]?> ResolveAsync(byte[] query, int length, bool overTcp, CancellationToken ct)
    {
        if (!DnsWire.TryReadQuestion(query, length, out var name, out var type))
        {
            return null;
        }

        var verdict = Decide(name);
        if (verdict == RouteVerdict.Block)
        {
            _refused.Note(name);

            return DnsWire.BuildRefusal(query, length);
        }

        // The tunnel carries no IPv6 here, so an address over it would take the destination around the tunnel
        // on a path the rules do not reach.
        if (type == DnsWire.TypeAaaa && WithholdsV6(verdict))
        {
            return DnsWire.BuildEmpty(query, length);
        }

        // A name under the suffix of another VPN or network is asked where that one points.
        var own = PoolOf(name);
        var upstream = verdict switch
        {
            RouteVerdict.Proxy => _tunnelResolvers,
            RouteVerdict.Direct => own ?? _lanResolvers,
            _ => own ?? (_split || IsLocalName(name) ? _lanResolvers : _tunnelResolvers),
        };

        // The network of the machine answers the names through the tunnel where the config leaves them to it.
        var answer = ReferenceEquals(upstream, _tunnelResolvers) && !_network
            ? await AskThroughTunnelAsync(query, length, overTcp, ct).ConfigureAwait(false)
            : await AskAsync(upstream, query, length, overTcp, ct).ConfigureAwait(false);
        if (answer is null)
        {
            return null;
        }

        lock (_ruleGate)
        {
            if (_disposed || Decide(name) != verdict)
            {
                return null;
            }

            return ApplyAnswer(name, verdict, answer) ? DnsWire.BuildRefusal(query, length) : answer;
        }
    }

    // A name of a single label, which only the machine's own network answers.
    private bool IsLocalName(string name) => _lanResolvers.Count > 0 && !name.TrimEnd('.').Contains('.', StringComparison.Ordinal);

    // Everything but a direct destination leaves the rules behind over IPv6 in a full tunnel; in a split only a
    // tunneled one does.
    private bool WithholdsV6(RouteVerdict verdict) =>
        _stripV6 && (verdict == RouteVerdict.Proxy || (!_split && verdict != RouteVerdict.Direct));

    // Hands the addresses the answer carries to the cache, which installs what each one's verdict asks for; true
    // when the destination is blocked and must not be answered. A name that matched a rule decides for its
    // addresses, and the rest are left to the ranges.
    private bool ApplyAnswer(string name, RouteVerdict verdict, byte[] answer)
    {
        var addresses = DnsWire.ReadAddresses(answer, answer.Length)
            .Where(record => record.Address.AddressFamily == AddressFamily.InterNetwork).ToArray();
        if (addresses.Any(record => _routes.Classify(record.Address) == RouteVerdict.Block))
        {
            return true;
        }

        var now = Environment.TickCount64;
        _nameRoutes.Expire(now);
        foreach (var (address, ttl) in addresses)
        {
            if (!_nameRoutes.Observe(address, name, ttl, now))
            {
                _log.Warn("dns", "the DNS routing cache is full; the answer was withheld");
                return true;
            }

            if (_nameSteering is null)
            {
                var inferred = _nameRoutes.VerdictOf(address, _rules);
                _routes.NoteDns(address, inferred == RouteVerdict.None ? [] : [inferred]);
            }

            Remember(name, address.ToString());
        }

        if (_nameSteering is not null && !ApplyNameRoutes(_rules))
        {
            _log.Error("dns", "netfilter did not install DNS routes; the answer was withheld");
            return true;
        }

        _log.Debug("dns", $"applied {addresses.Length} DNS address(es), domain verdict {verdict}");
        return false;
    }

    private bool ApplyNameRoutes(NameRules rules)
    {
        if (_nameSteering is null)
        {
            return true;
        }

        var kept = _endpoint is null ? _routes.PinnedRoutes : [.. _routes.PinnedRoutes, _endpoint];
        var pinned = GeoIpRanges.Build(kept);
        return _nameRoutes.Apply(rules, _nameSteering.ReloadNames,
            address => GeoIpRanges.TryToNumeric(address, out var value) && pinned.Contains(value));
    }

    private async Task SweepNameRoutesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            var pending = false;
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                lock (_ruleGate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    if (_nameRoutes.Expire(Environment.TickCount64) || pending)
                    {
                        var wasPending = pending;
                        pending = !ApplyNameRoutes(_rules);
                        if (pending && !wasPending)
                        {
                            _log.Error("dns", "netfilter could not withdraw expired DNS routes; retrying");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    // Keeps the name an address came with, dropping the whole set once it has grown past what a journal needs.
    private void Remember(string name, string address)
    {
        if (_names.Count >= MaxNames)
        {
            _names.Clear();
        }

        _names[address] = name;
    }

    // The narrowest rule that holds the name decides it.
    private RouteVerdict Decide(string name) => _rules.Verdict(name);

    /// <summary>
    /// The transport the resolvers behind the tunnel are reached over, and why that one.
    /// </summary>
    public string NameTransport => _tunnelAsk.State;

    // The resolvers behind the tunnel, asked over the transport the setting allows. A client that came over TCP came
    // for the whole answer, which plain DNS would cut to a datagram again.
    private async Task<byte[]?> AskThroughTunnelAsync(byte[] query, int length, bool overTcp, CancellationToken ct)
    {
        try
        {
            var asked = length == query.Length ? query : query[..length];
            return overTcp
                ? await _tunnelAsk.AskWholeAsync(asked, ct).ConfigureAwait(false)
                : await _tunnelAsk.AskAsync(asked, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Warn("dns", $"no resolver behind the tunnel answered: {ex.Message}");
            return null;
        }
    }

    // One query to the resolvers behind the tunnel; silence is raised, so the ladder steps on to the next transport.
    private async Task<byte[]> DemandAsync(byte[] query, bool overTcp, CancellationToken ct)
    {
        var answer = await AskAsync(_tunnelResolvers, query, query.Length, overTcp, ct).ConfigureAwait(false);
        return answer ?? throw new IOException(overTcp
            ? "no resolver behind the tunnel answered over tcp"
            : "no resolver behind the tunnel answered over udp");
    }

    // Asks each resolver in turn and returns the first answer.
    private async Task<byte[]?> AskAsync(IReadOnlyList<IPAddress> servers, byte[] query, int length, bool overTcp, CancellationToken ct)
    {
        foreach (var server in servers)
        {
            var answer = overTcp
                ? await AskOverTcpAsync(server, query, length, ct).ConfigureAwait(false)
                : await AskOverUdpAsync(server, query, length, ct).ConfigureAwait(false);
            if (answer is not null)
            {
                return answer;
            }
        }

        return null;
    }

    private async Task<byte[]?> AskOverUdpAsync(IPAddress server, byte[] query, int length, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            await socket.ConnectAsync(new IPEndPoint(server, Port), ct).ConfigureAwait(false);
            await socket.SendAsync(query.AsMemory(0, length), SocketFlags.None, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(UpstreamTimeoutMs);
            var buffer = new byte[BufferSize];
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            return read <= 0 ? null : buffer[..read];
        }
        catch (OperationCanceledException)
        {
            _log.Warn("dns", $"resolver {server} did not answer in time");
            return null;
        }
        catch (SocketException ex)
        {
            _log.Warn("dns", $"resolver {server} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<byte[]?> AskOverTcpAsync(IPAddress server, byte[] query, int length, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(UpstreamTimeoutMs);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(server, Port), timeout.Token).ConfigureAwait(false);
            await WriteFramedAsync(socket, length == query.Length ? query : query[..length], timeout.Token).ConfigureAwait(false);
            return await ReadFramedAsync(socket, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("dns", $"resolver {server} did not answer over TCP in time");
            return null;
        }
        catch (SocketException ex)
        {
            _log.Warn("dns", $"resolver {server} failed over TCP: {ex.Message}");
            return null;
        }
    }

    // Reads one length-prefixed message; null when the peer closed the connection.
    private static async Task<byte[]?> ReadFramedAsync(Socket socket, CancellationToken ct)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(socket, header, ct).ConfigureAwait(false))
        {
            return null;
        }

        var length = (header[0] << 8) | header[1];
        if (length is 0 or > MaxMessageLength)
        {
            return null;
        }

        var message = new byte[length];
        return await ReadExactAsync(socket, message, ct).ConfigureAwait(false) ? message : null;
    }

    private static async Task<bool> ReadExactAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(filled), SocketFlags.None, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            filled += read;
        }

        return true;
    }

    private static async Task WriteFramedAsync(Socket socket, byte[] message, CancellationToken ct)
    {
        var framed = new byte[message.Length + 2];
        framed[0] = (byte)(message.Length >> 8);
        framed[1] = (byte)message.Length;
        message.CopyTo(framed, 2);
        await socket.SendAsync(framed, SocketFlags.None, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_ruleGate)
        {
            _disposed = true;
        }
        _cts.Cancel();
        _udp?.Dispose();
        _tcp?.Dispose();
        _cts.Dispose();
    }
}
