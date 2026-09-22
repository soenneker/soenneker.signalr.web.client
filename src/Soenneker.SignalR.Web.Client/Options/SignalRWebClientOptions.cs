using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.SignalR.Web.Client.Events;

namespace Soenneker.SignalR.Web.Client.Options;

/// <summary>
/// Represents the options for configuring a SignalR web client.
/// Options and headers are copied at construction; later changes do not reconfigure an existing client.
/// </summary>
public sealed class SignalRWebClientOptions
{
    /// <summary>
    /// Gets or sets the URL of the SignalR hub.
    /// </summary>
    public string HubUrl { get; set; } = null!;

    /// <summary>
    /// Gets or sets the maximum number of retries after the initial connection attempt and during automatic reconnect.
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
    /// Values below 100 milliseconds use 100 milliseconds before jitter to prevent a tight retry loop.
    /// </summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the deadline for a manually managed connection attempt. Default is 30 seconds.
    /// SignalR controls automatic attempts; <see cref="AutomaticReconnectTimeout"/> bounds their entire cycle.
    /// </summary>
    public TimeSpan ConnectionAttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum duration of an automatic reconnect cycle, including delays and
    /// transport attempts. Default is two minutes. A stalled cycle is stopped before manual recovery
    /// continues, or ends recovery when <see cref="ReconnectIndefinitely"/> is false.
    /// </summary>
    public TimeSpan AutomaticReconnectTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the minimum delay after HTTP 401 or 403 failures. Default is 30 seconds.
    /// Recovery still obtains fresh credentials on subsequent attempts. Call ResumeConnection after
    /// credentials change to retry promptly. Custom retry delays cannot shorten this delay.
    /// </summary>
    public TimeSpan AuthenticationRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the gap in execution that indicates possible host suspension. Default is one minute;
    /// null disables detection. A background check runs every five seconds while recovery is active.
    /// When execution resumes after a longer gap, the client replaces the potentially stale connection.
    /// This uses UTC elapsed time, so large forward clock adjustments can also trigger recovery.
    /// </summary>
    public TimeSpan? ResumeDetectionThreshold { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the clock used for suspension detection and automatic reconnect deadlines.
    /// Default is <see cref="System.TimeProvider.System"/>.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Gets or sets the deadline for obtaining an access token, including during automatic reconnect.
    /// Default is 30 seconds. Providers must return a task promptly.
    /// </summary>
    public TimeSpan AccessTokenTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the deadline for each restoration callback. Default is 30 seconds.
    /// Timed-out callbacks are cancelled and retried; callbacks must return a task promptly and honor cancellation.
    /// </summary>
    public TimeSpan RestorationTimeout { get; set; } = TimeSpan.FromSeconds(30);

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
    /// Gets or sets a token provider that supports cancellation on timeout, stop, or disposal.
    /// Takes precedence over <see cref="AccessTokenProvider"/>. Fetch current credentials on each call.
    /// </summary>
    public Func<CancellationToken, Task<string>>? AccessTokenProviderWithCancellation { get; set; }

    /// <summary>
    /// Gets or sets the custom headers to be sent with each request.
    /// </summary>
    public IDictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Gets or sets a factory that wraps the HTTP handler used for negotiation and HTTP transports.
    /// Browser clients can use a delegating handler to include cookies in cross-origin requests.
    /// </summary>
    public Func<HttpMessageHandler, HttpMessageHandler>? HttpMessageHandlerFactory { get; set; }

    /// <summary>
    /// Gets or sets the transport to require, or null to let SignalR negotiate the best available transport.
    /// </summary>
    public HttpTransportType? TransportType { get; set; }

    /// <summary>
    /// Gets or sets the interval at which the client sends keep-alive pings to the server.
    /// Default value is 15 seconds.
    /// </summary>
    public TimeSpan? KeepAliveInterval { get; set; }

    /// <summary>
    /// Gets or sets how long the client waits without receiving a server message before timing out.
    /// Null uses SignalR's default.
    /// </summary>
    public TimeSpan? ServerTimeout { get; set; }

    /// <summary>
    /// Gets or sets a retry-delay provider whose argument is the zero-based retry count.
    /// The default retries immediately, then uses exponential backoff capped at 30 seconds with jitter.
    /// Custom providers must return a delay between zero and one day and are not jittered.
    /// A throwing provider or an invalid delay is logged and replaced by the default schedule.
    /// Providers must return promptly.
    /// </summary>
    public Func<long, TimeSpan>? RetryDelayProvider { get; set; }

    /// <summary>
    /// Gets or sets whether default retry and exhausted-cycle delays are randomly spread between
    /// 50% and 100% of their scheduled value to reduce reconnect bursts. Default is true.
    /// Does not change delays supplied by <see cref="RetryDelayProvider"/>.
    /// </summary>
    public bool UseRetryJitter { get; set; } = true;

    internal TimeSpan GetRetryDelay(long retryCount, Exception? error = null)
    {
        TimeSpan delay;
        try
        {
            delay = RetryDelayProvider?.Invoke(retryCount) ?? DefaultRetryDelay(retryCount);
            if (delay < TimeSpan.Zero || delay > TimeSpan.FromDays(1))
                throw new InvalidOperationException("Retry delays must be between zero and one day.");
        }
        catch (Exception ex)
        {
            WriteLog(LogLevel.Warning, ex, "RetryDelayProvider failed; using the default retry schedule.");
            delay = DefaultRetryDelay(retryCount);
        }
        if (IsAuthenticationFailure(error) && delay < AuthenticationRetryDelay) delay = AuthenticationRetryDelay;
        return delay;
    }

    private TimeSpan DefaultRetryDelay(long retryCount) => Jitter(retryCount <= 0
        ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(retryCount, 5)))));

    internal static bool IsAuthenticationFailure(Exception? error) => error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsAuthenticationFailure),
        { InnerException: { } inner } => IsAuthenticationFailure(inner),
        _ => false
    };

    internal void WriteLog(LogLevel level, Exception? exception, string message)
    {
        try { if (Log) Logger?.Log(level, exception, "{Message}", message); }
        catch (Exception) { }
    }

    internal TimeSpan Jitter(TimeSpan delay) => UseRetryJitter
        ? TimeSpan.FromTicks((long)(delay.Ticks * (0.5 + Random.Shared.NextDouble() * 0.5))) : delay;

    internal SignalRWebClientOptions Snapshot()
    {
        var copy = (SignalRWebClientOptions)MemberwiseClone();
        if (Headers != null) copy.Headers = new Dictionary<string, string>(Headers);
        return copy;
    }

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
    /// Failed callbacks are retried using the configured retry budget and delays, so
    /// synchronization must be idempotent. Honor the context's cancellation token:
    /// stop, disposal, or a newer connection cancels the current restoration.
    /// A callback that ignores cancellation may finish after it has been superseded.
    /// </summary>
    public Func<SignalRConnectionRestoredContext, Task>? ConnectionRestored { get; set; }

    /// <summary>
    /// Gets or sets the action to be invoked when all retry attempts have been exhausted.
    /// </summary>
    public Action? RetriesExhausted { get; set; }

    /// <summary>
    /// Gets or sets a notification for failed connection attempts or connection loss. The exception
    /// can be inspected for authentication or transport errors. Notifications may repeat during an outage.
    /// The callback must return promptly; exceptions are logged without stopping recovery.
    /// </summary>
    public Action<Exception>? ConnectionError { get; set; }

    internal void ReportConnectionError(Exception error)
    {
        try { ConnectionError?.Invoke(error); }
        catch (Exception ex) { WriteLog(LogLevel.Error, ex, "ConnectionError callback failed."); }
    }

    /// <summary>
    /// Gets or sets whether to request stateful reconnect. The server must also support and enable it.
    /// Unsupported transports continue using ordinary reconnect and restoration.
    /// </summary>
    public bool StatefulReconnect { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of serialized message bytes buffered by stateful reconnect.
    /// This value is used only when <see cref="StatefulReconnect"/> is enabled.
    /// </summary>
    public int? StatefulReconnectBufferSize { get; set; }
}
