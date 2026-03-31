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

        // Séparer le texte des payloads étrangers.
        // On enregistre chaque payload non-texte avec sa position en caractères dans le texte plat.
        var flatBuilder = new StringBuilder();
        var foreignPayloads = new List<(int flatPos, Payload payload)>();
        foreach (var payload in message.Payloads)
        {
            if (payload is TextPayload tp && tp.Text != null)
            {
                flatBuilder.Append(tp.Text);
            }
            else
            {
                foreignPayloads.Add((flatBuilder.Length, payload));
            }
        }
        var flatText = flatBuilder.ToString();

        var matches = pattern.Matches(flatText);
        if (matches.Count == 0)
            return;

        // Construire la liste des régions colorées sur le texte plat.
        var regions = new List<(int start, int end, HighlightKind kind)>(matches.Count);
        foreach (Match m in matches)
        {
            var kind = m.Groups[GroupQuotes].Success ? HighlightKind.Quotes
                     : m.Groups[GroupHrp].Success ? HighlightKind.Hrp
                     : HighlightKind.Emote;
            regions.Add((m.Index, m.Index + m.Length, kind));
        }

        // Reconstruire les payloads texte + nos couleurs à partir du texte plat,
        // puis réinsérer les payloads étrangers à leurs positions d'origine.
        var newPayloads = new List<Payload>();
        var foreignIdx = 0;
        var regionIdx = 0;
        var pos = 0;

        while (pos < flatText.Length)
        {
            // Insérer les payloads étrangers qui tombent à cette position
            while (foreignIdx < foreignPayloads.Count && foreignPayloads[foreignIdx].flatPos <= pos)
            {
                newPayloads.Add(foreignPayloads[foreignIdx].payload);
                foreignIdx++;
            }

            if (regionIdx < regions.Count && pos >= regions[regionIdx].start)
            {
                // On est dans une région colorée
                var region = regions[regionIdx];
                var activeColorKey = region.kind switch
                {
                    HighlightKind.Hrp => hrpColorKey,
                    HighlightKind.Quotes => quotesColorKey,
                    _ => emoteColorKey,
                };
                var activeItalic = region.kind == HighlightKind.Hrp
                    && _configService.Current.EmoteHighlightParenthesesItalic
                    && !chatTwoActive;

                newPayloads.Add(new UIForegroundPayload(activeColorKey));
                if (activeItalic)
                    newPayloads.Add(new EmphasisItalicPayload(true));

                // Émettre le texte de la région, en insérant les payloads étrangers au passage
                var regionEnd = region.end;
                while (pos < regionEnd && pos < flatText.Length)
                {
                    // Chercher le prochain payload étranger dans cette région
                    var nextForeignPos = (foreignIdx < foreignPayloads.Count && foreignPayloads[foreignIdx].flatPos < regionEnd)
                        ? foreignPayloads[foreignIdx].flatPos
                        : regionEnd;
                    nextForeignPos = Math.Min(nextForeignPos, flatText.Length);

                    if (nextForeignPos > pos)
                    {
                        var segEnd = Math.Min(nextForeignPos, regionEnd);
                        newPayloads.Add(new TextPayload(flatText[pos..segEnd]));
                        pos = segEnd;
                    }

                    // Insérer tous les payloads étrangers à cette position
                    while (foreignIdx < foreignPayloads.Count && foreignPayloads[foreignIdx].flatPos <= pos && pos < regionEnd)
                    {
                        newPayloads.Add(foreignPayloads[foreignIdx].payload);
                        foreignIdx++;
                    }
                }

                if (activeItalic)
                    newPayloads.Add(new EmphasisItalicPayload(false));
                newPayloads.Add(UIForegroundPayload.UIForegroundOff);
                regionIdx++;
            }
            else
            {
                // Texte hors région — émettre jusqu'au début de la prochaine région ou fin du texte
                var nextRegionStart = regionIdx < regions.Count ? regions[regionIdx].start : flatText.Length;
                while (pos < nextRegionStart)
                {
                    var nextForeignPos = (foreignIdx < foreignPayloads.Count && foreignPayloads[foreignIdx].flatPos < nextRegionStart)
                        ? foreignPayloads[foreignIdx].flatPos
                        : nextRegionStart;
                    nextForeignPos = Math.Min(nextForeignPos, flatText.Length);

                    if (nextForeignPos > pos)
                    {
                        var segEnd = Math.Min(nextForeignPos, nextRegionStart);
                        newPayloads.Add(new TextPayload(flatText[pos..segEnd]));
                        pos = segEnd;
                    }

                    while (foreignIdx < foreignPayloads.Count && foreignPayloads[foreignIdx].flatPos <= pos && pos < nextRegionStart)
                    {
                        newPayloads.Add(foreignPayloads[foreignIdx].payload);
                        foreignIdx++;
                    }
                }
            }
        }

        // Payloads étrangers restants (après tout le texte)
        while (foreignIdx < foreignPayloads.Count)
        {
            newPayloads.Add(foreignPayloads[foreignIdx].payload);
            foreignIdx++;
        }

        message = new SeString(newPayloads);
    }

    private enum HighlightKind { Emote, Hrp, Quotes }
}
