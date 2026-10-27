using System;
using Microsoft.Extensions.Logging;

namespace Soenneker.SignalR.Web.Client;

internal sealed class ForwardingLoggerProvider(ILogger logger) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ForwardingLogger(logger, categoryName);
    public void Dispose() { } // The application owns the supplied logger.
}
