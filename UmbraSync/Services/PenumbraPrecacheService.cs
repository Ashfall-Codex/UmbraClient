using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI.Files;

namespace UmbraSync.Services;

public sealed class PenumbraPrecacheService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly IpcManager _ipcManager;
    private readonly MareConfigService _configService;
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileUploadManager _fileUploadManager;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runningTask;
    private string? _lastScannedDir;
    private readonly TimeSpan _debounce = TimeSpan.FromSeconds(3);
    private DateTime _lastTrigger = DateTime.MinValue;
    private readonly System.Threading.Lock _stateLock = new();
    private readonly ConcurrentDictionary<string, byte> _pendingDeltaPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _deltaDebounce = TimeSpan.FromSeconds(2);
    private volatile bool _deltaRequested;
    private volatile bool _disposed;
    private volatile bool _stopping;
    public bool IsUploading { get; private set; }
    public string StatusText { get; private set; } = string.Empty;
    public DateTime? LastRunStartUtc { get; private set; }
    public DateTime? LastRunEndUtc { get; private set; }
    public long BytesUploadedThisRun { get; private set; }

    public PenumbraPrecacheService(ILogger<PenumbraPrecacheService> logger,
        MareMediator mediator,
        IpcManager ipcManager,
        MareConfigService configService,
        FileCacheManager fileCacheManager,
        FileUploadManager fileUploadManager) : base(logger, mediator)
    {
        _ipcManager = ipcManager;
        _configService = configService;
        _fileCacheManager = fileCacheManager;
        _fileUploadManager = fileUploadManager;

        Mediator.Subscribe<PenumbraInitializedMessage>(this, _ => TriggerScan("PenumbraInitialized"));
        Mediator.Subscribe<DalamudLoginMessage>(this, _ => TriggerScan("DalamudLogin"));
        Mediator.Subscribe<PenumbraDirectoryChangedMessage>(this, _ => TriggerScan("PenumbraDirChanged"));
        Mediator.Subscribe<PenumbraModSettingChangedMessage>(this, _ => TriggerScan("PenumbraModSettingChanged"));
        Mediator.Subscribe<PenumbraFilesChangedMessage>(this, msg => OnPenumbraFilesChanged(msg));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        TriggerScan("StartAsync");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* already disposed */ }

        // Give any running task a brief chance to finish gracefully
        var task = _runningTask;
        if (task != null)
        {
            try
            {
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    // ignoreEnableSetting : l'utilisateur le demande explicitement (tour de nouveautés), même si le
    // pré-cache automatique est désactivé dans les paramètres.
    public void TriggerManualPrecache(bool ignoreEnableSetting = false)
    {
        TriggerScan("Manual", ignoreEnableSetting);
    }

    private void TriggerScan(string source, bool ignoreEnableSetting = false)
    {
        if (_disposed || _stopping || _cts.IsCancellationRequested)
        {
            return;
        }

        if (!ignoreEnableSetting && !_configService.Current.EnablePenumbraPrecache)
        {
            Logger.LogDebug("PenumbraPrecache disabled; ignoring trigger {source}", source);
            return;
        }

        _lastTrigger = DateTime.UtcNow;
        if (_runningTask == null || _runningTask.IsCompleted)
        {
            _runningTask = Task.Run(async () =>
            {
                try
                {
                    if (_cts.IsCancellationRequested) return;
                    await Task.Delay(_debounce, _cts.Token).ConfigureAwait(false);
                    // ensure no fresh trigger happened very recently
                    while (DateTime.UtcNow - _lastTrigger < _debounce && !_cts.IsCancellationRequested)
                    {
                        await Task.Delay(_debounce, _cts.Token).ConfigureAwait(false);
                    }
                    await RunScanAndUpload(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogTrace("Precache scan task canceled");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Precache scan failed");
                }
            }, _cts.Token);
        }
    }

    private void OnPenumbraFilesChanged(PenumbraFilesChangedMessage msg)
    {
        if (_disposed || _stopping || _cts.IsCancellationRequested) return;
        if (!_configService.Current.EnablePenumbraPrecache) return;
        foreach (var p in msg.AddedOrChanged)
        {
            if (!IsEligible(p)) continue;
            _pendingDeltaPaths[p] = 1;
        }

        _deltaRequested = true;
        if (_runningTask == null || _runningTask.IsCompleted)
        {
            _runningTask = Task.Run(async () =>
            {
                try
                {
                    if (_cts.IsCancellationRequested) return;
                    do
                    {
                        _deltaRequested = false;
                        await Task.Delay(_deltaDebounce, _cts.Token).ConfigureAwait(false);
                    } while (_deltaRequested && !_cts.IsCancellationRequested);

                    await RunDeltaUpload(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogTrace("Delta precache task canceled");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Delta precache failed");
                }
            }, _cts.Token);
        }
    }

    private bool IsEligible(string path)
    {
        var ext = Path.GetExtension(path);
        if (!CacheMonitor.AllowedFileExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return false;
        var exclusions = _configService.Current.PrecacheExcludePatterns;
        if (exclusions.Any(ex => !string.IsNullOrWhiteSpace(ex) && path.Contains(ex, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    private async Task RunDeltaUpload(CancellationToken token)
    {
        if (_pendingDeltaPaths.IsEmpty)
        {
            return;
        }

        var penDir = _ipcManager.Penumbra.ModDirectory;
        if (string.IsNullOrEmpty(penDir) || !Directory.Exists(penDir))
        {
            return;
        }

        var paths = _pendingDeltaPaths.Keys.ToArray();
        _pendingDeltaPaths.Clear();

        var enabledRoots = await _ipcManager.Penumbra.GetEnabledModRootsAsync().ConfigureAwait(false);
        if (enabledRoots is { Count: > 0 })
        {
            paths = paths.Where(p => enabledRoots.Any(r => p.StartsWith(r, StringComparison.OrdinalIgnoreCase))).ToArray();
        }
        else
        {
            paths = Array.Empty<string>();
        }

        var dict = _fileCacheManager.GetFileCachesByPaths(paths);
        var hashes = dict.Values
            .Where(v => v != null)
            .Select(v => v!.Hash)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (hashes.Count == 0)
        {
            return;
        }

        using (_stateLock.EnterScope())
        {
            IsUploading = true;
            LastRunStartUtc = DateTime.UtcNow;
            StatusText = string.Format(Loc.Get("Settings.Transfer.Precache.Status.DeltaPreparing"), hashes.Count);
            BytesUploadedThisRun = 0;
        }

        var progress = new Progress<string>(s =>
        {
            using (_stateLock.EnterScope()) StatusText = LocalizeProgress(s);
        });
        var byteProgress = new Progress<long>(v =>
        {
            using (_stateLock.EnterScope()) BytesUploadedThisRun = v;
        });

        try
        {
            var missingOrForbidden = await _fileUploadManager.UploadFiles(hashes, progress, token, byteProgress).ConfigureAwait(false);
            using (_stateLock.EnterScope())
            {
                StatusText = missingOrForbidden.Count == 0
                    ? Loc.Get("Settings.Transfer.Precache.Status.DeltaDoneUpToDate")
                    : string.Format(Loc.Get("Settings.Transfer.Precache.Status.DeltaDoneSkipped"), missingOrForbidden.Count);
            }
        }
        finally
        {
            using (_stateLock.EnterScope())
            {
                IsUploading = false;
                LastRunEndUtc = DateTime.UtcNow;
                // keep BytesUploadedThisRun as final value for this run
            }
        }
    }

    private async Task RunScanAndUpload(CancellationToken token)
    {
        var penDir = _ipcManager.Penumbra.ModDirectory;
        if (string.IsNullOrEmpty(penDir) || !Directory.Exists(penDir))
        {
            Logger.LogDebug("Penumbra directory unavailable; skipping precache");
            using (_stateLock.EnterScope())
            {
                StatusText = Loc.Get("Settings.Transfer.Precache.Status.DirUnavailable");
            }
            return;
        }

        if (string.Equals(_lastScannedDir, penDir, StringComparison.Ordinal))
        {
            Logger.LogTrace("Penumbra directory unchanged; proceeding anyway to catch new files");
        }
        _lastScannedDir = penDir;

        List<string> files;
        try
        {
            // Enumerate only inside enabled mod roots
            var enabledRoots = await _ipcManager.Penumbra.GetEnabledModRootsAsync().ConfigureAwait(false);
            if (enabledRoots is { Count: > 0 })
            {
                files = enabledRoots.SelectMany(r => EnumerateEligibleFiles(r)).ToList();
            }
            else
            {
                // If we cannot determine enabled roots, do not upload anything (respect "only active mods")
                files = new List<string>();
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to enumerate Penumbra files");
            using (_stateLock.EnterScope())
            {
                StatusText = Loc.Get("Settings.Transfer.Precache.Status.FailedEnumerate");
            }
            return;
        }

        if (files.Count == 0)
        {
            Logger.LogDebug("No eligible Penumbra files found for precache");
            using (_stateLock.EnterScope())
            {
                StatusText = Loc.Get("Settings.Transfer.Precache.Status.NoEligible");
            }
            return;
        }

        var dict = _fileCacheManager.GetFileCachesByPaths(files.ToArray());
        var hashes = dict.Values
            .Where(v => v != null)
            .Select(v => v!.Hash)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (hashes.Count == 0)
        {
            Logger.LogDebug("No hashes resolved for precache");
            using (_stateLock.EnterScope())
            {
                StatusText = Loc.Get("Settings.Transfer.Precache.Status.NoHashes");
            }
            return;
        }

        Logger.LogInformation("Pre-caching {count} files (unique hashes: {hashCount})", files.Count, hashes.Count);

        using (_stateLock.EnterScope())
        {
            IsUploading = true;
            LastRunStartUtc = DateTime.UtcNow;
            StatusText = string.Format(Loc.Get("Settings.Transfer.Precache.Status.Preparing"), hashes.Count);
            BytesUploadedThisRun = 0;
        }

        var progress = new Progress<string>(s =>
        {
            Logger.LogTrace("[Precache] {msg}", s);
            using (_stateLock.EnterScope())
            {
                StatusText = LocalizeProgress(s);
            }
        });
        var byteProgress = new Progress<long>(v =>
        {
            using (_stateLock.EnterScope()) BytesUploadedThisRun = v;
        });

        // Note: UploadFiles itself checks for server presence and returns missing/forbidden list.
        try
        {
            if (!_fileUploadManager.IsInitialized)
            {
                Logger.LogDebug("FileUploadManager not initialized; waiting for connection");
                using (_stateLock.EnterScope())
                {
                    StatusText = Loc.Get("Settings.Transfer.Precache.Status.WaitingConnection");
                }

                while (!_fileUploadManager.IsInitialized && !token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }

                if (token.IsCancellationRequested) return;
            }

            var missingOrForbidden = await _fileUploadManager.UploadFiles(hashes, progress, token, byteProgress).ConfigureAwait(false);
            if (missingOrForbidden.Count > 0)
            {
                Logger.LogInformation("Pre-cache complete with {n} files skipped (locally missing or forbidden)", missingOrForbidden.Count);
                using (_stateLock.EnterScope())
                {
                    StatusText = string.Format(Loc.Get("Settings.Transfer.Precache.Status.CompletedSkipped"), missingOrForbidden.Count);
                }
            }
            else
            {
                Logger.LogInformation("Pre-cache complete: all files up-to-date on server");
                using (_stateLock.EnterScope())
                {
                    StatusText = Loc.Get("Settings.Transfer.Precache.Status.CompletedAllUpToDate");
                }
            }
        }
        finally
        {
            using (_stateLock.EnterScope())
            {
                IsUploading = false;
                LastRunEndUtc = DateTime.UtcNow;
                // keep BytesUploadedThisRun as final value for this run
            }
        }
    }

    private static readonly Regex RxStart = new(
        pattern: "^Starting upload for (?<total>\\d+) files",
        options: RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture,
        matchTimeout: TimeSpan.FromMilliseconds(250));
    private static readonly Regex RxUploading = new(
        pattern: "^Uploading file (?<cur>\\d+)/(?<total>\\d+)\\. ",
        options: RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture,
        matchTimeout: TimeSpan.FromMilliseconds(250));

    private static string LocalizeProgress(string s)
    {
        var m1 = RxStart.Match(s);
        if (m1.Success && int.TryParse(m1.Groups["total"].Value, out var total))
            return string.Format(Loc.Get("Settings.Transfer.Precache.Progress.Starting"), total);

        var m2 = RxUploading.Match(s);
        if (m2.Success
            && int.TryParse(m2.Groups["cur"].Value, out var cur)
            && int.TryParse(m2.Groups["total"].Value, out var total2))
            return string.Format(Loc.Get("Settings.Transfer.Precache.Progress.Uploading"), cur, total2);

        return s;
    }

    private IEnumerable<string> EnumerateEligibleFiles(string root)
    {
        IEnumerable<string> GetAllFilesSafe(string dir)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch
            {
                files = Array.Empty<string>();
            }

            foreach (var f in files)
                yield return f;

            IEnumerable<string> subs;
            try
            {
                subs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                subs = Array.Empty<string>();
            }

            foreach (var d in subs)
            {
                foreach (var f in GetAllFilesSafe(d))
                    yield return f;
            }
        }

        var allowed = CacheMonitor.AllowedFileExtensions;
        var exclusions = _configService.Current.PrecacheExcludePatterns;

        return GetAllFilesSafe(root)
            .Where(p => allowed.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Where(p => !exclusions.Any(ex => !string.IsNullOrWhiteSpace(ex) && p.Contains(ex, StringComparison.OrdinalIgnoreCase)));
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* no-op */ }

        base.Dispose(disposing);

        try { _cts.Dispose(); }
        catch (ObjectDisposedException) { /* no-op */ }
    }
}