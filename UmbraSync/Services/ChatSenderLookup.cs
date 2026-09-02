using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace UmbraSync.Services;
internal static class ChatSenderLookup
{
    public static string ExtractPlayerName(SeString sender, string? fallback = null)
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

        return fallback ?? sender.TextValue;
    }

    public static IGameObject? FindPlayerByName(IObjectTable objectTable, string name)
    {
        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind == ObjectKind.Pc
                && string.Equals(obj.Name.TextValue, name, StringComparison.OrdinalIgnoreCase))
                return obj;
        }

        return null;
    }
}
