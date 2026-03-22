using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace UmbraSync.MareConfiguration;

public class ConfigurationMigrator(ILogger<ConfigurationMigrator> logger, MareConfigService mareConfig) : IHostedService
{
    private readonly ILogger<ConfigurationMigrator> _logger = logger;
    private readonly MareConfigService _mareConfig = mareConfig;

    public void Migrate()
    {
        try
        {
            var path = _mareConfig.ConfigurationPath;
            if (!File.Exists(path)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            bool changed = false;

            if (root.TryGetProperty("EnableAutoSyncDiscovery", out var enableAutoSync) &&
                _mareConfig.Current.EnableAutoDetectDiscovery != enableAutoSync.GetBoolean())
            {
                _mareConfig.Current.EnableAutoDetectDiscovery = enableAutoSync.GetBoolean();
                changed = true;
            }
            if (root.TryGetProperty("AllowAutoSyncPairRequests", out var allowAutoSync) &&
                _mareConfig.Current.AllowAutoDetectPairRequests != allowAutoSync.GetBoolean())
            {
                _mareConfig.Current.AllowAutoDetectPairRequests = allowAutoSync.GetBoolean();
                changed = true;
            }
            if (root.TryGetProperty("AutoSyncMaxDistanceMeters", out var maxDistSync) &&
                maxDistSync.TryGetInt32(out var md) &&
                _mareConfig.Current.AutoDetectMaxDistanceMeters != md)
            {
                _mareConfig.Current.AutoDetectMaxDistanceMeters = md;
                changed = true;
            }
            if (root.TryGetProperty("AutoSyncMuteMinutes", out var muteSync) &&
                muteSync.TryGetInt32(out var mm) &&
                _mareConfig.Current.AutoDetectDeclineCooldownMinutes != mm)
            {
                _mareConfig.Current.AutoDetectDeclineCooldownMinutes = mm;
                changed = true;
            }

            // Migration : ChatTargetSoundEnabled → ChatTargetSoundMasterEnabled
            if (!_mareConfig.Current.ChatTargetSoundMasterEnabled
                && (_mareConfig.Current.ChatTargetSoundEnabled || _mareConfig.Current.ChatTargetSoundReverseEnabled))
            {
                _mareConfig.Current.ChatTargetSoundMasterEnabled = true;
                changed = true;
            }

            if (changed)
            {
                _logger.LogInformation("Migrated config");
                _mareConfig.Save();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Configuration migration failed");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Migrate();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}