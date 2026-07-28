namespace Soenneker.SignalR.Web.Client.Events;

/// <summary>
/// Describes a SignalR connection that has been established and is ready for
/// application-level synchronization.
/// </summary>
public sealed class SignalRConnectionRestoredContext
{
    /// <summary>
    /// Gets the current connection identifier, when the server provides one.
    /// </summary>
    public string? ConnectionId { get; }

    /// <summary>
    /// Gets a value indicating whether this connection restored a previously
    /// established session.
    /// </summary>
    public bool IsReconnect { get; }

    public SignalRConnectionRestoredContext(string? connectionId, bool isReconnect)
    {
        ConnectionId = connectionId;
        IsReconnect = isReconnect;
    }
}
