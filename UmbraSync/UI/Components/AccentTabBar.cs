using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace UmbraSync.UI.Components;

internal static class AccentTabBar
{
    private const float ButtonHeight = 32f;
    private const float ButtonSpacing = 8f;
    private const float Rounding = 4f;
    private const float IconTextGap = 6f;

    private static Vector4 BorderColor => UiSharedService.ThemeButtonActive;
    private static Vector4 BackgroundColor => UiSharedService.WithAlpha(UiSharedService.ThemeWindowBg, 0.9f);
    private static Vector4 HoverColor => UiSharedService.ThemeButtonBg;

    /// <param name="id">Préfixe des identifiants ImGui, propre à la fenêtre appelante.</param>
    /// <param name="activeTab">Onglet courant, mis à jour lorsqu'un autre est cliqué.</param>
    public static void Draw(string id, string[] labels, FontAwesomeIcon[] icons, Vector4 accent, ref int activeTab)
    {
        var dl = ImGui.GetWindowDrawList();
        var availWidth = ImGui.GetContentRegionAvail().X;
        var btnW = (availWidth - ButtonSpacing * (labels.Length - 1)) / labels.Length;

        for (int i = 0; i < labels.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, ButtonSpacing);

            var p = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton($"##{id}Tab_{i}", new Vector2(btnW, ButtonHeight));
            bool hovered = ImGui.IsItemHovered();
            bool isActive = activeTab == i;

            var bg = isActive ? accent : hovered ? HoverColor : BackgroundColor;
            dl.AddRectFilled(p, p + new Vector2(btnW, ButtonHeight), ImGui.GetColorU32(bg), Rounding);
            if (!isActive)
                dl.AddRect(p, p + new Vector2(btnW, ButtonHeight), ImGui.GetColorU32(BorderColor with { W = hovered ? 0.9f : 0.5f }), Rounding);

            ImGui.PushFont(UiBuilder.IconFont);
            var iconStr = icons[i].ToIconString();
            var iconSz = ImGui.CalcTextSize(iconStr);
            ImGui.PopFont();

            var labelSz = ImGui.CalcTextSize(labels[i]);
            var totalW = iconSz.X + IconTextGap + labelSz.X;
            var startX = p.X + (btnW - totalW) / 2f;

            var textColor = isActive ? UiSharedService.ThemeNavTextActive : hovered ? UiSharedService.ThemeNavTextHovered : UiSharedService.ThemeNavText;
            var textColorU32 = ImGui.GetColorU32(textColor);

            ImGui.PushFont(UiBuilder.IconFont);
            dl.AddText(new Vector2(startX, p.Y + (ButtonHeight - iconSz.Y) / 2f), textColorU32, iconStr);
            ImGui.PopFont();

            dl.AddText(new Vector2(startX + iconSz.X + IconTextGap, p.Y + (ButtonHeight - labelSz.Y) / 2f), textColorU32, labels[i]);

            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked) activeTab = i;
        }

        ImGuiHelpers.ScaledDummy(4f);
    }
}
