using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

public class EstablishmentProximityService : MediatorSubscriberBase, IDisposable
{
    private readonly ApiController _apiController;
    private readonly EstablishmentConfigService _configService;

    private Vector3 _lastQueryPosition = Vector3.Zero;
    private EstablishmentDto? _currentEstablishment;
    private CancellationTokenSource? _pollCts;

    public EstablishmentDto? CurrentEstablishment => _currentEstablishment;

    public EstablishmentProximityService(ILogger<EstablishmentProximityService> logger, MareMediator mediator,
        ApiController apiController, EstablishmentConfigService configService)
        : base(logger, mediator)
    {
        _apiController = apiController;
        _configService = configService;

        Mediator.Subscribe<ConnectedMessage>(this, _ => StartPolling());
        Mediator.Subscribe<DisconnectedMessage>(this, _ => StopPolling());
        Mediator.Subscribe<ZoneSwitchStartMessage>(this, _ =>
        {
            if (_currentEstablishment != null)
            {
                _currentEstablishment = null;
                Mediator.Publish(new EstablishmentLeftMessage());
            }
            _lastQueryPosition = Vector3.Zero;
        });
        Mediator.Subscribe<HousingPositionUpdateMessage>(this, msg =>
            _ = OnPositionUpdate(msg.ServerId, msg.TerritoryId, msg.DivisionId, msg.WardId,
                new Vector3(msg.Position.X, msg.Position.Y, msg.Position.Z)));
    }

    private void StartPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
    }

    private void StopPolling()
    {
        if (_pollCts != null)
        {
            _pollCts.Cancel();
            _pollCts.Dispose();
            _pollCts = null;
        }
        _currentEstablishment = null;
        _lastQueryPosition = Vector3.Zero;
    }

    private async Task OnPositionUpdate(uint serverId, uint territoryId, uint divisionId, uint wardId, Vector3 position)
    {
        if (!_configService.Current.EnableProximityNotifications) return;
        if (!_apiController.IsConnected) return;
        if (_pollCts == null || _pollCts.IsCancellationRequested) return;

        if (Vector3.Distance(position, _lastQueryPosition) < 5.0f) return;
        _lastQueryPosition = position;

        try
        {
            var request = new EstablishmentNearbyRequestDto
            {
                TerritoryId = territoryId,
                ServerId = serverId,
                WardId = wardId > 0 ? wardId : null,
                DivisionId = divisionId > 0 ? divisionId : null,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                Radius = 50f
            };

            var response = await _apiController.EstablishmentGetNearby(request).ConfigureAwait(false);
            var nearest = response?.Establishments.FirstOrDefault();

            if (nearest != null && (_currentEstablishment == null || _currentEstablishment.Id != nearest.Id))
            {
                _currentEstablishment = nearest;
                Mediator.Publish(new EstablishmentEnteredMessage(nearest));
            }
            else if (nearest == null && _currentEstablishment != null)
            {
                _currentEstablishment = null;
                Mediator.Publish(new EstablishmentLeftMessage());
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error in EstablishmentProximityService.OnPositionUpdate");
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopPolling();
            UnsubscribeAll();
        }
    }
}
