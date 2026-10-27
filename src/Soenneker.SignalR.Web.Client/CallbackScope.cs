namespace Soenneker.SignalR.Web.Client;

internal sealed class CallbackScope(Recovery recovery)
{
    public Recovery Recovery { get; } = recovery;
    public volatile bool Active = true;
}
