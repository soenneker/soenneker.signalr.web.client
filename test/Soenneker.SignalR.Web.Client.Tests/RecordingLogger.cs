using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Soenneker.SignalR.Web.Client.Tests;

internal sealed class RecordingLogger : ILogger
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, error));
}
