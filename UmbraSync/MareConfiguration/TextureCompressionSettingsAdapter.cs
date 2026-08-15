using UmbraSync.Core.Abstractions;
using UmbraSync.MareConfiguration.Models;

namespace UmbraSync.MareConfiguration;

public sealed class TextureCompressionSettingsAdapter : ITextureCompressionSettings
{
    private readonly PlayerPerformanceConfigService _configService;

    public TextureCompressionSettingsAdapter(PlayerPerformanceConfigService configService)
    {
        _configService = configService;
    }

    public TextureCompressionMode Mode => _configService.Current.TextureCompressionMode;

    public IReadOnlyList<string> UidsToOverride => _configService.Current.UIDsToOverride;
}
