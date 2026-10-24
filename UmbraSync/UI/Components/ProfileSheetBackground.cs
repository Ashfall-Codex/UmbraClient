using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using System.Numerics;
using UmbraSync.Services;

namespace UmbraSync.UI.Components;

public sealed record SheetPalette(Vector4 Veil, Vector4 Accent, Vector4 Label, Vector4 Value);

public static class ProfileSheetBackground
{
    private const float VignetteOpacity = 0.75f;
    private const float VignetteClearRadius = 0.55f;
    public const float BorderOpacity = 0.55f;
    public const float SeparatorOpacity = 0.5f;

    public static SheetPalette PaletteFor(ProfileBackgroundFamily family) => family switch
    {
        ProfileBackgroundFamily.Wood => new SheetPalette(
            Veil: new Vector4(0.12f, 0.08f, 0.05f, 1f),
            Accent: new Vector4(0.89f, 0.73f, 0.42f, 1f),
            Label: new Vector4(0.80f, 0.70f, 0.55f, 1f),
            Value: new Vector4(0.97f, 0.93f, 0.85f, 1f)),
        ProfileBackgroundFamily.Illustrated => new SheetPalette(
            Veil: new Vector4(0.15f, 0.12f, 0.09f, 1f),
            Accent: new Vector4(0.88f, 0.76f, 0.52f, 1f),
            Label: new Vector4(0.82f, 0.75f, 0.62f, 1f),
            Value: new Vector4(0.97f, 0.94f, 0.87f, 1f)),
        _ => new SheetPalette(
            Veil: new Vector4(0.19f, 0.13f, 0.08f, 1f),
            Accent: new Vector4(0.87f, 0.69f, 0.42f, 1f),
            Label: new Vector4(0.84f, 0.75f, 0.60f, 1f),
            Value: new Vector4(0.98f, 0.94f, 0.86f, 1f)),
    };

    public static SheetPalette WithCustomColors(SheetPalette palette, ProfileCardStyle style) => palette with
    {
        Accent = ColorOr(style.SheetTitleColor, palette.Accent),
        Label = ColorOr(style.SheetLabelColor, palette.Label),
        Value = ColorOr(style.SheetTextColor, palette.Value),
    };

    private static Vector4 ColorOr(string? hex, Vector4 fallback) => CustomColor(hex) ?? fallback;

    public static Vector4? CustomColor(string? hex)
        => UiSharedService.TryHexToVector4(hex, out var color) ? color : null;

    public static bool TryGetTexture(ITextureProvider textures, int iconId, out IDalamudTextureWrap texture)
    {
        texture = null!;
        if (!ProfileBackgrounds.IsKnown(iconId)) return false;
        if (!textures.GetFromGameIcon(new GameIconLookup((uint)iconId)).TryGetWrap(out var wrap, out _) || wrap == null)
            return false;
        texture = wrap;
        return texture.Width > 0 && texture.Height > 0;
    }

    public static SheetPalette? Draw(ITextureProvider textures, ImDrawListPtr drawList, ProfileCardStyle style,
        Vector2 min, Vector2 max, float rounding)
    {
        var size = max - min;
        if (size.X <= 1f || size.Y <= 1f) return null;
        if (ProfileBackgrounds.Find(style.Background) is not { } background) return null;
        if (!TryGetTexture(textures, background.IconId, out var texture)) return null;

        var palette = WithCustomColors(PaletteFor(background.Family), style);
        var (uv0, uv1) = ProfileBanner.CoverUv(texture.Width, texture.Height, size.X, size.Y);
        var brightness = 1f - style.BackgroundDimming;
        drawList.AddImageRounded(texture.Handle, min, max, uv0, uv1, ImGui.GetColorU32(new Vector4(brightness, brightness, brightness, 1f)), rounding);
        if (style.BackgroundFade)
        {
            if (style.BackgroundVeil > 0f)
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(palette.Veil with { W = style.BackgroundVeil }), rounding);
            ProfileBanner.FadeEdges(drawList, min, size, palette.Veil with { W = VignetteOpacity }, rounding, VignetteClearRadius);
        }

        DrawBorder(drawList, palette, min, max, rounding);
        return palette;
    }

    public static SheetPalette DrawPlain(ImDrawListPtr drawList, ProfileCardStyle style, Vector2 min, Vector2 max, float rounding)
    {
        var palette = WithCustomColors(new SheetPalette(
            Veil: UiSharedService.ThemeCardBg,
            Accent: UiSharedService.AccentColor,
            Label: ImGuiColors.DalamudGrey,
            Value: ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.Text))), style);
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(palette.Veil), rounding);
        DrawBorder(drawList, palette, min, max, rounding);
        return palette;
    }

    private static void DrawBorder(ImDrawListPtr drawList, SheetPalette palette, Vector2 min, Vector2 max, float rounding)
    {
        var thickness = MathF.Max(1f, ImGui.GetStyle().FrameBorderSize * ImGuiHelpers.GlobalScale);
        var inset = new Vector2(thickness * 0.5f + 0.5f);
        drawList.AddRect(min + inset, max - inset, ImGui.GetColorU32(palette.Accent with { W = BorderOpacity }),
            rounding, ImDrawFlags.None, thickness);
    }

    public static void DrawHeading(string title, SheetPalette palette, float width)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(title);
        var gap = 8f * scale;
        var diamond = 3f * scale;
        var thickness = MathF.Max(1f, scale);
        var lineY = MathF.Round(start.Y + (textSize.Y * 0.5f));
        var textX = start.X + MathF.Max((width - textSize.X) * 0.5f, 0f);

        var solid = ImGui.GetColorU32(palette.Accent with { W = 0.8f });
        var faded = ImGui.GetColorU32(palette.Accent with { W = 0f });
        var leftEnd = textX - gap - (diamond * 2f);
        var rightStart = textX + textSize.X + gap + (diamond * 2f);
        var inset = width * 0.04f;

        if (leftEnd > start.X + inset)
        {
            drawList.AddRectFilledMultiColor(new Vector2(start.X + inset, lineY), new Vector2(leftEnd, lineY + thickness), faded, solid, solid, faded);
            DrawDiamond(drawList, new Vector2(leftEnd + diamond, lineY + (thickness * 0.5f)), diamond, solid);
        }
        if (start.X + width - inset > rightStart)
        {
            drawList.AddRectFilledMultiColor(new Vector2(rightStart, lineY), new Vector2(start.X + width - inset, lineY + thickness), solid, faded, faded, solid);
            DrawDiamond(drawList, new Vector2(rightStart - diamond, lineY + (thickness * 0.5f)), diamond, solid);
        }

        ImGui.SetCursorScreenPos(new Vector2(textX, start.Y));
        ImGui.TextColored(palette.Accent, title);
        ImGui.SetCursorScreenPos(new Vector2(start.X, ImGui.GetCursorScreenPos().Y));
        ImGuiHelpers.ScaledDummy(2f);
    }

    private static void DrawDiamond(ImDrawListPtr drawList, Vector2 center, float radius, uint color)
        => drawList.AddQuadFilled(center + new Vector2(0f, -radius), center + new Vector2(radius, 0f),
            center + new Vector2(0f, radius), center + new Vector2(-radius, 0f), color);
}
