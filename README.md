[![](https://img.shields.io/nuget/v/soenneker.signalr.web.client.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.signalr.web.client/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.signalr.web.client/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.signalr.web.client/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.signalr.web.client.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.signalr.web.client/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.signalr.web.client/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.signalr.web.client/actions/workflows/codeql.yml)

# Soenneker.SignalR.Web.Client

A SignalR client wrapper that retries initial connections, recovers closed connections, and exposes callbacks for restoring application state after a connection returns.

## Installation

```bash
dotnet add package Soenneker.SignalR.Web.Client
```

## Usage

```csharp
using Microsoft.AspNetCore.Http.Connections;
using Soenneker.SignalR.Web.Client;
using Soenneker.SignalR.Web.Client.Options;

var options = new SignalRWebClientOptions
{
    HubUrl = "https://api.example.com/hubs/updates",
    AccessTokenProvider = () => Task.FromResult(accessToken),
    TransportType = HttpTransportType.WebSockets,
    ConnectionRestored = async context =>
    {
        // Reload authoritative state after the initial connection or a reconnect.
        await SynchronizeAsync(context.IsReconnect);
    },
    RetriesExhausted = () => NotifyConnectionFailure()
};

await using var client = new SignalRWebClient(options);

client.Connection.On<OrderUpdated>("OrderUpdated", update =>
{
    Apply(update);
});

await client.StartConnection(cancellationToken);
await client.Connection.InvokeAsync("Subscribe", accountId, cancellationToken);
```

The wrapper owns its `HubConnection`; dispose the wrapper when the connection is no longer needed. Use `Connection` to register handlers with `On(...)` and call hub methods with `InvokeAsync(...)`.

## Reconnection behavior

Each connection cycle retries a failed start up to `MaxRetryAttempts` times using exponential backoff. After an unexpected close, `ReconnectIndefinitely` starts another cycle after `InitialRetryDelay`; set it to `false` to stop after the first exhausted cycle. `RetriesExhausted` is invoked when recovery stops, and `ConnectionRestored` runs after both an initial connection and a successful recovery.

Enable `StatefulReconnect` only when the server supports it. `StatefulReconnectBufferSize` controls the client-side buffered message bytes used by that mode.

Call `StopConnection` for an intentional disconnect. This suppresses closed-connection recovery until `StartConnection` is called again.

## Authentication and headers

`AccessTokenProvider` is evaluated by SignalR when it needs a bearer token. Static request headers can be supplied through `Headers`; avoid placing secrets in headers that may be sent by transports or intermediaries you do not control.
