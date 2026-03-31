using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services.AutoDetect;

public sealed class AutoDetectSuppressionService(ILogger<AutoDetectSuppressionService> logger,
    MareConfigService configService,
    IClientState clientState,
    IDataManager dataManager,
    DalamudUtilService dalamudUtilService,
    MareMediator mediator)
    : IHostedService, IMediatorSubscriber
{
    private static readonly string[] ContentTypeKeywords =
    [
        "dungeon",
        "donjon",
        "raid",
        "trial",
        "défi",
        "front",
        "frontline",
        "pvp",
        "jcj",
        "conflict",
        "conflit"
    ];

    private readonly ILogger<AutoDetectSuppressionService> _logger = logger;
    private readonly MareConfigService _configService = configService;
    private readonly IClientState _clientState = clientState;
    private readonly IDataManager _dataManager = dataManager;
    private readonly MareMediator _mediator = mediator;
    private readonly DalamudUtilService _dalamudUtilService = dalamudUtilService;

    private bool _isSuppressed;
    private bool _hasSavedState;
    private bool _savedDiscoveryEnabled;
    private bool _savedAllowRequests;
    private bool _suppressionWarningShown;
    public bool IsSuppressed => _isSuppressed;

    public MareMediator Mediator => _mediator;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _mediator.Subscribe<ZoneSwitchEndMessage>(this, _ => UpdateSuppressionState());
        _mediator.Subscribe<DalamudLoginMessage>(this, _ => UpdateSuppressionState());
        _mediator.Subscribe<DalamudLogoutMessage>(this, _ => ClearSuppression());
        UpdateSuppressionState();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _mediator.UnsubscribeAll(this);
        return Task.CompletedTask;
    }

    private void UpdateSuppressionState()
    {
        _ = _dalamudUtilService.RunOnFrameworkThread(() =>
        {
            try
            {
                if (!_clientState.IsLoggedIn || _dalamudUtilService.GetPlayerCharacter() == null)
                {
                    ClearSuppression();
                    return;
                }

                uint territoryId = _clientState.TerritoryType;
                bool shouldSuppress = ShouldSuppressForTerritory(territoryId);
                ApplySuppression(shouldSuppress, territoryId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update AutoDetect suppression state");
            }
        });
    }

    private void ApplySuppression(bool shouldSuppress, uint territoryId)
    {
        if (shouldSuppress)
        {
            if (!_isSuppressed)
            {
                _savedDiscoveryEnabled = _configService.Current.EnableAutoDetectDiscovery;
                _savedAllowRequests = _configService.Current.AllowAutoDetectPairRequests;
                _hasSavedState = true;
                _isSuppressed = true;
            }

            bool stateChanged = false;
            if (_configService.Current.EnableAutoDetectDiscovery)
            {
                _configService.Current.EnableAutoDetectDiscovery = false;
                stateChanged = true;
            }
            if (_configService.Current.AllowAutoDetectPairRequests)
            {
                _configService.Current.AllowAutoDetectPairRequests = false;
                stateChanged = true;
            }

            if (stateChanged)
            {
                _logger.LogInformation("AutoDetect temporarily disabled in instanced content (territory {territoryId}).", territoryId);
                if (!_suppressionWarningShown)
                {
                    _suppressionWarningShown = true;
                    _mediator.Publish(new DualNotificationMessage(
                        Loc.Get("AutoDetect.Suppression.DisabledTitle"),
                        Loc.Get("AutoDetect.Suppression.DisabledBody"),
                        NotificationType.Info, TimeSpan.FromSeconds(5)));
                }
            }

            return;
        }

        if (!_isSuppressed) return;

        bool restoreChanged = false;
        bool wasSuppressed = _suppressionWarningShown;
        if (_hasSavedState)
        {
            if (_configService.Current.EnableAutoDetectDiscovery != _savedDiscoveryEnabled)
            {
                _configService.Current.EnableAutoDetectDiscovery = _savedDiscoveryEnabled;
                restoreChanged = true;
            }
            if (_configService.Current.AllowAutoDetectPairRequests != _savedAllowRequests)
            {
                _configService.Current.AllowAutoDetectPairRequests = _savedAllowRequests;
                restoreChanged = true;
            }
        }

        _isSuppressed = false;
        _hasSavedState = false;
        _suppressionWarningShown = false;

        if (restoreChanged || wasSuppressed)
        {
            _logger.LogInformation("AutoDetect restored after leaving instanced content (territory {territoryId}).", territoryId);
            _mediator.Publish(new DualNotificationMessage(
                Loc.Get("AutoDetect.Suppression.RestoredTitle"),
                Loc.Get("AutoDetect.Suppression.RestoredBody"),
                NotificationType.Info, TimeSpan.FromSeconds(5)));
        }
    }

    private void ClearSuppression()
    {
        if (!_isSuppressed) return;
        _isSuppressed = false;
        if (_hasSavedState)
        {
            _configService.Current.EnableAutoDetectDiscovery = _savedDiscoveryEnabled;
            _configService.Current.AllowAutoDetectPairRequests = _savedAllowRequests;
        }
        _hasSavedState = false;
        _suppressionWarningShown = false;
    }

    private bool ShouldSuppressForTerritory(uint territoryId)
    {
        if (territoryId == 0) return false;

        var cfcSheet = _dataManager.GetExcelSheet<ContentFinderCondition>();
        var cfc = cfcSheet.FirstOrDefault(c => c.TerritoryType.RowId == territoryId);
        if (cfc.RowId == 0) return false;

        if (MatchesSuppressionKeyword(cfc.Name.ToString())) return true;

        var contentType = cfc.ContentType.Value;
        if (contentType.RowId == 0) return false;

        return MatchesSuppressionKeyword(contentType.Name.ToString());
    }

    private static bool MatchesSuppressionKeyword(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return ContentTypeKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}