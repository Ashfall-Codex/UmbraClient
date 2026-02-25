using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;

namespace UmbraSync.UI.Components;

public static partial class BbCodeRenderer
{
    private enum TextAlignment { Left, Center, Right, Justify }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct SpanStyle(Vector4 Color, bool Bold, bool Italic, bool Underline);

    private readonly record struct StyledWord(string Text, SpanStyle Style, float Width, bool IsLineBreak);

    private static readonly Dictionary<string, Vector4> ColorMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"]        = new(0.90f, 0.20f, 0.20f, 1f),
        ["orange"]     = new(1.00f, 0.60f, 0.20f, 1f),
        ["yellow"]     = new(1.00f, 0.95f, 0.30f, 1f),
        ["gold"]       = new(0.85f, 0.75f, 0.20f, 1f),
        ["green"]      = new(0.20f, 0.80f, 0.20f, 1f),
        ["lightgreen"] = new(0.60f, 1.00f, 0.60f, 1f),
        ["lightblue"]  = new(0.50f, 0.80f, 1.00f, 1f),
        ["darkblue"]   = new(0.20f, 0.40f, 0.90f, 1f),
        ["blue"]       = new(0.30f, 0.50f, 1.00f, 1f),
        ["pink"]       = new(1.00f, 0.50f, 0.80f, 1f),
        ["purple"]     = new(0.70f, 0.40f, 0.90f, 1f),
        ["white"]      = new(1.00f, 1.00f, 1.00f, 1f),
        ["grey"]       = new(0.70f, 0.70f, 0.70f, 1f),
        ["gray"]       = new(0.70f, 0.70f, 0.70f, 1f),
    };

    [GeneratedRegex(@"\[(?<close>/)?(?<tag>color|b|i|u|center|right|left|justify|glow)(?:=(?<value>[^\]]*))?\]",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TagRegex();
    
    public static string StripTags(string bbcode)
        => string.IsNullOrEmpty(bbcode) ? string.Empty : TagRegex().Replace(bbcode, string.Empty);

    public static void Render(string bbcode, float wrapWidth)
    {
        if (string.IsNullOrEmpty(bbcode)) return;

        var drawList = ImGui.GetWindowDrawList();
        var defaultColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var blocks = Parse(bbcode, defaultColor);

        foreach (var block in blocks)
        {
            var words = SplitIntoWords(block.Spans);
            var lines = WrapIntoLines(words, wrapWidth);

            if (lines.Count == 0)
            {
                ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeightWithSpacing()));
                continue;
            }

            RenderLines(lines, wrapWidth, block.Alignment, drawList);
        }
    }

    #region Parsing

    #pragma warning disable S1172 // defaultColor is used by local function CurrentStyle()
    private static List<(TextAlignment Alignment, List<(string Text, SpanStyle Style)> Spans)>
        Parse(string input, Vector4 defaultColor)
    #pragma warning restore S1172
    {
        var blocks = new List<(TextAlignment, List<(string, SpanStyle)>)>();
        var currentAlignment = TextAlignment.Left;
        var currentSpans = new List<(string, SpanStyle)>();

        var colorStack = new Stack<Vector4>();
        var boldDepth = 0;
        var italicDepth = 0;
        var underlineDepth = 0;

        SpanStyle CurrentStyle() => new(
            colorStack.Count > 0 ? colorStack.Peek() : defaultColor,
            boldDepth > 0,
            italicDepth > 0,
            underlineDepth > 0);

        var matches = TagRegex().Matches(input);
        int lastIndex = 0;

        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
                currentSpans.Add((input[lastIndex..match.Index], CurrentStyle()));

            lastIndex = match.Index + match.Length;

            var isClose = match.Groups["close"].Success;
            var tag = match.Groups["tag"].Value.ToLowerInvariant();
            var value = match.Groups["value"].Value;

            switch (tag)
            {
                case "center" or "right" or "left" or "justify":
                    if (currentSpans.Count > 0)
                    {
                        blocks.Add((currentAlignment, currentSpans));
                        currentSpans = [];
                    }
                    currentAlignment = isClose ? TextAlignment.Left : tag switch
                    {
                        "center"  => TextAlignment.Center,
                        "right"   => TextAlignment.Right,
                        "justify" => TextAlignment.Justify,
                        _         => TextAlignment.Left
                    };
                    break;

                case "color":
                    if (!isClose)
                    {
                        if (TryParseColor(value, out var c))
                            colorStack.Push(c);
                    }
                    else if (colorStack.Count > 0)
                        colorStack.Pop();
                    break;

                case "b":
                    if (!isClose) boldDepth++;
                    else if (boldDepth > 0) boldDepth--;
                    break;

                case "i":
                    if (!isClose) italicDepth++;
                    else if (italicDepth > 0) italicDepth--;
                    break;

                case "u":
                    if (!isClose) underlineDepth++;
                    else if (underlineDepth > 0) underlineDepth--;
                    break;

                // [glow] is intentionally ignored
            }
        }

        if (lastIndex < input.Length)
            currentSpans.Add((input[lastIndex..], CurrentStyle()));

        if (currentSpans.Count > 0)
            blocks.Add((currentAlignment, currentSpans));

        return blocks;
    }

    private static bool TryParseColor(string value, out Vector4 color)
    {
        if (ColorMap.TryGetValue(value.Trim(), out color))
            return true;

        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            color = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
            return true;
        }

        color = default;
        return false;
    }

    #endregion

    #region Word splitting & line wrapping

    private static List<StyledWord> SplitIntoWords(List<(string Text, SpanStyle Style)> spans)
    {
        var words = new List<StyledWord>();

        foreach (var (text, style) in spans)
        {
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '\r') { i++; continue; }

                if (text[i] == '\n')
                {
                    words.Add(new StyledWord("", style, 0, true));
                    i++;
                    continue;
                }

                if (char.IsWhiteSpace(text[i])) { i++; continue; }

                int start = i;
                while (i < text.Length && text[i] != '\n' && text[i] != '\r' && !char.IsWhiteSpace(text[i]))
                    i++;

                var word = text[start..i];
                words.Add(new StyledWord(word, style, ImGui.CalcTextSize(word).X, false));
            }
        }

        return words;
    }

    private static List<List<StyledWord>> WrapIntoLines(List<StyledWord> words, float wrapWidth)
    {
        var lines = new List<List<StyledWord>>();
        var line = new List<StyledWord>();
        float lineWidth = 0;
        float spaceW = ImGui.CalcTextSize(" ").X;

        foreach (var w in words)
        {
            if (w.IsLineBreak)
            {
                lines.Add(line);
                line = [];
                lineWidth = 0;
                continue;
            }

            float needed = line.Count > 0 ? spaceW + w.Width : w.Width;

            if (lineWidth + needed > wrapWidth && line.Count > 0)
            {
                lines.Add(line);
                line = [w];
                lineWidth = w.Width;
            }
            else
            {
                line.Add(w);
                lineWidth += needed;
            }
        }

        if (line.Count > 0)
            lines.Add(line);

        return lines;
    }

    #endregion

    #region Rendering

    private static void RenderLines(
        List<List<StyledWord>> lines, float wrapWidth, TextAlignment alignment, ImDrawListPtr drawList)
    {
        var origin = ImGui.GetCursorScreenPos();
        float fontSize = ImGui.GetFontSize();
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        float spaceW = ImGui.CalcTextSize(" ").X;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Count == 0) continue;

            bool isLast = i == lines.Count - 1;
            float y = origin.Y + i * lineHeight;

            float totalWordW = 0;
            foreach (var w in line) totalWordW += w.Width;

            float startX;
            float gap;

            if (alignment == TextAlignment.Justify && !isLast && line.Count > 1)
            {
                startX = origin.X;
                gap = (wrapWidth - totalWordW) / (line.Count - 1);
            }
            else
            {
                float contentW = totalWordW + (line.Count - 1) * spaceW;
                startX = alignment switch
                {
                    TextAlignment.Center => origin.X + (wrapWidth - contentW) * 0.5f,
                    TextAlignment.Right  => origin.X + wrapWidth - contentW,
                    _                    => origin.X
                };
                gap = spaceW;
            }

            float x = startX;
            foreach (var w in line)
            {
                var pos = new Vector2(x, y);
                uint col = ImGui.ColorConvertFloat4ToU32(w.Style.Color);

                if (w.Style.Italic)
                    DrawSheared(drawList, pos, col, w.Text, fontSize, w.Style.Bold);
                else
                    drawList.AddText(pos, col, w.Text);

                if (w.Style.Bold && !w.Style.Italic)
                    drawList.AddText(pos + new Vector2(1, 0), col, w.Text);

                if (w.Style.Underline)
                    drawList.AddLine(
                        new Vector2(x, y + fontSize + 1),
                        new Vector2(x + w.Width, y + fontSize + 1),
                        col);

                x += w.Width + gap;
            }
        }

        ImGui.Dummy(new Vector2(wrapWidth, lines.Count * ImGui.GetTextLineHeightWithSpacing()));
    }

    private const float ItalicShear = -0.20f;

    private static unsafe void DrawSheared(ImDrawListPtr drawList, Vector2 pos, uint col, string text, float fontSize, bool bold)
    {
        var vtxBefore = drawList.VtxBuffer.Size;
        drawList.AddText(pos, col, text);
        if (bold)
            drawList.AddText(pos + new Vector2(1, 0), col, text);
        var vtxAfter = drawList.VtxBuffer.Size;

        var baselineY = pos.Y + fontSize;
        var vtxPtr = drawList.VtxBuffer.Data;
        for (int v = vtxBefore; v < vtxAfter; v++)
        {
            vtxPtr[v].Pos.X += ItalicShear * (baselineY - vtxPtr[v].Pos.Y);
        }
    }

    #endregion
}
