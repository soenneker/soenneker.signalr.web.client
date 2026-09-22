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
    /// Use this wrapper for start, stop, and disposal so recovery remains coordinated.
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
    /// Starts or joins recovery and waits until the transport is connected and ConnectionRestored has
    /// succeeded. Unlike StartConnection, this wait continues across exhausted retry cycles when
    /// indefinite recovery is enabled. Finite exhaustion throws with the last failure as its inner exception.
    /// Stop or disposal cancels pending waits. Readiness describes this instant, not future connectivity.
    /// Calling this from ConnectionRestored is invalid because restoration cannot await its own completion.
    /// </summary>
    /// <param name="cancellationToken">Cancels only this caller's wait. Supply a deadline for an offline host.</param>
    /// <returns>A task that completes only after successful connection restoration.</returns>
    ValueTask EnsureConnection(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a potentially stale connection after device resume or network restoration and reruns
    /// application restoration. Wire this to the host's resume, pageshow, or online notification when
    /// a suspended connection may be stale; it intentionally reconnects even if SignalR reports Connected.
    /// Concurrent calls share recovery. An unused or intentionally stopped client stays stopped,
    /// and a subsequent explicit stop or disposal prevents a pending resume from restarting it.
    /// The library cannot execute while its host is suspended or automatically observe platform events.
    /// Long pauses are also detected by the configurable ResumeDetectionThreshold; explicit notifications
    /// provide faster recovery after shorter pauses or credential changes.
    /// </summary>
    /// <param name="cancellationToken">Cancels only this caller's wait, not the shared resume operation.</param>
    /// <returns>A task that completes when resume recovery finishes its current retry cycle, or no resume is needed.</returns>
    ValueTask ResumeConnection(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops connection attempts, restoration, and the transport. Concurrent calls share shutdown.
    /// </summary>
    /// <param name="cancellationToken">Cancels this caller's wait; shutdown continues in the background.</param>
    /// <returns>A task that completes after the connection has stopped.</returns>
    Task StopConnection(CancellationToken cancellationToken = default);
}
