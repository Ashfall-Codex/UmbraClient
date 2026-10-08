using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;

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
    private readonly TransientConfigService _transientConfigService;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly NotificationTracker _notificationTracker;
    private readonly UmbraProfileManager _umbraProfileManager;
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
        TransientConfigService transientConfigService,
        PlayerPerformanceConfigService playerPerformanceConfigService,
        NotificationTracker notificationTracker,
        UmbraProfileManager umbraProfileManager,
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
        _transientConfigService = transientConfigService;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _notificationTracker = notificationTracker;
        _umbraProfileManager = umbraProfileManager;
        _configDirectory = pluginInterface.ConfigDirectory.FullName;

        Mediator.Subscribe<RgpdDataExportRequestMessage>(this, (msg) => _ = Task.Run(ExportLocalData));
        Mediator.Subscribe<RgpdLocalDataDeletionRequestMessage>(this, (msg) => _ = Task.Run(DeleteLocalData));
    }
    public bool IsRgpdConsentValid => _configService.Current.HasValidRgpdConsent();

    public bool IsRgpdConsentOutdated => _configService.Current.RgpdConsentGiven
        && _configService.Current.AcceptedRgpdVersion < MareConfig.ExpectedRgpdVersion;

    /// <summary>Traitements facultatifs : chacun correspond à un réglage que l'utilisateur peut couper à tout moment.</summary>
    /// <param name="NearbyDiscovery">Détection des joueurs UmbraSync à proximité.</param>
    /// <param name="ProximityPosition">Envoi de la position pour les slots et les établissements proches.</param>
    /// <param name="TypingIndicator">Indicateur d'écriture transmis aux paires.</param>
    /// <param name="PluginSharing">Lecture des fiches RP par les autres plugins installés.</param>
    public readonly record struct OptionalChoices(bool NearbyDiscovery, bool ProximityPosition, bool TypingIndicator, bool PluginSharing);

    public OptionalChoices CurrentOptionalChoices => new(
        _configService.Current.EnableAutoDetectDiscovery,
        _configService.Current.EnableSlotNotifications || _establishmentConfigService.Current.EnableProximityNotifications,
        _configService.Current.TypingIndicatorEnabled,
        _configService.Current.ShareRpProfileWithPlugins);

    public void AcceptRgpdConsent(OptionalChoices choices)
    {
        _configService.Current.RgpdConsentGiven = true;
        _configService.Current.RgpdConsentDate = DateTime.UtcNow;
        _configService.Current.AcceptedRgpdVersion = MareConfig.ExpectedRgpdVersion;
        _configService.Current.RgpdConsentDataCollection = true;
        _configService.Current.RgpdConsentDataSharing = true;
        _configService.Current.RgpdConsentThirdPartyPlugins = choices.PluginSharing;

        _configService.Current.EnableAutoDetectDiscovery = choices.NearbyDiscovery;
        _configService.Current.AllowAutoDetectPairRequests = choices.NearbyDiscovery;
        _configService.Current.EnableSlotNotifications = choices.ProximityPosition;
        _configService.Current.TypingIndicatorEnabled = choices.TypingIndicator;
        _configService.Current.ShareRpProfileWithPlugins = choices.PluginSharing;
        _configService.Save();

        _establishmentConfigService.Current.EnableProximityNotifications = choices.ProximityPosition;
        _establishmentConfigService.Save();

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

    private const string ProfileCachePattern = "profile_cache_*.json";

    private string NetworkDiagnosticDirectory => Path.Combine(_configDirectory, "NetworkDiag");

    private string EventLogDirectory => Path.Combine(_configDirectory, "eventlog");

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
                ["export_format_version"] = 3,
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
                ["optional_processing"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["nearby_discovery"] = _configService.Current.EnableAutoDetectDiscovery,
                    ["nearby_pair_requests"] = _configService.Current.AllowAutoDetectPairRequests,
                    ["nearby_visible_when_afk"] = _configService.Current.AutoDetectPublishWhenAfk,
                    ["nearby_visible_when_not_roleplaying"] = _configService.Current.AutoDetectPublishWhenNotRoleplaying,
                    ["slot_position"] = _configService.Current.EnableSlotNotifications,
                    ["establishment_position"] = _establishmentConfigService.Current.EnableProximityNotifications,
                    ["typing_indicator"] = _configService.Current.TypingIndicatorEnabled,
                    ["rp_profile_shared_with_plugins"] = _configService.Current.ShareRpProfileWithPlugins,
                    ["event_log"] = _configService.Current.LogEvents,
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
                ["pair_overrides"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["sync"] = _configService.Current.PairSyncOverrides,
                    ["target_sound"] = _configService.Current.PairTargetSoundOverrides,
                    ["performance"] = _playerPerformanceConfigService.Current.UIDsToOverride,
                    ["nearby_blocked"] = _configService.Current.AutoDetectBlockedUids,
                    ["delegated_scenarios"] = _configService.Current.KnownDelegatedScenarios,
                },
                ["slots"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["last_joined"] = _transientConfigService.Current.LastJoinedSlotSyncshellPerUid,
                    ["pending_leave"] = _transientConfigService.Current.PendingSlotLeaveGidPerUid,
                },
                ["notifications"] = _notificationTracker.GetEntries(),
                ["cached_profiles_of_others"] = DescribeFiles(_configDirectory, ProfileCachePattern),
                ["event_logs"] = DescribeFiles(EventLogDirectory, "*"),
                ["network_diagnostic_logs"] = DescribeFiles(NetworkDiagnosticDirectory, "*"),
                ["file_cache"] = SummarizeFileCache(),
                ["not_included"] = new[]
                {
                    "server.json : clés secrètes de connexion, volontairement exclues de l'export",
                    "housing_npc_scenarios.json : scènes PNJ que vous avez créées, conservées sur place",
                },
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

    private List<Dictionary<string, object?>> DescribeFiles(string directory, string pattern)
    {
        var result = new List<Dictionary<string, object?>>();
        try
        {
            if (!Directory.Exists(directory)) return result;
            foreach (var file in Directory.EnumerateFiles(directory, pattern))
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
            Logger.LogWarning(ex, "Could not enumerate {pattern} files for RGPD export", pattern);
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

            _transientConfigService.Current.LastJoinedSlotSyncshellPerUid.Clear();
            _transientConfigService.Current.PendingSlotLeaveGidPerUid.Clear();
            _transientConfigService.Save();

            _playerPerformanceConfigService.Current.UIDsToOverride.Clear();
            _playerPerformanceConfigService.Save();

            _configService.Current.PairSyncOverrides.Clear();
            _configService.Current.PairTargetSoundOverrides.Clear();
            _configService.Current.AutoDetectBlockedUids.Clear();
            _configService.Current.KnownDelegatedScenarios.Clear();

            _notificationTracker.Clear();
            _umbraProfileManager.ClearPersistedProfileCache();

            DeleteFiles(_configDirectory, ProfileCachePattern);
            DeleteFiles(EventLogDirectory, "*");
            DeleteFiles(NetworkDiagnosticDirectory, "*");
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

    private void DeleteFiles(string directory, string pattern)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.GetFiles(directory, pattern))
            {
                try { File.Delete(file); }
                catch (IOException) { /* fichier en cours d'écriture par la session active */ }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not delete {pattern} files", pattern);
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
            TransientConfigService.ConfigName,
            PlayerPerformanceConfigService.ConfigName,
            NotificationsConfigService.ConfigName,
            MareConfigService.ConfigName,
        ];

        foreach (var configName in purgedConfigs)
        {
            var prefix = configName.Split('.')[0];
            foreach (var file in Directory.GetFiles(backupFolder, prefix + ".*"))
            {
                try { File.Delete(file); }
                catch (IOException ex) { Logger.LogWarning(ex, "Could not delete config backup {file}", file); }
            }
        }
    }
}
