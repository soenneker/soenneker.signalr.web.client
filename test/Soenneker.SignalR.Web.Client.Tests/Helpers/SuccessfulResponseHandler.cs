using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client.Tests;

internal sealed class SuccessfulResponseHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
}
