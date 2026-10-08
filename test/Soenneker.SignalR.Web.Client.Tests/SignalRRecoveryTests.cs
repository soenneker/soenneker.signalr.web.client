using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace Soenneker.SignalR.Web.Client.Tests;

public class SignalRRecoveryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async ValueTask Concurrent_starts_share_one_attempt_and_one_restoration(CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        server.BlockNegotiation = true;
        var restored = 0;
        await using var client = server.Client(options => options.ConnectionRestored = _ =>
        {
            Interlocked.Increment(ref restored);
            return Task.CompletedTask;
        });
        Task[] starts = Enumerable.Range(0, 12).Select(_ => client.StartConnection(cancellationToken: cancellationToken).AsTask()).ToArray();
        await server.Negotiating.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        server.ReleaseNegotiation.TrySetResult();
        await Task.WhenAll(starts).WaitAsync(Deadline, cancellationToken: cancellationToken);
        await client.StartConnection(cancellationToken: cancellationToken);
        await Assert.That(server.Negotiations).IsEqualTo(1);
        await Assert.That(restored).IsEqualTo(1);
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
    }

    [Test]
    public async ValueTask Cancelling_one_waiter_does_not_cancel_shared_connection(CancellationToken cancellationToken)
    {
        using var server = new HubServer { BlockNegotiation = true };
        await using var client = server.Client();
        using var cancellation = new CancellationTokenSource();
        Task cancelled = client.StartConnection(cancellation.Token).AsTask();
        Task other = client.StartConnection(cancellationToken: cancellationToken).AsTask();
        await server.Negotiating.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        cancellation.Cancel();
        await Assert.That(async () => await cancelled).Throws<OperationCanceledException>();
        server.ReleaseNegotiation.TrySetResult();
        await other.WaitAsync(Deadline, cancellationToken: cancellationToken);
        await Assert.That(server.Negotiations).IsEqualTo(1);
    }

    [Test]
    public async ValueTask Stop_cancels_initial_negotiation_and_allows_restart(CancellationToken cancellationToken)
    {
        using var server = new HubServer { BlockNegotiation = true };
        await using var client = server.Client();
        Task start = client.StartConnection(cancellationToken: cancellationToken).AsTask();
        await server.Negotiating.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        await client.StopConnection(cancellationToken: cancellationToken).WaitAsync(Deadline, cancellationToken: cancellationToken);
        await Assert.That(async () => await start).Throws<OperationCanceledException>();
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Disconnected);
        server.ReleaseNegotiation.TrySetResult();
        await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
    }

    [Test]
    public async ValueTask Concurrent_disposals_cancel_initial_negotiation(CancellationToken cancellationToken)
    {
        using var server = new HubServer { BlockNegotiation = true };
        var client = server.Client();
        Task start = client.StartConnection(cancellationToken: cancellationToken).AsTask();
        await server.Negotiating.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask()).WaitAsync(Deadline, cancellationToken: cancellationToken);
        await client.StopConnection(cancellationToken: cancellationToken).WaitAsync(Deadline, cancellationToken: cancellationToken);
        await client.DisposeAsync().AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
        await Assert.That(async () => await start).Throws<OperationCanceledException>();
        await Assert.That(async () => await client.StartConnection(cancellationToken: cancellationToken)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async ValueTask Failed_restoration_retries_without_reconnecting_transport(CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        var callbacks = 0;
        await using var client = server.Client(options => options.ConnectionRestored = _ =>
        {
            if (Interlocked.Increment(ref callbacks) < 3) throw new InvalidOperationException("Temporary synchronization failure");
            return Task.CompletedTask;
        });
        await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
        await Assert.That(callbacks).IsEqualTo(3);
        await Assert.That(server.Negotiations).IsEqualTo(1);
    }

    [Test]
    public async ValueTask Restoration_retries_continue_after_exhausted_cycle(CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        var callbacks = 0;
        var exhausted = 0;
        var restored = Signal();
        await using var client = server.Client(options =>
        {
            options.MaxRetryAttempts = 0;
            options.RetriesExhausted = () => Interlocked.Increment(ref exhausted);
            options.ConnectionRestored = _ =>
            {
                if (Interlocked.Increment(ref callbacks) < 3) throw new InvalidOperationException("Try again");
                restored.TrySetResult();
                return Task.CompletedTask;
            };
        });
        await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
        await restored.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        await Assert.That(exhausted).IsEqualTo(2);
        await Assert.That(server.Negotiations).IsEqualTo(1);
    }

    [Test]
    public async ValueTask Finite_restoration_honors_retry_budget(CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        var callbacks = 0;
        await using var client = server.Client(options =>
        {
            options.ReconnectIndefinitely = false;
            options.MaxRetryAttempts = 2;
            options.ConnectionRestored = _ => { Interlocked.Increment(ref callbacks); throw new InvalidOperationException("Failed"); };
        });
        await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
        await Assert.That(callbacks).IsEqualTo(3);
        await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
        await Assert.That(callbacks).IsEqualTo(6);
        await Assert.That(server.Negotiations).IsEqualTo(1);
    }

    [Test]
    public async ValueTask Disconnect_during_restoration_cancels_stale_work_and_recovers_repeatedly(CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        var callbacks = 0;
        var firstCallback = Signal();
        var firstCancelled = Signal();
        var secondRestored = Signal();
        var thirdRestored = Signal();
        await using var client = server.Client(options => options.ConnectionRestored = async context =>
        {
            int call = Interlocked.Increment(ref callbacks);
            if (call == 1)
            {
                firstCallback.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
                catch (OperationCanceledException) { firstCancelled.TrySetResult(); throw; }
            }
            else
            {
                if (!context.IsReconnect) throw new InvalidOperationException("Expected reconnect");
                if (call == 2) secondRestored.TrySetResult();
                else thirdRestored.TrySetResult();
            }
        });
        Task start = client.StartConnection(cancellationToken: cancellationToken).AsTask();
        await firstCallback.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        await server.AbortConnection(client.Connection.ConnectionId!);
        await firstCancelled.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        await secondRestored.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        await start.WaitAsync(Deadline, cancellationToken: cancellationToken);
        await server.AbortConnection(client.Connection.ConnectionId!);
        await thirdRestored.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
    }

    [Test]
    public async ValueTask Stop_does_not_wait_forever_for_legacy_restoration_callback(CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        var entered = Signal();
        var release = Signal();
        await using var client = server.Client(options => options.ConnectionRestored = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        Task start = client.StartConnection(cancellationToken: cancellationToken).AsTask();
        try
        {
            await entered.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
            await client.StopConnection(cancellationToken: cancellationToken).WaitAsync(Deadline, cancellationToken: cancellationToken);
            await Assert.That(async () => await start).Throws<OperationCanceledException>();
            await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Disconnected);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async ValueTask Restoration_callback_can_ensure_connection_without_deadlocking(CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        SignalRWebClient? client = null;
        client = server.Client(options => options.ConnectionRestored = async _ => await client!.StartConnection(cancellationToken: cancellationToken));
        await using (client)
            await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
    }

    [Test]
    public async ValueTask Start_during_stop_waits_for_shutdown_before_restarting(CancellationToken cancellationToken)
    {
        using var server = new HubServer { BlockNegotiation = true };
        await using var client = server.Client();
        Task original = client.StartConnection(cancellationToken: cancellationToken).AsTask();
        await server.Negotiating.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        Task stop = client.StopConnection(cancellationToken: cancellationToken);
        Task restart = client.StartConnection(cancellationToken: cancellationToken).AsTask();
        server.ReleaseNegotiation.TrySetResult();
        await Task.WhenAll(stop, restart).WaitAsync(Deadline, cancellationToken: cancellationToken);
        try { await original; } catch (OperationCanceledException) { }
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
    }

    [Test]
    public async ValueTask Initial_failure_continues_recovery_without_another_start(CancellationToken cancellationToken)
    {
        using var server = new HubServer { Available = false };
        var exhausted = Signal();
        var restored = Signal();
        await using var client = server.Client(options =>
        {
            options.MaxRetryAttempts = 0;
            options.RetriesExhausted = () => exhausted.TrySetResult();
            options.ConnectionRestored = _ => { restored.TrySetResult(); return Task.CompletedTask; };
        });
        await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
        await exhausted.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        server.Available = true;
        await restored.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
    }

    [Test]
    public async ValueTask Start_during_automatic_reconnect_waits_without_spending_manual_retry_budget(CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        var reconnecting = Signal();
        var reconnected = Signal();
        var exhausted = 0;
        var restored = 0;
        await using var client = server.Client(options =>
        {
            options.MaxRetryAttempts = 1;
            options.RetryDelayProvider = _ => TimeSpan.FromMilliseconds(1200);
            options.ConnectionReconnecting = _ => reconnecting.TrySetResult();
            options.ConnectionReconnected = _ => reconnected.TrySetResult();
            options.RetriesExhausted = () => Interlocked.Increment(ref exhausted);
            options.ConnectionRestored = _ => { Interlocked.Increment(ref restored); return Task.CompletedTask; };
        });
        await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
        server.FailCurrentPoll.TrySetResult();
        await reconnecting.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
        await reconnected.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        await Assert.That(exhausted).IsEqualTo(0);
        await Assert.That(restored).IsEqualTo(2);
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
    }

    [Test]
    public async ValueTask Restoration_callback_can_stop_the_client(CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        var stopped = Signal();
        SignalRWebClient? client = null;
        client = server.Client(options => options.ConnectionRestored = async _ =>
        {
            await client!.StopConnection(cancellationToken: cancellationToken);
            stopped.TrySetResult();
        });
        await using (client)
        {
            Task start = client.StartConnection(cancellationToken: cancellationToken).AsTask();
            await stopped.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
            await Assert.That(async () => await start).Throws<OperationCanceledException>();
            await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Disconnected);
        }
    }

    [Test]
    public async ValueTask Stop_interrupts_retry_backoff(CancellationToken cancellationToken)
    {
        using var server = new HubServer { Available = false };
        var delaying = Signal();
        await using var client = server.Client(options => options.RetryDelayProvider = _ =>
        {
            delaying.TrySetResult();
            return TimeSpan.FromMinutes(1);
        });
        Task start = client.StartConnection(cancellationToken: cancellationToken).AsTask();
        await delaying.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
        await client.StopConnection(cancellationToken: cancellationToken).WaitAsync(Deadline, cancellationToken: cancellationToken);
        await Assert.That(async () => await start).Throws<OperationCanceledException>();
        await Assert.That(server.Negotiations).IsEqualTo(1);
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Disconnected);
    }
}
