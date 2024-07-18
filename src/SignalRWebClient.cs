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

namespace Soenneker.SignalR.Web.Client;

///<inheritdoc cref="ISignalRWebClient"/>
public class SignalRWebClient : ISignalRWebClient
{
    public HubConnection Connection { get; }

    private readonly AsyncRetryPolicy _retryPolicy;
    private bool _disposed;
    private readonly SignalRWebClientOptions _options;

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

        Connection = hubConnectionBuilder.Build();

        Connection.KeepAliveInterval = _options.KeepAliveInterval;

        Connection.Closed += async error =>
        {
            if (_options.Log)
                _options.Logger?.LogError(error, "Connection closed due to an error. Waiting to reconnect to hub ({HubUrl})...", _options.HubUrl);

            _options.ConnectionClosed?.Invoke(error);
            await HandleReconnect().NoSync();
        };

        Connection.Reconnecting += error =>
        {
            if (_options.Log)
                _options.Logger?.LogWarning(error, "Connection lost due to an error. Reconnecting to hub ({HubUrl})...", _options.HubUrl);

            _options.ConnectionReconnecting?.Invoke(error);
            return Task.CompletedTask;
        };

        Connection.Reconnected += connectionId =>
        {
            if (_options.Log)
                _options.Logger?.LogInformation("Reconnected to hub ({HubUrl}). Connection ID: {ConnectionId}", _options.HubUrl, connectionId);

            _options.ConnectionReconnected?.Invoke(connectionId);
            return Task.CompletedTask;
        };

        // Define the retry policy using Polly
        _retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(_options.MaxRetryAttempts, attempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                (exception, timeSpan, attempt, context) =>
                {
                    if (_options.Log)
                        _options.Logger?.LogWarning(exception, "SignalR connection attempt {Attempt} failed to hub ({HubUrl}). Waiting {TimeSpan} before next retry.", attempt, _options.HubUrl,
                            timeSpan);
                });
    }

    private async ValueTask HandleReconnect(CancellationToken cancellationToken = default)
    {
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
    }

    public async ValueTask StartConnection(CancellationToken cancellationToken = default)
    {
        try
        {
            await _retryPolicy.ExecuteAsync(async () =>
            {
                await Connection.StartAsync(cancellationToken).NoSync();

                if (_options.Log)
                    _options.Logger?.LogInformation("SignalR Connected to hub ({HubUrl}).", _options.HubUrl);
            }).NoSync();
        }
        catch (Exception ex)
        {
            if (_options.Log)
                _options.Logger?.LogError(ex, "Max retry attempts reached during initial connection to hub ({HubUrl}). Stopping retries.", _options.HubUrl);

            _options.RetriesExhausted?.Invoke();
        }
    }

    public async ValueTask StopConnection(CancellationToken cancellationToken = default)
    {
        await Connection.StopAsync(cancellationToken).NoSync();

        if (_options.Log)
            _options.Logger?.LogInformation("SignalR Disconnected from hub ({HubUrl}).", _options.HubUrl);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            if (_options.Log)
                _options.Logger?.LogInformation("Disposing SignalR connection to hub ({HubUrl}).", _options.HubUrl);

            await Connection.DisposeAsync().NoSync();
            _disposed = true;

            GC.SuppressFinalize(this);
        }
    }
}