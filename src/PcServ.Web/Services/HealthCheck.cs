namespace PcServ.Web.Services;

/// <summary>
/// Backs "dotnet PcServ.Web.dll --healthcheck", which Docker runs to see if the app is alive.
/// The slim .NET container image has no curl or wget, so the app probes itself.
/// </summary>
public static class HealthCheck
{
    public const string CommandLineFlag = "--healthcheck";

    /// <summary>Returns exit code 0 if the app answers on its /health endpoint, otherwise 1.</summary>
    public static async Task<int> ProbeAsync(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }

    /// <summary>The port the container listens on, from ASPNETCORE_HTTP_PORTS (first value), or 8080.</summary>
    public static int ConfiguredPort(string? httpPorts) =>
        int.TryParse(httpPorts?.Split([',', ';'])[0], out var port) ? port : 8080;
}
