using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client.Tests;

internal sealed class HubServerHandler(HubServer server, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Delete && server.BlockStop)
        {
            server.Stopping.TrySetResult();
            await server.ReleaseStop.Task.WaitAsync(cancellationToken);
        }
        if (request.RequestUri!.AbsolutePath.EndsWith("/negotiate", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref server.Negotiations);
            server.Negotiating.TrySetResult();
            if (server.BlockNegotiation)
            {
                server.BlockedNegotiation.TrySetResult();
                await server.ReleaseNegotiation.Task.WaitAsync(cancellationToken);
            }
        }
        if (!server.Available) return new HttpResponseMessage(server.FailureStatusCode);
        using var polling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<HttpResponseMessage> response = base.SendAsync(request, polling.Token);
        if (request.Method == HttpMethod.Get && !server.FailCurrentPoll.Task.IsCompleted)
        {
            Task finished = await Task.WhenAny(response, server.FailCurrentPoll.Task);
            if (finished != response)
            {
                await polling.CancelAsync();
                try { (await response).Dispose(); } catch (OperationCanceledException) { }
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
        }
        return await response;
    }
}
