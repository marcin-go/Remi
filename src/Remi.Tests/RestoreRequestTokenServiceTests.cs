using Remi.Web;
using Xunit;

namespace Remi.Tests;

public sealed class RestoreRequestTokenServiceTests
{
    [Fact]
    public void Issued_token_is_accepted_once()
    {
        var service = new RestoreRequestTokenService(TimeProvider.System);

        var token = service.Issue();

        Assert.True(service.TryConsume(token));
        Assert.False(service.TryConsume(token));
    }

    [Fact]
    public void Empty_token_is_rejected()
    {
        var service = new RestoreRequestTokenService(TimeProvider.System);

        Assert.False(service.TryConsume(null));
        Assert.False(service.TryConsume(string.Empty));
    }
}
