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
    private readonly AsyncLocal<CallbackScope?> _callbackScope = new();
    private Recovery? _recovery;
    private Task? _stopTask;
    private Task? _resumeTask;
    private long _stopVersion;
    private readonly TaskCompletionSource _disposeCompletion = NewCompletion();
    private ValueAtomicBool _disposed;
    private bool _enabled;
    private bool _hasConnected;
    private CancellationTokenSource _sessionCancellation = new();
    private CancellationToken _connectionAttemptToken;
    private Func<Exception?, Task> _closedHandler = null!;
    private Func<Exception?, Task> _reconnectingHandler = null!;
    private Func<string?, Task> _reconnectedHandler = null!;
    private readonly CancellationTokenSource _monitorCancellation = new();
    private Task? _monitor;
    private DateTimeOffset _lastRestoredAt;

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class CallbackScope(Recovery recovery)
    {
        public Recovery Recovery { get; } = recovery;
        public volatile bool Active = true;
    }

    public SignalRWebClient(SignalRWebClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options.Snapshot();
        if (string.IsNullOrWhiteSpace(options.HubUrl)) throw new ArgumentException("A hub URL is required.", nameof(options));
        if (options.MaxRetryAttempts is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(options.MaxRetryAttempts));
        if (options.InitialRetryDelay < TimeSpan.Zero || options.InitialRetryDelay > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(options.InitialRetryDelay));
        if (options.KeepAliveInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.KeepAliveInterval));
        if (options.ServerTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.ServerTimeout));
        if (options.StatefulReconnectBufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(options.StatefulReconnectBufferSize));
        ValidateTimeout(options.ConnectionAttemptTimeout, nameof(options.ConnectionAttemptTimeout));
        ValidateTimeout(options.AccessTokenTimeout, nameof(options.AccessTokenTimeout));
        ValidateTimeout(options.RestorationTimeout, nameof(options.RestorationTimeout));
        ValidateTimeout(options.AutomaticReconnectTimeout, nameof(options.AutomaticReconnectTimeout));
        ValidateTimeout(options.AuthenticationRetryDelay, nameof(options.AuthenticationRetryDelay));
        if (options.ResumeDetectionThreshold is { } threshold && threshold < TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(options.ResumeDetectionThreshold));
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        _options = options;

        IHubConnectionBuilder builder = new HubConnectionBuilder().WithUrl(options.HubUrl, http =>
        {
            if (options.AccessTokenProvider is not null || options.AccessTokenProviderWithCancellation is not null)
                http.AccessTokenProvider = GetAccessToken;
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
        AttachConnectionHandlers();
    }

    private void AttachConnectionHandlers()
    {
        Connection.Closed -= _closedHandler;
        Connection.Reconnecting -= _reconnectingHandler;
        Connection.Reconnected -= _reconnectedHandler;
        CancellationTokenSource session = _sessionCancellation;
        Connection.Closed += _closedHandler = error => OnConnectionClosed(session, error);
        Connection.Reconnecting += _reconnectingHandler = error => OnConnectionReconnecting(session, error);
        Connection.Reconnected += _reconnectedHandler = id => OnConnectionReconnected(session, id);
    }

    private static void ValidateTimeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(name);
    }

    private async Task<string?> GetAccessToken()
    {
        CancellationTokenSource cancellation;
        using (await _gate.Lock().ConfigureAwait(false))
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, _connectionAttemptToken);
        }
        using (cancellation)
        {
            using var timeout = CancelAfterSafely(cancellation, _options.AccessTokenTimeout);
            cancellation.Token.ThrowIfCancellationRequested();
            Task<string> task = _options.AccessTokenProviderWithCancellation is { } provider
                ? provider(cancellation.Token) : _options.AccessTokenProvider!();
            try { return await task.WaitAsync(cancellation.Token).ConfigureAwait(false); }
            catch
            {
                _ = ObserveCallback(task);
                throw;
            }
        }
    }

    public ValueTask StartConnection(CancellationToken cancellationToken = default) => StartCore(false, cancellationToken);

    public ValueTask EnsureConnection(CancellationToken cancellationToken = default) => StartCore(true, cancellationToken);

    private async ValueTask StartCore(bool requireReady, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            Task pending;
            bool stopping;
            long version;
            CancellationTokenSource session;
            using (await _gate.Lock(cancellationToken).ConfigureAwait(false))
            {
                ObjectDisposedException.ThrowIf(_disposed.Value, this);
                version = _stopVersion;
                session = _sessionCancellation;
                stopping = _stopTask is { IsCompleted: false };
                if (stopping)
                    pending = _stopTask!;
                else
                {
                    EnableLocked();
                    Recovery recovery = EnsureRecoveryLocked();
                    // A restoration callback can ensure the connection without awaiting itself.
                    if (_callbackScope.Value is { Active: true } scope && scope.Recovery == recovery)
                    {
                        if (requireReady) throw new InvalidOperationException("ConnectionRestored cannot await its own readiness. Use Connection directly inside restoration.");
                        return;
                    }
                    if (requireReady && recovery.Ready.Task.IsCompleted && (Connection.State != HubConnectionState.Connected ||
                        recovery.RestoredConnectionId != Connection.ConnectionId))
                        recovery.Ready = NewCompletion();
                    pending = requireReady ? recovery.Ready.Task : recovery.Completion.Task;
                }
            }
            // Caller cancellation cancels only this wait, not other callers' shared recovery.
            try { await pending.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_disposed.Value)
            {
                using (await _gate.Lock(cancellationToken).ConfigureAwait(false))
                {
                    if (!_disposed.Value && version == _stopVersion &&
                        (_resumeTask is { IsCompleted: false } || session != _sessionCancellation)) continue;
                }
                throw;
            }
            if (!stopping)
            {
                if (!requireReady) return;
                using (await _gate.Lock(cancellationToken).ConfigureAwait(false))
                {
                    if (_disposed.Value || !_enabled || version != _stopVersion) throw new OperationCanceledException("Connection recovery was stopped.");
                    if (_recovery is { } recovery && pending == recovery.Ready.Task &&
                        recovery.RestoredRevision == recovery.Revision && Connection.State == HubConnectionState.Connected &&
                        recovery.RestoredConnectionId == Connection.ConnectionId) return;
                }
            }
        }
    }

    private void EnableLocked()
    {
        if (_sessionCancellation.IsCancellationRequested)
        {
            _sessionCancellation.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            AttachConnectionHandlers();
        }
        _enabled = true;
        if (_options.ResumeDetectionThreshold != null && _monitor == null)
            _monitor = Task.Run(MonitorSuspension);
    }

    private async Task MonitorSuspension()
    {
        CancellationToken token = _monitorCancellation.Token;
        DateTimeOffset previous = _options.TimeProvider.GetUtcNow();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _options.TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                DateTimeOffset now = _options.TimeProvider.GetUtcNow();
                bool resume;
                using (await _gate.Lock().ConfigureAwait(false))
                {
                    resume = now - previous >= _options.ResumeDetectionThreshold && _enabled && _recovery != null &&
                        !_disposed.Value && _stopTask is not { IsCompleted: false } && now - _lastRestoredAt >= TimeSpan.FromSeconds(5);
                }
                previous = now;
                if (resume)
                {
                    Log(LogLevel.Information, null, "Host execution resumed after a long pause; refreshing the connection.");
                    _ = ObserveCallback(ResumeConnection().AsTask());
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed.Value) { }
    }

    public async ValueTask ResumeConnection(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task pending;
        using (await _gate.Lock(cancellationToken).ConfigureAwait(false))
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            // A restoration running as part of resume must not join the resume that is waiting for it.
            if (_callbackScope.Value is { Active: true } scope && scope.Recovery == _recovery && _resumeTask is { IsCompleted: false }) return;
            if (_resumeTask is { IsCompleted: false }) pending = _resumeTask;
            else
            {
                // A platform wake-up must never undo a deliberate stop or start an unused client.
                if (!_enabled) return;
                Task stop = BeginStopLocked();
                long version = _stopVersion;
                pending = _resumeTask = Task.Run(() => ResumeCore(stop, version));
            }
        }
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ResumeCore(Task stop, long version)
    {
        await stop.ConfigureAwait(false);
        if (_disposed.Value) return;
        Task pending;
        try
        {
            using (await _gate.Lock().ConfigureAwait(false))
            {
                if (_disposed.Value || version != _stopVersion) return;
                EnableLocked();
                pending = EnsureRecoveryLocked().Completion.Task;
            }
        }
        catch (ObjectDisposedException) when (_disposed.Value) { return; }
        await pending.ConfigureAwait(false);
    }

    public async Task StopConnection(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task pending;
        try
        {
            using (await _gate.Lock(cancellationToken).ConfigureAwait(false))
            {
                _stopVersion++;
                pending = _disposed.Value ? _disposeCompletion.Task : BeginStopLocked();
            }
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
        CancellationTokenSource session = _sessionCancellation;
        return _stopTask = Task.Run(() => StopCore(recovery, session));
    }

    private async Task StopCore(Recovery? recovery, CancellationTokenSource session)
    {
        // Cancel token acquisition before waiting for SignalR's connection lock during shutdown.
        _ = CancelSafely(session);
        if (recovery != null)
        {
            // Application cancellation handlers must not delay cancellation of our own waits.
            _ = CancelSafely(recovery.Cancellation);
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

    private async Task OnConnectionClosed(CancellationTokenSource session, Exception? error)
    {
        if (!await ConnectionChanged(session, closed: true, error: error).ConfigureAwait(false)) return;
        if (await IsStopping(session).ConfigureAwait(false)) return;
        Log(LogLevel.Warning, error, "Connection closed.");
        SafeInvoke(_options.ConnectionClosed, error, "ConnectionClosed");
        if (!_options.ReconnectIndefinitely) SafeInvoke(_options.RetriesExhausted, "RetriesExhausted");
    }

    private async Task OnConnectionReconnecting(CancellationTokenSource session, Exception? error)
    {
        if (!await ConnectionChanged(session, reconnecting: true, error: error).ConfigureAwait(false)) return;
        if (!await IsStopping(session).ConfigureAwait(false))
        {
            Log(LogLevel.Warning, error, "Connection lost. Reconnecting.");
            SafeInvoke(_options.ConnectionReconnecting, error, "ConnectionReconnecting");
        }
    }

    private async Task OnConnectionReconnected(CancellationTokenSource session, string? connectionId)
    {
        if (!await ConnectionChanged(session, connectionId: connectionId).ConfigureAwait(false)) return;
        if (!await IsStopping(session).ConfigureAwait(false)) SafeInvoke(_options.ConnectionReconnected, connectionId, "ConnectionReconnected");
    }

    private async ValueTask<bool> IsStopping(CancellationTokenSource session)
    {
        if (_disposed.Value) return true;
        try
        {
            using (await _gate.Lock().ConfigureAwait(false))
                return _disposed.Value || session != _sessionCancellation || !_enabled || _stopTask is { IsCompleted: false };
        }
        catch (ObjectDisposedException) when (_disposed.Value)
        {
            return true;
        }
    }

    private async Task<bool> ConnectionChanged(CancellationTokenSource session, bool closed = false, bool reconnecting = false,
        string? connectionId = null, Exception? error = null)
    {
        if (_disposed.Value) return false;
        CancellationTokenSource? restoration;
        try
        {
            using (await _gate.Lock().ConfigureAwait(false))
            {
                if (_disposed.Value || session != _sessionCancellation || !_enabled || _stopTask is { IsCompleted: false }) return false;
                // SignalR delivers events asynchronously. Ignore events from a state already superseded.
                if (closed && Connection.State != HubConnectionState.Disconnected ||
                    reconnecting && Connection.State != HubConnectionState.Reconnecting ||
                    !closed && !reconnecting && (Connection.State != HubConnectionState.Connected || connectionId != Connection.ConnectionId)) return false;
                Recovery? recovery = _recovery;
                if (recovery?.SuppressConnectionEvents == true) return false;
                if (recovery == null)
                {
                    if (closed && !_options.ReconnectIndefinitely) return true;
                    recovery = EnsureRecoveryLocked();
                }
                recovery.Revision++;
                if (error != null) recovery.LastError = error;
                recovery.IsReconnect = true;
                recovery.AutomaticReconnectPending = reconnecting;
                if (reconnecting) recovery.AutomaticReconnectStarted = _options.TimeProvider.GetTimestamp();
                if (recovery.Completion.Task.IsCompleted) recovery.Completion = NewCompletion();
                if (recovery.Ready.Task.IsCompleted) recovery.Ready = NewCompletion();
                restoration = recovery.Restoration;
                if (recovery.Changed.CurrentCount == 0) recovery.Changed.Release();
                if (closed && !_options.ReconnectIndefinitely)
                {
                    recovery.Exhausted = true;
                    _ = CancelSafely(recovery.Cancellation);
                }
            }
        }
        catch (ObjectDisposedException) when (_disposed.Value)
        {
            return false;
        }
        // Do not run application cancellation callbacks under the lifecycle lock.
        if (restoration != null)
            _ = CancelSafely(restoration);
        return true;
    }

    private CancellationTokenSource CancelAfterSafely(CancellationTokenSource cancellation, TimeSpan delay)
    {
        // CancelAfter on an application-facing token can throw unhandled exceptions from
        // application cancellation registrations on the timer thread. Observe those via CancelAsync.
        var timer = new CancellationTokenSource(delay);
        timer.Token.Register(() => _ = CancelSafely(cancellation));
        return timer;
    }

    private async Task CancelSafely(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log(LogLevel.Error, ex, "Application cancellation callback failed."); }
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
                long reconnectStarted;
                using (await _gate.Lock().ConfigureAwait(false))
                {
                    automaticReconnectPending = recovery.AutomaticReconnectPending;
                    reconnectStarted = recovery.AutomaticReconnectStarted;
                }
                if (automaticReconnectPending)
                {
                    // HubConnection publishes its new state before delivering Reconnected/Closed.
                    // Let that event identify the new connection before restoring or restarting it.
                    TimeSpan remaining = _options.AutomaticReconnectTimeout - _options.TimeProvider.GetElapsedTime(reconnectStarted);
                    if (remaining > TimeSpan.Zero)
                    {
                        await recovery.Changed.WaitAsync(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                        continue;
                    }
                    // StopAsync cancels SignalR's reconnect attempt; our session cancellation also
                    // releases access-token providers that SignalR cannot cancel itself.
                    using (await _gate.Lock().ConfigureAwait(false))
                    {
                        if (!_enabled || _stopTask is { IsCompleted: false }) return;
                        if (!recovery.AutomaticReconnectPending) continue;
                        recovery.SuppressConnectionEvents = true;
                        _ = CancelSafely(_sessionCancellation);
                    }
                    await Connection.StopAsync().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    recovery.LastError = new TimeoutException("Automatic reconnect exceeded its deadline.");
                    _options.ReportConnectionError(recovery.LastError);
                    using (await _gate.Lock().ConfigureAwait(false))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!_enabled || _stopTask is { IsCompleted: false }) return;
                        EnableLocked();
                        recovery.SuppressConnectionEvents = false;
                        recovery.AutomaticReconnectPending = false;
                        recovery.IsReconnect = true;
                        recovery.Revision++;
                    }
                    if (!_options.ReconnectIndefinitely)
                    {
                        exhausted = true;
                        SafeInvoke(_options.RetriesExhausted, "RetriesExhausted");
                        return;
                    }
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
                            recovery.RestoredConnectionId = Connection.ConnectionId;
                            _lastRestoredAt = _options.TimeProvider.GetUtcNow();
                            recovery.Completion.TrySetResult();
                            recovery.Ready.TrySetResult();
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
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                    using var timeout = CancelAfterSafely(attempt, _options.ConnectionAttemptTimeout);
                    try
                    {
                        using (await _gate.Lock().ConfigureAwait(false)) _connectionAttemptToken = attempt.Token;
                        await Connection.StartAsync(attempt.Token).ConfigureAwait(false);
                        failures = 0;
                        continue;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        recovery.LastError = ex;
                        Log(LogLevel.Warning, ex, "Connection attempt failed.");
                        _options.ReportConnectionError(ex);
                    }
                    finally
                    {
                        using (await _gate.Lock().ConfigureAwait(false)) _connectionAttemptToken = default;
                    }
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
                    TimeSpan cycleDelay = CycleDelay;
                    if (SignalRWebClientOptions.IsAuthenticationFailure(recovery.LastError) && cycleDelay < _options.AuthenticationRetryDelay)
                        cycleDelay = _options.AuthenticationRetryDelay;
                    await recovery.Changed.WaitAsync(cycleDelay, token).ConfigureAwait(false);
                    using (await _gate.Lock().ConfigureAwait(false))
                        if (recovery.Completion.Task.IsCompleted) recovery.Completion = NewCompletion();
                }
                else
                    await recovery.Changed.WaitAsync(_options.GetRetryDelay(failures - 1, recovery.LastError), token).ConfigureAwait(false);
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
                else if (exhausted || recovery.Exhausted) recovery.Completion.TrySetResult();
                else recovery.Completion.TrySetCanceled(token.IsCancellationRequested ? token : new CancellationToken(true));
                if (failure != null || exhausted || recovery.Exhausted)
                {
                    recovery.Ready.TrySetException(failure ?? new InvalidOperationException("Connection recovery exhausted its retry budget.", recovery.LastError));
                    // Background recovery need not have an EnsureConnection waiter.
                    _ = recovery.Ready.Task.Exception;
                }
                else recovery.Ready.TrySetCanceled(token.IsCancellationRequested ? token : new CancellationToken(true));
            }
        }
    }

    private TimeSpan CycleDelay => _options.Jitter(_options.InitialRetryDelay >= TimeSpan.FromMilliseconds(100)
        ? _options.InitialRetryDelay : TimeSpan.FromMilliseconds(100));

    private async Task<bool> Restore(Recovery recovery, long revision, bool isReconnect, CancellationToken token)
    {
        if (_options.ConnectionRestored is not { } callback) return true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var timeout = CancelAfterSafely(cancellation, _options.RestorationTimeout);
        using (await _gate.Lock().ConfigureAwait(false))
        {
            if (revision != recovery.Revision || Connection.State != HubConnectionState.Connected) return false;
            recovery.Restoration = cancellation;
        }
        Task? callbackTask = null;
        var scope = new CallbackScope(recovery);
        try
        {
            _callbackScope.Value = scope;
            callbackTask = callback(new SignalRConnectionRestoredContext(Connection.ConnectionId, isReconnect, cancellation.Token));
            await callbackTask.WaitAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Legacy callbacks have no cancellation parameter. Observe their eventual failure without blocking shutdown.
            if (callbackTask != null) _ = ObserveCallback(callbackTask);
            token.ThrowIfCancellationRequested();
            recovery.LastError = new TimeoutException("Connection restoration was cancelled or exceeded its deadline.");
            Log(LogLevel.Warning, null, "Restoration was cancelled or timed out; current connection state will be checked before retrying.");
            return false;
        }
        catch (Exception ex)
        {
            recovery.LastError = ex;
            Log(LogLevel.Error, ex, "ConnectionRestored callback failed; restoration will be retried.");
            return false;
        }
        finally
        {
            scope.Active = false;
            _callbackScope.Value = null;
            using (await _gate.Lock().ConfigureAwait(false))
                if (recovery.Restoration == cancellation) recovery.Restoration = null;
        }
    }

    private async Task ObserveCallback(Task callback)
    {
        try { await callback.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log(LogLevel.Error, ex, "Cancelled application callback failed."); }
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
        _options.WriteLog(level, exception, message);
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
                    Connection.Closed -= _closedHandler;
                    Connection.Reconnected -= _reconnectedHandler;
                    Connection.Reconnecting -= _reconnectingHandler;
                    await Connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await _monitorCancellation.CancelAsync().ConfigureAwait(false);
                if (_monitor != null) await _monitor.ConfigureAwait(false);
                _monitorCancellation.Dispose();
                _sessionCancellation.Dispose();
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
