using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Numerics;
using UmbraSync.Localization;

namespace UmbraSync.UI.Components;

public static class ResizableTextArea
{
    private static readonly Dictionary<uint, float> Heights = [];

    public static bool Draw(string id, ref string text, int maxLength, float defaultHeight,
        float minHeight = 60f, float maxHeight = 600f)
    {
        var scale = ImGuiHelpers.GlobalScale;
        uint key = ImGui.GetID(id);
        if (!Heights.TryGetValue(key, out var height))
            height = defaultHeight;

        bool changed = ImGui.InputTextMultiline(id, ref text, maxLength, new Vector2(-1, height * scale));
        float width = ImGui.GetItemRectSize().X;

        float gripHeight = 7f * scale;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + scale);
        var gripMin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id + "_grip", new Vector2(width, gripHeight));
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();

        if (hovered || active)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
        if (hovered && !active)
            UiSharedService.AttachToolTip(Loc.Get("Ui.TextArea.ResizeHint"));
        if (active)
            height = Math.Clamp(height + ImGui.GetIO().MouseDelta.Y / scale, minHeight, maxHeight);
        Heights[key] = height;

        var color = hovered || active
            ? ImGui.GetColorU32(UiSharedService.AccentColor)
            : ImGui.GetColorU32(ImGuiCol.Separator);
        var center = gripMin + new Vector2(width / 2f, gripHeight / 2f);
        float halfLength = 18f * scale;
        var dl = ImGui.GetWindowDrawList();
        dl.AddLine(center - new Vector2(halfLength, 1.5f * scale), center + new Vector2(halfLength, -1.5f * scale), color, scale);
        dl.AddLine(center - new Vector2(halfLength, -1.5f * scale), center + new Vector2(halfLength, 1.5f * scale), color, scale);

        return changed;
    }
}
