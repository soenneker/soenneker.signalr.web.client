using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client;

internal sealed class RecoverySignal(TimeProvider timeProvider)
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, AllowSynchronousContinuations = false
    });
    public void Set() => _channel.Writer.TryWrite(true);
    public async Task WaitAsync(CancellationToken token) => await _channel.Reader.ReadAsync(token).ConfigureAwait(false);
    public async Task WaitAsync(TimeSpan delay, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(delay, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { await _channel.Reader.ReadAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested) { }
    }
    public void Dispose() => _channel.Writer.TryComplete();
}
