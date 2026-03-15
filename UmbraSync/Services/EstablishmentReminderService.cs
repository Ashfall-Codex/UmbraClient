using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

public class EstablishmentReminderService : MediatorSubscriberBase, IDisposable
{
    private readonly ApiController _apiController;
    private readonly EstablishmentConfigService _configService;
    private CancellationTokenSource? _timerCts;
    private readonly HashSet<Guid> _notifiedEvents = [];

    public EstablishmentReminderService(ILogger<EstablishmentReminderService> logger, MareMediator mediator,
        ApiController apiController, EstablishmentConfigService configService)
        : base(logger, mediator)
    {
        _apiController = apiController;
        _configService = configService;

        Mediator.Subscribe<ConnectedMessage>(this, _ => StartTimer());
        Mediator.Subscribe<DisconnectedMessage>(this, _ => StopTimer());
    }

    private void StartTimer()
    {
        StopTimer();
        _timerCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_timerCts.Token.IsCancellationRequested)
                {
                    await CheckUpcomingEvents().ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMinutes(30), _timerCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in EstablishmentReminderService timer");
            }
        }, _timerCts.Token);
    }

    private void StopTimer()
    {
        if (_timerCts != null)
        {
            _timerCts.Cancel();
            _timerCts.Dispose();
            _timerCts = null;
        }
    }

    private async Task CheckUpcomingEvents()
    {
        if (!_configService.Current.EnableEventReminders) return;
        if (!_apiController.IsConnected) return;

        var bookmarks = _configService.Current.BookmarkedEstablishments;
        if (bookmarks.Count == 0) return;

        foreach (var establishmentId in bookmarks)
        {
            try
            {
                var establishment = await _apiController.EstablishmentGetById(establishmentId).ConfigureAwait(false);
                if (establishment?.Events == null) continue;

                var now = DateTime.UtcNow;
                var oneHourFromNow = now.AddHours(1);

                foreach (var evt in establishment.Events)
                {
                    if (_notifiedEvents.Contains(evt.Id)) continue;
                    if (evt.StartsAtUtc <= now) continue;
                    if (evt.StartsAtUtc > oneHourFromNow) continue;

                    _notifiedEvents.Add(evt.Id);
                    var minutesUntil = (int)(evt.StartsAtUtc - now).TotalMinutes;
                    Mediator.Publish(new NotificationMessage(
                        establishment.Name,
                        $"{evt.Title} commence dans {minutesUntil} min",
                        NotificationType.Info,
                        TimeSpan.FromSeconds(15)));
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error checking events for establishment {id}", establishmentId);
            }
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
            StopTimer();
            UnsubscribeAll();
        }
    }
}
