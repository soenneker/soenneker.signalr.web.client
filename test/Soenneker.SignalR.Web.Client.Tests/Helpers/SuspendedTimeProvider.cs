using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client.Tests;

// Models a host whose clock advances while timer callbacks cannot execute.
internal sealed class SuspendedTimeProvider : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.Ticks;
    private readonly ConcurrentBag<SuspendedTimer> _timers = new();
    public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    public void Tick() { foreach (SuspendedTimer timer in _timers) timer.Tick(); }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new SuspendedTimer(callback, state);
        _timers.Add(timer);
        if (period != Timeout.InfiniteTimeSpan) TimerCreated.TrySetResult();
        return timer;
    }

}
