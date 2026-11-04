using System;
using System.Threading.Tasks;
using Soenneker.SignalR.Web.Client.Options;

namespace Soenneker.SignalR.Web.Client.Tests;

public sealed class SignalRAuthenticationRefreshTests
{
    [Test]
    public async ValueTask Refresh_configuration_participates_in_client_configuration_identity()
    {
        var options = new SignalRWebClientOptions { HubUrl = "https://localhost/hub" };
        var copy = (SignalRWebClientOptions)options.Clone();
        await Assert.That(options.Equals(copy)).IsTrue();
        copy.EnableAuthenticationRefresh = false;
        await Assert.That(options.Equals(copy)).IsFalse();
        copy.EnableAuthenticationRefresh = true;
        copy.AuthenticationRefreshBeforeExpiration = TimeSpan.FromMinutes(1);
        await Assert.That(options.Equals(copy)).IsFalse();
        await Assert.That(copy.Equals((SignalRWebClientOptions)copy.Clone())).IsTrue();
    }

    [Test]
    public async ValueTask Negative_refresh_lead_time_is_rejected()
    {
        await Assert.That(() => new SignalRWebClient(new SignalRWebClientOptions
        {
            HubUrl = "https://localhost/hub", AuthenticationRefreshBeforeExpiration = TimeSpan.FromSeconds(-1)
        })).Throws<ArgumentOutOfRangeException>();
    }
}
