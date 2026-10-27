using System;
using Microsoft.AspNetCore.SignalR.Client;
using Soenneker.SignalR.Web.Client.Options;

namespace Soenneker.SignalR.Web.Client;

internal sealed class ConfiguredRetryPolicy(SignalRWebClientOptions options, Action<TimeSpan>? retryScheduled = null) : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext context)
    {
        if (context.RetryReason != null) options.ReportConnectionError(context.RetryReason);
        if (context.PreviousRetryCount >= options.MaxRetryAttempts) return null;
        TimeSpan delay = options.GetRetryDelay(context.PreviousRetryCount, context.RetryReason);
        retryScheduled?.Invoke(delay);
        return delay;
    }
}
