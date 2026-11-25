using System.Net;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class RoutingPolicyTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"routing-policy-{Guid.NewGuid():N}");
    private readonly TestFiles _files = new();
    private SqliteStateStore _store = null!;
    private GeoConfigurator _geo = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _store = new SqliteStateStore(Path.Combine(_directory, "state.db"));
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        _geo = new GeoConfigurator(_store, _files);
    }

    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CompilesOriginalClaimsWithoutChangingTheirPriorityOrIdentity()
    {
        var broad = await AddAsync("Broad", ["proxy|cidr:10.0.0.0/8", "direct|cidr:10.1.0.0/16",
            "proxy|cidr:2001:db8::/32", "direct|cidr:2001:db8:1::/48", "proxy|domain:EXAMPLE.org."]);
        var narrower = await AddAsync("Narrower", ["direct|cidr:10.2.0.0/16", "direct|cidr:2001:db8:2::/48",
            "block|cidr:2001:db8:2::123/48", "proxy|domain:example.org"]);
        await _store.SetRoutingSettingsAsync(new RoutingSettings(broad, "lan.example; 192.0.2.0/24", true));
        await _store.SetRoutingSettingsAsync(new RoutingSettings(narrower, "lan.example", false, "full", true));
        await _store.UpdateSelectedRoutingListsAsync(_ => [narrower, broad, broad]);

        var policy = Assert.IsType<RoutingPolicy>(await _geo.LoadSelectedPolicyAsync());
        var reversed = Assert.IsType<RoutingPolicy>(await _geo.LoadPolicyAsync([narrower, broad, narrower]));
        Assert.Equal(policy.Stamp, reversed.Stamp);
        Assert.Equal([broad, narrower], policy.Sources.Select(source => source.Id));
        Assert.True(policy.Settings!.UseGlobalProxy);
        Assert.True(policy.Settings.AllUdp);
        Assert.Equal("full", policy.Settings.Mode);
        Assert.Equal(["lan.example", "192.0.2.0/24"], RoutingSettings.ExclusionTokens(policy.Settings.Exclusions));
        Assert.Equal([new GeoDomain(GeoDomainKind.Domain, "example.org", true)], policy.List.Domains);
        Assert.Equal([broad, narrower], policy.RuleOrigins.Where(origin => origin.Original.Kind == GeoRuleKind.Domain
            && origin.Original.Role == RouteRole.Proxy).Select(origin => origin.ListId));
        Assert.Equal(RouteVerdict.Direct, Decide(policy, "10.2.0.7").Verdict);
        Assert.Equal(RouteVerdict.Proxy, Decide(policy, "10.3.0.7").Verdict);
        Assert.Equal(RouteVerdict.Block, Decide(policy, "2001:db8:2::7").Verdict);
        Assert.Equal(RouteVerdict.Proxy, Decide(policy, "2001:db8:3::7").Verdict);
        Assert.Equal(RouteVerdict.Direct, Decide(policy, "192.0.2.7").Verdict);
        Assert.Equal(RouteVerdict.Direct, Decide(policy, "203.0.113.5", "lan.example").Verdict);
        Assert.Equal(SplitBudget.Weight(policy.List), policy.Weight);

        await _store.RemoveRoutingListAsync(narrower);
        var remaining = Assert.IsType<RoutingPolicy>(await _geo.LoadSelectedPolicyAsync());
        Assert.False(remaining.Settings!.UseGlobalProxy);
        Assert.True(remaining.Settings.AllUdp);
        Assert.NotEqual(policy.Stamp, remaining.Stamp);
        await _store.SetSelectedRoutingListAsync(null);
        Assert.Null(await _geo.LoadSelectedPolicyAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpposingNamesOnTheSameIpKeepTheirVerdictsAndUseAppWhenHostIsUnknown(bool v6)
    {
        var proxy = await AddAsync("Proxy", ["proxy|domain:proxy.test", "proxy|app:pkg=org.example.tunnel"]);
        var direct = await AddAsync("Direct", ["direct|domain:direct.test", "direct|app:pkg=org.example.direct",
            "block|cidr:192.0.2.10", "block|cidr:2001:db8::10"]);
        var policy = Assert.IsType<RoutingPolicy>(await _geo.LoadPolicyAsync([proxy, direct], AppRulePlatform.Android));
        var address = v6 ? "2001:db8::8" : "192.0.2.8";
        var both = new[] { "direct.test", "proxy.test" };

        Assert.Equal(new RouteDecision(RouteVerdict.Proxy, RouteDecisionSource.Domain),
            Decide(policy, address, "proxy.test", "pkg=org.example.direct", both));
        Assert.Equal(new RouteDecision(RouteVerdict.Direct, RouteDecisionSource.Domain),
            Decide(policy, address, "direct.test", "pkg=org.example.tunnel", both));
        Assert.Equal(new RouteDecision(RouteVerdict.Direct, RouteDecisionSource.Application),
            Decide(policy, address, null, "pkg=org.example.direct", both));
        Assert.Equal(new RouteDecision(RouteVerdict.Proxy, RouteDecisionSource.Application),
            Decide(policy, address, null, "pkg=org.example.tunnel", both.Reverse()));
        Assert.Equal(new RouteDecision(RouteVerdict.Proxy, RouteDecisionSource.Dns),
            Decide(policy, address, null, "pkg=org.example.direct", ["proxy.test"]));
        Assert.Equal(new RouteDecision(RouteVerdict.Block, RouteDecisionSource.Address),
            Decide(policy, v6 ? "2001:db8::10" : "192.0.2.10", "proxy.test", "pkg=org.example.direct", both));
    }

    [Fact]
    public async Task ResolvesAppAliasesBeforeConflictResolutionAndInvalidatesTheStamp()
    {
        var proxy = await AddAsync("Proxy", ["proxy|app:Discord"]);
        var direct = await AddAsync("Direct", ["direct|app:name=discord.exe"]);
        const string path = "C:/Programs/Discord/Discord.exe";
        await AppFinds.SearchAsync(_store, ["Discord"], [new NamedApp("Discord", path, "", "path=" + path)]);

        var policy = Assert.IsType<RoutingPolicy>(await _geo.LoadPolicyAsync([proxy, direct], AppRulePlatform.Windows));
        Assert.Empty(policy.List.Apps);
        Assert.DoesNotContain(policy.List.Rules, rule => rule.Kind == GeoRuleKind.App && rule.Role == RouteRole.Proxy);
        Assert.Equal(RouteVerdict.Direct, Decide(policy, "192.0.2.5", app: "path=" + path).Verdict);
        var origin = Assert.Single(policy.RuleOrigins, row => row.ListId == proxy);
        Assert.Equal("Discord", origin.Original.Value);
        Assert.Equal("path=" + path, origin.Unfolded.Rule.Value);

        await AppFinds.SearchAsync(_store, ["Discord"], [new NamedApp("Discord", "", "", "pkg=discord.publisher")]);
        var appChanged = Assert.IsType<RoutingPolicy>(await _geo.LoadPolicyAsync([proxy, direct], AppRulePlatform.Windows));
        Assert.NotEqual(policy.Stamp, appChanged.Stamp);
        Assert.Equal(["pkg=discord.publisher"], appChanged.List.Apps);

        var android = Assert.IsType<RoutingPolicy>(await _geo.LoadPolicyAsync([direct], AppRulePlatform.Android));
        Assert.Empty(android.List.Apps);
        Assert.Empty(android.List.DirectApps);
        Assert.Equal(RouteVerdict.None, Decide(android, "192.0.2.5", app: "name=discord.exe").Verdict);
    }

    [Fact]
    public async Task LinuxApplicationPathsRemainCaseSensitive()
    {
        var proxy = await AddAsync("Proxy", ["proxy|app:path=/example/Discord"]);
        var direct = await AddAsync("Direct", ["direct|app:path=/example/discord", "direct|app:dir=/example/bypass"]);
        var policy = Assert.IsType<RoutingPolicy>(await _geo.LoadPolicyAsync([proxy, direct], AppRulePlatform.Linux));
        Assert.Equal(RouteVerdict.Proxy, Decide(policy, "192.0.2.5", app: "path=/example/Discord").Verdict);
        Assert.Equal(RouteVerdict.Direct, Decide(policy, "192.0.2.5", app: "path=/example/discord").Verdict);
        Assert.Equal(RouteVerdict.Direct, Decide(policy, "192.0.2.5", app: "path=/example/bypass/tool").Verdict);
        Assert.Equal(RouteVerdict.None, Decide(policy, "192.0.2.5", app: "path=/example/bypass-other/tool").Verdict);
    }

    [Fact]
    public async Task DeduplicatesFragmentsFormedAfterCarving()
    {
        var broad = await AddAsync("Proxy", ["proxy|cidr:10.0.0.0/8", "direct|cidr:10.128.0.0/9"]);
        var narrow = await AddAsync("Narrow", ["proxy|cidr:10.0.0.0/9"]);
        var policy = Assert.IsType<RoutingPolicy>(await _geo.LoadPolicyAsync([broad, narrow]));
        Assert.Equal(["10.0.0.0/9"], policy.List.Routes);
        Assert.Equal(["10.128.0.0/9"], policy.List.DirectRoutes);
        Assert.Equal(2, policy.Weight);
    }

    [Fact]
    public async Task RecompilingReadsTheCurrentGeoFileAndListGeneration()
    {
        await _store.SaveGeoSourceAsync(new GeoSource("ip", "geoip", "https://example.test/ip.dat", 1));
        _files.GeoIp = IpFile(192);
        var id = await AddAsync("Geo", ["proxy|geoip:zz"]);
        var before = Assert.IsType<RoutingPolicy>(await _geo.LoadPolicyAsync([id]));
        Assert.Equal(RouteVerdict.Proxy, Decide(before, "192.0.2.5").Verdict);

        _files.GeoIp = IpFile(198);
        var after = Assert.IsType<RoutingPolicy>(await _geo.LoadPolicyAsync([id]));
        Assert.Equal(RouteVerdict.None, Decide(after, "192.0.2.5").Verdict);
        Assert.Equal(RouteVerdict.Proxy, Decide(after, "198.0.2.5").Verdict);
        Assert.NotEqual(before.Stamp, after.Stamp);
        Assert.Equal(before.Sources[0].Generation, after.Sources[0].Generation);

        await _geo.ApplyToRoutingListAsync(id, "Geo", ["proxy|geoip:zz"]);
        var replaced = Assert.IsType<RoutingPolicy>(await _geo.LoadPolicyAsync([id]));
        Assert.NotEqual(after.Stamp, replaced.Stamp);
    }

    [Fact]
    public async Task MissingDownloadedGeodataCannotSilentlyRemovePolicyRules()
    {
        await _store.SaveGeoSourceAsync(new GeoSource("ip", "geoip", "https://example.test/ip.dat", 1));
        await _store.SaveGeoFileAsync(new GeoFileMetadata("ip", "https://example.test/ip.dat", DateTimeOffset.UnixEpoch, "known", 1));
        _files.GeoIp = IpFile(192);
        var id = await AddAsync("Geo block", ["block|geoip:zz"]);
        var before = Assert.IsType<RoutingPolicy>(await _geo.LoadPolicyAsync([id]));
        Assert.Equal(RouteVerdict.Block, Decide(before, "192.0.2.5").Verdict);

        _files.GeoIp = null;
        await Assert.ThrowsAsync<InvalidDataException>(() => _geo.LoadPolicyAsync([id]));
        Assert.Equal(RouteVerdict.Block, Decide(before, "192.0.2.5").Verdict);
    }

    private Task<long> AddAsync(string name, IReadOnlyList<string> rules) => _geo.ApplyToRoutingListAsync(0, name, rules);

    private static RouteDecision Decide(RoutingPolicy policy, string address, string? host = null,
        string? app = null, IEnumerable<string>? dns = null) => policy.Decide(IPAddress.Parse(address), host, app, dns);

    private static byte[] IpFile(byte firstOctet) =>
        [0x0a, 0x0e, 0x0a, 0x02, (byte)'Z', (byte)'Z', 0x12, 0x08, 0x0a, 0x04, firstOctet, 0, 2, 0, 0x10, 24];

    private sealed class TestFiles : IGeoFileStore
    {
        public byte[]? GeoIp { get; set; }
        public byte[]? Read(string name) => name == "ip" ? GeoIp : null;
        public Stream? OpenRead(string name) => Read(name) is { } bytes ? new MemoryStream(bytes) : null;
        public Task WriteAsync(string name, byte[] data, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
