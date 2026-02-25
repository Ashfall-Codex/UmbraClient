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
    private readonly string _configDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RgpdDataService(ILogger<RgpdDataService> logger, MareMediator mediator,
        MareConfigService configService,
        Dalamud.Plugin.IDalamudPluginInterface pluginInterface) : base(logger, mediator)
    {
        _configService = configService;
        _configDirectory = pluginInterface.ConfigDirectory.FullName;

        Mediator.Subscribe<RgpdDataExportRequestMessage>(this, (msg) => Task.Run(ExportLocalData));
        Mediator.Subscribe<RgpdLocalDataDeletionRequestMessage>(this, (msg) => Task.Run(DeleteLocalData));
    }

    public bool IsRgpdConsentValid => _configService.Current.RgpdConsentGiven;

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

    private void ExportLocalData()
    {
        try
        {
            var exportData = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["export_date"] = DateTime.UtcNow.ToString("O"),
                ["rgpd_version"] = _configService.Current.AcceptedRgpdVersion,
                ["consent_date"] = _configService.Current.RgpdConsentDate?.ToString("O") ?? "N/A",
                ["consent_data_collection"] = _configService.Current.RgpdConsentDataCollection,
                ["consent_data_sharing"] = _configService.Current.RgpdConsentDataSharing,
                ["consent_third_party_plugins"] = _configService.Current.RgpdConsentThirdPartyPlugins,
                ["cache_folder"] = _configService.Current.CacheFolder,
                ["export_folder"] = _configService.Current.ExportFolder,
                ["ui_language"] = _configService.Current.UiLanguage,
            };

            var notesPath = Path.Combine(_configDirectory, "notes.json");
            if (File.Exists(notesPath))
            {
                exportData["notes_file"] = notesPath;
                exportData["notes_file_size_bytes"] = new FileInfo(notesPath).Length;
            }

            var exportDir = !string.IsNullOrEmpty(_configService.Current.ExportFolder)
                ? _configService.Current.ExportFolder
                : _configDirectory;

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
        }
    }

    private void DeleteLocalData()
    {
        try
        {
            var notesPath = Path.Combine(_configDirectory, "notes.json");
            if (File.Exists(notesPath)) File.Delete(notesPath);

            var exportDir = !string.IsNullOrEmpty(_configService.Current.ExportFolder)
                ? _configService.Current.ExportFolder
                : _configDirectory;

            if (Directory.Exists(exportDir))
            {
                foreach (var file in Directory.GetFiles(exportDir, "umbrasync_rgpd_export_*.json"))
                    File.Delete(file);
            }

            RevokeRgpdConsent();

            Logger.LogInformation("RGPD local data deleted");
            Mediator.Publish(new RgpdLocalDataDeletionCompleteMessage());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete RGPD local data");
        }
    }
}
