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
    public async Task Concurrent_resume_signals_replace_stale_connection_and_restore_once()
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
        await client.StartConnection().AsTask().WaitAsync(Deadline);
        string? previousId = client.Connection.ConnectionId;
        Task[] resumes = Enumerable.Range(0, 12).Select(_ => client.ResumeConnection().AsTask()).ToArray();
        try
        {
            await server.Stopping.Task.WaitAsync(Deadline);
            server.ReleaseStop.TrySetResult();
            await Task.WhenAll(resumes).WaitAsync(Deadline);
            await Assert.That(server.Negotiations).IsEqualTo(2);
            await Assert.That(restores).IsEqualTo(2);
            await Assert.That(reconnected).IsTrue();
            await Assert.That(client.Connection.ConnectionId != previousId).IsTrue();
        }
        finally { server.ReleaseStop.TrySetResult(); }
    }

    [Test]
    public async Task Resume_leaves_unused_and_intentionally_stopped_clients_stopped()
    {
        using var server = new HubServer();
        await using var client = server.Client();
        await client.ResumeConnection();
        await Assert.That(server.Negotiations).IsEqualTo(0);
        await client.StartConnection().AsTask().WaitAsync(Deadline);
        await client.StopConnection().WaitAsync(Deadline);
        await client.ResumeConnection();
        await Assert.That(server.Negotiations).IsEqualTo(1);
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Disconnected);
    }

    [Test]
    public async Task Explicit_stop_prevents_pending_resume_from_restarting_connection()
    {
        using var server = new HubServer { BlockStop = true };
        await using var client = server.Client();
        await client.StartConnection().AsTask().WaitAsync(Deadline);
        Task resume = client.ResumeConnection().AsTask();
        try
        {
            await server.Stopping.Task.WaitAsync(Deadline);
            Task stop = client.StopConnection();
            server.ReleaseStop.TrySetResult();
            await Task.WhenAll(resume, stop).WaitAsync(Deadline);
            await Assert.That(server.Negotiations).IsEqualTo(1);
            await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Disconnected);
        }
        finally { server.ReleaseStop.TrySetResult(); }
    }

    [Test]
    public async Task Disposal_prevents_pending_resume_from_restarting_connection()
    {
        using var server = new HubServer { BlockStop = true };
        var client = server.Client();
        try
        {
            await client.StartConnection().AsTask().WaitAsync(Deadline);
            Task resume = client.ResumeConnection().AsTask();
            await server.Stopping.Task.WaitAsync(Deadline);
            Task disposal = client.DisposeAsync().AsTask();
            server.ReleaseStop.TrySetResult();
            await Task.WhenAll(resume, disposal).WaitAsync(Deadline);
            await Assert.That(server.Negotiations).IsEqualTo(1);
            await Assert.That(async () => await client.ResumeConnection()).Throws<ObjectDisposedException>();
        }
        finally
        {
            server.ReleaseStop.TrySetResult();
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task Cancelling_resume_waiter_does_not_cancel_shared_recovery()
    {
        using var server = new HubServer { BlockStop = true };
        await using var client = server.Client();
        using var cancellation = new CancellationTokenSource();
        await client.StartConnection().AsTask().WaitAsync(Deadline);
        Task cancelled = client.ResumeConnection(cancellation.Token).AsTask();
        Task other = client.ResumeConnection().AsTask();
        try
        {
            await server.Stopping.Task.WaitAsync(Deadline);
            cancellation.Cancel();
            await Assert.That(async () => await cancelled).Throws<OperationCanceledException>();
            server.ReleaseStop.TrySetResult();
            await other.WaitAsync(Deadline);
            await Assert.That(server.Negotiations).IsEqualTo(2);
            await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
        }
        finally { server.ReleaseStop.TrySetResult(); }
    }
}
