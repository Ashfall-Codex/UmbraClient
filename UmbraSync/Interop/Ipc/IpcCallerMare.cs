using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Microsoft.Extensions.Logging;
using UmbraSync.MareConfiguration;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Interop.Ipc;

public enum ExternalSyncStatus
{
    None,
    Listed,
    Owned
}

public sealed class IpcCallerMare : DisposableMediatorSubscriberBase
{
    private const string SnowcloakInternalName = "Snowcloak";
    private const string SnowcloakCollectionPrefix = "Snowcloak_";

    private readonly DalamudUtilService _dalamudUtil;
    private readonly IpcCallerPenumbra _penumbra;
    private readonly MareConfigService _configService;
    private readonly ICallGateSubscriber<List<nint>> _snowcloakHandledGameAddresses;
    private static readonly IReadOnlyList<nint> EmptyAddresses = Array.Empty<nint>();
    private volatile bool _snowcloakLoaded;

    public IpcCallerMare(ILogger<IpcCallerMare> logger, IDalamudPluginInterface pi, MareMediator mediator,
        DalamudUtilService dalamudUtil, IpcCallerPenumbra penumbra, MareConfigService configService) : base(logger, mediator)
    {
        _dalamudUtil = dalamudUtil;
        _penumbra = penumbra;
        _configService = configService;
        _snowcloakHandledGameAddresses = pi.GetIpcSubscriber<List<nint>>($"{SnowcloakInternalName}.GetHandledAddresses");
        _snowcloakLoaded = PluginWatcherService.GetInitialPluginState(pi, SnowcloakInternalName)?.IsLoaded ?? false;

        Mediator.SubscribeKeyed<PluginChangeMessage>(this, SnowcloakInternalName, msg => _snowcloakLoaded = msg.IsLoaded);
    }

    public bool APIAvailable { get; private set; } = false;
    public IReadOnlyCollection<nint> GetExternallyOwnedAddresses()
    {
        if (!_configService.Current.YieldToExternalSync) return EmptyAddresses;

        HashSet<nint>? owned = null;
        foreach (var address in GetSnowcloakAddresses())
        {
            if (IsCollectionOwnedBySnowcloak(address))
                (owned ??= []).Add(address);
        }

        return owned ?? (IReadOnlyCollection<nint>)EmptyAddresses;
    }

    // Must be called on framework thread
    public ExternalSyncStatus GetExternalSyncStatus(nint address)
    {
        if (address == nint.Zero || !_configService.Current.YieldToExternalSync) return ExternalSyncStatus.None;
        if (!GetSnowcloakAddresses().Contains(address)) return ExternalSyncStatus.None;

        return IsCollectionOwnedBySnowcloak(address) ? ExternalSyncStatus.Owned : ExternalSyncStatus.Listed;
    }

    private IReadOnlyList<nint> GetSnowcloakAddresses()
    {
        if (!_snowcloakLoaded) return EmptyAddresses;

        try
        {
            return _snowcloakHandledGameAddresses.InvokeFunc();
        }
        catch
        {
            return EmptyAddresses;
        }
    }

    private bool IsCollectionOwnedBySnowcloak(nint address)
    {
        try
        {
            if (_dalamudUtil.CreateGameObject(address) is not { } gameObject) return false;
            var collectionName = _penumbra.GetEffectiveCollectionNameOnFramework(gameObject.ObjectIndex);
            return collectionName != null && collectionName.StartsWith(SnowcloakCollectionPrefix, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Lecture de la collection Penumbra impossible pour l'adresse {address:X}", address);
            return false;
        }
    }
}
