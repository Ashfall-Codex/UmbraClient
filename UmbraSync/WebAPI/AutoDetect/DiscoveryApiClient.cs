using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UmbraSync.Services.AutoDetect;

namespace UmbraSync.WebAPI.AutoDetect;

public class DiscoveryApiClient
{
    private readonly ILogger<DiscoveryApiClient> _logger;
    private readonly TokenProvider _tokenProvider;
    private readonly DiscoveryConfigProvider _configProvider;
    private readonly HttpClient _httpClient = new();
    private static readonly JsonSerializerOptions JsonOpt = new() { PropertyNameCaseInsensitive = true };

    public DiscoveryApiClient(ILogger<DiscoveryApiClient> logger, TokenProvider tokenProvider, DiscoveryConfigProvider configProvider)
    {
        _logger = logger;
        _tokenProvider = tokenProvider;
        _configProvider = configProvider;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        try
        {
            if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("UmbraSync/AutoDetect");
            }
        }
        catch
        {
            // ignore header parse errors
        }
    }

    public async Task<List<ServerMatch>> QueryAsync(string endpoint, IEnumerable<string> hashes, CancellationToken ct)
    {
        try
        {
            var token = await _tokenProvider.GetOrUpdateToken(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) return [];
            var distinctHashes = hashes.Distinct(StringComparer.Ordinal).ToArray();
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var body = JsonSerializer.Serialize(new
            {
                hashes = distinctHashes,
                salt = _configProvider.SaltB64
            });
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var token2 = await _tokenProvider.ForceRefreshToken(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(token2)) return [];
                using var req2 = new HttpRequestMessage(HttpMethod.Post, endpoint);
                req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token2);
                var body2 = JsonSerializer.Serialize(new
                {
                    hashes = distinctHashes,
                    salt = _configProvider.SaltB64
                });
                req2.Content = new StringContent(body2, Encoding.UTF8, "application/json");
                resp = await _httpClient.SendAsync(req2, ct).ConfigureAwait(false);
            }
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<List<ServerMatch>>(json, JsonOpt) ?? [];
            return result;
        }
        catch (OperationCanceledException oce) when (LogCancellation(oce, ct, "Discovery query"))
        {
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery query failed");
            return [];
        }
    }

    // Le serveur n'identifie la cible que par le jeton obtenu via query : pas d'UID dans la requête
    public async Task<bool> SendRequestAsync(string endpoint, string token, string? displayName, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("Discovery request aborted: no token provided");
                return false;
            }

            var jwt = await _tokenProvider.GetOrUpdateToken(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(jwt)) return false;
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            var body = JsonSerializer.Serialize(new RequestPayload(token, displayName));
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var jwt2 = await _tokenProvider.ForceRefreshToken(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(jwt2)) return false;
                using var req2 = new HttpRequestMessage(HttpMethod.Post, endpoint);
                req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt2);
                var body2 = JsonSerializer.Serialize(new RequestPayload(token, displayName));
                req2.Content = new StringContent(body2, Encoding.UTF8, "application/json");
                using var resp2 = await _httpClient.SendAsync(req2, ct).ConfigureAwait(false);
                return await HandleRequestResponseAsync(resp2, ct).ConfigureAwait(false);
            }
            return await HandleRequestResponseAsync(resp, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce) when (LogCancellation(oce, ct, "Discovery send request"))
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery send request failed");
            return false;
        }
    }

    private async Task<bool> HandleRequestResponseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return true;

        string txt = string.Empty;
        try
        {
            txt = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception readEx)
        {
            _logger.LogDebug(readEx, "Failed to read discovery request error response");
        }
        _logger.LogWarning("Discovery request failed: {code} {reason} {body}", (int)resp.StatusCode, resp.ReasonPhrase, txt);
        return false;
    }

    private sealed record RequestPayload(
        [property: JsonPropertyName("token")]
        string Token,
        [property: JsonPropertyName("displayName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? DisplayName);

    public async Task<bool> PublishAsync(string endpoint, IEnumerable<string> hashes, string? displayName, CancellationToken ct, bool allowRequests = true)
    {
        try
        {
            var jwt = await _tokenProvider.GetOrUpdateToken(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(jwt)) return false;
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            var bodyObj = new
            {
                hashes = hashes.Distinct(StringComparer.Ordinal).ToArray(),
                displayName,
                salt = _configProvider.SaltB64,
                allowRequests
            };
            var body = JsonSerializer.Serialize(bodyObj);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return true;
            if (resp.StatusCode != System.Net.HttpStatusCode.Unauthorized) return false;

            var jwt2 = await _tokenProvider.ForceRefreshToken(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(jwt2)) return false;
            using var req2 = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt2);
            var body2 = JsonSerializer.Serialize(bodyObj);
            req2.Content = new StringContent(body2, Encoding.UTF8, "application/json");
            resp = await _httpClient.SendAsync(req2, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException oce) when (LogCancellation(oce, ct, "Discovery publish"))
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery publish failed");
            return false;
        }
    }

    public async Task DisableAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            var jwt = await _tokenProvider.GetOrUpdateToken(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(jwt)) return;
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var jwt2 = await _tokenProvider.ForceRefreshToken(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(jwt2)) return;
                using var req2 = new HttpRequestMessage(HttpMethod.Post, endpoint);
                req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt2);
                resp = await _httpClient.SendAsync(req2, ct).ConfigureAwait(false);
            }
            if (!resp.IsSuccessStatusCode)
            {
                string txt = string.Empty;
                try
                {
                    txt = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (Exception readEx)
                {
                    _logger.LogDebug(readEx, "Failed to read discovery disable response");
                }
                _logger.LogWarning("Discovery disable failed: {code} {reason} {body}", (int)resp.StatusCode, resp.ReasonPhrase, txt);
            }
        }
        catch (OperationCanceledException oce)
        {
            LogCancellation(oce, ct, "Discovery disable");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery disable failed");
        }
    }

    private bool LogCancellation(OperationCanceledException ex, CancellationToken ct, string operation)
    {
        if (ct.IsCancellationRequested)
        {
            _logger.LogDebug("{operation} cancelled", operation);
            return true;
        }

        if (ex is TaskCanceledException)
        {
            _logger.LogWarning(ex, "{operation} timed out after {timeoutSeconds}s", operation, _httpClient.Timeout.TotalSeconds);
            return true;
        }

        return false;
    }
}

public sealed class ServerMatch
{
    public string Hash { get; set; } = string.Empty;
    public string? Token { get; set; }
    public string? Uid { get; set; }
    public string? DisplayName { get; set; }
    [JsonPropertyName("acceptPairRequests")]
    public bool? AcceptPairRequests { get; set; }
}