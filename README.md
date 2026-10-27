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
using Soenneker.SignalR.Web.Client;
using Soenneker.SignalR.Web.Client.Options;

SignalRWebClient? client = null;
var options = new SignalRWebClientOptions
{
    HubUrl = "https://api.example.com/hubs/updates",
    AccessTokenProviderWithCancellation = token => GetCurrentAccessTokenAsync(token),
    ConnectionRestored = async context =>
    {
        // Groups/subscriptions belong to the new connection and must be restored first.
        await client!.Connection.InvokeAsync("Subscribe", accountId, context.CancellationToken);
        // Synchronize from persisted history/checkpoint; commit state and checkpoint atomically.
        await replay.Synchronize(accountId, context.CancellationToken);
    }
};

await using var ownedClient = client = new SignalRWebClient(options);
using var subscription = client.Connection.On("UpdatesAvailable", () =>
{
    // Signal a coalescing worker that calls the same replay.Synchronize method.
    // Do not apply event payloads independently of the snapshot/checkpoint transaction.
    RequestSynchronization();
});
await client.EnsureConnection(cancellationToken);

```

The wrapper owns its `HubConnection`; dispose the wrapper when the connection is no longer needed. Use `Connection` to register handlers with `On(...)` and call hub methods with `InvokeAsync(...)`.

## Reconnection behavior

Each connection cycle retries a failed start up to `MaxRetryAttempts` times using exponential backoff. `RetryDelayProvider` can replace that schedule. Automatic reconnect uses the same policy. After an exhausted cycle, `ReconnectIndefinitely` continues recovery in the background after `InitialRetryDelay`; set it to `false` to stop. `ConnectionRestored` runs after both an initial connection and successful recovery; callback failures are retried before application readiness is reported. `StartConnection` may return after an exhausted cycle while offline; use `EnsureConnection` before work that requires restoration.

SignalR transport negotiation is enabled by default so WebSockets can fall back when a proxy or mobile network blocks them. Set `TransportType` only when a specific transport is required. `ServerTimeout` and `KeepAliveInterval` can be overridden when the server uses matching connection settings.

Enable `StatefulReconnect` only when the server supports it. `StatefulReconnectBufferSize` controls the client-side buffered message bytes used by that mode.

Call `StopConnection` for an intentional disconnect. This suppresses closed-connection recovery until `StartConnection` is called again.

## Authentication and headers

`AccessTokenProvider` is evaluated by SignalR when it needs a bearer token. Static request headers can be supplied through `Headers`; avoid placing secrets in headers that may be sent by transports or intermediaries you do not control.

## Readiness, callback bounds, and diagnostics

`GetStatus` reports transport state separately from application readiness, the last failure, scheduled retry count (`RetryCount`), observed failure count (`FailureCount`), next retry time, last restoration, and outstanding/abandoned callbacks. Readiness is a point-in-time observation, not a guarantee that a later invocation succeeds.

`MaxOutstandingCallbacks` defaults to two for token acquisition and independently for restoration. A timed-out callback retains its slot until its task finishes. At capacity, callback execution pauses; status reports `WaitingForCallback`. Stop and disposal cancel library waits without waiting for abandoned application code. The cap survives stop/resume. Callbacks must honor cancellation; detached work started by a callback is outside this bound.

The `Soenneker.SignalR.Web.Client` meter exposes `signalr.client.active`, `signalr.client.failures`, `signalr.client.callbacks.abandoned`, `signalr.client.outage.duration`, and `signalr.client.restoration.duration`. Durations are seconds; labels contain no connection IDs or credentials. The supplied logger also receives underlying SignalR logs, preserving the source category in each message. Configure logging levels and secret redaction in the host.

## Optional durable replay

`Soenneker.SignalR.Dtos.Envelope.Replay` supplies versioned event metadata, replay/snapshot contracts, and `SignalRReplaySynchronizer`. Implement `ISignalRReplayConsumer` against an atomic application-state/checkpoint transaction and `ISignalRReplayJournal` against durable server history. Use `ISignalRCommandHandler` for server-side command idempotency. These contracts deliberately require application storage: the connection wrapper does not persist business data or promise exactly-once external effects.

Use the same synchronization path for restoration and live notifications. A snapshot is committed only if its expected checkpoint is still current; competing writes fail instead of overwriting newer state. Retention gaps request a fresh authoritative snapshot. Apply deduplication and authorization at the storage boundary, and retain command identities for the full retry horizon.
