using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Soenneker.SignalR.Web.Client.Options;

namespace Soenneker.SignalR.Web.Client.Tests;

internal sealed class HubServer : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TestServer _server;
    private readonly IHost _host;
    private readonly ConnectionRegistry _registry = new();
    public volatile bool BlockNegotiation;
    public volatile bool BlockStop;
    public volatile bool Available = true;
    public HttpStatusCode FailureStatusCode = HttpStatusCode.ServiceUnavailable;
    public int Negotiations;
    public TaskCompletionSource Negotiating { get; } = Signal();
    public TaskCompletionSource BlockedNegotiation { get; } = Signal();
    public TaskCompletionSource FailCurrentPoll { get; } = Signal();
    public TaskCompletionSource ReleaseNegotiation { get; } = Signal();
    public TaskCompletionSource Stopping { get; } = Signal();
    public TaskCompletionSource ReleaseStop { get; } = Signal();

    public HubServer()
    {
        _host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddSingleton(_registry);
            services.AddRouting();
            services.AddSignalR();
        }).Configure(app =>
        {
            app.UseRouting();
            app.UseEndpoints(endpoints => endpoints.MapHub<RecoveryHub>("/hub"));
        })).Start();
        _server = _host.GetTestServer();
    }

    public SignalRWebClient Client(Action<SignalRWebClientOptions>? configure = null)
    {
        var options = new SignalRWebClientOptions
        {
            HubUrl = "http://localhost/hub", TransportType = HttpTransportType.LongPolling,
            HttpMessageHandlerFactory = handler => { handler.Dispose(); return new HubServerHandler(this, _server.CreateHandler()); },
            InitialRetryDelay = TimeSpan.FromMilliseconds(20), RetryDelayProvider = _ => TimeSpan.FromMilliseconds(10)
        };
        configure?.Invoke(options);
        return new SignalRWebClient(options);
    }

    public async Task AbortConnection(string id)
    {
        using var cancellation = new CancellationTokenSource(Deadline);
        while (!_registry.Connections.TryGetValue(id, out HubCallerContext? context))
            await Task.Delay(10, cancellation.Token);
        _registry.Connections[id].Abort();
    }

    public void Dispose() => _host.Dispose();
}
