using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Linux.App;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A server that allows none but its own routing lists: while its configuration stands up, the list in use is one of
/// them, an own list or none is refused, the configuration takes the routing list, and the window holds the switches
/// that would leave the lists of the server.
/// </summary>
public sealed class ServerListsOnlyTests : IAsyncLifetime
{
    private const string Text = "[Interface]\nPrivateKey = a\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";

    private const string First = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private const string Second = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private const string Morning = "2026-10-08T09:00:00+00:00";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-only-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync();
        await _store.SaveConfigAsync("office", Text);
        await _store.SaveConfigAsync("home", Text.Replace("vpn.example", "home.example", StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("{\"lists\":[],\"only\":true}", true)]
    [InlineData("{\"lists\":[],\"only\":false}", false)]
    [InlineData("{\"lists\":[]}", false)]
    [InlineData("[]", false)]
    public void TheOffer_TellsWhetherTheServerAllowsItsOwnListsAlone(string presets, bool only)
    {
        var offer = ServerOffer.Parse("{\"server\":\"amneziageo\",\"version\":\"1\",\"client\":\"c\",\"features\":{\"presets\":" + presets + "}}");

        Assert.Equal(only, offer.PresetsOnly);
    }

    [Fact]
    public async Task SelectionChangesUseTheCurrentSetAndPreserveTheLastRequiredList()
    {
        await AskAsync(true, Item(First, "Unblock", Morning), Item(Second, "Work", Morning));
        var ids = await IdsAsync();
        var unblock = ids["Unblock"];
        var work = ids["Work"];
        var ct = TestContext.Current.CancellationToken;
        var limit = await ServerListsOnly.OfAsync(_store, "office", ct);
        await _store.SetSelectedRoutingListAsync(unblock, ct);

        var add = await ServerListsOnly.ChangeSelectionAsync(_store, limit, new(RoutingSelectionChange.Add, [work]), ct);
        Assert.Null(add.Refusal);
        Assert.Equal(new[] { unblock, work }.Order(), add.After);

        var remove = await ServerListsOnly.ChangeSelectionAsync(_store, limit, new(RoutingSelectionChange.Remove, [unblock]), ct);
        Assert.Null(remove.Refusal);
        Assert.Equal([work], remove.After);

        var clear = await ServerListsOnly.ChangeSelectionAsync(_store, limit, new(RoutingSelectionChange.Replace, []), ct);
        Assert.NotNull(clear.Refusal);
        Assert.Equal([work], await _store.GetSelectedRoutingListsAsync(ct));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task HelloBindsAnImportedPresetToTheSameUidAndSelection(bool readOnly, bool contentsMatch)
    {
        var ct = TestContext.Current.CancellationToken;
        var geo = new GeoConfigurator(_store, new NoFiles());
        const string serverRule = "proxy|domain:server.example";
        var importedRule = contentsMatch ? serverRule : "proxy|domain:backup.example";
        var localId = await geo.ApplyToRoutingListAsync(0, "Office", [importedRule], First, ct);
        await _store.UpdateSelectedRoutingListsAsync(_ => [localId], ct);

        var preset = new OfferedPreset("Office", [serverRule], false, false, Id: First, ReadOnly: readOnly);
        var offer = ServerOffer.Parse(Lists(true, preset.ToPayload()));
        var taken = new List<long>();
        var offers = new ServerOffers(_store,
            ask: (_, _) => Task.FromResult(new HelloReply(offer, true)),
            geo: geo,
            take: async (ids, token) =>
            {
                foreach (var id in ids)
                {
                    var ack = await OfferedLists.AcceptPendingAsync(_store, id, async pending =>
                    {
                        await geo.ApplyToRoutingListAsync(id, pending.Name, pending.Rules, token);
                        return new IpcAck(true, "");
                    }, new IpcAck(false, "missing"), token);
                    Assert.True(ack.Ok);
                    taken.Add(id);
                }
            });

        await offers.AskAsync("office", Text, ct);
        await offers.AskAsync("office", Text, ct);

        var list = Assert.Single(await _store.ListRoutingListsAsync(ct));
        Assert.Equal(localId, list.Id);
        Assert.Equal(First, list.Uid);
        Assert.Equal([readOnly ? serverRule : importedRule], list.Rules.Select(GeoConfigurator.FormatWithRole));
        var origin = Assert.Single(await _store.ListRoutingListOriginsAsync(ct));
        Assert.Equal(localId, origin.ListId);
        Assert.Equal(readOnly, origin.ReadOnly);
        Assert.Equal(readOnly || contentsMatch, origin.Pending is null);
        Assert.Equal(readOnly ? [localId] : Array.Empty<long>(), taken);

        var limit = await ServerListsOnly.OfAsync(_store, "office", ct);
        Assert.NotNull(limit);
        Assert.Equal([localId], limit.Lists);
        Assert.False(await ServerListsOnly.EnforceSelectionAsync(_store, limit, ct));
        Assert.Equal([localId], await _store.GetSelectedRoutingListsAsync(ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LinuxStartupRequiresThePendingReadOnlyVersion(bool acceptUpdate)
    {
        var ct = TestContext.Current.CancellationToken;
        var geo = new GeoConfigurator(_store, new NoFiles());
        const string serverRule = "block|domain:server.example";
        var localId = await geo.ApplyToRoutingListAsync(0, "Office", ["proxy|domain:backup.example"], First, ct);
        await _store.UpdateSelectedRoutingListsAsync(_ => [localId], ct);
        var preset = new OfferedPreset("Office", [serverRule], true, true, Id: First, ReadOnly: true);
        var offer = ServerOffer.Parse(Lists(true, preset.ToPayload()));
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(offer, true)),
            geo: geo, take: (_, _) => Task.CompletedTask);
        await offers.BeforeConnectAsync("office", Text, null, ct);
        var limit = await ServerListsOnly.OfAsync(_store, "office", ct);
        Assert.True(limit?.Allows(localId));
        Assert.NotNull((await _store.GetRoutingListOriginAsync(localId, ct))!.Pending);

        async Task TakeAsync(long id, CancellationToken token)
        {
            await OfferedLists.AcceptPendingAsync(_store, id, async pending =>
            {
                if (!acceptUpdate)
                {
                    return new IpcAck(false, "update failed");
                }

                await geo.ApplyToRoutingListAsync(id, pending.Name, pending.Rules, token);
                await _store.SetRoutingSettingsAsync(new(id, pending.Exclusions, pending.AllUdp, "full", pending.Full), token);
                return new IpcAck(true, "");
            }, new IpcAck(false, "missing"), token);
        }

        if (!acceptUpdate)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => TunnelRouting.LoadForConnectAsync(_store, geo, "office", TakeAsync, ct));
            Assert.NotNull((await _store.GetRoutingListOriginAsync(localId, ct))!.Pending);
            return;
        }

        var routing = await TunnelRouting.LoadForConnectAsync(_store, geo, "office", TakeAsync, ct);
        Assert.Equal([localId], routing.ListIds);
        Assert.Equal([serverRule], routing.Rules.Select(GeoConfigurator.FormatWithRole));
        Assert.True(routing.AllUdp);
        Assert.False(routing.Split);
        Assert.Null((await _store.GetRoutingListOriginAsync(localId, ct))!.Pending);
    }

    [Fact]
    public void SnapshotDistinguishesLegacySelectionFromAnExplicitEmptyAppliedSet()
    {
        var legacy = new StatusSnapshot("old", null, [], BoundStatus: ConnectionStatus.Connected, SelectedRoutingList: 7);
        Assert.Equal([7L], legacy.RequestedRoutingIds());
        Assert.Equal([7L], legacy.AppliedRoutingIds());

        var current = legacy with { RequestedRoutingListIds = [7, 9], AppliedRoutingListIds = [] };
        Assert.Equal([7L, 9L], current.RequestedRoutingIds());
        Assert.Empty(current.AppliedRoutingIds());
    }

    [Fact]
    public async Task TheLimit_NamesTheListsOfTheServerAndPicksTheDefaultOne()
    {
        await AskAsync(true, Item(First, "Unblock", Morning), Item(Second, "Work", Morning, marked: true));
        var ids = await IdsAsync();

        var limit = await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None);

        Assert.NotNull(limit);
        Assert.Equal("office", limit.Config);
        Assert.Equal([ids["Unblock"], ids["Work"]], limit.Lists);
        Assert.Equal([ids["Work"]], limit.Fallback);
    }

    [Fact]
    public async Task TheLimit_PicksTheFirstListWhereTheServerMarksNoneDefault()
    {
        await AskAsync(true, Item(First, "Unblock", Morning), Item(Second, "Work", Morning));
        var ids = await IdsAsync();

        var limit = await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None);

        Assert.Equal([ids["Unblock"]], limit!.Fallback);
    }

    [Fact]
    public async Task AConfigTheStoreHoldsNoTextOf_IsLimitedByTheTextItComesWith()
    {
        await Asked(true, Item(First, "Unblock", Morning)).AskAsync("phone", Text, CancellationToken.None);
        await _store.SetConfigTransportAsync(new ConfigTransport("phone", false, UseRouting: false));

        Assert.NotNull(await ServerListsOnly.OfAsync(_store, "phone", Text, CancellationToken.None));
        Assert.Null(await ServerListsOnly.OfAsync(_store, "phone", CancellationToken.None));
        Assert.NotNull(await ServerListsOnly.RoutingOffRefusalAsync(_store, "phone", Text, false, CancellationToken.None));
        Assert.True(await ServerListsOnly.RoutingOnAsync(_store, "phone", Text, CancellationToken.None));
    }

    [Fact]
    public async Task AServerThatAllowsAnyList_SetsNoLimit()
    {
        await AskAsync(false, Item(First, "Unblock", Morning));

        Assert.Null(await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None));
    }

    [Fact]
    public async Task AServerWhoseListsTheDeviceNoLongerHoldsStillRejectsUnrelatedLists()
    {
        await AskAsync(true, Item(First, "Unblock", Morning));
        await _store.RemoveRoutingListAsync((await IdsAsync())["Unblock"]);
        var geo = new GeoConfigurator(_store, new NoFiles());
        var own = await geo.ApplyToRoutingListAsync(0, "Mine", ["proxy|geosite:openai"]);
        await _store.SetSelectedRoutingListAsync(own);

        var limit = await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None);

        Assert.NotNull(limit);
        Assert.False(limit.Available);
        Assert.Empty(limit.Lists);
        Assert.NotNull(ServerListsOnly.RefusalOfSet(limit, [own]));
        Assert.False(await ServerListsOnly.EnforceSelectionAsync(_store, limit, CancellationToken.None));
        Assert.Equal([own], await _store.GetSelectedRoutingListsAsync());
    }

    [Fact]
    public async Task TheFirstConfigWhoseServerLimits_SetsTheLimit()
    {
        await AskAsync(true, Item(First, "Unblock", Morning));

        var limit = await ServerListsOnly.FirstAsync(_store, ["home", "office"], CancellationToken.None);

        Assert.Equal("office", limit!.Config);
    }

    [Fact]
    public async Task AnOwnListOrNone_IsRefusedWhileAListOfTheServerIsNot()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        await AskAsync(true, Item(First, "Unblock", Morning));
        var server = (await IdsAsync())["Unblock"];
        var own = await geo.ApplyToRoutingListAsync(0, "Mine", ["proxy|geosite:openai"]);
        var limit = await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None);

        var refused = ServerListsOnly.Refusal(limit, own);

        Assert.NotNull(refused);
        Assert.False(refused.Ok);
        Assert.True(IpcMessage.TryParse(refused.Message, out var key, out var args));
        Assert.Equal("Agent_RoutingListsServerOnly", key);
        Assert.Equal(["office"], args);
        Assert.NotNull(ServerListsOnly.Refusal(limit, null));
        Assert.Null(ServerListsOnly.Refusal(limit, server));
        Assert.Null(ServerListsOnly.Refusal(null, own));
    }

    [Fact]
    public async Task AnOwnListInUse_GivesWayToTheListOfTheServer()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        await AskAsync(true, Item(First, "Unblock", Morning));
        var server = (await IdsAsync())["Unblock"];
        var own = await geo.ApplyToRoutingListAsync(0, "Mine", ["proxy|geosite:openai"]);
        var limit = await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None);

        await _store.SetSelectedRoutingListAsync(own);

        Assert.True(await ServerListsOnly.EnforceSelectionAsync(_store, limit, CancellationToken.None));
        Assert.Equal([server], await _store.GetSelectedRoutingListsAsync());
        Assert.False(await ServerListsOnly.EnforceSelectionAsync(_store, limit, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARestrictionKeepsAllowedMembersOrFallsBackToAllAllowedDefaults(bool hasAllowedMember)
    {
        await AskAsync(true, Item(First, "Unblock", Morning, marked: true), Item(Second, "Work", Morning, marked: true));
        var ids = await IdsAsync();
        var geo = new GeoConfigurator(_store, new NoFiles());
        var own = await geo.ApplyToRoutingListAsync(0, "Mine", ["proxy|geosite:openai"]);
        var limit = await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None);
        var allowed = new[] { ids["Unblock"], ids["Work"] };
        await _store.UpdateSelectedRoutingListsAsync(_ => hasAllowedMember ? [allowed[1], own] : [own]);

        Assert.Null(ServerListsOnly.RefusalOfSet(limit, allowed));
        Assert.NotNull(ServerListsOnly.RefusalOfSet(limit, [allowed[0], own]));
        Assert.NotNull(ServerListsOnly.RefusalOfSet(limit, []));
        Assert.True(await ServerListsOnly.EnforceSelectionAsync(_store, limit, CancellationToken.None));
        Assert.Equal(hasAllowedMember ? [allowed[1]] : allowed, await _store.GetSelectedRoutingListsAsync());
        Assert.False(await ServerListsOnly.EnforceSelectionAsync(_store, limit, CancellationToken.None));
    }

    [Fact]
    public async Task TwoConcurrentRemovalsCannotDeleteTheEntireRestrictedSelection()
    {
        await AskAsync(true, Item(First, "Unblock", Morning, marked: true), Item(Second, "Work", Morning, marked: true));
        var limit = await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None);
        Assert.NotNull(limit);

        var refusals = await Task.WhenAll(limit.Lists.Select(id =>
            ServerListsOnly.RemoveAsync(_store, limit, id, CancellationToken.None)));

        Assert.Single(refusals, refusal => refusal is null);
        Assert.Single(refusals, refusal => refusal is { Ok: false });
        var remaining = Assert.Single((await _store.GetSelectedRoutingListsAsync())!);
        Assert.Equal(remaining, Assert.Single(await _store.ListRoutingListsAsync()).Id);
    }

    [Fact]
    public async Task AConfigOffTheRoutingList_TakesItUnderTheFlagAlone()
    {
        await AskAsync(true, Item(First, "Unblock", Morning));
        await _store.SetConfigTransportAsync(new ConfigTransport("office", false, UseRouting: false));
        await _store.SetConfigTransportAsync(new ConfigTransport("home", false, UseRouting: false));

        Assert.True(await ServerListsOnly.RoutingOnAsync(_store, "office", CancellationToken.None));
        Assert.False(await ServerListsOnly.RoutingOnAsync(_store, "office", CancellationToken.None));
        Assert.False(await ServerListsOnly.RoutingOnAsync(_store, "home", CancellationToken.None));
        Assert.True((await _store.GetConfigTransportAsync("office"))!.UseRouting);
        Assert.False((await _store.GetConfigTransportAsync("home"))!.UseRouting);
    }

    [Fact]
    public async Task TurningTheRoutingListOff_IsRefusedUnderTheFlag()
    {
        await AskAsync(true, Item(First, "Unblock", Morning));

        var refused = await ServerListsOnly.RoutingOffRefusalAsync(_store, "office", false, CancellationToken.None);

        Assert.NotNull(refused);
        Assert.Null(await ServerListsOnly.RoutingOffRefusalAsync(_store, "office", true, CancellationToken.None));
        Assert.Null(await ServerListsOnly.RoutingOffRefusalAsync(_store, "home", false, CancellationToken.None));
    }

    [Fact]
    public async Task TheFlagOfTheServer_IsToldToTheAgentWhenItChanges()
    {
        await AskAsync(false, Item(First, "Unblock", Morning));
        var told = new List<string>();
        Task Tell(string config)
        {
            told.Add(config);
            return Task.CompletedTask;
        }

        await Asked(true, Item(First, "Unblock", Morning)).AskEachAsync([("office", Text)], Tell, CancellationToken.None);
        await Asked(true, Item(First, "Unblock", Morning)).AskEachAsync([("office", Text)], Tell, CancellationToken.None);
        await Asked(false, Item(First, "Unblock", Morning)).AskEachAsync([("office", Text)], Tell, CancellationToken.None);

        Assert.Equal(["office", "office"], told);
    }

    [Fact]
    public async Task WithdrawalOfAnEditablePresetIsReportedWithTheRestrictionStillOn()
    {
        var first = Item(First, "Unblock", Morning, marked: true);
        var second = Item(Second, "Work", Morning, marked: true);
        await AskAsync(true, first, second);
        var ids = await IdsAsync();
        var notifications = 0;

        async Task Tell(string config)
        {
            notifications++;
            var limit = await ServerListsOnly.OfAsync(_store, config, CancellationToken.None);
            await ServerListsOnly.EnforceSelectionAsync(_store, limit, CancellationToken.None);
        }

        await Asked(true, second).AskEachAsync([("office", Text)], Tell, CancellationToken.None);

        Assert.Equal(1, notifications);
        Assert.Equal([ids["Work"]], await _store.GetSelectedRoutingListsAsync());
        Assert.Equal(2, (await _store.ListRoutingListsAsync()).Count);
    }

    [Fact]
    public void UnderTheLimit_TheSwitchesThatLeaveTheListsOfTheServerHold()
    {
        var routing = Routing();

        routing.Apply(Snapshot("office-awg1", [2, 3], selected: 2));

        Assert.Equal([true, true, false], routing.RoutingLists.Select(row => row.Locked));
        Assert.Equal(Loc.Instance.Get("Main_ServerListsOnlyOf", "office-awg1"), routing.RoutingLists[0].ActiveTip);
        Assert.Equal(Loc.Instance.Get("Main_RoutingEnableList"), routing.RoutingLists[2].ActiveTip);
        Assert.True(routing.HomeRoutingLocked);
        Assert.Equal(Loc.Instance.Get("Main_ServerListsOnlyOf", "office-awg1"), routing.HomeRoutingTip);
    }

    [Fact]
    public void TheUiRestoresTheLastSetAndShowsTheAppliedRowsIndependently()
    {
        var agent = new Silent();
        var prefs = new UiPreferences { PresetRegions = "de" };
        var routing = new MainWindowViewModel(agent, prefs).Routing;
        var snapshot = Snapshot(string.Empty, null, selected: 2) with
        {
            RequestedRoutingListIds = [2, 3],
            AppliedRoutingListIds = [1],
        };

        routing.Apply(snapshot);
        Assert.Equal([false, true, true], routing.RoutingLists.Select(row => row.IsSelected));
        Assert.Equal([true, false, false], routing.RoutingLists.Select(row => row.IsLive));
        routing.HomeRoutingOn = false;
        Assert.Equal(["replace"], agent.Commands.Last(command => command.Op == IpcContract.OpChangeRoutingSelection).Args);

        routing.Apply(snapshot with { SelectedRoutingList = null, RequestedRoutingListIds = [] });
        routing.HomeRoutingOn = true;
        Assert.Equal(["replace", "2", "3"], agent.Commands.Last(command => command.Op == IpcContract.OpChangeRoutingSelection).Args);
    }

    [Fact]
    public async Task RoutingControlsDisableUntilTheAgentAnswers()
    {
        var agent = new Silent();
        var routing = new MainWindowViewModel(agent, new UiPreferences { PresetRegions = "de" }).Routing;
        routing.Apply(Snapshot(string.Empty, null, selected: 2) with { RequestedRoutingListIds = [2] });
        var first = routing.RoutingLists[0];
        var second = routing.RoutingLists[2];
        var response = new TaskCompletionSource<IpcAck>();
        agent.RoutingResponse = response.Task;
        var readyAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        routing.SelectCardCommand.CanExecuteChanged += (_, _) =>
        {
            if (routing.SelectCardCommand.CanExecute(second))
            {
                readyAgain.TrySetResult();
            }
        };

        Assert.True(routing.SelectCardCommand.CanExecute(first));
        routing.SelectCardCommand.Execute(first);
        Assert.True(routing.ChangingRouting);
        Assert.False(routing.CanChangeHomeRouting);
        Assert.False(routing.SelectCardCommand.CanExecute(second));
        Assert.Single(agent.Commands.Where(command => command.Op == IpcContract.OpChangeRoutingSelection));

        agent.RoutingResponse = null;
        response.SetResult(new IpcAck(false, "request refused"));
        await readyAgain.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(routing.CanChangeHomeRouting);
        Assert.Equal("request refused", routing.RoutingChangeError);
        routing.SelectCardCommand.Execute(second);
        Assert.Equal(["add", "3"], agent.Commands.Last(command => command.Op == IpcContract.OpChangeRoutingSelection).Args);
    }

    [Fact]
    public void TheUiLocksOnlyTheLastPermittedSelectedList()
    {
        var routing = Routing();
        var snapshot = Snapshot("office", [2, 3], selected: 2);
        routing.Apply(snapshot with { RequestedRoutingListIds = [2, 3] });
        Assert.Equal([true, false, false], routing.RoutingLists.Select(row => row.Locked));

        routing.Apply(snapshot with { RequestedRoutingListIds = [2] });
        Assert.Equal([true, true, false], routing.RoutingLists.Select(row => row.Locked));
    }

    [Fact]
    public void WithoutTheLimit_EverySwitchIsFree()
    {
        var routing = Routing();
        routing.Apply(Snapshot("office-awg1", [2, 3], selected: 2));

        routing.Apply(Snapshot(string.Empty, null, selected: 2));

        Assert.All(routing.RoutingLists, row => Assert.False(row.Locked));
        Assert.False(routing.HomeRoutingLocked);
        Assert.Equal(Loc.Instance.Get("Main_HomeRoutingHint"), routing.HomeRoutingTip);
    }

    [Fact]
    public void AnOpenOwnList_HoldsItsSwitchUnderTheLimit()
    {
        var routing = Routing();
        routing.Apply(Snapshot("office-awg1", [2, 3], selected: 2));

        routing.EditRoutingList = routing.RoutingLists[0];
        var own = (routing.OpenListLocked, routing.OpenListLockTip);
        routing.EditRoutingList = routing.RoutingLists[2];

        Assert.Equal((true, Loc.Instance.Get("Main_ServerListsOnlyOf", "office-awg1")), own);
        Assert.False(routing.OpenListLocked);
        Assert.Null(routing.OpenListLockTip);
    }

    [Fact]
    public void TheHeldSwitchOfTheOpenList_SendsNothing()
    {
        var agent = new Silent();
        var routing = new MainWindowViewModel(agent, new UiPreferences { PresetRegions = "de" }).Routing;
        routing.Apply(Snapshot("office-awg1", [2, 3], selected: 2));
        routing.EditRoutingList = routing.RoutingLists[0];

        routing.UseOpenList = true;

        Assert.DoesNotContain(IpcContract.OpAssignRouting, agent.Sent);
        Assert.False(routing.UseOpenList);
    }

    [Fact]
    public void TheRoutingSwitchOfTheConfig_ShowsOnAndStaysCleanUnderTheFlag()
    {
        var transport = new ConfigTransportViewModel(new Silent(), "office", false, 1420, false, useRouting: false);

        transport.ServerListsOnly = true;

        Assert.True(transport.RoutingSwitch);
        Assert.False(transport.UseRouting);
        Assert.False(transport.IsDirty);
        Assert.Equal(Loc.Instance.Get("Main_ServerListsOnly"), transport.RoutingSwitchTip);
    }

    [Fact]
    public void TheRoutingSwitchOfTheConfig_IsFreeWithoutTheFlag()
    {
        var transport = new ConfigTransportViewModel(new Silent(), "office", false, 1420, false, useRouting: false);

        Assert.False(transport.RoutingSwitch);
        Assert.Null(transport.RoutingSwitchTip);
    }

    private Task AskAsync(bool only, params string[] items) => Asked(only, items).AskAsync("office", Text, CancellationToken.None);

    private ServerOffers Asked(bool only, params string[] items) =>
        new(
            _store,
            ask: (_, _) => Task.FromResult(new HelloReply(ServerOffer.Parse(Lists(only, items)), true)),
            geo: new GeoConfigurator(_store, new NoFiles()));

    private async Task<Dictionary<string, long>> IdsAsync() =>
        (await _store.ListRoutingListsAsync()).ToDictionary(list => list.Name, list => list.Id, StringComparer.Ordinal);

    private static string Lists(bool only, params string[] items) =>
        "{\"server\":\"amneziageo\",\"version\":\"1\",\"client\":\"c\",\"features\":{\"presets\":{\"lists\":["
        + string.Join(',', items) + "],\"only\":" + (only ? "true" : "false") + "}}}";

    // A list the way the server hands it out with its identifier.
    private static string Item(string id, string name, string updated, bool marked = false) =>
        "{\"name\":\"" + name + "\",\"rules\":[\"proxy|geosite:youtube\"],\"allUdp\":false,\"full\":false,\"id\":\"" + id
        + "\",\"updated\":\"" + updated + "\",\"default\":" + (marked ? "true" : "false") + ",\"source\":\"office-awg1\"}";

    // The window asks no region of its own: the regions are picked and the tunnel is up.
    private static RoutingViewModel Routing() =>
        new MainWindowViewModel(new Silent(), new UiPreferences { PresetRegions = "de" }).Routing;

    // An own list and two lists of the server of office-awg1, with the limit the agent reports.
    private static StatusSnapshot Snapshot(string limitedBy, IReadOnlyList<long>? lists, long selected) =>
        new(
            "1",
            null,
            [],
            RoutingLists:
            [
                new(1, "Mine", 1, 1, 1, ProxyRuleCount: 1),
                new(2, "Unblock", 1, 1, 1, ProxyRuleCount: 1, Source: "office-awg1"),
                new(3, "Work", 1, 1, 1, ProxyRuleCount: 1, Source: "office-awg1"),
            ],
            BoundStatus: ConnectionStatus.Connected,
            SelectedRoutingList: selected,
            ServerListsOf: limitedBy,
            ServerLists: lists);

    // No geo files.
    private sealed class NoFiles : IGeoFileStore
    {
        public byte[]? Read(string name) => null;

        public Stream? OpenRead(string name) => null;

        public Task WriteAsync(string name, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Answers every command with nothing and keeps what was sent.
    private sealed class Silent : IAgentConnection
    {
        public event Action? Connected
        {
            add { }
            remove { }
        }

        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public List<string> Sent { get; } = [];

        public List<IpcCommand> Commands { get; } = [];

        public Task<IpcAck>? RoutingResponse { get; set; }

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            Sent.Add(command.Op);
            Commands.Add(command);

            return command.Op == IpcContract.OpChangeRoutingSelection && RoutingResponse is { } response
                ? response : Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
