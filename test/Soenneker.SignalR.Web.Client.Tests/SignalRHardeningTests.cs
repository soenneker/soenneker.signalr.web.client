using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Soenneker.SignalR.Web.Client.Options;

namespace Soenneker.SignalR.Web.Client.Tests;

public class SignalRHardeningTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async Task Stalled_negotiation_times_out_and_retries()
    {
        using var server = new HubServer { BlockNegotiation = true };
        var retry = Signal();
        await using var client = server.Client(options =>
        {
            options.ConnectionAttemptTimeout = TimeSpan.FromMilliseconds(100);
            options.RetryDelayProvider = _ => { retry.TrySetResult(); return TimeSpan.Zero; };
        });
        Task start = client.StartConnection().AsTask();
        await retry.Task.WaitAsync(Deadline);
        server.ReleaseNegotiation.TrySetResult();
        await start.WaitAsync(Deadline);
        await Assert.That(server.Negotiations >= 2).IsTrue();
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
    }

    [Test]
    public async Task Stalled_legacy_token_provider_does_not_block_stop_or_restart()
    {
        using var server = new HubServer();
        var entered = Signal();
        var stalled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var client = server.Client(options => options.AccessTokenProvider = () =>
        {
            entered.TrySetResult();
            return Interlocked.Increment(ref calls) == 1 ? stalled.Task : Task.FromResult("fresh-token");
        });
        Task start = client.StartConnection().AsTask();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            await client.StopConnection().WaitAsync(Deadline);
            await Assert.That(async () => await start).Throws<OperationCanceledException>();
            await client.StartConnection().AsTask().WaitAsync(Deadline);
            await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
        }
        finally { stalled.TrySetException(new InvalidOperationException("Late token failure")); }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Token_or_attempt_timeout_cancels_provider_and_recovers_with_fresh_credentials(bool tokenTimeout)
    {
        using var server = new HubServer();
        var cancelled = Signal();
        var calls = 0;
        await using var client = server.Client(options =>
        {
            options.AccessTokenTimeout = tokenTimeout ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMinutes(1);
            options.ConnectionAttemptTimeout = tokenTimeout ? TimeSpan.FromMinutes(1) : TimeSpan.FromMilliseconds(100);
            options.AccessTokenProvider = () => throw new InvalidOperationException("Cancellation-aware provider should take precedence");
            options.AccessTokenProviderWithCancellation = async token =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    using var registration = token.Register(() => throw new InvalidOperationException("Application cancellation handler failed"));
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                }
                return "fresh-token";
            };
        });
        await client.StartConnection().AsTask().WaitAsync(Deadline);
        await cancelled.Task.WaitAsync(Deadline);
        await Assert.That(calls >= 2).IsTrue();
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
    }

    [Test]
    public async Task Dispose_interrupts_token_acquisition_during_automatic_reconnect()
    {
        using var server = new HubServer();
        var entered = Signal();
        var stalled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = false;
        var client = server.Client(options => options.AccessTokenProvider = () =>
        {
            if (!Volatile.Read(ref block)) return Task.FromResult("token");
            entered.TrySetResult();
            return stalled.Task;
        });
        try
        {
            await client.StartConnection().AsTask().WaitAsync(Deadline);
            Volatile.Write(ref block, true);
            server.FailCurrentPoll.TrySetResult();
            await entered.Task.WaitAsync(Deadline);
            await client.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
        finally
        {
            stalled.TrySetResult("late-token");
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task Stalled_restoration_is_cancelled_and_retried_without_restarting_transport()
    {
        using var server = new HubServer();
        var cancelled = Signal();
        var calls = 0;
        await using var client = server.Client(options =>
        {
            options.RestorationTimeout = TimeSpan.FromMilliseconds(100);
            options.ConnectionRestored = async context =>
            {
                if (Interlocked.Increment(ref calls) != 1) return;
                using var registration = context.CancellationToken.Register(() => throw new InvalidOperationException("Application cancellation handler failed"));
                try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            };
        });
        await client.StartConnection().AsTask().WaitAsync(Deadline);
        await cancelled.Task.WaitAsync(Deadline);
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(server.Negotiations).IsEqualTo(1);
    }

    [Test]
    public async Task Stalled_legacy_restoration_does_not_prevent_subsequent_attempts()
    {
        using var server = new HubServer();
        var stalled = Signal();
        var calls = 0;
        await using var client = server.Client(options =>
        {
            options.RestorationTimeout = TimeSpan.FromMilliseconds(100);
            options.ConnectionRestored = _ => Interlocked.Increment(ref calls) == 1 ? stalled.Task : Task.CompletedTask;
        });
        try
        {
            await client.StartConnection().AsTask().WaitAsync(Deadline);
            await Assert.That(calls).IsEqualTo(2);
        }
        finally { stalled.TrySetResult(); }
    }

    [Test]
    public async Task Options_mutation_does_not_change_an_existing_clients_retry_budget()
    {
        using var server = new HubServer { Available = false };
        SignalRWebClientOptions original = null!;
        await using var client = server.Client(options =>
        {
            original = options;
            options.ReconnectIndefinitely = false;
            options.MaxRetryAttempts = 1;
        });
        original.MaxRetryAttempts = 20;
        original.ReconnectIndefinitely = true;
        original.ConnectionAttemptTimeout = TimeSpan.Zero;
        await client.StartConnection().AsTask().WaitAsync(Deadline);
        await Assert.That(server.Negotiations).IsEqualTo(2);
    }

    [Test]
    public async Task Throwing_logger_and_notifications_do_not_terminate_recovery()
    {
        using var server = new HubServer { Available = false };
        var exhausted = Signal();
        var restored = Signal();
        await using var client = server.Client(options =>
        {
            options.Logger = new ThrowingLogger();
            options.MaxRetryAttempts = 0;
            options.RetriesExhausted = () => { exhausted.TrySetResult(); throw new InvalidOperationException("Notification failed"); };
            options.ConnectionRestored = _ => { restored.TrySetResult(); return Task.CompletedTask; };
        });
        await client.StartConnection().AsTask().WaitAsync(Deadline);
        await exhausted.Task.WaitAsync(Deadline);
        server.Available = true;
        await restored.Task.WaitAsync(Deadline);
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
    }

    [Test]
    public async Task Reconnection_interrupts_old_restoration_backoff()
    {
        using var server = new HubServer();
        var delaying = Signal();
        var restored = Signal();
        var calls = 0;
        await using var client = server.Client(options =>
        {
            options.RetryDelayProvider = _ => { delaying.TrySetResult(); return TimeSpan.FromMinutes(1); };
            options.ConnectionRestored = _ =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("Temporary restoration failure");
                restored.TrySetResult();
                return Task.CompletedTask;
            };
        });
        Task start = client.StartConnection().AsTask();
        await delaying.Task.WaitAsync(Deadline);
        // A hub abort closes without automatic reconnect; manual recovery must interrupt the old delay.
        await server.AbortConnection(client.Connection.ConnectionId!);
        await restored.Task.WaitAsync(Deadline);
        await start.WaitAsync(Deadline);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(86401)]
    public async Task Invalid_deadlines_are_rejected(int seconds)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(seconds);
        await Assert.That(() => new SignalRWebClient(new SignalRWebClientOptions
        { HubUrl = "http://localhost/hub", ConnectionAttemptTimeout = timeout })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SignalRWebClient(new SignalRWebClientOptions
        { HubUrl = "http://localhost/hub", AccessTokenTimeout = timeout })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SignalRWebClient(new SignalRWebClientOptions
        { HubUrl = "http://localhost/hub", RestorationTimeout = timeout })).Throws<ArgumentOutOfRangeException>();
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("Logging sink failed");
    }
}
