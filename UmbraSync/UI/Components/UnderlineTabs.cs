using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace UmbraSync.UI.Components;


internal static class UnderlineTabs
{
    public static void Draw(string id, string[] labels, FontAwesomeIcon[] icons, ref int active)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        float tabWidth = width / labels.Length;
        float underline = 2f * scale;
        float height = ImGui.GetFrameHeight() + (6f * scale);
        float gap = 5f * scale;
        float inset = 6f * scale;

        drawList.AddLine(new Vector2(origin.X, origin.Y + height - (0.5f * scale)),
            new Vector2(origin.X + width, origin.Y + height - (0.5f * scale)),
            ImGui.GetColorU32(UiSharedService.ThemeSeparator), scale);

        for (int i = 0; i < labels.Length; i++)
        {
            using var tabId = ImRaii.PushId(i);
            var min = origin + new Vector2(i * tabWidth, 0f);
            ImGui.SetCursorScreenPos(min);
            bool clicked = ImGui.InvisibleButton($"##{id}", new Vector2(tabWidth, height));
            bool hovered = ImGui.IsItemHovered();
            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked) active = i;
            bool isActive = active == i;

            var textColor = ImGui.GetColorU32(isActive ? UiSharedService.AccentColor
                : hovered ? UiSharedService.ThemeNavTextHovered : UiSharedService.ThemeNavText);
            string icon = icons[i].ToIconString();
            Vector2 iconSize;
            using (ImRaii.PushFont(UiBuilder.IconFont))
                iconSize = ImGui.CalcTextSize(icon);
            var labelSize = ImGui.CalcTextSize(labels[i]);
            float x = min.X + ((tabWidth - (iconSize.X + gap + labelSize.X)) / 2f);
            float contentHeight = height - underline;

            using (ImRaii.PushFont(UiBuilder.IconFont))
                drawList.AddText(new Vector2(x, min.Y + ((contentHeight - iconSize.Y) / 2f)), textColor, icon);
            drawList.AddText(new Vector2(x + iconSize.X + gap, min.Y + ((contentHeight - labelSize.Y) / 2f)), textColor, labels[i]);

            if (isActive || hovered)
            {
                var lineColor = isActive ? UiSharedService.AccentColor : UiSharedService.ThemeBorder;
                drawList.AddRectFilled(new Vector2(min.X + inset, min.Y + contentHeight),
                    new Vector2(min.X + tabWidth - inset, min.Y + height), ImGui.GetColorU32(lineColor), underline);
            }
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
    }
}
