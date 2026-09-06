using Microsoft.Extensions.Logging;
using UmbraSync.Interop.Ipc;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;

namespace UmbraSync.FileCache;

public sealed record StorageRelocationResult(
    bool Success,
    StorageFolderIssue Issue,
    int MovedFiles,
    int FailedFiles,
    long MovedBytes,
    string SourceFolder,
    string TargetFolder,
    bool SourceEmptied);

// Déplace le stockage local vers un autre dossier
public sealed class StorageRelocationService : IDisposable
{
    private const string HaltSource = "StorageRelocation";

    private readonly ILogger<StorageRelocationService> _logger;
    private readonly MareConfigService _configService;
    private readonly CacheMonitor _cacheMonitor;
    private readonly FileCacheManager _fileCacheManager;
    private readonly IpcManager _ipcManager;
    private readonly MareMediator _mediator;
#pragma warning disable S2930 // durée de vie volontairement liée au processus, pas au service
    private readonly CancellationTokenSource _shutdownCts = new();
#pragma warning restore S2930
    private int _running;

    public StorageRelocationService(ILogger<StorageRelocationService> logger, MareConfigService configService,
        CacheMonitor cacheMonitor, FileCacheManager fileCacheManager, IpcManager ipcManager, MareMediator mediator)
    {
        _logger = logger;
        _configService = configService;
        _cacheMonitor = cacheMonitor;
        _fileCacheManager = fileCacheManager;
        _ipcManager = ipcManager;
        _mediator = mediator;
    }

    public bool IsRunning => _running == 1;
    public int FilesTotal { get; private set; }
    public int FilesProcessed { get; private set; }
    public long BytesTotal { get; private set; }
    public long BytesProcessed { get; private set; }
    public string TargetFolder { get; private set; } = string.Empty;
    public StorageRelocationResult? LastResult { get; private set; }
    public StorageFolderIssue ValidateTarget(string? targetFolder, bool checkFreeSpace = false)
    {
        StorageFolderIssue issue = StorageFolderValidator.Validate(targetFolder, _ipcManager.Penumbra.ModDirectory);
        if (issue != StorageFolderIssue.None) return issue;

        string source = _configService.Current.CacheFolder;
        if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
            return StorageFolderIssue.Missing;

        if (StorageFolderValidator.IsSameOrNested(source, targetFolder!))
            return string.Equals(StorageFolderValidator.Normalize(source), StorageFolderValidator.Normalize(targetFolder!), StringComparison.OrdinalIgnoreCase)
                ? StorageFolderIssue.SameAsCurrent
                : StorageFolderIssue.Nested;

        if (StorageFolderValidator.IsSameOrNested(targetFolder!, source))
            return StorageFolderIssue.Nested;

        if (checkFreeSpace && !HasEnoughSpace(source, targetFolder!))
            return StorageFolderIssue.NotEnoughSpace;

        return StorageFolderIssue.None;
    }

    public Task RelocateAsync(string targetFolder)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            _logger.LogWarning("Storage relocation already running, ignoring request");
            return Task.CompletedTask;
        }

        TargetFolder = targetFolder;
        FilesTotal = 0;
        FilesProcessed = 0;
        BytesTotal = 0;
        BytesProcessed = 0;
        LastResult = null;

        return Task.Run(() =>
        {
            try
            {
                Relocate(targetFolder, _shutdownCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Storage relocation failed");
                LastResult = new StorageRelocationResult(false, StorageFolderIssue.None, FilesProcessed, FilesTotal - FilesProcessed,
                    BytesProcessed, _configService.Current.CacheFolder, targetFolder, false);
                Notify(false);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    private void Relocate(string targetFolder, CancellationToken token)
    {
        string source = _configService.Current.CacheFolder;
        StorageFolderIssue issue = ValidateTarget(targetFolder, checkFreeSpace: true);
        if (issue != StorageFolderIssue.None)
        {
            _logger.LogWarning("Storage relocation refused: {issue}", issue);
            LastResult = new StorageRelocationResult(false, issue, 0, 0, 0, source, targetFolder, false);
            return;
        }

        string target = StorageFolderValidator.Normalize(targetFolder);
        List<(string Source, string Target, long Size)> plan = BuildPlan(source, target, out List<string> temporaryFiles);
        FilesTotal = plan.Count;
        BytesTotal = plan.Sum(p => p.Size);

        _logger.LogInformation("Relocating storage from {source} to {target}: {count} file(s), {bytes} bytes",
            source, target, FilesTotal, BytesTotal);

        int moved = 0;
        int failed = 0;
        long movedBytes = 0;

        _cacheMonitor.HaltScan(HaltSource);

        try
        {
            _cacheMonitor.StopMonitoring();
            Directory.CreateDirectory(target);

            foreach ((string sourceFile, string targetFile, long size) in plan)
            {
                if (token.IsCancellationRequested)
                {
                    _logger.LogWarning("Storage relocation interrupted after {moved} file(s)", moved);
                    break;
                }

                try
                {
                    string? targetDirectory = Path.GetDirectoryName(targetFile);
                    if (!string.IsNullOrEmpty(targetDirectory))
                        Directory.CreateDirectory(targetDirectory);

                    File.Move(sourceFile, targetFile, overwrite: true);
                    moved++;
                    movedBytes += size;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex, "Could not move storage file {file}", sourceFile);
                }

                FilesProcessed = moved + failed;
                BytesProcessed = movedBytes;
            }

            DeleteTemporaryFiles(temporaryFiles);
            RemoveEmptySubstFolder(source);
        }
        finally
        {
            bool switched = moved > 0 || failed == 0;
            if (switched)
            {
                _configService.Current.CacheFolder = target;
                _configService.Save();
                _fileCacheManager.ResolveAllPaths();
            }

            // Sur un déchargement du plugin, on se contente d'enregistrer le nouveau chemin
            if (token.IsCancellationRequested)
            {
                try
                {
                    _cacheMonitor.ResumeScan(HaltSource);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not release the scan lock during shutdown");
                }
            }
            else
            {
                RestartMonitoring();
            }

            bool sourceEmptied = IsSourceEmptied(source);
            LastResult = new StorageRelocationResult(switched && failed == 0, StorageFolderIssue.None,
                moved, failed, movedBytes, source, target, sourceEmptied);

            _logger.LogInformation("Storage relocation finished: {moved} moved, {failed} failed, switched: {switched}",
                moved, failed, switched);

            if (!token.IsCancellationRequested)
                Notify(switched && failed == 0);
        }
    }

    private List<(string Source, string Target, long Size)> BuildPlan(string source, string target, out List<string> temporaryFiles)
    {
        List<(string Source, string Target, long Size)> plan = [];
        temporaryFiles = [];

        CollectFiles(source, target, plan, temporaryFiles);

        string substSource = Path.Combine(source, FileCacheManager.SubstPath);
        if (Directory.Exists(substSource))
            CollectFiles(substSource, Path.Combine(target, FileCacheManager.SubstPath), plan, temporaryFiles);

        return plan;
    }

    private void CollectFiles(string sourceDirectory, string targetDirectory,
        List<(string Source, string Target, long Size)> plan, List<string> temporaryFiles)
    {
        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            // Les résidus de téléchargement sont supprimés au démarrage
            if (StorageFolderValidator.IsTemporaryFile(file))
            {
                temporaryFiles.Add(file);
                continue;
            }

            long size = 0;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read size of {file}", file);
            }

            plan.Add((file, Path.Combine(targetDirectory, Path.GetFileName(file)), size));
        }
    }

    private void DeleteTemporaryFiles(List<string> temporaryFiles)
    {
        foreach (string file in temporaryFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not delete temporary file {file}", file);
            }
        }
    }

    private void RemoveEmptySubstFolder(string source)
    {
        string substSource = Path.Combine(source, FileCacheManager.SubstPath);
        try
        {
            if (Directory.Exists(substSource) && !Directory.EnumerateFileSystemEntries(substSource).Any())
                Directory.Delete(substSource);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove empty subst folder {folder}", substSource);
        }
    }

    private static bool IsSourceEmptied(string source)
    {
        try
        {
            return !Directory.Exists(source) || !Directory.EnumerateFileSystemEntries(source).Any();
        }
        catch
        {
            return false;
        }
    }

    private void RestartMonitoring()
    {
        try
        {
            _cacheMonitor.StartMareWatcher(_configService.Current.CacheFolder);
            _cacheMonitor.StartSubstWatcher(_fileCacheManager.SubstFolder);
            _cacheMonitor.StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restart storage monitoring after relocation");
        }

        try
        {
            _cacheMonitor.ResumeScan(HaltSource);
            _cacheMonitor.InvokeScan();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resume the storage scan after relocation");
        }
    }

    private bool HasEnoughSpace(string source, string target)
    {
        if (StorageFolderValidator.IsSameVolume(source, target)) return true;

        try
        {
            long required = 0;
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                required += new FileInfo(file).Length;
            }

            string? root = Path.GetPathRoot(Path.GetFullPath(target));
            if (string.IsNullOrEmpty(root)) return true;

            long available = new DriveInfo(root).AvailableFreeSpace;
            return available > required + 1024L * 1024L * 256L;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not determine free space on the target drive, proceeding anyway");
            return true;
        }
    }

    private void Notify(bool success)
    {
        StorageRelocationResult? result = LastResult;
        if (result == null) return;

        string title = Localization.Loc.Get("Settings.Storage.Move.NotificationTitle");
        string body = success
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Localization.Loc.Get("Settings.Storage.Move.NotificationSuccess"), result.MovedFiles, result.TargetFolder)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Localization.Loc.Get("Settings.Storage.Move.NotificationFailed"), result.FailedFiles, result.SourceFolder);

        _mediator.Publish(new NotificationMessage(title, body,
            success ? NotificationType.Success : NotificationType.Warning, TimeSpan.FromSeconds(10)));
    }

    public void Dispose()
    {
        try
        {
            _shutdownCts.Cancel();
        }
        catch
        {
            // le déchargement ne doit jamais échouer à cause d'une annulation
        }
    }
}
