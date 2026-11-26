using System.Net;
using AmneziaGeo.Linux.App;
using AmneziaGeo.Linux.Engine;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class LinuxRouteApplierTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialUpholdRoutesAreRemovedAfterRollback(bool failRead)
    {
        const string gateway = "192.0.2.1";
        const string device = "eth0";
        const string oldRange = "198.51.100.0/24";
        const string unrelated = "203.0.113.1/32";
        var first = IPAddress.Parse("198.51.100.1");
        var second = IPAddress.Parse("198.51.100.2");
        var routes = new HashSet<string>([unrelated], StringComparer.Ordinal);
        var directory = Directory.CreateTempSubdirectory("ageo-uphold-");
        try
        {
            using var log = new AgentLog(Path.Combine(directory.FullName, "log.db"));
            using var engine = new AwgDaemon("unused", "testawg");
            var applier = new LinuxRouteApplier("testawg", null, engine, gateway, device, [], null, log, RunIp);
            Assert.True(applier.TryAddRoute(first, out var firstIndex));
            Assert.True(applier.TryAddRoute(second, out var secondIndex));

            applier.Uphold([]);
            Assert.Equal(1, applier.Failures);
            Assert.Contains($"{first}/32", routes);
            Assert.DoesNotContain($"{second}/32", routes);

            applier.Uphold([oldRange]);
            applier.RemoveRoute(first, firstIndex);
            applier.RemoveRoute(second, secondIndex);

            Assert.Equal(unrelated, Assert.Single(routes));
            Assert.Equal(1, applier.Failures);
        }
        finally
        {
            directory.Delete(recursive: true);
        }

        (int ExitCode, string Output) RunIp(string[] args) => args switch
        {
            ["route", "get", var address] => (0, $"{address} via {gateway} dev {device} table {DirectPath.Table}"),
            ["-batch", var file] => InstallFirst(file),
            ["-o", "-4", "route", "show", "table", "main", "via", gateway, "dev", device] =>
                failRead ? (1, "simulated listing failure") : (0, first.ToString()),
            ["route", "del", var host, "via", gateway, "dev", device, "table", "main"] =>
                routes.Remove(host) ? (0, "") : (1, "route absent"),
            ["route", "show", "exact", var host, "via", gateway, "dev", device, "table", "main"] =>
                (0, routes.Contains(host) ? host : ""),
            _ => throw new InvalidOperationException($"Unexpected ip command: {string.Join(' ', args)}"),
        };

        (int ExitCode, string Output) InstallFirst(string file)
        {
            var lines = File.ReadAllLines(file);
            Assert.Equal(2, lines.Length);
            Assert.True(routes.Add(lines[0].Split(' ')[2]));
            return (1, "simulated partial insertion");
        }
    }
}
