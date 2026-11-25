using System.Net;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Из пересекающихся правил адреса решает более узкое, при равных - блок, потом «Напрямую».
/// </summary>
public sealed class RangeClaimsTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-claims-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    // Файлов гео-баз нет.
    private sealed class NoFiles : IGeoFileStore
    {
        public byte[]? Read(string name) => null;

        public Stream? OpenRead(string name) => null;

        public Task WriteAsync(string name, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_dbPath);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public void Ipv6DefaultCanBeCarvedAtBothAddressBoundaries()
    {
        const string last = "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff";
        var ranges = RangeClaims.Settle(["::/0", "192.0.2.0/24"], ["::", last], [last + "/128"]);
        var proxy = RangeClaims.Matcher(ranges.Proxy);
        var direct = RangeClaims.Matcher(ranges.Direct);
        var block = RangeClaims.Matcher(ranges.Block);

        Assert.False(proxy(IPAddress.IPv6Any));
        Assert.True(direct(IPAddress.IPv6Any));
        Assert.False(proxy(IPAddress.Parse(last)));
        Assert.False(direct(IPAddress.Parse(last)));
        Assert.True(block(IPAddress.Parse(last)));
        Assert.True(proxy(IPAddress.IPv6Loopback));
        Assert.True(proxy(IPAddress.Parse("192.0.2.3")));
    }

    [Fact]
    public void ANarrowerTunnelRange_CutsItsHoleInTheDirectRange()
    {
        var direct = GeoIpRanges.Build(RangeClaims.Settle(["10.8.2.0/24"], ["10.0.0.0/8"], []).Direct);

        Assert.False(direct.Contains(Address("10.8.2.5")));
        Assert.True(direct.Contains(Address("10.8.3.1")));
        Assert.True(direct.Contains(Address("10.0.0.1")));
        Assert.True(direct.Contains(Address("10.255.255.255")));
    }

    [Fact]
    public void AWiderTunnelRange_LeavesTheDirectRangeWhole()
    {
        Assert.Equal(["10.8.2.0/24"], RangeClaims.Settle(["10.0.0.0/8"], ["10.8.2.0/24"], []).Direct);
    }

    [Fact]
    public void TheSameRangeInBothBuckets_StaysDirect()
    {
        Assert.Equal(["192.168.1.0/24"], RangeClaims.Settle(["192.168.1.0/24"], ["192.168.1.0/24"], []).Direct);
    }

    [Fact]
    public void NestedRules_AreDecidedByTheNarrowest()
    {
        var direct = GeoIpRanges.Build(RangeClaims.Settle(["10.8.0.0/16", "10.8.2.128/25"], ["10.0.0.0/8", "10.8.2.0/24"], []).Direct);

        Assert.True(direct.Contains(Address("10.9.0.1")));
        Assert.False(direct.Contains(Address("10.8.3.5")));
        Assert.True(direct.Contains(Address("10.8.2.5")));
        Assert.False(direct.Contains(Address("10.8.2.200")));
    }

    [Fact]
    public void AHostOfTheTunnel_LeavesTheRestOfTheNetworkDirect()
    {
        var carved = RangeClaims.Settle(["1.1.1.1"], ["0.0.0.0/0"], []).Direct;

        Assert.Equal(32, carved.Count);
        Assert.False(GeoIpRanges.Build(carved).Contains(Address("1.1.1.1")));
        Assert.True(GeoIpRanges.Build(carved).Contains(Address("1.1.1.0")));
    }

    [Fact]
    public void AnEntryOfAnotherFamily_IsCarriedThrough()
    {
        Assert.Equal(["fd00::/8", "10.128.0.0/9"], RangeClaims.Settle(["10.0.0.0/9"], ["fd00::/8", "10.0.0.0/8"], []).Direct);
    }

    [Fact]
    public async Task ASavedList_CarriesTheNarrowerTunnelRangeOutOfItsDirectOne()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());

        var list = await geo.MaterializeDraftAsync(["direct|cidr:10.0.0.0/8", "proxy|cidr:10.8.2.0/24"]);

        Assert.Equal(["10.8.2.0/24"], list.Routes);
        Assert.False(GeoIpRanges.Build(list.DirectRoutes).Contains(Address("10.8.2.5")));
        Assert.True(GeoIpRanges.Build(list.DirectRoutes).Contains(Address("10.1.2.3")));
    }

    private static uint Address(string text)
    {
        Assert.True(GeoIpRanges.TryToNumeric(System.Net.IPAddress.Parse(text), out var value));
        return value;
    }
}
