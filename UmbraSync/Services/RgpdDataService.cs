using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

public class RgpdDataService : DisposableMediatorSubscriberBase
{
    private readonly MareConfigService _configService;
    private readonly NotesConfigService _notesConfigService;
    private readonly ServerTagConfigService _serverTagConfigService;
    private readonly RpConfigService _rpConfigService;
    private readonly ServerBlockConfigService _serverBlockConfigService;
    private readonly EstablishmentConfigService _establishmentConfigService;
    private readonly SyncshellConfigService _syncshellConfigService;
    private readonly CharaDataConfigService _charaDataConfigService;
    private readonly string _configDirectory;
    private static readonly TimeSpan BackupPurgeDelay = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RgpdDataService(ILogger<RgpdDataService> logger, MareMediator mediator,
        MareConfigService configService,
        NotesConfigService notesConfigService,
        ServerTagConfigService serverTagConfigService,
        RpConfigService rpConfigService,
        ServerBlockConfigService serverBlockConfigService,
        EstablishmentConfigService establishmentConfigService,
        SyncshellConfigService syncshellConfigService,
        CharaDataConfigService charaDataConfigService,
        Dalamud.Plugin.IDalamudPluginInterface pluginInterface) : base(logger, mediator)
    {
        _configService = configService;
        _notesConfigService = notesConfigService;
        _serverTagConfigService = serverTagConfigService;
        _rpConfigService = rpConfigService;
        _serverBlockConfigService = serverBlockConfigService;
        _establishmentConfigService = establishmentConfigService;
        _syncshellConfigService = syncshellConfigService;
        _charaDataConfigService = charaDataConfigService;
        _configDirectory = pluginInterface.ConfigDirectory.FullName;

        Mediator.Subscribe<RgpdDataExportRequestMessage>(this, (msg) => _ = Task.Run(ExportLocalData));
        Mediator.Subscribe<RgpdLocalDataDeletionRequestMessage>(this, (msg) => _ = Task.Run(DeleteLocalData));
    }
    public bool IsRgpdConsentValid => _configService.Current.RgpdConsentGiven
        && _configService.Current.AcceptedRgpdVersion >= MareConfig.ExpectedRgpdVersion;
    
    public bool IsRgpdConsentOutdated => _configService.Current.RgpdConsentGiven
        && _configService.Current.AcceptedRgpdVersion < MareConfig.ExpectedRgpdVersion;

    public void AcceptRgpdConsent(bool dataCollection, bool dataSharing, bool thirdPartyPlugins)
    {
        _configService.Current.RgpdConsentGiven = true;
        _configService.Current.RgpdConsentDate = DateTime.UtcNow;
        _configService.Current.AcceptedRgpdVersion = MareConfig.ExpectedRgpdVersion;
        _configService.Current.RgpdConsentDataCollection = dataCollection;
        _configService.Current.RgpdConsentDataSharing = dataSharing;
        _configService.Current.RgpdConsentThirdPartyPlugins = thirdPartyPlugins;
        _configService.Save();
        Mediator.Publish(new RgpdConsentUpdatedMessage(true));
    }

    public void RevokeRgpdConsent()
    {
        _configService.Current.RgpdConsentGiven = false;
        _configService.Current.RgpdConsentDate = null;
        _configService.Current.AcceptedRgpdVersion = 0;
        _configService.Current.RgpdConsentDataCollection = false;
        _configService.Current.RgpdConsentDataSharing = false;
        _configService.Current.RgpdConsentThirdPartyPlugins = false;
        _configService.Save();
        Mediator.Publish(new RgpdConsentUpdatedMessage(false));
    }

    private string NetworkDiagnosticDirectory => Path.Combine(_configDirectory, "NetworkDiag");

    private string ResolveExportDirectory()
        => !string.IsNullOrEmpty(_configService.Current.ExportFolder)
            ? _configService.Current.ExportFolder
            : _configDirectory;

    private void ExportLocalData()
    {
        try
        {
            var exportData = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["export_date"] = DateTime.UtcNow.ToString("O"),
                ["export_format_version"] = 2,
                ["consent"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["given"] = _configService.Current.RgpdConsentGiven,
                    ["accepted_version"] = _configService.Current.AcceptedRgpdVersion,
                    ["expected_version"] = MareConfig.ExpectedRgpdVersion,
                    ["date"] = _configService.Current.RgpdConsentDate?.ToString("O"),
                    ["data_collection"] = _configService.Current.RgpdConsentDataCollection,
                    ["data_sharing"] = _configService.Current.RgpdConsentDataSharing,
                    ["third_party_plugins"] = _configService.Current.RgpdConsentThirdPartyPlugins,
                },
                ["settings"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["cache_folder"] = _configService.Current.CacheFolder,
                    ["export_folder"] = _configService.Current.ExportFolder,
                    ["ui_language"] = _configService.Current.UiLanguage,
                    ["network_diagnostic_log_enabled"] = _configService.Current.EnableNetworkDiagnosticLog,
                },
                ["rp_profiles"] = _rpConfigService.Current.CharacterProfiles,
                ["pair_notes"] = _notesConfigService.Current.ServerNotes,
                ["pair_groups"] = _serverTagConfigService.Current.ServerTagStorage,
                ["blocked_players"] = _serverBlockConfigService.Current.ServerBlocks,
                ["establishments"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["bookmarked"] = _establishmentConfigService.Current.BookmarkedEstablishments,
                    ["syncslot_bindings"] = _establishmentConfigService.Current.EstablishmentSyncSlotBindings,
                },
                ["syncshells"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["favorites"] = _syncshellConfigService.Current.FavoriteSyncshells,
                    ["collection_overrides"] = _syncshellConfigService.Current.GroupCollectionOverrides,
                },
                ["chara_data"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["favorites"] = _charaDataConfigService.Current.FavoriteCodes,
                    ["last_saved_location"] = _charaDataConfigService.Current.LastSavedCharaDataLocation,
                    ["mcdf_local_folder"] = _charaDataConfigService.Current.McdfLocalFolder,
                },
                ["network_diagnostic_logs"] = CollectNetworkDiagnosticLogs(),
                ["file_cache"] = SummarizeFileCache(),
            };

            var exportDir = ResolveExportDirectory();
            Directory.CreateDirectory(exportDir);
            var exportPath = Path.Combine(exportDir, $"umbrasync_rgpd_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            var json = JsonSerializer.Serialize(exportData, JsonOptions);
            File.WriteAllText(exportPath, json);

            Logger.LogInformation("RGPD local data exported to {path}", exportPath);
            Mediator.Publish(new RgpdDataExportReadyMessage(exportPath));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to export RGPD local data");
            Mediator.Publish(new RgpdDataExportReadyMessage(null));
        }
    }

    private List<Dictionary<string, object?>> CollectNetworkDiagnosticLogs()
    {
        var result = new List<Dictionary<string, object?>>();
        try
        {
            if (!Directory.Exists(NetworkDiagnosticDirectory)) return result;
            foreach (var file in Directory.EnumerateFiles(NetworkDiagnosticDirectory))
            {
                var info = new FileInfo(file);
                result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = info.FullName,
                    ["size_bytes"] = info.Length,
                    ["last_write_utc"] = info.LastWriteTimeUtc.ToString("O"),
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not enumerate network diagnostic logs for RGPD export");
        }
        return result;
    }

    private Dictionary<string, object?> SummarizeFileCache()
    {
        var summary = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["folder"] = _configService.Current.CacheFolder,
        };
        try
        {
            var folder = _configService.Current.CacheFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return summary;

            long count = 0;
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                count++;
                bytes += new FileInfo(file).Length;
            }
            summary["file_count"] = count;
            summary["size_bytes"] = bytes;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not summarize file cache for RGPD export");
        }
        return summary;
    }

    private void DeleteLocalData()
    {
        try
        {

            _rpConfigService.Current.CharacterProfiles.Clear();
            _rpConfigService.Save();

            _notesConfigService.Current.ServerNotes.Clear();
            _notesConfigService.Save();

            _serverTagConfigService.Current.ServerTagStorage.Clear();
            _serverTagConfigService.Save();

            _serverBlockConfigService.Current.ServerBlocks.Clear();
            _serverBlockConfigService.Save();

            _establishmentConfigService.Current.BookmarkedEstablishments.Clear();
            _establishmentConfigService.Current.EstablishmentSyncSlotBindings.Clear();
            _establishmentConfigService.Save();

            _syncshellConfigService.Current.FavoriteSyncshells.Clear();
            _syncshellConfigService.Current.GroupCollectionOverrides.Clear();
            _syncshellConfigService.Save();

            _charaDataConfigService.Current.FavoriteCodes.Clear();
            _charaDataConfigService.Current.LastSavedCharaDataLocation = string.Empty;
            _charaDataConfigService.Save();

            DeleteNetworkDiagnosticLogs();
            DeletePreviousExports();

            RevokeRgpdConsent();


            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(BackupPurgeDelay).ConfigureAwait(false);
                    PurgeConfigBackups();
                    Logger.LogInformation("RGPD local data deleted");
                    Mediator.Publish(new RgpdLocalDataDeletionCompleteMessage());
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to purge config backups during RGPD deletion");
                    Mediator.Publish(new RgpdLocalDataDeletionCompleteMessage());
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete RGPD local data");
            Mediator.Publish(new RgpdLocalDataDeletionCompleteMessage());
        }
    }

    private void DeleteNetworkDiagnosticLogs()
    {
        try
        {
            if (!Directory.Exists(NetworkDiagnosticDirectory)) return;
            foreach (var file in Directory.GetFiles(NetworkDiagnosticDirectory))
            {
                try { File.Delete(file); }
                catch (IOException) { /* fichier en cours d'écriture par la session active */ }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not delete network diagnostic logs");
        }
    }

    private void DeletePreviousExports()
    {
        try
        {
            var exportDir = ResolveExportDirectory();
            if (!Directory.Exists(exportDir)) return;
            foreach (var file in Directory.GetFiles(exportDir, "umbrasync_rgpd_export_*.json"))
                File.Delete(file);
            foreach (var file in Directory.GetFiles(exportDir, "umbrasync_server_export_*.json"))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not delete previous RGPD exports");
        }
    }

    private void PurgeConfigBackups()
    {
        var backupFolder = Path.Combine(_configDirectory, ConfigurationSaveService.BackupFolder);
        if (!Directory.Exists(backupFolder)) return;

        string[] purgedConfigs =
        [
            RpConfigService.ConfigName,
            NotesConfigService.ConfigName,
            ServerTagConfigService.ConfigName,
            ServerBlockConfigService.ConfigName,
            EstablishmentConfigService.ConfigName,
            SyncshellConfigService.ConfigName,
            CharaDataConfigService.ConfigName,
        ];

        foreach (var configName in purgedConfigs)
        {
            var prefix = configName.Split('.')[0];
            foreach (var file in Directory.GetFiles(backupFolder, prefix + "*"))
            {
                try { File.Delete(file); }
                catch (IOException ex) { Logger.LogWarning(ex, "Could not delete config backup {file}", file); }
            }
        }
    }
}
