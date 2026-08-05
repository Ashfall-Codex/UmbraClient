using Microsoft.Extensions.Logging;
using UmbraSync.FileCache;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI.Files;

namespace UmbraSync.PlayerData.Factories;

public class FileDownloadManagerFactory
{
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileCompactor _fileCompactor;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly MareConfigService _mareConfigService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MareMediator _mareMediator;
    private readonly FileDownloadDeduplicator _deduplicator;
    private readonly CompressedAlternateManager _compressedAlternateManager;

    public FileDownloadManagerFactory(ILoggerFactory loggerFactory, MareMediator mareMediator, FileTransferOrchestrator fileTransferOrchestrator,
        FileCacheManager fileCacheManager, FileCompactor fileCompactor, MareConfigService mareConfigService, FileDownloadDeduplicator deduplicator,
        CompressedAlternateManager compressedAlternateManager)
    {
        _loggerFactory = loggerFactory;
        _mareMediator = mareMediator;
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _fileCacheManager = fileCacheManager;
        _fileCompactor = fileCompactor;
        _mareConfigService = mareConfigService;
        _deduplicator = deduplicator;
        _compressedAlternateManager = compressedAlternateManager;
    }

    public FileDownloadManager Create()
    {
        return new FileDownloadManager(_loggerFactory.CreateLogger<FileDownloadManager>(), _mareMediator, _fileTransferOrchestrator, _fileCacheManager, _fileCompactor, _mareConfigService, _deduplicator, _compressedAlternateManager);
    }
}