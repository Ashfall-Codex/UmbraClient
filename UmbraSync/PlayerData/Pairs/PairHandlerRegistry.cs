using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.PlayerData.Factories;
using UmbraSync.Services.Mediator;
namespace UmbraSync.PlayerData.Pairs;

public sealed class PairHandlerRegistry : IDisposable
{
    private readonly ILogger<PairHandlerRegistry> _logger;
    private readonly PairHandlerFactory _handlerFactory;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, PairHandlerEntry> _entriesByIdent = new(StringComparer.Ordinal);
    private readonly TimeSpan _deletionGracePeriod = TimeSpan.FromMinutes(5);

    private bool _disposed;

    public PairHandlerRegistry(
        ILogger<PairHandlerRegistry> logger,
        PairHandlerFactory handlerFactory)
    {
        _logger = logger;
        _handlerFactory = handlerFactory;
    }

    public int HandlerCount
    {
        get
        {
            lock (_gate) return _entriesByIdent.Count;
        }
    }
    
    public int HandlersInGracePeriod
    {
        get
        {
            lock (_gate) return _entriesByIdent.Values.Count(e => e.IsInGracePeriod);
        }
    }
    
    public IPairHandlerAdapter? RegisterPairOnline(Pair pair)
    {
        if (pair == null) throw new ArgumentNullException(nameof(pair));

        var ident = pair.Ident;
        var uid = pair.UserData.UID;

        lock (_gate)
        {
            if (_entriesByIdent.TryGetValue(ident, out var existingEntry))
            {
                existingEntry.AddPair(uid);
                _logger.LogDebug("RegisterPairOnline: réutilisation du handler existant pour {ident}, paire {uid} ajoutée (total: {count})",
                    ident, uid, existingEntry.PairCount);
                return existingEntry.Handler;
            }

            try
            {
                var handler = _handlerFactory.Create(pair);
                if (handler is not IPairHandlerAdapter adapter)
                {
                    _logger.LogError("RegisterPairOnline: le handler créé n'implémente pas IPairHandlerAdapter pour {ident}", ident);
                    handler.Dispose();
                    return null;
                }

                var entry = new PairHandlerEntry(ident, adapter);
                entry.AddPair(uid);
                _entriesByIdent[ident] = entry;

                _logger.LogDebug("RegisterPairOnline: nouveau handler créé pour {ident}, paire {uid}", ident, uid);
                return adapter;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RegisterPairOnline: erreur lors de la création du handler pour {ident}", ident);
                return null;
            }
        }
    }
    
    public void DeregisterPairOffline(Pair pair, bool forceDisposal = false)
    {
        if (pair == null) return;

        var ident = pair.Ident;
        var uid = pair.UserData.UID;

        lock (_gate)
        {
            if (!_entriesByIdent.TryGetValue(ident, out var entry))
            {
                _logger.LogDebug("DeregisterPairOffline: aucun handler trouvé pour {ident}", ident);
                return;
            }

            entry.RemovePair(uid);
            _logger.LogDebug("DeregisterPairOffline: paire {uid} retirée de {ident} (restant: {count})",
                uid, ident, entry.PairCount);

            if (!entry.HasPairs)
            {
                if (forceDisposal)
                {
                    _logger.LogDebug("DeregisterPairOffline: suppression immédiate du handler {ident}", ident);
                    RemoveEntry(entry);
                }
                else
                {
                    _logger.LogDebug("DeregisterPairOffline: démarrage grace period pour {ident}", ident);
                    entry.StartGracePeriod(_deletionGracePeriod, OnGracePeriodExpired);
                }
            }
        }
    }
    public IPairHandlerAdapter? GetHandler(string ident)
    {
        if (string.IsNullOrEmpty(ident)) return null;

        lock (_gate)
        {
            return _entriesByIdent.TryGetValue(ident, out var entry) ? entry.Handler : null;
        }
    }

    public bool HasHandler(string ident)
    {
        if (string.IsNullOrEmpty(ident)) return false;

        lock (_gate)
        {
            return _entriesByIdent.ContainsKey(ident);
        }
    }
    
    public void CancelGracePeriod(string ident)
    {
        if (string.IsNullOrEmpty(ident)) return;

        lock (_gate)
        {
            if (_entriesByIdent.TryGetValue(ident, out var entry))
            {
                entry.CancelGracePeriod();
                _logger.LogDebug("CancelGracePeriod: grace period annulée pour {ident}", ident);
            }
        }
    }

    public bool ApplyCharacterData(string ident, Guid applicationId, CharacterData data, bool forced = false)
    {
        if (string.IsNullOrEmpty(ident) || data == null) return false;

        IPairHandlerAdapter? handler;
        lock (_gate)
        {
            if (!_entriesByIdent.TryGetValue(ident, out var entry))
            {
                _logger.LogDebug("ApplyCharacterData: aucun handler pour {ident}", ident);
                return false;
            }
            handler = entry.Handler;
        }

        try
        {
            handler.ApplyCharacterData(applicationId, data, forced);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyCharacterData: erreur pour {ident}", ident);
            return false;
        }
    }

    public void ResetAllHandlers()
    {
        List<IPairHandlerAdapter> handlers;

        lock (_gate)
        {
            handlers = _entriesByIdent.Values.Select(e => e.Handler).ToList();
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler.Invalidate();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResetAllHandlers: erreur lors de l'invalidation de {ident}", handler.Ident);
            }
        }

        _logger.LogInformation("ResetAllHandlers: {count} handlers invalidés", handlers.Count);
    }


    public void ClearAll()
    {
        List<PairHandlerEntry> entries;

        lock (_gate)
        {
            entries = _entriesByIdent.Values.ToList();
            _entriesByIdent.Clear();
        }

        foreach (var entry in entries)
        {
            try
            {
                entry.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ClearAll: erreur lors de la suppression de {ident}", entry.Ident);
            }
        }

        _logger.LogInformation("ClearAll: {count} handlers supprimés", entries.Count);
    }


    public RegistryStats GetStats()
    {
        lock (_gate)
        {
            var totalPairs = _entriesByIdent.Values.Sum(e => e.PairCount);
            var inGracePeriod = _entriesByIdent.Values.Count(e => e.IsInGracePeriod);
            var visible = _entriesByIdent.Values.Count(e => e.Handler.IsVisible);

            return new RegistryStats(
                HandlerCount: _entriesByIdent.Count,
                TotalPairs: totalPairs,
                HandlersInGracePeriod: inGracePeriod,
                VisibleHandlers: visible
            );
        }
    }

    private void OnGracePeriodExpired(PairHandlerEntry entry)
    {
        lock (_gate)
        {
            // Vérifier que l'entrée est toujours dans le registre et sans paires
            if (!_entriesByIdent.TryGetValue(entry.Ident, out var currentEntry) ||
                currentEntry != entry ||
                entry.HasPairs)
            {
                _logger.LogDebug("OnGracePeriodExpired: conditions non remplies pour {ident}, annulation", entry.Ident);
                return;
            }

            _logger.LogDebug("OnGracePeriodExpired: suppression du handler {ident} après grace period", entry.Ident);
            RemoveEntry(entry);
        }
    }

    private void RemoveEntry(PairHandlerEntry entry)
    {
        // Doit être appelé sous _gate lock
        _entriesByIdent.Remove(entry.Ident);
        try
        {
            entry.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoveEntry: erreur lors du dispose de {ident}", entry.Ident);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ClearAll();
    }

    public sealed record RegistryStats(int HandlerCount, int TotalPairs, int HandlersInGracePeriod, int VisibleHandlers);
}
