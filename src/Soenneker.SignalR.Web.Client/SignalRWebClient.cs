using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Asyncs.Locks;
using Soenneker.Atomics.ValueBools;
using Soenneker.SignalR.Web.Client.Abstract;
using Soenneker.SignalR.Web.Client.Events;
using Soenneker.SignalR.Web.Client.Options;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client;

public sealed class SignalRWebClient : ISignalRWebClient
{
    public HubConnection Connection { get; }
    private readonly SignalRWebClientOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AsyncLock _connectionLock = new();
    private readonly AsyncLock _reconnectLock = new();
    private CancellationTokenSource? _reconnectCancellation;
    private Task? _reconnectTask;
    private ValueAtomicBool _stopping;
    private ValueAtomicBool _disposed;

    public SignalRWebClient(SignalRWebClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.HubUrl)) throw new ArgumentException("A hub URL is required.", nameof(options));
        if (options.MaxRetryAttempts is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(options.MaxRetryAttempts));
        if (options.InitialRetryDelay < TimeSpan.Zero || options.InitialRetryDelay > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(options.InitialRetryDelay));
        if (options.KeepAliveInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.KeepAliveInterval));
        if (options.ServerTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.ServerTimeout));
        if (options.StatefulReconnectBufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(options.StatefulReconnectBufferSize));
        _options = options;

        IHubConnectionBuilder builder = new HubConnectionBuilder().WithUrl(options.HubUrl, http =>
        {
            if (options.AccessTokenProvider is not null)
                http.AccessTokenProvider = async () => await options.AccessTokenProvider().ConfigureAwait(false);
            if (options.Headers is not null)
                foreach (KeyValuePair<string, string> header in options.Headers) http.Headers.Add(header.Key, header.Value);
            if (options.TransportType is { } transport) http.Transports = transport;
        });
        if (options.StatefulReconnect)
        {
            builder.WithStatefulReconnect();
            if (options.StatefulReconnectBufferSize is { } size)
                builder.Services.Configure<HubConnectionOptions>(configured => configured.StatefulReconnectBufferSize = size);
        }
        builder.WithAutomaticReconnect(new ConfiguredRetryPolicy(options));
        Connection = builder.Build();
        if (options.KeepAliveInterval is { } keepAlive) Connection.KeepAliveInterval = keepAlive;
        if (options.ServerTimeout is { } serverTimeout) Connection.ServerTimeout = serverTimeout;
        Connection.Closed += OnConnectionClosed;
        Connection.Reconnecting += OnConnectionReconnecting;
        Connection.Reconnected += OnConnectionReconnected;
    }

    public async ValueTask StartConnection(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _stopping.Value = false;
        Task? previousReconnect = await CancelReconnect().ConfigureAwait(false);
        if (previousReconnect is not null) await IgnoreCancellation(previousReconnect).ConfigureAwait(false);
        if (await TryConnectCycle(cancellationToken).ConfigureAwait(false))
        {
            await NotifyConnectionRestored(Connection.ConnectionId, false, cancellationToken).ConfigureAwait(false);
            return;
        }
        SafeInvoke(_options.RetriesExhausted, "RetriesExhausted");
        if (_options.ReconnectIndefinitely && !_stopping.Value) await EnsureReconnectLoop().ConfigureAwait(false);
    }

    public async Task StopConnection(CancellationToken cancellationToken = default)
    {
        if (_disposed.Value) return;
        _stopping.Value = true;
        Task? reconnect = await CancelReconnect().ConfigureAwait(false);
        if (reconnect is not null) await IgnoreCancellation(reconnect).ConfigureAwait(false);
        using (await _connectionLock.Lock(cancellationToken).ConfigureAwait(false))
        {
            if (Connection.State != HubConnectionState.Disconnected) await Connection.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnConnectionClosed(Exception? error)
    {
        if (_disposed.Value || _stopping.Value) return;
        Log(LogLevel.Error, error, "Connection closed. Recovery will continue.");
        SafeInvoke(_options.ConnectionClosed, error, "ConnectionClosed");
        if (_options.ReconnectIndefinitely) await EnsureReconnectLoop().ConfigureAwait(false);
        else SafeInvoke(_options.RetriesExhausted, "RetriesExhausted");
    }

    private Task OnConnectionReconnecting(Exception? error)
    {
        if (!_disposed.Value)
        {
            Log(LogLevel.Warning, error, "Connection lost. Reconnecting.");
            SafeInvoke(_options.ConnectionReconnecting, error, "ConnectionReconnecting");
        }
        return Task.CompletedTask;
    }

    private async Task OnConnectionReconnected(string? connectionId)
    {
        if (_disposed.Value || _stopping.Value) return;
        SafeInvoke(_options.ConnectionReconnected, connectionId, "ConnectionReconnected");
        try { await NotifyConnectionRestored(connectionId, true, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async ValueTask EnsureReconnectLoop()
    {
        using (await _reconnectLock.Lock(_lifetime.Token).ConfigureAwait(false))
        {
            if (_reconnectTask is { IsCompleted: false } || _stopping.Value || _disposed.Value) return;
            _reconnectCancellation?.Dispose();
            _reconnectCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _reconnectTask = ReconnectLoop(_reconnectCancellation.Token);
        }
    }

    private async Task ReconnectLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!_stopping.Value && !cancellationToken.IsCancellationRequested)
            {
                if (await TryConnectCycle(cancellationToken).ConfigureAwait(false))
                {
                    await NotifyConnectionRestored(Connection.ConnectionId, true, cancellationToken).ConfigureAwait(false);
                    return;
                }
                SafeInvoke(_options.RetriesExhausted, "RetriesExhausted");
                if (!_options.ReconnectIndefinitely) return;
                await Task.Delay(_options.InitialRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task<bool> TryConnectCycle(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= _options.MaxRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping.Value || _disposed.Value) return false;
            if (Connection.State == HubConnectionState.Connected) return true;
            if (Connection.State != HubConnectionState.Disconnected)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (attempt > 0) await Task.Delay(_options.GetRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
            using (await _connectionLock.Lock(cancellationToken).ConfigureAwait(false))
            {
                if (Connection.State == HubConnectionState.Connected) return true;
                if (Connection.State != HubConnectionState.Disconnected) continue;
                try
                {
                    await Connection.StartAsync(cancellationToken).ConfigureAwait(false);
                    if (Connection.State == HubConnectionState.Connected) return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log(LogLevel.Warning, ex, $"Connection attempt {attempt + 1} failed."); }
            }
        }
        return false;
    }

    private async Task NotifyConnectionRestored(string? connectionId, bool isReconnect, CancellationToken cancellationToken)
    {
        if (_options.ConnectionRestored is not { } callback) return;
        cancellationToken.ThrowIfCancellationRequested();
        try { await callback(new SignalRConnectionRestoredContext(connectionId, isReconnect)).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log(LogLevel.Error, ex, "ConnectionRestored callback failed."); }
    }

    private async ValueTask<Task?> CancelReconnect()
    {
        using (await _reconnectLock.Lock().ConfigureAwait(false))
        {
            _reconnectCancellation?.Cancel();
            return _reconnectTask;
        }
    }

    private void SafeInvoke(Action? callback, string name)
    {
        try { callback?.Invoke(); } catch (Exception ex) { Log(LogLevel.Error, ex, $"{name} callback failed."); }
    }

    private void SafeInvoke<T>(Action<T>? callback, T value, string name)
    {
        try { callback?.Invoke(value); } catch (Exception ex) { Log(LogLevel.Error, ex, $"{name} callback failed."); }
    }

    private void Log(LogLevel level, Exception? exception, string message)
    {
        if (_options.Log) _options.Logger?.Log(level, exception, "{Message} Hub: {HubUrl}", message, _options.HubUrl);
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed.Value, this);

    public async ValueTask DisposeAsync()
    {
        if (!_disposed.TrySetTrue()) return;
        _stopping.Value = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task? reconnect = await CancelReconnect().ConfigureAwait(false);
        if (reconnect is not null) await IgnoreCancellation(reconnect).ConfigureAwait(false);
        Connection.Closed -= OnConnectionClosed;
        Connection.Reconnected -= OnConnectionReconnected;
        Connection.Reconnecting -= OnConnectionReconnecting;
        using (await _connectionLock.Lock().ConfigureAwait(false))
        {
            if (Connection.State != HubConnectionState.Disconnected) await Connection.StopAsync().ConfigureAwait(false);
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
        _reconnectCancellation?.Dispose();
        await _reconnectLock.DisposeAsync().ConfigureAwait(false);
        await _connectionLock.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed class ConfiguredRetryPolicy(SignalRWebClientOptions options) : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) => context.PreviousRetryCount >= options.MaxRetryAttempts
            ? null : options.GetRetryDelay(context.PreviousRetryCount);
    }
}
