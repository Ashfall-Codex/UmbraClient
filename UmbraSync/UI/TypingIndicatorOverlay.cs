using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Extensions;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI;

public sealed class TypingIndicatorOverlay : WindowMediatorSubscriberBase
{
    private const int NameplateIconId = 61397;
    private static readonly TimeSpan TypingDisplayTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TypingDisplayDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TypingDisplayFade = TypingDisplayTime;

    private readonly ILogger<TypingIndicatorOverlay> _typedLogger;
    private readonly MareConfigService _configService;
    private readonly IGameGui _gameGui;
    private readonly ITextureProvider _textureProvider;
    private readonly IClientState _clientState;
    private readonly PairManager _pairManager;
    private readonly IPartyList _partyList;
    private readonly IObjectTable _objectTable;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly TypingIndicatorStateService _typingStateService;
    private readonly ApiController _apiController;
    private readonly Ashfall.Engine.OverlayEngine _engine;

    public TypingIndicatorOverlay(ILogger<TypingIndicatorOverlay> logger, MareMediator mediator, PerformanceCollectorService performanceCollectorService,
        MareConfigService configService, IGameGui gameGui, ITextureProvider textureProvider, IClientState clientState,
        IPartyList partyList, IObjectTable objectTable, DalamudUtilService dalamudUtil, PairManager pairManager,
        TypingIndicatorStateService typingStateService, ApiController apiController,
        Ashfall.Engine.OverlayEngine engine)
        : base(logger, mediator, nameof(TypingIndicatorOverlay), performanceCollectorService)
    {
        _typedLogger = logger;
        _configService = configService;
        _gameGui = gameGui;
        _textureProvider = textureProvider;
        _clientState = clientState;
        _partyList = partyList;
        _objectTable = objectTable;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _typingStateService = typingStateService;
        _apiController = apiController;
        _engine = engine;

        RespectCloseHotkey = false;
        IsOpen = true;
        Flags |= ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav;
    }

    protected override void DrawInternal()
    {
        var viewport = ImGui.GetMainViewport();
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetWindowPos(viewport.Pos);
        ImGui.SetWindowSize(viewport.Size);

        if (!_clientState.IsLoggedIn)
            return;

        var showParty = _configService.Current.TypingIndicatorShowOnPartyList;
        var showNameplates = _configService.Current.TypingIndicatorShowOnNameplates;

        if ((!showParty && !showNameplates) || _dalamudUtil.IsInGpose)
            return;

        var overlayDrawList = ImGui.GetWindowDrawList();
        var activeTypers = _typingStateService.GetActiveTypers(TypingDisplayTime);
        var hasSelf = _typingStateService.TryGetSelfTyping(TypingDisplayTime, out var selfStart, out var selfLast);
        var now = DateTime.UtcNow;

        if (showParty)
        {
            DrawPartyIndicators(overlayDrawList, activeTypers, hasSelf, now, selfStart, selfLast);
        }

        if (showNameplates)
        {
            DrawNameplateIndicators(ImGui.GetWindowDrawList(), activeTypers, hasSelf, now, selfStart, selfLast);
        }
    }

    private unsafe void DrawPartyIndicators(ImDrawListPtr drawList, IReadOnlyDictionary<string, (UserData User, DateTime FirstSeen, DateTime LastUpdate)> activeTypers,
        bool selfActive, DateTime now, DateTime selfStart, DateTime selfLast)
    {
        var partyAddon = (AtkUnitBase*)_gameGui.GetAddonByName("_PartyList", 1).Address;
        if (partyAddon == null || !partyAddon->IsVisible)
            return;

        var showSelf = _configService.Current.TypingIndicatorShowSelf;
        if (selfActive
            && showSelf
            && (now - selfStart) >= TypingDisplayDelay
            && (now - selfLast) <= TypingDisplayFade)
        {
            DrawPartyMemberTyping(drawList, partyAddon, 0);
        }

        foreach (var (uid, entry) in activeTypers)
        {
            if ((now - entry.LastUpdate) > TypingDisplayFade)
                continue;

            var pair = _pairManager.GetPairByUID(uid);
            var targetIndex = -1;
            var playerName = pair?.PlayerName;
            var objectId = pair?.PlayerCharacterId ?? uint.MaxValue;

            if (objectId != 0 && objectId != uint.MaxValue)
            {
                targetIndex = GetPartyIndexFromAgentHUD(objectId);
            }
            if (targetIndex < 0 && objectId != 0 && objectId != uint.MaxValue)
            {
                targetIndex = GetPartyIndexForObjectId(objectId);
            }

            if (targetIndex < 0 && !string.IsNullOrEmpty(playerName))
            {
                targetIndex = GetPartyIndexForName(playerName);
            }

            if (targetIndex < 0)
                continue;

            DrawPartyMemberTyping(drawList, partyAddon, targetIndex);
        }
    }

    private unsafe void DrawPartyMemberTyping(ImDrawListPtr drawList, AtkUnitBase* partyList, int memberIndex)
    {
        if (memberIndex < 0 || memberIndex > 7) return;

        var nodeIndex = 23 - memberIndex;
        if (partyList->UldManager.NodeListCount <= nodeIndex) return;

        var memberNode = (AtkComponentNode*)partyList->UldManager.NodeList[nodeIndex];
        if (memberNode == null || !memberNode->AtkResNode.IsVisible()) return;

        var iconNode = memberNode->Component->UldManager.NodeListCount > 4 ? memberNode->Component->UldManager.NodeList[4] : null;
        if (iconNode == null) return;

        var align = partyList->UldManager.NodeList[3]->Y;
        var partyScale = partyList->Scale;

        var iconOffset = new Vector2(-14, 8) * partyScale;
        var iconSize = new Vector2(iconNode->Width / 2f, iconNode->Height / 2f) * partyScale;

        var iconPos = new Vector2(
            partyList->X + (memberNode->AtkResNode.X * partyScale) + (iconNode->X * partyScale) + (iconNode->Width * partyScale / 2f),
            partyList->Y + align + (memberNode->AtkResNode.Y * partyScale) + (iconNode->Y * partyScale) + (iconNode->Height * partyScale / 2f));

        iconPos += iconOffset;

        var texture = _textureProvider.GetFromGame("ui/uld/charamake_dataimport.tex").GetWrapOrEmpty();
        if (texture.Handle == IntPtr.Zero) return;

        drawList.AddImage(texture.Handle, iconPos, iconPos + iconSize, Vector2.Zero, Vector2.One,
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.9f)));
    }

    private unsafe void DrawNameplateIndicators(ImDrawListPtr drawList, IReadOnlyDictionary<string, (UserData User, DateTime FirstSeen, DateTime LastUpdate)> activeTypers,
        bool selfActive, DateTime now, DateTime selfStart, DateTime selfLast)
    {
        var iconWrap = _textureProvider.GetFromGameIcon(NameplateIconId).GetWrapOrEmpty();
        if (iconWrap.Handle == IntPtr.Zero)
            return;

        var nameplateAddonPtr = (AtkUnitBase*)_gameGui.GetAddonByName("NamePlate", 1).Address;
        _engine.BeginFrame(nameplateAddonPtr);

        try
        {
        var showSelf = _configService.Current.TypingIndicatorShowSelf;
        if (selfActive
            && showSelf
            && _objectTable.LocalPlayer != null
            && (now - selfStart) >= TypingDisplayDelay
            && (now - selfLast) <= TypingDisplayFade)
        {
            var selfId = GetEntityId(_objectTable.LocalPlayer.Address);
            // For self, if the nameplate isn't available (e.g. user hid their own nameplate),
            // fall back to a world-anchored bubble above the player.
            if (selfId != 0 && !TryDrawNameplateBubble(drawList, iconWrap, selfId))
                DrawWorldFallbackIcon(drawList, iconWrap, _objectTable.LocalPlayer.Position);
        }

        foreach (var (uid, entry) in activeTypers)
        {
            if ((now - entry.LastUpdate) > TypingDisplayFade)
                continue;

            if (string.Equals(uid, _apiController.UID, StringComparison.Ordinal))
                continue;

            var pair = _pairManager.GetPairByUID(uid);
            var objectId = pair?.PlayerCharacterId ?? 0;
            if (objectId == 0 || objectId == uint.MaxValue) continue;

            if (TryDrawNameplateBubble(drawList, iconWrap, objectId))
                continue;

            // Fallback world bubble only if the player is nearby (< 15 yalms): this means
            // the nameplate is hidden by user settings rather than faded by distance.
            if (TryGetNearbyPlayerPosition(objectId, 15f, out var worldPos))
                DrawWorldFallbackIcon(drawList, iconWrap, worldPos);
        }
        }
        finally
        {
            _engine.EndFrame();
        }
    }

    private Vector2 GetConfiguredBubbleSize(float scaleX, float scaleY, bool isNameplateVisible, TypingIndicatorBubbleSize? overrideSize = null)
    {
        var sizeSetting = overrideSize ?? _configService.Current.TypingIndicatorBubbleSize;
        var baseSize = sizeSetting switch
        {
            TypingIndicatorBubbleSize.Small when isNameplateVisible => 32f,
            TypingIndicatorBubbleSize.Medium when isNameplateVisible => 44f,
            TypingIndicatorBubbleSize.Large when isNameplateVisible => 56f,
            TypingIndicatorBubbleSize.Small => 15f,
            TypingIndicatorBubbleSize.Medium => 25f,
            TypingIndicatorBubbleSize.Large => 35f,
            _ => 35f,
        };

        return new Vector2(baseSize * scaleX, baseSize * scaleY);
    }

    private unsafe bool TryDrawNameplateBubble(ImDrawListPtr drawList, IDalamudTextureWrap textureWrap, uint objectId)
    {
        if (textureWrap.Handle == IntPtr.Zero)
            return false;

        var framework = Framework.Instance();
        if (framework == null)
            return false;

        var ui3D = framework->GetUIModule()->GetUI3DModule();
        if (ui3D == null)
            return false;

        var addonNamePlate = (AddonNamePlate*)_gameGui.GetAddonByName("NamePlate", 1).Address;
        if (addonNamePlate == null)
            return false;

        AddonNamePlate.NamePlateObject* namePlate = null;
        float distance = 0f;
        System.Numerics.Vector3 playerWorldPos = default;

        for (var i = 0; i < ui3D->NamePlateObjectInfoCount; i++)
        {
            var objectInfo = ui3D->NamePlateObjectInfoPointers[i];
            if (objectInfo.Value == null || objectInfo.Value->GameObject == null)
                continue;

            if (objectInfo.Value->GameObject->EntityId != objectId)
                continue;

            if ((byte)objectInfo.Value->GameObject->ObjectKind != 1)
                continue;

            if (objectInfo.Value->GameObject->YalmDistanceFromPlayerX > 15f)
                return false;

            namePlate = &addonNamePlate->NamePlateObjectArray[objectInfo.Value->NamePlateIndex];
            distance = objectInfo.Value->GameObject->YalmDistanceFromPlayerX;
            var gp = objectInfo.Value->GameObject->Position;
            playerWorldPos = new System.Numerics.Vector3(gp.X, gp.Y + 2.2f, gp.Z);
            break;
        }

        // Occlusion 3D pixel-perfect via l'engine (stratégie automatique selon plateforme).
        bool ShouldSkipByDepth(Vector2 center)
            => _engine.IsWorldOccluded(playerWorldPos, center);

        if (namePlate == null || namePlate->RootComponentNode == null)
            return false;

        var iconNode = namePlate->RootComponentNode->Component->UldManager.NodeList[0];
        if (iconNode == null)
            return false;

        var nameplateScaleX = namePlate->RootComponentNode->AtkResNode.ScaleX;
        var nameplateScaleY = namePlate->RootComponentNode->AtkResNode.ScaleY;
        var iconVisible = iconNode->IsVisible();
        var scaleVector = new Vector2(nameplateScaleX, nameplateScaleY);

        const float bubbleScaleFactor = 1.0f;
        var rootPosition = new Vector2(namePlate->RootComponentNode->AtkResNode.X, namePlate->RootComponentNode->AtkResNode.Y);
        var iconLocalPosition = new Vector2(iconNode->X, iconNode->Y) * scaleVector;
        var iconDimensions = new Vector2(iconNode->Width, iconNode->Height) * scaleVector;

        // The nameplate icon node is hidden (faded by distance, or hidden by FFXIV settings such as
        // "always hide my nameplate"). The nameplate object itself still has a valid screen position,
        // so we anchor the bubble to that position rather than computing one from the world.
        if (!iconVisible)
        {
            var anchor = rootPosition + iconLocalPosition + new Vector2(iconDimensions.X * 0.5f, 0f);
            var hiddenOffset = new Vector2(0f, -16f + distance) * scaleVector;
            if (iconNode->Height == 24)
                hiddenOffset.Y += 16f * nameplateScaleY;
            hiddenOffset.Y += 64f * nameplateScaleY;

            var hiddenSize = GetConfiguredBubbleSize(bubbleScaleFactor, bubbleScaleFactor, true);
            var hiddenCenter = anchor + hiddenOffset + new Vector2(hiddenSize.X * 0.5f, hiddenSize.Y * 0.5f);
            var hiddenTopLeft = hiddenCenter - hiddenSize / 2f;

            if (ShouldSkipByDepth(hiddenCenter)) return true;
            if (_engine.IsNativeUiOccluded(new Vector4(hiddenTopLeft.X, hiddenTopLeft.Y, hiddenTopLeft.X + hiddenSize.X, hiddenTopLeft.Y + hiddenSize.Y))) return true;

            drawList.AddImage(textureWrap.Handle, hiddenTopLeft, hiddenTopLeft + hiddenSize, Vector2.Zero, Vector2.One,
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.95f)));
            return true;
        }

        var iconPos = rootPosition + iconLocalPosition + new Vector2(iconDimensions.X, 0f);

        var iconOffset = new Vector2(distance / 1.5f, distance / 3.5f) * scaleVector;
        if (iconNode->Height == 24)
        {
            iconOffset.Y -= 8f * nameplateScaleY;
        }

        iconPos += iconOffset;

        var bubbleSize = GetConfiguredBubbleSize(bubbleScaleFactor, bubbleScaleFactor, true);

        if (ShouldSkipByDepth(iconPos + bubbleSize * 0.5f)) return true;
        if (_engine.IsNativeUiOccluded(new Vector4(iconPos.X, iconPos.Y, iconPos.X + bubbleSize.X, iconPos.Y + bubbleSize.Y))) return true;

        drawList.AddImage(textureWrap.Handle, iconPos, iconPos + bubbleSize, Vector2.Zero, Vector2.One,
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.95f)));

        return true;
    }

    private unsafe int GetPartyIndexFromAgentHUD(uint objectId)
    {
        if (objectId == 0 || objectId == uint.MaxValue)
            return -1;

        try
        {
            var framework = Framework.Instance();
            if (framework == null) return -1;

            var uiModule = framework->GetUIModule();
            if (uiModule == null) return -1;

            var agentModule = uiModule->GetAgentModule();
            if (agentModule == null) return -1;

            var agentHud = agentModule->GetAgentHUD();
            if (agentHud == null) return -1;

            var partyMembers = agentHud->PartyMembers;

            for (var i = 0; i < agentHud->PartyMemberCount; i++)
            {
                if (partyMembers[i].EntityId == objectId)
                    return i;
            }
        }
        catch (Exception ex)
        {
            _typedLogger.LogDebug(ex, "Failed to get party index from AgentHUD for objectId {ObjectId}", objectId);
        }

        return -1;
    }

    private int GetPartyIndexForObjectId(uint objectId)
    {
        for (var i = 0; i < _partyList.Count; ++i)
        {
            var member = _partyList[i];
            if (member == null) continue;

            var gameObject = member.GameObject;
            if (gameObject != null && GetEntityId(gameObject.Address) == objectId)
                return i;
        }

        return -1;
    }

    private int GetPartyIndexForName(string name)
    {
        for (var i = 0; i < _partyList.Count; ++i)
        {
            var member = _partyList[i];
            if (member?.Name == null) continue;

            if (member.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static unsafe uint GetEntityId(nint address)
    {
        if (address == nint.Zero) return 0;
        return ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address)->EntityId;
    }

    private void DrawWorldFallbackIcon(ImDrawListPtr drawList, IDalamudTextureWrap textureWrap, Vector3 worldPosition)
    {
        var offsetPosition = worldPosition + new Vector3(0f, 1.8f, 0f);
        if (!_gameGui.WorldToScreen(offsetPosition, out var screenPos))
            return;

        var iconSize = GetConfiguredBubbleSize(ImGuiHelpers.GlobalScale, ImGuiHelpers.GlobalScale, false);
        var iconPos = screenPos - (iconSize / 2f) - new Vector2(0f, iconSize.Y * 0.6f);
        drawList.AddImage(textureWrap.Handle, iconPos, iconPos + iconSize, Vector2.Zero, Vector2.One,
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.95f)));
    }

    // Returns true and sets `position` if the player is found in the object table within `maxYalms`.
    // Used to detect "nameplate hidden by user settings" (object exists nearby but FFXIV did not
    // render its nameplate) vs "out of range" (object not found / too far).
    private bool TryGetNearbyPlayerPosition(uint objectId, float maxYalms, out Vector3 position)
    {
        position = Vector3.Zero;
        if (objectId == 0 || objectId == uint.MaxValue || _objectTable.LocalPlayer == null)
            return false;

        for (var i = 0; i < _objectTable.Length; ++i)
        {
            var obj = _objectTable[i];
            if (obj == null) continue;
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player) continue;
            if (obj.EntityId != objectId) continue;

            var dist = Vector3.Distance(_objectTable.LocalPlayer.Position, obj.Position);
            if (dist > maxYalms) return false;
            position = obj.Position;
            return true;
        }
        return false;
    }
}
