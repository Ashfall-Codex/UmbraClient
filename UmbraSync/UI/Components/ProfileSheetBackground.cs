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

    public static SheetPalette? PaletteOf(ProfileCardStyle style)
        => ProfileBackgrounds.Find(style.Background) is { } background
            ? WithCustomColors(PaletteFor(background.Family), style)
            : null;

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

    public static SheetPalette PlainPalette(ProfileCardStyle style)
        => WithCustomColors(new SheetPalette(
            Veil: UiSharedService.ThemeCardBg,
            Accent: UiSharedService.AccentColor,
            Label: ImGuiColors.DalamudGrey,
            Value: ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.Text))), style);

    public static SheetPalette DrawPlain(ImDrawListPtr drawList, ProfileCardStyle style, Vector2 min, Vector2 max, float rounding)
    {
        var palette = PlainPalette(style);
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

    private sealed record HeadingLayout(
        ImDrawListPtr DrawList, Vector2 Start, Vector2 TextSize, float Scale, float TextX,
        float Left, float Right, float LeftEnd, float RightStart, float LineY, float Thickness, uint Solid, uint Faded);

    public static void DrawHeading(string title, SheetPalette palette, float width, ProfileHeadingStyle style = ProfileHeadingStyle.Diamonds)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var framed = style is ProfileHeadingStyle.Cartouche or ProfileHeadingStyle.Band;
        if (framed) ImGuiHelpers.ScaledDummy(FramePadding);
        var start = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(title);
        var gap = 8f * scale;
        var inset = width * 0.04f;
        var textX = style == ProfileHeadingStyle.Editorial
            ? start.X + inset + (10f * scale)
            : start.X + MathF.Max((width - textSize.X) * 0.5f, 0f);

        var layout = new HeadingLayout(
            ImGui.GetWindowDrawList(), start, textSize, scale, textX,
            Left: start.X + inset,
            Right: start.X + width - inset,
            LeftEnd: textX - gap,
            RightStart: textX + textSize.X + gap,
            LineY: MathF.Round(start.Y + (textSize.Y * 0.5f)),
            Thickness: MathF.Max(1f, scale),
            Solid: ImGui.GetColorU32(palette.Accent with { W = 0.8f }),
            Faded: ImGui.GetColorU32(palette.Accent with { W = 0f }));

        switch (style)
        {
            case ProfileHeadingStyle.DoubleRule: DrawDoubleRule(layout); break;
            case ProfileHeadingStyle.Dashes: DrawDashes(layout); break;
            case ProfileHeadingStyle.Milestones: DrawMilestones(layout); break;
            case ProfileHeadingStyle.Editorial: DrawEditorial(layout); break;
            case ProfileHeadingStyle.HollowDiamond: DrawDiamonds(layout, hollow: true); break;
            case ProfileHeadingStyle.Stars: DrawStars(layout); break;
            case ProfileHeadingStyle.Pearls: DrawPearls(layout); break;
            case ProfileHeadingStyle.Trefoil: DrawTrefoil(layout); break;
            case ProfileHeadingStyle.Chevrons: DrawChevrons(layout); break;
            case ProfileHeadingStyle.Scrolls: DrawScrolls(layout); break;
            case ProfileHeadingStyle.Waves: DrawWaves(layout); break;
            case ProfileHeadingStyle.Brackets: DrawBrackets(layout); break;
            case ProfileHeadingStyle.Parentheses: DrawParentheses(layout); break;
            case ProfileHeadingStyle.Cartouche: DrawCartouche(layout); break;
            case ProfileHeadingStyle.Band: DrawBand(layout, start.X + (width * 0.5f)); break;
            case ProfileHeadingStyle.Rule:
            case ProfileHeadingStyle.Underline: break;
            default: DrawDiamonds(layout, hollow: false); break;
        }

        ImGui.SetCursorScreenPos(new Vector2(textX, start.Y));
        ImGui.TextColored(palette.Accent, title);
        ImGui.SetCursorScreenPos(new Vector2(start.X, ImGui.GetCursorScreenPos().Y));

        switch (style)
        {
            case ProfileHeadingStyle.Rule:
            {
                var y = ImGui.GetCursorScreenPos().Y + (1f * scale);
                var center = start.X + (width * 0.5f);
                FadeIn(layout, layout.Left, center, y);
                FadeOut(layout, center, layout.Right, y);
                ImGuiHelpers.ScaledDummy(4f);
                break;
            }
            case ProfileHeadingStyle.Underline:
            {
                var y = ImGui.GetCursorScreenPos().Y + (1f * scale);
                var underline = MathF.Max(textSize.X * 0.6f, 24f * scale);
                var x0 = textX + ((textSize.X - underline) * 0.5f);
                layout.DrawList.AddRectFilled(new Vector2(x0, y), new Vector2(x0 + underline, y + MathF.Max(1f, 1.5f * scale)), layout.Solid);
                ImGuiHelpers.ScaledDummy(4f);
                break;
            }
        }

        if (framed) ImGuiHelpers.ScaledDummy(FramePadding);
        ImGuiHelpers.ScaledDummy(2f);
    }

    private const float FramePadding = 3f;

    public static float HeadingHeight(ProfileHeadingStyle style)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var height = ImGui.GetTextLineHeight() + (2f * scale);
        if (style is ProfileHeadingStyle.Cartouche or ProfileHeadingStyle.Band) height += FramePadding * 2f * scale;
        if (style is ProfileHeadingStyle.Rule or ProfileHeadingStyle.Underline) height += 4f * scale;
        return height;
    }

    private static void FadeIn(HeadingLayout l, float x0, float x1, float y)
    {
        if (x1 <= x0) return;
        l.DrawList.AddRectFilledMultiColor(new Vector2(x0, y), new Vector2(x1, y + l.Thickness), l.Faded, l.Solid, l.Solid, l.Faded);
    }

    private static void FadeOut(HeadingLayout l, float x0, float x1, float y)
    {
        if (x1 <= x0) return;
        l.DrawList.AddRectFilledMultiColor(new Vector2(x0, y), new Vector2(x1, y + l.Thickness), l.Solid, l.Faded, l.Faded, l.Solid);
    }

    private static void DrawDiamonds(HeadingLayout l, bool hollow)
    {
        var diamond = (hollow ? 5f : 3f) * l.Scale;
        var leftEnd = l.LeftEnd - (diamond * 2f);
        var rightStart = l.RightStart + (diamond * 2f);
        var centerY = l.LineY + (l.Thickness * 0.5f);

        if (leftEnd > l.Left)
        {
            FadeIn(l, l.Left, leftEnd, l.LineY);
            DrawDiamond(l.DrawList, new Vector2(leftEnd + diamond, centerY), diamond, l.Solid, hollow ? Stroke(l) : 0f);
        }
        if (l.Right > rightStart)
        {
            FadeOut(l, rightStart, l.Right, l.LineY);
            DrawDiamond(l.DrawList, new Vector2(rightStart - diamond, centerY), diamond, l.Solid, hollow ? Stroke(l) : 0f);
        }
    }

    private static float Stroke(HeadingLayout l) => MathF.Max(1f, 1.2f * l.Scale);

    private static void DrawDashes(HeadingLayout l)
    {
        var dash = 12f * l.Scale;
        var step = 18f * l.Scale;
        var accent = ImGui.ColorConvertU32ToFloat4(l.Solid);
        for (int i = 0; i < 4; i++)
        {
            var color = ImGui.GetColorU32(accent with { W = 0.9f - (i * 0.2f) });
            var leftX1 = l.LeftEnd - (i * step);
            var rightX0 = l.RightStart + (i * step);
            if (leftX1 - dash > l.Left)
                l.DrawList.AddRectFilled(new Vector2(leftX1 - dash, l.LineY), new Vector2(leftX1, l.LineY + l.Thickness), color);
            if (rightX0 + dash < l.Right)
                l.DrawList.AddRectFilled(new Vector2(rightX0, l.LineY), new Vector2(rightX0 + dash, l.LineY + l.Thickness), color);
        }
    }

    private static void DrawMilestones(HeadingLayout l)
    {
        var tick = 4.5f * l.Scale;
        var centerY = l.LineY + (l.Thickness * 0.5f);
        var leftEnd = l.LeftEnd - (2f * l.Scale);
        var rightStart = l.RightStart + (2f * l.Scale);
        if (leftEnd > l.Left)
        {
            l.DrawList.AddRectFilled(new Vector2(l.Left, l.LineY), new Vector2(leftEnd, l.LineY + l.Thickness), l.Solid);
            DrawTick(l, l.Left, centerY, tick);
            DrawTick(l, leftEnd, centerY, tick);
        }
        if (l.Right > rightStart)
        {
            l.DrawList.AddRectFilled(new Vector2(rightStart, l.LineY), new Vector2(l.Right, l.LineY + l.Thickness), l.Solid);
            DrawTick(l, rightStart, centerY, tick);
            DrawTick(l, l.Right, centerY, tick);
        }
    }

    private static void DrawTick(HeadingLayout l, float x, float centerY, float halfHeight)
        => l.DrawList.AddLine(new Vector2(x, centerY - halfHeight), new Vector2(x, centerY + halfHeight), l.Solid, MathF.Max(1f, 1.4f * l.Scale));

    private static void DrawTrefoil(HeadingLayout l)
    {
        var radius = 2.2f * l.Scale;
        var spread = 5f * l.Scale;
        var centerY = l.LineY + (l.Thickness * 0.5f);
        var leftEnd = l.LeftEnd - (18f * l.Scale);
        var rightStart = l.RightStart + (18f * l.Scale);
        if (leftEnd > l.Left)
        {
            FadeIn(l, l.Left, leftEnd, l.LineY);
            DrawTrefoilDots(l, l.LeftEnd - (8f * l.Scale), centerY, radius, spread);
        }
        if (l.Right > rightStart)
        {
            FadeOut(l, rightStart, l.Right, l.LineY);
            DrawTrefoilDots(l, l.RightStart + (8f * l.Scale), centerY, radius, spread);
        }
    }

    private static void DrawTrefoilDots(HeadingLayout l, float cx, float cy, float radius, float spread)
    {
        l.DrawList.AddCircleFilled(new Vector2(cx, cy - spread), radius, l.Solid);
        l.DrawList.AddCircleFilled(new Vector2(cx - spread, cy + (spread * 0.6f)), radius, l.Solid);
        l.DrawList.AddCircleFilled(new Vector2(cx + spread, cy + (spread * 0.6f)), radius, l.Solid);
    }

    private static void DrawChevrons(HeadingLayout l)
    {
        var half = 5f * l.Scale;
        var depth = 5f * l.Scale;
        var step = 8f * l.Scale;
        var centerY = l.LineY + (l.Thickness * 0.5f);
        var leftEnd = l.LeftEnd - (22f * l.Scale);
        var rightStart = l.RightStart + (22f * l.Scale);
        if (leftEnd > l.Left)
        {
            FadeIn(l, l.Left, leftEnd, l.LineY);
            DrawChevron(l, l.LeftEnd - (18f * l.Scale), centerY, half, depth);
            DrawChevron(l, l.LeftEnd - (18f * l.Scale) + step, centerY, half, depth);
        }
        if (l.Right > rightStart)
        {
            FadeOut(l, rightStart, l.Right, l.LineY);
            DrawChevron(l, l.RightStart + (18f * l.Scale), centerY, half, -depth);
            DrawChevron(l, l.RightStart + (18f * l.Scale) - step, centerY, half, -depth);
        }
    }

    private static void DrawChevron(HeadingLayout l, float x, float centerY, float half, float depth)
    {
        var tip = new Vector2(x + depth, centerY);
        var thickness = Stroke(l);
        l.DrawList.AddLine(new Vector2(x, centerY - half), tip, l.Solid, thickness);
        l.DrawList.AddLine(tip, new Vector2(x, centerY + half), l.Solid, thickness);
    }

    private static void DrawWaves(HeadingLayout l)
    {
        var amplitude = 2.5f * l.Scale;
        var wavelength = 16f * l.Scale;
        var centerY = l.LineY + (l.Thickness * 0.5f);
        DrawWave(l, l.Left, l.LeftEnd - (4f * l.Scale), centerY, amplitude, wavelength);
        DrawWave(l, l.RightStart + (4f * l.Scale), l.Right, centerY, amplitude, wavelength);
    }

    private static void DrawWave(HeadingLayout l, float x0, float x1, float centerY, float amplitude, float wavelength)
    {
        if (x1 - x0 < wavelength) return;
        var step = 2f * l.Scale;
        for (var x = x0; x <= x1; x += step)
            l.DrawList.PathLineTo(new Vector2(x, centerY - (amplitude * MathF.Sin((x - x0) / wavelength * MathF.Tau))));
        l.DrawList.PathStroke(l.Solid, ImDrawFlags.None, Stroke(l));
    }

    private static void DrawParentheses(HeadingLayout l)
    {
        var bulge = 7f * l.Scale;
        var top = new Vector2(0f, l.Start.Y + (1f * l.Scale));
        var bottom = new Vector2(0f, l.Start.Y + l.TextSize.Y - (1f * l.Scale));
        var midY = (top.Y + bottom.Y) * 0.5f;
        var leftEnd = l.LeftEnd - (14f * l.Scale);
        var rightStart = l.RightStart + (14f * l.Scale);
        var thickness = Stroke(l);

        if (leftEnd > l.Left)
        {
            FadeIn(l, l.Left, leftEnd, l.LineY);
            l.DrawList.AddBezierQuadratic(top with { X = l.LeftEnd }, new Vector2(l.LeftEnd - bulge, midY), bottom with { X = l.LeftEnd }, l.Solid, thickness);
        }
        if (l.Right > rightStart)
        {
            FadeOut(l, rightStart, l.Right, l.LineY);
            l.DrawList.AddBezierQuadratic(top with { X = l.RightStart }, new Vector2(l.RightStart + bulge, midY), bottom with { X = l.RightStart }, l.Solid, thickness);
        }
    }

    private static void DrawCartouche(HeadingLayout l)
    {
        var padX = 10f * l.Scale;
        var padY = FramePadding * l.Scale;
        var min = new Vector2(l.TextX - padX, l.Start.Y - padY);
        var max = new Vector2(l.TextX + l.TextSize.X + padX, l.Start.Y + l.TextSize.Y + padY);
        l.DrawList.AddRect(min, max, l.Solid, UiSharedService.RadiusControl * l.Scale, ImDrawFlags.None, Stroke(l));
        FadeIn(l, l.Left, min.X, l.LineY);
        FadeOut(l, max.X, l.Right, l.LineY);
    }

    private static void DrawBand(HeadingLayout l, float centerX)
    {
        var padY = FramePadding * l.Scale;
        var band = ImGui.GetColorU32(ImGui.ColorConvertU32ToFloat4(l.Solid) with { W = 0.22f });
        var top = l.Start.Y - padY;
        var bottom = l.Start.Y + l.TextSize.Y + padY;
        if (centerX > l.Left)
            l.DrawList.AddRectFilledMultiColor(new Vector2(l.Left, top), new Vector2(centerX, bottom), l.Faded, band, band, l.Faded);
        if (l.Right > centerX)
            l.DrawList.AddRectFilledMultiColor(new Vector2(centerX, top), new Vector2(l.Right, bottom), band, l.Faded, l.Faded, band);
    }

    private static void DrawDoubleRule(HeadingLayout l)
    {
        var offset = 3f * l.Scale;
        FadeIn(l, l.Left, l.LeftEnd, l.LineY - offset);
        FadeIn(l, l.Left, l.LeftEnd, l.LineY + offset - l.Thickness);
        FadeOut(l, l.RightStart, l.Right, l.LineY - offset);
        FadeOut(l, l.RightStart, l.Right, l.LineY + offset - l.Thickness);
    }

    private static void DrawPearls(HeadingLayout l)
    {
        ReadOnlySpan<float> radii = [3f, 2.4f, 1.9f, 1.4f, 1f];
        var step = 12f * l.Scale;
        var centerY = l.LineY + (l.Thickness * 0.5f);
        var accent = ImGui.ColorConvertU32ToFloat4(l.Solid);
        for (int i = 0; i < radii.Length; i++)
        {
            var radius = radii[i] * l.Scale;
            var color = ImGui.GetColorU32(accent with { W = 0.9f - (i * 0.12f) });
            var leftX = l.LeftEnd - radius - (i * step);
            var rightX = l.RightStart + radius + (i * step);
            if (leftX - radius > l.Left)
                l.DrawList.AddCircleFilled(new Vector2(leftX, centerY), radius, color);
            if (rightX + radius < l.Right)
                l.DrawList.AddCircleFilled(new Vector2(rightX, centerY), radius, color);
        }
    }

    private static void DrawScrolls(HeadingLayout l)
    {
        var reach = 14f * l.Scale;
        var centerY = l.LineY + (l.Thickness * 0.5f);
        var leftEnd = l.LeftEnd - reach;
        var rightStart = l.RightStart + reach;
        if (leftEnd > l.Left)
        {
            FadeIn(l, l.Left, leftEnd, l.LineY);
            DrawScroll(l, new Vector2(leftEnd, centerY), 1f);
        }
        if (l.Right > rightStart)
        {
            FadeOut(l, rightStart, l.Right, l.LineY);
            DrawScroll(l, new Vector2(rightStart, centerY), -1f);
        }
    }

    private static void DrawScroll(HeadingLayout l, Vector2 origin, float dir)
    {
        var s = l.Scale;
        var thickness = MathF.Max(1f, 1.2f * s);
        Vector2 At(Vector2 from, float dx, float dy) => from + new Vector2(dx * dir * s, dy * s);

        var p = origin;
        var q = At(p, 12f, -8f);
        l.DrawList.AddBezierCubic(p, At(p, 8f, 0f), At(p, 12f, -5f), q, l.Solid, thickness);
        var r = At(q, -6f, -2f);
        l.DrawList.AddBezierCubic(q, At(q, 0f, -3f), At(q, -4f, -4f), r, l.Solid, thickness);
        l.DrawList.AddBezierCubic(r, At(r, -2f, 3f), At(r, 1f, 7f), At(r, 8f, 7f), l.Solid, thickness);
    }

    private static void DrawStars(HeadingLayout l)
    {
        var radius = 6f * l.Scale;
        var centerY = l.LineY + (l.Thickness * 0.5f);
        var leftEnd = l.LeftEnd - (radius * 2f) - (4f * l.Scale);
        var rightStart = l.RightStart + (radius * 2f) + (4f * l.Scale);
        if (leftEnd > l.Left)
        {
            FadeIn(l, l.Left, leftEnd, l.LineY);
            DrawSparkle(l.DrawList, new Vector2(leftEnd + radius + (2f * l.Scale), centerY), radius, l.Solid);
        }
        if (l.Right > rightStart)
        {
            FadeOut(l, rightStart, l.Right, l.LineY);
            DrawSparkle(l.DrawList, new Vector2(rightStart - radius - (2f * l.Scale), centerY), radius, l.Solid);
        }
    }

    private static void DrawSparkle(ImDrawListPtr drawList, Vector2 center, float radius, uint color)
    {
        var waist = radius * 0.28f;
        drawList.AddQuadFilled(center + new Vector2(0f, -radius), center + new Vector2(waist, 0f),
            center + new Vector2(0f, radius), center + new Vector2(-waist, 0f), color);
        drawList.AddQuadFilled(center + new Vector2(-radius, 0f), center + new Vector2(0f, -waist),
            center + new Vector2(radius, 0f), center + new Vector2(0f, waist), color);
    }

    private static void DrawBrackets(HeadingLayout l)
    {
        var pad = 6f * l.Scale;
        var arm = 7f * l.Scale;
        var top = l.Start.Y - (1f * l.Scale);
        var bottom = l.Start.Y + l.TextSize.Y + (1f * l.Scale);
        var leg = l.TextSize.Y * 0.3f;
        var x0 = l.TextX - pad;
        var x1 = l.TextX + l.TextSize.X + pad;
        var thickness = MathF.Max(1f, 1.2f * l.Scale);

        DrawCorner(l.DrawList, new Vector2(x0, top), arm, leg, l.Solid, thickness);
        DrawCorner(l.DrawList, new Vector2(x0, bottom), arm, -leg, l.Solid, thickness);
        DrawCorner(l.DrawList, new Vector2(x1, top), -arm, leg, l.Solid, thickness);
        DrawCorner(l.DrawList, new Vector2(x1, bottom), -arm, -leg, l.Solid, thickness);
    }

    private static void DrawCorner(ImDrawListPtr drawList, Vector2 corner, float arm, float leg, uint color, float thickness)
    {
        drawList.AddLine(corner, corner + new Vector2(arm, 0f), color, thickness);
        drawList.AddLine(corner, corner + new Vector2(0f, leg), color, thickness);
    }

    private static void DrawEditorial(HeadingLayout l)
    {
        var barWidth = 2f * l.Scale;
        var barInset = 1f * l.Scale;
        l.DrawList.AddRectFilled(new Vector2(l.Left, l.Start.Y + barInset),
            new Vector2(l.Left + barWidth, l.Start.Y + l.TextSize.Y - barInset), l.Solid);
        FadeOut(l, l.RightStart, l.Right, l.LineY);
    }

    private static void DrawDiamond(ImDrawListPtr drawList, Vector2 center, float radius, uint color, float strokeThickness = 0f)
    {
        var top = center + new Vector2(0f, -radius);
        var right = center + new Vector2(radius, 0f);
        var bottom = center + new Vector2(0f, radius);
        var left = center + new Vector2(-radius, 0f);
        if (strokeThickness > 0f)
            drawList.AddQuad(top, right, bottom, left, color, strokeThickness);
        else
            drawList.AddQuadFilled(top, right, bottom, left, color);
    }
}
