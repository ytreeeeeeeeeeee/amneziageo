using System.Net;
using AmneziaGeo.Routing;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class WindowsIpv6PolicyBlockTests
{
    [Fact]
    public void Ipv6BlocksUseTheEffectivePrefixesWithoutTakingAnExplicitNarrowerRoute()
    {
        var settled = RangeClaims.Settle(["2001:db8::/33"], [],
            ["192.0.2.0/24", "2001:db8::/32", "2001:db8:8000::/33"]);
        var blocks = WindowsFirewall.Ipv6PolicyBlocks(settled.Block);

        Assert.Equal(IPNetwork.Parse("2001:db8:8000::/33"), Assert.Single(blocks));
        Assert.True(blocks[0].Contains(IPAddress.Parse("2001:db8:ffff::7")));
        Assert.False(blocks[0].Contains(IPAddress.Parse("2001:db8::7")));
    }
}
