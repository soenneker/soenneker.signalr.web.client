using System;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client.Tests;

// Models a host whose clock advances while timer callbacks cannot execute.
internal sealed class SuspendedTimeProvider : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.Ticks;
    private SuspendedTimer? _timer;
    public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    public void Tick() => _timer?.Tick();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _timer = new SuspendedTimer(callback, state);
        TimerCreated.TrySetResult();
        return _timer;
    }

    private sealed class SuspendedTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _disposed;
        public void Tick() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
