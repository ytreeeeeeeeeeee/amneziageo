using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Routing lists in a bundle go by their ids: an export picks them by number or by name, an import finds the held
/// list by the lasting id the block carries.
/// </summary>
public sealed class BundleListsTests
{
    private static readonly IReadOnlyList<RoutingList> Lists =
    [
        List(3, "work", "uid-3"),
        List(5, "work", "uid-5"),
        List(7, "home", "uid-7"),
    ];

    [Fact]
    public void ANumber_PicksThatListAlone()
    {
        var (picked, missing) = BundleLists.Pick(Lists, ["5"]);

        Assert.Equal([5L], picked.Select(one => one.List.Id));
        Assert.Equal("5", Assert.Single(picked).Key);
        Assert.Empty(missing);
    }

    [Fact]
    public void AName_PicksEveryListUnderIt()
    {
        var (picked, _) = BundleLists.Pick(Lists, ["work"]);

        Assert.Equal([3L, 5L], picked.Select(one => one.List.Id));
    }

    [Fact]
    public void AListPickedTwice_GoesOnce_AndAWordThatPicksNothingComesBack()
    {
        var (picked, missing) = BundleLists.Pick(Lists, ["3", "work", "nowhere", "99"]);

        Assert.Equal([3L, 5L], picked.Select(one => one.List.Id));
        Assert.Equal(["nowhere", "99"], missing);
    }

    [Fact]
    public void ABlock_IsTheListHeldUnderItsId()
    {
        Assert.Equal(5, BundleLists.Held(Block("other name", "uid-5"), Lists)!.Id);
    }

    [Fact]
    public void ABlockWithoutAnIdOrWithAnUnknownOne_IsAListOfItsOwn()
    {
        Assert.Null(BundleLists.Held(Block("work", null), Lists));
        Assert.Null(BundleLists.Held(Block("work", "uid-9"), Lists));
    }

    [Fact]
    public void ABlockWithoutAName_LandsUnderTheFallback()
    {
        Assert.Equal(BundleLists.FallbackName, BundleLists.NameOf(Block("  ", null)));
        Assert.Equal("work", BundleLists.NameOf(Block(" work ", null)));
    }

    [Fact]
    public void TheIdOfAList_TravelsInTheBundle()
    {
        var bundle = new PortableBundle.Bundle(
            PortableBundle.FormatTag,
            PortableBundle.CurrentVersion,
            [],
            [Block("work", "uid-5")],
            ActiveRoutingListId: "uid-5");

        var back = PortableBundle.Deserialize(PortableBundle.Serialize(bundle))!;

        Assert.Equal("uid-5", Assert.Single(back.RoutingLists).Id);
        Assert.Equal("uid-5", back.ActiveRoutingListId);
    }

    [Fact]
    public void ABundleOfAnEarlierBuild_ReadsWithoutIds()
    {
        var back = PortableBundle.Deserialize("""
            {"format":"amneziageo-bundle","version":2,"configs":[],"routingLists":[{"name":"work","rules":["proxy|domain:a.example"]}]}
            """)!;

        Assert.Null(Assert.Single(back.RoutingLists).Id);
        Assert.Null(BundleLists.Held(back.RoutingLists[0], Lists));
    }

    [Fact]
    public async Task TheWholeSelection_RoundTripsByUid_IntoADifferentLibrary()
    {
        var root = Directory.CreateTempSubdirectory("ageo-bundle-");
        var source = new SqliteStateStore(Path.Combine(root.FullName, "source.db"));
        var target = new SqliteStateStore(Path.Combine(root.FullName, "target.db"));
        try
        {
            await source.InitializeAsync();
            await target.InitializeAsync();
            var srcA = await source.SaveRoutingListAsync(List(0, "work", "uid-a"));
            var srcB = await source.SaveRoutingListAsync(List(0, "work", "uid-b"));
            await source.UpdateSelectedRoutingListsAsync(_ => [srcA, srcB]);

            var existing = await target.SaveRoutingListAsync(List(0, "work", "other"));
            var dstB = await target.SaveRoutingListAsync(List(0, "work", "uid-b"));
            var dstA = await target.SaveRoutingListAsync(List(0, "work", "uid-a"));
            await target.UpdateSelectedRoutingListsAsync(_ => [existing]);
            Assert.NotEqual(srcA, dstA);

            var picked = BundleLists.Pick(await source.ListRoutingListsAsync(), ["work"]).Picked;
            Assert.Null(await BundleLists.ExportSelectionAsync(source, picked, full: false));
            Assert.Null(await BundleLists.ExportSelectionAsync(source, [picked[0]], full: true));
            var uids = await BundleLists.ExportSelectionAsync(source, picked, full: true);
            var backup = PortableBundle.Deserialize(PortableBundle.Serialize(Backup(uids)))!;
            Assert.Equal(["uid-a", "uid-b"], backup.ActiveRoutingListIds);

            var warnings = new List<string>();
            var errors = new List<string>();
            var info = new List<string>();

            async Task<bool> Restore(PortableBundle.Bundle bundle, ListsLimit? limit = null)
            {
                return await BundleLists.RestoreSelectionAsync(target, bundle, async (change, ct) =>
                {
                    var (refusal, _, _) = await ServerListsOnly.ChangeSelectionAsync(target, limit, change, ct);
                    return refusal ?? new IpcAck(true, "");
                }, info.Add, warnings.Add, errors.Add);
            }

            Assert.True(await Restore(backup));
            Assert.Equal([dstB, dstA], await target.GetSelectedRoutingListsAsync());
            Assert.Contains("2 list(s)", Assert.Single(info));

            Assert.True(await Restore(Backup(["uid-a", "missing"])));
            Assert.Equal([dstB, dstA], await target.GetSelectedRoutingListsAsync());
            Assert.Contains("unknown list UID", Assert.Single(warnings));
            Assert.True(await Restore(Backup(null)));
            Assert.Equal([dstB, dstA], await target.GetSelectedRoutingListsAsync());

            var limit = new ListsLimit("restricted", [dstA], [dstA]);
            Assert.True(await Restore(backup, limit));
            Assert.True(await Restore(Backup([]), limit));
            Assert.Equal([dstB, dstA], await target.GetSelectedRoutingListsAsync());
            Assert.Equal(3, warnings.Count);

            var legacy = PortableBundle.Deserialize("""
                {"format":"amneziageo-bundle","version":2,"configs":[],"routingLists":[],"ActiveRoutingListId":"uid-a"}
                """)!;
            Assert.True(await Restore(legacy, limit));
            Assert.Equal([dstA], await target.GetSelectedRoutingListsAsync());
            Assert.True(await Restore(Backup([]) with { ActiveRoutingListId = "uid-a" }));
            Assert.Empty((await target.GetSelectedRoutingListsAsync())!);

            await source.UpdateSelectedRoutingListsAsync(_ => [srcA]);
            var partial = Backup(await BundleLists.ExportSelectionAsync(source, [picked[0]], full: false));
            Assert.DoesNotContain("ActiveRoutingListIds", PortableBundle.Serialize(partial));

            await source.UpdateSelectedRoutingListsAsync(_ => []);
            Assert.Null(await BundleLists.ExportSelectionAsync(source, picked, full: false));
            Assert.Empty((await BundleLists.ExportSelectionAsync(source, picked, full: true))!);

            var failed = await BundleLists.RestoreSelectionAsync(target, backup,
                (_, _) => throw new IOException("backup contents should not leak"),
                info.Add, warnings.Add, errors.Add);
            Assert.False(failed);
            Assert.DoesNotContain("backup contents", Assert.Single(errors));
        }
        finally
        {
            source.ClearPool();
            target.ClearPool();
            root.Delete(true);
        }
    }

    private static PortableBundle.Bundle Backup(IReadOnlyList<string>? uids) =>
        new(PortableBundle.FormatTag, PortableBundle.CurrentVersion, [], [], ActiveRoutingListIds: uids);

    private static RoutingList List(long id, string name, string uid) =>
        new(id, name, [], [], [], [], [], [], [], [], [], uid);

    private static PortableBundle.RoutingBlock Block(string name, string? id) =>
        new(name, ["proxy|domain:a.example"], null, id);
}
