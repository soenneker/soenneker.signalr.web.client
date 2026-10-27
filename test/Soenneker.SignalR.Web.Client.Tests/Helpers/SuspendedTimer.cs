using System;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client.Tests;

internal sealed class SuspendedTimer(TimerCallback callback, object? state) : ITimer
{
    private int _disposed;
    public void Tick() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
    public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
