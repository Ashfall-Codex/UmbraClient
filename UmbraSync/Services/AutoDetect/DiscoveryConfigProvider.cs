using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using UmbraSync.Services.ServerConfiguration;

namespace UmbraSync.Services.AutoDetect;

public class DiscoveryConfigProvider
{
    private readonly ILogger<DiscoveryConfigProvider> _logger;
    private readonly ServerConfigurationManager _serverManager;
    private readonly TokenProvider _tokenProvider;

    private WellKnownRoot? _config;

    public DiscoveryConfigProvider(ILogger<DiscoveryConfigProvider> logger, ServerConfigurationManager serverManager, TokenProvider tokenProvider)
    {
        _logger = logger;
        _serverManager = serverManager;
        _tokenProvider = tokenProvider;
    }

    public bool HasConfig => _config != null;
    public bool NearbyEnabled => _config?.NearbyDiscovery?.Enabled ?? false;
    public byte[]? Salt => _config?.NearbyDiscovery?.SaltBytes;
    public string? SaltB64 => _config?.NearbyDiscovery?.SaltB64;
    public DateTimeOffset? SaltExpiresAt => _config?.NearbyDiscovery?.SaltExpiresAt;
    public int RefreshSec => _config?.NearbyDiscovery?.RefreshSec ?? 300;
    public int MinQueryIntervalMs => _config?.NearbyDiscovery?.Policies?.MinQueryIntervalMs ?? 2000;
    public int MaxQueryBatch => _config?.NearbyDiscovery?.Policies?.MaxQueryBatch ?? 100;
    public string? PublishEndpoint => _config?.NearbyDiscovery?.Endpoints?.Publish;
    public string? QueryEndpoint => _config?.NearbyDiscovery?.Endpoints?.Query;
    public string? RequestEndpoint => _config?.NearbyDiscovery?.Endpoints?.Request;
    public string? AcceptEndpoint => _config?.NearbyDiscovery?.Endpoints?.Accept;

    public bool TryLoadFromStapled()
    {
        try
        {
            var json = _tokenProvider.GetStapledWellKnown(_serverManager.CurrentApiUrl);
            if (string.IsNullOrEmpty(json)) return false;

            var root = JsonSerializer.Deserialize<WellKnownRoot>(json);
            if (root == null) return false;

            root.NearbyDiscovery?.Hydrate();
            _config = root;
            _logger.LogDebug("Loaded Nearby well-known (stapled), enabled={enabled}, expires={exp}", NearbyEnabled, _config?.NearbyDiscovery?.SaltExpiresAt);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse stapled well-known");
            return false;
        }
    }

    public async Task<bool> TryFetchFromServerAsync(CancellationToken ct = default)
    {
        try
        {
            var baseUrl = _serverManager.CurrentApiUrl
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase);
            // Try likely candidates based on nginx config
            string[] candidates =
            [
                "/.well-known/Umbra/client", // matches provided nginx
                "/.well-known/umbra",        // lowercase variant
            ];

            using var http = new HttpClient();
            try
            {
                var ver = Assembly.GetExecutingAssembly().GetName().Version!;
                http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UmbraSync", $"{ver.Major}.{ver.Minor}.{ver.Build}"));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to set user agent header for discovery request");
            }

            foreach (var path in candidates)
            {
                try
                {
                    var uri = new Uri(new Uri(baseUrl), path);
                    var json = await http.GetStringAsync(uri, ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(json)) continue;

                    var root = JsonSerializer.Deserialize<WellKnownRoot>(json);
                    if (root == null) continue;

                    root.NearbyDiscovery?.Hydrate();
                    _config = root;
                    _logger.LogInformation("Loaded Nearby well-known (http {path}), enabled={enabled}", path, NearbyEnabled);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Nearby well-known fetch failed for {path}", path);
                }
            }

            _logger.LogInformation("Nearby well-known not found via HTTP candidates");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Nearby well-known via HTTP");
            return false;
        }
    }

    public bool IsExpired()
    {
        if (_config?.NearbyDiscovery?.SaltExpiresAt == null) return false;
        return DateTimeOffset.UtcNow > _config.NearbyDiscovery.SaltExpiresAt;
    }

    // DTOs for well-known JSON
    private sealed class WellKnownRoot
    {
        [JsonPropertyName("features")] public Features? Features { get; set; }
        [JsonPropertyName("nearby_discovery")] public Nearby? NearbyDiscovery { get; set; }
    }

    private sealed class Features
    {
        [JsonPropertyName("nearby_discovery")] public bool NearbyDiscovery { get; set; }
    }

    private sealed class Nearby
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("hash_algo")] public string? HashAlgo { get; set; }
        [JsonPropertyName("salt_b64")] public string? SaltB64 { get; set; }
        [JsonPropertyName("salt_expires_at")] public string? SaltExpiresAtRaw { get; set; }
        [JsonPropertyName("refresh_sec")] public int RefreshSec { get; set; } = 300;
        [JsonPropertyName("endpoints")] public Endpoints? Endpoints { get; set; }
        [JsonPropertyName("policies")] public Policies? Policies { get; set; }

        [JsonIgnore] public byte[]? SaltBytes { get; private set; }
        [JsonIgnore] public DateTimeOffset? SaltExpiresAt { get; private set; }

        public void Hydrate()
        {
            try
            {
                SaltBytes = string.IsNullOrEmpty(SaltB64) ? null : Convert.FromBase64String(SaltB64!);
            }
            catch (FormatException)
            {
                SaltBytes = null;
            }
            if (DateTimeOffset.TryParse(SaltExpiresAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)) SaltExpiresAt = dto;
        }
    }

    private sealed class Endpoints
    {
        [JsonPropertyName("publish")] public string? Publish { get; set; }
        [JsonPropertyName("query")] public string? Query { get; set; }
        [JsonPropertyName("request")] public string? Request { get; set; }
        [JsonPropertyName("accept")] public string? Accept { get; set; }
    }

    private sealed class Policies
    {
        [JsonPropertyName("max_query_batch")] public int MaxQueryBatch { get; set; } = 100;
        [JsonPropertyName("min_query_interval_ms")] public int MinQueryIntervalMs { get; set; } = 2000;
        [JsonPropertyName("rate_limit_per_min")] public int RateLimitPerMin { get; set; } = 30;
        [JsonPropertyName("token_ttl_sec")] public int TokenTtlSec { get; set; } = 120;
    }
}