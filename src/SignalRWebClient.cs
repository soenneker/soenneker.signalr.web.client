using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.SignalR.Web.Client.Abstract;
using Soenneker.SignalR.Web.Client.Options;
using Soenneker.Utils.Random;

namespace Soenneker.SignalR.Web.Client;

///<inheritdoc cref="ISignalRWebClient"/>
public class SignalRWebClient : ISignalRWebClient
{
    public HubConnection Connection { get; }

    private readonly AsyncRetryPolicy _retryPolicy;
    private bool _disposed;
    private readonly SignalRWebClientOptions _options;

    private int _reconnecting; // 0 = false, 1 = true

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalRWebClient"/> class.
    /// </summary>
    /// <param name="options">The options used to configure the SignalR web client.</param>
    public SignalRWebClient(SignalRWebClientOptions options)
    {
        _options = options;

        IHubConnectionBuilder hubConnectionBuilder = new HubConnectionBuilder()
            .WithUrl(_options.HubUrl, httpConnectionOptions =>
            {
                if (_options.AccessTokenProvider != null)
                    httpConnectionOptions.AccessTokenProvider = _options.AccessTokenProvider!;

                if (_options.Headers != null)
                {
                    foreach (KeyValuePair<string, string> header in _options.Headers)
                    {
                        httpConnectionOptions.Headers.Add(header.Key, header.Value);
                    }
                }

                httpConnectionOptions.Transports = _options.TransportType;
            });

        if (options.StatefulReconnect)
            hubConnectionBuilder.WithStatefulReconnect();

        Connection = hubConnectionBuilder.Build();

        if (_options.KeepAliveInterval != null)
            Connection.KeepAliveInterval = _options.KeepAliveInterval.Value;

        Connection.Closed += OnConnectionClosed;
        Connection.Reconnecting += OnConnectionReconnecting;
        Connection.Reconnected += OnConnectionReconnected;

        // Define the retry policy using Polly
        _retryPolicy = Policy
                       .Handle<Exception>(ex =>
                       {
                           _options.Logger?.LogError(ex, "SignalR retry handler caught exception when connecting to hub ({HubUrl})", _options.HubUrl);
                           return true; // always retry on any exception
                       })
                       .WaitAndRetryAsync(_options.MaxRetryAttempts,
                           attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt) + RandomUtil.NextDouble()),
                           async (exception, timeSpan, attempt, context) =>
                           {
                               if (Connection.State == HubConnectionState.Connected)
                               {
                                   _options.Logger?.LogInformation("SignalR connected during retry attempt {Attempt}. Skipping further retries.", attempt);
                                   return; // stop retrying, already connected
                               }

                               if (_options.Log)
                                   _options.Logger?.LogWarning(exception,
                                       "SignalR connection attempt {Attempt} failed to hub ({HubUrl}). Waiting {TimeSpan} before next retry.",
                                       attempt, _options.HubUrl, timeSpan);

                               await Task.CompletedTask;
                           });
    }

    private async Task OnConnectionClosed(Exception? error)
    {
        if (_options.Log)
            _options.Logger?.LogError(error, "Connection closed due to an error. Waiting to reconnect to hub ({HubUrl})...", _options.HubUrl);

        _options.ConnectionClosed?.Invoke(error);
        await HandleReconnect().NoSync();
    }

    private Task OnConnectionReconnecting(Exception? error)
    {
        if (_options.Log)
            _options.Logger?.LogWarning(error, "Connection lost due to an error. Reconnecting to hub ({HubUrl})...", _options.HubUrl);

        _options.ConnectionReconnecting?.Invoke(error);
        return Task.CompletedTask;
    }

    private Task OnConnectionReconnected(string? connectionId)
    {
        if (_options.Log)
            _options.Logger?.LogInformation("Reconnected to hub ({HubUrl}). Connection ID: {ConnectionId}", _options.HubUrl, connectionId);

        _options.ConnectionReconnected?.Invoke(connectionId);
        return Task.CompletedTask;
    }

    private async ValueTask HandleReconnect(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _reconnecting, 1) == 1)
            return; // already reconnecting

        try
        {
            await _retryPolicy.ExecuteAsync(async () =>
            {
                await Connection.StartAsync(cancellationToken).NoSync();

                if (_options.Log)
                    _options.Logger?.LogInformation("SignalR Reconnected to hub ({HubUrl}).", _options.HubUrl);
            }).NoSync();
        }
        catch (Exception ex)
        {
            if (_options.Log)
                _options.Logger?.LogError(ex, "Max retry attempts reached. Stopping retries to hub ({HubUrl}).", _options.HubUrl);

            _options.RetriesExhausted?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    public async ValueTask StartConnection(CancellationToken cancellationToken = default)
    {
        try
        {
            await _retryPolicy.ExecuteAsync(async () =>
            {
                await Connection.StartAsync(cancellationToken).NoSync();

                // Do not trust success here immediately
            }).NoSync();

            if (Connection.State == HubConnectionState.Connected)
            {
                _options.Logger?.LogInformation("SignalR Connected to hub ({HubUrl}).", _options.HubUrl);
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
        if (_options.Log)
            _options.Logger?.LogInformation("SignalR disconnecting from hub ({HubUrl})...", _options.HubUrl);

        return Connection.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;

            if (_options.Log)
                _options.Logger?.LogInformation("Disposing SignalR connection to hub ({HubUrl})...", _options.HubUrl);

            Connection.Closed -= OnConnectionClosed;
            Connection.Reconnected -= OnConnectionReconnected;
            Connection.Reconnecting -= OnConnectionReconnecting;

            await StopConnection().NoSync();
            await Connection.DisposeAsync().NoSync();

            GC.SuppressFinalize(this);
        }
    }
}