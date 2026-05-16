using Dalamud.Plugin;
using Microsoft.Extensions.Logging;

namespace UmbraSync.Services.Housing;

public sealed class ArrPathResolver
{
    private const string ArrPluginName = "ARealmRepopulated";
    private const string ScenariosSubfolder = "Scenarios";

    private readonly ILogger<ArrPathResolver> _logger;
    private readonly IDalamudPluginInterface _pluginInterface;
    private bool _missingLogged;

    public ArrPathResolver(ILogger<ArrPathResolver> logger, IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        _pluginInterface = pluginInterface;
    }

    /// <summary>
    /// Retourne le chemin absolu vers le dossier Scenarios d'ARR, ou null si introuvable.
    /// Un log warn n'est émis qu'une seule fois par session si le dossier est manquant.
    /// </summary>
    public string? TryGetScenariosPath()
    {
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
