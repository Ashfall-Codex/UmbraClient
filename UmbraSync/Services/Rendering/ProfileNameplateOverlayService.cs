using Ashfall.Engine;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.API.Data;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services.Rendering;

public sealed unsafe class ProfileNameplateOverlayService : DisposableMediatorSubscriberBase
{
    private readonly PairManager _pairManager;
    private readonly UmbraProfileManager _profileManager;
    private readonly IGameGui _gameGui;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ITextureProvider _textureProvider;
#pragma warning disable S4487 // injecté pour forcer l'initialisation du singleton PictomancyService (side-effect ctor)
    private readonly PictomancyService _pictomancyService;
#pragma warning restore S4487
    private readonly OverlayEngine _engine;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ILogger<ProfileNameplateOverlayService> _logger;

    private bool _drawSubscribed;

    public ProfileNameplateOverlayService(
        ILogger<ProfileNameplateOverlayService> logger,
        MareMediator mediator,
        PairManager pairManager,
        UmbraProfileManager profileManager,
        IGameGui gameGui,
        IClientState clientState,
        IObjectTable objectTable,
        ITextureProvider textureProvider,
        PictomancyService pictomancyService,
        OverlayEngine engine,
        IDalamudPluginInterface pluginInterface)
        : base(logger, mediator)
    {
        _logger = logger;
        _pairManager = pairManager;
        _profileManager = profileManager;
        _gameGui = gameGui;
        _clientState = clientState;
        _objectTable = objectTable;
        _textureProvider = textureProvider;
        _pictomancyService = pictomancyService;
        _engine = engine;
        _pluginInterface = pluginInterface;

        _pluginInterface.UiBuilder.Draw += OnDraw;
        _drawSubscribed = true;
    }


    private void OnDraw()
    {
        if (_objectTable.LocalPlayer == null) return;
        if (_clientState.IsPvPExcludingDen) return;
        if (_gameGui.GameUiHidden) return;

        try
        {
            var addonHandle = _gameGui.GetAddonByName("NamePlate");
            if (addonHandle.Address == nint.Zero) return;
            var addon = (AddonNamePlate*)addonHandle.Address;
            if (addon->AtkUnitBase.RootNode == null || !addon->AtkUnitBase.RootNode->IsVisible()) return;

            var framework = Framework.Instance();
            if (framework == null) return;
            var uiModule = framework->GetUIModule();
            if (uiModule == null) return;
            var ui3DModule = uiModule->GetUI3DModule();
            if (ui3DModule == null) return;

            var vp = ImGui.GetMainViewport();
            var useViewportOffset = ImGui.GetIO().ConfigFlags.HasFlag(ImGuiConfigFlags.ViewportsEnable);
            var drawList = ImGui.GetBackgroundDrawList(vp);

            _engine.BeginFrame((AtkUnitBase*)addon);

            var ownUid = _profileManager.CurrentUid;
            var localPlayer = _objectTable.LocalPlayer;
            var localGoId = localPlayer != null ? localPlayer.GameObjectId : 0UL;

            var pairsByGoId = new Dictionary<ulong, UserData>();
            foreach (var userData in _pairManager.GetVisibleUsers())
            {
                var pair = _pairManager.GetPairByUID(userData.UID);
                if (pair == null || !pair.IsVisible) continue;
                var pcid = pair.PlayerCharacterId;
                if (pcid == 0 || pcid == uint.MaxValue) continue;
                pairsByGoId[pcid] = userData;
            }

            var infoPointers = ui3DModule->NamePlateObjectInfoPointers;
            var infoCount = Math.Min(ui3DModule->NamePlateObjectInfoCount, infoPointers.Length);

            for (int i = 0; i < infoCount; i++)
            {
                var objInfoPtr = infoPointers[i];
                if (objInfoPtr == null) continue;
                var objInfo = objInfoPtr.Value;
                if (objInfo == null || objInfo->GameObject == null) continue;

                var gameObject = objInfo->GameObject;
                if ((ObjectKind)gameObject->ObjectKind != ObjectKind.Player) continue;

                var nameplateIndex = objInfo->NamePlateIndex;
                if (nameplateIndex < 0 || nameplateIndex >= AddonNamePlate.NumNamePlateObjects) continue;

                var np = addon->NamePlateObjectArray[nameplateIndex];
                var nameContainer = np.NameContainer;
                var nameText = np.NameText;
                var rootNode = np.RootComponentNode;
                if (nameContainer == null || !nameContainer->IsVisible()) continue;
                if (nameText == null) continue;

                // Fade progressif
                const float fadeStart = 15f;
                const float fadeEnd = 25f;
                float distance = gameObject->YalmDistanceFromPlayerX;
                float distanceFade;
                if (distance >= fadeEnd) continue;
                else if (distance <= fadeStart) distanceFade = 1f;
                else distanceFade = 1f - (distance - fadeStart) / (fadeEnd - fadeStart);

                byte rootA = rootNode != null ? rootNode->AtkResNode.Alpha_2 : (byte)255;
                float effectiveAlpha = distanceFade * (rootA / 255f);
                if (effectiveAlpha <= 0.01f) continue; 
                
                // Position approximative de la nameplate en monde ; l'occlusion par géométrie 3D
                // sera testée au pixel final de l'icône par raycast dans DrawIconOnNameplate.
                var headWorld = new Vector3(gameObject->Position.X, gameObject->Position.Y + 2.2f, gameObject->Position.Z);

                uint iconId = 0;
                var goId = gameObject->GetGameObjectId();
                if (goId == localGoId && !string.IsNullOrEmpty(ownUid))
                {
                    var profile = _profileManager.GetUmbraProfile(new UserData(ownUid));
                    iconId = profile.ProfileIconId;
                }
                else if (pairsByGoId.TryGetValue(goId, out var pairUd))
                {
                    var profile = _profileManager.GetUmbraProfile(pairUd);
                    iconId = profile.ProfileIconId;
                }

                if (iconId == 0) continue;

                var textBlockHeight = Math.Abs((int)np.TextH);
                var textBlockWidth = Math.Abs((int)np.TextW);
                DrawIconOnNameplate(nameContainer, nameText, textBlockWidth, textBlockHeight, iconId, effectiveAlpha,
                    headWorld, useViewportOffset, vp.Pos, drawList);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error in nameplate overlay draw");
        }
        finally
        {
            _engine.EndFrame();
        }
    }

private void DrawIconOnNameplate(AtkResNode* nameContainer, AtkTextNode* nameText, int textBlockWidthRaw, int textBlockHeightRaw, uint iconId, float alpha, Vector3 nameplateWorld, bool useViewportOffset, Vector2 vpPos, ImDrawListPtr drawList)
    {
        ISharedImmediateTexture? textureWrap;
        try
        {
            textureWrap = _textureProvider.GetFromGameIcon(new GameIconLookup(iconId));
        }
        catch
        {
            return;
        }
        var wrap = textureWrap.GetWrapOrEmpty();
        if (wrap == null || wrap.Handle == IntPtr.Zero) return;

        var scaleX = GetWorldScaleX(nameContainer);
        var scaleY = GetWorldScaleY(nameContainer);
        if (scaleX <= 0f) scaleX = 1f;
        if (scaleY <= 0f) scaleY = 1f;

        var lineHeight = (nameText->FontSize > 0 ? nameText->FontSize : 14f) * scaleY;
        var containerHeight = nameContainer->Height * scaleY;
        var blockHeight = (textBlockHeightRaw > 0 ? textBlockHeightRaw : lineHeight / scaleY) * scaleY;
        var blockTop = MathF.Max(0f, containerHeight - blockHeight);

        var containerWidth = nameContainer->Width * scaleX;
        var textWidth = textBlockWidthRaw > 0 ? textBlockWidthRaw * scaleX : containerWidth;
        var containerCenterX = nameContainer->ScreenX + containerWidth / 2f;
        var textRightX = containerCenterX + textWidth / 2f;

        var iconSize = lineHeight * 0.9f;
        var aspect = wrap.Height > 0 ? (float)wrap.Width / wrap.Height : 1f;
        var drawSize = new Vector2(iconSize * aspect, iconSize);

        // Position : à droite du bloc de texte, centrée verticalement sur la ligne du nom principal.
        var nameLineCenterY = nameContainer->ScreenY + blockTop + lineHeight * 0.25f;
        var drawPos = new Vector2(
            textRightX + 4f * scaleX,
            nameLineCenterY - drawSize.Y / 2f);

        if (useViewportOffset) drawPos += vpPos;

        // Occlusion 3D pixel-perfect : raycast caméra → pixel de l'icône, avec distance max
        // jusqu'à la nameplate. Si un hit est trouvé avant, un objet 3D est devant → skip.
        var iconCenter = new Vector2(drawPos.X + drawSize.X * 0.5f, drawPos.Y + drawSize.Y * 0.5f);
        var iconRect = new Vector4(drawPos.X, drawPos.Y, drawPos.X + drawSize.X, drawPos.Y + drawSize.Y);
        if (_engine.IsOccluded(nameplateWorld, iconCenter, iconRect))
            return;

        var tint = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, Math.Clamp(alpha, 0f, 1f)));
        drawList.AddImage(wrap.Handle, drawPos, drawPos + drawSize, Vector2.Zero, Vector2.One, tint);
    }

private static float GetWorldScaleX(AtkResNode* node)
    {
        var t = node->Transform;
        return MathF.Sqrt(t.M11 * t.M11 + t.M12 * t.M12);
    }

    private static float GetWorldScaleY(AtkResNode* node)
    {
        var t = node->Transform;
        return MathF.Sqrt(t.M21 * t.M21 + t.M22 * t.M22);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _drawSubscribed)
        {
            _pluginInterface.UiBuilder.Draw -= OnDraw;
            _drawSubscribed = false;
        }
        base.Dispose(disposing);
    }
}
