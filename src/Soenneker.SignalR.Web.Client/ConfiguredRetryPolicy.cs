using System;
using Microsoft.AspNetCore.SignalR.Client;
using Soenneker.SignalR.Web.Client.Options;

namespace Soenneker.SignalR.Web.Client;

internal sealed class ConfiguredRetryPolicy(SignalRWebClientOptions options) : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext context) => context.PreviousRetryCount >= options.MaxRetryAttempts
        ? null : options.GetRetryDelay(context.PreviousRetryCount);
}
