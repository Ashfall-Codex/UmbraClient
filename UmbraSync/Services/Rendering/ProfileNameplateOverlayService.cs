using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.API.Data;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services.Rendering;

public sealed class ProfileNameplateOverlayService : DisposableMediatorSubscriberBase
{
    private readonly PairManager _pairManager;
    private readonly UmbraProfileManager _profileManager;
    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly ITextureProvider _textureProvider;
    private readonly PictomancyService _pictomancyService;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ILogger<ProfileNameplateOverlayService> _logger;

    private bool _drawSubscribed;

    public ProfileNameplateOverlayService(
        ILogger<ProfileNameplateOverlayService> logger,
        MareMediator mediator,
        PairManager pairManager,
        UmbraProfileManager profileManager,
        IGameGui gameGui,
        IObjectTable objectTable,
        IClientState clientState,
        ITextureProvider textureProvider,
        PictomancyService pictomancyService,
        IDalamudPluginInterface pluginInterface)
        : base(logger, mediator)
    {
        _logger = logger;
        _pairManager = pairManager;
        _profileManager = profileManager;
        _gameGui = gameGui;
        _objectTable = objectTable;
        _clientState = clientState;
        _textureProvider = textureProvider;
        _pictomancyService = pictomancyService;
        _pluginInterface = pluginInterface;

        if (_pictomancyService.IsInitialized)
        {
            _pluginInterface.UiBuilder.Draw += OnDraw;
            _drawSubscribed = true;
        }
    }

    private int _selfLogCounter;

    private void OnDraw()
    {
        if (_clientState.LocalPlayer == null) return;
        if (_clientState.IsPvPExcludingDen) return;

        try
        {
            var vp = ImGui.GetMainViewport();
            var useViewportOffset = ImGui.GetIO().ConfigFlags.HasFlag(ImGuiConfigFlags.ViewportsEnable);
            var drawList = ImGui.GetBackgroundDrawList(vp);

            var visiblePairs = _pairManager.GetVisibleUsers();
            foreach (var userData in visiblePairs)
            {
                TryDrawPairIcon(userData, useViewportOffset, vp.Pos, drawList);
            }

            TryDrawSelfIcon(useViewportOffset, vp.Pos, drawList);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error in nameplate overlay draw");
        }
    }

    private void TryDrawPairIcon(UserData userData, bool useViewportOffset, Vector2 vpPos, ImDrawListPtr drawList)
    {
        var pair = _pairManager.GetPairByUID(userData.UID);
        if (pair == null || !pair.IsVisible) return;

        var profile = _profileManager.GetUmbraProfile(userData);
        if (profile.ProfileIconId == 0) return;

        var actor = FindActorForPair(pair);
        if (actor == null) return;

        DrawIconAboveActor(actor, profile.ProfileIconId, useViewportOffset, vpPos, drawList);
    }

    private void TryDrawSelfIcon(bool useViewportOffset, Vector2 vpPos, ImDrawListPtr drawList)
    {
        var uid = _profileManager.CurrentUid;
        if (string.IsNullOrEmpty(uid)) return;

        var actor = _clientState.LocalPlayer;
        if (actor == null) return;

        var profile = _profileManager.GetUmbraProfile(new UserData(uid));
        if (_selfLogCounter++ % 180 == 0)
        {
            _logger.LogInformation("SelfNameplate: uid={uid}, iconId={iconId}", uid, profile.ProfileIconId);
        }
        if (profile.ProfileIconId == 0) return;

        DrawIconAboveActor(actor, profile.ProfileIconId, useViewportOffset, vpPos, drawList);
    }

    private void DrawIconAboveActor(IGameObject actor, uint iconId, bool useViewportOffset, Vector2 vpPos, ImDrawListPtr drawList)
    {
        var headOffset = new Vector3(0f, 2.1f, 0f);
        var worldPos = actor.Position + headOffset;
        if (!_gameGui.WorldToScreen(worldPos, out var screenPos)) return;

        if (useViewportOffset) screenPos += vpPos;

        ISharedImmediateTexture? textureWrap;
        try
        {
            textureWrap = _textureProvider.GetFromGameIcon(new GameIconLookup(iconId));
        }
        catch
        {
            return;
        }
        var wrap = textureWrap?.GetWrapOrEmpty();
        if (wrap == null || wrap.Handle == IntPtr.Zero) return;

        var iconSize = 32f;
        var aspect = wrap.Height > 0 ? (float)wrap.Width / wrap.Height : 1f;
        var drawSize = new Vector2(iconSize * aspect, iconSize);
        var drawPos = new Vector2(screenPos.X - drawSize.X / 2f, screenPos.Y - drawSize.Y - 30f);

        drawList.AddImage(wrap.Handle, drawPos, drawPos + drawSize);
    }

    private IPlayerCharacter? FindActorForPair(Pair pair)
    {
        var ident = pair.Ident;
        if (string.IsNullOrEmpty(ident)) return null;

        foreach (var obj in _objectTable)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (pc.Address == IntPtr.Zero) continue;
            if (string.Equals(pair.PlayerName, pc.Name.TextValue, StringComparison.Ordinal))
                return pc;
        }
        return null;
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
