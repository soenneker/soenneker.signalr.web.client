using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Soenneker.Atomics.ValueBools;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.SignalR.Web.Client.Abstract;
using Soenneker.SignalR.Web.Client.Options;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Utils.Random;

namespace Soenneker.SignalR.Web.Client;

/// <inheritdoc cref="ISignalRWebClient"/>
public sealed class SignalRWebClient : ISignalRWebClient
{
    public HubConnection Connection { get; }

    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly SignalRWebClientOptions _options;

    private ValueAtomicBool _reconnecting = new(false);
    private ValueAtomicBool _disposed = new(false);

    public SignalRWebClient(SignalRWebClientOptions options)
    {
        _options = options;

        IHubConnectionBuilder hubConnectionBuilder = new HubConnectionBuilder().WithUrl(_options.HubUrl, httpConnectionOptions =>
        {
            if (_options.AccessTokenProvider is not null)
                httpConnectionOptions.AccessTokenProvider = _options.AccessTokenProvider;

            if (_options.Headers is not null)
            {
                foreach (KeyValuePair<string, string> header in _options.Headers)
                    httpConnectionOptions.Headers.Add(header.Key, header.Value);
            }

            httpConnectionOptions.Transports = _options.TransportType;
        });

        if (_options.StatefulReconnect)
            hubConnectionBuilder.WithStatefulReconnect();

        Connection = hubConnectionBuilder.Build();

        if (_options.KeepAliveInterval is not null)
            Connection.KeepAliveInterval = _options.KeepAliveInterval.Value;

        Connection.Closed += OnConnectionClosed;
        Connection.Reconnecting += OnConnectionReconnecting;
        Connection.Reconnected += OnConnectionReconnected;

        _retryPolicy = Policy.Handle<Exception>(ex =>
                             {
                                 _options.Logger?.LogError(ex, "SignalR retry handler caught exception when connecting to hub ({HubUrl})", _options.HubUrl);
                                 return true; // always retry on any exception
                             })
                             .WaitAndRetryAsync(_options.MaxRetryAttempts,
                                 // exponential backoff with jitter, avoiding Math.Pow
                                 attempt =>
                                 {
                                     // Polly attempts are 1-based. Cap shift to avoid overflow / absurd delays.
                                     int shift = attempt <= 30 ? attempt : 30;
                                     double backoffSeconds = 1 << shift; // 2,4,8,...
                                     double jitter = RandomUtil.NextDouble(); // [0,1)
                                     return TimeSpan.FromSeconds(backoffSeconds + jitter);
                                 },
                                 // non-async callback to avoid state machine allocation
                                 (exception, timeSpan, attempt, context) =>
                                 {
                                     if (Connection.State == HubConnectionState.Connected)
                                     {
                                         _options.Logger?.LogInformation("SignalR connected during retry attempt {Attempt}. Skipping further retries.",
                                             attempt);
                                         return Task.CompletedTask;
                                     }

                                     if (_options.Log)
                                     {
                                         _options.Logger?.LogWarning(exception,
                                             "SignalR connection attempt {Attempt} failed to hub ({HubUrl}). Waiting {TimeSpan} before next retry.", attempt,
                                             _options.HubUrl, timeSpan);
                                     }

                                     return Task.CompletedTask;
                                 });
    }

    private async Task OnConnectionClosed(Exception? error)
    {
        if (_disposed.Value)
            return;

        if (_options.Log)
            _options.Logger?.LogError(error, "Connection closed due to an error. Waiting to reconnect to hub ({HubUrl})...", _options.HubUrl);

        _options.ConnectionClosed?.Invoke(error);

        await HandleReconnect()
            .NoSync();
    }

    private Task OnConnectionReconnecting(Exception? error)
    {
        if (_disposed.Value)
            return Task.CompletedTask;

        if (_options.Log)
            _options.Logger?.LogWarning(error, "Connection lost due to an error. Reconnecting to hub ({HubUrl})...", _options.HubUrl);

        _options.ConnectionReconnecting?.Invoke(error);
        return Task.CompletedTask;
    }

    private Task OnConnectionReconnected(string? connectionId)
    {
        if (_disposed.Value)
            return Task.CompletedTask;

        if (_options.Log)
            _options.Logger?.LogInformation("Reconnected to hub ({HubUrl}). Connection ID: {ConnectionId}", _options.HubUrl, connectionId);

        _options.ConnectionReconnected?.Invoke(connectionId);
        return Task.CompletedTask;
    }

    private async ValueTask HandleReconnect(CancellationToken cancellationToken = default)
    {
        if (_disposed.Value)
            return;

        if (!_reconnecting.TrySetTrue())
            return; // already reconnecting

        try
        {
            // Use overload that passes CancellationToken to reduce closure capture.
            await _retryPolicy.ExecuteAsync(async ct =>
                              {
                                  if (_disposed.Value)
                                      return;

                                  await Connection.StartAsync(ct)
                                                  .NoSync();

                                  if (_options.Log)
                                      _options.Logger?.LogInformation("SignalR reconnected to hub ({HubUrl}).", _options.HubUrl);
                              }, cancellationToken)
                              .NoSync();
        }
        catch (Exception ex)
        {
            if (_options.Log)
                _options.Logger?.LogError(ex, "Max retry attempts reached. Stopping retries to hub ({HubUrl}).", _options.HubUrl);

            _options.RetriesExhausted?.Invoke();
        }
        finally
        {
            _reconnecting.Value = false;
        }
    }

    public async ValueTask StartConnection(CancellationToken cancellationToken = default)
    {
        if (_disposed.Value)
            return;

        try
        {
            await _retryPolicy.ExecuteAsync(async ct =>
                              {
                                  if (_disposed.Value)
                                      return;

                                  await Connection.StartAsync(ct)
                                                  .NoSync();
                                  // do not trust success here immediately
                              }, cancellationToken)
                              .NoSync();

            if (_disposed.Value)
                return;

            if (Connection.State == HubConnectionState.Connected)
            {
                _options.Logger?.LogInformation("SignalR connected to hub ({HubUrl}).", _options.HubUrl);
            }
            else
            {
                _options.Logger?.LogWarning("SignalR connection attempt finished but state is {State}.", Connection.State);
            }
        }
        catch (Exception ex)
        {
            _options.Logger?.LogError(ex, "Max retry attempts reached during initial connection to hub ({HubUrl}). Stopping retries.", _options.HubUrl);
            _options.RetriesExhausted?.Invoke();
        }
    }

    public Task StopConnection(CancellationToken cancellationToken = default)
    {
        if (_disposed.Value)
            return Task.CompletedTask;

        if (_options.Log)
            _options.Logger?.LogInformation("SignalR disconnecting from hub ({HubUrl})...", _options.HubUrl);

        return Connection.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed.TrySetTrue())
            return; // already disposed

        if (_options.Log)
            _options.Logger?.LogInformation("Disposing SignalR connection to hub ({HubUrl})...", _options.HubUrl);

        Connection.Closed -= OnConnectionClosed;
        Connection.Reconnected -= OnConnectionReconnected;
        Connection.Reconnecting -= OnConnectionReconnecting;

        await StopConnection()
            .NoSync();
        await Connection.DisposeAsync()
                        .NoSync();
    }
}