using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using UmbraSync.MareConfiguration;

namespace UmbraSync.Services.Housing;

public sealed class ArrPathResolver
{
    private const string ArrPluginName = "ARealmRepopulated";
    private const string ScenariosSubfolder = "Scenarios";

    private readonly ILogger<ArrPathResolver> _logger;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly MareConfigService _configService;
    private bool _missingLogged;

    public ArrPathResolver(ILogger<ArrPathResolver> logger, IDalamudPluginInterface pluginInterface,
        MareConfigService configService)
    {
        _logger = logger;
        _pluginInterface = pluginInterface;
        _configService = configService;
    }
    public string? TryGetScenariosPath()
    {
        // Override manuel : prioritaire si défini et valide, sinon retour à la détection auto.
        var overridePath = _configService.Current.ArrScenariosPathOverride;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (Directory.Exists(overridePath))
            {
                return overridePath;
            }
            if (!_missingLogged)
            {
                _logger.LogWarning("ArrScenariosPathOverride configuré mais introuvable : {Path}. Retour à la détection automatique.", overridePath);
                _missingLogged = true;
            }
        }

        DirectoryInfo? pluginConfigsRoot = _pluginInterface.ConfigDirectory.Parent;
        if (pluginConfigsRoot == null)
        {
            return null;
        }

        string candidate = Path.Combine(pluginConfigsRoot.FullName, ArrPluginName, ScenariosSubfolder);
        if (!Directory.Exists(candidate))
        {
            if (!_missingLogged)
            {
                _logger.LogWarning("Dossier ARR Scenarios introuvable : {Path}. La synchro de scénarios est désactivée pour cette session.", candidate);
                _missingLogged = true;
            }
            return null;
        }

        return candidate;
    }

    /// <summary>
    /// True si ARR est détecté (dossier Scenarios accessible).
    /// </summary>
    public bool IsArrAvailable() => TryGetScenariosPath() != null;
}
