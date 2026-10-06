using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace UmbraSync.UI.Components;

internal static class PopupMenu
{
    private const float RowHeight = 24f;
    private const float IconOffset = 8f;
    private const float LabelOffset = 34f;

    public static IDisposable PushStyle(float minWidth = 250f)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var scope = new StyleScope();
        scope.Add(ImRaii.PushColor(ImGuiCol.PopupBg, UiSharedService.WithAlpha(UiSharedService.ThemeWindowBg, 0.98f)));
        scope.Add(ImRaii.PushColor(ImGuiCol.Border, UiSharedService.AccentColor with { W = 0.7f }));
        scope.Add(ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(8f, 8f) * scale));
        scope.Add(ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 2f) * scale));
        scope.Add(ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, 10f * scale));
        scope.Add(ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize, 1.5f * scale));
        ImGui.SetNextWindowSizeConstraints(new Vector2(minWidth * scale, 0f), new Vector2(float.MaxValue, float.MaxValue));
        return scope;
    }

    public static bool Row(FontAwesomeIcon icon, string label, string id, Vector4? iconColor = null, Vector4? textColor = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float height = RowHeight * scale;
        float width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.Selectable($"##{id}", false, ImGuiSelectableFlags.None, new Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        DrawIconAndLabel(dl, pos, height, icon, label,
            iconColor ?? UiSharedService.AccentColor,
            textColor ?? (hovered ? Vector4.One : new Vector4(0.9f, 0.88f, 1f, 1f)));
        return clicked;
    }

    public static bool ToggleRow(FontAwesomeIcon icon, string label, bool enabled, string id)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float height = RowHeight * scale;
        float width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.Selectable($"##{id}", false, ImGuiSelectableFlags.None, new Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        DrawIconAndLabel(dl, pos, height, icon, label,
            enabled ? new Vector4(0.45f, 0.9f, 0.45f, 1f) : ImGuiColors.DalamudRed,
            hovered ? Vector4.One : new Vector4(0.9f, 0.88f, 1f, 1f));

        float switchWidth = 32f * scale;
        float switchHeight = 16f * scale;
        var switchMin = new Vector2(pos.X + width - switchWidth - 6f * scale, pos.Y + (height - switchHeight) / 2f);
        var switchMax = switchMin + new Vector2(switchWidth, switchHeight);
        dl.AddRectFilled(switchMin, switchMax,
            ImGui.GetColorU32(enabled ? new Vector4(0.3f, 0.7f, 0.35f, 0.9f) : new Vector4(0.45f, 0.45f, 0.5f, 0.6f)), switchHeight / 2f);
        float knobRadius = switchHeight / 2f - 2f * scale;
        float knobX = enabled ? switchMax.X - switchHeight / 2f : switchMin.X + switchHeight / 2f;
        dl.AddCircleFilled(new Vector2(knobX, switchMin.Y + switchHeight / 2f), knobRadius, ImGui.GetColorU32(Vector4.One));
        return clicked;
    }

    public static void Section(string title)
    {
        ImGuiHelpers.ScaledDummy(1f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(1f);
        UiSharedService.ColorText(title, ImGuiColors.DalamudGrey);
    }

    public static void Divider()
    {
        ImGuiHelpers.ScaledDummy(1f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(1f);
    }

    private static void DrawIconAndLabel(ImDrawListPtr dl, Vector2 pos, float height, FontAwesomeIcon icon, string label, Vector4 iconColor, Vector4 textColor)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var iconText = icon.ToIconString();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var iconSize = ImGui.CalcTextSize(iconText);
            dl.AddText(new Vector2(pos.X + IconOffset * scale, pos.Y + (height - iconSize.Y) / 2f), ImGui.GetColorU32(iconColor), iconText);
        }

        var labelSize = ImGui.CalcTextSize(label);
        dl.AddText(new Vector2(pos.X + LabelOffset * scale, pos.Y + (height - labelSize.Y) / 2f), ImGui.GetColorU32(textColor), label);
    }

    private sealed class StyleScope : IDisposable
    {
        private readonly List<IDisposable> _scopes = [];

        public void Add(IDisposable scope) => _scopes.Add(scope);

        public void Dispose()
        {
            for (int i = _scopes.Count - 1; i >= 0; i--)
                _scopes[i].Dispose();
        }
    }
}
