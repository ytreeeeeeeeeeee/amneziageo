using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Widget;
using AmneziaGeo.Android.Ui.Services;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// Keeps the tile request in the foreground while the agent prepares the VPN service.
/// </summary>
[Activity(Exported = false, ExcludeFromRecents = true, LaunchMode = LaunchMode.SingleTop,
    TaskAffinity = "org.amneziageo.android.quicksettings", Theme = "@android:style/Theme.Translucent.NoTitleBar")]
public sealed class VpnTileActivity : Activity
{
    internal const string ConnectExtra = "connect";
    private const int ConsentRequest = 1;
    private const string Tag = "AmneziaGeoTile";
    private bool? _pendingConnect;
    private bool _resumed;
    private bool _handling;
    private bool _awaitingConsent;
    private bool _sendingConnect;
    private string _language = string.Empty;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Enqueue(Intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _resumed = true;
        HandlePending();
    }

    protected override void OnPause()
    {
        _resumed = false;
        base.OnPause();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        Enqueue(intent);
        HandlePending();
    }

    private void Enqueue(Intent? intent)
    {
        if (intent?.HasExtra(ConnectExtra) != true)
        {
            return;
        }

        var connect = intent.GetBooleanExtra(ConnectExtra, false);
        if (!connect && _sendingConnect && AndroidAgentConnection.Current is { } agent)
        {
            _pendingConnect = null;
            _ = SendConnectionAsync(agent, false);
            return;
        }

        // A button press carries the desired state. An off request takes back earlier queued starts.
        _pendingConnect = connect;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != ConsentRequest)
        {
            return;
        }

        _awaitingConsent = false;
        if (resultCode != Result.Ok && _pendingConnect == true)
        {
            _pendingConnect = null;
        }

        HandlePending();
    }

    private void HandlePending()
    {
        if (_handling || _awaitingConsent || IsFinishing || IsDestroyed)
        {
            return;
        }

        if (_pendingConnect is not { } connect)
        {
            Finish();
            return;
        }

        if (_resumed)
        {
            _pendingConnect = null;
            _handling = true;
            _ = ToggleAsync(connect);
        }
    }

    private async Task ToggleAsync(bool connect)
    {
        try
        {
            var prefs = UiPreferences.Load();
            _language = prefs.Language;
            if (!LegalTexts.Accepted(prefs))
            {
                OpenApp();
                return;
            }

            var agent = AndroidAgentConnection.Current ?? new AndroidAgentConnection();
            agent.Start();
            await agent.ReadyAsync();
            if (IsFinishing || IsDestroyed || agent.Latest is not { } snapshot)
            {
                return;
            }

            if (!_resumed)
            {
                _pendingConnect ??= connect;
                return;
            }

            connect = _pendingConnect ?? connect;
            _pendingConnect = null;
            if (connect && snapshot.Active)
            {
                return;
            }

            if (connect && string.IsNullOrEmpty(snapshot.SelectedTarget))
            {
                OpenApp();
                return;
            }

            if (connect && VpnService.Prepare(this) is { } consent)
            {
                using (consent)
                {
                    StartActivityForResult(consent, ConsentRequest);
                    _awaitingConsent = true;
                    _pendingConnect = true;
                }

                return;
            }

            _sendingConnect = connect;
            await SendConnectionAsync(agent, connect);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
        finally
        {
            _handling = false;
            _sendingConnect = false;
            HandlePending();
        }
    }

    private async Task SendConnectionAsync(AndroidAgentConnection agent, bool connect)
    {
        try
        {
            var ack = await agent.SendCommandAsync(
                new IpcCommand(IpcContract.OpSetConnection, [connect ? "connect" : "disconnect"]));
            if (!ack.Ok && ack != VpnConnectPreparation.Cancelled)
            {
                ShowFailure();
            }
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private void OpenApp()
    {
        using var intent = new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask);
        StartActivity(intent);
    }

    private void ReportFailure(Exception error)
    {
        global::Android.Util.Log.Error(Tag, error.ToString());
        ShowFailure();
    }

    private void ShowFailure() =>
        Toast.MakeText(this, Loc.GetIn(_language, "Status_Failed"), ToastLength.Short)?.Show();
}
