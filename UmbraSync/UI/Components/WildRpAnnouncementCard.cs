using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Dto.WildRp;
using UmbraSync.Localization;
using UmbraSync.Services;

namespace UmbraSync.UI.Components;

internal static class WildRpAnnouncementCard
{
    public static void Draw(WildRpAnnouncementDto announcement, UiSharedService uiShared, DalamudUtilService dalamudUtil)
    {
        ImGui.PushID(announcement.Id.ToString());

        var worldName = dalamudUtil.WorldData.Value.TryGetValue((ushort)announcement.WorldId, out string? wn) ? wn : announcement.WorldId.ToString(CultureInfo.InvariantCulture);
        var territoryName = dalamudUtil.TerritoryData.Value.TryGetValue(announcement.TerritoryId, out string? tn) ? tn : announcement.TerritoryId.ToString(CultureInfo.InvariantCulture);
        var wardSuffix = announcement.WardId is > 0 ? $" - {string.Format(CultureInfo.CurrentCulture, Loc.Get("WildRp.Ward"), announcement.WardId)}" : string.Empty;

        var hasRpProfile = !string.IsNullOrWhiteSpace(announcement.RpFirstName);
        string displayName;
        if (hasRpProfile)
        {
            var parts = new[] { announcement.RpTitle, announcement.RpFirstName, announcement.RpLastName };
            displayName = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
        else
        {
            displayName = announcement.CharacterName ?? announcement.UserAlias ?? announcement.UserUID;
        }

        var elapsed = DateTime.UtcNow - announcement.CreatedAtUtc;
        var elapsedStr = elapsed.TotalMinutes < 1 ? "< 1 min"
            : elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h{elapsed.Minutes:D2}"
            : $"{(int)elapsed.TotalMinutes} min";

        UiSharedService.DrawCard($"wildrp_{announcement.Id}", () =>
        {
            float bigTextHeight;
            using (uiShared.UidFont.Push())
                bigTextHeight = ImGui.CalcTextSize(displayName).Y;
            float iconHeight;
            using (ImRaii.PushFont(UiBuilder.IconFont))
                iconHeight = ImGui.CalcTextSize(FontAwesomeIcon.Compass.ToIconString()).Y;
            var iconOffsetY = (bigTextHeight - iconHeight) / 2f;

            var cursorY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(cursorY + iconOffsetY);
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.Compass.ToIconString());

            ImGui.SameLine();
            ImGui.SetCursorPosY(cursorY);
            uiShared.BigText(displayName);

            if (announcement.RpLevel != 0)
            {
                ImGui.SameLine();
                DrawRpLevelInline(announcement.RpLevel);
            }

            var worldText = $"[{worldName}]";
            var worldWidth = ImGui.CalcTextSize(worldText).X;
            var rightPadRow1 = (ImGui.GetStyle().FramePadding.X + 4f * ImGuiHelpers.GlobalScale) * 2f;
            var availRow1 = ImGui.GetContentRegionAvail().X - rightPadRow1;
            if (availRow1 > worldWidth)
                ImGui.SameLine(ImGui.GetCursorPosX() + availRow1 - worldWidth);
            else
                ImGui.SameLine();
            ImGui.TextDisabled(worldText);

            // Row 2: territory + ward + message + elapsed time (right-aligned)
            ImGui.TextDisabled($"{territoryName}{wardSuffix}");

            if (!string.IsNullOrWhiteSpace(announcement.Message))
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"| \"{announcement.Message}\"");
            }

            float iconWidth;
            using (ImRaii.PushFont(UiBuilder.IconFont))
                iconWidth = ImGui.CalcTextSize(FontAwesomeIcon.Clock.ToIconString()).X;
            var timeWidth = ImGui.CalcTextSize(elapsedStr).X + iconWidth + ImGui.GetStyle().ItemSpacing.X;
            var rightPad = (ImGui.GetStyle().FramePadding.X + 4f * ImGuiHelpers.GlobalScale) * 2f;
            var availWidth = ImGui.GetContentRegionAvail().X - rightPad;
            if (availWidth > timeWidth)
            {
                ImGui.SameLine(ImGui.GetCursorPosX() + availWidth - timeWidth);
            }
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(ImGuiColors.DalamudGrey, FontAwesomeIcon.Clock.ToIconString());
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, elapsedStr);
        }, stretchWidth: true);

        ImGui.PopID();
    }

    private static void DrawRpLevelInline(byte level)
    {
        var (label, color, icon) = level switch
        {
            1 => (Loc.Get("UserProfile.RpLevel.Beginner"), new Vector4(0.55f, 0.85f, 0.55f, 1f), FontAwesomeIcon.Seedling),
            2 => (Loc.Get("UserProfile.RpLevel.Regular"), new Vector4(0.55f, 0.75f, 1f, 1f), FontAwesomeIcon.Tree),
            3 => (Loc.Get("UserProfile.RpLevel.Mentor"), new Vector4(1f, 0.75f, 0.4f, 1f), FontAwesomeIcon.Crown),
            _ => (string.Empty, ImGuiColors.DalamudGrey, FontAwesomeIcon.None),
        };
        if (string.IsNullOrEmpty(label)) return;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(color, icon.ToIconString());
        ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(color, label);
    }
}
