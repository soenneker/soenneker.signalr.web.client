using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.SignalR.Web.Client.Options;

namespace Soenneker.SignalR.Web.Client.Tests;

public class SignalRTransportTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Test]
    [Arguments(HttpTransportType.WebSockets)]
    [Arguments(HttpTransportType.ServerSentEvents)]
    [Arguments(HttpTransportType.LongPolling)]
    public async Task Real_transport_recovers_after_server_disconnect(HttpTransportType transport)
    {
        var registry = new ConnectionRegistry();
        await using WebApplication app = CreateServer(registry);
        app.MapHub<RecoveryHub>("/hub", options => options.AllowStatefulReconnects = true);
        await app.StartAsync();
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        await using var client = new SignalRWebClient(new SignalRWebClientOptions
        {
            HubUrl = app.Urls.Single() + "/hub",
            TransportType = transport,
            StatefulReconnect = true,
            RetryDelayProvider = _ => TimeSpan.FromMilliseconds(10),
            ConnectionRestored = context =>
            {
                if (Interlocked.Increment(ref callbacks) == 2 && context.IsReconnect) restored.TrySetResult();
                return Task.CompletedTask;
            }
        });
        await client.StartConnection().AsTask().WaitAsync(Deadline);
        string firstId = client.Connection.ConnectionId!;
        using var cancellation = new CancellationTokenSource(Deadline);
        while (!registry.Connections.ContainsKey(firstId)) await Task.Delay(10, cancellation.Token);
        await Assert.That(registry.Connections[firstId].Features.Get<IHttpTransportFeature>()!.TransportType).IsEqualTo(transport);
        registry.Connections[firstId].Abort();
        await restored.Task.WaitAsync(Deadline);
        await client.StartConnection().AsTask().WaitAsync(Deadline);
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
        await Assert.That(client.Connection.ConnectionId != firstId).IsTrue();
    }

    [Test]
    public async Task Blocked_websocket_upgrade_falls_back_to_an_available_http_transport()
    {
        var registry = new ConnectionRegistry();
        var rejected = 0;
        await using WebApplication app = CreateServer(registry);
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.Upgrade == "websocket")
            {
                Interlocked.Increment(ref rejected);
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }
            await next(context);
        });
        app.MapHub<RecoveryHub>("/hub");
        await app.StartAsync();
        await using var client = new SignalRWebClient(new SignalRWebClientOptions { HubUrl = app.Urls.Single() + "/hub" });
        await client.StartConnection().AsTask().WaitAsync(Deadline);
        string id = client.Connection.ConnectionId!;
        using var cancellation = new CancellationTokenSource(Deadline);
        while (!registry.Connections.ContainsKey(id)) await Task.Delay(10, cancellation.Token);
        await Assert.That(rejected).IsEqualTo(1);
        await Assert.That(registry.Connections[id].Features.Get<IHttpTransportFeature>()!.TransportType == HttpTransportType.WebSockets).IsFalse();
        await Assert.That(client.Connection.State).IsEqualTo(HubConnectionState.Connected);
    }

    [Test]
    public async Task Silent_websocket_is_detected_by_heartbeat_timeout_and_recovers()
    {
        var registry = new ConnectionRegistry();
        await using WebApplication app = CreateServer(registry, TimeSpan.FromMinutes(1));
        app.MapHub<RecoveryHub>("/hub");
        await app.StartAsync();
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var calls = 0;
        await using var client = new SignalRWebClient(new SignalRWebClientOptions
        {
            HubUrl = app.Urls.Single() + "/hub", TransportType = HttpTransportType.WebSockets,
            ServerTimeout = TimeSpan.FromMilliseconds(200),
            ConnectionReconnecting = error => failure = error,
            RetryDelayProvider = _ => TimeSpan.FromMilliseconds(10),
            ConnectionRestored = _ => { if (Interlocked.Increment(ref calls) == 2) restored.TrySetResult(); return Task.CompletedTask; }
        });
        await client.EnsureConnection().AsTask().WaitAsync(Deadline);
        await restored.Task.WaitAsync(Deadline);
        await client.EnsureConnection().AsTask().WaitAsync(Deadline);
        await Assert.That(failure is TimeoutException).IsTrue();
    }

    private static WebApplication CreateServer(ConnectionRegistry registry, TimeSpan? keepAlive = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSignalR(options => { if (keepAlive is { } interval) options.KeepAliveInterval = interval; });
        return builder.Build();
    }
}
