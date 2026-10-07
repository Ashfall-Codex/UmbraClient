using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

public class EstablishmentReminderService : MediatorSubscriberBase, IDisposable
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultEventDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);

    private readonly ApiController _apiController;
    private readonly EstablishmentConfigService _configService;
    private CancellationTokenSource? _timerCts;
    private readonly HashSet<(Guid EventId, DateTime OccurrenceStartUtc, bool IsReminder)> _notifiedOccurrences = [];
    private readonly Dictionary<Guid, (EstablishmentDto Establishment, DateTime FetchedAtUtc)> _establishmentCache = [];

    public EstablishmentReminderService(ILogger<EstablishmentReminderService> logger, MareMediator mediator,
        ApiController apiController, EstablishmentConfigService configService)
        : base(logger, mediator)
    {
        _apiController = apiController;
        _configService = configService;

        Mediator.Subscribe<ConnectedMessage>(this, _ => StartTimer());
        Mediator.Subscribe<DisconnectedMessage>(this, _ => StopTimer());

        // Démarré après la connexion (rechargement du plugin, reprise de session) : le
        // ConnectedMessage est déjà passé, on lance la boucle tout de suite.
        if (_apiController.IsConnected)
            StartTimer();
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
                // Initial jitter (3–8s) pour étaler les fetchs post-connect et éviter le burst
                // simultané qui sature les middleboxes stateful de certains FAI.
                int initialDelayMs = Random.Shared.Next(3000, 8000);
                await Task.Delay(initialDelayMs, token).ConfigureAwait(false);

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
        var config = _configService.Current;
        if (!config.EnableEventReminders) return;
        if (!_apiController.IsConnected) return;

        // Copie : la liste est modifiée depuis l'UI pendant que cette boucle tourne en tâche de fond.
        var bookmarks = config.BookmarkedEstablishments.ToArray();
        foreach (var stale in _establishmentCache.Keys.Where(id => !bookmarks.Contains(id)).ToList())
            _establishmentCache.Remove(stale);
        if (bookmarks.Length == 0) return;

        var now = DateTime.UtcNow;

        // Periodic cleanup: drop notification keys older than 7 days to keep memory bounded.
        _notifiedOccurrences.RemoveWhere(k => k.OccurrenceStartUtc < now.AddDays(-7));

        var lead = TimeSpan.FromMinutes(Math.Clamp(config.EventReminderMinutesBefore, 0, 120));

        foreach (var establishmentId in bookmarks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var establishment = await GetEstablishmentAsync(establishmentId, now).ConfigureAwait(false);
                if (establishment?.Events == null) continue;

                foreach (var evt in establishment.Events)
                {
                    if (evt == null) continue;
                    var occurrence = ComputeCurrentOrNextOccurrence(evt, now);
                    if (occurrence == null) continue;

                    var (occStart, occEnd) = occurrence.Value;

                    if (lead > TimeSpan.Zero && now >= occStart - lead && now < occStart
                        && _notifiedOccurrences.Add((evt.Id, occStart, true)))
                    {
                        PublishReminder(establishment, evt, occStart, now);
                    }

                    if (config.NotifyOnEventStart && now >= occStart && now <= occEnd
                        && _notifiedOccurrences.Add((evt.Id, occStart, false)))
                    {
                        Mediator.Publish(new NotificationMessage(
                            establishment.Name,
                            string.Format(Loc.CurrentCulture, Loc.Get("Establishment.Reminder.Started"),
                                establishment.Name, evt.Title),
                            NotificationType.Info,
                            TimeSpan.FromSeconds(15)));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error checking events for establishment {id}", establishmentId);
            }
        }
    }

    // Les horaires changent rarement : la fiche est relue toutes les 15 minutes, l'échéance
    // est recalculée localement chaque minute. Un favori tout juste ajouté est lu au tour suivant.
    private async Task<EstablishmentDto?> GetEstablishmentAsync(Guid establishmentId, DateTime now)
    {
        if (_establishmentCache.TryGetValue(establishmentId, out var cached) && now - cached.FetchedAtUtc < CacheLifetime)
            return cached.Establishment;

        var establishment = await _apiController.EstablishmentGetById(establishmentId).ConfigureAwait(false);
        if (establishment != null)
            _establishmentCache[establishmentId] = (establishment, now);
        else
            _establishmentCache.Remove(establishmentId);
        return establishment;
    }

    private void PublishReminder(EstablishmentDto establishment, EstablishmentEventDto evt, DateTime occStart, DateTime now)
    {
        var minutesLeft = Math.Max(1, (int)Math.Ceiling((occStart - now).TotalMinutes));
        var localStart = occStart.ToLocalTime().ToString("t", Loc.CurrentCulture);
        var message = string.Format(Loc.CurrentCulture, Loc.Get("Establishment.Reminder.Soon"),
            establishment.Name, evt.Title, minutesLeft, localStart);

        // Toast et chat quel que soit le réglage « Info » : c'est un rappel que l'utilisateur a demandé.
        Mediator.Publish(new DualNotificationMessage(
            Loc.Get("Establishment.Reminder.Title"), message, NotificationType.Info,
            TimeSpan.FromSeconds(20), ForceBoth: true));
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
