using Soenneker.Tests.HostedUnit;

namespace Soenneker.SignalR.Web.Client.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public class SignalRWebClientTests : HostedUnitTest
{
    public SignalRWebClientTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
