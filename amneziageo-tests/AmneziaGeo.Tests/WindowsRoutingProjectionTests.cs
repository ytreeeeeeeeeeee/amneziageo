using System.Net;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Routing;
using AmneziaGeo.Windows.App;
using AmneziaGeo.Windows.App.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class WindowsRoutingProjectionTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-projection-{Guid.NewGuid():N}.db");
    private readonly TestFiles _files = new();
    private SqliteStateStore _store = null!;
    private GeoConfigurator _geo = null!;

    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        _geo = new GeoConfigurator(_store, _files);
        await _store.InitializeAsync();
    }

    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RunningProjectionKeepsRulesModeAndSelectionUntilStartupPublishesAnother()
    {
        var proxy = await AddAsync("Proxy", "proxy|cidr:203.0.113.0/24");
        var bypass = await AddAsync("Bypass", "direct|cidr:203.0.113.0/25");
        await _store.SetRoutingSettingsAsync(new RoutingSettings(bypass, string.Empty, true));
        await _store.UpdateSelectedRoutingListsAsync(_ => [bypass, proxy]);
        var roster = new TunnelDutyRoster();

        await PublishAsync(roster);
        var before = (await _store.GetTunnelRoutingProjectionAsync("alpha"))!;
        Assert.Equal([proxy, bypass], before.ListIds);
        Assert.True(before.Settings?.AllUdp);
        Assert.True(before.Split);
        Assert.Equal(["203.0.113.128/25"], before.NamedProxyRoutes);

        await _geo.ApplyToRoutingListAsync(proxy, "Proxy", ["proxy|cidr:192.0.2.0/24"]);
        await _store.SetRoutingSettingsAsync(new RoutingSettings(proxy, string.Empty, false, "full", true));
        await _store.RemoveRoutingListAsync(bypass);
        var next = await BuildAsync(roster, "alpha");

        Assert.Equal(before.Stamp, (await _store.GetTunnelRoutingProjectionAsync("alpha"))!.Stamp);
        Assert.Equal(["203.0.113.128/25"], (await _store.GetActiveTunnelGeoAsync("alpha"))!.Routes);
        Assert.Equal([proxy], next.ListIds);
        Assert.False(next.Split);
        Assert.NotEqual(before.InputStamp, next.InputStamp);

        await PublishAsync(roster);
        Assert.Equal(next.Stamp, (await _store.GetTunnelRoutingProjectionAsync("alpha"))!.Stamp);
        await _store.ClearTunnelProjectionAsync("alpha");
        Assert.Null(await _store.GetTunnelRoutingProjectionAsync("alpha"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshForRestartPublishesTheNewPolicyOnlyIfPrepared(bool prepared)
    {
        const string config = "alpha";
        var roster = new TunnelDutyRoster();
        var id = await AddAsync("Rules", "proxy|cidr:203.0.113.0/24");
        await _store.UpdateSelectedRoutingListsAsync(_ => [id]);
        await PublishAsync(roster);
        var before = (await _store.GetTunnelRoutingProjectionAsync(config))!;

        await _geo.ApplyToRoutingListAsync(id, "Rules", ["block|cidr:203.0.113.0/24"]);
        var refreshed = await RoutingProjection.RefreshAsync(_store, config,
            async (name, ct) => prepared
                ? await RoutingProjection.BuildAsync(_store, _geo, roster, name, NullLogger.Instance, ct)
                : null,
            NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(prepared, refreshed);
        var after = (await _store.GetTunnelRoutingProjectionAsync(config))!;
        if (prepared)
        {
            Assert.NotEqual(before.Stamp, after.Stamp);
            Assert.Empty(after.List.Routes);
            Assert.Equal(["203.0.113.0/24"], after.List.BlockRoutes);
            Assert.Equal(after.List.BlockRoutes, (await _store.GetActiveTunnelGeoAsync(config))!.BlockRoutes);
        }
        else
        {
            Assert.Equal(before.Stamp, after.Stamp);
        }
    }

    [Fact]
    public async Task FleetUsesOriginalKeysForEquivalentPathsAndRanksExplicitThenFallbackThenAutomatic()
    {
        const string upper = @"app:path=C:\Tools\Browser.exe";
        const string lower = @"app:path=c:\tools\BROWSER.exe";
        var first = await AddAsync("First", "proxy|" + upper);
        var second = await AddAsync("Second", "proxy|" + lower);
        await _store.UpdateSelectedRoutingListsAsync(_ => [second, first]);

        var fleet = new FleetControl(new FleetLive());
        fleet.SetRole("alpha", TunnelRoles.Primary);
        fleet.SetRole("bravo", TunnelRoles.Reserve);
        fleet.Add("alpha");
        fleet.Add("bravo");

        fleet.SetTarget(FleetTargets.Key(first, upper), new RuleRoute(RuleTarget.Parse("missing"), RuleTarget.Parse("bravo")));
        fleet.SetTarget(FleetTargets.Key(second, lower), new RuleRoute(RuleTarget.Parse("alpha"), RuleTarget.Default));
        Assert.Single((await BuildAsync(fleet, "alpha")).List.Apps);
        Assert.Empty((await BuildAsync(fleet, "bravo")).List.Apps);

        fleet.SetTarget(FleetTargets.Key(first, upper), new RuleRoute(RuleTarget.Parse("bravo"), RuleTarget.Default));
        Assert.Empty((await BuildAsync(fleet, "alpha")).List.Apps);
        Assert.Single((await BuildAsync(fleet, "bravo")).List.Apps);

        fleet.SetTarget(FleetTargets.Key(first, upper), new RuleRoute(RuleTarget.Parse("missing"), RuleTarget.Parse("block")));
        fleet.SetTarget(FleetTargets.Key(second, lower), RuleRoute.Default);
        var blocked = await BuildAsync(fleet, "alpha");
        Assert.Empty(blocked.List.Apps);
        Assert.Empty((await BuildAsync(fleet, "bravo")).List.Apps);
        Assert.Single(blocked.BlockApps);
    }

    [Theory]
    [InlineData("proxy|app:path=C:\\Tools\\Vpn.exe", false, false)]
    [InlineData("proxy|domain:vpn.example.test", false, false)]
    [InlineData("proxy|cidr:203.0.113.0/24", false, false)]
    [InlineData("direct|domain:local.example.test", true, false)]
    [InlineData("direct|domain:local.example.test", false, true)]
    public async Task DirectAppsCannotExposeAnotherApplicationsTrafficOnASharedAddress(
        string rule, bool full, bool allUdp)
    {
        var direct = await AddAsync("Direct", @"direct|app:path=C:ToolsDirect.exe");
        var other = await AddAsync("Other", rule);
        await _store.UpdateSelectedRoutingListsAsync(_ => [direct]);
        var fleet = new FleetControl(new FleetLive());
        fleet.SetRole("alpha", TunnelRoles.Primary);
        fleet.SetRole("bravo", TunnelRoles.Reserve);
        fleet.Add("alpha");
        fleet.Add("bravo");
        if (rule.StartsWith("proxy|", StringComparison.Ordinal))
        {
            fleet.SetTarget(FleetTargets.Key(other, rule[6..]),
                new RuleRoute(RuleTarget.Parse("bravo"), RuleTarget.Default));
        }

        await PublishAsync(fleet);
        var applied = (await _store.GetTunnelRoutingProjectionAsync("alpha"))!;
        Assert.Single(applied.List.DirectApps);

        await _store.SetRoutingSettingsAsync(new RoutingSettings(other, string.Empty, allUdp, UseGlobalProxy: full));
        await _store.UpdateSelectedRoutingListsAsync(_ => [direct, other]);
        var failure = await Assert.ThrowsAsync<NotSupportedException>(() => PublishAsync(fleet));
        Assert.Contains("share a destination IP", failure.Message);
        Assert.Equal(applied.Stamp, (await _store.GetTunnelRoutingProjectionAsync("alpha"))!.Stamp);
    }

    [Fact]
    public async Task FleetCarvesAStaticRangeAroundTheNarrowerRangeOwnedByAnotherTunnel()
    {
        var broad = await AddAsync("Broad", "proxy|cidr:203.0.113.0/24");
        var narrow = await AddAsync("Narrow", "proxy|cidr:203.0.113.0/25");
        await _store.UpdateSelectedRoutingListsAsync(_ => [broad, narrow]);
        var fleet = new FleetControl(new FleetLive());
        fleet.SetRole("alpha", TunnelRoles.Primary);
        fleet.SetRole("bravo", TunnelRoles.Reserve);
        fleet.Add("alpha");
        fleet.Add("bravo");
        fleet.SetTarget(FleetTargets.Key(narrow, "cidr:203.0.113.0/25"), new RuleRoute(RuleTarget.Parse("bravo"), RuleTarget.Default));

        Assert.Equal(["203.0.113.128/25"], (await BuildAsync(fleet, "alpha")).NamedProxyRoutes);
        Assert.Equal(["203.0.113.0/25"], (await BuildAsync(fleet, "bravo")).NamedProxyRoutes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupFragmentsAreReadFromTheSameVersionAfterAGeoFileChanges(bool removeFile)
    {
        await _store.SaveGeoSourceAsync(new GeoSource("ip", "geoip", "https://example.test/ip.dat", 1));
        await _store.SaveGeoFileAsync(new GeoFileMetadata("ip", "https://example.test/ip.dat", DateTimeOffset.UnixEpoch, "seen", 1));
        _files.GeoIp = IpFile(192);
        var cidr = await AddAsync("Explicit network", "proxy|cidr:192.0.2.0/24");
        var geo = await AddAsync("Narrow geodata", "direct|geoip:zz");
        await _store.UpdateSelectedRoutingListsAsync(_ => [cidr, geo]);
        var roster = new TunnelDutyRoster();
        await PublishAsync(roster);

        _files.GeoIp = removeFile ? null : IpFile(198);
        var applied = (await _store.GetTunnelRoutingProjectionAsync("alpha"))!;
        var standing = StandingRanges.Of(applied.List.Routes, applied.List.Rules, true, false, [], [],
            applied.NamedProxyRoutes, applied.NamedOffTunnelRoutes);
        Assert.Equal(["192.0.2.128/25"], standing.Named);

        if (removeFile)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => PublishAsync(roster));
            Assert.Equal(applied.Stamp, (await _store.GetTunnelRoutingProjectionAsync("alpha"))!.Stamp);
        }
        else
        {
            Assert.Equal(["192.0.2.0/24"], (await BuildAsync(roster, "alpha")).NamedProxyRoutes);
        }
    }

    [Fact]
    public async Task FoldedIpv6FragmentsAndDomainMemoryAreSavedWithTheirVersion()
    {
        var proxy = await AddAsync("IPv6 proxy", "proxy|cidr:2001:db8::/32");
        var direct = await AddAsync("IPv6 direct", "direct|cidr:2001:db8::/33");
        await _store.UpdateSelectedRoutingListsAsync(_ => [proxy, direct]);
        var roster = new TunnelDutyRoster();
        await PublishAsync(roster);

        var first = (await _store.GetTunnelRoutingProjectionAsync("alpha"))!;
        Assert.Equal(["2001:db8:8000::/33"], first.NamedProxyRoutes);
        await _store.SaveDomainResolutionAsync("alpha", new DomainResolution("host.test", ["2001:db8:8000::7"], PolicyStamp: first.Stamp), 0);

        await _store.UpdateSelectedRoutingListsAsync(_ => [proxy]);
        await PublishAsync(roster);

        var next = (await _store.GetTunnelRoutingProjectionAsync("alpha"))!;
        Assert.Equal(["2001:db8::/32"], next.NamedProxyRoutes);
        Assert.Equal(first.Stamp, (await _store.GetDomainResolutionAsync("alpha", "host.test"))!.PolicyStamp);
        Assert.NotEqual(first.Stamp, next.Stamp);
    }

    [Fact]
    public void ConflictingDnsNamesFallBackToTheApplicationUntilTheOldEvidenceExpires()
    {
        var names = new DnsRouteNames();
        var address = IPAddress.Parse("192.0.2.5");
        var first = names.Observe(address, "proxy.test", RouteVerdict.Proxy, 0, 5);
        Assert.Equal(RouteVerdict.Proxy, RouteDecision.Infer(first, out _));

        var shared = names.Observe(address, "direct.test", RouteVerdict.Direct, 1000, 10);
        var inferred = RouteDecision.Infer(shared, out var conflict);
        Assert.True(conflict);
        Assert.Equal(RouteDecisionSource.Application,
            RouteDecision.Resolve(RouteVerdict.None, RouteVerdict.None, inferred, RouteVerdict.Proxy).Source);

        var expired = names.Observe(address, "direct.test", RouteVerdict.Direct, 5000, 10);
        Assert.Equal(RouteVerdict.Direct, RouteDecision.Infer(expired, out _));
    }

    private Task<long> AddAsync(string name, params string[] tokens) => _geo.ApplyToRoutingListAsync(0, name, tokens);

    private Task<TunnelRoutingProjection> BuildAsync(TunnelDutyRoster roster, string config) =>
        RoutingProjection.BuildAsync(_store, _geo, roster, config, NullLogger.Instance, CancellationToken.None);

    private async Task PublishAsync(TunnelDutyRoster roster)
    {
        var projection = await BuildAsync(roster, "alpha");
        await RoutingProjection.SaveAsync(_store, "alpha", projection, NullLogger.Instance, CancellationToken.None);
    }

    private static byte[] IpFile(byte firstOctet) =>
        [0x0a, 0x0e, 0x0a, 0x02, (byte)'Z', (byte)'Z', 0x12, 0x08, 0x0a, 0x04, firstOctet, 0, 2, 0, 0x10, 25];

    private sealed class TestFiles : IGeoFileStore
    {
        public byte[]? GeoIp { get; set; }
        public byte[]? Read(string name) => name == "ip" ? GeoIp : null;
        public Stream? OpenRead(string name) => Read(name) is { } bytes ? new MemoryStream(bytes) : null;
        public Task WriteAsync(string name, byte[] data, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
