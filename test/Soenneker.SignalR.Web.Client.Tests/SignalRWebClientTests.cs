using Soenneker.Tests.HostedUnit;
using Soenneker.SignalR.Web.Client.Events;
using Soenneker.SignalR.Web.Client.Options;
using Soenneker.SignalR.Web.Client;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public class SignalRWebClientTests : HostedUnitTest
{
    public SignalRWebClientTests(Host host) : base(host)
    {
    }

    [Test]
    public async ValueTask Reconnect_recovery_is_indefinite_by_default(CancellationToken cancellationToken)
    {
        var options = new SignalRWebClientOptions();

        await Assert.That(options.ReconnectIndefinitely).IsTrue();
    }

    [Test]
    public async ValueTask Restored_context_distinguishes_reconnects(CancellationToken cancellationToken)
    {
        var context = new SignalRConnectionRestoredContext("connection-2", true);

        await Assert.That(context.ConnectionId).IsEqualTo("connection-2");
        await Assert.That(context.IsReconnect).IsTrue();
    }

    [Test]
    public async ValueTask Transport_negotiation_is_enabled_by_default(CancellationToken cancellationToken)
    {
        var options = new SignalRWebClientOptions();

        await Assert.That(options.TransportType).IsNull();
    }

    [Test]
    public async ValueTask Constructor_rejects_invalid_retry_configuration(CancellationToken cancellationToken)
    {
        var options = new SignalRWebClientOptions { HubUrl = "https://localhost/hub", MaxRetryAttempts = -1 };

        await Assert.That(() => new SignalRWebClient(options)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async ValueTask Negotiation_uses_configured_http_handler_factory(CancellationToken cancellationToken)
    {
        var invoked = false;
        await using var client = new SignalRWebClient(new SignalRWebClientOptions
        {
            HubUrl = "https://localhost/hub", MaxRetryAttempts = 0, ReconnectIndefinitely = false,
            HttpMessageHandlerFactory = handler =>
            {
                invoked = true;
                handler.Dispose();
                throw new InvalidOperationException("Stop before issuing a network request.");
            }
        });

        await client.StartConnection(cancellationToken: cancellationToken);

        await Assert.That(invoked).IsTrue();
    }

    [Test]
    public async ValueTask Cancelled_initial_connection_propagates_cancellation(CancellationToken cancellationToken)
    {
        await using var client = new SignalRWebClient(new SignalRWebClientOptions
        {
            HubUrl = "http://127.0.0.1:1/hub", MaxRetryAttempts = 0, ReconnectIndefinitely = false
        });
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.That(async () => await client.StartConnection(cancellation.Token)).Throws<OperationCanceledException>();
    }
}
