using Microsoft.Extensions.Logging;
using UmbraSync.Services.Mediator;
using PenumbraEnum = global::Penumbra.Api.Enums;
using PenumbraIpc = global::Penumbra.Api.IpcSubscribers;
namespace UmbraSync.Interop.Ipc.Penumbra;
public sealed class PenumbraTextures
{
    private readonly PenumbraCore _core;
    private readonly PenumbraIpc.ConvertTextureFile _penumbraConvertTextureFile;

    public PenumbraTextures(PenumbraCore core)
    {
        _core = core;

        // Initialiser l'IPC de conversion de texture
        _penumbraConvertTextureFile = new PenumbraIpc.ConvertTextureFile(_core.PluginInterface);
    }
    
    public async Task ConvertTextureFiles(ILogger logger, Dictionary<string, string[]> textures, IProgress<(string, int)> progress, CancellationToken token)
    {
        if (!_core.APIAvailable) return;

        _core.Mediator.Publish(new HaltScanMessage(nameof(ConvertTextureFiles)));
        int currentTexture = 0;
        foreach (var texture in textures)
        {
            if (token.IsCancellationRequested) break;

            progress.Report((texture.Key, ++currentTexture));

            logger.LogInformation("Converting Texture {path} to {type}", texture.Key, PenumbraEnum.TextureType.Bc7Tex);
            var convertTask = _penumbraConvertTextureFile.Invoke(texture.Key, texture.Key, PenumbraEnum.TextureType.Bc7Tex, mipMaps: true);
            await convertTask.ConfigureAwait(false);
            if (convertTask.IsCompletedSuccessfully && texture.Value.Any())
            {
                foreach (var duplicatedTexture in texture.Value)
                {
                    logger.LogInformation("Migrating duplicate {dup}", duplicatedTexture);
                    try
                    {
                        File.Copy(texture.Key, duplicatedTexture, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to copy duplicate {dup}", duplicatedTexture);
                    }
                }
            }
        }
        _core.Mediator.Publish(new ResumeScanMessage(nameof(ConvertTextureFiles)));

        await _core.DalamudUtil.RunOnFrameworkThread(async () =>
        {
            // Force un redraw après conversion
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
