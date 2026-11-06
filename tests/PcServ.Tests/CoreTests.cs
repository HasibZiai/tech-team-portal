using PcServ.Core.Personality;
using PcServ.Core.Security;

namespace PcServ.Tests;

public class TempPasswordGeneratorTests
{
    [Fact]
    public void Generates_readable_passwords_that_meet_the_policy()
    {
        for (var i = 0; i < 500; i++)
        {
            var password = TempPasswordGenerator.Generate();

            Assert.Equal(12, password.Length);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsDigit);
            Assert.DoesNotContain(password, c => "0O1lI".Contains(c));
        }
    }

    [Fact]
    public void Rejects_short_lengths() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TempPasswordGenerator.Generate(6));
}

public class HealthCheckTests
{
    [Theory]
    [InlineData(null, 8080)]
    [InlineData("", 8080)]
    [InlineData("8080", 8080)]
    [InlineData("5000;5001", 5000)]
    [InlineData("not-a-port", 8080)]
    public void Reads_the_port_from_configuration(string? value, int expected) =>
        Assert.Equal(expected, PcServ.Web.Services.HealthCheck.ConfiguredPort(value));

    [Fact]
    public async Task Reports_unhealthy_when_nothing_is_listening()
    {
        // Ask the OS for a free port, then release it so nothing is listening there.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Equal(1, await PcServ.Web.Services.HealthCheck.ProbeAsync(port));
    }
}

public class QuipsTests
{
    [Fact]
    public void Every_moment_has_a_plain_line()
    {
        foreach (var moment in Enum.GetValues<QuipMoment>())
        {
            Assert.False(string.IsNullOrWhiteSpace(Quips.PlainFor(moment)));
        }
    }

    [Fact]
    public void Personality_off_always_returns_the_plain_line()
    {
        foreach (var moment in Enum.GetValues<QuipMoment>())
        {
            Assert.Equal(Quips.PlainFor(moment), Quips.For(moment, personalityEnabled: false));
        }
    }
}
