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
    private readonly AsyncLock _gate = new();
    private readonly AsyncLocal<Recovery?> _callbackRecovery = new();
    private Recovery? _recovery;
    private Task? _stopTask;
    private readonly TaskCompletionSource _disposeCompletion = NewCompletion();
    private ValueAtomicBool _disposed;
    private bool _enabled;
    private bool _hasConnected;

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            if (options.HttpMessageHandlerFactory is not null)
                http.HttpMessageHandlerFactory = options.HttpMessageHandlerFactory;
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
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            Task pending;
            bool stopping;
            using (await _gate.Lock(cancellationToken).ConfigureAwait(false))
            {
                ObjectDisposedException.ThrowIf(_disposed.Value, this);
                stopping = _stopTask is { IsCompleted: false };
                if (stopping)
                    pending = _stopTask!;
                else
                {
                    _enabled = true;
                    Recovery recovery = EnsureRecoveryLocked();
                    // A restoration callback can ensure the connection without awaiting itself.
                    if (_callbackRecovery.Value == recovery) return;
                    pending = recovery.Completion.Task;
                }
            }
            // Caller cancellation cancels only this wait, not other callers' shared recovery.
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!stopping) return;
        }
    }

    public async Task StopConnection(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task pending;
        try
        {
            using (await _gate.Lock(cancellationToken).ConfigureAwait(false))
                pending = _disposed.Value ? _disposeCompletion.Task : BeginStopLocked();
        }
        catch (ObjectDisposedException) when (_disposed.Value)
        {
            pending = _disposeCompletion.Task;
        }
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task BeginStopLocked()
    {
        _enabled = false;
        if (_stopTask is { IsCompleted: false }) return _stopTask;
        Recovery? recovery = _recovery;
        // Publish the task before cancellation or user callbacks can run.
        return _stopTask = Task.Run(() => StopCore(recovery));
    }

    private async Task StopCore(Recovery? recovery)
    {
        if (recovery != null)
        {
            try { await recovery.Cancellation.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Log(LogLevel.Error, ex, "Recovery cancellation callback failed."); }
            await recovery.Worker.ConfigureAwait(false);
        }
        await Connection.StopAsync().ConfigureAwait(false);
    }

    private Recovery EnsureRecoveryLocked()
    {
        if (_recovery != null) return _recovery;
        var recovery = new Recovery { IsReconnect = _hasConnected };
        _recovery = recovery;
        recovery.Worker = Task.Run(() => Recover(recovery));
        return recovery;
    }

    private async Task OnConnectionClosed(Exception? error)
    {
        await ConnectionChanged(closed: true).ConfigureAwait(false);
        if (await IsStopping().ConfigureAwait(false)) return;
        Log(LogLevel.Warning, error, "Connection closed.");
        SafeInvoke(_options.ConnectionClosed, error, "ConnectionClosed");
        if (!_options.ReconnectIndefinitely) SafeInvoke(_options.RetriesExhausted, "RetriesExhausted");
    }

    private async Task OnConnectionReconnecting(Exception? error)
    {
        await ConnectionChanged(reconnecting: true).ConfigureAwait(false);
        if (!await IsStopping().ConfigureAwait(false))
        {
            Log(LogLevel.Warning, error, "Connection lost. Reconnecting.");
            SafeInvoke(_options.ConnectionReconnecting, error, "ConnectionReconnecting");
        }
    }

    private async Task OnConnectionReconnected(string? connectionId)
    {
        await ConnectionChanged().ConfigureAwait(false);
        if (!await IsStopping().ConfigureAwait(false)) SafeInvoke(_options.ConnectionReconnected, connectionId, "ConnectionReconnected");
    }

    private async ValueTask<bool> IsStopping()
    {
        if (_disposed.Value) return true;
        try
        {
            using (await _gate.Lock().ConfigureAwait(false))
                return _disposed.Value || !_enabled || _stopTask is { IsCompleted: false };
        }
        catch (ObjectDisposedException) when (_disposed.Value)
        {
            return true;
        }
    }

    private async Task ConnectionChanged(bool closed = false, bool reconnecting = false)
    {
        if (_disposed.Value) return;
        CancellationTokenSource? restoration;
        try
        {
            using (await _gate.Lock().ConfigureAwait(false))
            {
                if (_disposed.Value || !_enabled || _stopTask is { IsCompleted: false }) return;
                Recovery? recovery = _recovery;
                if (recovery == null)
                {
                    if (closed && !_options.ReconnectIndefinitely) return;
                    recovery = EnsureRecoveryLocked();
                }
                recovery.Revision++;
                recovery.IsReconnect = true;
                recovery.AutomaticReconnectPending = reconnecting;
                if (recovery.Completion.Task.IsCompleted) recovery.Completion = NewCompletion();
                restoration = recovery.Restoration;
                if (recovery.Changed.CurrentCount == 0) recovery.Changed.Release();
                if (closed && !_options.ReconnectIndefinitely)
                    _ = CancelRestoration(recovery.Cancellation);
            }
        }
        catch (ObjectDisposedException) when (_disposed.Value)
        {
            return;
        }
        // Do not run application cancellation callbacks under the lifecycle lock.
        if (restoration != null)
            _ = CancelRestoration(restoration);
    }

    private async Task CancelRestoration(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log(LogLevel.Error, ex, "Restoration cancellation callback failed."); }
    }

    private async Task Recover(Recovery recovery)
    {
        CancellationToken token = recovery.Cancellation.Token;
        var failures = 0;
        bool exhausted = false;
        Exception? failure = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                bool automaticReconnectPending;
                using (await _gate.Lock().ConfigureAwait(false)) automaticReconnectPending = recovery.AutomaticReconnectPending;
                if (automaticReconnectPending)
                {
                    // HubConnection publishes its new state before delivering Reconnected/Closed.
                    // Let that event identify the new connection before restoring or restarting it.
                    await recovery.Changed.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }
                if (Connection.State == HubConnectionState.Connected)
                {
                    long revision;
                    bool restored;
                    bool isReconnect;
                    using (await _gate.Lock().ConfigureAwait(false))
                    {
                        revision = recovery.Revision;
                        restored = recovery.RestoredRevision == revision;
                        isReconnect = recovery.IsReconnect;
                        _hasConnected = true;
                    }
                    if (restored)
                    {
                        await recovery.Changed.WaitAsync(token).ConfigureAwait(false);
                        continue;
                    }
                    bool successful = await Restore(recovery, revision, isReconnect, token).ConfigureAwait(false);
                    using (await _gate.Lock().ConfigureAwait(false))
                    {
                        if (revision != recovery.Revision || Connection.State != HubConnectionState.Connected)
                        {
                            failures = 0;
                            continue;
                        }
                        if (successful)
                        {
                            recovery.RestoredRevision = revision;
                            recovery.Completion.TrySetResult();
                            failures = 0;
                            continue;
                        }
                    }
                }
                else if (Connection.State != HubConnectionState.Disconnected)
                {
                    // Automatic reconnect owns its attempts; waiting must not spend our retry budget.
                    await recovery.Changed.WaitAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    continue;
                }
                else
                {
                    try
                    {
                        await Connection.StartAsync(token).ConfigureAwait(false);
                        failures = 0;
                        continue;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { Log(LogLevel.Warning, ex, "Connection attempt failed."); }
                }

                if (failures++ >= _options.MaxRetryAttempts)
                {
                    SafeInvoke(_options.RetriesExhausted, "RetriesExhausted");
                    if (!_options.ReconnectIndefinitely)
                    {
                        exhausted = true;
                        return;
                    }
                    using (await _gate.Lock().ConfigureAwait(false))
                        recovery.Completion.TrySetResult();
                    failures = 0;
                    await Task.Delay(CycleDelay, token).ConfigureAwait(false);
                    using (await _gate.Lock().ConfigureAwait(false))
                        if (recovery.Completion.Task.IsCompleted) recovery.Completion = NewCompletion();
                }
                else
                    await Task.Delay(_options.GetRetryDelay(failures - 1), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log(LogLevel.Error, ex, "Connection recovery failed.");
            failure = ex;
        }
        finally
        {
            using (await _gate.Lock().ConfigureAwait(false))
            {
                if (_recovery == recovery) _recovery = null;
                recovery.Changed.Dispose();
                recovery.Cancellation.Dispose();
                if (failure != null) recovery.Completion.TrySetException(failure);
                else if (exhausted) recovery.Completion.TrySetResult();
                else recovery.Completion.TrySetCanceled(token.IsCancellationRequested ? token : new CancellationToken(true));
            }
        }
    }

    private TimeSpan CycleDelay => _options.InitialRetryDelay > TimeSpan.Zero ? _options.InitialRetryDelay : TimeSpan.FromMilliseconds(100);

    private async Task<bool> Restore(Recovery recovery, long revision, bool isReconnect, CancellationToken token)
    {
        if (_options.ConnectionRestored is not { } callback) return true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using (await _gate.Lock().ConfigureAwait(false))
        {
            if (revision != recovery.Revision || Connection.State != HubConnectionState.Connected) return false;
            recovery.Restoration = cancellation;
        }
        Task? callbackTask = null;
        try
        {
            _callbackRecovery.Value = recovery;
            callbackTask = callback(new SignalRConnectionRestoredContext(Connection.ConnectionId, isReconnect, cancellation.Token));
            await callbackTask.WaitAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Legacy callbacks have no cancellation parameter. Observe their eventual failure without blocking shutdown.
            if (callbackTask != null) _ = ObserveCallback(callbackTask);
            token.ThrowIfCancellationRequested();
            return false;
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, ex, "ConnectionRestored callback failed; restoration will be retried.");
            return false;
        }
        finally
        {
            _callbackRecovery.Value = null;
            using (await _gate.Lock().ConfigureAwait(false))
                if (recovery.Restoration == cancellation) recovery.Restoration = null;
        }
    }

    private async Task ObserveCallback(Task callback)
    {
        try { await callback.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log(LogLevel.Error, ex, "Cancelled restoration callback failed."); }
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

    public ValueTask DisposeAsync()
    {
        if (_disposed.TrySetTrue()) _ = DisposeCore();
        return new ValueTask(_disposeCompletion.Task);
    }

    private async Task DisposeCore()
    {
        try
        {
            try
            {
                Task stop;
                using (await _gate.Lock().ConfigureAwait(false))
                    stop = BeginStopLocked();
                try { await stop.ConfigureAwait(false); }
                finally
                {
                    Connection.Closed -= OnConnectionClosed;
                    Connection.Reconnected -= OnConnectionReconnected;
                    Connection.Reconnecting -= OnConnectionReconnecting;
                    await Connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await _gate.DisposeAsync().ConfigureAwait(false);
            }
            _disposeCompletion.TrySetResult();
        }
        catch (Exception ex)
        {
            _disposeCompletion.TrySetException(ex);
        }
    }
}
