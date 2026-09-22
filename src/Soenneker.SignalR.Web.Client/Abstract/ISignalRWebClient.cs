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
    /// Starts or joins shared connection recovery. Concurrent calls do not restart recovery
    /// or repeat successful restoration callbacks. A call during stop waits for stop to finish.
    /// </summary>
    /// <param name="cancellationToken">Cancels this caller's wait without cancelling shared recovery. Use StopConnection to stop recovery.</param>
    /// <returns>A task that completes after restoration succeeds or the current retry cycle is exhausted.</returns>
    ValueTask StartConnection(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops connection attempts, restoration, and the transport. Concurrent calls share shutdown.
    /// </summary>
    /// <param name="cancellationToken">Cancels this caller's wait; shutdown continues in the background.</param>
    /// <returns>A task that completes after the connection has stopped.</returns>
    Task StopConnection(CancellationToken cancellationToken = default);
}
