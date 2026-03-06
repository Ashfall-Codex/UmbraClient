using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using System.Text;
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
    private const string GroupQuotes = "quotes";

    /// <summary>Couleur fixe pour les guillemets (blanc = texte normal).</summary>
    public const ushort QuotesColorKey = 1;

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

        if (config.EmoteHighlightQuotes)
            allParts.Add($@"(?<{GroupQuotes}>"".+?"")");

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
        var quotesColorKey = QuotesColorKey;
        var chatTwoActive = PluginWatcherService.GetInitialPluginState(_pluginInterface, "ChatTwo")?.IsLoaded == true;

        // Concaténer tous les TextPayload.Text pour que le regex puisse détecter les patterns
        var flatBuilder = new StringBuilder();
        foreach (var payload in message.Payloads)
        {
            if (payload is TextPayload tp && tp.Text != null)
                flatBuilder.Append(tp.Text);
        }
        var flatText = flatBuilder.ToString();

        var matches = pattern.Matches(flatText);
        if (matches.Count == 0)
            return;

        // Construire la liste des régions (start, end exclusif, type de groupe)
        var regions = new List<(int start, int end, HighlightKind kind)>(matches.Count);
        foreach (Match m in matches)
        {
            var kind = m.Groups[GroupQuotes].Success ? HighlightKind.Quotes
                     : m.Groups[GroupHrp].Success ? HighlightKind.Hrp
                     : HighlightKind.Emote;
            regions.Add((m.Index, m.Index + m.Length, kind));
        }
        
        // Reconstruire le texte
        var newPayloads = new List<Payload>();
        var flatPos = 0;
        var regionIdx = 0;
        var colorOpen = false;
        var foreignColorOpen = false;
        ushort activeColorKey = 0;
        var activeItalic = false;

        foreach (var payload in message.Payloads)
        {
            if (payload is TextPayload textPayload && !string.IsNullOrEmpty(textPayload.Text))
            {
                var text = textPayload.Text;
                var localPos = 0;

                while (localPos < text.Length)
                {
                    if (colorOpen)
                    {
                        // Emettre le texte jusqu'à la fin de la région ou du payload
                        var region = regions[regionIdx];
                        var endInText = Math.Min(region.end - flatPos, text.Length);

                        if (endInText > localPos)
                            newPayloads.Add(new TextPayload(text[localPos..endInText]));

                        localPos = endInText;

                        if (flatPos + localPos >= region.end)
                        {
                            // Fermer
                            if (activeItalic)
                                newPayloads.Add(new EmphasisItalicPayload(false));
                            newPayloads.Add(UIForegroundPayload.UIForegroundOff);
                            colorOpen = false;
                            foreignColorOpen = false;
                            regionIdx++;
                        }
                    }
                    else
                    {
                        if (regionIdx < regions.Count && regions[regionIdx].start < flatPos + text.Length)
                        {
                            var region = regions[regionIdx];
                            var startInText = region.start - flatPos;

                            if (startInText > localPos)
                                newPayloads.Add(new TextPayload(text[localPos..startInText]));

                            activeColorKey = region.kind switch
                            {
                                HighlightKind.Hrp => hrpColorKey,
                                HighlightKind.Quotes => quotesColorKey,
                                _ => emoteColorKey,
                            };
                            activeItalic = region.kind == HighlightKind.Hrp && _configService.Current.EmoteHighlightParenthesesItalic && !chatTwoActive;

                            newPayloads.Add(new UIForegroundPayload(activeColorKey));
                            if (activeItalic)
                                newPayloads.Add(new EmphasisItalicPayload(true));
                            colorOpen = true;

                            localPos = startInText;
                        }
                        else
                        {
                            // R.A.S
                            if (localPos > 0)
                                newPayloads.Add(new TextPayload(text[localPos..]));
                            else
                                newPayloads.Add(payload);
                            localPos = text.Length;
                        }
                    }
                }

                flatPos += text.Length;
            }
            else if (payload is UIForegroundPayload ufp)
            {
                if (colorOpen)
                {
                    if (ufp.ColorKey != 0)
                    {
                        // Couleur étrangère, suspendre notre couleur
                        newPayloads.Add(UIForegroundPayload.UIForegroundOff);
                        newPayloads.Add(ufp);
                        foreignColorOpen = true;
                    }
                    else
                    {
                        //  restaurer notre couleur
                        if (foreignColorOpen)
                        {
                            newPayloads.Add(new UIForegroundPayload(activeColorKey));
                            foreignColorOpen = false;
                        }
                        else
                        {
                            newPayloads.Add(ufp);
                        }
                    }
                }
                else
                {
                    newPayloads.Add(ufp);
                }
            }
            else
            {
                newPayloads.Add(payload);
            }
        }

        message = new SeString(newPayloads);
    }

    private enum HighlightKind { Emote, Hrp, Quotes }
}
