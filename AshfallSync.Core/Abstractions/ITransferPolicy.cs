using UmbraSync.MareConfiguration.Models;

namespace UmbraSync.Core.Abstractions;

public interface IForbiddenTransferRegistry
{
    bool IsForbidden(string hash);
}

public interface ITextureCompressionSettings
{
    TextureCompressionMode Mode { get; }
    
    IReadOnlyList<string> UidsToOverride { get; }
}
