using System;
using Microsoft.AspNetCore.SignalR.Client;

namespace Soenneker.SignalR.Web.Client;

/// <summary>An immutable, point-in-time view of transport and application recovery. Connectivity may change after observation.</summary>
public sealed record SignalRClientStatus
{
    /// <summary>Underlying SignalR transport state.</summary>
    public HubConnectionState TransportState { get; init; }
    /// <summary>Current application recovery phase.</summary>
    public SignalRRecoveryPhase Phase { get; init; }
    /// <summary>Whether this connection revision has completed restoration.</summary>
    public bool IsReady { get; init; }
    /// <summary>Most recent failure, retained after successful recovery; may contain application-sensitive details.</summary>
    public Exception? LastFailure { get; init; }
    /// <summary>Total scheduled retries over this client lifetime, including automatic reconnect.</summary>
    public long RetryCount { get; init; }
    /// <summary>Total observed connection/restoration failures over this client lifetime.</summary>
    public long FailureCount { get; init; }
    /// <summary>Scheduled next retry time, or null when no retry delay is active.</summary>
    public DateTimeOffset? NextRetryAt { get; init; }
    /// <summary>UTC time of the most recent successful restoration.</summary>
    public DateTimeOffset? LastRestoredAt { get; init; }
    /// <summary>Token callback tasks that have not finished, including abandoned tasks.</summary>
    public int OutstandingTokenCallbacks { get; init; }
    /// <summary>Restoration callback tasks that have not finished, including abandoned tasks.</summary>
    public int OutstandingRestorationCallbacks { get; init; }
    /// <summary>Tasks whose library waits ended but whose application work has not finished.</summary>
    public int AbandonedCallbacks { get; init; }
}
