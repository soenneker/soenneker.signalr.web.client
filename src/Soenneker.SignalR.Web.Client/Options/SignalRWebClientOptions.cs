using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Soenneker.SignalR.Web.Client.Events;

namespace Soenneker.SignalR.Web.Client.Options;

/// <summary>
/// Represents the options for configuring a SignalR web client.
/// </summary>
public sealed class SignalRWebClientOptions
{
    /// <summary>
    /// Gets or sets the URL of the SignalR hub.
    /// </summary>
    public string HubUrl { get; set; } = null!;

    /// <summary>
    /// Gets or sets the maximum number of retry attempts for reconnecting.
    /// Default value is 5.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>
    /// Gets or sets a value indicating whether recovery continues with another
    /// retry cycle after <see cref="MaxRetryAttempts"/> is reached.
    /// </summary>
    public bool ReconnectIndefinitely { get; set; } = true;

    /// <summary>
    /// Gets or sets the delay between exhausted retry cycles when
    /// <see cref="ReconnectIndefinitely"/> is enabled. Default value is 2 seconds.
    /// </summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the logger to be used for logging events.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to log connection events.
    /// </summary>
    public bool Log { get; set; } = true;

    /// <summary>
    /// Gets or sets the access token provider used for authentication.
    /// </summary>
    public Func<Task<string>>? AccessTokenProvider { get; set; }

    /// <summary>
    /// Gets or sets the custom headers to be sent with each request.
    /// </summary>
    public IDictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Gets or sets the transport type for the SignalR connection.
    /// </summary>
    public HttpTransportType TransportType { get; set; } = HttpTransportType.WebSockets;

    /// <summary>
    /// Gets or sets the interval at which the client sends keep-alive pings to the server.
    /// Default value is 15 seconds.
    /// </summary>
    public TimeSpan? KeepAliveInterval { get; set; }

    /// <summary>
    /// Gets or sets the action to be invoked when the connection is closed due to an error.
    /// </summary>
    public Action<Exception?>? ConnectionClosed { get; set; }

    /// <summary>
    /// Gets or sets the action to be invoked when the connection is reconnecting after being lost.
    /// </summary>
    public Action<Exception?>? ConnectionReconnecting { get; set; }

    /// <summary>
    /// Gets or sets the action to be invoked when the connection is successfully reconnected.
    /// </summary>
    public Action<string?>? ConnectionReconnected { get; set; }

    /// <summary>
    /// Gets or sets the asynchronous callback invoked after an initial connection
    /// or reconnection succeeds. Applications should use this callback to reload
    /// authoritative state that may have changed while disconnected.
    /// </summary>
    public Func<SignalRConnectionRestoredContext, Task>? ConnectionRestored { get; set; }

    /// <summary>
    /// Gets or sets the action to be invoked when all retry attempts have been exhausted.
    /// </summary>
    public Action? RetriesExhausted { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether stateful reconnect.
    /// </summary>
    public bool StatefulReconnect { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of serialized message bytes buffered by stateful reconnect.
    /// This value is used only when <see cref="StatefulReconnect"/> is enabled.
    /// </summary>
    public int? StatefulReconnectBufferSize { get; set; }
}
