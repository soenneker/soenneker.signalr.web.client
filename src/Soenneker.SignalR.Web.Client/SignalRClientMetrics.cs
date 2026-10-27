using System.Diagnostics.Metrics;

namespace Soenneker.SignalR.Web.Client;

internal static class SignalRClientMetrics
{
    // Deliberately no URLs, connection IDs, tenant IDs, or exception text in metric labels.
    private static readonly Meter Meter = new("Soenneker.SignalR.Web.Client", "1.0.0");
    private static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>("signalr.client.active", "{client}");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("signalr.client.failures", "{failure}");
    private static readonly Counter<long> Abandoned = Meter.CreateCounter<long>("signalr.client.callbacks.abandoned", "{callback}");
    private static readonly Histogram<double> Outages = Meter.CreateHistogram<double>("signalr.client.outage.duration", "s");
    private static readonly Histogram<double> Restorations = Meter.CreateHistogram<double>("signalr.client.restoration.duration", "s");
    public static void ClientAdded() { try { Active.Add(1); } catch { } }
    public static void ClientRemoved() { try { Active.Add(-1); } catch { } }
    public static void Failed() { try { Failures.Add(1); } catch { } }
    public static void CallbackAbandoned() { try { Abandoned.Add(1); } catch { } }
    public static void Outage(double seconds) { try { Outages.Record(seconds); } catch { } }
    public static void Restoration(double seconds) { try { Restorations.Record(seconds); } catch { } }
}
