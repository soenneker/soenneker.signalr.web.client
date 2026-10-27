using System;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Asyncs.Semaphores;

namespace Soenneker.SignalR.Web.Client;

internal sealed class CallbackTracker(int capacity, Action<Exception> reportFailure)
{
    private readonly AsyncSemaphore _slots = new(capacity);
    private int _outstanding;
    private int _abandoned;
    public int Outstanding => Volatile.Read(ref _outstanding);
    public int Abandoned => Volatile.Read(ref _abandoned);
    public bool Saturated => Outstanding >= capacity && Abandoned > 0;

    public async Task<T> Invoke<T>(Func<Task<T>> callback, CancellationToken token)
    {
        // The permit belongs to the application task, not the caller's cancellable wait.
        SemaphoreLease lease = await _slots.Acquire(token).ConfigureAwait(false);
        Interlocked.Increment(ref _outstanding);
        Task<T> task = Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            return await callback().ConfigureAwait(false);
        });
        bool abandoned = false;
        try { return await task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            abandoned = true;
            Interlocked.Increment(ref _abandoned);
            SignalRClientMetrics.CallbackAbandoned();
            _ = Finish(task, lease);
            throw;
        }
        finally
        {
            if (!abandoned)
            {
                Interlocked.Decrement(ref _outstanding);
                lease.Dispose();
            }
        }
    }

    private async Task Finish(Task task, SemaphoreLease lease)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { reportFailure(ex); }
        finally
        {
            Interlocked.Decrement(ref _abandoned);
            Interlocked.Decrement(ref _outstanding);
            lease.Dispose();
        }
    }
}
