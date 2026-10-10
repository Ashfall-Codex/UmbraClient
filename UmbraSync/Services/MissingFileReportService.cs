using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.User;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI.SignalR;

namespace UmbraSync.Services;

public sealed class MissingFileReportService : DisposableMediatorSubscriberBase
{
    private static readonly TimeSpan ReportDeduplication = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(180)];
    private const int MaxReportsPerHash = 3;
    private const int MaxHashesPerReport = 200;

    private readonly ApiController _apiController;
    private readonly ConcurrentDictionary<string, (DateTime ReportedAt, int Count)> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _retriesByUid = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _disposed;

    public MissingFileReportService(ILogger<MissingFileReportService> logger, MareMediator mediator, ApiController apiController)
        : base(logger, mediator)
    {
        _apiController = apiController;
        Mediator.Subscribe<ReportMissingFilesMessage>(this, OnReportMissingFiles);
    }

    private void OnReportMissingFiles(ReportMissingFilesMessage msg)
    {
        if (_disposed || msg.Hashes.Count == 0 || !_apiController.CanReportMissingFiles) return;

        var now = DateTime.UtcNow;
        List<string> toReport = [];
        foreach (var hash in msg.Hashes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (toReport.Count >= MaxHashesPerReport) break;

            var key = msg.Owner.UID + "|" + hash;
            if (_reported.TryGetValue(key, out var previous)
                && (now - previous.ReportedAt < ReportDeduplication || previous.Count >= MaxReportsPerHash))
                continue;

            _reported[key] = (now, previous.Count + 1);
            toReport.Add(hash);
        }

        PurgeExpiredReports(now);
        if (toReport.Count == 0) return;

        _ = ReportAndScheduleRetriesAsync(msg.Owner, toReport, msg.DataHash);
    }

    private async Task ReportAndScheduleRetriesAsync(UserData owner, List<string> hashes, string dataHash)
    {
        try
        {
            if (!await _apiController.TryReportMissingFiles(new UserDto(owner), hashes).ConfigureAwait(false))
                return;

            Logger.LogInformation("{count} fichier(s) absent(s) du serveur signalé(s) pour {uid}, ré-upload demandé", hashes.Count, owner.UID);
            await RetryAsync(owner.UID, hashes, dataHash).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Nouveau signalement pour ce pair, ou arrêt du plugin
        }
        catch (ObjectDisposedException)
        {
            // Arrêt du plugin
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Échec du signalement de fichiers absents du serveur pour {uid}", owner.UID);
        }
    }

    private async Task RetryAsync(string uid, List<string> hashes, string dataHash)
    {
        // Un nouveau signalement pour ce pair remplace les relances en cours
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        if (_retriesByUid.TryRemove(uid, out var previous))
        {
            try
            {
                await previous.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Les relances précédentes venaient de se terminer
            }
        }
        _retriesByUid[uid] = cts;

        try
        {
            foreach (var delay in RetryDelays)
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                Mediator.Publish(new RetryMissingFilesMessage(uid, hashes, dataHash));
            }
        }
        finally
        {
            _retriesByUid.TryRemove(new KeyValuePair<string, CancellationTokenSource>(uid, cts));
            cts.Dispose();
        }
    }

    private void PurgeExpiredReports(DateTime now)
    {
        // Les entrées sont gardées une heure : c'est ce qui borne à MaxReportsPerHash les signalements d'un même fichier
        foreach (var (key, value) in _reported)
        {
            if (now - value.ReportedAt > TimeSpan.FromHours(1))
                _reported.TryRemove(key, out _);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                _lifetimeCts.Cancel();
                _lifetimeCts.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Erreur ignorée pendant le Dispose de MissingFileReportService");
            }
        }
        base.Dispose(disposing);
    }
}
