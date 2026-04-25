using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using Pictomancy;

namespace UmbraSync.Services.Rendering;

public sealed class PictomancyService : IDisposable
{
    private readonly ILogger<PictomancyService> _logger;
    private bool _initialized;

    public PictomancyService(ILogger<PictomancyService> logger, IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        try
        {
            PictoService.Initialize(pluginInterface);
            _initialized = true;
            _logger.LogDebug("Pictomancy initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Pictomancy");
        }
    }

    public bool IsInitialized => _initialized;

    public void Dispose()
    {
        if (!_initialized) return;
        try
        {
            PictoService.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispose Pictomancy");
        }
        finally
        {
            _initialized = false;
        }
    }
}
