using System;
using Microsoft.Extensions.Logging;

namespace Soenneker.SignalR.Web.Client;

internal sealed class ForwardingLogger(ILogger logger, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) { try { return logger.IsEnabled(level); } catch { return false; } }
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
    {
        try { logger.Log(level, id, error, "{Category}: {Message}", category, formatter(state, error)); } catch { }
    }
}
