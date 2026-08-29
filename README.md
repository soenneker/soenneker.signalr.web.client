[![](https://img.shields.io/nuget/v/soenneker.signalr.web.client.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.signalr.web.client/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.signalr.web.client/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.signalr.web.client/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.signalr.web.client.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.signalr.web.client/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.signalr.web.client/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.signalr.web.client/actions/workflows/codeql.yml)

# Soenneker.SignalR.Web.Client

A resilient and dependable .NET SignalR web client.

## Install

```bash
dotnet add package Soenneker.SignalR.Web.Client
```

## Quick start

```csharp
using Soenneker.SignalR.Web.Client.Abstract;

ISignalRWebClient signalRWebClient = /* resolve from DI */;
await signalRWebClient.StartConnection(default);
```

Starts the SignalR connection asynchronously.

## What you get

- `ISignalRWebClient` — A resilient and dependable .NET SignalR web client.
- `SignalRConnectionRestoredContext` — Describes a SignalR connection that has been established and is ready for application-level synchronization.
- `SignalRWebClientOptions` — Represents the options for configuring a SignalR web client.

## API at a glance

| API | What it does | Result / important behavior |
| --- | --- | --- |
| `ISignalRWebClient.Connection` | Gets connection. | Gets connection. |
| `ISignalRWebClient.StartConnection(cancellationToken)` | Starts the SignalR connection asynchronously. | A task that completes after the Signal R Web Client has started. |
| `ISignalRWebClient.StopConnection(cancellationToken)` | Stops the SignalR connection asynchronously. | A task that completes after the Signal R Web Client has stopped. |
| `SignalRConnectionRestoredContext.ConnectionId` | Gets the current connection identifier, when the server provides one. | Gets the current connection identifier, when the server provides one. |
| `SignalRConnectionRestoredContext.IsReconnect` | Gets a value indicating whether this connection restored a previously established session. | Gets a value indicating whether this connection restored a previously established session. |
| `SignalRWebClientOptions.HubUrl` | Gets or sets the URL of the SignalR hub. | Gets or sets the URL of the SignalR hub. |
| `SignalRWebClientOptions.MaxRetryAttempts` | Gets or sets the maximum number of retry attempts for reconnecting. Default value is 5. | Gets or sets the maximum number of retry attempts for reconnecting. Default value is 5. |
| `SignalRWebClientOptions.ReconnectIndefinitely` | Gets or sets a value indicating whether recovery continues with another retry cycle after `MaxRetryAttempts` is reached. | Gets or sets a value indicating whether recovery continues with another retry cycle after `MaxRetryAttempts` is reached. |
| `SignalRWebClientOptions.InitialRetryDelay` | Gets or sets the initial delay before the first retry attempt. Default value is 2 seconds. | Gets or sets the initial delay before the first retry attempt. Default value is 2 seconds. |
| `SignalRWebClientOptions.Logger` | Gets or sets the logger to be used for logging events. | Gets or sets the logger to be used for logging events. |
| `SignalRWebClientOptions.Log` | Gets or sets a value indicating whether to log connection events. | Gets or sets a value indicating whether to log connection events. |
| `SignalRWebClientOptions.AccessTokenProvider` | Gets or sets the access token provider used for authentication. | Gets or sets the access token provider used for authentication. |
| `SignalRWebClientOptions.Headers` | Gets or sets the custom headers to be sent with each request. | Gets or sets the custom headers to be sent with each request. |
| `SignalRWebClientOptions.TransportType` | Gets or sets the transport type for the SignalR connection. | Gets or sets the transport type for the SignalR connection. |
| `SignalRWebClientOptions.KeepAliveInterval` | Gets or sets the interval at which the client sends keep-alive pings to the server. Default value is 15 seconds. | Gets or sets the interval at which the client sends keep-alive pings to the server. Default value is 15 seconds. |
| `SignalRWebClientOptions.ConnectionClosed` | Gets or sets the action to be invoked when the connection is closed due to an error. | Gets or sets the action to be invoked when the connection is closed due to an error. |
| `SignalRWebClientOptions.ConnectionReconnecting` | Gets or sets the action to be invoked when the connection is reconnecting after being lost. | Gets or sets the action to be invoked when the connection is reconnecting after being lost. |
| `SignalRWebClientOptions.ConnectionReconnected` | Gets or sets the action to be invoked when the connection is successfully reconnected. | Gets or sets the action to be invoked when the connection is successfully reconnected. |

## Practical notes

- Cancellation stops pending work; it does not undo work that has already completed.
- Dispose instances you own when their scope ends so held resources can be released.
