using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace Soenneker.SignalR.Web.Client.Tests;

public sealed class SignalRCallbackBoundsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async ValueTask Abandoned_callbacks_are_bounded_across_restart_and_release_capacity(bool tokenProvider, CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        async Task<string> Callback()
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return "token";
        }
        await using var client = server.Client(options =>
        {
            options.MaxOutstandingCallbacks = 2;
            options.AccessTokenTimeout = TimeSpan.FromMilliseconds(60);
            options.RestorationTimeout = TimeSpan.FromMilliseconds(60);
            if (tokenProvider) options.AccessTokenProvider = Callback;
            else options.ConnectionRestored = async _ => { await Callback(); };
        });
        try
        {
            Task readiness = client.EnsureConnection(cancellationToken).AsTask();
            SignalRClientStatus saturated = await WaitForStatus(client, s => s.AbandonedCallbacks == 2, cancellationToken);
            await Assert.That(saturated.IsReady).IsFalse();
            await Assert.That(saturated.Phase).IsEqualTo(SignalRRecoveryPhase.WaitingForCallback);
            await client.StopConnection(cancellationToken).WaitAsync(Deadline, cancellationToken);
            await Assert.That(async () => await readiness).Throws<OperationCanceledException>();
            Task restarted = client.EnsureConnection(cancellationToken).AsTask();
            // Several callback deadlines elapse while the original tasks remain unfinished.
            await Task.Delay(250, cancellationToken);
            await Assert.That(Volatile.Read(ref calls)).IsEqualTo(2);
            await Assert.That(restarted.IsCompleted).IsFalse();
            release.TrySetResult();
            await restarted.WaitAsync(Deadline, cancellationToken);
            SignalRClientStatus ready = await WaitForStatus(client, s => s.IsReady && s.AbandonedCallbacks == 0, cancellationToken);
            await Assert.That(ready.Phase).IsEqualTo(SignalRRecoveryPhase.Ready);
            await Assert.That(ready.LastRestoredAt.HasValue).IsTrue();
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async ValueTask Connected_transport_is_not_ready_until_restoration_commits(CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = server.Client(options => options.ConnectionRestored = _ => release.Task);
        try
        {
            Task readiness = client.EnsureConnection(cancellationToken).AsTask();
            SignalRClientStatus restoring = await WaitForStatus(client, s => s.OutstandingRestorationCallbacks == 1, cancellationToken);
            await Assert.That(restoring.TransportState).IsEqualTo(HubConnectionState.Connected);
            await Assert.That(restoring.IsReady).IsFalse();
            release.TrySetResult();
            await readiness.WaitAsync(Deadline, cancellationToken);
            await Assert.That((await client.GetStatus(cancellationToken)).IsReady).IsTrue();
            await client.DisposeAsync();
            await Assert.That((await client.GetStatus(cancellationToken)).Phase).IsEqualTo(SignalRRecoveryPhase.Disposed);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async ValueTask Synchronously_blocking_callback_does_not_block_shutdown(CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        using var release = new ManualResetEventSlim();
        await using var client = server.Client(options =>
        {
            options.MaxOutstandingCallbacks = 1;
            options.RestorationTimeout = TimeSpan.FromMilliseconds(60);
            options.ConnectionRestored = _ => { release.Wait(); return Task.CompletedTask; };
        });
        try
        {
            Task readiness = client.EnsureConnection(cancellationToken).AsTask();
            await WaitForStatus(client, s => s.AbandonedCallbacks == 1, cancellationToken);
            await client.DisposeAsync().AsTask().WaitAsync(Deadline, cancellationToken);
            await Assert.That(async () => await readiness).Throws<OperationCanceledException>();
        }
        finally { release.Set(); }
    }

    private static async Task<SignalRClientStatus> WaitForStatus(SignalRWebClient client, Func<SignalRClientStatus, bool> predicate, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Deadline);
        while (true)
        {
            SignalRClientStatus status = await client.GetStatus(deadline.Token);
            if (predicate(status)) return status;
            await Task.Delay(10, deadline.Token);
        }
    }
}
