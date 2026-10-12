namespace TimeflipCrmSync.Health;

/// <summary>
/// Self-contained health probe for the Docker HEALTHCHECK, so the image needs no
/// curl/wget. Invoked as <c>dotnet TimeflipCrmSync.dll --healthcheck</c>:
/// GETs the /healthz endpoint and exits 0 (healthy) or 1 (not).
/// URL is taken from HEALTHCHECK_URL, else derived from the app's listen port.
/// </summary>
public static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        var url = Environment.GetEnvironmentVariable("HEALTHCHECK_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            // Prefer the explicit container port var, then the first ASPNETCORE_URLS entry.
            var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")
                       ?? PortFromUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
                       ?? "8080";
            url = $"http://localhost:{port.Split(';')[0].Trim()}/healthz";
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await http.GetAsync(url);
            if (resp.IsSuccessStatusCode) return 0;
            await Console.Error.WriteLineAsync($"Health probe {url} -> {(int)resp.StatusCode}");
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Health probe {url} failed: {ex.Message}");
            return 1;
        }
    }

    private static string? PortFromUrls(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls)) return null;
        var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return Uri.TryCreate(first, UriKind.Absolute, out var u) && u.Port > 0
            ? u.Port.ToString() : null;
    }
}
