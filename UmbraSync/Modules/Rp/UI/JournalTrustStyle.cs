using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using System.Numerics;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.UI;

namespace UmbraSync.Modules.Rp.UI;

public static class JournalTrustStyle
{
    public static readonly JournalTrust[] Levels =
    [
        JournalTrust.Unknown, JournalTrust.Hostile, JournalTrust.Wary,
        JournalTrust.Neutral, JournalTrust.Trusting, JournalTrust.Devoted,
    ];

    public static Vector4 Color(JournalTrust trust) => trust switch
    {
        JournalTrust.Hostile => ImGuiColors.DalamudRed,
        JournalTrust.Wary => ImGuiColors.DalamudOrange,
        JournalTrust.Neutral => ImGuiColors.DalamudGrey,
        JournalTrust.Trusting => ImGuiColors.HealerGreen,
        JournalTrust.Devoted => UiSharedService.AccentColor,
        _ => ImGuiColors.DalamudGrey3,
    };

    public static string Label(JournalTrust trust) => Loc.Get("Journal.Trust." + trust);

    public static string Tooltip(JournalTrust trust) => Loc.Get("Journal.Trust." + trust + ".Tooltip");

    public static void DrawDot(JournalTrust trust, float radius = 4.5f)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float r = radius * scale;
        float lineHeight = ImGui.GetTextLineHeight();
        var pos = ImGui.GetCursorScreenPos();
        var center = new Vector2(pos.X + r, pos.Y + lineHeight / 2f);
        var drawList = ImGui.GetWindowDrawList();
        if (trust == JournalTrust.Unknown)
            drawList.AddCircle(center, r, ImGui.GetColorU32(Color(trust)), 16, 1.5f * scale);
        else
            drawList.AddCircleFilled(center, r, ImGui.GetColorU32(Color(trust)), 16);
        ImGui.Dummy(new Vector2(r * 2f, lineHeight));
    }
}
