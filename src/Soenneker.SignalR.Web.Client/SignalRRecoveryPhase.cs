namespace Soenneker.SignalR.Web.Client;

/// <summary>The application recovery phase, distinct from the transport state.</summary>
public enum SignalRRecoveryPhase
{
    Stopped, Connecting, Reconnecting, Restoring, Ready, RetryDelay, WaitingForCallback, Exhausted, Faulted, Disposed
}
