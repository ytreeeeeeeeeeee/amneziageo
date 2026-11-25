using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using AmneziaGeo.Android.Engine;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Routing;
using AmneziaGeo.Ui.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// In-process agent for the Android head: persists configs, projects status snapshots, and drives
/// the tunnel through <see cref="GeoVpnService"/>.
/// </summary>
internal sealed class AndroidAgentConnection : IAgentConnection
{
    private readonly Dictionary<string, string> _configs = new(StringComparer.Ordinal);

    // The order the list is shown in; the store file keeps it as the order its config map is written in.
    private List<string> _order = [];
    private readonly string _storePath;

    // Age past which the tunnel's own snapshot of what it carries is no longer an answer about what runs now.
    private const int SessionWindowSeconds = 60;

    // What the system broadcasts when this device starts or stops sharing its network.
    private const string TetherChanged = "android.net.conn.TETHER_STATE_CHANGED";

    // How long after that broadcast the links are read once more.
    private const int TetherSettleMs = 2000;

    // How long a burst of changes in the networks is waited out before the links are read.
    private const int LinkSettleMs = 300;

    // How long a probe handed to the tunnel is waited for, and how often its result is looked for.
    private const int ProbeWaitMs = 40_000;
    private const int ProbePollMs = 250;

    // How long the tunnel is waited for while it asks the server of a configuration before a switch.
    private const int SwitchWaitMs = 15_000;

    // How long after a refused switch the snapshot is told once more.
    private const int RetellMs = 100;

    // Raises of a session that went with its process, and the time they are counted over.
    private const int GoneRaises = 3;
    private const int GoneWindowMs = 120_000;

    // Leaves the start-up rush to the interface before the first geo check goes out.
    private const int GeoStartDelaySeconds = 5;
    private const int GeoTickSeconds = 60;

    // The pause before a source a scheduled check did not reach is asked again, and how many times.
    private const int GeoRecheckMinutes = 10;
    private const int GeoRechecks = 3;
    private const int MinGeoIntervalHours = 1;
    private const int MaxGeoIntervalHours = 24 * 7;

    // Seconds between two looks at whose subscription interval has run out.
    private const int SubscriptionTickSeconds = 900;
    private const int DefaultSubscriptionIntervalHours = 12;

    // How long the networks the device stands in are taken as read for the window.
    private const int LocalReadMs = 10_000;
    private static readonly string AppVersion = ReadAppVersion();

    private readonly SqliteStateStore _store;
    private readonly GeoConfigurator _geo;
    private readonly GeoFileUpdater _geoUpdater;
    private readonly AndroidGeoFileStore _geoFiles;
    private readonly GeoUpdateChecker _geoChecker;
    private readonly AndroidAgentLog _log;
    private readonly GeoHttp _geoHttp;
    private readonly HttpClient _httpClient = new();
    private readonly AndroidUpdater _updater;
    private readonly ServerOffers _offers;
    private readonly object _loadGate = new();
    private readonly SemaphoreSlim _routingSelectionRead = new(1, 1);
    // Null until the store has been read: the snapshot says "not loaded yet", not "no lists".
    private IReadOnlyList<RoutingListEntry>? _routingSummaries;
    private IReadOnlyDictionary<string, ConfigTransport> _transports = new Dictionary<string, ConfigTransport>(StringComparer.Ordinal);

    // The name servers of each config: what the names through the tunnel are asked of.
    private IReadOnlyDictionary<string, ConfigDns> _dns = new Dictionary<string, ConfigDns>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, ServerOffer> _offered = new Dictionary<string, ServerOffer>(StringComparer.Ordinal);

    // The networks the device stands in, as the window last read them, and when.
    private IReadOnlyList<string> _localNets = [];
    private long _localReadAt;

    // The limit the server of the configuration standing up sets on the routing lists, as last read.
    private ListsLimit? _serverLists;

    // What the device found for each app:<name> of the routing lists, as last kept.
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _appFinds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    // One search of the programs of the device at a time.
    private readonly SemaphoreSlim _finding = new(1, 1);

    // Which subscription brought which configuration; empty until the store has been read.
    private IReadOnlyDictionary<string, SubscriptionMember> _members = new Dictionary<string, SubscriptionMember>(StringComparer.Ordinal);

    // Subscriptions whose server holds another revision than the one read last.
    private IReadOnlySet<string> _stale = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlyList<GeoSource> _geoSources = [];
    private IReadOnlyList<GeoFileMetadata> _geoFileMeta = [];
    private readonly HashSet<string> _updatingSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _sourceErrors = new(StringComparer.Ordinal);

    // Per-source download volume while a base is being fetched.
    private readonly ConcurrentDictionary<string, GeoDownload> _sourceProgress = new(StringComparer.Ordinal);
    private Task? _initTask;
    private Task? _geoFilesTask;
    private VpnBridge.Listener? _events;
    private readonly object _linkGate = new();
    private VpnBridge.Listener? _tether;
    private LinkWatch? _links;
    private int _linksDue;
    private readonly TunnelWatch _watch = new(GoneRaises, GoneWindowMs);
    private readonly VpnConnectPreparation _connectPreparation = new();
    private PendingLaunch? _pendingLaunch;

    private sealed record PendingLaunch(string Id, string Config, string DownNotice,
        WsEndpoint? Front, bool RestartRequired);
    private readonly Handler _ticks = new(Looper.MainLooper!);

    // Когда голова собирала снимок по показанию связи.
    private long _linkToldAt;

    // Показание связи пришло без окна на экране и в снимок не попало.
    private volatile bool _linkUntold;

    private string? _selectedTarget;
    private LegacyRoutingSelection? _legacyRoutingSelection;
    private IReadOnlyList<long>? _routingSelection;

    private long? SelectedRoutingList => Volatile.Read(ref _routingSelection) is { Count: > 0 } selection ? selection[0] : null;

    // What the notification of a tunnel taken down was last told, so the same is not posted again.
    private string _downNotice = string.Empty;

    // Set while the tunnel process is gone and the session is to be raised again without the window.
    private bool _raiseOwed;
    private string? _boundTarget;
    private string _boundStatus = ConnectionStatus.Disconnected;
    private long _handshakeUnix;
    private LinkReading _link = LinkReading.Empty;
    private volatile LinkNow _linkNow = LinkNow.Unknown;
    private DateTimeOffset _linkLoggedAt;
    private bool _churnLogged;
    private DateTimeOffset _churnSince;
    private bool _active;
    private bool _restartRequired;

    // Просил ли пользователь быть подключённым: держится через падения связи и гаснет только по отбою.
    private bool _dialWanted;

    // The websocket front the dial that stands was handed, and whether it was handed one yet.
    private WsEndpoint? _handedFront;
    private bool _frontHanded;

    // Сколько ждать остановки туннеля перед подъёмом заново.
    private const int RestartTickMs = 100;
    private const int RestartWaitTicks = 50;

    // Setting keys of the retry, which this platform holds fixed.
    private const string PeriodicReconnectKey = "periodic-reconnect-enabled";
    private const string ReconnectIntervalKey = "periodic-reconnect-interval-seconds";
    private bool _connectFailed;
    private string _connectFailReason = string.Empty;
    private string _connectFailDetail = string.Empty;

    // Failed attempts in a row of the connect the tunnel is dialling.
    private int _retryAttempt;
    private bool _started;

    // Whether a listener was there when the agent announced itself.
    private bool _heard;
    private bool _loaded;
    private bool _disposed;
    private string _logLevel = "error";
    private bool _routeLog;
    private bool _directTcp = true;
    private bool _excludeRoutes = true;
    private bool _localInTunnel;
    private string _tunnelNotice = TunnelNoticeModes.Full;
    private int _routeTtl = 300;
    private int _splitLimit = SplitBudget.DefaultLimit;
    // Whether the plan in force adds the named applications to the rules by connection; read off the plan when the
    // process has not built one yet.
    private VpnRequest? _appliedRequest;
    private string? _acceptedSessionId;
    private bool _geoAutoCheck = true;
    private int _geoCheckIntervalHours = 24;
    private bool _subscriptionAutoRefresh = true;
    private int _subscriptionIntervalHours = DefaultSubscriptionIntervalHours;
    private DateTimeOffset _geoCheckedAt;
    private readonly GeoRecheck _geoRecheck = new(TimeSpan.FromMinutes(GeoRecheckMinutes), GeoRechecks);
    private readonly CancellationTokenSource _geoChecks = new();
    private bool _alwaysOn;
    private bool _alwaysOnLockdown;
    private LocalProxyOptions _proxyOptions = new();
    private string _proxyOfferLine = string.Empty;

    public event Action? Connected;

    public event Action? Disconnected;

    public event Action<StatusSnapshot>? SnapshotReceived;

    /// <summary>
    /// The in-process agent instance.
    /// </summary>
    public static AndroidAgentConnection? Current { get; private set; }

    internal static event Action? ConnectionActivityChanged;

    /// <summary>
    /// Latest snapshot pushed to the listeners.
    /// </summary>
    public StatusSnapshot? Latest { get; private set; }

    /// <summary>
    /// Awaits the store load and republishes the snapshot it filled.
    /// </summary>
    public async Task ReadyAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        PushSnapshot();
    }

    /// <summary>
    /// ctor
    /// </summary>
    public AndroidAgentConnection()
    {
        var dir = Application.Context.FilesDir?.AbsolutePath ?? ".";
        _storePath = System.IO.Path.Combine(dir, "agent.json");
        var geoFiles = new AndroidGeoFileStore(System.IO.Path.Combine(dir, "geo"));
        _geoFiles = geoFiles;
        _store = new SqliteStateStore(System.IO.Path.Combine(dir, "state.db"));
        _geoHttp = new GeoHttp(_httpClient, NullLogger<GeoHttp>.Instance, PastTunnel.ConnectAsync);
        _geoUpdater = new GeoFileUpdater(_store, _geoHttp, geoFiles);
        _geoChecker = new GeoUpdateChecker(_store, _geoHttp, geoFiles);
        _geo = new GeoConfigurator(_store, geoFiles);
        _log = new AndroidAgentLog(System.IO.Path.Combine(dir, "log.db"));
        _log.Context = NetworkContext;
        _updater = new AndroidUpdater(_httpClient, _log, PushSnapshot, AppVersion, UpdateServers, Save);
        _offers = new ServerOffers(
            _store,
            OfferNote,
            (point, ct) => ServerHello.AskAsync(point, PastTunnel.ConnectAsync, ct),
            geo: _geo,
            fetch: sources => _ = Task.Run(() => FetchOfferedAsync(sources)),
            take: (ids, ct) => Task.Run(() => TakeVersionsAsync(ids), ct),
            carried: Carries,
            dialed: DialedAsync,
            debug: message => _log.Debug("agent", message));
        Current = this;
    }

    // Whether the tunnel of a config is up now: the hello then goes inside it.
    private bool Carries(string config) =>
        _boundStatus == ConnectionStatus.Connected && string.Equals(_boundTarget, config, StringComparison.Ordinal);

    // Returns the address the tunnel of a config dials, as the network under the tunnel names its server.
    private async Task<string?> DialedAsync(string config, CancellationToken ct)
    {
        var host = ConfigServices.Host(_configs.GetValueOrDefault(config));

        return host.Length == 0 ? null : (await PastTunnel.AddressAsync(host, ct).ConfigureAwait(false))?.ToString();
    }

    // Tells whether an address lies in a network the device stands in; the window takes the networks it read within
    // the last ten seconds, a connect reads them anew.
    private Func<string, bool> Beside(bool fresh = false)
    {
        var now = System.Environment.TickCount64;
        if (fresh || _localReadAt == 0 || now - _localReadAt > LocalReadMs)
        {
            _localNets = [.. GeoVpnService.LocalSubnets()];
            _localReadAt = now;
        }

        return PrivateNetworks.Beside(_localNets);
    }

    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        // A window that joins an agent the console raised is told what the agent has told nobody.
        if (_started)
        {
            if (!_heard && Connected is not null)
            {
                _heard = true;
                Connected.Invoke();
                PushSnapshot();
            }

            // Looks at the tunnel process before a command reads the stage.
            LookAtTunnel();
            return;
        }

        _started = true;
        _heard = Connected is not null;
        EnsureLoaded();
        _log.SetCaptureLevel(_logLevel);
        _log.SetRouteLog(_routeLog);
        _events = new VpnBridge.Listener { Handler = OnVpnEvent };
        VpnBridge.Listen(Application.Context, _events, VpnBridge.ActionEvent);
        MainActivity.Resumed += SyncTunnelState;
        MainActivity.ShownChanged += PaceWatch;
        MainActivity.ShownChanged += WatchLinks;
        WatchLinks(MainActivity.Shown);
        TakeTunnelStage();
        Connected?.Invoke();
        PushSnapshot();
        SyncTunnelState();
        _ticks.PostDelayed(WatchTick, HeadPace.WatchMs(MainActivity.Shown));
        _ = EnsureInitAsync().ContinueWith(_ => PushSnapshot(), TaskScheduler.Default);
        _ = CheckGeoUpdatesAsync(_geoChecks.Token);
        _ = RefreshSubscriptionsAsync(_geoChecks.Token);
    }

    /// <summary>
    /// Sends a command and localizes the reply.
    /// </summary>
    public async Task<IpcAck> SendCommandAsync(IpcCommand command)
    {
        var ack = await DispatchAsync(command).ConfigureAwait(false);
        return IpcMessage.TryParse(ack.Message, out var key, out var args)
            ? ack with { Message = Loc.Instance.Get(key, args) }
            : ack;
    }

    // Reads the version the package manager reports for this build.
    private static string ReadAppVersion()
    {
        try
        {
            var context = Application.Context;
            var name = context.PackageName;
            var info = name is null ? null : context.PackageManager?.GetPackageInfo(name, 0);
            return info?.VersionName ?? string.Empty;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("AndroidAgent", "reading the package version failed: " + ex);
            return string.Empty;
        }
    }

    public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => DispatchAsync(command);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _geoChecks.Cancel();
        MainActivity.Resumed -= SyncTunnelState;
        MainActivity.ShownChanged -= PaceWatch;
        MainActivity.ShownChanged -= WatchLinks;
        WatchLinks(false);
        _ticks.RemoveCallbacksAndMessages(null);
        if (_events is not null)
        {
            Application.Context.UnregisterReceiver(_events);
            _events = null;
        }

        _geoChecks.Dispose();
        _updater.Dispose();
        _geoHttp.Dispose();
        _httpClient.Dispose();
        _log.Dispose();
        if (_started)
        {
            Disconnected?.Invoke();
        }
    }

    // Отбивает конфиг, который движок отверг бы при подъёме туннеля.
    private static IpcAck? RejectBadConfig(string text)
    {
        try
        {
            WgConfigValidator.Validate(text);
            return null;
        }
        catch (WgConfigFormatException ex)
        {
            return new IpcAck(false, ex.UnknownKey
                ? IpcMessage.Key("Agent_ConfigUnsupportedKey", ex.Offender)
                : IpcMessage.Key("Agent_ConfigRejected", ex.Message));
        }
    }

    private async Task<IpcAck> DispatchAsync(IpcCommand command)
    {
        var args = command.Args;
        switch (command.Op)
        {
            case IpcContract.OpImportConfig:
            case IpcContract.OpEditConfig:
                await EnsureInitAsync().ConfigureAwait(false);
                if (args.Count < 2)
                {
                    return Fail();
                }

                var rejected = RejectBadConfig(args[1]);
                if (rejected is not null)
                {
                    return rejected;
                }

                // Import creates; replacing the text of an existing configuration is edit-config.
                if (command.Op == IpcContract.OpImportConfig && _configs.ContainsKey(args[0]))
                {
                    return new IpcAck(false, IpcMessage.Key("Agent_ConfigNameTaken", args[0]));
                }

                _configs[args[0]] = args[1];

                // A first import is ready to dial: it takes the selection while there is none.
                if (command.Op == IpcContract.OpImportConfig && _selectedTarget is null)
                {
                    _selectedTarget = args[0];
                }

                Save();
                PushSnapshot();
                _offers.Warm([(args[0], _configs.GetValueOrDefault(args[0]))], OfferChangedAsync);
                return Ok();

            // One head, one process: presence needs no announcing here; the servers are asked what they offer.
            case IpcContract.OpAttachUi:
                await EnsureInitAsync().ConfigureAwait(false);
                _offers.Warm(OfferTargets(), OfferChangedAsync);
                return Ok();

            case IpcContract.OpAddConfig:
                return AskServers(await AddConfigAsync(args).ConfigureAwait(false));

            case IpcContract.OpCopyConfig:
                return await CopyConfigAsync(args).ConfigureAwait(false);

            case IpcContract.OpAddSubscription:
                return await AskNewServersAsync(await AddSubscriptionAsync(args).ConfigureAwait(false)).ConfigureAwait(false);

            case IpcContract.OpListSubscriptions:
                return await ListSubscriptionsAsync().ConfigureAwait(false);

            case IpcContract.OpRefreshSubscription:
                return await AskNewServersAsync(await RefreshSubscriptionAsync(args).ConfigureAwait(false)).ConfigureAwait(false);

            case IpcContract.OpRemoveSubscription:
                return await RemoveSubscriptionAsync(args).ConfigureAwait(false);

            case IpcContract.OpConfigSubscription:
                return await ConfigSubscriptionAsync(args).ConfigureAwait(false);

            case IpcContract.OpRemoveConfig:
                return await RemoveConfigAsync(args).ConfigureAwait(false);

            case IpcContract.OpGetConfig:
                return args.Count > 0 && _configs.TryGetValue(args[0], out var text)
                    ? new IpcAck(true, text)
                    : Fail();

            case IpcContract.OpSelectConfig:
                return await SelectConfigAsync(args).ConfigureAwait(false);

            case IpcContract.OpSetConnection:
                return await SetConnectionAsync(args.Count > 0 ? args[0] : string.Empty);

            case IpcContract.OpRenameConfig:
                return await RenameConfigAsync(args).ConfigureAwait(false);

            case IpcContract.OpReorderConfigs:
                return ReorderConfigs(args);

            case IpcContract.OpAssignRouting:
                return await AssignRoutingAsync(args).ConfigureAwait(false);

            case IpcContract.OpChangeRoutingSelection:
                return await ChangeRoutingAsync(RoutingSelectionChange.Parse(args)).ConfigureAwait(false);

            case IpcContract.OpAddSource:
                return await AddSourceAsync(args);

            case IpcContract.OpRemoveSource:
                return await RemoveSourceAsync(args);

            case IpcContract.OpEditSource:
                return await EditSourceAsync(args);

            case IpcContract.OpUpdateSource:
                return await UpdateSourceAsync(args);

            case IpcContract.OpUpdateSources:
            case IpcContract.OpDownloadGeo:
                return await UpdateAllSourcesAsync();

            case IpcContract.OpCheckSource:
                return await CheckSourceAsync(args);

            case IpcContract.OpCheckSources:
                return await CheckSourcesAsync();

            case IpcContract.OpRefreshSources:
                await EnsureInitAsync().ConfigureAwait(false);
                await RefreshGeoSourcesAsync().ConfigureAwait(false);
                PushSnapshot();
                return Ok();

            case IpcContract.OpListLocalSubnets:
                return new IpcAck(true, string.Join('\n', GeoVpnService.LocalSubnets()));

            case IpcContract.OpListTunnelSubnets:
                await EnsureInitAsync().ConfigureAwait(false);
                return new IpcAck(true, string.Join('\n', ConfigSubnets()));

            // Гео-базы разбираются в пуле: разворачивание правил держит вызывающий поток, а он тут UI-шный.
            case IpcContract.OpListGeo:
                return await Task.Run(ListGeoAsync).ConfigureAwait(false);

            case IpcContract.OpGetGeoEntries:
                return await Task.Run(() => GetGeoEntriesAsync(args)).ConfigureAwait(false);

            case IpcContract.OpSaveRoutingList:
                return Searched(await Task.Run(() => UnlessReadOnlyAsync(args, SaveRoutingListAsync)).ConfigureAwait(false));

            case IpcContract.OpGetRoutingList:
                return await Task.Run(() => GetRoutingListAsync(args)).ConfigureAwait(false);

            case IpcContract.OpCountRoutes:
                return await Task.Run(() => CountRoutesAsync(args)).ConfigureAwait(false);

            case IpcContract.OpRemoveRoutingList:
                return await Task.Run(() => UnlessReadOnlyAsync(args, RemoveRoutingListAsync)).ConfigureAwait(false);

            case IpcContract.OpUpdateRoutingList:
                return Searched(await Task.Run(() => UpdateRoutingListAsync(args)).ConfigureAwait(false));

            case IpcContract.OpReorderRoutingLists:
                return await Task.Run(() => ReorderRoutingListsAsync(args)).ConfigureAwait(false);

            case IpcContract.OpGetRoutingSettings:
                return await Task.Run(() => GetRoutingSettingsAsync(args)).ConfigureAwait(false);

            case IpcContract.OpSetRoutingSettings:
                return await Task.Run(() => UnlessReadOnlyAsync(args, SetRoutingSettingsAsync)).ConfigureAwait(false);

            case IpcContract.OpSetWebSocket:
                return await SetWebSocketAsync(args);

            case IpcContract.OpSetSignal:
                return await SetSignalAsync(args).ConfigureAwait(false);

            case IpcContract.OpSetHoldTraffic:
                return await SetHoldTrafficAsync(args).ConfigureAwait(false);

            case IpcContract.OpSetConfigVpnDns:
                return await SetConfigVpnDnsAsync(args).ConfigureAwait(false);

            case IpcContract.OpSetGeo:
                return await SetGeoAsync(args);

            // The tunnel here resolves names through its own trap and routes by list, so neither of these
            // reaches the session: the setting would be stored and never applied.
            case IpcContract.OpSetConfigDns:
            case IpcContract.OpSetConfigExclusions:
                return new IpcAck(false, IpcMessage.Key("Android_PerConfigResolverUnsupported"));

            case IpcContract.OpListProcesses:
                return ListProcesses();

            case IpcContract.OpCollectDiagnostics:
                return await CollectDiagnosticsAsync();

            case IpcContract.OpCheckUpdate:
                return await _updater.CheckAsync(args.Count > 0 && args[0] == "silent", CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpDownloadUpdate:
            {
                var started = _updater.StartDownload(CancellationToken.None);
                PushSnapshot();
                return started;
            }

            case IpcContract.OpCancelUpdateDownload:
                return _updater.Cancel();

            case IpcContract.OpApplyUpdate:
                return await _updater.InstallAsync(CancellationToken.None).ConfigureAwait(false);

            // The agent owns the download here, so a client report changes nothing.
            case IpcContract.OpReportUpdateDownload:
                return Ok();

            case IpcContract.OpReadLog:
                return await ReadLogAsync(args);

            case IpcContract.OpClearLog:
                return await ClearLogAsync(args);

            case IpcContract.OpExportLog:
                return await ExportLogAsync(args);

            case IpcContract.OpSetSetting:
                return SetSetting(args);

            case IpcContract.OpLogClient:
                return LogClient(args);

            case IpcContract.OpGetRuntimeConfig:
                return await GetRuntimeConfigAsync();

            case IpcContract.OpGetSessions:
                return await GetSessionsAsync();

            case IpcContract.OpKnownHosts:
                return await KnownHostsAsync(CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpCheckChannel:
                return await CheckChannelAsync(args, CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpLinkNow:
                return LookAtLink();

            case IpcContract.OpProbeServers:
                return await ProbeServersAsync(CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpCheckServers:
                return await CheckServersAsync(CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpCheckTarget:
                return await CheckTargetAsync(args, CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpProbeTarget:
                return await ProbeTargetAsync(args, CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpServerOffer:
                return await ServerOfferAsync().ConfigureAwait(false);

            case IpcContract.OpAskServers:
                await EnsureInitAsync().ConfigureAwait(false);
                return AskServers(new IpcAck(true, string.Empty));

            case IpcContract.OpExportBundle:
                return await ExportBundleAsync(args);

            case IpcContract.OpImportBundle:
                return await AskNewServersAsync(await ImportBundleAsync(args)).ConfigureAwait(false);

            case IpcContract.OpOpenVpnSettings:
                return OpenVpnSettings();

            default:
                _log.Warn("agent", $"command '{command.Op}' is not wired in the Android agent");
                return new IpcAck(false, IpcMessage.Key("Android_OpNotWired", command.Op));
        }
    }

    private Task<IpcAck> SetConnectionAsync(string desired) => desired == "disconnect"
        ? Task.FromResult(_connectPreparation.Disconnect(DisconnectTunnel).Ack)
        : QueueConnectAsync();

    private Task<IpcAck> QueueConnectAsync(long? afterDisconnect = null)
    {
        var restartRequired = false;
        return _connectPreparation.RunAsync(ct =>
        {
            restartRequired = _restartRequired;
            return ConnectTunnelAsync(ct);
        }, ex => ConnectStartFailed(restartRequired, ex), afterDisconnect);
    }

    private IpcAck DisconnectTunnel()
    {
        if (!RevokePendingLaunch())
        {
            return new IpcAck(false, "could not revoke the pending VPN launch");
        }

        ClearDialIntent();
        Save();
        _log.Info("agent", "disconnect requested");

        // Revocation and promotion compete for one file. If promotion won, stop that committed session only.
        var committed = VpnBridge.ReadRequest();
        if (VpnBridge.IsRunning(Application.Context) && (committed is not null || _boundStatus == ConnectionStatus.Connected))
        {
            VpnBridge.RequestStop(Application.Context, committed?.SessionId ?? string.Empty);
        }
        else
        {
            VpnBridge.ClearRequest();
            OnVpnStateChanged(VpnStage.Disconnected, null);
        }

        return Ok();
    }

    private bool RevokePendingLaunch()
    {
        if (_pendingLaunch is not { } launch)
        {
            return true;
        }

        if (!VpnBridge.DropStagedRequest(launch.Id))
        {
            return false;
        }

        _pendingLaunch = null;
        _log.Info("agent", "[FIX] revoked the pending VPN launch");
        return true;
    }

    private async Task<IpcAck> ConnectTunnelAsync(CancellationToken ct)
    {
        IpcAck Refuse(ConnectFailureReason reason, string detail, string message) => _connectPreparation.Apply(() =>
        {
            SetConnectFailure(reason.ToString(), detail);
            _log.Warn("agent", "connect refused: " + message);
            PushSnapshot();
            return new IpcAck(false, message);
        }, ct);

        if (_selectedTarget is not { Length: > 0 } configName)
        {
            return Refuse(ConnectFailureReason.NoTargetSelected, string.Empty, "nothing selected");
        }

        if (!_configs.TryGetValue(configName, out var configText))
        {
            return Refuse(ConnectFailureReason.ConfigMissing, configName, $"configuration '{configName}' not found");
        }

        if (!await EnsureVpnPermissionAsync())
        {
            return Refuse(ConnectFailureReason.PermissionDenied, string.Empty, "vpn permission denied");
        }

        var (standing, restart) = _connectPreparation.Apply(() =>
        {
            // A tunnel connected on another configuration stays until the server of this one answers.
            var live = _active && VpnBridge.IsRunning(Application.Context);
            var previous = SwitchGuard.Standing(live, _boundStatus, _boundTarget, configName);
            var restartRequired = _restartRequired;
            _raiseOwed = false;
            ClearConnectFailure();
            _retryAttempt = 0;
            _restartRequired = false;
            _dialWanted = true;
            _frontHanded = false;
            _watch.Asked(live);
            Save();
            _active = true;
            _boundStatus = ConnectionStatus.Connecting;
            _boundTarget = configName;
            PushSnapshot();
            return (previous, restartRequired);
        }, ct);

        var offer = await _offers.BeforeConnectAsync(configName, configText, OfferChangedAsync, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await RefreshOffersAsync().ConfigureAwait(false);

        // A list the server put in use joins the plan of this connect, and the lists it handed out show at once.
        ct.ThrowIfCancellationRequested();
        if (await RefreshRoutingSelectionAsync().ConfigureAwait(false))
        {
            _connectPreparation.Apply(() => _restartRequired = false, ct);
        }

        var (_, available) = await KeepServerListsAsync(configName).ConfigureAwait(false);
        if (!available)
        {
            return _connectPreparation.Apply(() =>
            {
                ApplyConnectFailure(nameof(ConnectFailureReason.TunnelSetupFailed), ServerListsOnly.UnavailableDetail, restart);
                return new IpcAck(false, ServerListsOnly.UnavailableDetail);
            }, ct);
        }

        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        await RefreshGeoSourcesAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (standing is not null && SwitchGuard.Keeps(await AskedAsync(configName, configText, ct).ConfigureAwait(false)))
        {
            return _connectPreparation.Apply(() => Kept(configName, standing, restart), ct);
        }

        var transport = _transports.GetValueOrDefault(configName);

        // Правила разворачиваются в пуле: агент живёт в процессе UI, и план большого списка держит поток.
        var planStarted = System.Environment.TickCount64;
        (GeoRoutingPlan Plan, string Stamp, string? AppMode, string[] Apps, string[] Bypass) session;
        try
        {
            session = await Task.Run(() => PrepareSessionPlanAsync(configName, transport, ct), ct).ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            return _connectPreparation.Apply(() =>
            {
                _log.Warn("geo", $"Android API {(int)Build.VERSION.SdkInt}: {ex.Message}");
                ApplyConnectFailure(nameof(ConnectFailureReason.TunnelSetupFailed), ex.Message, true);
                return new IpcAck(false, ex.Message);
            }, ct);
        }

        ct.ThrowIfCancellationRequested();
        var words = await NoticeWordsAsync(session.Plan).ConfigureAwait(false);
        var dns = await _store.GetConfigDnsAsync(configName, ct).ConfigureAwait(false);
        var vpnDns = VpnDns.Pick(dns, offer.Dns(), WgConfigEditor.GetDns(configText), Beside(true));
        var front = Front(configName, configText);
        var signal = TunnelSignal.Of(offer, configText, transport?.UseIpv6 == true, transport?.AllowSignal != false);
        var request = new VpnRequest(configText, configName,
            session.AppMode, session.Apps,
            transport?.Mtu ?? 0, transport?.UseIpv6 == true, front?.Address(), front?.Port ?? 0,
            EngineLog: EngineLogLevel(_logLevel), MtuMode: (int)(transport?.MtuMode ?? default),
            DirectTcp: _directTcp, ExcludeRoutes: _excludeRoutes, BypassApps: session.Bypass,
            LocalInTunnel: _localInTunnel, WsOffered: front?.Offered == true, Notice: words,
            SignalPort: signal?.Port ?? 0, SignalFrom: signal is null ? null : [.. signal.Sources],
            Bases: [.. _geoSources.Select(source => source.Url), LinkCause.UpdateBase],
            HoldTraffic: transport?.HoldTraffic != false,
            VpnDns: vpnDns is null ? null : [.. vpnDns.Servers], VpnDnsNetwork: vpnDns?.Network == true,
            Plan: session.Plan, PlanStamp: session.Stamp);
        ct.ThrowIfCancellationRequested();
        var id = VpnBridge.StageRequest(request);
        var delivery = new PendingLaunch(id, configName,
            DownKey(configName, words), front, restart);
        var started = false;
        try
        {
            return _connectPreparation.Apply(() =>
            {
                if (!string.Equals(configName, _selectedTarget, StringComparison.Ordinal))
                {
                    RestoreCancelledPreparation();
                    return VpnConnectPreparation.Cancelled;
                }

                _log.Debug("geo", $"routing {session.Stamp[..12]} prepared for Android API {(int)Build.VERSION.SdkInt}: "
                    + $"{session.Plan.ListIds.Count} list(s), {(session.Plan.FlowRules ? "per-flow" : "system routes")}, "
                    + $"{session.Apps.Length + session.Bypass.Length} filtered package(s), "
                    + $"{System.Environment.TickCount64 - planStarted} ms");
                _log.Info("agent", $"connect requested: config '{configName}'");
                if (!RevokePendingLaunch())
                {
                    throw new IOException("could not revoke the previous pending VPN launch");
                }

                StartService(id);
                _pendingLaunch = delivery;
                started = true;
                return new IpcAck(true, "connecting");
            }, ct);
        }
        finally
        {
            if (!started)
            {
                VpnBridge.DropStagedRequest(id);
            }
        }
    }

    private IpcAck ConnectStartFailed(bool restartRequired, Exception error)
    {
        var failedTarget = _boundTarget ?? _selectedTarget ?? string.Empty;
        _log.Error("agent", "[FIX] preparing or starting the VPN failed", error);

        ApplyConnectFailure(nameof(ConnectFailureReason.ServiceStartFailed), failedTarget, restartRequired);
        return new IpcAck(false, "could not start vpn service");
    }

    private void ApplyConnectFailure(string reason, string config, bool restartRequired)
    {
        if (TryRestoreRunningTunnel())
        {
            _restartRequired = restartRequired;
            SetConnectFailure(reason, config);
            PushSnapshot();
        }
        else
        {
            ClearDialIntent();
            Save();
            OnVpnStateChanged(VpnStage.Failed, config, reason);
        }
    }

    private void ClearDialIntent()
    {
        _dialWanted = false;
        _frontHanded = false;
        _raiseOwed = false;
        _watch.Dropped();
    }

    private bool TryRestoreRunningTunnel()
    {
        if (!VpnBridge.IsRunning(Application.Context)
            || VpnBridge.ReadStage() is not { Stage: VpnStage.Connected or VpnStage.Connecting } live
            || live.SessionId != VpnBridge.ReadRequest()?.SessionId)
        {
            return false;
        }

        _active = true;
        _boundTarget = live.Detail;
        _boundStatus = live.Stage == VpnStage.Connected ? ConnectionStatus.Connected : ConnectionStatus.Connecting;
        if (live.Stage == VpnStage.Connected)
        {
            ConfirmRouting(live.SessionId);
        }
        _acceptedSessionId = live.SessionId;
        _watch.Heard();
        return true;
    }

    // Hands the server of a configuration to the tunnel, which dials it past itself, and waits for its word; a
    // tunnel that says nothing leaves the verdict unknown.
    private async Task<string> AskedAsync(string name, string text, CancellationToken ct)
    {
        var front = Front(name, text);
        var id = Guid.NewGuid().ToString("N");
        VpnBridge.ClearSwitchResult();
        VpnBridge.WriteSwitch(new SwitchRequest(text, name, front?.Address(), front?.Port ?? 0, front?.Offered ?? false, EngineLogLevel(_logLevel), id));
        try
        {
            VpnBridge.RequestSwitch(Application.Context);
            return await SwitchGuard
                .AwaitAsync(() => VpnBridge.ReadSwitchResult(id), SwitchWaitMs, ProbePollMs, pause => Task.Delay(pause, ct))
                .ConfigureAwait(false);
        }
        finally
        {
            VpnBridge.ClearSwitchResult();
            VpnBridge.ClearSwitch();
        }
    }

    // Puts the head back on the configuration the tunnel stands on: the server of the one asked for kept silent.
    private IpcAck Kept(string asked, string standing, bool restart)
    {
        _log.Warn("agent", $"connect refused: the server of '{asked}' did not answer, so the tunnel stays on '{standing}'");
        if (string.Equals(_selectedTarget, asked, StringComparison.Ordinal))
        {
            Journal(SwitchLog.Config(asked, standing));
            _selectedTarget = standing;
        }

        _boundStatus = ConnectionStatus.Connected;
        _boundTarget = standing;
        _restartRequired = restart;
        SetConnectFailure(nameof(ConnectFailureReason.SwitchNoHandshake), asked);
        Save();
        PushSnapshot();
        _ = RetellAsync();
        return new IpcAck(false, $"the server of '{asked}' did not answer; '{standing}' stays connected");
    }

    // Tells the snapshot once more, after the window has taken the refusal of its command.
    private async Task RetellAsync()
    {
        await Task.Delay(RetellMs).ConfigureAwait(false);
        PushSnapshot();
    }

    private static async Task<bool> EnsureVpnPermissionAsync()
    {
        var prepare = VpnService.Prepare(Application.Context);
        if (prepare is null)
        {
            return true;
        }

        var activity = MainActivity.Current;
        return activity is not null && await activity.RequestVpnPermissionAsync(prepare);
    }

    // Движок пишет каждое своё решение через JNI: на диагностическом уровне это заметная доля времени пакета.
    private static int EngineLogLevel(string level)
    {
        return level switch
        {
            "none" => 0,
            "trace" or "debug" => 2,
            _ => 1,
        };
    }

    private static void StartService(string sessionId)
    {
        var context = Application.Context;
        using var intent = new Intent(context, typeof(GeoVpnService));
        intent.SetAction(GeoVpnService.ActionConnect);
        intent.PutExtra(GeoVpnService.ExtraSessionId, sessionId);
        using var service = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? context.StartForegroundService(intent)
            : context.StartService(intent);
        if (service is null)
        {
            throw new InvalidOperationException("Android did not start the VPN service");
        }
    }

    // The words of the tunnel notification in the language the window is set to, with the routing list in use.
    private async Task<NoticeWords> NoticeWordsAsync(GeoRoutingPlan? plan = null)
    {
        var language = UiPreferences.Load().Language;
        var takes = _selectedTarget is not { Length: > 0 } name
            || ConfigRouting.Allowed(_transports.GetValueOrDefault(name));
        var list = plan?.ListName ?? Volatile.Read(ref _appliedRequest)?.Plan?.ListName
            ?? (takes ? await ListNameAsync(SelectedRoutingList).ConfigureAwait(false) : null) ?? string.Empty;
        var lists = takes && (list.Length > 0 || await AnyListAsync().ConfigureAwait(false));
        return new NoticeWords(
            Loc.GetIn(language, "Status_Connected"),
            Loc.GetIn(language, "Status_Connecting"),
            Loc.GetIn(language, "Notice_Attempt"),
            Loc.GetIn(language, "Status_Disconnected"),
            Loc.GetIn(language, "Notice_Stopped"),
            Loc.GetIn(language, "Notice_SpeedMbit"),
            Loc.GetIn(language, "Notice_SpeedKbit"),
            Loc.GetIn(language, "Notice_Routing"),
            Loc.GetIn(language, "Notice_RoutingOff"),
            Loc.GetIn(language, "Notice_Disconnect"),
            Loc.GetIn(language, "Notice_Connect"),
            Loc.GetIn(language, "Notice_StoppedChannel"),
            Loc.CultureOf(language).Name,
            list,
            lists,
            _tunnelNotice == TunnelNoticeModes.Brief);
    }

    // Whether there is a routing list at all.
    private async Task<bool> AnyListAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        return (await _store.ListRoutingListsAsync().ConfigureAwait(false)).Count > 0;
    }

    // What the notification of a tunnel taken down says of a configuration and its words.
    private static string DownKey(string name, NoticeWords words) =>
        string.Join('\n', name, words.List, words.Lists ? "1" : "0", words.Culture);

    // Keeps the notification a tunnel that is down left on the shade true to the configuration and the list chosen
    // now, or takes it off when the notification is brief; a tunnel whose process is gone and which is to be raised
    // again shows as connecting.
    private async Task RetellDownAsync()
    {
        try
        {
            var context = Application.Context;
            if (_active || _selectedTarget is not { Length: > 0 } name || !TunnelNotices.Shown(context, TunnelNotices.TunnelId))
            {
                return;
            }

            var words = await NoticeWordsAsync().ConfigureAwait(false);
            var owed = _raiseOwed;
            if (words.Brief && !owed)
            {
                TunnelNotices.Cancel(context, TunnelNotices.TunnelId);
                return;
            }

            var key = DownKey(name, words) + (owed ? "|raise" : string.Empty);
            if (_active || string.Equals(key, _downNotice, StringComparison.Ordinal))
            {
                return;
            }

            _downNotice = key;
            TunnelNotices.Post(context, TunnelNotices.TunnelId, TunnelNotices.Compose(context, words, name,
                owed ? NoticeStage.Connecting : NoticeStage.Disconnected, 0, 0, 0, Java.Lang.JavaSystem.CurrentTimeMillis()));
        }
        catch (Exception ex)
        {
            _log.Warn("agent", "the notification of the tunnel was not brought up to date: " + ex);
        }
    }

    // Hands a running tunnel the words of its notification again.
    private async Task RetellRunningAsync()
    {
        try
        {
            var context = Application.Context;
            if (!VpnBridge.IsRunning(context))
            {
                return;
            }

            var words = await NoticeWordsAsync().ConfigureAwait(false);
            VpnBridge.RequestNotice(context, JsonSerializer.Serialize(words));
        }
        catch (Exception ex)
        {
            _log.Warn("agent", "the words of the notification were not handed to the tunnel: " + ex);
        }
    }

    // Takes the stage the running tunnel wrote last, so the first snapshot shows what runs.
    private void TakeTunnelStage()
    {
        if (!VpnBridge.IsRunning(Application.Context) || VpnBridge.ReadStage() is not { } told
            || told.Stage is not (VpnStage.Connecting or VpnStage.Connected)
            || told.SessionId != VpnBridge.ReadRequest()?.SessionId)
        {
            return;
        }

        _active = true;
        _watch.Heard();
        _boundStatus = told.Stage == VpnStage.Connected ? ConnectionStatus.Connected : ConnectionStatus.Connecting;
        _boundTarget = told.Detail.Length > 0 ? told.Detail : _selectedTarget;
        _acceptedSessionId = told.SessionId;
        if (told.Stage == VpnStage.Connected)
        {
            ConfirmRouting(told.SessionId);
        }
        global::Android.Util.Log.Info("AndroidAgent", $"the head comes up on the stage the tunnel wrote: {told.Stage} {_boundTarget}");
    }

    // Reads what is running: the tunnel lives in another process and can be gone without a word, so the window
    // asks again whenever it comes up.
    private void SyncTunnelState() => _connectPreparation.Apply(() =>
    {
        if (_disposed || _connectPreparation.IsPending || _pendingLaunch is not null)
        {
            return;
        }

        if (VpnBridge.IsRunning(Application.Context))
        {
            VpnBridge.RequestState(Application.Context);
            return;
        }

        if (LookAtTunnel())
        {
            return;
        }

        if (_active)
        {
            OnVpnStateChanged(VpnStage.Disconnected, null);
            return;
        }

        if (_watch.Due(_active, StillWanted(), System.Environment.TickCount64))
        {
            _log.Note("agent", "the window is back on the screen; the session that went with the tunnel process is raised again");
            _ = SetConnectionAsync("connect");
        }
    });

    // Looks at the tunnel process and comes back after the pause.
    private void WatchTick()
    {
        if (_disposed)
        {
            return;
        }

        LookAtTunnel();
        _ticks.PostDelayed(WatchTick, HeadPace.WatchMs(MainActivity.Shown));
    }

    // Ставит взгляды на процесс туннеля на темп окна, которое пришло на экран или ушло с него.
    private void PaceWatch(bool shown)
    {
        if (_disposed)
        {
            return;
        }

        _ticks.RemoveCallbacksAndMessages(null);
        _ticks.PostDelayed(WatchTick, HeadPace.WatchMs(shown));
    }

    /// <summary>
    /// Собирает снимок заново, если показание связи в него не попало или включён прокси.
    /// </summary>
    public void CatchUp()
    {
        if (_linkUntold || _proxyOptions.Enabled)
        {
            PushSnapshot();
        }
    }

    // Asks the tunnel to keep its echoes at the watched pace and answers with what it showed last.
    private IpcAck LookAtLink()
    {
        if (!_active || _boundTarget is not { Length: > 0 } name)
        {
            return new IpcAck(true, string.Empty);
        }

        VpnBridge.RequestWatch(Application.Context);
        return new IpcAck(true, _linkNow.ToRow(name));
    }

    // Собирает снимок по показанию связи; без окна на экране делает это редко.
    private void TellLink()
    {
        var now = System.Environment.TickCount64;
        if (!HeadPace.TellsLink(MainActivity.Shown, now, _linkToldAt))
        {
            _linkUntold = true;
            return;
        }

        _linkToldAt = now;
        PushSnapshot();
    }

    // Takes a session whose tunnel process is gone: raised again while the window is on the screen, shown as down
    // otherwise.
    private bool LookAtTunnel() => _connectPreparation.Apply(() =>
    {
        if (_connectPreparation.IsPending)
        {
            return false;
        }

        var active = _active;
        var step = _watch.Look(active, VpnBridge.IsRunning(Application.Context), active && StillWanted(), MainActivity.Shown,
            System.Environment.TickCount64);
        if (step is not { } taken)
        {
            return false;
        }

        _log.Note("agent", GoneNote(taken));
        _raiseOwed = taken == TunnelGoneStep.Owed;
        OnVpnStateChanged(VpnStage.Disconnected, null);
        switch (taken)
        {
            case TunnelGoneStep.Raise:
                _ = SetConnectionAsync("connect");
                break;
            case TunnelGoneStep.GiveUp:
                SetConnectFailure(nameof(ConnectFailureReason.ServiceStartFailed), "the tunnel process keeps dying");
                PushSnapshot();
                break;
        }

        return true;
    });

    // Whether the session that stood is still asked for: nobody took it down and the tunnel did not close it itself.
    private bool StillWanted() =>
        _dialWanted && VpnBridge.HasRequest()
        && VpnBridge.ReadStage() is { Stage: VpnStage.Connecting or VpnStage.Connected };

    // The line of the journal about a session that went with its process.
    private static string GoneNote(TunnelGoneStep step) => step switch
    {
        TunnelGoneStep.Raise => "the tunnel process is gone; the session is raised again",
        TunnelGoneStep.Owed => "the tunnel process is gone while the window is off the screen; the guard of the tunnel raises the session, or the window once it is back",
        TunnelGoneStep.GiveUp => $"the tunnel process is gone for the {GoneRaises + 1}th time in {GoneWindowMs / 1000} s; the session is not raised again",
        _ => "the tunnel process is gone; nobody asked for the session any more",
    };

    // What a stage the tunnel told means for the head beyond the snapshot: a session taken down from the
    // notification is no longer wanted, and a failure told to a window on the screen needs no notification.
    private void Heard(VpnStage stage)
    {
        if (stage == VpnStage.Disconnected && _dialWanted && _boundStatus != ConnectionStatus.Connecting && !VpnBridge.HasRequest())
        {
            _dialWanted = false;
            _watch.Dropped();
            Save();
        }

        if (stage == VpnStage.Failed && MainActivity.Shown)
        {
            TunnelNotices.Cancel(Application.Context, TunnelNotices.StoppedId);
        }

        _raiseOwed = false;
    }

    // Takes what the tunnel process reports: a stage change or a line for the routing log.
    private void OnVpnEvent(Intent intent)
    {
        if (_disposed)
        {
            return;
        }

        var note = intent.GetStringExtra(VpnBridge.ExtraNote);
        if (note is not null)
        {
            _log.Note(intent.GetStringExtra(VpnBridge.ExtraNoteSource) ?? "tunnel", note);
            return;
        }

        var trace = intent.GetStringExtra(VpnBridge.ExtraTrace);
        if (trace is not null)
        {
            _log.Route(trace);
            return;
        }

        var handshake = intent.GetLongExtra(VpnBridge.ExtraHandshake, -1);
        if (handshake >= 0)
        {
            _handshakeUnix = handshake;
            _link = new LinkReading(
                intent.GetLongExtra(VpnBridge.ExtraRxBits, 0),
                intent.GetLongExtra(VpnBridge.ExtraTxBits, 0),
                intent.GetIntExtra(VpnBridge.ExtraChurn, 0),
                intent.GetIntExtra(VpnBridge.ExtraLoss, LinkHealth.LossUnknown),
                intent.GetIntExtra(VpnBridge.ExtraRtt, -1),
                intent.GetBooleanExtra(VpnBridge.ExtraChurning, false),
                intent.GetIntExtra(VpnBridge.ExtraLossStreak, 0),
                intent.GetIntExtra(VpnBridge.ExtraRekey, -1))
            {
                Now = VpnBridge.ReadNow(intent) ?? LinkNow.Unknown,
            };
            _linkNow = _link.Now;
            LogLink(_link);
            TellLink();
            return;
        }

        if (VpnBridge.ReadNow(intent) is { } shown)
        {
            _linkNow = shown;
            return;
        }

        var stage = intent.GetIntExtra(VpnBridge.ExtraStage, -1);
        var launchId = intent.GetStringExtra(VpnBridge.ExtraLaunchId);
        if (stage >= 0 || launchId is not null)
        {
            _connectPreparation.Apply(() => TakeTunnelEvent(stage, launchId, intent));
        }
    }

    private void TakeTunnelEvent(int stageValue, string? launchId, Intent intent)
    {
        var reason = intent.GetStringExtra(VpnBridge.ExtraReason);
        var terminal = stageValue == (int)VpnStage.Disconnected || stageValue == (int)VpnStage.Failed;
        if (_pendingLaunch is { } launch)
        {
            // A reply for a revoked launch, or a stage from the previous tunnel, cannot settle this launch.
            if (launch.Id != launchId)
            {
                return;
            }

            _pendingLaunch = null;
            if (stageValue < 0)
            {
                ApplyConnectFailure(reason ?? nameof(ConnectFailureReason.ServiceStartFailed), launch.Config, launch.RestartRequired);
                return;
            }

            _acceptedSessionId = launchId;
            _downNotice = launch.DownNotice;
            _handedFront = launch.Front;
            _frontHanded = true;
        }
        else if (launchId != VpnBridge.ReadRequest()?.SessionId && (!terminal || launchId != _acceptedSessionId))
        {
            return;
        }

        if (stageValue < 0)
        {
            return;
        }

        var stage = (VpnStage)stageValue;
        if (!terminal && launchId is not null)
        {
            _acceptedSessionId = launchId;
        }

        if (stage == VpnStage.Connected)
        {
            ConfirmRouting(launchId);
        }

        if (stage is VpnStage.Connecting or VpnStage.Connected)
        {
            _watch.Heard();
        }

        _alwaysOn = intent.GetBooleanExtra(VpnBridge.ExtraAlwaysOn, false);
        _alwaysOnLockdown = intent.GetBooleanExtra(VpnBridge.ExtraLockdown, false);
        Heard(stage);
        OnVpnStateChanged(stage, intent.GetStringExtra(VpnBridge.ExtraDetail),
            reason, intent.GetIntExtra(VpnBridge.ExtraRetry, 0));
    }

    private void ConfirmRouting(string? sessionId)
    {
        var request = VpnBridge.ReadRequest();
        if (request is null || request.SessionId != sessionId)
        {
            return;
        }

        var previous = Interlocked.Exchange(ref _appliedRequest, request);
        if (previous?.SessionId != request.SessionId && request.PlanStamp is { Length: >= 12 } stamp)
        {
            _log.Info("geo", $"routing {stamp[..12]} confirmed for {request.Plan?.ListIds.Count ?? 0} list(s)");
        }
    }

    private void OnVpnStateChanged(VpnStage stage, string? detail, string? reason = null, int retry = 0)
    {
        // The session name comes back from the tunnel, so a head that started after it still names what runs.
        var session = string.IsNullOrEmpty(detail) ? _selectedTarget : detail;
        var again = stage == VpnStage.Connecting && retry > _retryAttempt;
        _retryAttempt = stage == VpnStage.Connecting ? retry : 0;
        switch (stage)
        {
            case VpnStage.Connecting:
                _active = true;
                _boundStatus = ConnectionStatus.Connecting;
                _boundTarget = session;
                break;
            case VpnStage.Connected:
                _active = true;
                if (_boundStatus != ConnectionStatus.Connected)
                {
                    FindAppsLater();
                }

                _boundStatus = ConnectionStatus.Connected;
                _boundTarget = session;
                if (!_restartRequired)
                {
                    ClearConnectFailure();
                }
                break;
            case VpnStage.Disconnected:
                _acceptedSessionId = null;
                Volatile.Write(ref _appliedRequest, null);
                _active = false;
                _restartRequired = false;
                _boundStatus = ConnectionStatus.Disconnected;
                _boundTarget = null;
                _handshakeUnix = 0;
                ResetLink();
                ResetAlwaysOn();
                ForgetKeptSwitch();
                break;
            case VpnStage.Failed:
                _acceptedSessionId = null;
                Volatile.Write(ref _appliedRequest, null);
                _active = false;
                _restartRequired = false;
                _boundStatus = ConnectionStatus.Disconnected;
                _boundTarget = null;
                _handshakeUnix = 0;
                ResetLink();
                ResetAlwaysOn();
                SetConnectFailure(reason ?? nameof(ConnectFailureReason.Unknown), detail ?? string.Empty);
                break;
        }

        LogVpnStage(stage, detail);
        PushSnapshot();
        if (again)
        {
            _ = AskDialedAsync(session);
        }
    }

    // A dial inside a websocket that goes on to another attempt has the server of its configuration asked again.
    private async Task AskDialedAsync(string? config)
    {
        if (config is { Length: > 0 } && _frontHanded && _configs.TryGetValue(config, out var text)
            && _transports.GetValueOrDefault(config) is { UseWebSocket: true })
        {
            try
            {
                await EnsureInitAsync().ConfigureAwait(false);
                _offers.Warm([(config, text)], OfferChangedAsync);
            }
            catch (Exception ex)
            {
                _log.Error("agent", "Could not load the routing selection before asking the server", ex);
            }
        }
    }

    // Opens the system screen carrying the always-on switch; no application may set always-on for itself.
    private IpcAck OpenVpnSettings()
    {
        try
        {
            var intent = new Intent(global::Android.Provider.Settings.ActionVpnSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            Application.Context.StartActivity(intent);
            return Ok();
        }
        catch (Exception ex)
        {
            _log.Warn("agent", "the system vpn settings did not open: " + ex);
            return new IpcAck(false, "vpn settings unavailable");
        }
    }

    // Only a running tunnel is asked about always-on, so a stopped one leaves no answer behind.
    private void ResetAlwaysOn()
    {
        _alwaysOn = false;
        _alwaysOnLockdown = false;
    }

    // Drops the link view a stopped tunnel left behind.
    private void ResetLink()
    {
        if (_churnLogged)
        {
            _log.Note("link", $"the tunnel stopped after {ChurnSeconds()} s of re-establishing the session");
        }

        _link = LinkReading.Empty;
        _linkNow = LinkNow.Unknown;
        _churnLogged = false;
    }

    // Writes the link to the journal: a line a minute while it runs, and a warning when it starts or stops
    // re-establishing the session.
    private void LogLink(LinkReading reading)
    {
        var churning = reading.Churning;
        if (churning != _churnLogged)
        {
            _churnLogged = churning;
            if (churning)
            {
                _churnSince = DateTimeOffset.UtcNow;
                // Loud enough to survive the default capture floor: this is the record a transient outage leaves.
                _log.Error("link", $"the session is re-established {reading.HandshakesPerMinute} times a minute, so the link carries almost nothing");
            }
            else
            {
                _log.Note("link", $"the session stopped re-establishing after {ChurnSeconds()} s");
            }
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _linkLoggedAt < TimeSpan.FromSeconds(60))
        {
            return;
        }

        _linkLoggedAt = now;
        _log.Info("link", reading.Describe(ChurnPerMinute(_boundTarget)));
    }

    // Handshake rate from which the session of a configuration counts as re-establishing.
    private int ChurnPerMinute(string? config)
    {
        return LinkHealth.ChurnPerMinuteFor(WgConfigEditor.GetRekeyAfterSeconds(_configs.GetValueOrDefault(config ?? string.Empty, string.Empty)));
    }

    // How long the session has been re-established over and over.
    private int ChurnSeconds() => (int)(DateTimeOffset.UtcNow - _churnSince).TotalSeconds;

    // The tunnel and the networks under it as a failed request found them.
    private string NetworkContext()
    {
        var view = AndroidNetworks.Read(Application.Context);
        var age = _handshakeUnix > 0 ? (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _handshakeUnix) : -1;
        return new NetworkSnapshot(_boundStatus, age, _link.RxBitsPerSecond, _link.LossPercent, _link.Churning,
            view.Under, view.UnderValidated, view.TunnelValidated, view.PrivateDnsHost, view.PrivateDnsActive).Describe();
    }

    // Marks the last connect as failed and names its cause for the notice.
    private void SetConnectFailure(string reason, string detail)
    {
        _connectFailed = true;
        _connectFailReason = reason;
        _connectFailDetail = detail;
    }

    private void ClearConnectFailure()
    {
        _connectFailed = false;
        _connectFailReason = string.Empty;
        _connectFailDetail = string.Empty;
    }

    // Drops the failure of a move to another configuration: the tunnel that was kept is down.
    private void ForgetKeptSwitch()
    {
        if (_connectFailReason == nameof(ConnectFailureReason.SwitchNoHandshake))
        {
            ClearConnectFailure();
        }
    }

    // Records a tunnel state transition in the agent log; a failure is logged at error level with its cause.
    private void LogVpnStage(VpnStage stage, string? detail)
    {
        var suffix = string.IsNullOrEmpty(detail) ? string.Empty : ": " + detail;
        if (stage == VpnStage.Failed)
        {
            _log.Error("tunnel", "failed" + suffix);
        }
        else
        {
            _log.Info("tunnel", stage.ToString().ToLowerInvariant() + suffix);
        }
    }

    private void MarkRoutingChanged(long listId)
    {
        if (Volatile.Read(ref _routingSelection)?.Contains(listId) == true
            || Volatile.Read(ref _appliedRequest)?.Plan?.ListIds.Contains(listId) == true)
        {
            ReconnectRouting();
        }
    }

    private void ReconnectRouting() => _connectPreparation.Apply(() =>
    {
        if (!_dialWanted || !_active || _boundTarget is not { } bound
            || !ConfigRouting.Allowed(_transports.GetValueOrDefault(bound)))
        {
            return;
        }

        _restartRequired = true;
        if (!string.Equals(bound, _selectedTarget, StringComparison.Ordinal))
        {
            return;
        }

        _connectPreparation.Invalidate();
        if (RevokePendingLaunch())
        {
            _log.Info("geo", "the routing policy changed; preparing a replacement VPN session");
            _ = QueueConnectAsync();
        }
    });

    private void PushSnapshot()
    {
        _linkUntold = false;
        if (!_active)
        {
            _ = RetellDownAsync();
        }

        var configs = OrderedNames().Select(name => Entry(name, _configs[name])).ToList();
        var proxy = VpnBridge.ReadProxyState();
        var reach = ProxyReach(_proxyOptions.Enabled);
        LogProxyOffer(proxy.Running, reach);
        var (updateSources, updateSource) = _updater.Sources();

        var requested = Volatile.Read(ref _routingSelection) ?? [];
        var applied = Volatile.Read(ref _appliedRequest)?.Plan;
        var routingPending = string.Empty;
        if (_restartRequired || (applied is not null && !requested.SequenceEqual(applied.ListIds)
            && ConfigRouting.Allowed(_transports.GetValueOrDefault(_boundTarget ?? string.Empty))))
        {
            routingPending = _connectFailed && _connectFailReason == nameof(ConnectFailureReason.TunnelSetupFailed)
                && _connectFailDetail.Length > 0 ? _connectFailDetail : IpcMessage.Key("Main_RoutingReconnectAndroidPending");
        }

        var activeChanged = Latest?.Active != _active;
        Latest = new StatusSnapshot(
            AgentVersion: AppVersion,
            BoundTarget: _boundTarget,
            Configs: configs,
            RoutingLists: _routingSummaries,
            Active: _active,
            BoundStatus: _boundStatus,
            RestartRequired: _restartRequired,
            SelectedTarget: _selectedTarget ?? string.Empty,
            SelectedRoutingList: requested.Count == 0 ? null : requested[0],
            Sources: BuildSources(),
            ConnectFailed: _connectFailed,
            ConnectFailReason: _connectFailReason,
            ConnectFailDetail: _connectFailDetail,
            RetryAttempt: _retryAttempt,
            PeriodicReconnect: true,
            PeriodicReconnectIntervalSeconds: ConnectRetry.CeilingSeconds,
            EngineVersion: AndroidBuild.EngineVersion,
            LogLevel: _logLevel,
            RouteLog: _routeLog,
            TunnelNotice: _tunnelNotice,
            RouteTtlSeconds: _routeTtl,
            SplitRouteLimit: _splitLimit,
            GeoAutoCheck: _geoAutoCheck,
            GeoCheckIntervalHours: _geoCheckIntervalHours,
            SubscriptionAutoRefresh: _subscriptionAutoRefresh,
            SubscriptionRefreshIntervalHours: _subscriptionIntervalHours,
            UpdateUrl: _updater.Url,
            UpdateAvailable: _updater.Available,
            UpdateVersion: _updater.Version,
            UpdateSetupUrl: _updater.SetupUrl,
            UpdateDescription: _updater.Description,
            AllowPrerelease: _updater.AllowPrerelease,
            UpdateChannel: _updater.Channel,
            ReleaseChannel: AndroidBuild.ReleaseChannel,
            UpdateSources: updateSources,
            UpdateSource: updateSource,
            UpdateSourceError: _updater.Fault.Text,
            UpdateSourceErrorSince: _updater.Fault.Since,
            UpdateSetupSha256: _updater.Sha256,
            UpdateSetupPath: _updater.SetupPath,
            UpdateDownloading: _updater.Downloading,
            UpdateDownloaded: _updater.Downloaded,
            UpdateDownloadPercent: _updater.Percent,
            UpdateDownloadFailed: _updater.Failed,
            UpdateCancelRequested: _updater.CancelRequested,
            UpdateChecking: _updater.Checking,
            UpdateCheckFailed: _updater.CheckFailed,
            UpdateInstalling: _updater.Installing,
            AlwaysOn: _alwaysOn,
            AlwaysOnLockdown: _alwaysOnLockdown,
            ProxyEnabled: _proxyOptions.Enabled,
            ProxySocksPort: _proxyOptions.SocksPort,
            ProxyHttpPort: _proxyOptions.HttpPort,
            ProxyAnonymous: _proxyOptions.AllowAnonymous,
            ProxyCredentials: _proxyOptions.Credentials,
            ProxyRunning: proxy.Running,
            ProxyError: proxy.Error,
            ProxyAddresses: reach.Addresses,
            ProxyPlaces: reach.Places,
            ProxyClients: proxy.Clients,
            ServerListsOf: ShownServerLists?.Config ?? string.Empty,
            ServerLists: ShownServerLists?.Lists,
            FoundApps: _appFinds,
            RequestedRoutingListIds: requested,
            AppliedRoutingListIds: applied?.ListIds ?? [],
            RoutingPolicyMode: applied is null ? string.Empty : StatusSnapshot.PolicyMode(!applied.FullTunnel, applied.ListIds.Count > 0),
            RoutingPolicyAllUdp: applied?.RequestedAllUdp == true,
            RoutingPendingReason: routingPending);

        SnapshotReceived?.Invoke(Latest);
        if (activeChanged)
        {
            ConnectionActivityChanged?.Invoke();
        }
    }

    // Where the proxy answers. Named from the moment it is switched on: the screen that offers it is where the
    // user reads what to point a client at, and here it only listens while the tunnel stands.
    private static LocalProxyServer.Reach ProxyReach(bool enabled)
    {
        return enabled ? GeoVpnService.ProxyReach() : new LocalProxyServer.Reach([], string.Empty, []);
    }

    // Writes where the proxy is offered and the links the answer was picked from.
    private void LogProxyOffer(bool running, LocalProxyServer.Reach reach)
    {
        var line = (running ? "up" : "down") + " "
            + (reach.Addresses.Count > 0 ? string.Join(", ", reach.Addresses) : "nowhere");
        var told = line + "\n" + reach.Links;
        if (string.Equals(told, _proxyOfferLine, StringComparison.Ordinal))
        {
            return;
        }

        _proxyOfferLine = told;
        global::Android.Util.Log.Info("AmneziaGeo", "proxy offered " + line);
        if (reach.Links.Length > 0)
        {
            global::Android.Util.Log.Info("AmneziaGeo", "proxy links " + reach.Links);
        }
    }

    // Watches the networks and the sharing of this device while the window is on the screen.
    private void WatchLinks(bool shown)
    {
        lock (_linkGate)
        {
            DropLinkWatch();
            if (!shown || _disposed)
            {
                return;
            }

            try
            {
                var context = Application.Context;
                var tether = new VpnBridge.Listener { Handler = _ => OnTetherChanged() };
                var filter = new IntentFilter(TetherChanged);
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                {
                    context.RegisterReceiver(tether, filter, ReceiverFlags.Exported);
                }
                else
                {
                    context.RegisterReceiver(tether, filter);
                }

                _tether = tether;
                if (context.GetSystemService(Context.ConnectivityService) is ConnectivityManager manager
                    && new NetworkRequest.Builder().Build() is { } request)
                {
                    var links = new LinkWatch { Changed = OnLinksChanged };
                    manager.RegisterNetworkCallback(request, links);
                    _links = links;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("agent", "the networks of this device are not watched: " + ex);
            }
        }
    }

    // Drops the watch of the networks and of the sharing; the caller holds the gate.
    private void DropLinkWatch()
    {
        try
        {
            if (_tether is not null)
            {
                Application.Context.UnregisterReceiver(_tether);
            }

            if (_links is not null
                && Application.Context.GetSystemService(Context.ConnectivityService) is ConnectivityManager manager)
            {
                manager.UnregisterNetworkCallback(_links);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("agent", "the watch of the networks was not dropped: " + ex);
        }

        _tether = null;
        _links = null;
    }

    // Reads the links once for a burst of changes in the networks.
    private void OnLinksChanged()
    {
        if (Interlocked.Exchange(ref _linksDue, 1) == 0)
        {
            _ = Task.Delay(LinkSettleMs).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _linksDue, 0);
                RetellReach();
            }, TaskScheduler.Default);
        }
    }

    // Reads the links when the sharing changed, and once more when the address of the shared network settled.
    private void OnTetherChanged()
    {
        OnLinksChanged();
        _ = Task.Delay(TetherSettleMs).ContinueWith(_ => RetellReach(), TaskScheduler.Default);
    }

    // Brings the addresses of the proxy in the snapshot up to date.
    private void RetellReach()
    {
        if (!_disposed && _proxyOptions.Enabled)
        {
            PushSnapshot();
        }
    }

    // Tells when a network of this device came, went or changed its addresses.
    private sealed class LinkWatch : ConnectivityManager.NetworkCallback
    {
        /// <summary>
        /// Called on each change.
        /// </summary>
        public Action? Changed { get; set; }

        /// <inheritdoc/>
        public override void OnAvailable(Network network) => Changed?.Invoke();

        /// <inheritdoc/>
        public override void OnLost(Network network) => Changed?.Invoke();

        /// <inheritdoc/>
        public override void OnLinkPropertiesChanged(Network network, LinkProperties linkProperties) => Changed?.Invoke();
    }

    // The names in the order the user set, with anything it does not name after them.
    private List<string> OrderedNames()
    {
        var names = _order.Where(_configs.ContainsKey).ToList();
        names.AddRange(_configs.Keys.Where(name => !names.Contains(name)));
        return names;
    }

    private ConfigEntry Entry(string name, string config)
    {
        var transport = _transports.GetValueOrDefault(name);
        var bound = _active && string.Equals(_boundTarget, name, StringComparison.Ordinal);
        var handshake = bound && _handshakeUnix > 0
            ? HandshakeAge.Step(Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _handshakeUnix))
            : -1;
        var reading = bound ? _link : LinkReading.Empty;
        var member = _members.GetValueOrDefault(name);
        var shown = bound ? _linkNow : LinkNow.Unknown;
        var offer = _offered.GetValueOrDefault(name);
        var vpnDns = VpnDns.Showing(_dns.GetValueOrDefault(name), offer?.Dns(), WgConfigEditor.GetDns(config), Beside());
        var offered = member is null ? null : offer?.Subscription();
        return new ConfigEntry(name, WgConfigEditor.GetEndpoint(config) ?? string.Empty, false, StatusFor(name), [],
            WebSocket: transport?.UseWebSocket ?? false,
            Mtu: transport?.Mtu ?? 0,
            UseIpv6: transport?.UseIpv6 ?? false,
            UseRouter: transport?.UseRouter ?? true,
            AllowInbound: transport?.AllowInbound ?? false,
            InboundNetwork: transport?.InboundNetwork ?? false,
            Address: string.Join(", ", WgConfigEditor.GetAddresses(config)),
            WebSocketFront: WsEndpoint.Of(config, offer, transport)?.Display() ?? string.Empty,
            UseRouting: transport?.UseRouting ?? true,
            WebSocketHost: transport?.WebSocketHost ?? string.Empty,
            WebSocketPort: transport?.WebSocketPort ?? 0,
            WebSocketManual: WsEndpoint.SourceOf(config, offer) == WsSource.Settings,
            HandshakeAgeSeconds: handshake,
            RxBitsPerSecond: reading.RxBitsPerSecond,
            TxBitsPerSecond: reading.TxBitsPerSecond,
            HandshakesPerMinute: reading.HandshakesPerMinute,
            LinkChurning: reading.Churning,
            LossStreak: shown.LossStreak,
            LossPercent: shown.LossPercent,
            RttMs: shown.RttMs,
            Subscription: member?.Subscription ?? string.Empty,
            SubscriptionGone: member is { Present: false },
            SubscriptionStale: member is not null && _stale.Contains(member.Subscription),
            ConfigMtu: WgConfigEditor.GetMtu(config),
            MtuMode: transport?.MtuMode ?? MtuMode.Auto,
            ResolvedMtu: MtuPlan.ResolveForLearnedLink(transport, config),
            AllowSignal: transport?.AllowSignal ?? true,
            SignalOffered: offer?.SignalAllowed == true,
            HoldTraffic: transport?.HoldTraffic ?? true,
            VpnDnsMode: _dns.GetValueOrDefault(name)?.VpnMode ?? VpnDnsMode.Default,
            VpnDns: _dns.GetValueOrDefault(name)?.VpnServers ?? string.Empty,
            VpnDnsShown: vpnDns.Shown,
            VpnDnsNetwork: vpnDns.Network,
            ServerDns: vpnDns.Server,
            DefaultDns: vpnDns.Default,
            DefaultDnsNetwork: vpnDns.DefaultNetwork,
            ServerListsOnly: offer?.PresetsOnly == true,
            ServerOurs: offer?.Ours == true,
            SubscriptionFault: offered?.Fault ?? string.Empty,
            SubscriptionDetail: offered?.Detail ?? string.Empty);
    }

    private string StatusFor(string target)
    {
        return _active && string.Equals(_boundTarget, target, StringComparison.Ordinal)
            ? _boundStatus
            : ConnectionStatus.Disconnected;
    }

    // Initializes the SQLite store and seeds default geo sources once.
    private Task EnsureInitAsync()
    {
        lock (_loadGate)
        {
            return _initTask ??= InitAsync();
        }
    }

    private async Task InitAsync()
    {
        EnsureLoaded();
        await _log.InitializeAsync().ConfigureAwait(false);
        _ = Task.Run(PruneLogsAsync);
        _log.Info("agent", "android agent started");
        _ = Task.Run(() => CrashLog.ReportPastExits(_log));
        await _store.InitializeAsync().ConfigureAwait(false);
        if (_legacyRoutingSelection is { } legacy)
        {
            try
            {
                if (await _store.TryMigrateAndroidRoutingListAsync(legacy.ListId).ConfigureAwait(false))
                {
                    _log.Debug("agent", legacy.ListId is null
                        ? "[FIX] Migrated an explicit routing off choice into SQLite without rewriting agent.json"
                        : "[FIX] Migrated one routing list ID into SQLite without rewriting agent.json");
                }
                else if (legacy.ListId is long id && await _store.GetRoutingListStampAsync(id).ConfigureAwait(false) is null)
                {
                    _log.Warn("agent", "[FIX] The legacy Android selection refers to a missing routing list");
                }
                else
                {
                    _log.Debug("agent", "[FIX] Kept the existing SQLite routing selection");
                }
            }
            catch (Exception ex)
            {
                _log.Error("agent", "[FIX] Could not migrate the legacy Android routing selection", ex);
                throw;
            }

            // The SQLite choice takes precedence even if the legacy JSON is read again on restart.
            _legacyRoutingSelection = null;
        }

        await RefreshRoutingSelectionAsync().ConfigureAwait(false);
        await GeoDefaults.SeedAsync(_store, _geoFiles, null, CancellationToken.None).ConfigureAwait(false);
        await RematerializeIfStaleAsync().ConfigureAwait(false);
        await RefreshTransportsAsync().ConfigureAwait(false);
        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        await RefreshGeoSourcesAsync().ConfigureAwait(false);
    }

    // Cuts the logs to their caps at start and on a schedule after it.
    private async Task PruneLogsAsync()
    {
        while (!_disposed)
        {
            try
            {
                var pruned = await _log.PruneAsync().ConfigureAwait(false);
                if (pruned.Total > 0)
                {
                    _log.Debug("agent", $"dropped the oldest {pruned.Agent} log entries, {pruned.Dns} resolver entries and {pruned.Routes} routing entries past the retention limit");
                }
            }
            catch (Exception ex)
            {
                _log.Warn("agent", "old log entries could not be removed: " + ex);
            }

            await Task.Delay(LogRetention.Interval).ConfigureAwait(false);
        }
    }

    // Rebuilds the stored lists when the app started covering a rule token differently than the run that wrote them.
    private async Task RematerializeIfStaleAsync()
    {
        try
        {
            if (await _geo.RematerializeIfStaleAsync().ConfigureAwait(false))
            {
                _log.Info("geo", "rule expansion changed, the stored routing lists were rebuilt");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("geo", "the routing lists could not be rebuilt: " + ex);
        }
    }

    private async Task RefreshGeoSourcesAsync()
    {
        _geoSources = await _store.ListGeoSourcesAsync().ConfigureAwait(false);
        _geoFileMeta = await _store.ListGeoFilesAsync().ConfigureAwait(false);
    }

    // The geo bases shown on the sources screen: both geoip and geosite; geosite domains route on Android via resolution to IPs.
    private IReadOnlyList<SourceEntry> BuildSources()
    {
        var metaByName = _geoFileMeta.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var list = new List<SourceEntry>();
        foreach (var source in _geoSources)
        {
            var meta = metaByName.GetValueOrDefault(source.Name);
            var updated = meta is null
                ? null
                : meta.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var updating = _updatingSources.Contains(source.Name);
            var error = !updating && _sourceErrors.TryGetValue(source.Name, out var e) ? e : null;
            var volume = _sourceProgress.GetValueOrDefault(source.Name);
            list.Add(new SourceEntry(source.Name, source.Kind, source.Url, updated, meta?.CategoryCount ?? 0, updating,
                updating ? volume.Percent : 0, meta?.UpdateAvailable ?? false, error,
                updating ? volume.Read : 0, updating ? volume.Total : 0));
        }

        return list;
    }

    private async Task<IpcAck> AddSourceAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        var kind = args[0];
        var url = args[1].Trim();
        if (url.Length == 0)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var position = _geoSources.Count == 0 ? 1 : _geoSources.Max(s => s.Position) + 1;
        var name = NextSourceName(kind, position);
        var source = new GeoSource(name, kind, url, position);
        await _store.SaveGeoSourceAsync(source).ConfigureAwait(false);
        await UpdateOneSourceAsync(source).ConfigureAwait(false);
        await AfterSourcesChangedAsync().ConfigureAwait(false);
        return _sourceErrors.TryGetValue(name, out var err) && err is not null ? new IpcAck(false, err) : Ok();
    }

    // A source name not already taken; positions can repeat after removals, so bump until free.
    private string NextSourceName(string kind, int position)
    {
        var n = position;
        var candidate = $"{kind}-{n}";
        while (_geoSources.Any(s => string.Equals(s.Name, candidate, StringComparison.Ordinal)))
        {
            n++;
            candidate = $"{kind}-{n}";
        }

        return candidate;
    }

    private async Task<IpcAck> RemoveSourceAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        await _store.RemoveGeoSourceAsync(args[0]).ConfigureAwait(false);
        _sourceErrors.Remove(args[0]);
        await AfterSourcesChangedAsync().ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> EditSourceAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 3)
        {
            return Fail();
        }

        var name = args[0];
        var kind = args[1];
        var url = args[2].Trim();
        await EnsureInitAsync().ConfigureAwait(false);
        var current = _geoSources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (current is null)
        {
            return Fail();
        }

        var edited = current with { Kind = kind, Url = url };
        await _store.SaveGeoSourceAsync(edited).ConfigureAwait(false);
        await UpdateOneSourceAsync(edited).ConfigureAwait(false);
        await AfterSourcesChangedAsync().ConfigureAwait(false);
        return _sourceErrors.TryGetValue(name, out var err) && err is not null ? new IpcAck(false, err) : Ok();
    }

    private async Task<IpcAck> UpdateSourceAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var source = _geoSources.FirstOrDefault(s => string.Equals(s.Name, args[0], StringComparison.Ordinal));
        if (source is null)
        {
            return Fail();
        }

        _updatingSources.Add(source.Name);
        PushSnapshot();
        await UpdateOneSourceAsync(source).ConfigureAwait(false);
        await AfterSourcesChangedAsync().ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> UpdateAllSourcesAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var targets = _geoSources.ToList();
        foreach (var source in targets)
        {
            _updatingSources.Add(source.Name);
        }

        PushSnapshot();
        foreach (var source in targets)
        {
            await UpdateOneSourceAsync(source).ConfigureAwait(false);
        }

        await AfterSourcesChangedAsync().ConfigureAwait(false);
        return Ok();
    }

    // Downloads one geo base, recording any failure against its name and clearing the in-flight flag.
    private async Task UpdateOneSourceAsync(GeoSource source)
    {
        try
        {
            await _geoUpdater.UpdateAsync(source, new SourceProgress(_sourceProgress, source.Name)).ConfigureAwait(false);
            _sourceErrors.Remove(source.Name);
        }
        catch (Exception ex)
        {
            _sourceErrors[source.Name] = ex.Message;
        }
        finally
        {
            _updatingSources.Remove(source.Name);
            _sourceProgress.TryRemove(source.Name, out _);
        }
    }

    // Пушит снимок, пока идут загрузки: иначе объём в списке источников стоит на месте.
    private async Task ProgressPumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }

            PushSnapshot();
        }
    }

    // Складывает ход загрузки одного источника.
    private sealed class SourceProgress(ConcurrentDictionary<string, GeoDownload> map, string name) : IProgress<GeoDownload>
    {
        public void Report(GeoDownload value) => map[name] = value;
    }

    // Есть ли база на диске.
    private bool HasGeoFile(string name)
    {
        using var stream = _geoFiles.OpenRead(name);
        return stream is not null;
    }

    // Re-materializes routing lists against the changed bases, refreshes caches, and pushes a fresh snapshot.
    private async Task AfterSourcesChangedAsync()
    {
        try
        {
            await _geo.RematerializeAllRoutingListsAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _appliedRequest) is { PlanStamp: { } applied, Name: var config })
            {
                var transport = _transports.GetValueOrDefault(config);
                var next = await PrepareSessionPlanAsync(config, transport, CancellationToken.None).ConfigureAwait(false);
                if (!string.Equals(applied, next.Stamp, StringComparison.Ordinal))
                {
                    ReconnectRouting();
                }
            }
        }
        catch (NotSupportedException ex)
        {
            _log.Warn("geo", ex.Message);
            ReconnectRouting();
        }
        catch (Exception ex)
        {
            _log.Error("geo", "the updated routing lists could not be prepared", ex);
        }

        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        await RefreshGeoSourcesAsync().ConfigureAwait(false);
        PushSnapshot();
    }

    // Pulls the newest rule databases while the interface is up: the app carries no agent in the background,
    // so the schedule runs here, and only over a link that costs the user nothing.
    private async Task CheckGeoUpdatesAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(GeoStartDelaySeconds), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var again = _geoRecheck.Due(now);
                var whole = now - _geoCheckedAt >= TimeSpan.FromHours(_geoCheckIntervalHours);
                if (_geoAutoCheck && (whole || again.Count > 0) && OnFreeLink())
                {
                    await RunGeoCheckAsync(whole ? null : again).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromSeconds(GeoTickSeconds), ct).ConfigureAwait(false);
            }
        }
        catch (System.OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("geo", "the scheduled check of the rule databases stopped", ex);
        }
    }

    // Asks every source, or the ones named, whether its file changed and downloads the ones that did.
    private async Task RunGeoCheckAsync(IReadOnlyList<string>? only)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var stale = new List<GeoSource>();
        var missed = new List<string>();
        foreach (var source in _geoSources.Where(one => only is null || only.Contains(one.Name)).ToList())
        {
            var status = await CheckOneSourceAsync(source).ConfigureAwait(false);
            if (status == GeoUpdateChecker.Status.Available)
            {
                stale.Add(source);
            }
            else if (status == GeoUpdateChecker.Status.Unknown)
            {
                missed.Add(source.Name);
            }
        }

        var now = DateTimeOffset.UtcNow;
        _geoRecheck.Took(missed, now, only is null);
        if (only is null)
        {
            _geoCheckedAt = now;
            Save();
        }
        if (stale.Count == 0)
        {
            await RefreshGeoSourcesAsync().ConfigureAwait(false);
            PushSnapshot();
            return;
        }

        _log.Info("geo", $"a newer file stands for {stale.Count} rule database(s), downloading");
        foreach (var source in stale)
        {
            _updatingSources.Add(source.Name);
        }

        PushSnapshot();
        foreach (var source in stale)
        {
            await UpdateOneSourceAsync(source).ConfigureAwait(false);
        }

        await AfterSourcesChangedAsync().ConfigureAwait(false);
    }

    // Whether the device sits on a link that carries no traffic bill: wifi, or the cable a set-top box runs on.
    private bool OnFreeLink()
    {
        try
        {
            if (Application.Context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager manager
                || manager.ActiveNetwork is not { } network
                || manager.GetNetworkCapabilities(network) is not { } capabilities)
            {
                return false;
            }

            // A tunnel of ours carries the transports of the link under it, so this reads the same either way.
            return capabilities.HasTransport(TransportType.Wifi) || capabilities.HasTransport(TransportType.Ethernet);
        }
        catch (Exception ex)
        {
            _log.Warn("geo", "the kind of the network link could not be read: " + ex);
            return false;
        }
    }

    private async Task RefreshRoutingSummariesAsync()
    {
        var summaries = await _store.ListRoutingListSummariesAsync().ConfigureAwait(false);
        _routingSummaries = summaries
            .Select(s => new RoutingListEntry(s.Id, s.Name, s.RuleCount, s.RouteCount, s.DomainCount,
                s.ProxyRuleCount, s.DirectRuleCount, s.BlockRuleCount, s.AllUdp, s.UseGlobalProxy, s.Source, s.HasUpdate, s.ReadOnly))
            .ToList();
        _serverLists = await ServerListsLimitAsync().ConfigureAwait(false);
        _appFinds = await AppFinds.ReadAsync(_store).ConfigureAwait(false);
    }

    // Has the programs of the device searched again behind a command that changed a routing list.
    private IpcAck Searched(IpcAck ack)
    {
        if (ack.Ok)
        {
            FindAppsLater();
        }

        return ack;
    }

    // Searches the programs of the device for the names of the routing lists in the background.
    private void FindAppsLater() => _ = Task.Run(FindAppsAsync);

    // Keeps what each app:<name> of the routing lists stands for; a find that changes what the routed list names
    // builds the session again with it.
    private async Task FindAppsAsync()
    {
        await _finding.WaitAsync().ConfigureAwait(false);
        try
        {
            var search = await AppFinds.SearchListsAsync(_store, ListedApps, note => _log.Info("agent", note)).ConfigureAwait(false);
            _appFinds = await AppFinds.ReadAsync(_store).ConfigureAwait(false);
            if (search is not { Changed.Count: > 0 })
            {
                return;
            }

            _log.Info("agent", $"programs found again for {string.Join(", ", search.Changed.Select(name => "app:" + name))}");
            foreach (var id in Volatile.Read(ref _routingSelection) ?? [])
            {
                if (await _store.GetRoutingListStampAsync(id).ConfigureAwait(false) is { } stamp
                    && AppFinds.NamesOf(stamp.Rules).Intersect(search.Changed, StringComparer.OrdinalIgnoreCase).Any())
                {
                    ReconnectRouting();
                    break;
                }
            }

            PushSnapshot();
        }
        catch (Exception ex)
        {
            _log.Error("agent", "the programs of the device were not searched", ex);
        }
        finally
        {
            _finding.Release();
        }
    }

    // The programs of the device the way a search of app:<name> sees them.
    private static IReadOnlyList<NamedApp> ListedApps() =>
        [.. AppCensus.List(Application.Context).Select(entry => new NamedApp(entry.Label, string.Empty, entry.Package, "pkg=" + entry.Package))];

    // The limit the server of the configuration standing up sets on the routing lists.
    private async Task<ListsLimit?> ServerListsLimitAsync() =>
        _active && _boundTarget is { Length: > 0 } name
            ? await ServerListsOnly.OfAsync(_store, name, _configs.GetValueOrDefault(name), CancellationToken.None).ConfigureAwait(false)
            : null;

    // The limit as the snapshot shows it: the one of the configuration standing up.
    private ListsLimit? ShownServerLists =>
        _active && _serverLists is { } limit && string.Equals(limit.Config, _boundTarget, StringComparison.Ordinal) ? limit : null;

    private async Task<(bool Changed, bool Available)> KeepServerListsAsync(string config)
    {
        var text = _configs.GetValueOrDefault(config);
        var routed = await ServerListsOnly.RoutingOnAsync(_store, config, text, CancellationToken.None).ConfigureAwait(false);
        if (routed)
        {
            await RefreshTransportsAsync().ConfigureAwait(false);
            _log.Info("agent", $"{config}: its server allows none but its own routing lists; the configuration takes the routing list");
        }

        var limit = await ServerListsOnly.OfAsync(_store, config, text, CancellationToken.None).ConfigureAwait(false);
        if (limit is { Available: false })
        {
            _log.Warn("agent", $"{config}: {ServerListsOnly.UnavailableDetail}");
            return (routed, false);
        }

        if (await ServerListsOnly.EnforceSelectionAsync(_store, limit, CancellationToken.None).ConfigureAwait(false))
        {
            await RefreshRoutingSelectionAsync().ConfigureAwait(false);
            _log.Info("agent", $"{config}: the routing list selection was synchronized with the server");
            return (true, true);
        }

        return (routed, true);
    }

    // Downloads the geoip databases on first use; retries after a failed attempt.
    private Task EnsureGeoFilesAsync()
    {
        var task = _geoFilesTask;
        if (task is { IsCompletedSuccessfully: true })
        {
            return task;
        }

        if (task is null || task.IsFaulted || task.IsCanceled)
        {
            _geoFilesTask = task = DownloadGeoFilesAsync();
        }

        return task;
    }

    private async Task DownloadGeoFilesAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        // Только отсутствующие базы: лежащую на диске обновляет экран источников, а не открытие списка.
        var missing = (await _store.ListGeoSourcesAsync().ConfigureAwait(false))
            .Where(source => !HasGeoFile(source.Name))
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var source in missing)
        {
            _updatingSources.Add(source.Name);
        }

        PushSnapshot();
        var pump = new CancellationTokenSource();
        var ticker = ProgressPumpAsync(pump.Token);
        try
        {
            foreach (var source in missing)
            {
                await UpdateOneSourceAsync(source).ConfigureAwait(false);
            }
        }
        finally
        {
            pump.Cancel();
            await ticker.ConfigureAwait(false);
            pump.Dispose();
        }

        await AfterSourcesChangedAsync().ConfigureAwait(false);
        if (missing.All(source => _sourceErrors.GetValueOrDefault(source.Name) is not null))
        {
            throw new InvalidOperationException("geo download failed");
        }
    }

    private async Task<IpcAck> ListGeoAsync()
    {
        try
        {
            await EnsureGeoFilesAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        var tokens = await _geo.CategoriesAsync().ConfigureAwait(false);
        return new IpcAck(true, string.Join('\n', tokens));
    }

    private async Task<IpcAck> GetGeoEntriesAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        try
        {
            await EnsureGeoFilesAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        var entries = await _geo.EntriesAsync(args[0]).ConfigureAwait(false);
        // A limit of 0 asks for the whole category; without one the answer stays a short page.
        var cap = args.Count > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
            ? (c <= 0 ? entries.Count : c)
            : 300;
        return new IpcAck(true, JsonSerializer.Serialize(new { total = entries.Count, entries = entries.Take(cap).ToArray() }));
    }

    private async Task<IpcAck> SaveRoutingListAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        try
        {
            await EnsureGeoFilesAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        var id = long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var name = args[1].Trim();
        if (name.Length == 0)
        {
            return Fail();
        }

        var lists = await _store.ListRoutingListsAsync().ConfigureAwait(false);

        var savedId = await _geo.ApplyToRoutingListAsync(id, name, [.. args.Skip(2)]).ConfigureAwait(false);

        // The first list applies at once.
        if (id == 0 && lists.Count == 0 && await ServerListsLimitAsync().ConfigureAwait(false) is null)
        {
            await _store.UpdateSelectedRoutingListsAsync(current => current is null ? [savedId] : null).ConfigureAwait(false);
            await RefreshRoutingSelectionAsync().ConfigureAwait(false);
        }

        MarkRoutingChanged(savedId);
        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        PushSnapshot();
        return new IpcAck(true, savedId.ToString(CultureInfo.InvariantCulture));
    }

    private async Task<IpcAck> GetRoutingListAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var list = await _store.GetRoutingListStampAsync(id).ConfigureAwait(false);
        if (list is null)
        {
            return Fail();
        }

        return new IpcAck(true, string.Join('\n', list.Rules.Select(GeoConfigurator.FormatWithRole)));
    }

    // Counts the routes a draft rule set puts into the tun, so the editor can refuse a list this device cannot
    // carry. A device with the relay reports no ceiling, and for a list that carries only what it names it says
    // whether the relay would stand under it.
    private async Task<IpcAck> CountRoutesAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var full = string.Equals(args[0], "full", StringComparison.OrdinalIgnoreCase);
        // A session behind the relay has no ceiling: what the route table will not hold, the relay decides.
        var relayable = PerAppSupported && RouterEnabled();
        if (relayable && full)
        {
            return new IpcAck(true, "{\"limit\":0,\"trims\":0}");
        }

        try
        {
            await EnsureGeoFilesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("geo", $"route count runs on the geo files at hand: {ex.Message}");
        }

        var draft = await _geo.MaterializeDraftAsync([.. args.Skip(1)]).ConfigureAwait(false);
        // A name carries no address until connect, where it resolves and cuts or adds about two routes.
        var names = draft.Domains.Count + draft.DirectDomains.Count + draft.BlockDomains.Count;
        if (relayable)
        {
            var listed = draft.Routes.Count + draft.DirectRoutes.Count + draft.BlockRoutes.Count;
            var share = SplitBudget.Share([.. (await _geo.UnfoldTokensAsync([.. args.Skip(1)]).ConfigureAwait(false)).Select(rule => rule.Weight)], _splitLimit);
            return new IpcAck(true, $"{{\"limit\":0,\"trims\":0,\"relay\":{(share.AnyLive ? "1" : "0")}"
                + $",\"ranges\":{listed.ToString(CultureInfo.InvariantCulture)}"
                + $",\"names\":{names.ToString(CultureInfo.InvariantCulture)}"
                + $",\"weight\":{SplitBudget.Weight(listed, names).ToString(CultureInfo.InvariantCulture)}"
                + $",\"threshold\":{_splitLimit.ToString(CultureInfo.InvariantCulture)}"
                + $",\"live\":{share.LiveWeight.ToString(CultureInfo.InvariantCulture)}}}");
        }
        var routes = SystemRoutes.Tunneled(full, draft.Routes, draft.DirectRoutes, draft.BlockRoutes).Count + (names * 2);
        // The tun keeps the direct ranges that do not fit and leaves out the widest, so a list over the budget still
        // runs - shorter of reach, in either mode: every range left outside costs the routes around it.
        var trims = routes > RouteBudget.Max;
        var kept = trims
            ? SystemRoutes.Carve(draft.DirectRoutes, [], draft.BlockRoutes, RouteBudget.Max)
            : [];
        if (trims)
        {
            routes = SystemRoutes.Tunneled(full, draft.Routes, kept, draft.BlockRoutes).Count + (names * 2);
        }

        return new IpcAck(true, $"{{\"routes\":{routes.ToString(CultureInfo.InvariantCulture)}"
            + $",\"limit\":{RouteBudget.Max.ToString(CultureInfo.InvariantCulture)}"
            + $",\"trims\":{(trims ? 1 : 0).ToString(CultureInfo.InvariantCulture)}"
            + $",\"kept\":{kept.Count.ToString(CultureInfo.InvariantCulture)}"
            + $",\"total\":{draft.DirectRoutes.Count.ToString(CultureInfo.InvariantCulture)}}}");
    }

    // Stores the order the routing-list catalogue is shown in; a name the store does not know leaves it alone.
    private async Task<IpcAck> ReorderRoutingListsAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var stored = (await _store.ListRoutingListSummariesAsync().ConfigureAwait(false))
            .Select(summary => summary.Id)
            .ToHashSet();
        var ids = RoutingListIds.Parse(args);
        if (ids is null || ids.Any(id => !stored.Contains(id)))
        {
            return Fail();
        }

        await _store.SetRoutingListOrderAsync(ids).ConfigureAwait(false);
        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
    }

    private async Task<IpcAck> RemoveRoutingListAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        if (await ServerListsOnly.RemoveAsync(_store, await ServerListsLimitAsync().ConfigureAwait(false), id, CancellationToken.None).ConfigureAwait(false) is { } refusal)
        {
            _log.Warn("agent", $"the server restriction refused removal of routing list {id}");
            return refusal;
        }

        MarkRoutingChanged(id);

        await RefreshRoutingSelectionAsync().ConfigureAwait(false);

        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
    }

    private async Task<IpcAck> GetRoutingSettingsAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var settings = await _store.GetRoutingSettingsAsync(id).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(new
        {
            exclusions = settings?.Exclusions ?? string.Empty,
            allUdp = settings?.AllUdp ?? false,
            mode = settings?.Mode ?? "split",
            useGlobalProxy = settings?.UseGlobalProxy ?? false,
        });
        return new IpcAck(true, json);
    }

    // Takes the newer version the server handed out for a list, by the commands that save a list and its settings.
    // A list the server keeps read-only takes no change and no removal on the device.
    private async Task<IpcAck> UnlessReadOnlyAsync(IReadOnlyList<string> args, Func<IReadOnlyList<string>, Task<IpcAck>> command)
    {
        await EnsureInitAsync().ConfigureAwait(false);

        return await OfferedLists.ReadOnlyRefusalAsync(_store, args, CancellationToken.None).ConfigureAwait(false)
            ?? await command(args).ConfigureAwait(false);
    }

    // Takes the newer versions of the lists the server keeps read-only the way the update of a list does.
    private async Task TakeVersionsAsync(IReadOnlyList<long> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                var taken = await UpdateRoutingListAsync([id.ToString(CultureInfo.InvariantCulture)]).ConfigureAwait(false);
                if (taken.Ok)
                {
                    _log.Info("routing", $"routing list {id} took the newer version its server keeps read-only");
                }
                else
                {
                    _log.Warn("routing", $"routing list {id} did not take the newer version its server keeps read-only: {taken.Message}");
                }
            }
            catch (Exception ex) when (ex is not System.OperationCanceledException)
            {
                _log.Warn("routing", $"routing list {id} did not take the newer version its server keeps read-only{System.Environment.NewLine}{ex}");
            }
        }

        PushSnapshot();
    }

    private async Task<IpcAck> UpdateRoutingListAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var accepted = await OfferedLists.AcceptPendingAsync(
            _store,
            id,
            async waiting =>
            {
                var saved = await SaveRoutingListAsync(waiting.SaveArgs).ConfigureAwait(false);
                return saved.Ok ? await SetRoutingSettingsAsync(waiting.SettingsArgs).ConfigureAwait(false) : saved;
            },
            Fail(),
            CancellationToken.None).ConfigureAwait(false);
        if (accepted.Ok)
        {
            await RefreshRoutingSummariesAsync().ConfigureAwait(false);
            PushSnapshot();
        }

        return accepted;
    }

    private async Task<IpcAck> SetRoutingSettingsAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var exclusions = args.Count > 1 ? args[1] : string.Empty;
        var allUdp = args.Count > 2 && IsOn(args[2]);
        var useGlobalProxy = args.Count > 4 && IsOn(args[4]);
        if (exclusions.Length == 0 && !allUdp && !useGlobalProxy)
        {
            await _store.RemoveRoutingSettingsAsync(id).ConfigureAwait(false);
        }
        else
        {
            var mode = useGlobalProxy ? "full" : "split";
            await _store.SetRoutingSettingsAsync(new RoutingSettings(id, exclusions, allUdp, mode, useGlobalProxy)).ConfigureAwait(false);
        }

        MarkRoutingChanged(id);

        // Сводки списков держатся в памяти: без перечитывания снимок отдаёт прежние режимы.
        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
    }

    // Export selection from OpExportBundle's arg0 json; all arrays optional. A routing list goes by its number or
    // by its name; RoutingRules maps that word to the rule tokens to keep; an absent list keeps all its rules.
    private sealed record BundleSelection(
        string[]? Configs,
        string[]? RoutingLists,
        Dictionary<string, string[]>? RoutingRules,
        bool IncludeRoutingSelection = false);

    // Packs the picked configs and routing lists into a portable bundle.
    private async Task<IpcAck> ExportBundleAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return Fail();
        }

        var selection = ParseSelection(args[0]);
        if (selection is null)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_ExportSelectionParseFailed"));
        }

        await EnsureInitAsync().ConfigureAwait(false);

        var configNames = new HashSet<string>(selection.Configs ?? [], StringComparer.Ordinal);
        var routingNames = new HashSet<string>(selection.RoutingLists ?? [], StringComparer.Ordinal);

        var full = selection.IncludeRoutingSelection && selection.RoutingRules is not { Count: > 0 };
        if (configNames.Count == 0 && routingNames.Count == 0 && !full)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NothingSelectedForExport"));
        }

        var configBlocks = new List<PortableBundle.ConfigBlock>();
        foreach (var name in configNames)
        {
            if (!_configs.TryGetValue(name, out var text))
            {
                continue;
            }

            var transport = await _store.GetConfigTransportAsync(name).ConfigureAwait(false);
            configBlocks.Add(new PortableBundle.ConfigBlock(
                name,
                text,
                transport is null
                    ? null
                    : new PortableBundle.TransportBlock(
                        transport.UseWebSocket,
                        transport.WebSocketHost,
                        transport.WebSocketPort,
                        transport.Mtu,
                        transport.UseIpv6,
                        transport.MtuMode,
                        transport.UseRouter,
                        transport.AllowInbound,
                        transport.InboundNetwork,
                        transport.UseRouting,
                        transport.AllowSignal,
                        transport.HoldTraffic),
                null).WithVpnDns(await _store.GetConfigDnsAsync(name).ConfigureAwait(false)));
        }

        var routingBlocks = new List<PortableBundle.RoutingBlock>();
        var (picked, _) = routingNames.Count == 0
            ? ([], [])
            : BundleLists.Pick(await _store.ListRoutingListsAsync().ConfigureAwait(false), routingNames);
        foreach (var (key, list) in picked)
        {
            // Role-tagged tokens: bare ones would re-import every rule as Proxy.
            var rules = list.Rules.Select(GeoConfigurator.FormatWithRole).ToList();
            if (selection.RoutingRules is not null && selection.RoutingRules.TryGetValue(key, out var kept))
            {
                var keep = new HashSet<string>(kept, StringComparer.Ordinal);
                rules = rules.Where(keep.Contains).ToList();
            }

            var settings = await _store.GetRoutingSettingsAsync(list.Id).ConfigureAwait(false);
            routingBlocks.Add(new PortableBundle.RoutingBlock(
                list.Name,
                rules,
                settings is null ? null : new PortableBundle.RoutingSettingsBlock(settings.Exclusions, settings.AllUdp, settings.Mode, settings.UseGlobalProxy),
                list.Uid));
        }

        var activeConfig = _selectedTarget is { Length: > 0 } selected
            && configBlocks.Exists(block => string.Equals(block.Name, selected, StringComparison.Ordinal))
                ? selected
                : null;

        var bundle = new PortableBundle.Bundle(
            PortableBundle.FormatTag,
            PortableBundle.CurrentVersion,
            configBlocks,
            routingBlocks,
            ActiveConfig: activeConfig,
            ActiveRoutingListIds: await BundleLists.ExportSelectionAsync(_store, picked, full).ConfigureAwait(false));
        _log.Info("agent", $"exported bundle: {configBlocks.Count} configs, {routingBlocks.Count} routing lists");
        return new IpcAck(true, PortableBundle.Serialize(bundle));
    }

    // Recreates the bundle's configs and routing lists; policy picks what to do on a name clash.
    private async Task<IpcAck> ImportBundleAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return Fail();
        }

        var policy = args.Count > 1 ? args[1] : "new";

        PortableBundle.Bundle? bundle;
        try
        {
            bundle = PortableBundle.Deserialize(args[0]);
        }
        catch (JsonException ex)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_BundleParseFailed", ex.Message));
        }

        if (bundle is null || !string.Equals(bundle.Format, PortableBundle.FormatTag, StringComparison.Ordinal))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NotAnAmneziaGeoFile"));
        }

        if (bundle.Version > PortableBundle.CurrentVersion)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_BundleTooNew", bundle.Version));
        }

        await EnsureInitAsync().ConfigureAwait(false);
        try
        {
            await EnsureGeoFilesAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        var existingLists = await _store.ListRoutingListsAsync().ConfigureAwait(false);
        var configNames = new HashSet<string>(_configs.Keys, StringComparer.Ordinal);
        var renames = new List<string>();
        var importedConfigs = new Dictionary<string, string>(StringComparer.Ordinal);
        var importedLists = new List<(PortableBundle.RoutingBlock Block, long Id)>();
        var replacedLists = new HashSet<long>();

        foreach (var block in bundle.Configs)
        {
            if (_configs.ContainsKey(block.Name) && policy != "new")
            {
                if (policy != "skip")
                {
                    _configs[block.Name] = block.ConfigText;
                    await ApplyTransportAsync(block.Name, block.Transport).ConfigureAwait(false);
                    await (block with { Dns = null }).ApplyDnsAsync(_store, block.Name, CancellationToken.None).ConfigureAwait(false);
                }

                importedConfigs[block.Name] = block.Name;
                continue;
            }

            var finalName = FreeName(block.Name, configNames, "Конфигурация");
            configNames.Add(finalName);
            if (!string.Equals(finalName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{finalName}»");
            }

            _configs[finalName] = block.ConfigText;
            importedConfigs[block.Name] = finalName;
            await ApplyTransportAsync(finalName, block.Transport).ConfigureAwait(false);
            await (block with { Dns = null }).ApplyDnsAsync(_store, finalName, CancellationToken.None).ConfigureAwait(false);
        }

        foreach (var block in bundle.RoutingLists)
        {
            // The list held under the block's id takes it; a copy beside it would carry a second id for the same
            // list, so "new" replaces it as well.
            if (BundleLists.Held(block, existingLists) is { } existing)
            {
                if (policy != "skip")
                {
                    var merge = policy == "merge";
                    var merged = merge
                        ? existing.Rules.Select(GeoConfigurator.FormatWithRole).Concat(block.Rules).Distinct(StringComparer.Ordinal).ToList()
                        : block.Rules.ToList();
                    await _geo.ApplyToRoutingListAsync(existing.Id, merge ? existing.Name : BundleLists.NameOf(block), merged).ConfigureAwait(false);
                    if (block.Settings is null && !merge)
                    {
                        await _store.RemoveRoutingSettingsAsync(existing.Id).ConfigureAwait(false);
                    }
                    else
                    {
                        await ApplyRoutingSettingsAsync(existing.Id, block.Settings).ConfigureAwait(false);
                    }
                    replacedLists.Add(existing.Id);
                }

                importedLists.Add((block, existing.Id));
                continue;
            }

            // A list no row holds lands under its own name and id; a bundle of an earlier build gives no id.
            var newId = await _geo.ApplyToRoutingListAsync(0, BundleLists.NameOf(block), block.Rules, block.Id ?? string.Empty).ConfigureAwait(false);
            importedLists.Add((block, newId));
            await ApplyRoutingSettingsAsync(newId, block.Settings).ConfigureAwait(false);
        }

        if (bundle.Profiles is { Count: > 0 } legacy)
        {
            _log.Info("agent", $"the bundle carries {legacy.Count} profile(s) from an older build; their pairings are dropped");
        }

        await RestoreBundleSelectionAsync(bundle, importedConfigs, importedLists).ConfigureAwait(false);
        Save();
        var restored = await BundleLists.RestoreSelectionAsync(_store, bundle, (change, _) => ChangeRoutingAsync(change),
            message => _log.Info("routing", message),
            message => _log.Warn("routing", message),
            message => _log.Error("routing", message)).ConfigureAwait(false);
        foreach (var id in replacedLists)
        {
            MarkRoutingChanged(id);
        }

        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        PushSnapshot();
        if (!restored)
        {
            return new IpcAck(false, "The backup contents were imported, but the routing selection could not be restored.");
        }

        _log.Info("agent", $"imported bundle: {bundle.Configs.Count} configs, {bundle.RoutingLists.Count} routing lists");

        if (renames.Count == 0)
        {
            return new IpcAck(true, IpcMessage.Key(
                "Agent_BundleImported",
                bundle.Configs.Count,
                bundle.RoutingLists.Count));
        }

        if (renames.Count <= 5)
        {
            return new IpcAck(true, IpcMessage.Key(
                "Agent_BundleImportedRenamed",
                bundle.Configs.Count,
                bundle.RoutingLists.Count,
                string.Join(", ", renames)));
        }

        return new IpcAck(true, IpcMessage.Key(
            "Agent_BundleImportedRenamedMany",
            bundle.Configs.Count,
            bundle.RoutingLists.Count));
    }

    // Restores defaults from older Android backups that identified the routing list only by name.
    private async Task RestoreBundleSelectionAsync(
        PortableBundle.Bundle bundle,
        IReadOnlyDictionary<string, string> configs,
        IReadOnlyList<(PortableBundle.RoutingBlock Block, long Id)> lists)
    {
        if (string.IsNullOrEmpty(_selectedTarget)
            && bundle.ActiveConfig is { Length: > 0 } config
            && configs.TryGetValue(config, out var name))
        {
            _selectedTarget = name;
            _log.Info("agent", $"the bundle's server is selected again: {name}");
        }

        if (bundle.ActiveRoutingListIds is not null || !string.IsNullOrEmpty(bundle.ActiveRoutingListId)
            || string.IsNullOrEmpty(bundle.ActiveRoutingList))
        {
            return;
        }

        var namedLists = lists.Where(one => string.Equals(one.Block.Name, bundle.ActiveRoutingList, StringComparison.Ordinal))
            .Select(one => one.Id).Distinct().ToArray();
        if (namedLists.Length == 1
            && ServerListsOnly.RefusalOfSet(await ServerListsLimitAsync().ConfigureAwait(false), namedLists) is null)
        {
            var restored = false;
            await _store.UpdateSelectedRoutingListsAsync(current =>
            {
                restored = current is null;
                return restored ? namedLists : null;
            }).ConfigureAwait(false);
            await RefreshRoutingSelectionAsync().ConfigureAwait(false);
            if (restored)
            {
                _log.Info("routing", "restored the legacy backup routing selection: 1 list");
            }
        }
    }

    // Подбирает свободное имя импортируемому блоку; пустое заменяет запасным.
    private static string FreeName(string desired, HashSet<string> taken, string fallback)
        => UniqueName.ResolveParen(string.IsNullOrWhiteSpace(desired) ? fallback : desired, taken);

    private static BundleSelection? ParseSelection(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BundleSelection>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Stores the bundle's transport for a config.
    private async Task ApplyTransportAsync(string config, PortableBundle.TransportBlock? transport)
    {
        if (transport is null)
        {
            return;
        }

        await _store.SetConfigTransportAsync(
            new ConfigTransport(config, transport.UseWebSocket, transport.Mtu, transport.UseIpv6, transport.MtuMode, transport.UseRouter, transport.AllowInbound, transport.InboundNetwork, transport.UseRouting, transport.Host, transport.Port, transport.AllowSignal, transport.HoldTraffic)).ConfigureAwait(false);
    }

    private async Task ApplyRoutingSettingsAsync(long listId, PortableBundle.RoutingSettingsBlock? settings)
    {
        if (settings is null)
        {
            return;
        }

        await _store.SetRoutingSettingsAsync(
            new RoutingSettings(listId, settings.Exclusions, settings.AllUdp, settings.Mode, settings.UseGlobalProxy)).ConfigureAwait(false);
    }

    // Stores a config's websocket switch and front, tunnel MTU and IPv6 opt-in; all reach the tunnel builder on the
    // next connect.
    private async Task<IpcAck> SetWebSocketAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !_configs.ContainsKey(args[0]))
        {
            return Fail();
        }

        // Empty leaves the config in charge: a zero here means nothing was chosen.
        var mtuText = args.Count > 2 ? args[2].Trim() : string.Empty;
        if ((mtuText.Length == 0 ? 0 : ParseRange(mtuText, MtuModes.MinMtu, MtuModes.MaxMtu)) is not { } mtu)
        {
            return new IpcAck(false, Loc.Instance.Get("Transport_InvalidMtu"));
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var previous = await _store.GetConfigTransportAsync(args[0]).ConfigureAwait(false);
        var useIpv6 = args.Count > 3 ? IsOn(args[3]) : previous?.UseIpv6 ?? false;

        // A size sent without a mode stands for a choice of its own.
        var mode = args.Count > 4
            ? MtuModes.Parse(args[4], previous?.MtuMode ?? MtuMode.Auto)
            : mtu > 0 ? MtuMode.Custom : previous?.MtuMode ?? MtuMode.Auto;
        var useRouter = args.Count > 5 ? IsOn(args[5]) : previous?.UseRouter ?? true;
        var allowInbound = args.Count > 6 ? IsOn(args[6]) : previous?.AllowInbound ?? false;
        var inboundNetwork = args.Count > 7 ? IsOn(args[7]) : previous?.InboundNetwork ?? false;
        var useRouting = args.Count > 8 ? IsOn(args[8]) : previous?.UseRouting ?? true;
        if (await ServerListsOnly.RoutingOffRefusalAsync(_store, args[0], _configs.GetValueOrDefault(args[0]), useRouting || !(previous?.UseRouting ?? true), CancellationToken.None).ConfigureAwait(false) is { } kept)
        {
            return kept;
        }

        var port = args.Count > 9 ? ConfigTransport.PortSent(args[9]) : previous?.WebSocketPort ?? 0;
        if (port < 0)
        {
            return new IpcAck(false, Loc.Instance.Get("Transport_InvalidPort"));
        }

        var host = args.Count > 10 ? args[10].Trim() : previous?.WebSocketHost ?? string.Empty;
        if (!WsEndpoint.Dials(host))
        {
            return new IpcAck(false, Loc.Instance.Get("Transport_InvalidHost"));
        }

        await _store.SetConfigTransportAsync(new ConfigTransport(args[0], IsOn(args[1]), mtu, useIpv6, mode, useRouter, allowInbound, inboundNetwork, useRouting, host, port, previous?.AllowSignal ?? true, previous?.HoldTraffic ?? true)).ConfigureAwait(false);
        if (_active && string.Equals(_boundTarget, args[0], StringComparison.Ordinal) && useRouting != (previous?.UseRouting ?? true))
        {
            _restartRequired = true;
            _log.Info("agent", "the routing switch applies on the next connect");
        }

        await RefreshTransportsAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
    }

    // Stores whether a config takes the signal of its server to disconnect; the tunnel reads it on the next connect.
    private async Task<IpcAck> SetSignalAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !_configs.ContainsKey(args[0]))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var previous = await _store.GetConfigTransportAsync(args[0]).ConfigureAwait(false) ?? new ConfigTransport(args[0], false, 0);
        var allowed = IsOn(args[1]);
        await _store.SetConfigTransportAsync(previous with { AllowSignal = allowed }).ConfigureAwait(false);
        if (_active && string.Equals(_boundTarget, args[0], StringComparison.Ordinal) && allowed != previous.AllowSignal)
        {
            _restartRequired = true;
            _log.Info("agent", "the signal switch applies on the next connect");
        }

        await RefreshTransportsAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
    }

    // Stores whether a config holds its traffic while its tunnel is repaired; the tunnel reads it on the next connect.
    private async Task<IpcAck> SetConfigVpnDnsAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !_configs.ContainsKey(args[0]) || VpnDns.Read(args[1]) is not { } mode)
        {
            return Fail();
        }

        // The servers of its own stay with the record under another choice, there to come back to.
        var servers = mode == VpnDnsMode.Own ? args.Count > 2 ? args[2].Trim() : string.Empty : null;
        if (VpnDns.Odd(servers) is { Count: > 0 } odd)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_VpnDnsBad", string.Join(", ", odd)));
        }

        if (VpnDns.LacksV4(servers))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_VpnDnsV4"));
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var previous = await _store.GetConfigDnsAsync(args[0]).ConfigureAwait(false);
        await VpnDns.SaveAsync(_store, args[0], null, mode, servers, CancellationToken.None).ConfigureAwait(false);
        var moved = mode != (previous?.VpnMode ?? VpnDnsMode.Default)
            || (servers is not null && !string.Equals(servers, previous?.VpnServers ?? string.Empty, StringComparison.Ordinal));
        if (moved && _active && string.Equals(_boundTarget, args[0], StringComparison.Ordinal))
        {
            _restartRequired = true;
            _log.Info("agent", "the name servers of the names through the tunnel apply on the next connect");
        }

        await RefreshTransportsAsync().ConfigureAwait(false);
        PushSnapshot();
        return new IpcAck(true, IpcMessage.Key("Agent_VpnDnsSaved", VpnDns.Describe(mode, servers)));
    }

    private async Task<IpcAck> SetHoldTrafficAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !_configs.ContainsKey(args[0]))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var previous = await _store.GetConfigTransportAsync(args[0]).ConfigureAwait(false) ?? new ConfigTransport(args[0], false, 0);
        var hold = IsOn(args[1]);
        await _store.SetConfigTransportAsync(previous with { HoldTraffic = hold }).ConfigureAwait(false);
        if (_active && string.Equals(_boundTarget, args[0], StringComparison.Ordinal) && hold != previous.HoldTraffic)
        {
            _restartRequired = true;
            _log.Info("agent", "the hold of the traffic applies on the next connect");
        }

        await RefreshTransportsAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
    }

    private static int? ParseRange(string value, int min, int max) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        && parsed >= min && parsed <= max
            ? parsed
            : null;

    private async Task RefreshTransportsAsync()
    {
        var transports = new Dictionary<string, ConfigTransport>(StringComparer.Ordinal);
        var dns = new Dictionary<string, ConfigDns>(StringComparer.Ordinal);
        foreach (var name in _configs.Keys.ToList())
        {
            if (await _store.GetConfigTransportAsync(name).ConfigureAwait(false) is { } transport)
            {
                transports[name] = transport;
            }

            if (await _store.GetConfigDnsAsync(name).ConfigureAwait(false) is { } held)
            {
                dns[name] = held;
            }
        }

        _transports = transports;
        _dns = dns;
        await RefreshOffersAsync().ConfigureAwait(false);
        await RefreshSubscriptionMembersAsync().ConfigureAwait(false);
    }

    // The servers of the configurations that hand out the releases.
    private IReadOnlyList<UpdateServer> UpdateServers()
    {
        var offered = _offered;
        return UpdateSources.Servers(OrderedNames().Where(offered.ContainsKey).Select(name => (name, offered[name])));
    }

    // Reads another source or channel at once, so the window shows what it offers.
    private void RecheckUpdate()
    {
        PushSnapshot();
        _ = Task.Run(() => _updater.CheckAsync(true, CancellationToken.None), CancellationToken.None);
    }

    // Reads what the servers of the configurations offer, as last kept.
    private async Task RefreshOffersAsync()
    {
        var offered = new Dictionary<string, ServerOffer>(StringComparer.Ordinal);
        foreach (var (name, text) in _configs.ToList())
        {
            offered[name] = await _offers.OfferAsync(name, text, CancellationToken.None).ConfigureAwait(false);
        }

        _offered = offered;
    }

    // Every configuration with its text, for their servers to be asked what they offer.
    private IReadOnlyList<(string Config, string? Text)> OfferTargets() =>
        [.. _configs.ToList().Select(entry => (entry.Key, (string?)entry.Value))];

    // Asks the servers again behind a configuration that was stored.
    private IpcAck AskServers(IpcAck ack)
    {
        if (ack.Ok)
        {
            _offers.Warm(OfferTargets(), OfferChangedAsync);
        }

        return ack;
    }

    // Asks the servers of the configurations a subscription or a bundle brought.
    private async Task<IpcAck> AskNewServersAsync(IpcAck ack)
    {
        await _offers.WarmUnaskedAsync(OfferTargets(), OfferChangedAsync, CancellationToken.None).ConfigureAwait(false);

        return ack;
    }

    // A server that offers the tunnel something else now shows it in the next snapshot, and a tunnel dialing
    // another websocket front than it names is dialed again.
    private async Task OfferChangedAsync(string config)
    {
        await RefreshOffersAsync().ConfigureAwait(false);
        await RefreshSubscriptionMembersAsync().ConfigureAwait(false);
        var routingChanged = await RefreshRoutingSelectionAsync().ConfigureAwait(false);

        FindAppsLater();
        if (_active && string.Equals(_boundTarget, config, StringComparison.Ordinal))
        {
            var (changed, available) = await KeepServerListsAsync(config).ConfigureAwait(false);
            routingChanged |= changed || !available;
        }

        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        await RefreshGeoSourcesAsync().ConfigureAwait(false);
        if (routingChanged)
        {
            ReconnectRouting();
        }

        PushSnapshot();
        if (!routingChanged)
        {
            RedialForFront(config);
        }
    }

    // Dials the tunnel again when it stands dialing a websocket front the server of its configuration no longer names.
    private void RedialForFront(string config)
    {
        var dialing = _frontHanded && _dialWanted && _active && _boundStatus == ConnectionStatus.Connecting
            && string.Equals(_boundTarget, config, StringComparison.Ordinal);
        if (!_configs.TryGetValue(config, out var text) || !FrontRedial.Wanted(dialing, _handedFront, Front(config, text)))
        {
            return;
        }

        _frontHanded = false;
        _log.Info("agent", $"{config}: its server names another websocket front than the one the tunnel dials; the tunnel is dialed again");
        _ = RestartTunnelAsync();
    }

    // SQLite owns the choice. The in-process copy is only used for the synchronous status snapshot and legacy callers.
    private async Task<bool> RefreshRoutingSelectionAsync()
    {
        await _routingSelectionRead.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = Volatile.Read(ref _routingSelection);
            var current = await _store.GetSelectedRoutingListsAsync().ConfigureAwait(false);
            Volatile.Write(ref _routingSelection, current);
            var changed = (previous is null) != (current is null) || !(previous ?? []).SequenceEqual(current ?? []);
            if (changed)
            {
                _log.Debug("agent", $"Refreshed the routing selection from SQLite ({current?.Count ?? 0} list(s))");
            }

            return changed;
        }
        finally
        {
            _routingSelectionRead.Release();
        }
    }

    // The files of the geo sources a server handed out are fetched in the background, then the lists take them.
    private async Task FetchOfferedAsync(IReadOnlyList<GeoSource> sources)
    {
        try
        {
            await RefreshGeoSourcesAsync().ConfigureAwait(false);
            foreach (var source in sources)
            {
                await UpdateOneSourceAsync(source).ConfigureAwait(false);
            }

            await AfterSourcesChangedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("geo", "the sources the server handed out were not fetched", ex);
        }
    }

    // What asking the servers has to say, at the level its news deserves.
    private void OfferNote(string message, Exception? ex)
    {
        if (ex is null)
        {
            _log.Info("agent", message);
        }
        else
        {
            _log.Failure("agent", message, string.Empty, ex);
        }
    }

    // The websocket front the tunnel of a configuration is carried to, while it is asked for.
    private WsEndpoint? Front(string name, string text) =>
        _transports.GetValueOrDefault(name) is { UseWebSocket: true } transport ? WsEndpoint.Of(text, _offered.GetValueOrDefault(name), transport) : null;

    // What the server of the selected configuration offers; it is asked again behind the answer.
    private async Task<IpcAck> ServerOfferAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var config = _selectedTarget ?? string.Empty;
        var text = _configs.GetValueOrDefault(config);
        _offers.Warm([(config, text)], OfferChangedAsync);

        return new IpcAck(true, OfferReply.Render(config, await _offers.OfferAsync(config, text, CancellationToken.None).ConfigureAwait(false)));
    }

    // Where the send leg uploads to, and whether that is the server of the selected configuration.
    private async Task<(string Url, bool Own)> UploadAsync(string path, string chosen, CancellationToken ct)
    {
        if (chosen.Length > 0 || _selectedTarget is not { Length: > 0 } config)
        {
            return (chosen, false);
        }

        return ServerOffers.Upload(chosen, await _offers.SpeedAsync(config, _configs.GetValueOrDefault(config), ct).ConfigureAwait(false), path);
    }

    // Reads the configurations from this agent's own JSON once, whichever comes first: the window or a command.
    private void EnsureLoaded()
    {
        lock (_loadGate)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            Load();
            if (_updater.Settle())
            {
                Save();
            }
        }
    }

    private async Task RefreshSubscriptionMembersAsync()
    {
        var members = new Dictionary<string, SubscriptionMember>(StringComparer.Ordinal);
        foreach (var member in await _store.ListSubscriptionMembersAsync(null).ConfigureAwait(false))
        {
            members[member.ConfigName] = member;
        }

        _members = members;
        _stale = await SubscriptionBinding.StaleAsync(_store, CancellationToken.None).ConfigureAwait(false);
    }

    // Picks the routing list every config uses. Args: list id, or "none" to turn routing off.
    private async Task<IpcAck> AssignRoutingAsync(IReadOnlyList<string> args)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var listArg = args.Count > 0 ? args[0] : "none";
        long? picked = null;
        if (!string.Equals(listArg, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(listArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var listId) || listId <= 0
                || await _store.GetRoutingListStampAsync(listId).ConfigureAwait(false) is null)
            {
                _log.Warn("agent", "The routing selection refers to an unknown or invalid list");
                return Fail();
            }

            picked = listId;
        }
        return await ChangeRoutingAsync(new(RoutingSelectionChange.Replace, picked is long id ? [id] : [])).ConfigureAwait(false);
    }

    private async Task<IpcAck> ChangeRoutingAsync(RoutingSelectionChange? change)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        _log.Debug("routing", $"changing selection: {change?.Action}, {change?.Ids.Count ?? 0} ID(s)");
        var (refusal, before, after) = await ServerListsOnly.ChangeSelectionAsync(_store,
            await ServerListsLimitAsync().ConfigureAwait(false), change).ConfigureAwait(false);
        if (refusal is not null)
        {
            _log.Warn("routing", "the routing selection change was refused");
            return refusal;
        }

        await RefreshRoutingSelectionAsync().ConfigureAwait(false);
        var changed = !before.SequenceEqual(after);
        if (changed && before.Count <= 1 && after.Count <= 1)
        {
            Journal(SwitchLog.RoutingList(
                await ListNameAsync(before.Count == 0 ? null : before[0]).ConfigureAwait(false),
                await ListNameAsync(after.Count == 0 ? null : after[0]).ConfigureAwait(false)));
        }

        if (changed)
        {
            _log.Info("routing", $"selection changed to {after.Count} list(s)");
        }

        if (changed || _restartRequired)
        {
            ReconnectRouting();
        }

        PushSnapshot();
        return Ok();
    }

    private async Task<(GeoRoutingPlan Plan, string Stamp, string? AppMode, string[] Apps, string[] Bypass)>
        PrepareSessionPlanAsync(string config, ConfigTransport? transport, CancellationToken ct)
    {
        var policy = ConfigRouting.Allowed(transport)
            ? await _geo.LoadPolicyAsync(await _store.GetSelectedRoutingListsAsync(ct).ConfigureAwait(false),
                AppRulePlatform.Android, ct: ct).ConfigureAwait(false)
            : null;
        var inbound = transport is { AllowInbound: true } && _configs.TryGetValue(config, out var text)
            ? TunnelInbound.Ranges(WgConfigEditor.GetAddresses(text), WgConfigEditor.GetAllowedIps(text), transport.InboundNetwork)
            : [];
        var session = GeoRoutingPlan.FromPolicy(policy, transport?.UseRouter != false, transport?.UseIpv6 == true, (int)Build.VERSION.SdkInt,
            inbound, _routeTtl, _splitLimit, IsPackageInstalled);
        if (session.Plan.UseConfigRoutes && _configs.TryGetValue(config, out var configText)
            && !RouteBudget.Fits(session.Plan.RoutesForFamily(WgConfigEditor.GetAllowedIps(configText), transport?.UseIpv6 == true).Count, 0))
        {
            throw new NotSupportedException("The profile's AllowedIPs exceed the Android VPN Binder budget.");
        }

        return session;
    }

    private static bool IsPackageInstalled(string package)
    {
        try
        {
            return !string.Equals(package, Application.Context.PackageName, StringComparison.Ordinal)
                && Application.Context.PackageManager?.GetPackageInfo(package, 0) is not null;
        }
        catch (global::Android.Content.PM.PackageManager.NameNotFoundException)
        {
            return false;
        }
    }

    // The first ID is retained for single-list callers until the multi-list IPC task.
    private long? RoutedList => _selectedTarget is { Length: > 0 } name
        && ConfigRouting.Allowed(_transports.GetValueOrDefault(name)) ? SelectedRoutingList : null;

    private static bool PerAppSupported => RouteBudget.SupportsOwner((int)Build.VERSION.SdkInt);

    private static int SubscriptionInterval(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            ? Math.Clamp(hours, SettingKeys.SubscriptionIntervalMinHours, SettingKeys.SubscriptionIntervalMaxHours)
            : DefaultSubscriptionIntervalHours;
    }

    private static int GeoInterval(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            ? Math.Clamp(hours, MinGeoIntervalHours, MaxGeoIntervalHours)
            : 24;

    private static bool IsOn(string value) =>
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "1", StringComparison.Ordinal)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private void Load()
    {
        try
        {
            if (!System.IO.File.Exists(_storePath))
            {
                return;
            }

            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(_storePath));
            LoadMap(document.RootElement, "Configs", _configs);
            _order = [.. _configs.Keys];
            _legacyRoutingSelection = LegacyRoutingSelection.Read(document.RootElement);

            if (document.RootElement.TryGetProperty("LogLevel", out var level) && level.ValueKind == JsonValueKind.String)
            {
                _logLevel = KnownLogLevel(level.GetString() ?? "info");
            }

            if (document.RootElement.TryGetProperty("RouteLog", out var route))
            {
                _routeLog = route.ValueKind == JsonValueKind.True;
            }

            if (document.RootElement.TryGetProperty("DirectTcp", out var directTcp)
                && directTcp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _directTcp = directTcp.ValueKind == JsonValueKind.True;
            }

            if (document.RootElement.TryGetProperty("ExcludeRoutes", out var excludeRoutes)
                && excludeRoutes.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _excludeRoutes = excludeRoutes.ValueKind == JsonValueKind.True;
            }

            if (document.RootElement.TryGetProperty("LocalInTunnel", out var localInTunnel)
                && localInTunnel.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _localInTunnel = localInTunnel.ValueKind == JsonValueKind.True;
            }

            if (document.RootElement.TryGetProperty("TunnelNotice", out var tunnelNotice)
                && tunnelNotice.ValueKind == JsonValueKind.String)
            {
                _tunnelNotice = TunnelNoticeModes.Known(tunnelNotice.GetString());
            }

            if (document.RootElement.TryGetProperty("UpdateChannel", out var channel) && channel.ValueKind == JsonValueKind.String)
            {
                _updater.PickedChannel = UpdateChannels.Of(channel.GetString(), string.Empty);
            }

            if (document.RootElement.TryGetProperty("UpdateSource", out var source) && source.ValueKind == JsonValueKind.String)
            {
                _updater.Choice = UpdateSources.Choice(source.GetString()) ?? string.Empty;
            }

            if (document.RootElement.TryGetProperty("UpdateOrigin", out var origin) && origin.ValueKind == JsonValueKind.String)
            {
                _updater.Origin = UpdateOrigin.Read(origin.GetString()) ?? UpdateOrigin.Baked;
            }

            if (document.RootElement.TryGetProperty("UpdateHanded", out var handed) && handed.ValueKind == JsonValueKind.String)
            {
                _updater.Handed = UpdateOrigin.Read(handed.GetString());
            }

            if (document.RootElement.TryGetProperty("RouteTtl", out var ttl)
                && ttl.ValueKind == JsonValueKind.Number
                && SettingKeys.TryParseRouteTtl(ttl.GetRawText(), out var seconds))
            {
                _routeTtl = seconds;
            }

            if (document.RootElement.TryGetProperty("SplitLimit", out var storedSplit)
                && storedSplit.ValueKind == JsonValueKind.Number
                && SettingKeys.TryParseSplitRouteLimit(storedSplit.GetRawText(), out var storedLimit))
            {
                _splitLimit = storedLimit;
            }

            if (document.RootElement.TryGetProperty("GeoAutoCheck", out var geoAuto)
                && geoAuto.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _geoAutoCheck = geoAuto.ValueKind == JsonValueKind.True;
            }

            if (document.RootElement.TryGetProperty("GeoCheckInterval", out var geoInterval)
                && geoInterval.ValueKind == JsonValueKind.Number)
            {
                _geoCheckIntervalHours = GeoInterval(geoInterval.GetRawText());
            }

            if (document.RootElement.TryGetProperty("DialWanted", out var dial)
                && (dial.ValueKind == JsonValueKind.True || dial.ValueKind == JsonValueKind.False))
            {
                _dialWanted = dial.GetBoolean();
            }

            if (document.RootElement.TryGetProperty("SubscriptionAutoRefresh", out var subAuto)
                && subAuto.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _subscriptionAutoRefresh = subAuto.ValueKind == JsonValueKind.True;
            }

            if (document.RootElement.TryGetProperty("SubscriptionInterval", out var subInterval)
                && subInterval.ValueKind == JsonValueKind.Number)
            {
                _subscriptionIntervalHours = SubscriptionInterval(subInterval.GetRawText());
            }

            if (document.RootElement.TryGetProperty("GeoCheckedAt", out var geoChecked)
                && geoChecked.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(geoChecked.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var checkedAt))
            {
                _geoCheckedAt = checkedAt;
            }

            if (document.RootElement.TryGetProperty("Proxy", out var proxy) && proxy.ValueKind == JsonValueKind.Object)
            {
                _proxyOptions = ReadProxy(proxy);
                // The tunnel runs in its own process and listens by its own copy; a restored library brings it back.
                VpnBridge.WriteProxy(_proxyOptions);
            }

            var target = document.RootElement.TryGetProperty("Selected", out var selected) && selected.ValueKind == JsonValueKind.String
                ? selected.GetString()
                : null;
            target = CarryProfile(document.RootElement, target);

            // A selection outliving the config it names would refuse every connect with nothing to point at.
            if (target is { Length: > 0 } && _configs.ContainsKey(target))
            {
                _selectedTarget = target;
            }
        }
        catch (Exception)
        {
        }
    }

    // One-time move off named profiles: the selected profile (else any) hands its config to the selection and its
    // routing list to the global one. The next regular settings save drops the legacy maps.
    private string? CarryProfile(JsonElement root, string? target)
    {
        if (!root.TryGetProperty("Profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object)
        {
            return target;
        }

        var carried = default(string);
        foreach (var entry in profiles.EnumerateObject())
        {
            if (carried is null || string.Equals(entry.Name, target, StringComparison.Ordinal))
            {
                carried = entry.Name;
            }
        }

        if (carried is null)
        {
            return target;
        }

        _legacyRoutingSelection ??= LegacyRoutingSelection.ReadProfile(root, carried);

        var bound = profiles.GetProperty(carried);
        return bound.ValueKind == JsonValueKind.String && bound.GetString() is { Length: > 0 } config ? config : target;
    }

    private static void LoadMap(JsonElement root, string name, Dictionary<string, string> into)
    {
        if (!root.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var entry in map.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String)
            {
                into[entry.Name] = entry.Value.GetString() ?? string.Empty;
            }
        }
    }

    private void Save()
    {
        try
        {
            var builder = new System.Text.StringBuilder();
            builder.Append("{\"Configs\":");
            AppendMap(builder, OrderedNames(), _configs);
            builder.Append(",\"LogLevel\":").Append(JsonSerializer.Serialize(_logLevel));
            builder.Append(",\"RouteLog\":").Append(_routeLog ? "true" : "false");
            builder.Append(",\"DirectTcp\":").Append(_directTcp ? "true" : "false");
            builder.Append(",\"ExcludeRoutes\":").Append(_excludeRoutes ? "true" : "false");
            builder.Append(",\"LocalInTunnel\":").Append(_localInTunnel ? "true" : "false");
            builder.Append(",\"TunnelNotice\":").Append(JsonSerializer.Serialize(_tunnelNotice));
            builder.Append(",\"RouteTtl\":").Append(_routeTtl);
            builder.Append(",\"SplitLimit\":").Append(_splitLimit);
            builder.Append(",\"UpdateChannel\":").Append(JsonSerializer.Serialize(_updater.PickedChannel));
            builder.Append(",\"UpdateSource\":").Append(JsonSerializer.Serialize(_updater.Choice));
            builder.Append(",\"UpdateOrigin\":").Append(JsonSerializer.Serialize(_updater.Origin.Write()));
            builder.Append(",\"UpdateHanded\":").Append(JsonSerializer.Serialize(_updater.Handed?.Write() ?? string.Empty));
            builder.Append(",\"GeoAutoCheck\":").Append(_geoAutoCheck ? "true" : "false");
            builder.Append(",\"GeoCheckInterval\":").Append(_geoCheckIntervalHours);
            builder.Append(",\"GeoCheckedAt\":").Append(JsonSerializer.Serialize(_geoCheckedAt.ToString("O", CultureInfo.InvariantCulture)));
            builder.Append(",\"SubscriptionAutoRefresh\":").Append(_subscriptionAutoRefresh ? "true" : "false");
            builder.Append(",\"SubscriptionInterval\":").Append(_subscriptionIntervalHours);
            builder.Append(",\"Proxy\":").Append(JsonSerializer.Serialize(_proxyOptions));
            builder.Append(",\"DialWanted\":").Append(_dialWanted ? "true" : "false");
            builder.Append(",\"Selected\":").Append(JsonSerializer.Serialize(_selectedTarget));
            // Keep the legacy choice if another setting is saved before SQLite initialization completes.
            if (_legacyRoutingSelection is { } legacy)
            {
                builder.Append(",\"SelectedRouting\":").Append(legacy.ListId.GetValueOrDefault().ToString(CultureInfo.InvariantCulture));
            }
            builder.Append('}');
            System.IO.File.WriteAllText(_storePath, builder.ToString());
        }
        catch (Exception)
        {
        }
    }

    private static void AppendMap(System.Text.StringBuilder builder, List<string> names, Dictionary<string, string> map)
    {
        builder.Append('{');
        var first = true;
        foreach (var name in names)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(JsonSerializer.Serialize(name)).Append(':').Append(JsonSerializer.Serialize(map[name]));
        }

        builder.Append('}');
    }

    // Renames a config in place, carrying its text, its stored settings, the selection and the live latch with it.
    // Imports a config from a file the caller names; the text path is import-config's.
    private async Task<IpcAck> AddConfigAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[0].Length == 0 || args[1].Length == 0)
        {
            return Fail();
        }

        try
        {
            var text = await System.IO.File.ReadAllTextAsync(args[1]).ConfigureAwait(false);
            return await DispatchAsync(new IpcCommand(IpcContract.OpImportConfig, [args[0], text])).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("agent", $"the configuration file '{args[1]}' could not be read", ex);
            return new IpcAck(false, ex.Message);
        }
    }

    // Duplicates a config into an independent copy: its text, its transport and its geo settings.
    private async Task<IpcAck> CopyConfigAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[0].Length == 0 || args[1].Trim().Length == 0)
        {
            return Fail();
        }

        var source = args[0];
        var destination = args[1].Trim();
        if (!_configs.TryGetValue(source, out var text))
        {
            return Fail();
        }

        if (_configs.ContainsKey(destination))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NameTaken", destination));
        }

        _configs[destination] = text;
        Save();
        await EnsureInitAsync().ConfigureAwait(false);
        if (await _store.GetConfigTransportAsync(source).ConfigureAwait(false) is { } transport)
        {
            await _store.SetConfigTransportAsync(transport with { Name = destination }).ConfigureAwait(false);
        }

        if (await _store.GetTunnelGeoAsync(source).ConfigureAwait(false) is { } geo)
        {
            await _store.SaveTunnelGeoAsync(geo with { Name = destination }).ConfigureAwait(false);
        }

        await RefreshTransportsAsync().ConfigureAwait(false);
        PushSnapshot();
        return new IpcAck(true, IpcMessage.Key("Agent_ConfigCopied", destination));
    }

    // Stores a config's own geo rules; the session takes them at the next connect.
    private async Task<IpcAck> SetGeoAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !_configs.ContainsKey(args[0]))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        try
        {
            await EnsureGeoFilesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("geo", $"the rules are expanded against the geo files at hand: {ex}");
        }

        var (rules, routes, domains, skipped) = await _geo.ApplyAsync(args[0], IsOn(args[1]), [.. args.Skip(2)]).ConfigureAwait(false);
        PushSnapshot();
        var summary = $"saved: {rules} rules, {routes} routes, {domains} domains";
        return new IpcAck(true, skipped > 0
            ? $"{summary}, {skipped} tokens ignored (applies on reconnect)"
            : $"{summary} (applies on reconnect)");
    }

    // The private networks and hosts the stored configurations reach, each behind the name of the one reaching it
    // and a tab.
    private List<string> ConfigSubnets()
    {
        var lines = new List<string>();
        foreach (var name in OrderedNames())
        {
            foreach (var network in PrivateNetworks.Reachable(_configs[name]))
            {
                lines.Add($"{name}\t{network}");
            }
        }

        return lines;
    }

    // Asks every source whether its remote file changed, without downloading it.
    private async Task<IpcAck> CheckSourcesAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        if (_geoSources.Count == 0)
        {
            return new IpcAck(true, IpcMessage.Key("Agent_NoSourcesToCheck"));
        }

        var available = 0;
        foreach (var source in _geoSources.ToList())
        {
            if (await CheckOneSourceAsync(source).ConfigureAwait(false) == GeoUpdateChecker.Status.Available)
            {
                available++;
            }
        }

        await RefreshGeoSourcesAsync().ConfigureAwait(false);
        PushSnapshot();
        return new IpcAck(true, available == 0
            ? IpcMessage.Key("Agent_CheckedNoUpdates", _geoSources.Count)
            : IpcMessage.Key("Agent_CheckedUpdatesAvailable", _geoSources.Count, available));
    }

    private async Task<IpcAck> CheckSourceAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || args[0].Length == 0)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var source = _geoSources.FirstOrDefault(entry => string.Equals(entry.Name, args[0], StringComparison.Ordinal));
        if (source is null)
        {
            return Fail();
        }

        var status = await CheckOneSourceAsync(source).ConfigureAwait(false);
        await RefreshGeoSourcesAsync().ConfigureAwait(false);
        PushSnapshot();
        return new IpcAck(true, status switch
        {
            GeoUpdateChecker.Status.Available => IpcMessage.Key("Agent_SourceUpdateAvailable", source.Name),
            GeoUpdateChecker.Status.UpToDate => IpcMessage.Key("Agent_SourceUpToDate", source.Name),
            _ => IpcMessage.Key("Agent_SourceCheckFailed", source.Name),
        });
    }

    // Checks one source under its own ceiling; an unreachable host must not hold the command.
    private async Task<GeoUpdateChecker.Status> CheckOneSourceAsync(GeoSource source)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var status = await _geoChecker.CheckAsync(source, budget.Token).ConfigureAwait(false);
            if (status != GeoUpdateChecker.Status.Unknown)
            {
                await _store.SetGeoUpdateAvailableAsync(source.Name, status == GeoUpdateChecker.Status.Available).ConfigureAwait(false);
            }

            return status;
        }
        catch (System.OperationCanceledException)
        {
            return GeoUpdateChecker.Status.Unknown;
        }
        catch (Exception ex)
        {
            _log.Failure("geo", $"'{source.Name}' could not be checked for a newer file; the copy at hand stays in use", NetworkFailure.Address(source.Url), ex);
            return GeoUpdateChecker.Status.Unknown;
        }
    }

    // Программы устройства, которые может назвать правило по приложению.
    private static IpcAck ListProcesses()
    {
        var rows = AppCensus.List(Application.Context)
            .Select(app => string.Join('\t', "app", app.Label, app.Package, $"app:pkg={app.Package}"));
        return new IpcAck(true, string.Join('\n', rows));
    }

    // The ladder, as far as a phone can measure it: the socket cannot be excused from the tunnel outside the
    // service that owns it, so the leg under the tunnel is skipped rather than measured through the tunnel.
    private async Task<IpcAck> CheckChannelAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var config = _selectedTarget;
        if (string.IsNullOrEmpty(config))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NoConfigSelected"));
        }

        // Configs live in this agent's own JSON; the store the desktop agents read them from is empty here.
        var text = _configs.GetValueOrDefault(config, string.Empty);
        var running = VpnBridge.IsRunning(Application.Context);
        var transport = await _store.GetConfigTransportAsync(config, ct).ConfigureAwait(false);
        var carrier = Carrier(text, transport, _offered.GetValueOrDefault(config));
        var speed = await _offers.SpeedAsync(config, text, ct).ConfigureAwait(false);
        var options = new ChannelProbeOptions(
            config,
            running,
            LocalGateway.Find(),
            await ResolveAsync(carrier.Host, ct).ConfigureAwait(false),
            LinkLossProbe.PeerTargets(WgConfigEditor.GetAddresses(text)),
            LinkLossProbe.BeyondTargets(WgConfigEditor.GetDns(text)),
            true,
            false,
            running && _handshakeUnix > 0 ? (int)Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _handshakeUnix) : -1,
            running ? _link.HandshakesPerMinute : -1,
            SourceHost: args.Count > 0 && args[0].Length > 0 ? args[0] : BusiestHost(),
            ConfiguredMtu: text.Length == 0 ? 0 : MtuPlan.ResolveForLink(transport, text),
            CarrierPort: carrier.Port,
            TunnelSpeedUrl: ServerOffers.Download(speed, true),
            DirectSpeedUrl: ServerOffers.Download(speed, false),
            Churning: running && _link.Churning,
            RekeySeconds: running ? _link.RekeySeconds : -1,
            ChurnPerMinute: ChurnPerMinute(config));

        var report = await ChannelProbe.RunAsync(options, ct).ConfigureAwait(false);
        Record(report.Render(), report.Culprit.Length > 0, report.Advice);
        return new IpcAck(true, report.ToPayload());
    }

    // The destination the user is actually watching: the relay lives in the tunnel process and leaves what it
    // ranks by traffic where the head can read it.
    private static string? BusiestHost()
    {
        var busiest = VpnBridge.ReadSessions(SessionWindowSeconds).Busiest;
        if (busiest is null)
        {
            return null;
        }

        return busiest.Name.Length > 0 ? busiest.Name : busiest.Host;
    }

    // What the relay holds, as it left it: the tunnel runs in another process, so this is read whole rather
    // than asked for, and a snapshot older than the window answers about a session that is already gone.
    // Every destination the agent can put a name to: what it resolved for this config before, which outlives a
    // disconnect, and what the tunnel carries right now.
    private async Task<IpcAck> KnownHostsAsync(CancellationToken ct)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var hosts = new List<string>(await _log.Store.ListProbeTargetsAsync(KnownHostList.HistoryRows, ct).ConfigureAwait(false));
        if (_selectedTarget is { Length: > 0 } config)
        {
            foreach (var resolution in await _store.ListDomainResolutionsAsync(config, ct).ConfigureAwait(false))
            {
                hosts.Add(resolution.Domain);
            }
        }

        foreach (var held in VpnBridge.ReadSessions(SessionWindowSeconds).Sessions)
        {
            hosts.Add(held.Name.Length > 0 ? held.Name : held.Host);
        }

        return new IpcAck(true, KnownHostList.Payload(hosts));
    }

    private async Task<IpcAck> GetSessionsAsync()
    {
        var report = VpnBridge.ReadSessions(SessionWindowSeconds);
        if (report.Mode.Length == 0)
        {
            return new IpcAck(true, report.ToPayload());
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var list = Volatile.Read(ref _appliedRequest)?.Plan?.ListName ?? string.Empty;
        return new IpcAck(true, (report with { List = list }).ToPayload());
    }

    // Серверы для карточек каталога. Под поднятым туннелем меряет тоннельный процесс: сокет от туннеля
    // освобождает только он, здесь замер ушёл бы в сам туннель.
    private async Task<IpcAck> ProbeServersAsync(CancellationToken ct)
    {
        var running = VpnBridge.IsRunning(Application.Context);
        var servers = new List<SweepServer>();
        foreach (var name in OrderedNames())
        {
            var text = _configs.GetValueOrDefault(name, string.Empty);
            var transport = await _store.GetConfigTransportAsync(name, ct).ConfigureAwait(false);
            var carrier = Carrier(text, transport, _offered.GetValueOrDefault(name));
            servers.Add(new SweepServer(
                name,
                await ResolveAsync(carrier.Host, ct).ConfigureAwait(false),
                carrier.Port,
                running && string.Equals(name, _selectedTarget, StringComparison.Ordinal)));
        }

        // Телефон несёт весь tun: поднятый туннель забирает дефолт у любого замера отсюда.
        return new IpcAck(true, running
            ? await HandOverCardsAsync(servers, ct).ConfigureAwait(false)
            : await CardProbe.RunAsync(servers, carriesDefault: false, bypass: null, ct).ConfigureAwait(false));
    }

    // Отдаёт замер туннелю и ждёт измеренного; молчащий туннель оставляет карточки без чисел, а не держит окно.
    private static async Task<string> HandOverCardsAsync(IReadOnlyList<SweepServer> servers, CancellationToken ct)
    {
        VpnBridge.ClearCardsResult();
        VpnBridge.WriteCards(new CardsRequest(servers, true));
        VpnBridge.RequestCards(Application.Context);
        for (var waited = 0; waited < ProbeWaitMs; waited += ProbePollMs)
        {
            await Task.Delay(ProbePollMs, ct).ConfigureAwait(false);
            var payload = VpnBridge.ReadCardsResult();
            if (payload.Length > 0)
            {
                VpnBridge.ClearCardsResult();
                return payload;
            }
        }

        VpnBridge.ClearCards();
        return CardProbe.Path(bypassed: false, carriesDefault: true);
    }

    // Every saved server measured by the legs that cost only echoes. A socket cannot be excused from the tunnel
    // outside the service that owns it, so a sweep run while the tunnel is up says so in its verdict.
    private async Task<IpcAck> CheckServersAsync(CancellationToken ct)
    {
        var running = VpnBridge.IsRunning(Application.Context);
        var servers = new List<SweepServer>();
        foreach (var name in OrderedNames())
        {
            var text = _configs.GetValueOrDefault(name, string.Empty);
            var transport = await _store.GetConfigTransportAsync(name, ct).ConfigureAwait(false);
            var carrier = Carrier(text, transport, _offered.GetValueOrDefault(name));
            servers.Add(new SweepServer(
                name,
                await ResolveAsync(carrier.Host, ct).ConfigureAwait(false),
                carrier.Port,
                running && string.Equals(name, _selectedTarget, StringComparison.Ordinal)));
        }

        // The phone carries the whole tun, so a tunnel that runs is the default route for every probe here.
        var report = await ServerSweep
            .RunAsync(servers, new SweepOptions(LocalGateway.Find(), running, running), ct)
            .ConfigureAwait(false);

        Record(report.Render(), report.VerdictKey != CheckVerdicts.SweepBest);
        return new IpcAck(true, report.ToPayload());
    }

    // Why one destination goes where it goes, under the rules in force.
    private async Task<IpcAck> CheckTargetAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "check-target requires a domain, an address, an app token or a geo rule");
        }

        var (inspector, _) = await RulesAsync(ct).ConfigureAwait(false);
        var report = await inspector
            .InspectAsync(await AppFinds.TargetAsync(_store, args[0], ct).ConfigureAwait(false), _selectedTarget ?? string.Empty, new TargetProbes(), ct)
            .ConfigureAwait(false);

        Record(report.Render(), report.VerdictKey != TargetVerdicts.Proxy);
        return new IpcAck(true, report.ToPayload());
    }

    // The rules in force: the inspector that answers under them, and whether they put everything in the tunnel.
    // A named application is added to the rules where the relay can name the owner of a connection, and below
    // that the tunnel is built as an allow list of them instead - the answer follows the mode in force.
    private async Task<(TargetInspector Inspector, bool Whole)> RulesAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _appliedRequest) is { Plan: { } applied } request && request.PlanStamp is not null)
        {
            IReadOnlyList<string> appliedApps = applied.FlowRules ? applied.TunnelApps : request.AppList ?? [];
            IReadOnlyList<string> direct = applied.FlowRules ? applied.DirectApps : request.BypassApps ?? [];
            var list = applied.ListIds.Count == 0 ? null : new RoutingList(applied.ListIds[0], applied.ListName, [],
                applied.ProxyRoutes, applied.ProxyDomains, PackageRules(appliedApps), applied.DirectRoutes,
                applied.DirectDomains, PackageRules(direct), applied.BlockRoutes, applied.BlockDomains);
            var scope = applied.FlowRules ? AppScope.Additive : request.AppMode == "include" ? AppScope.Exclusive : AppScope.None;
            return (new TargetInspector(list, !applied.FullTunnel, scope), applied.FullTunnel);
        }

        var policy = RoutedList is not null
            ? await _geo.LoadPolicyAsync(Volatile.Read(ref _routingSelection), AppRulePlatform.Android, ct: ct).ConfigureAwait(false)
            : null;
        var full = policy?.Settings?.UseGlobalProxy == true;
        return (new TargetInspector(policy?.List, !full), policy is null || full);

        static string[] PackageRules(IEnumerable<string> packages) => [.. packages.Select(package => "pkg=" + package)];
    }

    // Measures one destination over the path asked for. The tun fixes its routes at establish, so the tunnel
    // path holds only where the rules take the target there anyway, and a socket is excused from it only inside
    // the process that owns it - that run is handed to the tunnel instead.
    private async Task<IpcAck> ProbeTargetAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "probe-target requires a domain or an address");
        }

        var target = args[0];
        var path = args.Count > 1 && args[1].Length > 0 ? args[1] : ProbePaths.Auto;
        var running = VpnBridge.IsRunning(Application.Context);
        if (!running && path != ProbePaths.Bypass)
        {
            return ProbeAck(TargetProbe.Refused(target, path, ProbeVerdicts.NotConnected));
        }

        var (inspector, whole) = await RulesAsync(ct).ConfigureAwait(false);
        var forced = running && path == ProbePaths.Tunnel && !whole;
        var routed = path == ProbePaths.Auto || forced
            ? await RoutedPathAsync(inspector, target, ct).ConfigureAwait(false)
            : (Taken: string.Empty, ViaTunnel: false);

        // The target the rules keep outside the tun cannot be put back into it: the run says so itself, so the
        // path stays on offer and the refusal lands in the journal beside the runs that measured.
        if (forced && !routed.ViaTunnel)
        {
            return ProbeAck(TargetProbe.Refused(target, path, ProbeVerdicts.PathUnavailable));
        }

        var taken = path == ProbePaths.Auto ? routed.Taken : path;
        var (upload, own) = await UploadAsync(path, args.Count > 2 ? args[2] : string.Empty, ct).ConfigureAwait(false);
        if (running && path == ProbePaths.Bypass)
        {
            return ProbeAck(await HandOverAsync(target, path, taken, upload, own, ct).ConfigureAwait(false));
        }

        var options = new TargetProbeOptions(target, path, taken, upload, OwnUpload: own);
        return ProbeAck(await TargetProbe.RunAsync(options, ct).ConfigureAwait(false));
    }

    // Where the rules in force send a destination, said the way the desktops say it, and whether that is the
    // tunnel.
    private async Task<(string Taken, bool ViaTunnel)> RoutedPathAsync(TargetInspector inspector, string target, CancellationToken ct)
    {
        var report = await inspector
            .InspectAsync(target, _selectedTarget ?? string.Empty, new TargetProbes(), ct)
            .ConfigureAwait(false);
        return report.VerdictKey switch
        {
            TargetVerdicts.Proxy or TargetVerdicts.ProxyAppsOnly => ("tunnel by rule", true),
            TargetVerdicts.UnlistedFull => ("tunnel by default", true),
            TargetVerdicts.Direct => ("bypass by rule", false),
            TargetVerdicts.UnlistedSplit or TargetVerdicts.AppOutside or TargetVerdicts.AppUnlisted => ("bypass by default", false),
            TargetVerdicts.Blocked => ("blocked by rule", false),
            _ => (string.Empty, false),
        };
    }

    // Hands the run to the tunnel and waits for what it measured; a tunnel that never answers leaves the path
    // unmeasured rather than the caller waiting on it.
    private static async Task<ProbeReport> HandOverAsync(string target, string path, string taken, string upload, bool own, CancellationToken ct)
    {
        VpnBridge.ClearProbeResult();
        VpnBridge.WriteProbe(new ProbeRequest(target, path, taken, upload, own));
        VpnBridge.RequestProbe(Application.Context);
        for (var waited = 0; waited < ProbeWaitMs; waited += ProbePollMs)
        {
            await Task.Delay(ProbePollMs, ct).ConfigureAwait(false);
            var payload = VpnBridge.ReadProbeResult();
            if (payload.Length > 0)
            {
                VpnBridge.ClearProbeResult();
                return ProbeReport.Parse(payload, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }

        VpnBridge.ClearProbe();
        return TargetProbe.Refused(target, path, ProbeVerdicts.PathUnavailable);
    }

    // Stores a probe in its own journal, which no capture floor reaches, and puts its closing line in the agent log.
    private IpcAck ProbeAck(ProbeReport report)
    {
        var rendered = report.Render().TrimEnd();
        _log.Store.AppendProbe(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rendered);
        var rows = rendered.Split('\n');
        _log.Info("probe", rows[0].Trim());
        _log.Info("probe", rows[^1].Trim());
        return new IpcAck(true, report.ToPayload());
    }

    // Stores a finished run where no capture floor reaches it, and puts its closing line in the agent log.
    // An MTU that does not fit the measured path is warned about on its own, whoever the run blames.
    private void Record(string rendered, bool blamed, MtuAdvice? advice = null)
    {
        _log.Store.AppendCheck(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rendered.TrimEnd());
        if (advice is not null)
        {
            _log.Warn("check", advice.Describe());
        }

        var closing = rendered.TrimEnd().Split('\n')[^1].Trim();
        if (blamed)
        {
            _log.Warn("check", closing);
            return;
        }

        _log.Info("check", closing);
    }

    // The host the tunnel dials and the port to knock on: a websocket carrier stands at its front, and the
    // endpoint in the config is only what the server hands the tunnel to behind it.
    private static (string Host, int Port) Carrier(string text, ConfigTransport? transport, ServerOffer? offer) =>
        transport?.UseWebSocket == true && WsEndpoint.Of(text, offer, transport) is { } front
            ? (front.Host, front.Port)
            : (ConfigServices.Host(text), 0);

    // One address for a host, as the tunnel resolves it.
    private static async Task<string?> ResolveAsync(string host, CancellationToken ct)
    {
        if (host.Length == 0)
        {
            return null;
        }

        if (System.Net.IPAddress.TryParse(host, out var parsed))
        {
            return parsed.ToString();
        }

        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.FirstOrDefault(one => one.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException or System.OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<IpcAck> CollectDiagnosticsAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        try
        {
            await _log.Store.FlushAsync().ConfigureAwait(false);
            var bundle = new DiagnosticsBundle(_store, _log.Store);
            var held = VpnBridge.ReadSessions(SessionWindowSeconds);
            var texts = new Dictionary<string, string>(_configs, StringComparer.Ordinal);
            var path = await bundle.WriteAsync(
                DiagnosticsDirectory(),
                DiagnosticsHeader(held),
                AndroidAgentLog.Render,
                new BundleSources(
                    Cache: _ => Task.FromResult(held.Sessions.Count == 0 ? "the tunnel holds nothing right now" : held.Render()),
                    Recent: _ => Task.FromResult(RecentLog.Merge(_log.Recent.Snapshot(), RecentLog.Parse(VpnBridge.ReadRecent()))),
                    Library: new BundleLibrary([.. _order.Where(texts.ContainsKey)], name => texts.GetValueOrDefault(name)),
                    AppliedRoutingListIds: Volatile.Read(ref _appliedRequest)?.Plan?.ListIds ?? [])).ConfigureAwait(false);
            _log.Info("agent", $"diagnostics archive written to {path}; keys and credentials in it are masked");
            return new IpcAck(true, path);
        }
        catch (Exception ex)
        {
            _log.Error("agent", "the diagnostics archive could not be built; nothing was written", ex);
            return new IpcAck(false, IpcMessage.Key("Agent_DiagnosticsFailed", ex.Message));
        }
    }

    // Writes where support can pull the archive without root; the private directory is the fallback.
    private static string DiagnosticsDirectory()
    {
        var external = Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
        var root = string.IsNullOrEmpty(external) ? Application.Context.FilesDir?.AbsolutePath ?? "." : external;
        return System.IO.Path.Combine(root, "diagnostics");
    }

    // Opens the diagnostics summary with the build, the device, the agent's own state and what the session of the
    // tunnel went through.
    private string DiagnosticsHeader(SessionReport held)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("AmneziaGeo diagnostics");
        sb.AppendLine($"generated:       {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"app version:     {AppVersion}");
        sb.AppendLine($"android:         {Build.VERSION.Release} (api {(int)Build.VERSION.SdkInt})");
        sb.AppendLine($"device:          {Build.Manufacturer} {Build.Model}");
        sb.AppendLine($"abi:             {string.Join(", ", Build.SupportedAbis ?? [])}");
        sb.AppendLine();
        sb.AppendLine("[settings]");
        sb.AppendLine($"log level:       {_logLevel}");
        sb.AppendLine($"routing log:     {(_routeLog ? "on" : "off")}");
        sb.AppendLine($"route ttl:       {_routeTtl}s");
        sb.AppendLine();
        sb.AppendLine("[state]");
        sb.AppendLine($"selected target: {_selectedTarget ?? "-"}");
        sb.AppendLine($"bound target:    {_boundTarget ?? "-"}");
        sb.AppendLine($"status:          {_boundStatus}");
        sb.AppendLine($"connect failed:  {_connectFailed}");
        sb.AppendLine();
        var marks = VpnBridge.IsRunning(Application.Context) ? SessionMarks.Parse(VpnBridge.ReadMarks()) : new SessionMarks();
        foreach (var line in marks.Lines(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), held))
        {
            sb.AppendLine(line);
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private IpcAck ReorderConfigs(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Fail();
        }

        foreach (var name in args)
        {
            if (!_configs.ContainsKey(name))
            {
                return new IpcAck(false, Loc.Instance.Get("Agent_ConfigUnknown", name));
            }
        }

        _order = [.. args];
        Save();
        PushSnapshot();
        return Ok();
    }

    private async Task<IpcAck> RenameConfigAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        var oldName = args[0];
        var newName = args[1];
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return Ok();
        }

        if (!_configs.TryGetValue(oldName, out var value))
        {
            return new IpcAck(false, Loc.Instance.Get("Agent_ConfigUnknown", oldName));
        }

        if (_configs.ContainsKey(newName))
        {
            return new IpcAck(false, Loc.Instance.Get("Agent_NameTaken", newName));
        }

        _configs.Remove(oldName);
        _configs[newName] = value;

        // A rename keeps the row where it stands.
        var place = _order.IndexOf(oldName);
        if (place >= 0)
        {
            _order[place] = newName;
        }

        RetargetSelection(oldName, newName);
        Save();
        await EnsureInitAsync().ConfigureAwait(false);
        await ConfigRename.CarryAsync(_store, oldName, newName).ConfigureAwait(false);
        await RefreshTransportsAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
    }

    // A new selection invalidates preparation before it can hand its old request to the service.
    private Task<IpcAck> SelectConfigAsync(IReadOnlyList<string> args) => Task.FromResult(_connectPreparation.Apply(() =>
    {
        var name = args.Count > 0 && args[0].Length > 0 ? args[0] : null;
        if (name is not null && !_configs.ContainsKey(name))
        {
            return Fail();
        }

        if (string.Equals(_selectedTarget, name, StringComparison.Ordinal)
            && (name is not null || (!_active && !_connectPreparation.IsPending && _pendingLaunch is null)))
        {
            return Ok();
        }

        var preparing = _connectPreparation.IsPending || _pendingLaunch is not null;
        if (!RevokePendingLaunch())
        {
            return new IpcAck(false, "could not revoke the pending VPN launch");
        }

        Journal(SwitchLog.Config(_selectedTarget, name));
        _selectedTarget = name;

        if (name is null)
        {
            return _connectPreparation.Disconnect(DisconnectTunnel).Ack;
        }

        _connectPreparation.Invalidate();
        if (preparing)
        {
            _log.Info("agent", "[FIX] the VPN preparation was cancelled after the selected configuration changed");
            RestoreCancelledPreparation();
        }
        else
        {
            Save();
            PushSnapshot();
        }

        return Ok();
    }));

    private void RestoreCancelledPreparation()
    {
        if (TryRestoreRunningTunnel())
        {
            Save();
            PushSnapshot();
            return;
        }

        ClearDialIntent();
        Save();
        OnVpnStateChanged(VpnStage.Disconnected, null);
    }

    // Keeps a switchover in the log whatever the capture floor is.
    private void Journal(string? line)
    {
        if (line is { Length: > 0 })
        {
            _log.Note(SwitchLog.Source, line);
        }
    }

    // The name a routing list id stands for; null id is routing off.
    private async Task<string?> ListNameAsync(long? listId)
    {
        if (listId is not long id)
        {
            return null;
        }

        await EnsureInitAsync().ConfigureAwait(false);
        return (await _store.GetRoutingListStampAsync(id).ConfigureAwait(false))?.Name;
    }

    // Refuses while the config is the running target; otherwise drops it with its stored settings and clears a selection pointing at it.
    private async Task<IpcAck> AddSubscriptionAsync(IReadOnlyList<string> args)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var outcome = await Subscriptions().AddAsync(args, CancellationToken.None).ConfigureAwait(false);
        if (!outcome.Ack.Ok)
        {
            if (outcome.Foreign)
            {
                _log.Warn("sub", $"subscription {outcome.Name} refused: {SubscriptionCodec.AddressName(args[0])} is not an AmneziaGeo panel");
            }

            return outcome.Ack;
        }

        _log.Info("sub", $"subscription {outcome.Name} brought in {outcome.Added} configuration(s)");

        // A first import is ready to dial: it takes the selection while there is none.
        if (_selectedTarget is null
            && await Subscriptions().MembersAsync(outcome.Name, CancellationToken.None).ConfigureAwait(false) is [var first, ..])
        {
            _selectedTarget = first;
        }

        await RefreshSubscriptionMembersAsync().ConfigureAwait(false);
        Save();
        PushSnapshot();
        return outcome.Ack;
    }

    private async Task<IpcAck> ListSubscriptionsAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        return await Subscriptions().ListAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<IpcAck> RefreshSubscriptionAsync(IReadOnlyList<string> args)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var targets = await Subscriptions().HelloTargetsAsync(args, OfferTargets(), CancellationToken.None).ConfigureAwait(false);
        await _offers.AskEachAsync(targets, OfferChangedAsync, CancellationToken.None).ConfigureAwait(false);
        var outcome = await Subscriptions().RefreshAsync(args, CancellationToken.None).ConfigureAwait(false);
        DropRunningIfGone();
        FlagRewritten(outcome);
        await RefreshTransportsAsync().ConfigureAwait(false);
        Save();
        PushSnapshot();
        return outcome.Ack;
    }

    private async Task<IpcAck> RemoveSubscriptionAsync(IReadOnlyList<string> args)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var ack = await Subscriptions().RemoveAsync(args, _active && _boundTarget is { Length: > 0 } ? [_boundTarget] : [], CancellationToken.None).ConfigureAwait(false);
        if (ack.Ok)
        {
            await RefreshTransportsAsync().ConfigureAwait(false);
            Save();
            PushSnapshot();
        }

        return ack;
    }

    private async Task<IpcAck> ConfigSubscriptionAsync(IReadOnlyList<string> args)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        return await Subscriptions().ConfigUrlAsync(args, CancellationToken.None).ConfigureAwait(false);
    }

    // Re-reads the subscriptions whose interval has run out. A subscription lives on the open internet, so this
    // runs whether or not the interface is up; a document of a couple of kilobytes needs no free link either.
    private async Task RefreshSubscriptionsAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(GeoStartDelaySeconds), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (_subscriptionAutoRefresh)
                {
                    await EnsureInitAsync().ConfigureAwait(false);
                    var service = Subscriptions();
                    var due = await service.DueAsync(_subscriptionIntervalHours, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                    foreach (var subscription in due)
                    {
                        var outcome = await service.RefreshAsync([subscription.Name], ct).ConfigureAwait(false);
                        FlagRewritten(outcome);
                        if (!outcome.Ack.Ok)
                        {
                            _log.Warn("sub", $"subscription {subscription.Name} could not be re-read");
                        }
                    }

                    if (due.Count > 0)
                    {
                        Save();
                        PushSnapshot();
                        await _offers.WarmUnaskedAsync(OfferTargets(), OfferChangedAsync, ct).ConfigureAwait(false);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(SubscriptionTickSeconds), ct).ConfigureAwait(false);
            }
        }
        catch (System.OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("sub", "the scheduled re-read of the subscriptions stopped", ex);
        }
    }

    // A rewritten text applies on a fresh interface; flag a reconnect when the running target is affected.
    // Переписанный подпиской текст встаёт сразу: туннель поднимается заново на нём же. Упавший от смены
    // адреса поднимается так же - подписка чинит именно его.
    private void FlagRewritten(SubscriptionOutcome outcome)
    {
        if (!_dialWanted)
        {
            return;
        }

        var dialled = _active ? _boundTarget : _selectedTarget;

        if (!outcome.Rewritten.Any(name => string.Equals(name, dialled, StringComparison.Ordinal)))
        {
            return;
        }

        // Выбор успели увести на другую конфигурацию - переподключение остаётся за пользователем.
        if (!string.Equals(dialled, _selectedTarget, StringComparison.Ordinal))
        {
            _restartRequired = true;
            return;
        }

        _log.Info("sub", "the subscription rewrote the configuration it dials; dialling it again");
        _ = RestartTunnelAsync();
    }

    // Подписка снесла работающую конфигурацию - держать туннель не на чем.
    private void DropRunningIfGone()
    {
        if (_active && _boundTarget is { Length: > 0 } bound && !_configs.ContainsKey(bound))
        {
            _log.Info("sub", "the subscription dropped the running configuration; disconnecting");
            _ = SetConnectionAsync("disconnect");
        }
    }

    // Движок читает конфигурацию на старте: новый текст встаёт только на поднятом заново туннеле.
    private async Task RestartTunnelAsync()
    {
        var generation = _connectPreparation.Apply<long?>(() => _dialWanted
            ? _connectPreparation.Disconnect(DisconnectTunnel).Generation
            : null);
        if (generation is null)
        {
            return;
        }

        for (var attempt = 0; attempt < RestartWaitTicks && _active; attempt++)
        {
            await Task.Delay(RestartTickMs).ConfigureAwait(false);
        }

        await QueueConnectAsync(generation).ConfigureAwait(false);
    }

    private SubscriptionService Subscriptions()
    {
        return new SubscriptionService(_geoHttp, _store, new MapLibrary(this));
    }

    // The configuration library as a subscription sees it: this head keeps its configurations in one map.
    private sealed class MapLibrary(AndroidAgentConnection agent) : ISubscriptionLibrary
    {
        public Task<IReadOnlyCollection<string>> NamesAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyCollection<string>>([.. agent._configs.Keys]);
        }

        public Task<string?> TextAsync(string name, CancellationToken ct)
        {
            return Task.FromResult(agent._configs.TryGetValue(name, out var text) ? text : null);
        }

        public Task AddAsync(string name, string confText, CancellationToken ct)
        {
            agent._configs[name] = confText;
            return Task.CompletedTask;
        }

        public Task EditAsync(string name, string confText, CancellationToken ct)
        {
            agent._configs[name] = confText;
            return Task.CompletedTask;
        }

        public Task DropAsync(string name, CancellationToken ct)
        {
            agent._configs.Remove(name);
            if (string.Equals(name, agent._selectedTarget, StringComparison.Ordinal))
            {
                agent._selectedTarget = null;
            }

            // Always-on raises the last session on its own: a config that is gone must not come back with it.
            if (VpnBridge.ReadRequest() is { } stored && string.Equals(name, stored.Name, StringComparison.Ordinal))
            {
                VpnBridge.ClearRequest();
            }

            return Task.CompletedTask;
        }

        public async Task RemoveAsync(string name, CancellationToken ct)
        {
            await DropAsync(name, ct).ConfigureAwait(false);
            await agent._store.RemoveConfigTransportAsync(name, ct).ConfigureAwait(false);
        }
    }

    private async Task<IpcAck> RemoveConfigAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        if (_active && string.Equals(args[0], _boundTarget, StringComparison.Ordinal))
        {
            return new IpcAck(false, $"config {args[0]} is running; disconnect first");
        }

        _configs.Remove(args[0]);
        if (string.Equals(args[0], _selectedTarget, StringComparison.Ordinal))
        {
            _selectedTarget = null;
        }

        // Always-on raises the last session on its own: a config that is gone must not come back with it.
        if (VpnBridge.ReadRequest() is { } stored && string.Equals(args[0], stored.Name, StringComparison.Ordinal))
        {
            VpnBridge.ClearRequest();
        }

        Save();
        await EnsureInitAsync().ConfigureAwait(false);
        await _store.RemoveConfigTransportAsync(args[0]).ConfigureAwait(false);
        await ConfigForget.CarryAsync(_store, args[0]).ConfigureAwait(false);
        await RefreshSubscriptionMembersAsync().ConfigureAwait(false);
        await RefreshTransportsAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
    }

    private void RetargetSelection(string oldName, string newName)
    {
        if (string.Equals(_selectedTarget, oldName, StringComparison.Ordinal))
        {
            _selectedTarget = newName;
        }

        if (string.Equals(_boundTarget, oldName, StringComparison.Ordinal))
        {
            _boundTarget = newName;
        }
    }

    // Whether the selected configuration decides connections on its own: the flag rides with the tunnel, not
    // with the rules, and a configuration that never said otherwise does.
    private bool RouterEnabled() =>
        _selectedTarget is not { Length: > 0 } name || _transports.GetValueOrDefault(name)?.UseRouter != false;

    // Reads a window of one log table for the in-app viewer, newest first.
    private async Task<IpcAck> ReadLogAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !IsKnownLogTable(args[0]))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var table = args[0];
        var limit = args.Count > 1 && int.TryParse(args[1], out var l) ? Math.Clamp(l, 1, 2000) : 400;
        var beforeId = args.Count > 2 && long.TryParse(args[2], out var b) && b > 0 ? (long?)b : null;
        var minLevelId = SqliteLogStore.IsLeveled(table) && args.Count > 3 ? AndroidAgentLog.MinId(args[3]) : null;
        var search = args.Count > 4 && args[4].Length > 0 ? args[4] : null;

        var page = await _log.QueryAsync(table, beforeId, limit, minLevelId, search).ConfigureAwait(false);
        var lines = page.Rows.Select(AndroidAgentLog.Render).ToList();
        var firstId = page.Rows.Count > 0 ? page.Rows[^1].Id : 0L;
        var matchCount = search is null ? 0 : await _log.CountAsync(table, minLevelId, search).ConfigureAwait(false);

        return new IpcAck(true, JsonSerializer.Serialize(new
        {
            lines,
            firstId,
            hasOlder = page.HasOlder,
            matchCount,
        }));
    }

    // Clears one log table.
    private async Task<IpcAck> ClearLogAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !IsKnownLogTable(args[0]))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        await _log.ClearAsync(args[0]).ConfigureAwait(false);
        return Ok();
    }

    // Renders a whole log table to text for the UI to save.
    private async Task<IpcAck> ExportLogAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !IsKnownLogTable(args[0]))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var text = await _log.RenderAllAsync(args[0]).ConfigureAwait(false);
        return new IpcAck(true, text);
    }

    // Sets a named agent setting; handles the log capture level and the routing-log switch.
    private IpcAck SetSetting(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        switch (args[0])
        {
            case "log-level":
                _logLevel = KnownLogLevel(args[1]);
                _log.SetCaptureLevel(_logLevel);
                Save();
                PushSnapshot();
                return Ok();
            case "route-log":
                _routeLog = IsOn(args[1]);
                _log.SetRouteLog(_routeLog);
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.DirectTcp:
                _directTcp = IsOn(args[1]);
                _restartRequired = true;
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.ExcludeRoutes:
                _excludeRoutes = IsOn(args[1]);
                _restartRequired = true;
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.LocalInTunnel:
                _localInTunnel = IsOn(args[1]);
                _restartRequired = true;
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.RouteTtl:
                if (!SettingKeys.TryParseRouteTtl(args[1], out var ttl))
                {
                    return Fail();
                }

                _routeTtl = ttl;
                VpnBridge.WriteRouteTtl(ttl);
                if (VpnBridge.IsRunning(Application.Context))
                {
                    VpnBridge.RequestRouteTtl(Application.Context);
                }

                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.SplitRouteLimit:
                if (!SettingKeys.TryParseSplitRouteLimit(args[1], out var splitThreshold))
                {
                    return Fail();
                }

                _splitLimit = splitThreshold;
                _restartRequired = true;
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.TunnelNotice:
                if (!TunnelNoticeModes.IsKnown(args[1]))
                {
                    return Fail();
                }

                _tunnelNotice = args[1];
                _downNotice = string.Empty;
                Save();
                _ = RetellRunningAsync();
                PushSnapshot();
                return Ok();
            case "allow-prerelease":
            case SettingKeys.UpdateChannel:
                var channel = args[0] == SettingKeys.UpdateChannel
                    ? UpdateChannels.Of(args[1], string.Empty)
                    : UpdateChannels.Of(IsOn(args[1]));
                if (channel.Length == 0)
                {
                    return Fail();
                }

                _updater.PickedChannel = channel;
                Save();
                RecheckUpdate();
                return Ok();
            case SettingKeys.UpdateSource:
                if (UpdateSources.Choice(args[1]) is not { } choice)
                {
                    return Fail();
                }

                _updater.Choice = choice;
                Save();
                RecheckUpdate();
                return Ok();
            case "geo-auto-check":
                _geoAutoCheck = IsOn(args[1]);
                Save();
                PushSnapshot();
                return Ok();
            case "geo-check-interval-hours":
                _geoCheckIntervalHours = GeoInterval(args[1]);
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.SubscriptionAutoRefresh:
                _subscriptionAutoRefresh = IsOn(args[1]);
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.SubscriptionRefreshInterval:
                _subscriptionIntervalHours = SubscriptionInterval(args[1]);
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.ProxyEnabled:
            case SettingKeys.ProxyAnonymous:
            case SettingKeys.ProxySocksPort:
            case SettingKeys.ProxyHttpPort:
            case SettingKeys.ProxyCredentials:
                if (!TryProxySetting(args[0], args[1], out var options))
                {
                    return Fail();
                }

                _proxyOptions = options;
                Save();
                PublishProxy();
                PushSnapshot();
                return Ok();
            case PeriodicReconnectKey:
            case ReconnectIntervalKey:
                return new IpcAck(false, IpcMessage.Key("Android_ReconnectFixed", args[0], ConnectRetry.CeilingSeconds));
            default:
                return Ok();
        }
    }

    // The single user/password pair became a list; a pair stored by an earlier version becomes its first account.
    private static LocalProxyOptions ReadProxy(JsonElement stored)
    {
        var options = JsonSerializer.Deserialize<LocalProxyOptions>(stored.GetRawText()) ?? new LocalProxyOptions();
        if (options.Credentials.Length > 0
            || !stored.TryGetProperty("User", out var user)
            || user.GetString() is not { Length: > 0 } name)
        {
            return options;
        }

        var password = stored.TryGetProperty("Password", out var secret) ? secret.GetString() ?? string.Empty : string.Empty;
        return options with { Credentials = $"{name}:{password}" };
    }

    // One proxy setting on top of the ones in force.
    private bool TryProxySetting(string key, string value, out LocalProxyOptions options)
    {
        options = _proxyOptions;
        switch (key)
        {
            case SettingKeys.ProxyEnabled:
                options = options with { Enabled = IsOn(value) };
                return true;
            case SettingKeys.ProxyAnonymous:
                options = options with { AllowAnonymous = IsOn(value) };
                return true;
            case SettingKeys.ProxySocksPort:
                if (!SettingKeys.TryParseProxyPort(value, out var socks))
                {
                    return false;
                }

                options = options with { SocksPort = socks };
                return true;
            case SettingKeys.ProxyHttpPort:
                if (!SettingKeys.TryParseProxyPort(value, out var http))
                {
                    return false;
                }

                options = options with { HttpPort = http };
                return true;
            default:
                options = options with { Credentials = value.Trim() };
                return true;
        }
    }

    // The tunnel process owns the listener: it takes the settings now if it runs, and at its next start if not.
    private void PublishProxy()
    {
        VpnBridge.WriteProxy(_proxyOptions);
        VpnBridge.RequestProxy(Application.Context);
    }

    // Records a UI diagnostic line in the agent log.
    private IpcAck LogClient(IReadOnlyList<string> args)
    {
        if (args.Count > 0 && args[0].Length > 0)
        {
            _log.Warn("ui", args[0]);
        }

        return Ok();
    }

    // Renders the configuration the tunnel runs on, or would run on at the next connect.
    private async Task<IpcAck> GetRuntimeConfigAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var configName = _selectedTarget;
        var report = new System.Text.StringBuilder();
        report.Append("config       : ").Append(configName ?? "(none)").Append('\n');
        report.Append("status       : ").Append(_boundStatus).Append('\n');
        report.Append("active       : ").Append(_active ? "yes" : "no").Append('\n');
        if (configName is not null && _configs.TryGetValue(configName, out var configText))
        {
            report.Append("endpoint     : ").Append(WgConfigEditor.GetEndpoint(configText) ?? "(none)").Append('\n');
        }

        if (Volatile.Read(ref _appliedRequest) is { } applied)
        {
            report.Append("applied routing : ").Append(applied.PlanStamp ?? "(legacy)").Append('\n');
            report.Append("applied lists   : ").AppendJoin(", ", applied.Plan?.ListIds ?? []).Append('\n');
            if (applied.Plan is { SelectionPlan: true } plan)
            {
                report.Append("requested all UDP : ").Append(plan.RequestedAllUdp ? "on" : "off").Append('\n');
            }
        }

        report.Append("selected lists  : ").AppendJoin(", ", Volatile.Read(ref _routingSelection) ?? []).Append('\n');
        report.Append("log level    : ").Append(_logLevel).Append('\n');
        report.Append("direct tcp   : ").Append(_directTcp ? "on" : "off").Append('\n');
        report.Append("exclude routes: ").Append(_excludeRoutes ? "on" : "off").Append('\n');
        report.Append("local in tun : ").Append(_localInTunnel ? "on" : "off").Append('\n');
        report.Append("tunnel notice: ").Append(_tunnelNotice).Append('\n');
        report.Append("route log    : ").Append(_routeLog ? "on" : "off").Append('\n');
        return new IpcAck(true, report.ToString());
    }

    private static bool IsKnownLogTable(string name) => name is SqliteLogStore.AgentTable or SqliteLogStore.DnsTable
        or SqliteLogStore.RoutesTable or SqliteLogStore.ChecksTable or SqliteLogStore.ProbeTable;

    private static string KnownLogLevel(string token)
    {
        return token switch
        {
            "none" or "trace" or "debug" or "info" or "warning" or "error" => token,
            _ => "info",
        };
    }

    private static IpcAck Ok() => new(true, string.Empty);

    private static IpcAck Fail() => new(false, string.Empty);
}
