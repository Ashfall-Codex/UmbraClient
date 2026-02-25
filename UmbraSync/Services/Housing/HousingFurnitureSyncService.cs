using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.API.Dto.HousingShare;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI.SignalR;

namespace UmbraSync.Services.Housing;

public sealed class HousingFurnitureSyncService : IHostedService, IMediatorSubscriber
{
    private readonly ILogger<HousingFurnitureSyncService> _logger;
    private readonly MareMediator _mediator;
    private readonly ApiController _apiController;
    private readonly HousingShareManager _housingShareManager;

    public HousingFurnitureSyncService(
        ILogger<HousingFurnitureSyncService> logger,
        MareMediator mediator,
        ApiController apiController,
        HousingShareManager housingShareManager)
    {
        _logger = logger;
        _mediator = mediator;
        _apiController = apiController;
        _housingShareManager = housingShareManager;
    }

    public MareMediator Mediator => _mediator;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting HousingFurnitureSyncService");

        _mediator.Subscribe<HousingPlotEnteredMessage>(this, OnHousingPlotEntered);
        _mediator.Subscribe<HousingPlotLeftMessage>(this, _ => OnHousingPlotLeft());

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping HousingFurnitureSyncService");
        _mediator.UnsubscribeAll(this);
        return Task.CompletedTask;
    }

    private void OnHousingPlotEntered(HousingPlotEnteredMessage msg)
    {
        _logger.LogDebug("Entered housing plot {Server}:{Territory}:{Ward}:{House}",
            msg.LocationInfo.ServerId, msg.LocationInfo.TerritoryId, msg.LocationInfo.WardId, msg.LocationInfo.HouseId);

        _ = TryApplyHousingModsAsync(msg.LocationInfo);
    }

    private void OnHousingPlotLeft()
    {
        _logger.LogDebug("Left housing plot");

        if (_housingShareManager.IsApplied)
        {
            _ = _housingShareManager.RemoveAppliedModsAsync();
        }
    }

    private async Task TryApplyHousingModsAsync(LocationInfo location)
    {
        try
        {
            var shares = await _apiController.HousingShareGetForLocation(location).ConfigureAwait(false);
            if (shares.Count == 0)
            {
                _logger.LogDebug("No housing shares found for this location");
                return;
            }

            var share = shares[0];
            _logger.LogInformation("Found housing share {ShareId} from {Owner} for this location", share.Id, share.OwnerUid);

            await _housingShareManager.DownloadAndApplyAsync(share.Id).ConfigureAwait(false);

            _mediator.Publish(new NotificationMessage(
                Loc.Get("HousingShare.Notification.SyncTitle"),
                string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingShare.Notification.Applied"), share.Description),
                NotificationType.Info,
                TimeSpan.FromSeconds(4)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply housing mods for location {Server}:{Territory}:{Ward}:{House}",
                location.ServerId, location.TerritoryId, location.WardId, location.HouseId);
        }
    }
}
