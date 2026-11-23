using AmneziaGeo.Ipc;

namespace AmneziaGeo.Android.Ui.Services;

// Serializes preparation of the shared routing and switch files. Disconnect invalidates preparations
// immediately, and the last check and the service launch run under the same lock as disconnect.
internal sealed class VpnConnectPreparation
{
    public static readonly IpcAck Cancelled = new(false, "connection cancelled");
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private CancellationTokenSource? _pending;
    private long _generation;

    public bool IsPending => Apply(() => _pending is { IsCancellationRequested: false });

    public async Task<IpcAck> RunAsync(Func<CancellationToken, Task<IpcAck>> connect, Func<Exception, IpcAck> failed,
        long? afterDisconnect = null)
    {
        var generation = afterDisconnect ?? Apply(() => _generation);
        await _serial.WaitAsync().ConfigureAwait(false);
        using var attempt = new CancellationTokenSource();
        try
        {
            lock (_gate)
            {
                if (_generation != generation)
                {
                    return Cancelled;
                }

                _pending = attempt;
            }

            return await connect(attempt.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Apply(() => attempt.IsCancellationRequested ? Cancelled : failed(ex));
        }
        finally
        {
            lock (_gate)
            {
                _pending = null;
            }

            _serial.Release();
        }
    }

    public T Apply<T>(Func<T> change, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            return change();
        }
    }

    public void Apply(Action change)
    {
        lock (_gate)
        {
            change();
        }
    }

    public void Invalidate() => Apply(() => InvalidateLocked());

    public (IpcAck Ack, long Generation) Disconnect(Func<IpcAck> disconnect) => Apply(() =>
    {
        var generation = InvalidateLocked();
        return (disconnect(), generation);
    });

    private long InvalidateLocked()
    {
        var generation = ++_generation;
        _pending?.Cancel();
        return generation;
    }
}
