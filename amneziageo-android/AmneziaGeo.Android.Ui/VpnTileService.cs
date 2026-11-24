using Android.App;
using Android.Content;
using Android.OS;
using Android.Service.QuickSettings;
using AmneziaGeo.Android.Engine;
using AmneziaGeo.Android.Ui.Services;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// Shows the AmneziaGeo tunnel in Android Quick Settings.
/// </summary>
[Service(Name = "org.amneziageo.android.VpnTileService", Label = "AmneziaGeo",
    Icon = "@drawable/ic_stat_tunnel", Exported = true, Permission = "android.permission.BIND_QUICK_SETTINGS_TILE")]
[IntentFilter(new[] { ActionQsTile })]
[MetaData("android.service.quicksettings.TOGGLEABLE_TILE", Value = "true")]
public sealed class VpnTileService : TileService
{
    private readonly Handler _main = new(Looper.MainLooper!);
    private VpnBridge.Listener? _listener;

    public override void OnStartListening()
    {
        base.OnStartListening();
        _listener = new VpnBridge.Listener { Handler = OnTunnelEvent };
        VpnBridge.Listen(this, _listener, VpnBridge.ActionEvent);
        AndroidAgentConnection.ConnectionActivityChanged += OnConnectionActivityChanged;
        RefreshTile();
    }

    public override void OnStopListening()
    {
        AndroidAgentConnection.ConnectionActivityChanged -= OnConnectionActivityChanged;
        if (_listener is not null)
        {
            UnregisterReceiver(_listener);
            _listener.Dispose();
            _listener = null;
        }

        base.OnStopListening();
    }

    public override void OnClick()
    {
        var active = QsTile is { } tile ? tile.State == TileState.Active : IsActive();
        if (IsLocked)
        {
            UnlockAndRun(new Java.Lang.Runnable(() => LaunchToggle(!active)));
            return;
        }

        LaunchToggle(!active);
    }

    private void LaunchToggle(bool connect)
    {
        using var intent = new Intent(this, typeof(VpnTileActivity)).AddFlags(ActivityFlags.NewTask);
        intent.PutExtra(VpnTileActivity.ConnectExtra, connect);
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            using var pending = PendingIntent.GetActivity(this, connect ? 1 : 0, intent, PendingIntentFlags.Immutable);
            if (pending is not null)
            {
                StartActivityAndCollapse(pending);
            }
        }
        else
        {
            StartActivityAndCollapse(intent);
        }
    }

    private void OnTunnelEvent(Intent intent)
    {
        if (intent.HasExtra(VpnBridge.ExtraStage))
        {
            RefreshTile();
        }
    }

    private void OnConnectionActivityChanged() => _main.Post(RefreshTile);

    private bool IsActive() => AndroidAgentConnection.Current?.Latest?.Active
        ?? (VpnBridge.IsRunning(this) && VpnBridge.ReadStage()?.Stage is VpnStage.Connecting or VpnStage.Connected);

    private void RefreshTile()
    {
        if (_listener is not null && QsTile is { } tile)
        {
            var state = IsActive() ? TileState.Active : TileState.Inactive;
            if (tile.State != state)
            {
                tile.State = state;
                tile.UpdateTile();
            }
        }
    }
}
