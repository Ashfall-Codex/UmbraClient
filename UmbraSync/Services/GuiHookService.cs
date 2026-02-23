using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;
using UmbraSync.UI;

namespace UmbraSync.Services;

public class GuiHookService : DisposableMediatorSubscriberBase
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly MareConfigService _configService;
    private readonly INamePlateGui _namePlateGui;
    private readonly IGameConfig _gameConfig;
    private readonly IPartyList _partyList;
    private readonly IObjectTable _objectTable;
    private readonly PairManager _pairManager;
    private readonly UmbraProfileManager _umbraProfileManager;
    private readonly ApiController _apiController;

    private bool _isModified;
    private bool _isInDuty;
    private bool _namePlateRoleColorsEnabled;

    public GuiHookService(ILogger<GuiHookService> logger, DalamudUtilService dalamudUtil, MareMediator mediator, MareConfigService configService,
        INamePlateGui namePlateGui, IGameConfig gameConfig, IPartyList partyList, IObjectTable objectTable,
        PairManager pairManager, UmbraProfileManager umbraProfileManager, ApiController apiController)
        : base(logger, mediator)
    {
        _dalamudUtil = dalamudUtil;
        _configService = configService;
        _namePlateGui = namePlateGui;
        _gameConfig = gameConfig;
        _partyList = partyList;
        _objectTable = objectTable;
        _pairManager = pairManager;
        _umbraProfileManager = umbraProfileManager;
        _apiController = apiController;

        _namePlateGui.OnNamePlateUpdate += OnNamePlateUpdate;
        _namePlateGui.RequestRedraw();

        Mediator.Subscribe<InstanceOrDutyStartMessage>(this, (_) => { _isInDuty = true; RequestRedraw(force: true); });
        Mediator.Subscribe<InstanceOrDutyEndMessage>(this, (_) => { _isInDuty = false; RequestRedraw(force: true); });
        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => GameSettingsCheck());
        Mediator.Subscribe<PairHandlerVisibleMessage>(this, (_) => RequestRedraw());
        Mediator.Subscribe<NameplateRedrawMessage>(this, (_) => RequestRedraw());
        Mediator.Subscribe<ClearProfileDataMessage>(this, (_) => RequestRedraw());
        Mediator.Subscribe<UserTypingStateMessage>(this, (_) => RequestRedraw());
    }

    public void RequestRedraw(bool force = false)
    {
        var useColors = _configService.Current.UseNameColors;
        var useRpNames = _configService.Current.UseRpNamesOnNameplates;

        if (!useColors && !useRpNames)
        {
            if (!_isModified && !force)
                return;
            _isModified = false;
        }

        _ = Task.Run(async () =>
        {
            await _dalamudUtil.RunOnFrameworkThread(() => _namePlateGui.RequestRedraw()).ConfigureAwait(false);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _namePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate;

        _ = Task.Run(async () =>
        {
            await _dalamudUtil.RunOnFrameworkThread(() => _namePlateGui.RequestRedraw()).ConfigureAwait(false);
        });
    }

    private void OnNamePlateUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        var applyColors = _configService.Current.UseNameColors;
        var applyRpNames = _configService.Current.UseRpNamesOnNameplates;
        if (!applyColors && !applyRpNames)
            return;

        if (_isInDuty && _configService.Current.DisableNameplatesInDuty)
            return;

        var visibleUsers = _pairManager.GetOnlineUserPairs()
            .Where(u => u.IsVisible && u.PlayerCharacterId != uint.MaxValue)
            .ToList();
        var visibleUsersIds = visibleUsers.Select(u => (ulong)u.PlayerCharacterId).ToHashSet();

        var visibleUsersDict = visibleUsers.ToDictionary(u => (ulong)u.PlayerCharacterId);

        var partyMembers = new nint[_partyList.Count];

        for (int i = 0; i < _partyList.Count; ++i)
        {
            var partyMember = _partyList[i];
            if (partyMember == null)
            {
                partyMembers[i] = nint.MaxValue;
                continue;
            }

            var gameObject = partyMember.GameObject;
            partyMembers[i] = gameObject != null ? gameObject.Address : nint.MaxValue;
        }

        if (applyRpNames && _apiController.IsConnected && !string.IsNullOrEmpty(_apiController.UID))
        {
            var localPlayer = _objectTable.LocalPlayer;
            if (localPlayer != null)
            {
                var localProfile = _umbraProfileManager.GetUmbraProfile(new API.Data.UserData(_apiController.UID));
                var localPlayerName = _dalamudUtil.GetPlayerName();
                if (!string.IsNullOrEmpty(localProfile.RpFirstName) && !string.IsNullOrEmpty(localProfile.RpLastName)
                    && !string.IsNullOrEmpty(localPlayerName) && IsRpFirstNameValid(localPlayerName, localProfile.RpFirstName))
                {
                    var localObjectId = localPlayer.GameObjectId;
                    foreach (var handler in handlers)
                    {
                        if (handler.GameObjectId == localObjectId)
                        {
                            handler.NameParts.Text = new SeString(new TextPayload(BuildRpDisplayName(localProfile)));
                            _isModified = true;
                            break;
                        }
                    }
                }
            }
        }

        foreach (var handler in handlers)
        {
            if (visibleUsersIds.Contains(handler.GameObjectId))
            {
                var skipColors = false;
                if (_namePlateRoleColorsEnabled)
                {
                    var handlerGameObject = handler.GameObject;
                    var handlerObjectAddress = handlerGameObject != null ? handlerGameObject.Address : nint.MaxValue;
                    if (partyMembers.Contains(handlerObjectAddress))
                        skipColors = true;
                }

                var pair = visibleUsersDict[handler.GameObjectId];

                if (applyColors && !skipColors)
                {
                    var colors = !pair.IsApplicationBlocked ? _configService.Current.NameColors : _configService.Current.BlockedNameColors;
                    handler.NameParts.TextWrap = (
                        BuildColorStartSeString(colors),
                        BuildColorEndSeString(colors)
                    );
                    _isModified = true;
                }

                if (applyRpNames)
                {
                    var profile = _umbraProfileManager.GetUmbraProfile(pair.UserData);
                    if (!string.IsNullOrEmpty(profile.RpFirstName) && !string.IsNullOrEmpty(profile.RpLastName)
                        && !string.IsNullOrEmpty(pair.PlayerName) && IsRpFirstNameValid(pair.PlayerName, profile.RpFirstName))
                    {
                        handler.NameParts.Text = new SeString(new TextPayload(BuildRpDisplayName(profile)));
                        _isModified = true;
                    }
                }
            }

        }
    }

    private void GameSettingsCheck()
    {
        if (!_gameConfig.TryGet(Dalamud.Game.Config.UiConfigOption.NamePlateSetRoleColor, out bool namePlateRoleColorsEnabled))
            return;

        if (_namePlateRoleColorsEnabled != namePlateRoleColorsEnabled)
        {
            _namePlateRoleColorsEnabled = namePlateRoleColorsEnabled;
            RequestRedraw(force: true);
        }
    }

    private static bool IsRpFirstNameValid(string vanillaFullName, string rpFirstName)
    {
        var spaceIndex = vanillaFullName.IndexOf(' ');
        var vanillaFirstName = spaceIndex >= 0 ? vanillaFullName[..spaceIndex] : vanillaFullName;
        foreach (var part in rpFirstName.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part, vanillaFirstName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string BuildRpDisplayName(UmbraProfileData profile)
    {
        var name = $"{profile.RpFirstName} {profile.RpLastName}";
        return !string.IsNullOrEmpty(profile.RpTitle) ? $"{profile.RpTitle} {name}" : name;
    }

    #region Colored SeString
    private const byte _colorTypeForeground = 0x13;
    private const byte _colorTypeGlow = 0x14;

    private static SeString BuildColorStartSeString(DtrEntry.Colors colors)
    {
        var ssb = new SeStringBuilder();
        if (colors.Foreground != default)
            ssb.Add(BuildColorStartPayload(_colorTypeForeground, colors.Foreground));
        if (colors.Glow != default)
            ssb.Add(BuildColorStartPayload(_colorTypeGlow, colors.Glow));
        return ssb.Build();
    }

    private static SeString BuildColorEndSeString(DtrEntry.Colors colors)
    {
        var ssb = new SeStringBuilder();
        if (colors.Glow != default)
            ssb.Add(BuildColorEndPayload(_colorTypeGlow));
        if (colors.Foreground != default)
            ssb.Add(BuildColorEndPayload(_colorTypeForeground));
        return ssb.Build();
    }

    private static RawPayload BuildColorStartPayload(byte colorType, uint color)
        => new(unchecked([0x02, colorType, 0x05, 0xF6, byte.Max((byte)color, 0x01), byte.Max((byte)(color >> 8), 0x01), byte.Max((byte)(color >> 16), 0x01), 0x03]));

    private static RawPayload BuildColorEndPayload(byte colorType)
        => new([0x02, colorType, 0x02, 0xEC, 0x03]);
    #endregion
}