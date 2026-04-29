using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using Pictomancy;

namespace UmbraSync.Services.Rendering;

public sealed class PictomancyService : IDisposable
{
    private readonly ILogger<PictomancyService> _logger;
    private PctContext? _context;

    public PictomancyService(ILogger<PictomancyService> logger, IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        try
        {
            _context = PctService.Initialize(pluginInterface);
            _logger.LogDebug("Pictomancy initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Pictomancy");
        }
    }

    public bool IsInitialized => _context != null;

    public void Dispose()
    {
        if (_context == null) return;
        try
        {
            _context.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispose Pictomancy");
        }
        finally
        {
            _context = null;
        }
    }
}
