using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.API.Dto.Slot;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

public class EstablishmentSyncSlotService : MediatorSubscriberBase, IDisposable
{
    private readonly EstablishmentConfigService _configService;
    private readonly SlotService _slotService;
    private Guid? _currentEstablishmentId;

    public EstablishmentSyncSlotService(ILogger<EstablishmentSyncSlotService> logger, MareMediator mediator,
        EstablishmentConfigService configService, SlotService slotService)
        : base(logger, mediator)
    {
        _configService = configService;
        _slotService = slotService;

        Mediator.Subscribe<EstablishmentEnteredMessage>(this, msg => OnEstablishmentEntered(msg.Establishment));
        Mediator.Subscribe<EstablishmentLeftMessage>(this, _ => OnEstablishmentLeft());
        Mediator.Subscribe<DisconnectedMessage>(this, _ =>
        {
            _currentEstablishmentId = null;
        });
    }

    private void OnEstablishmentEntered(EstablishmentDto establishment)
    {
        _currentEstablishmentId = establishment.Id;

        var bindings = _configService.Current.EstablishmentSyncSlotBindings;
        if (!bindings.TryGetValue(establishment.Id, out var syncshellGid)) return;

        if (string.IsNullOrEmpty(syncshellGid)) return;

        Logger.LogInformation("Establishment {name} entered, auto-joining syncshell {gid}", establishment.Name, syncshellGid);
        _slotService.MarkJoinedViaSlot(new SlotSyncshellDto
        {
            Gid = syncshellGid,
            Name = establishment.Name
        });
    }

    private void OnEstablishmentLeft()
    {
        if (_currentEstablishmentId.HasValue)
        {
            Logger.LogInformation("Left establishment {id}", _currentEstablishmentId.Value);
            _currentEstablishmentId = null;
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
            UnsubscribeAll();
        }
    }
}
