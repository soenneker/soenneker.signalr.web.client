using System.Threading;
using System.Threading.Tasks;
using System;

namespace Soenneker.SignalR.Web.Client;

internal sealed class Recovery(TimeProvider timeProvider)
{
    public readonly CancellationTokenSource Cancellation = new();
    public readonly RecoverySignal Changed = new(timeProvider);
    public TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Worker = Task.CompletedTask;
    public CancellationTokenSource? Restoration;
    public long Revision;
    public long RestoredRevision = -1;
    public bool IsReconnect;
    public bool AutomaticReconnectPending;
    public long AutomaticReconnectStarted;
    public Exception? LastError;
    public bool SuppressConnectionEvents;
    public string? RestoredConnectionId;
    public bool Exhausted;
}
