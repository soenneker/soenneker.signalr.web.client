using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace Soenneker.SignalR.Web.Client.Tests;

public class SignalRResilienceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async Task Ensure_waits_across_exhausted_cycles_until_connected_and_restored()
    {
        using var server = new HubServer { Available = false };
        var entered = Signal();
        var release = Signal();
        await using var client = server.Client(options =>
        {
            options.MaxRetryAttempts = 0;
            options.ConnectionRestored = async _ => { entered.TrySetResult(); await release.Task; };
        });
        Task ready = client.EnsureConnection().AsTask();
        await client.StartConnection().AsTask().WaitAsync(Deadline);
        await Assert.That(ready.IsCompleted).IsFalse();
        server.Available = true;
        await entered.Task.WaitAsync(Deadline);
        await Assert.That(ready.IsCompleted).IsFalse();
        release.TrySetResult();
        await ready.WaitAsync(Deadline);
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
    }

    [Test]
    public async Task Ensure_reports_finite_exhaustion_with_the_original_failure()
    {
        using var server = new HubServer { Available = false };
        Exception? reported = null;
        await using var client = server.Client(options =>
        {
            options.ReconnectIndefinitely = false;
            options.MaxRetryAttempts = 0;
            options.ConnectionError = error => reported = error;
        });
        try
        {
            await client.EnsureConnection().AsTask().WaitAsync(Deadline);
            throw new Exception("Readiness incorrectly succeeded");
        }
        catch (InvalidOperationException error)
        {
            await Assert.That(error.InnerException == reported && reported != null).IsTrue();
        }
    }

    [Test]
    public async Task Ensure_waiter_cancellation_is_local_but_stop_cancels_remaining_waiters()
    {
        using var server = new HubServer { BlockNegotiation = true };
        await using var client = server.Client();
        using var cancellation = new CancellationTokenSource();
        Task first = client.EnsureConnection(cancellation.Token).AsTask();
        Task second = client.EnsureConnection().AsTask();
        await server.BlockedNegotiation.Task.WaitAsync(Deadline);
        cancellation.Cancel();
        await Assert.That(async () => await first).Throws<OperationCanceledException>();
        await Assert.That(second.IsCompleted).IsFalse();
        await client.StopConnection().WaitAsync(Deadline);
        await Assert.That(async () => await second).Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Invalid_retry_provider_falls_back_and_recovers(bool invalidDelay)
    {
        using var server = new HubServer { Available = false };
        await using var client = server.Client(options => options.RetryDelayProvider = _ =>
        {
            server.Available = true;
            if (invalidDelay) return TimeSpan.MaxValue;
            throw new InvalidOperationException("Custom retry schedule failed");
        });
        await client.EnsureConnection().AsTask().WaitAsync(Deadline);
        await Assert.That(server.Negotiations).IsEqualTo(2);
    }

    [Test]
    public async Task Throwing_retry_provider_also_recovers_during_automatic_reconnect()
    {
        using var server = new HubServer();
        var restored = Signal();
        var calls = 0;
        await using var client = server.Client(options =>
        {
            options.RetryDelayProvider = _ => throw new InvalidOperationException("Custom retry schedule failed");
            options.ConnectionError = _ => throw new InvalidOperationException("Error observer failed");
            options.ConnectionRestored = _ => { if (Interlocked.Increment(ref calls) == 2) restored.TrySetResult(); return Task.CompletedTask; };
        });
        await client.EnsureConnection().AsTask().WaitAsync(Deadline);
        server.FailCurrentPoll.TrySetResult();
        await restored.Task.WaitAsync(Deadline);
        await client.EnsureConnection().AsTask().WaitAsync(Deadline);
    }

    [Test]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.Forbidden)]
    public async Task Authentication_failures_wait_for_credentials_and_resume_retries_promptly(HttpStatusCode status)
    {
        using var server = new HubServer { Available = false, FailureStatusCode = status };
        var failed = Signal();
        await using var client = server.Client(options =>
        {
            options.MaxRetryAttempts = 0;
            options.AuthenticationRetryDelay = TimeSpan.FromMinutes(1);
            options.RetriesExhausted = () => failed.TrySetResult();
        });
        Task ready = client.EnsureConnection().AsTask();
        await failed.Task.WaitAsync(Deadline);
        await Assert.That(server.Negotiations).IsEqualTo(1);
        await Assert.That(ready.IsCompleted).IsFalse();
        server.Available = true;
        await client.ResumeConnection().AsTask().WaitAsync(Deadline);
        await ready.WaitAsync(Deadline);
        await Assert.That(server.Negotiations).IsEqualTo(2);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Automatic_reconnect_deadline_interrupts_stalled_transport(bool indefinite)
    {
        using var server = new HubServer();
        await using var client = server.Client(options =>
        {
            options.ReconnectIndefinitely = indefinite;
            options.AutomaticReconnectTimeout = TimeSpan.FromMilliseconds(300);
        });
        await client.EnsureConnection().AsTask().WaitAsync(Deadline);
        server.BlockNegotiation = true;
        server.FailCurrentPoll.TrySetResult();
        await server.BlockedNegotiation.Task.WaitAsync(Deadline);
        Task ready = client.EnsureConnection().AsTask();
        // The in-flight request remains blocked. Only a new request can now succeed.
        server.BlockNegotiation = false;
        if (indefinite)
        {
            await ready.WaitAsync(Deadline);
            await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
            await Assert.That(server.Negotiations).IsEqualTo(3);
        }
        else
        {
            await Assert.That(async () => await ready.WaitAsync(Deadline)).Throws<InvalidOperationException>();
            await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Disconnected);
        }
    }

    [Test]
    public async Task Resume_during_initial_connection_preserves_readiness_waiters()
    {
        using var server = new HubServer { BlockNegotiation = true };
        await using var client = server.Client();
        Task ready = client.EnsureConnection().AsTask();
        await server.BlockedNegotiation.Task.WaitAsync(Deadline);
        server.BlockNegotiation = false;
        await client.ResumeConnection().AsTask().WaitAsync(Deadline);
        await ready.WaitAsync(Deadline);
        await Assert.That(server.Negotiations).IsEqualTo(2);
    }

    [Test]
    public async Task Restoration_can_join_its_own_resume_without_deadlocking()
    {
        using var server = new HubServer();
        SignalRWebClient? client = null;
        var calls = 0;
        client = server.Client(options => options.ConnectionRestored = async _ =>
        {
            if (Interlocked.Increment(ref calls) == 2) await client!.ResumeConnection();
        });
        await using (client)
        {
            await client.EnsureConnection().AsTask().WaitAsync(Deadline);
            await client.ResumeConnection().AsTask().WaitAsync(Deadline);
            await Assert.That(calls).IsEqualTo(2);
        }
    }

    [Test]
    public async Task Restoration_cannot_await_its_own_readiness()
    {
        using var server = new HubServer();
        SignalRWebClient? client = null;
        client = server.Client(options => options.ConnectionRestored = async _ =>
            await Assert.That(async () => await client!.EnsureConnection()).Throws<InvalidOperationException>());
        await using (client) await client.EnsureConnection().AsTask().WaitAsync(Deadline);
    }

    [Test]
    public async Task Suspended_host_recovers_automatically_and_preserves_intentional_stop()
    {
        using var server = new HubServer();
        var clock = new SuspendedTimeProvider();
        var restored = Signal();
        var calls = 0;
        await using var client = server.Client(options =>
        {
            options.TimeProvider = clock;
            options.ConnectionRestored = _ => { if (Interlocked.Increment(ref calls) == 2) restored.TrySetResult(); return Task.CompletedTask; };
        });
        await client.EnsureConnection().AsTask().WaitAsync(Deadline);
        await clock.TimerCreated.Task.WaitAsync(Deadline);
        clock.Advance(TimeSpan.FromMinutes(10));
        clock.Tick();
        await restored.Task.WaitAsync(Deadline);
        await client.EnsureConnection().AsTask().WaitAsync(Deadline);
        await Assert.That(server.Negotiations).IsEqualTo(2);
        await client.StopConnection().WaitAsync(Deadline);
        clock.Advance(TimeSpan.FromMinutes(10));
        clock.Tick();
        await client.ResumeConnection();
        await Assert.That(server.Negotiations).IsEqualTo(2);
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Disconnected);
    }

    [Test]
    public async Task Detached_work_can_ensure_readiness_after_its_parent_restoration_finishes()
    {
        using var server = new HubServer();
        var release = Signal();
        Task? child = null;
        SignalRWebClient? client = null;
        client = server.Client(options => options.ConnectionRestored = _ =>
        {
            child = Task.Run(async () => { await release.Task; await client!.EnsureConnection(); });
            return Task.CompletedTask;
        });
        await using (client)
        {
            await client.EnsureConnection().AsTask().WaitAsync(Deadline);
            release.TrySetResult();
            await child!.WaitAsync(Deadline);
        }
    }

    [Test]
    public async Task Repeated_resume_cycles_do_not_deliver_old_events_into_new_sessions()
    {
        using var server = new HubServer();
        var restores = 0;
        await using var client = server.Client(options => options.ConnectionRestored = _ =>
        {
            Interlocked.Increment(ref restores);
            return Task.CompletedTask;
        });
        await client.EnsureConnection().AsTask().WaitAsync(Deadline);
        for (int cycle = 0; cycle < 25; cycle++)
        {
            await client.ResumeConnection().AsTask().WaitAsync(Deadline);
            await client.EnsureConnection().AsTask().WaitAsync(Deadline);
        }
        await Assert.That(restores).IsEqualTo(26);
        await Assert.That(server.Negotiations).IsEqualTo(26);
    }
}
