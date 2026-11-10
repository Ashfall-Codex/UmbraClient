using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Modules.Rp.Services;

public sealed class RpJournalContextMenuService : IDisposable, IRuntimeScopedService
{
    private readonly ILogger<RpJournalContextMenuService> _logger;
    private readonly IContextMenu _contextMenu;
    private readonly MareConfigService _configService;
    private readonly RpJournalService _journal;
    private readonly PairManager _pairManager;
    private readonly MareMediator _mediator;
    private bool _subscribed;

    public RpJournalContextMenuService(ILogger<RpJournalContextMenuService> logger, IContextMenu contextMenu,
        MareConfigService configService, RpJournalService journal, PairManager pairManager, MareMediator mediator)
    {
        _logger = logger;
        _contextMenu = contextMenu;
        _configService = configService;
        _journal = journal;
        _pairManager = pairManager;
        _mediator = mediator;
        _contextMenu.OnMenuOpened += OnMenuOpened;
        _subscribed = true;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            if (args.MenuType != ContextMenuType.Default) return;
            if (!_configService.Current.EnableRightClickMenus || !_journal.HasOwner) return;
            if (args.Target is not MenuTargetDefault target) return;

            var name = target.TargetName;
            var worldId = target.TargetHomeWorld.RowId;
            if (string.IsNullOrEmpty(name) || worldId == 0) return;

            bool known = _journal.TryGetSubject(name, worldId) != null;
            var objectId = (uint)target.TargetObjectId;
            args.AddMenuItem(new MenuItem
            {
                Name = Loc.Get(known ? "Journal.ContextMenu.Open" : "Journal.ContextMenu.Start"),
                PrefixColor = 708,
                PrefixChar = 'U',
                UseDefaultPrefix = false,
                IsEnabled = true,
                IsSubmenu = false,
                IsReturn = false,
                Priority = 1,
                OnClicked = _ =>
                {
                    var uid = _pairManager.GetVisiblePairByObjectId(objectId)?.UserData.UID;
                    _mediator.Publish(new OpenJournalMessage(name, worldId, uid));
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Journal: menu contextuel");
        }
    }

    public void Dispose()
    {
        if (!_subscribed) return;
        _subscribed = false;
        try
        {
            _contextMenu.OnMenuOpened -= OnMenuOpened;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Journal: désabonnement du menu contextuel");
        }
    }
}
