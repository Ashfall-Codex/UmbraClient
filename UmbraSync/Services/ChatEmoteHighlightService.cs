using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

public class ChatEmoteHighlightService : DisposableMediatorSubscriberBase
{
    private readonly IChatGui _chatGui;
    private readonly MareConfigService _configService;
    private readonly IDalamudPluginInterface _pluginInterface;

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

    private const string GroupEmote = "emote";
    private const string GroupHrp = "hrp";

    public ChatEmoteHighlightService(ILogger<ChatEmoteHighlightService> logger, MareMediator mediator,
        IChatGui chatGui, MareConfigService configService, IDalamudPluginInterface pluginInterface)
        : base(logger, mediator)
    {
        _chatGui = chatGui;
        _configService = configService;
        _pluginInterface = pluginInterface;

        _chatGui.ChatMessage += OnChatMessage;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _chatGui.ChatMessage -= OnChatMessage;
    }

    private Regex? BuildPattern()
    {
        var config = _configService.Current;
        var emoteParts = new List<string>(3);

        if (config.EmoteHighlightAsterisks)
            emoteParts.Add(@"\*.+?\*");
        if (config.EmoteHighlightAngleBrackets)
            emoteParts.Add(@"<.+?>");
        if (config.EmoteHighlightSquareBrackets)
            emoteParts.Add(@"\[.+?\]");

        var allParts = new List<string>(2);

        if (emoteParts.Count > 0)
            allParts.Add($"(?<{GroupEmote}>{string.Join('|', emoteParts)})");
        if (config.EmoteHighlightParenthesesGray)
        {
            var hrpParts = new List<string>(2);
            if (config.EmoteHighlightDoubleParentheses)
                hrpParts.Add(@"\(\(.+?\)\)");
            hrpParts.Add(@"\(.+?\)");
            allParts.Add($@"(?<{GroupHrp}>{string.Join('|', hrpParts)})");
        }

        if (allParts.Count == 0)
            return null;

        return new Regex(string.Join('|', allParts), RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (isHandled || !_configService.Current.EmoteHighlightEnabled)
            return;

        if (!RpChatTypes.Contains(type))
            return;

        var pattern = BuildPattern();
        if (pattern == null)
            return;

        var emoteColorKey = _configService.Current.EmoteHighlightColorKey;
        var hrpColorKey = _configService.Current.EmoteHighlightParenthesesColorKey;
        var chatTwoActive = PluginWatcherService.GetInitialPluginState(_pluginInterface, "ChatTwo")?.IsLoaded == true;
        var newPayloads = new List<Payload>();
        var modified = false;

        foreach (var payload in message.Payloads)
        {
            if (payload is not TextPayload textPayload || string.IsNullOrEmpty(textPayload.Text))
            {
                newPayloads.Add(payload);
                continue;
            }

            var text = textPayload.Text;
            var matches = pattern.Matches(text);

            if (matches.Count == 0)
            {
                newPayloads.Add(payload);
                continue;
            }

            modified = true;
            var lastIndex = 0;

            foreach (Match match in matches)
            {
                if (match.Index > lastIndex)
                    newPayloads.Add(new TextPayload(text[lastIndex..match.Index]));

                var isHrp = match.Groups[GroupHrp].Success;
                var colorKey = isHrp ? hrpColorKey : emoteColorKey;
                var useItalic = isHrp && _configService.Current.EmoteHighlightParenthesesItalic && !chatTwoActive;

                newPayloads.Add(new UIForegroundPayload(colorKey));
                if (useItalic)
                    newPayloads.Add(new EmphasisItalicPayload(true));
                newPayloads.Add(new TextPayload(match.Value));
                if (useItalic)
                    newPayloads.Add(new EmphasisItalicPayload(false));
                newPayloads.Add(UIForegroundPayload.UIForegroundOff);

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
                newPayloads.Add(new TextPayload(text[lastIndex..]));
        }

        if (modified)
            message = new SeString(newPayloads);
    }
}
