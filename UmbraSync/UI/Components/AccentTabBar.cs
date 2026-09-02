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

    private static readonly Vector4 BorderColor = new(0.29f, 0.21f, 0.41f, 0.7f);
    private static readonly Vector4 BackgroundColor = new(0.11f, 0.11f, 0.11f, 0.9f);
    private static readonly Vector4 HoverColor = new(0.17f, 0.13f, 0.22f, 1f);

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

            var textColor = isActive ? new Vector4(1f, 1f, 1f, 1f) : hovered ? new Vector4(0.9f, 0.85f, 1f, 1f) : new Vector4(0.7f, 0.65f, 0.8f, 1f);
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
