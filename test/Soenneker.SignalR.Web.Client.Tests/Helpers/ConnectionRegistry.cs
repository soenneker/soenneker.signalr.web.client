using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Soenneker.SignalR.Web.Client.Tests;

public sealed class ConnectionRegistry
{
    public ConcurrentDictionary<string, HubCallerContext> Connections { get; } = new();
}
