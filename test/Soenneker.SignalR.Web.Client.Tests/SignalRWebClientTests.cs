using Soenneker.Tests.HostedUnit;
using Soenneker.SignalR.Web.Client.Events;
using Soenneker.SignalR.Web.Client.Options;
using System.Threading.Tasks;

namespace Soenneker.SignalR.Web.Client.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public class SignalRWebClientTests : HostedUnitTest
{
    public SignalRWebClientTests(Host host) : base(host)
    {
    }

    [Test]
    public async Task Reconnect_recovery_is_indefinite_by_default()
    {
        var options = new SignalRWebClientOptions();

        await Assert.That(options.ReconnectIndefinitely).IsTrue();
    }

    [Test]
    public async Task Restored_context_distinguishes_reconnects()
    {
        var context = new SignalRConnectionRestoredContext("connection-2", true);

        await Assert.That(context.ConnectionId).IsEqualTo("connection-2");
        await Assert.That(context.IsReconnect).IsTrue();
    }
}
