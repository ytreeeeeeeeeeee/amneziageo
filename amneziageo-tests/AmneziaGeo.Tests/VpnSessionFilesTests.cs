using AmneziaGeo.Android.Engine;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class VpnSessionFilesTests
{
    [Fact]
    public void AnAbandonedSwitchLeavesTheCommittedRequestAndPlanIntact()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var files = new VpnSessionFiles(directory.FullName);
            var first = files.Stage(Request("first", "192.0.2.0/24"));
            Assert.True(files.TryPromote(first));

            var second = files.Stage(Request("second", "198.51.100.0/24"));
            // The service may have read the request before the UI revokes it.
            Assert.Equal("second", files.ReadStaged(second)!.Name);
            new VpnSessionFiles(directory.FullName).Drop(second);
            Assert.False(files.TryPromote(second));

            var restored = Assert.IsType<VpnRequest>(new VpnSessionFiles(directory.FullName).Read());
            Assert.Equal(first, restored.SessionId);
            Assert.Equal("first", restored.Name);
            Assert.Equal("192.0.2.0/24", Assert.Single(restored.Plan!.DirectRoutes));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void PromotionReplacesTheRequestAndPlanWithTheSameStagedVersion()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var files = new VpnSessionFiles(directory.FullName);
            Assert.True(files.TryPromote(files.Stage(Request("first", "192.0.2.0/24"))));
            var second = files.Stage(Request("second", "198.51.100.0/24"));
            Assert.Equal("first", files.Read()!.Name);

            Assert.True(files.TryPromote(second));
            files.Drop(second);

            var restored = Assert.IsType<VpnRequest>(files.Read());
            Assert.Equal(second, restored.SessionId);
            Assert.Null(files.ReadStaged(second));
            Assert.Equal("second", restored.Name);
            Assert.Equal("198.51.100.0/24", Assert.Single(restored.Plan!.DirectRoutes));
            Assert.Equal("second", restored.PlanStamp);
            Assert.Equal(restored.PlanStamp, restored.Plan.Version);
            Assert.Equal(["org.example.second"], restored.AppList);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static VpnRequest Request(string name, string range) =>
        new("config", name, "include", ["org.example." + name], 0, false, null, 0,
            Plan: GeoRoutingPlan.Full with { DirectRoutes = [range], Version = name }, PlanStamp: name);
}
