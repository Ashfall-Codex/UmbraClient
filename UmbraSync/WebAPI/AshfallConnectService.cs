using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Logging;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.WebAPI.SignalR;

namespace UmbraSync.WebAPI;

/// Client REST pour Ashfall Connect (passe par UmbraServer côté /main/connect/*).
/// Le plugin n'appelle JAMAIS Connect directement : c'est UmbraServer qui pousse les codes
/// vers Connect via son service token interne. Le plugin parle uniquement à UmbraServer.
public sealed class AshfallConnectService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ServerConfigurationManager _serverManager;
    private readonly TokenProvider _tokenProvider;
    private readonly ILogger<AshfallConnectService> _logger;

    public AshfallConnectService(ServerConfigurationManager serverManager, TokenProvider tokenProvider, ILogger<AshfallConnectService> logger)
    {
        _serverManager = serverManager;
        _tokenProvider = tokenProvider;
        _logger = logger;
        _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 });
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        var versionString = ver is null ? "unknown" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UmbraSync", versionString));
    }

    public void Dispose() => _httpClient.Dispose();

    public sealed record GenerateLinkCodeResult(string Code, DateTimeOffset ExpiresAt);

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

        using var req = new HttpRequestMessage(HttpMethod.Post, uri);
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
}
