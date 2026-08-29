using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client.Abstract;

/// <summary>
/// A resilient and dependable .NET SignalR web client
/// </summary>
/// <summary>
/// Defines the contract for a SignalR web client that manages connections and reconnections to a SignalR hub.
/// </summary>
public interface ISignalRWebClient : IAsyncDisposable
{
    /// <summary>
    /// Gets connection.
    /// </summary>
    HubConnection Connection { get; }

    /// <summary>
    /// Starts the SignalR connection asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes after the Signal R Web Client has started.</returns>
    ValueTask StartConnection(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the SignalR connection asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes after the Signal R Web Client has stopped.</returns>
    Task StopConnection(CancellationToken cancellationToken = default);
}
