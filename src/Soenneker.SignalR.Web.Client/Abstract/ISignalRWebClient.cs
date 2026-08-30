using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client.Abstract;

/// <summary>
/// Defines the contract for a SignalR web client that manages connections and reconnections to a SignalR hub.
/// </summary>
public interface ISignalRWebClient : IAsyncDisposable
{
    /// <summary>
    /// Gets the underlying SignalR connection for registering hub handlers and invoking hub methods.
    /// </summary>
    HubConnection Connection { get; }

    /// <summary>
    /// Starts the SignalR connection asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes after the connection attempt finishes.</returns>
    ValueTask StartConnection(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the SignalR connection asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes after the connection has stopped.</returns>
    Task StopConnection(CancellationToken cancellationToken = default);
}
