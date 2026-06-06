using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Logging;
using UmbraSync.Services;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.WebAPI.SignalR;

namespace UmbraSync.WebAPI;

public sealed class AshfallConnectService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ServerConfigurationManager _serverManager;
    private readonly TokenProvider _tokenProvider;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ILogger<AshfallConnectService> _logger;

    public AshfallConnectService(ServerConfigurationManager serverManager, TokenProvider tokenProvider, DalamudUtilService dalamudUtil, ILogger<AshfallConnectService> logger)
    {
        _serverManager = serverManager;
        _tokenProvider = tokenProvider;
        _dalamudUtil = dalamudUtil;
        _logger = logger;
        _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 });
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        var versionString = ver is null ? "unknown" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UmbraSync", versionString));
    }

    public void Dispose() => _httpClient.Dispose();

    public sealed record GenerateLinkCodeResult(string Code, DateTimeOffset ExpiresAt);
    public sealed record LinkCodeStatus(string Status, string? LinkedTo, DateTimeOffset? ExpiresAt, DateTimeOffset? ConsumedAt);
    public sealed record MyStatusResult(bool ConnectEnabled, bool Linked, string? Level, DateTimeOffset? Since);

    public async Task<MyStatusResult?> GetMyStatusAsync(CancellationToken token)
    {
        var jwt = await _tokenProvider.GetToken().ConfigureAwait(false);
        if (string.IsNullOrEmpty(jwt)) return null;

        var baseUrl = _serverManager.CurrentApiUrl
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        var uri = new Uri($"{baseUrl}/main/connect/my-status");

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        try
        {
            using var res = await _httpClient.SendAsync(req, token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadFromJsonAsync<MyStatusResult>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LinkCodeStatus?> GetLinkCodeStatusAsync(string code, CancellationToken token)
    {
        var jwt = await _tokenProvider.GetToken().ConfigureAwait(false);
        if (string.IsNullOrEmpty(jwt)) return null;

        var baseUrl = _serverManager.CurrentApiUrl
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        var uri = new Uri($"{baseUrl}/main/connect/link-status/{Uri.EscapeDataString(code)}");

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        try
        {
            using var res = await _httpClient.SendAsync(req, token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadFromJsonAsync<LinkCodeStatus>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<GenerateLinkCodeResult> GenerateLinkCodeAsync(CancellationToken token)
    {
        var jwt = await _tokenProvider.GetToken().ConfigureAwait(false);
        if (string.IsNullOrEmpty(jwt))
            throw new InvalidOperationException("JWT indisponible — la connexion à UmbraServer doit être active.");

        var baseUrl = _serverManager.CurrentApiUrl
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        var uri = new Uri($"{baseUrl}/main/connect/generate-link-code");
        
        var characters = await CollectCharactersForCurrentKeyAsync().ConfigureAwait(false);
        var payload = new { characters };
        var serialized = System.Text.Json.JsonSerializer.Serialize(payload);
        if (ContainsSecretLikePattern(serialized))
        {
            _logger.LogError("Ashfall garde-fou : pattern de hash détecté dans le payload Connect. Envoi ABORTÉ.");
            throw new InvalidOperationException("Garde-fou sécurité Ashfall : un pattern ressemblant à une clé/hash a été détecté dans le payload. Aucune donnée n'a été envoyée.");
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(serialized, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        using var res = await _httpClient.SendAsync(req, token).ConfigureAwait(false);
        if (res.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            throw new InvalidOperationException("Ashfall Connect n'est pas activé sur ce serveur UmbraSync.");
        if (res.StatusCode == System.Net.HttpStatusCode.BadGateway)
            throw new InvalidOperationException("UmbraServer n'arrive pas à joindre Connect.");
        res.EnsureSuccessStatusCode();

        var dto = await res.Content.ReadFromJsonAsync<GenerateLinkCodeDto>(token).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("Réponse vide d'UmbraServer.");
        return new GenerateLinkCodeResult(dto.Code ?? string.Empty, dto.ExpiresAt);
    }

    private sealed class GenerateLinkCodeDto
    {
        public string? Code { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    // Pousse la liste actuelle des persos partageant la SecretKey vers Connect.
    public async Task<SyncResult> SyncCharactersAsync(CancellationToken token)
    {
        var jwt = await _tokenProvider.GetToken().ConfigureAwait(false);
        if (string.IsNullOrEmpty(jwt)) return SyncResult.Failed;

        var baseUrl = _serverManager.CurrentApiUrl
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        var uri = new Uri($"{baseUrl}/main/connect/sync-characters");

        var characters = await CollectCharactersForCurrentKeyAsync().ConfigureAwait(false);
        var payload = new { characters };
        var serialized = System.Text.Json.JsonSerializer.Serialize(payload);
        if (ContainsSecretLikePattern(serialized))
        {
            _logger.LogError("Ashfall garde-fou : pattern de hash détecté dans le payload Connect (sync). Envoi ABORTÉ.");
            return SyncResult.Failed;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(serialized, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        try
        {
            using var res = await _httpClient.SendAsync(req, token).ConfigureAwait(false);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return SyncResult.NotLinked;
            if (res.IsSuccessStatusCode) return SyncResult.Synced;
            _logger.LogWarning("Sync Connect a renvoyé {Status}", res.StatusCode);
            return SyncResult.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de la synchronisation des personnages vers Connect");
            return SyncResult.Failed;
        }
    }

    public enum SyncResult { Synced, NotLinked, Failed }

    private async Task<List<CharacterDto>> CollectCharactersForCurrentKeyAsync()
    {
        try
        {
            var server = _serverManager.CurrentServer;
            if (server is null) return new();

            // Les lectures d'état du jeu (nom du joueur, monde) exigent le framework thread ;
            // cette méthode est appelée depuis des threads async (auto-sync au connect).
            var (playerName, playerWorldId) = await _dalamudUtil.RunOnFrameworkThread(
                () => (_dalamudUtil.GetPlayerName(), _dalamudUtil.GetHomeWorldId())).ConfigureAwait(false);
            if (string.IsNullOrEmpty(playerName) || playerWorldId == 0) return new();

            var currentAuth = server.Authentications.FirstOrDefault(a =>
                string.Equals(a.CharacterName, playerName, StringComparison.Ordinal) && a.WorldId == playerWorldId);
            if (currentAuth is null) return new();

            var worldData = _dalamudUtil.WorldData.Value;

            return server.Authentications
                .Where(a => a.SecretKeyIdx == currentAuth.SecretKeyIdx && !string.IsNullOrEmpty(a.CharacterName))
                .Select(a => new CharacterDto(
                    a.CharacterName,
                    worldData.GetValueOrDefault((ushort)a.WorldId, "")))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de la collecte des persos pour la SecretKey courante");
            return new();
        }
    }

    public sealed record CharacterDto(string Name, string World);
    private static readonly System.Text.RegularExpressions.Regex Sha256Pattern =
        new(@"\b[0-9a-fA-F]{64}\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool ContainsSecretLikePattern(string json)
    {
        // Détecte les hash SHA-256 (64 hex chars) - signature des SecretKey UmbraSync hashées.
        if (Sha256Pattern.IsMatch(json)) return true;

        // Sentinel mots-clés : si un de ces mots apparaît comme nom de propriété JSON, c'est qu'on est en train d'envoyer un truc qu'on ne devrait pas.
        var lowered = json.ToLowerInvariant();
        string[] forbidden = ["\"secretkey\"", "\"hashedkey\"", "\"hashedsecretkey\"", "\"password\"", "\"jwt\"", "\"bearer\""];
        foreach (var word in forbidden)
            if (lowered.Contains(word, StringComparison.Ordinal)) return true;

        return false;
    }
}
