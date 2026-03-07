using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Microsoft.Extensions.Logging;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

public class ChatTargetSoundService : DisposableMediatorSubscriberBase
{
    private readonly IChatGui _chatGui;
    private readonly MareConfigService _configService;
    private readonly IObjectTable _objectTable;
    private readonly ITargetManager _targetManager;
    private readonly PairManager _pairManager;

    private DateTime _lastSoundTime = DateTime.MinValue;
    private static readonly TimeSpan SoundCooldown = TimeSpan.FromSeconds(2);

    private static readonly HashSet<XivChatType> TargetSoundChatTypes =
    [
        XivChatType.Say,
        XivChatType.Yell,
        XivChatType.CustomEmote,
        XivChatType.StandardEmote,
    ];

    public ChatTargetSoundService(
        ILogger<ChatTargetSoundService> logger,
        MareMediator mediator,
        IChatGui chatGui,
        MareConfigService configService,
        IObjectTable objectTable,
        ITargetManager targetManager,
        PairManager pairManager)
        : base(logger, mediator)
    {
        _chatGui = chatGui;
        _configService = configService;
        _objectTable = objectTable;
        _targetManager = targetManager;
        _pairManager = pairManager;

        _chatGui.ChatMessage += OnChatMessage;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _chatGui.ChatMessage -= OnChatMessage;
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (isHandled || !_configService.Current.ChatTargetSoundMasterEnabled)
            return;

        if (!TargetSoundChatTypes.Contains(type))
            return;

        var localPlayer = _objectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        var senderName = ExtractPlayerName(sender);
        if (string.IsNullOrEmpty(senderName))
            return;

        // Ne pas jouer de son pour nos propres messages
        if (string.Equals(senderName, localPlayer.Name.TextValue, StringComparison.OrdinalIgnoreCase))
            return;

        var senderObj = FindPlayerByName(senderName);
        if (senderObj == null)
            return;

        var config = _configService.Current;

        // Vérifier les deux conditions de déclenchement
        var theyTargetMe = config.ChatTargetSoundEnabled
            && senderObj.TargetObjectId == localPlayer.GameObjectId;

        // Utiliser ITargetManager pour la cible actuelle (plus fiable, comme Snooper)
        var myTarget = _targetManager.Target;
        var iTargetThem = config.ChatTargetSoundReverseEnabled
            && myTarget != null
            && myTarget.ObjectKind == ObjectKind.Player
            && string.Equals(myTarget.Name.TextValue, senderName, StringComparison.OrdinalIgnoreCase);

        if (!theyTargetMe && !iTargetThem)
            return;

        var now = DateTime.UtcNow;
        if (now - _lastSoundTime < SoundCooldown)
            return;

        _lastSoundTime = now;

        // Résolution du son avec priorité : pair → group → global
        var resolvedSound = ResolveSoundIndex(senderObj.EntityId);
        if (resolvedSound == 0)
            return; // Son désactivé pour ce pair/groupe

        PlayChatSound((uint)resolvedSound);
    }
    
    private int ResolveSoundIndex(uint senderEntityId)
    {
        var config = _configService.Current;

        // Chercher le pair correspondant au sender via son EntityId
        var pair = FindPairByEntityId(senderEntityId);
        if (pair != null)
        {
            // Vérifier la surcharge pair
            if (config.ChatTargetSoundPairOverridesEnabled
                && config.PairTargetSoundOverrides.TryGetValue(pair.UserData.UID, out var pairSound))
                return pairSound == 0 ? 0 : Math.Clamp(pairSound, 1, 16);

            // Vérifier les surcharges groupe (premier trouvé)
            if (config.ChatTargetSoundGroupOverridesEnabled)
            {
                foreach (var group in pair.GroupPair.Keys)
                {
                    if (config.GroupTargetSoundOverrides.TryGetValue(group.GID, out var groupSound))
                        return groupSound == 0 ? 0 : Math.Clamp(groupSound, 1, 16);
                }
            }
        }

        // Fallback : son global
        return Math.Clamp(config.ChatTargetSoundIndex, 1, 16);
    }
    
    private Pair? FindPairByEntityId(uint entityId)
    {
        foreach (var pair in _pairManager.GetOnlineUserPairs())
        {
            if (pair.PlayerCharacterId == entityId)
                return pair;
        }
        return null;
    }

    private static string ExtractPlayerName(SeString sender)
    {
        foreach (var payload in sender.Payloads)
        {
            if (payload is PlayerPayload playerPayload && !string.IsNullOrEmpty(playerPayload.PlayerName))
                return playerPayload.PlayerName;
        }
        foreach (var payload in sender.Payloads)
        {
            if (payload is TextPayload textPayload && !string.IsNullOrWhiteSpace(textPayload.Text))
                return textPayload.Text.Trim();
        }
        return sender.TextValue;
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindPlayerByName(string name)
    {
        foreach (var obj in _objectTable)
        {
            if (obj.ObjectKind == ObjectKind.Player
                && string.Equals(obj.Name.TextValue, name, StringComparison.OrdinalIgnoreCase))
                return obj;
        }
        return null;
    }

    private static void PlayChatSound(uint soundIndex)
    {
        UIGlobals.PlayChatSoundEffect(soundIndex);
    }
}
