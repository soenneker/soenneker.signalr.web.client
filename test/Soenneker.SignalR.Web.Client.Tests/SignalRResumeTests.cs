using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace Soenneker.SignalR.Web.Client.Tests;

public class SignalRResumeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Test]
    public async ValueTask Concurrent_resume_signals_replace_stale_connection_and_restore_once(CancellationToken cancellationToken)
    {
        using var server = new HubServer { BlockStop = true };
        var restores = 0;
        var reconnected = false;
        await using var client = server.Client(options => options.ConnectionRestored = context =>
        {
            Interlocked.Increment(ref restores);
            reconnected = context.IsReconnect;
            return Task.CompletedTask;
        });
        await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
        string? previousId = client.Connection.ConnectionId;
        Task[] resumes = Enumerable.Range(0, 12).Select(_ => client.ResumeConnection(cancellationToken: cancellationToken).AsTask()).ToArray();
        try
        {
            await server.Stopping.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
            server.ReleaseStop.TrySetResult();
            await Task.WhenAll(resumes).WaitAsync(Deadline, cancellationToken: cancellationToken);
            await Assert.That(server.Negotiations).IsEqualTo(2);
            await Assert.That(restores).IsEqualTo(2);
            await Assert.That(reconnected).IsTrue();
            await Assert.That(client.Connection.ConnectionId != previousId).IsTrue();
        }
        finally { server.ReleaseStop.TrySetResult(); }
    }

    [Test]
    public async ValueTask Resume_leaves_unused_and_intentionally_stopped_clients_stopped(CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        await using var client = server.Client();
        await client.ResumeConnection(cancellationToken: cancellationToken);
        await Assert.That(server.Negotiations).IsEqualTo(0);
        await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
        await client.StopConnection(cancellationToken: cancellationToken).WaitAsync(Deadline, cancellationToken: cancellationToken);
        await client.ResumeConnection(cancellationToken: cancellationToken);
        await Assert.That(server.Negotiations).IsEqualTo(1);
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Disconnected);
    }

    [Test]
    public async ValueTask Explicit_stop_prevents_pending_resume_from_restarting_connection(CancellationToken cancellationToken)
    {
        using var server = new HubServer { BlockStop = true };
        await using var client = server.Client();
        await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
        Task resume = client.ResumeConnection(cancellationToken: cancellationToken).AsTask();
        try
        {
            await server.Stopping.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
            Task stop = client.StopConnection(cancellationToken: cancellationToken);
            server.ReleaseStop.TrySetResult();
            await Task.WhenAll(resume, stop).WaitAsync(Deadline, cancellationToken: cancellationToken);
            await Assert.That(server.Negotiations).IsEqualTo(1);
            await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Disconnected);
        }
        finally { server.ReleaseStop.TrySetResult(); }
    }

    [Test]
    public async ValueTask Disposal_prevents_pending_resume_from_restarting_connection(CancellationToken cancellationToken)
    {
        using var server = new HubServer { BlockStop = true };
        var client = server.Client();
        try
        {
            await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
            Task resume = client.ResumeConnection(cancellationToken: cancellationToken).AsTask();
            await server.Stopping.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
            Task disposal = client.DisposeAsync().AsTask();
            server.ReleaseStop.TrySetResult();
            await Task.WhenAll(resume, disposal).WaitAsync(Deadline, cancellationToken: cancellationToken);
            await Assert.That(server.Negotiations).IsEqualTo(1);
            await Assert.That(async () => await client.ResumeConnection(cancellationToken: cancellationToken)).Throws<ObjectDisposedException>();
        }
        finally
        {
            server.ReleaseStop.TrySetResult();
            await client.DisposeAsync();
        }
    }

    [Test]
    public async ValueTask Cancelling_resume_waiter_does_not_cancel_shared_recovery(CancellationToken cancellationToken)
    {
        using var server = new HubServer { BlockStop = true };
        await using var client = server.Client();
        using var cancellation = new CancellationTokenSource();
        await client.StartConnection(cancellationToken: cancellationToken).AsTask().WaitAsync(Deadline, cancellationToken: cancellationToken);
        Task cancelled = client.ResumeConnection(cancellation.Token).AsTask();
        Task other = client.ResumeConnection(cancellationToken: cancellationToken).AsTask();
        try
        {
            await server.Stopping.Task.WaitAsync(Deadline, cancellationToken: cancellationToken);
            cancellation.Cancel();
            await Assert.That(async () => await cancelled).Throws<OperationCanceledException>();
            server.ReleaseStop.TrySetResult();
            await other.WaitAsync(Deadline, cancellationToken: cancellationToken);
            await Assert.That(server.Negotiations).IsEqualTo(2);
            await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
        }
        finally { server.ReleaseStop.TrySetResult(); }
    }
}
