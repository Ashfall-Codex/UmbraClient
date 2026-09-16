using Microsoft.Extensions.Logging;
using UmbraSync.MareConfiguration;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Interop.Ipc;

public sealed class IpcCallerMare : DisposableMediatorSubscriberBase
{
    private static readonly string[] ForeignSyncCollectionPrefixes = ["Snowcloak_", "Lightless_"];

    private readonly DalamudUtilService _dalamudUtil;
    private readonly IpcCallerPenumbra _penumbra;
    private readonly MareConfigService _configService;

    public IpcCallerMare(ILogger<IpcCallerMare> logger, MareMediator mediator,
        DalamudUtilService dalamudUtil, IpcCallerPenumbra penumbra, MareConfigService configService) : base(logger, mediator)
    {
        _dalamudUtil = dalamudUtil;
        _penumbra = penumbra;
        _configService = configService;
    }

    public bool IsReclaimEnabled => _configService.Current.ExperimentalExternalSyncReclaim;

    public static bool IsForeignSyncCollection(string? collectionName)
        => !string.IsNullOrEmpty(collectionName)
           && ForeignSyncCollectionPrefixes.Any(prefix => collectionName.StartsWith(prefix, StringComparison.Ordinal));

    // Préfixe seul : le reste du nom contient l'identifiant de la paire chez l'autre plugin
    public static string DescribeForeignSyncCollection(string? collectionName)
        => ForeignSyncCollectionPrefixes.FirstOrDefault(prefix => collectionName?.StartsWith(prefix, StringComparison.Ordinal) ?? false) ?? "inconnue";

    public (Guid Id, string Name)? GetEffectiveCollection(int objectIndex)
        => _penumbra.GetEffectiveCollectionOnFramework(objectIndex);

    // Must be called on framework thread
    public bool IsForeignSyncCollectionActive(nint address)
    {
        if (address == nint.Zero) return false;

        try
        {
            if (_dalamudUtil.CreateGameObject(address) is not { } gameObject) return false;
            return IsForeignSyncCollection(GetEffectiveCollection(gameObject.ObjectIndex)?.Name);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Lecture de la collection Penumbra impossible pour l'adresse {address:X}", address);
            return false;
        }
    }
}
