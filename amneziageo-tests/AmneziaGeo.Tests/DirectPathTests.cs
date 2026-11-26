using AmneziaGeo.Linux.App;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class DirectPathTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task PartialWithdrawalRequiresReinstallBeforeRollbackSucceeds(bool installNewPrefix, bool failReinstall)
    {
        const string newPrefix = "192.0.2.128/25";
        string[] previous = ["198.51.100.0/25", "203.0.113.0/25"];
        var routes = new HashSet<string>(StringComparer.Ordinal);
        var directory = Directory.CreateTempSubdirectory("ageo-direct-path-");
        var withdrawingFailed = false;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            using var log = new AgentLog(Path.Combine(directory.FullName, "log.db"));
            var direct = new DirectPath("192.0.2.1", "eth0", log, Path.Combine(directory.FullName, "direct.routes"), RunIpAsync);
            Assert.True(await direct.ApplyAsync(previous, ct));
            Assert.Equal(previous, routes.Order(StringComparer.Ordinal));

            var updated = installNewPrefix ? await direct.ApplyAsync([newPrefix], ct) : await direct.ShedAsync([], ct);
            Assert.False(updated);
            Assert.DoesNotContain(previous[0], routes);
            Assert.Contains(previous[1], routes);
            Assert.Equal(installNewPrefix, routes.Contains(newPrefix));
            Assert.True(await direct.ShedAsync(previous, ct));

            var restored = await direct.ApplyAsync(previous, ct);
            Assert.Equal(!failReinstall, restored);
            if (restored)
            {
                Assert.Equal(previous, routes.Order(StringComparer.Ordinal));
                Assert.Equal(previous, direct.Standing);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }

        async Task<(int ExitCode, string Output)> RunIpAsync(CancellationToken token, string[] args)
        {
            switch (args)
            {
                case ["rule", "del", ..]:
                    return (1, "rule absent");
                case ["rule", "add", ..]:
                    return (0, "");
                case ["route", "flush", ..]:
                    routes.Clear();
                    return (0, "");
                case ["-force", "-batch", var withdrawal]:
                    var deletions = await File.ReadAllLinesAsync(withdrawal, token);
                    if (!withdrawingFailed)
                    {
                        Assert.Equal(2, deletions.Length);
                        Assert.True(routes.Remove(deletions[0].Split(' ')[2]));
                        withdrawingFailed = true;
                        return (1, "simulated partial withdrawal");
                    }

                    Assert.True(routes.Remove(Assert.Single(deletions).Split(' ')[2]));
                    return (0, "");
                case ["-batch", var installation]:
                    if (withdrawingFailed && failReinstall)
                    {
                        return (1, "simulated reinstall failure");
                    }

                    var replacements = await File.ReadAllLinesAsync(installation, token);
                    routes.UnionWith(replacements.Select(line => line.Split(' ')[2]));
                    return (0, "");
                default:
                    throw new InvalidOperationException($"Unexpected ip command: {string.Join(' ', args)}");
            }
        }
    }
}
