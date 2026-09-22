using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Soenneker.SignalR.Web.Client.Tests;

public sealed class RecoveryHub(ConnectionRegistry registry) : Hub
{
    public override Task OnConnectedAsync()
    {
        registry.Connections[Context.ConnectionId] = Context;
        return base.OnConnectedAsync();
    }
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Connections.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }
}
