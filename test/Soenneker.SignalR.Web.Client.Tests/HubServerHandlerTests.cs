using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client.Tests;

public sealed class HubServerHandlerTests
{
    [Test]
    public async ValueTask Failure_requested_between_polls_is_consumed_once_by_the_next_poll(CancellationToken cancellationToken)
    {
        using var server = new HubServer();
        using var handler = new HubServerHandler(server, new SuccessfulResponseHandler());
        using var client = new HttpMessageInvoker(handler);
        server.FailCurrentPoll.TrySetResult();
        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, "http://localhost/hub");
        using HttpResponseMessage first = await client.SendAsync(firstRequest, cancellationToken);
        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, "http://localhost/hub");
        using HttpResponseMessage second = await client.SendAsync(secondRequest, cancellationToken);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
