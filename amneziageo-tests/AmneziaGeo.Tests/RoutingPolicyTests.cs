using System.Net;
using AmneziaGeo.Android.Engine;
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

    [Theory]
    [InlineData(29)]
    [InlineData(32)]
    [InlineData(33)]
    public async Task AndroidBuildsOnePlanAndKeepsMixedPackagesInsideTun(int api)
    {
        var proxy = await AddAsync("Proxy", ["proxy|app:pkg=org.example.tunnel", "proxy|domain:proxy.test"]);
        var direct = await AddAsync("Direct", ["direct|app:pkg=org.example.direct", "direct|domain:direct.test",
            "block|app:pkg=org.example.block", "proxy|app:pkg=org.example.missing"]);
        await _store.SetRoutingSettingsAsync(new RoutingSettings(proxy, string.Empty, true));
        var policy = await _geo.LoadPolicyAsync([direct, proxy], AppRulePlatform.Android);
        var session = AndroidPlan(policy, api, package => package != "org.example.missing");

        Assert.Equal([proxy, direct], session.Plan.ListIds);
        Assert.Equal(session.Stamp, session.Plan.Version);
        Assert.Equal(session.Stamp, AndroidPlan(await _geo.LoadPolicyAsync([proxy, direct], AppRulePlatform.Android),
            api, package => package != "org.example.missing").Stamp);
        Assert.True(session.Plan.FlowRules);
        Assert.True(session.Plan.AllUdp);
        Assert.False(session.Plan.FullTunnel);
        Assert.Null(session.AppMode);
        Assert.Empty(session.Apps);
        Assert.Empty(session.Bypass);
        Assert.Equal(RouteVerdict.Proxy, session.Plan.AppVerdict("org.example.tunnel"));
        Assert.Equal(RouteVerdict.Direct, session.Plan.AppVerdict("org.example.direct"));
        Assert.Equal(RouteVerdict.Block, session.Plan.AppVerdict("org.example.block"));
        Assert.Equal(RouteVerdict.None, session.Plan.AppVerdict("org.example.missing"));

        await _store.SetRoutingSettingsAsync(new RoutingSettings(direct, "exclude.test", false, UseGlobalProxy: true));
        var full = AndroidPlan(await _geo.LoadPolicyAsync([proxy, direct], AppRulePlatform.Android), api);
        Assert.True(full.Plan.FullTunnel);
        Assert.True(full.Plan.RequestedAllUdp);
        Assert.False(full.Plan.AllUdp);
        Assert.Contains(full.Plan.DirectDomains, domain => domain.Value == "exclude.test");
        Assert.NotEqual(session.Stamp, full.Stamp);
        Assert.NotEqual(session.Stamp, AndroidPlan(policy, api).Stamp);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(28)]
    public async Task AndroidOlderApisOnlyUsePackageFiltersWhenTheyAreEquivalent(int api)
    {
        var apps = await AddAsync("Apps", ["proxy|app:pkg=org.example.tunnel"]);
        var names = await AddAsync("Names", ["direct|domain:direct.test"]);
        var session = AndroidPlan(await _geo.LoadPolicyAsync([apps], AppRulePlatform.Android), api);
        Assert.Equal("include", session.AppMode);
        Assert.Equal(["org.example.tunnel"], session.Apps);
        Assert.Empty(session.Bypass);
        Assert.False(session.Plan.FlowRules);

        var missing = AndroidPlan(await _geo.LoadPolicyAsync([apps], AppRulePlatform.Android), api, _ => false);
        Assert.False(missing.Plan.FullTunnel);
        Assert.Null(missing.AppMode);
        Assert.Empty(missing.Apps);

        var mixed = await _geo.LoadPolicyAsync([apps, names], AppRulePlatform.Android);
        Assert.Throws<NotSupportedException>(() => AndroidPlan(mixed, api));
        Assert.Throws<NotSupportedException>(() => AndroidPlan(mixed, 33, useRouter: false));
        Assert.Throws<NotSupportedException>(() => AndroidPlan(mixed, 33, ipv6: true));
    }

    [Fact]
    public void AndroidUsesApplicationForAmbiguousDnsAndHonorsKnownDomains()
    {
        const uint shared = 0xc000020c;
        var hints = new DnsRouteHints(new NameRules([], [], []), 300);
        hints.SetInitial(new NameAnswers(["192.0.2.12/32", "192.0.2.13/32"], ["192.0.2.12/32"], []));
        Assert.Equal(RouteVerdict.None, hints.Lookup(shared));
        Assert.Equal(RouteVerdict.Proxy, hints.Lookup(0xc000020d));

        var names = GeoRoutingPlan.Full with
        {
            FlowRules = true,
            ProxyDomains = [new(GeoDomainKind.Full, "shared.test")],
            DirectDomains = [new(GeoDomainKind.Domain, "shared.test")],
        };
        Assert.Equal("shared.test", Assert.Single(names.DnsNames().Proxy).Value);
        Assert.Empty(names.DnsNames().Direct);

        Assert.Equal(RouteVerdict.Direct, GeoRoutingPlan.DecideFlow(RouteVerdict.Proxy, RouteVerdict.None,
            hints.Lookup(shared), RouteVerdict.Direct, true, RouteVerdict.Proxy));
        Assert.Equal(RouteVerdict.Proxy, GeoRoutingPlan.DecideFlow(RouteVerdict.Direct, RouteVerdict.Proxy,
            RouteVerdict.None, RouteVerdict.Direct, true, RouteVerdict.Direct));
        Assert.Equal(RouteVerdict.Direct, GeoRoutingPlan.DecideFlow(RouteVerdict.Proxy, RouteVerdict.Direct,
            RouteVerdict.None, RouteVerdict.Proxy, true, RouteVerdict.Proxy));
        Assert.Equal(RouteVerdict.Block, GeoRoutingPlan.DecideFlow(RouteVerdict.Block, RouteVerdict.Proxy,
            RouteVerdict.None, RouteVerdict.Proxy, true, RouteVerdict.Proxy));
        Assert.Equal(RouteVerdict.Block, GeoRoutingPlan.DecideFlow(RouteVerdict.Proxy, RouteVerdict.None,
            RouteVerdict.None, RouteVerdict.None, false, RouteVerdict.Proxy));
    }

    [Fact]
    public void AndroidLearnsSuffixMatchesAndUsesAppWhileObservedDnsDisagrees()
    {
        const uint address = 0xc0000210;
        var now = 0L;
        var hints = new DnsRouteHints(new NameRules([new(GeoDomainKind.Domain, "proxy.test")],
            [new(GeoDomainKind.Full, "direct.test")], []), 300, () => now);

        Assert.Equal(RouteVerdict.None, hints.Lookup(address));
        hints.Observe(address, "video.proxy.test.", 30);
        Assert.Equal(RouteVerdict.Proxy, GeoRoutingPlan.DecideFlow(RouteVerdict.None, RouteVerdict.None,
            hints.Lookup(address), RouteVerdict.Direct, true, RouteVerdict.Direct));

        hints.Observe(address, "direct.test.", 2);
        Assert.Equal(RouteVerdict.Direct, GeoRoutingPlan.DecideFlow(RouteVerdict.None, RouteVerdict.None,
            hints.Lookup(address), RouteVerdict.Direct, true, RouteVerdict.Direct));
        now = 2_000;
        Assert.Equal(RouteVerdict.Proxy, hints.Lookup(address));
        now = 30_000;
        Assert.Equal(RouteVerdict.None, hints.Lookup(address));
    }

    [Fact]
    public async Task AndroidKeepsAllowedIpsWithoutPolicyAndRejectsAnOversizedPackageFilter()
    {
        var empty = AndroidPlan(null, 28, ipv6: true);
        Assert.True(empty.Plan.UseConfigRoutes);
        Assert.Equal(["192.0.2.0/24", "2001:db8::/32"],
            empty.Plan.RoutesForFamily(["192.0.2.0/24", "2001:db8::/32"], true));

        var rules = Enumerable.Range(0, 4096).Select(i => $"proxy|app:pkg=org.example.{new string('a', 120)}{i}").ToArray();
        var apps = await AddAsync("Large app filter", rules);
        var policy = await _geo.LoadPolicyAsync([apps], AppRulePlatform.Android);
        Assert.Throws<NotSupportedException>(() => AndroidPlan(policy, 33));
    }

    private static (GeoRoutingPlan Plan, string Stamp, string? AppMode, string[] Apps, string[] Bypass)
        AndroidPlan(RoutingPolicy? policy, int api, Func<string, bool>? installed = null,
            bool useRouter = true, bool ipv6 = false) =>
        GeoRoutingPlan.FromPolicy(policy, useRouter, ipv6, api, [], 300, SplitBudget.DefaultLimit, installed ?? (_ => true));

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
