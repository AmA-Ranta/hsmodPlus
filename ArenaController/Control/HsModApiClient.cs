using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ArenaController;

/// <summary>
/// Single access point for the HsMod local web API.  Before this class the
/// controller repeated "scan ports 58744..58748, fetch the page, scrape the
/// token, send the request" in six places; those copies drifted apart (different
/// timeouts, different error handling).  Everything now goes through here so the
/// transport policy exists once and the parsing stays unit-testable.
/// </summary>
internal sealed class HsModApiClient
{
    private static readonly int FirstPort = 58744;
    private static readonly int LastPort = 58748;

    private readonly TimeSpan _timeout;
    private readonly Action<string>? _trace;

    internal HsModApiClient(TimeSpan timeout, Action<string>? trace = null)
    {
        _timeout = timeout;
        _trace = trace;
    }

    private HttpClient CreateClient() => new() { Timeout = _timeout };

    /// <summary>Reads /api/status.  The identity sampler runs before and after
    /// the request so a response is only attributed to the client that produced
    /// it.</summary>
    internal async Task<HsModStatusSnapshot?> GetStatusAsync(
        Func<GameIdentitySample> sampleIdentity,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        for (var port = FirstPort; port <= LastPort; port++)
        {
            var root = $"http://127.0.0.1:{port}";
            try
            {
                var before = sampleIdentity();
                var json = await GetAuthorizedJsonAsync(client, root, "/api/status", cancellationToken)
                    .ConfigureAwait(false);
                if (json == null) continue;
                var after = sampleIdentity();
                var sameGame = LiveMatchEvidence.IsSameGameInstance(
                    before.Known, before.Running, before.ProcessId, before.StartedAtUtc,
                    after.Known, after.Running, after.ProcessId, after.StartedAtUtc);
                var snapshot = HsModApiParser.ParseStatus(json, sameGame, after.ProcessId, after.StartedAtUtc);
                if (snapshot != null) return snapshot;
            }
            catch (Exception exception)
            {
                _trace?.Invoke("status port=" + port + ";error=" + exception.GetType().Name);
            }
        }
        return null;
    }

    internal async Task<HsModDeckSnapshot?> GetDeckSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        for (var port = FirstPort; port <= LastPort; port++)
        {
            var root = $"http://127.0.0.1:{port}";
            try
            {
                var json = await GetAuthorizedJsonAsync(client, root, "/api/config?lang=zhCN", cancellationToken)
                    .ConfigureAwait(false);
                if (json == null) continue;
                var snapshot = HsModApiParser.ParseDeckSnapshot(json);
                if (snapshot != null) return snapshot;
            }
            catch (Exception exception)
            {
                _trace?.Invoke("config port=" + port + ";error=" + exception.GetType().Name);
            }
        }
        return null;
    }

    internal async Task<bool> SetScriptEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        for (var port = FirstPort; port <= LastPort; port++)
        {
            var root = $"http://127.0.0.1:{port}";
            try
            {
                if (await PostAuthorizedAsync(client, root, "/api/script",
                        JsonSerializer.Serialize(new { enabled }), cancellationToken).ConfigureAwait(false))
                    return true;
            }
            catch (Exception exception)
            {
                _trace?.Invoke("script port=" + port + ";enabled=" + enabled + ";error=" + exception.GetType().Name);
            }
        }
        return false;
    }

    internal async Task<bool> SetDeckAsync(string value, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        for (var port = FirstPort; port <= LastPort; port++)
        {
            var root = $"http://127.0.0.1:{port}";
            try
            {
                if (await PostAuthorizedAsync(client, root, "/config",
                        JsonSerializer.Serialize(new { key = "wildLadderDeckName.name", value }), cancellationToken)
                    .ConfigureAwait(false))
                    return true;
            }
            catch (Exception exception)
            {
                _trace?.Invoke("deck port=" + port + ";error=" + exception.GetType().Name);
            }
        }
        return false;
    }

    /// <summary>Synchronous convenience used by the stop path, which cannot await
    /// (it runs inside a UI callback that must complete deterministically).</summary>
    internal bool TrySetScriptEnabled(bool enabled)
        => SetScriptEnabledAsync(enabled).GetAwaiter().GetResult();

    private static async Task<string?> GetAuthorizedJsonAsync(
        HttpClient client, string root, string path, CancellationToken cancellationToken)
    {
        var token = await DiscoverTokenAsync(client, root, cancellationToken).ConfigureAwait(false);
        if (token == null) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, root + path);
        request.Headers.Add("X-HsMod-Token", token);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> PostAuthorizedAsync(
        HttpClient client, string root, string path, string body, CancellationToken cancellationToken)
    {
        var token = await DiscoverTokenAsync(client, root, cancellationToken).ConfigureAwait(false);
        if (token == null) return false;
        using var request = new HttpRequestMessage(HttpMethod.Post, root + path);
        request.Headers.Add("X-HsMod-Token", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private static async Task<string?> DiscoverTokenAsync(
        HttpClient client, string root, CancellationToken cancellationToken)
    {
        var html = await client.GetStringAsync(root + "/", cancellationToken).ConfigureAwait(false);
        return HsModApiParser.ExtractToken(html);
    }
}
