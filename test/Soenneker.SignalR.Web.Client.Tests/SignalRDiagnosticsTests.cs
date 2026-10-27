using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client.Tests;

public sealed class SignalRDiagnosticsTests
{
    [Test]
    public async ValueTask Recovery_exposes_metrics_readiness_and_underlying_transport_logs(CancellationToken cancellationToken)
    {
        var observed = new ConcurrentDictionary<string, bool>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Soenneker.SignalR.Web.Client") current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) => observed[instrument.Name] = true);
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) => observed[instrument.Name] = true);
        listener.Start();
        using var server = new HubServer();
        var logger = new RecordingLogger();
        int restorations = 0;
        await using var client = server.Client(options =>
        {
            options.Logger = logger;
            options.ConnectionRestored = _ => Interlocked.Increment(ref restorations) == 1
                ? Task.FromException(new InvalidOperationException("Retry restoration")) : Task.CompletedTask;
        });
        await client.EnsureConnection(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        SignalRClientStatus status = await client.GetStatus(cancellationToken);
        await Assert.That(status.IsReady).IsTrue();
        await Assert.That(status.LastFailure is InvalidOperationException).IsTrue();
        await Assert.That(status.FailureCount).IsEqualTo(1);
        await Assert.That(status.RetryCount).IsEqualTo(1);
        await Assert.That(status.NextRetryAt == null).IsTrue();
        await client.DisposeAsync();
        await Assert.That(observed.ContainsKey("signalr.client.active")).IsTrue();
        await Assert.That(observed.ContainsKey("signalr.client.failures")).IsTrue();
        await Assert.That(observed.ContainsKey("signalr.client.restoration.duration")).IsTrue();
        await Assert.That(observed.ContainsKey("signalr.client.outage.duration")).IsTrue();
        await Assert.That(logger.Messages.Any(message => message.Contains("Microsoft.AspNetCore.SignalR.Client"))).IsTrue();
    }
}
