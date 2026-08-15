using System.Collections.Concurrent;

namespace UmbraSync.FileCache;

// Cache mémoire du mapping hash_source -> hash_bc7 appris via getFileSizes.
// Permet à PairHandler de rediriger vers l'alternate sans re-questionner le serveur à chaque apply.
// - alternate connu OU "n'aura jamais d'alternate" -> permanent
// - en attente (ni l'un ni l'autre) -> re-check après 2 min
public sealed class CompressedAlternateManager
{
    private readonly record struct Entry(string? AlternateHash, DateTimeOffset NextCheck);
    private const double PendingRecheckMinutes = 2;
    
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void SetCompressedAlternate(string sourceHash, string? alternateHash, bool neverWillHaveAlternate)
    {
        var nextCheck = (alternateHash != null || neverWillHaveAlternate)
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.UtcNow + TimeSpan.FromMinutes(PendingRecheckMinutes);
        _entries[sourceHash] = new Entry(alternateHash, nextCheck);
    }

    public bool TryGetCachedCompressedAlternate(string sourceHash, out string? alternateHash)
    {
        if (_entries.TryGetValue(sourceHash, out var e) && (e.AlternateHash != null || DateTimeOffset.UtcNow < e.NextCheck))
        {
            alternateHash = e.AlternateHash;
            return true;
        }
        alternateHash = null;
        return false;
    }
}
