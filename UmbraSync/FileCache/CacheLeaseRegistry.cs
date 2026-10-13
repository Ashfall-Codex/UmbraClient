using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace UmbraSync.FileCache;

/// <summary>
/// Hashes « épinglés » par leurs propriétaires (un pair appliqué, par exemple) : l'éviction
/// automatique du cache ne les supprime pas tant qu'un bail existe. Garde aussi la date de
/// dernier usage de chaque hash, LastAccessTime n'étant pas fiable (Wine/macOS, NTFS sans atime).
/// </summary>
public sealed class CacheLeaseRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<object, HashSet<string>> _leasesByOwner = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> _leaseCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastUseTicks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingTouches = new(StringComparer.Ordinal);

    /// <summary>
    /// Remplace les baux de <paramref name="owner"/>. Accepte des hashes ou des chemins de fichiers
    /// du cache (<c>HASH</c> ou <c>HASH.ext</c>) ; le reste (swaps, fichiers Penumbra locaux) est ignoré.
    /// </summary>
    public void SetLeases(object owner, IEnumerable<string> hashesOrPaths)
    {
        ArgumentNullException.ThrowIfNull(owner);
        HashSet<string> hashes = Normalize(hashesOrPaths);

        lock (_lock)
        {
            ReleaseLeasesUnsafe(owner);
            if (hashes.Count == 0) return;

            _leasesByOwner[owner] = hashes;
            foreach (string hash in hashes)
            {
                _leaseCounts[hash] = _leaseCounts.TryGetValue(hash, out int count) ? count + 1 : 1;
            }
        }

        MarkUsedNormalized(hashes);
    }

    public void ReleaseLeases(object? owner)
    {
        if (owner == null) return;
        lock (_lock)
        {
            ReleaseLeasesUnsafe(owner);
        }
    }

    public bool IsLeased(string hash)
    {
        if (!TryNormalize(hash, out string? normalized)) return false;
        lock (_lock)
        {
            return _leaseCounts.ContainsKey(normalized);
        }
    }

    public HashSet<string> GetLeasedHashesSnapshot()
    {
        lock (_lock)
        {
            return new HashSet<string>(_leaseCounts.Keys, StringComparer.Ordinal);
        }
    }

    public int LeasedHashCount
    {
        get
        {
            lock (_lock)
            {
                return _leaseCounts.Count;
            }
        }
    }

    /// <summary>Note l'usage d'un fichier du cache (résolu ou appliqué) pour l'ordre d'éviction.</summary>
    public void MarkUsed(string hashOrPath)
    {
        if (!TryNormalize(hashOrPath, out string? hash)) return;
        _lastUseTicks[hash] = DateTime.UtcNow.Ticks;
        _pendingTouches[hash] = 0;
    }

    public void MarkUsed(IEnumerable<string> hashesOrPaths)
        => MarkUsedNormalized(Normalize(hashesOrPaths));

    public bool TryGetLastUse(string hash, out DateTime lastUseUtc)
    {
        if (TryNormalize(hash, out string? normalized) && _lastUseTicks.TryGetValue(normalized, out long ticks))
        {
            lastUseUtc = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }

        lastUseUtc = DateTime.MinValue;
        return false;
    }

    /// <summary>
    /// Hashes utilisés depuis le dernier appel, dont l'atime disque doit être rafraîchi
    /// pour que l'ordre d'éviction survive à un redémarrage.
    /// </summary>
    internal HashSet<string> DrainPendingTouches()
    {
        HashSet<string> drained = new(StringComparer.Ordinal);
        foreach (string hash in _pendingTouches.Keys)
        {
            if (_pendingTouches.TryRemove(hash, out _))
                drained.Add(hash);
        }

        return drained;
    }

    internal void Forget(string hash)
    {
        if (!TryNormalize(hash, out string? normalized)) return;
        _lastUseTicks.TryRemove(normalized, out _);
        _pendingTouches.TryRemove(normalized, out _);
    }

    /// <summary>
    /// Vrai si le nom de fichier est celui d'un fichier du cache : hash hexadécimal de 40 caractères,
    /// sans extension ou avec une seule extension qui n'est pas celle d'un temporaire.
    /// </summary>
    public static bool TryGetHashFromCacheFileName(string pathOrName, [NotNullWhen(true)] out string? hash)
    {
        hash = null;
        if (string.IsNullOrEmpty(pathOrName)) return false;

        ReadOnlySpan<char> name = Path.GetFileName(pathOrName.AsSpan());
        int dot = name.IndexOf('.');
        ReadOnlySpan<char> stem = dot < 0 ? name : name[..dot];
        if (stem.Length != 40) return false;

        if (dot >= 0)
        {
            ReadOnlySpan<char> extension = name[dot..];
            if (extension.Length < 2 || extension[1..].Contains('.')) return false;
            if (StorageFolderValidator.TemporaryExtensions.Contains(extension.ToString(), StringComparer.OrdinalIgnoreCase)) return false;
        }

        foreach (char c in stem)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }

        hash = stem.ToString().ToUpperInvariant();
        return true;
    }

    private void MarkUsedNormalized(IEnumerable<string> hashes)
    {
        long now = DateTime.UtcNow.Ticks;
        foreach (string hash in hashes)
        {
            _lastUseTicks[hash] = now;
            _pendingTouches[hash] = 0;
        }
    }

    private void ReleaseLeasesUnsafe(object owner)
    {
        if (!_leasesByOwner.Remove(owner, out HashSet<string>? previous)) return;

        foreach (string hash in previous)
        {
            if (!_leaseCounts.TryGetValue(hash, out int count)) continue;
            if (count <= 1)
                _leaseCounts.Remove(hash);
            else
                _leaseCounts[hash] = count - 1;
        }
    }

    private static HashSet<string> Normalize(IEnumerable<string>? hashesOrPaths)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        if (hashesOrPaths == null) return result;

        foreach (string value in hashesOrPaths)
        {
            if (TryNormalize(value, out string? hash))
                result.Add(hash);
        }

        return result;
    }

    private static bool TryNormalize(string? hashOrPath, [NotNullWhen(true)] out string? hash)
    {
        hash = null;
        return !string.IsNullOrWhiteSpace(hashOrPath) && TryGetHashFromCacheFileName(hashOrPath, out hash);
    }
}
