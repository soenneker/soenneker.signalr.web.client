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
        // Retain a requested failure until a GET consumes it, even if there was no poll
        // in flight when the test signaled the outage.
        if (request.Method == HttpMethod.Get && server.TryFailPoll())
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using var polling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<HttpResponseMessage> response = base.SendAsync(request, polling.Token);
        if (request.Method == HttpMethod.Get)
        {
            Task finished = await Task.WhenAny(response, server.FailCurrentPoll.Task);
            if (finished != response && server.TryFailPoll())
            {
                await polling.CancelAsync();
                try { (await response).Dispose(); } catch (OperationCanceledException) { }
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
        }
        HttpResponseMessage result = await response;
        // Graceful SignalR shutdown can end the final poll with 204 instead of issuing DELETE.
        if (server.BlockStop && request.Method == HttpMethod.Get && result.StatusCode == HttpStatusCode.NoContent)
        {
            server.Stopping.TrySetResult();
            await server.ReleaseStop.Task;
        }
        return result;
    }
}
