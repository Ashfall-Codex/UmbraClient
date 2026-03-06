using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Numerics;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

public class ChatProximityBlendService : DisposableMediatorSubscriberBase
{
    private readonly IChatGui _chatGui;
    private readonly MareConfigService _configService;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly IObjectTable _objectTable;

    private bool _initialized;
    private bool _chatProximityActive;
    private CpConfig? _cpConfig;

    private static readonly HashSet<XivChatType> ProximityChatTypes =
    [
        XivChatType.Say,
        XivChatType.Yell,
        XivChatType.StandardEmote,
        XivChatType.CustomEmote,
    ];

    private static readonly Dictionary<string, float> DefaultRanges = new(StringComparer.Ordinal)
    {
        ["Say"] = 20f,
        ["Yell"] = 100f,
        ["StandardEmote"] = 20f,
        ["CustomEmote"] = 20f,
    };

    public ChatProximityBlendService(
        ILogger<ChatProximityBlendService> logger,
        MareMediator mediator,
        IChatGui chatGui,
        MareConfigService configService,
        IDalamudPluginInterface pluginInterface,
        DalamudUtilService dalamudUtil,
        IObjectTable objectTable)
        : base(logger, mediator)
    {
        _chatGui = chatGui;
        _configService = configService;
        _pluginInterface = pluginInterface;
        _dalamudUtil = dalamudUtil;
        _objectTable = objectTable;

        _chatGui.ChatMessage += OnChatMessage;

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, _ => InitOnce());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _chatGui.ChatMessage -= OnChatMessage;
    }

    private void InitOnce()
    {
        if (_initialized) return;
        _initialized = true;

        var cpState = PluginWatcherService.GetInitialPluginState(_pluginInterface, "ChatProximity");
        _chatProximityActive = cpState?.IsLoaded == true;

        if (!_chatProximityActive)
        {
            Logger.LogDebug("Chat Proximity not loaded, ChatProximityBlendService inactive");
            return;
        }

        LoadCpConfig();

        // Re-subscribe to ensure we run after Chat Proximity's handler
        _chatGui.ChatMessage -= OnChatMessage;
        _chatGui.ChatMessage += OnChatMessage;

        Logger.LogInformation("ChatProximityBlendService initialized (Chat Proximity detected)");
    }

    private void LoadCpConfig()
    {
        try
        {
            var configDir = _pluginInterface.ConfigDirectory.Parent;
            if (configDir == null) return;

            var configPath = Path.Combine(configDir.FullName, "ChatProximity.json");
            if (!File.Exists(configPath))
            {
                Logger.LogDebug("Chat Proximity config not found at {Path}", configPath);
                return;
            }

            var json = File.ReadAllText(configPath);
            _cpConfig = JsonConvert.DeserializeObject<CpConfig>(json);
            Logger.LogDebug("Loaded Chat Proximity config");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to read Chat Proximity config, using defaults");
        }
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (isHandled || !_chatProximityActive)
            return;

        if (!_configService.Current.EmoteHighlightEnabled)
            return;

        if (!ProximityChatTypes.Contains(type))
            return;

        // Identify the color keys used by ChatEmoteHighlightService
        var emoteColorKey = _configService.Current.EmoteHighlightColorKey;
        var hrpColorKey = _configService.Current.EmoteHighlightParenthesesColorKey;
        var quotesColorKey = ChatEmoteHighlightService.QuotesColorKey;

        // Quick check: does the message contain any UIForeground with our color keys?
        var hasOurColors = false;
        foreach (var payload in message.Payloads)
        {
            if (payload is UIForegroundPayload fgPayload
                && fgPayload.ColorKey != 0
                && (fgPayload.ColorKey == emoteColorKey || fgPayload.ColorKey == hrpColorKey || fgPayload.ColorKey == quotesColorKey))
            {
                hasOurColors = true;
                break;
            }
        }

        if (!hasOurColors)
            return;

        // Find sender in object table to calculate distance.
        // Use PlayerPayload (survives ChatNameReplacementService RP name swap)
        var senderName = ExtractPlayerName(sender);
        var senderObj = FindPlayerByName(senderName);
        if (senderObj == null)
            return;

        var localPlayer = _objectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        // Calculate fade factor from distance
        var distance = Vector3.Distance(localPlayer.Position, senderObj.Position);
        var channelRange = GetChannelRange(type);
        var ratio = Math.Clamp(distance / channelRange, 0f, 1f);
        var nearColor = GetChannelNearColor(type);
        var farColor = GetChannelFarColor(type);
        var fadedColor = Vector4.Lerp(nearColor, farColor, ratio);
        var fadeFactor = Math.Max(Math.Max(fadedColor.X, Math.Max(fadedColor.Y, fadedColor.Z)), 0.01f);
        var newPayloads = new List<Payload>(message.Payloads.Count);
        var modified = false;
        var insideBlend = false;

        foreach (var payload in message.Payloads)
        {
            if (payload is UIForegroundPayload fgPayload)
            {
                if (fgPayload.ColorKey != 0
                    && (fgPayload.ColorKey == emoteColorKey || fgPayload.ColorKey == hrpColorKey || fgPayload.ColorKey == quotesColorKey))
                {
                    var baseColor = ResolveColorKey(fgPayload.ColorKey);
                    var blended = new Vector4(
                        baseColor.X * fadeFactor,
                        baseColor.Y * fadeFactor,
                        baseColor.Z * fadeFactor,
                        baseColor.W);
                    newPayloads.Add(MakeColorPushPayload(blended));
                    insideBlend = true;
                    modified = true;
                }
                else if (fgPayload.ColorKey == 0 && insideBlend)
                {
                    newPayloads.Add(ColorPopPayload);
                    insideBlend = false;
                    modified = true;
                }
                else
                {
                    newPayloads.Add(payload);
                }
            }
            else
            {
                newPayloads.Add(payload);
            }
        }

        if (modified)
            message = new SeString(newPayloads);
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

    private float GetChannelRange(XivChatType type)
    {
        var key = type.ToString();
        if (_cpConfig?.ChatTypeConfigs != null
            && _cpConfig.ChatTypeConfigs.TryGetValue(key, out var channelCfg)
            && channelCfg.Enabled)
        {
            return channelCfg.Range > 0 ? channelCfg.Range : DefaultRanges.GetValueOrDefault(key, 20f);
        }
        return DefaultRanges.GetValueOrDefault(key, 20f);
    }

    private Vector4 GetChannelNearColor(XivChatType type)
    {
        var key = type.ToString();
        if (_cpConfig?.ChatTypeConfigs != null
            && _cpConfig.ChatTypeConfigs.TryGetValue(key, out var channelCfg)
            && channelCfg.NearColor != default)
        {
            return channelCfg.NearColor;
        }
        return Vector4.One;
    }

    private Vector4 GetChannelFarColor(XivChatType type)
    {
        var key = type.ToString();
        if (_cpConfig?.ChatTypeConfigs != null
            && _cpConfig.ChatTypeConfigs.TryGetValue(key, out var channelCfg)
            && channelCfg.FarColor != default)
        {
            return channelCfg.FarColor;
        }
        return new Vector4(0.4f, 0.4f, 0.4f, 1f);
    }

    private Vector4 ResolveColorKey(ushort colorKey)
    {
        if (_dalamudUtil.UiColors.Value.TryGetValue(colorKey, out var uiColor))
        {
            var rgba = uiColor.Dark;
            var r = ((rgba >> 24) & 0xFF) / 255.0f;
            var g = ((rgba >> 16) & 0xFF) / 255.0f;
            var b = ((rgba >> 8) & 0xFF) / 255.0f;
            var a = (rgba & 0xFF) / 255.0f;
            return new Vector4(r, g, b, a);
        }
        return new Vector4(1f, 0.65f, 0f, 1f);
    }
    
    private static RawPayload MakeColorPushPayload(Vector4 color)
    {
        var r = byte.Max((byte)(color.X * 255), (byte)0x01);
        var g = byte.Max((byte)(color.Y * 255), (byte)0x01);
        var b = byte.Max((byte)(color.Z * 255), (byte)0x01);
        return new RawPayload(unchecked([0x02, 0x13, 0x05, 0xF6, r, g, b, 0x03]));
    }
    
    private static readonly RawPayload ColorPopPayload = new([0x02, 0x13, 0x02, 0xEC, 0x03]);

    #region Chat Proximity Config Model

#pragma warning disable S3459, S1144 // Properties are set by Newtonsoft.Json deserialization
    private sealed class CpConfig
    {
        public Dictionary<string, CpChannelConfig>? ChatTypeConfigs { get; set; }
    }

    private sealed class CpChannelConfig
    {
        public bool Enabled { get; set; }
        public float Range { get; set; }
        public Vector4 NearColor { get; set; }
        public Vector4 FarColor { get; set; }
    }
#pragma warning restore S3459, S1144

    #endregion
}
