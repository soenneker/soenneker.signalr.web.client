using Soenneker.Tests.FixturedUnit;
using Xunit;

namespace Soenneker.SignalR.Web.Client.Tests;

[Collection("Collection")]
public class SignalRWebClientTests : FixturedUnitTest
{
    public SignalRWebClientTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
    }

    [Fact]
    public void Default()
    {

    }
}
