using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

public class EstablishmentReminderService : MediatorSubscriberBase, IDisposable
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultEventDuration = TimeSpan.FromMinutes(30);

    private readonly ApiController _apiController;
    private readonly EstablishmentConfigService _configService;
    private CancellationTokenSource? _timerCts;
    private readonly HashSet<(Guid EventId, DateTime OccurrenceStartUtc)> _notifiedOccurrences = [];

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
        var token = _timerCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await CheckUpcomingEvents(token).ConfigureAwait(false);
                    await Task.Delay(PollingInterval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Timer cancelled during shutdown — expected, nothing to do
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in EstablishmentReminderService timer");
            }
        }, token);
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

    private async Task CheckUpcomingEvents(CancellationToken ct)
    {
        if (!_configService.Current.EnableEventReminders) return;
        if (!_apiController.IsConnected) return;

        var bookmarks = _configService.Current.BookmarkedEstablishments;
        if (bookmarks.Count == 0) return;

        var now = DateTime.UtcNow;

        // Periodic cleanup: drop notification keys older than 7 days to keep memory bounded.
        _notifiedOccurrences.RemoveWhere(k => k.OccurrenceStartUtc < now.AddDays(-7));

        foreach (var establishmentId in bookmarks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var establishment = await _apiController.EstablishmentGetById(establishmentId).ConfigureAwait(false);
                if (establishment?.Events == null) continue;

                foreach (var evt in establishment.Events)
                {
                    var occurrence = ComputeCurrentOrNextOccurrence(evt, now);
                    if (occurrence == null) continue;

                    var (occStart, occEnd) = occurrence.Value;

                    // Skip if not currently open (i.e. occurrence is in the future or already past).
                    if (now < occStart || now > occEnd) continue;

                    var key = (evt.Id, occStart);
                    if (!_notifiedOccurrences.Add(key)) continue;

                    Mediator.Publish(new NotificationMessage(
                        establishment.Name,
                        $"{evt.Title} a ouvert",
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

    // Returns the occurrence (start, end) that is currently active or the next future one,
    // honoring the event recurrence pattern. Returns null if the (non-recurring) event is fully past.
    internal static (DateTime Start, DateTime End)? ComputeCurrentOrNextOccurrence(EstablishmentEventDto evt, DateTime now)
    {
        var rawDuration = evt.EndsAtUtc.HasValue ? evt.EndsAtUtc.Value - evt.StartsAtUtc : DefaultEventDuration;
        var duration = rawDuration > TimeSpan.Zero ? rawDuration : DefaultEventDuration;

        var start = evt.StartsAtUtc;

        if (evt.Recurrence == 0)
        {
            var end = start + duration;
            return end < now ? null : (start, end);
        }

        var step = GetRecurrenceStep(evt.Recurrence);
        if (step == null)
            return (start, start + duration);

        // Advance until the occurrence end is no longer in the past.
        // Hard stop after a generous max iteration to avoid runaway loops on bad data.
        const int maxIterations = 10_000;
        var iterations = 0;
        while (start + duration < now && iterations++ < maxIterations)
            start = step(start);

        return (start, start + duration);
    }

    private static Func<DateTime, DateTime>? GetRecurrenceStep(int recurrence) => recurrence switch
    {
        1 => d => d.AddDays(1),       // Quotidien
        2 => d => d.AddDays(7),       // Hebdomadaire
        3 => d => d.AddMonths(1),     // Mensuel
        4 => d => d.AddDays(14),      // Toutes les 2 semaines
        5 => d => d.AddMonths(2),     // Tous les 2 mois
        6 => d => d.AddMonths(3),     // Tous les 3 mois
        7 => d => d.AddYears(1),      // Annuel
        _ => null,
    };

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
