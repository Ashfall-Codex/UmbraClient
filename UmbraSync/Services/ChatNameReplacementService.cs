using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

public class ChatNameReplacementService : DisposableMediatorSubscriberBase
{
    private readonly IChatGui _chatGui;
    private readonly PairManager _pairManager;
    private readonly UmbraProfileManager _umbraProfileManager;
    private readonly MareConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ApiController _apiController;

    private bool _isInDuty;

    private static readonly HashSet<XivChatType> RpChatTypes =
    [
        XivChatType.Say,
        XivChatType.Yell,
        XivChatType.Shout,
        XivChatType.Party,
        XivChatType.CrossParty,
        XivChatType.Alliance,
        XivChatType.FreeCompany,
        XivChatType.CustomEmote,
        XivChatType.StandardEmote,
        XivChatType.TellIncoming,
        XivChatType.TellOutgoing,
        XivChatType.Ls1,
        XivChatType.Ls2,
        XivChatType.Ls3,
        XivChatType.Ls4,
        XivChatType.Ls5,
        XivChatType.Ls6,
        XivChatType.Ls7,
        XivChatType.Ls8,
        XivChatType.CrossLinkShell1,
        XivChatType.CrossLinkShell2,
        XivChatType.CrossLinkShell3,
        XivChatType.CrossLinkShell4,
        XivChatType.CrossLinkShell5,
        XivChatType.CrossLinkShell6,
        XivChatType.CrossLinkShell7,
        XivChatType.CrossLinkShell8,
    ];

    public ChatNameReplacementService(ILogger<ChatNameReplacementService> logger, MareMediator mediator,
        IChatGui chatGui, PairManager pairManager,
        UmbraProfileManager umbraProfileManager,
        MareConfigService configService, DalamudUtilService dalamudUtil, ApiController apiController)
        : base(logger, mediator)
    {
        _chatGui = chatGui;
        _pairManager = pairManager;
        _umbraProfileManager = umbraProfileManager;
        _configService = configService;
        _dalamudUtil = dalamudUtil;
        _apiController = apiController;

        _chatGui.ChatMessage += OnChatMessage;

        Mediator.Subscribe<InstanceOrDutyStartMessage>(this, (_) => _isInDuty = true);
        Mediator.Subscribe<InstanceOrDutyEndMessage>(this, (_) => _isInDuty = false);

        // Pré-charger le profil du joueur local dès la connexion
        Mediator.Subscribe<ConnectedMessage>(this, (_) => PreloadLocalProfile());
        PreloadLocalProfile();
    }

    private void PreloadLocalProfile()
    {
        if (!_apiController.IsConnected || string.IsNullOrEmpty(_apiController.UID))
            return;

        _ = _dalamudUtil.RunOnFrameworkThread(() =>
            _umbraProfileManager.GetUmbraProfile(new UserData(_apiController.UID)));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _chatGui.ChatMessage -= OnChatMessage;
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (isHandled || !_configService.Current.UseRpNamesInChat)
            return;

        if (_isInDuty && _configService.Current.DisableRpNamesInChatInDuty)
            return;

        if (!RpChatTypes.Contains(type))
            return;

        var senderText = ExtractSenderName(sender);
        if (string.IsNullOrEmpty(senderText))
            return;

        var (rpName, nameColor) = ResolveRpName(senderText);
        if (rpName == null)
            return;

        var effectiveColor = _configService.Current.UseRpNameColors
            && !(_isInDuty && _configService.Current.DisableRpNameColorsInDuty)
            ? nameColor : null;
        ReplaceSenderName(ref sender, senderText, rpName, effectiveColor);
    }

    private static string ExtractSenderName(SeString sender)
    {
        foreach (var payload in sender.Payloads)
        {
            if (payload is TextPayload textPayload && !string.IsNullOrWhiteSpace(textPayload.Text))
                return textPayload.Text.Trim();
        }
        return string.Empty;
    }

    private (string? rpName, string? nameColor) ResolveRpName(string senderName)
    {
        var localPlayerName = _dalamudUtil.GetPlayerName();
        if (!string.IsNullOrEmpty(localPlayerName) && NameMatches(senderName, localPlayerName)
            && _apiController.IsConnected && !string.IsNullOrEmpty(_apiController.UID))
        {
            var profile = _umbraProfileManager.GetUmbraProfile(new UserData(_apiController.UID));
            if (!string.IsNullOrEmpty(profile.RpFirstName) && !string.IsNullOrEmpty(profile.RpLastName)
                && IsRpFirstNameValid(localPlayerName, profile.RpFirstName))
                return (BuildRpDisplayName(profile), profile.RpNameColor);
        }

        foreach (var pair in _pairManager.GetOnlineUserPairs())
        {
            var playerName = pair.PlayerName;
            if (string.IsNullOrEmpty(playerName))
                continue;

            if (NameMatches(senderName, playerName))
            {
                var profile = _umbraProfileManager.GetUmbraProfile(pair.UserData);
                if (!string.IsNullOrEmpty(profile.RpFirstName) && !string.IsNullOrEmpty(profile.RpLastName)
                    && IsRpFirstNameValid(playerName, profile.RpFirstName))
                    return (BuildRpDisplayName(profile), profile.RpNameColor);
            }
        }

        return (null, null);
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

    private static bool NameMatches(string senderName, string fullName)
    {
        if (string.Equals(senderName, fullName, StringComparison.OrdinalIgnoreCase))
            return true;

        var atIndex = senderName.IndexOf('@');
        var nameWithoutWorld = atIndex >= 0 ? senderName[..atIndex] : senderName;

        if (string.Equals(nameWithoutWorld, fullName, StringComparison.OrdinalIgnoreCase))
            return true;

        var senderParts = nameWithoutWorld.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var fullParts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (senderParts.Length != 2 || fullParts.Length != 2)
            return false;

        return PartMatches(senderParts[0], fullParts[0]) && PartMatches(senderParts[1], fullParts[1]);
    }

    private static bool PartMatches(string part, string fullPart)
    {
        if (string.Equals(part, fullPart, StringComparison.OrdinalIgnoreCase))
            return true;

        if (part.Length == 2 && part[1] == '.' &&
            char.ToUpperInvariant(part[0]) == char.ToUpperInvariant(fullPart[0]))
            return true;

        return false;
    }

    private const byte ColorTypeForeground = 0x13;

    private static void ReplaceSenderName(ref SeString sender, string originalName, string rpName, string? nameColor)
    {
        var newPayloads = new List<Payload>(sender.Payloads.Count);
        var replaced = false;
        uint colorUint = 0;
        bool applyColor = !string.IsNullOrEmpty(nameColor);
        if (applyColor)
            colorUint = UI.UiSharedService.HexToUint(nameColor!);

        foreach (var payload in sender.Payloads)
        {
            if (!replaced && payload is TextPayload textPayload)
            {
                var text = textPayload.Text ?? string.Empty;
                var index = text.IndexOf(originalName, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    var before = text[..index];
                    var after = text[(index + originalName.Length)..];

                    if (!string.IsNullOrEmpty(before))
                        newPayloads.Add(new TextPayload(before));

                    if (applyColor && colorUint != 0)
                        newPayloads.Add(BuildColorStartPayload(ColorTypeForeground, colorUint));

                    newPayloads.Add(new TextPayload(rpName));

                    if (applyColor && colorUint != 0)
                        newPayloads.Add(BuildColorEndPayload(ColorTypeForeground));

                    if (!string.IsNullOrEmpty(after))
                        newPayloads.Add(new TextPayload(after));

                    replaced = true;
                    continue;
                }
            }
            newPayloads.Add(payload);
        }

        if (replaced)
            sender = new SeString(newPayloads);
    }

    private static RawPayload BuildColorStartPayload(byte colorType, uint color)
    {
        // SeString packed integer encoding (Lumina/Dalamud format)
        // Type byte = (0xF0 | presentByteMask) - 1
        // Data bytes written raw in MSB order: byte@24, byte@16(R), byte@8(G), byte@0(B)
        byte r = (byte)(color >> 16);
        byte g = (byte)(color >> 8);
        byte b = (byte)color;

        byte mask = 0;
        var data = new List<byte>(3);
        if (r != 0) { mask |= 0x04; data.Add(r); }
        if (g != 0) { mask |= 0x02; data.Add(g); }
        if (b != 0) { mask |= 0x01; data.Add(b); }

        var result = new byte[4 + data.Count];
        result[0] = 0x02;
        result[1] = colorType;
        result[2] = (byte)(data.Count + 2); // typeByte + data + end marker
        result[3] = (byte)((0xF0 | mask) - 1);
        for (var i = 0; i < data.Count; i++) result[4 + i] = data[i];
        result[^1] = 0x03;
        return new RawPayload(result);
    }

    private static RawPayload BuildColorEndPayload(byte colorType)
        => new([0x02, colorType, 0x02, 0xEC, 0x03]);
}
