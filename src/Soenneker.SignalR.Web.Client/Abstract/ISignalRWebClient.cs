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
    HubConnection Connection { get; }

    /// <summary>
    /// Starts the SignalR connection asynchronously.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    ValueTask StartConnection(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the SignalR connection asynchronously.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task StopConnection(CancellationToken cancellationToken = default);
}