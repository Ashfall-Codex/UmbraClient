using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace UmbraSync.Services;

public sealed class ProfileImageTextureCache : IDisposable
{
    private const long CacheBudgetBytes = 128L * 1024 * 1024;
    private const int MaxConcurrentLoads = 4;
    private const long FailureRetryDelayMs = 60_000;
    private const long RecentUseMs = 2_000;
    private const long PruneIntervalMs = 1_000;

    private readonly ILogger<ProfileImageTextureCache> _logger;
    private readonly ITextureProvider _textureProvider;
    private readonly IFramework _framework;
    private readonly Dictionary<object, CacheEntry> _entries = [];
    private readonly HashSet<IDalamudTextureWrap> _pendingDisposals = new(ReferenceEqualityComparer.Instance);
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _loadLimit = new(MaxConcurrentLoads);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CancellationToken _disposeToken;
    private long _cacheBytes;
    private long _nextPruneTicks;
    private bool _disposed;

    public ProfileImageTextureCache(ILogger<ProfileImageTextureCache> logger, ITextureProvider textureProvider, IFramework framework)
    {
        _logger = logger;
        _textureProvider = textureProvider;
        _framework = framework;
        _disposeToken = _disposeCts.Token;
    }

    public IDalamudTextureWrap? GetTexture(ProfileImage image)
    {
        if (!image.HasImage) return null;

        IDalamudTextureWrap? texture;
        List<IDalamudTextureWrap>? evicted = null;
        lock (_lock)
        {
            if (_disposed) return null;

            var now = Environment.TickCount64;
            if (!_entries.TryGetValue(image.TextureKey, out var entry))
            {
                entry = new CacheEntry(image.TextureKey);
                _entries[entry.Key] = entry;
            }

            entry.LastUseTicks = now;
            texture = entry.Texture;
            if (texture == null) StartLoadLocked(entry, image, now);

            if (_cacheBytes > CacheBudgetBytes && now >= _nextPruneTicks)
            {
                _nextPruneTicks = now + PruneIntervalMs;
                evicted = PruneLocked(entry, now);
            }
        }

        DisposeAfterDraw(evicted);
        return texture;
    }

    public void Rekey(ProfileImage previous, ProfileImage current)
    {
        if (!previous.HasImage || !current.HasImage || Equals(previous.TextureKey, current.TextureKey)) return;

        IDalamudTextureWrap? replaced = null;
        lock (_lock)
        {
            if (_disposed) return;

            if (_entries.TryGetValue(current.TextureKey, out var target)) target.FailedAtTicks = null;
            if (!_entries.Remove(previous.TextureKey, out var entry)) return;

            if (target is { Texture: null } && entry.Texture != null)
            {
                target.Texture = entry.Texture;
                target.Size = entry.Size;
            }
            else if (target != null)
            {
                _cacheBytes -= entry.Size;
                replaced = entry.Texture;
            }
            else
            {
                entry.Key = current.TextureKey;
                _entries[entry.Key] = entry;
            }
        }

        DisposeAfterDraw(replaced);
    }

    public void Clear()
    {
        List<IDalamudTextureWrap> textures;
        lock (_lock)
        {
            if (_disposed) return;

            textures = _entries.Values.Select(entry => entry.Texture).OfType<IDalamudTextureWrap>().ToList();
            _entries.Clear();
            _cacheBytes = 0;
        }

        DisposeAfterDraw(textures);
    }

    private void StartLoadLocked(CacheEntry entry, ProfileImage image, long now)
    {
        if (entry.Load is { IsCompleted: false }) return;
        if (entry.FailedAtTicks is { } failedAt && now - failedAt < FailureRetryDelayMs) return;

        entry.Load = Task.Run(() => LoadEntryAsync(entry, image));
    }

    private async Task LoadEntryAsync(CacheEntry entry, ProfileImage image)
    {
        IDalamudTextureWrap? texture = null;
        try
        {
            await _loadLimit.WaitAsync(_disposeToken).ConfigureAwait(false);
            try
            {
                var bytes = image.ReadNow();
                if (bytes.Length == 0) throw new InvalidDataException("Image de fiche absente ou refusée");

                texture = await _textureProvider.CreateFromImageAsync(bytes, "UmbraSync profile image", _disposeToken).ConfigureAwait(false);

                List<IDalamudTextureWrap>? evicted = null;
                lock (_lock)
                {
                    if (!_disposed && entry.Texture == null && _entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                    {
                        var now = Environment.TickCount64;
                        entry.Texture = texture;
                        entry.Size = (long)texture.Width * texture.Height * 4;
                        entry.FailedAtTicks = null;
                        entry.LastUseTicks = now;
                        _cacheBytes += entry.Size;
                        texture = null;
                        evicted = PruneLocked(entry, now);
                    }
                }

                DisposeAfterDraw(evicted);
            }
            finally
            {
                _loadLimit.Release();
            }
        }
        catch (OperationCanceledException) when (_disposeToken.IsCancellationRequested)
        {
            _logger.LogTrace("Chargement d'une image de fiche annulé");
        }
        catch (Exception ex)
        {
            lock (_lock) entry.FailedAtTicks = Environment.TickCount64;
            _logger.LogDebug(ex, "Chargement d'une image de fiche impossible");
        }
        finally
        {
            if (texture != null) DisposeAfterDraw([texture]);
        }
    }

    private List<IDalamudTextureWrap>? PruneLocked(CacheEntry protectedEntry, long now)
    {
        if (_cacheBytes <= CacheBudgetBytes) return null;

        List<IDalamudTextureWrap> evicted = [];
        foreach (var candidate in _entries.Values
                     .Where(entry => entry.Texture != null && !ReferenceEquals(entry, protectedEntry) && now - entry.LastUseTicks >= RecentUseMs)
                     .OrderBy(entry => entry.LastUseTicks)
                     .ToArray())
        {
            _entries.Remove(candidate.Key);
            _cacheBytes -= candidate.Size;
            evicted.Add(candidate.Texture!);
            if (_cacheBytes <= CacheBudgetBytes) break;
        }

        return evicted;
    }

    private void DisposeAfterDraw(List<IDalamudTextureWrap>? textures)
    {
        if (textures == null) return;
        foreach (var texture in textures) DisposeAfterDraw(texture);
    }

    private void DisposeAfterDraw(IDalamudTextureWrap? texture)
    {
        if (texture == null) return;

        bool disposeNow;
        lock (_lock)
        {
            disposeNow = _disposed || _framework.IsFrameworkUnloading;
            if (!disposeNow && !_pendingDisposals.Add(texture)) return;
        }

        if (disposeNow)
        {
            DisposeQuietly(texture);
            return;
        }

        _ = DisposeAfterDrawAsync(texture);
    }

    private async Task DisposeAfterDrawAsync(IDalamudTextureWrap texture)
    {
        try
        {
            await _framework.RunOnTick(() => DisposePending(texture), delayTicks: 2).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DisposePending(texture);
            if (ex is not OperationCanceledException)
                _logger.LogDebug(ex, "Libération différée d'une texture de fiche impossible");
        }
    }

    private void DisposePending(IDalamudTextureWrap texture)
    {
        lock (_lock)
        {
            if (!_pendingDisposals.Remove(texture)) return;
        }

        DisposeQuietly(texture);
    }

    private void DisposeQuietly(IDalamudTextureWrap texture)
    {
        try
        {
            texture.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Libération d'une texture de fiche impossible");
        }
    }

    public void Dispose()
    {
        List<IDalamudTextureWrap> textures;
        lock (_lock)
        {
            if (_disposed) return;

            _disposed = true;
            textures = _entries.Values.Select(entry => entry.Texture).OfType<IDalamudTextureWrap>()
                .Concat(_pendingDisposals).Distinct<IDalamudTextureWrap>(ReferenceEqualityComparer.Instance).ToList();
            _pendingDisposals.Clear();
            _entries.Clear();
            _cacheBytes = 0;
        }

        try
        {
            _disposeCts.Cancel();
            _disposeCts.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Annulation des chargements d'images de fiche impossible");
        }

        foreach (var texture in textures) DisposeQuietly(texture);
    }

    private sealed class CacheEntry(object key)
    {
        public object Key = key;
        public IDalamudTextureWrap? Texture;
        public Task? Load;
        public long LastUseTicks;
        public long? FailedAtTicks;
        public long Size;
    }
}
