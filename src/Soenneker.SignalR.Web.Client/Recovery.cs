using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client;

internal sealed class Recovery
{
    public readonly CancellationTokenSource Cancellation = new();
    public readonly SemaphoreSlim Changed = new(0, 1);
    public TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Worker = Task.CompletedTask;
    public CancellationTokenSource? Restoration;
    public long Revision;
    public long RestoredRevision = -1;
    public bool IsReconnect;
    public bool AutomaticReconnectPending;
}
