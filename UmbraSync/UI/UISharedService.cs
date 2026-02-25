using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Models;
using UmbraSync.Localization;
using UmbraSync.Services.ServerConfiguration;

namespace UmbraSync.UI;

public partial class UiSharedService : DisposableMediatorSubscriberBase
{
    public const string TooltipSeparator = "--SEP--";
    public static string DoubleNewLine => Environment.NewLine + Environment.NewLine;

    public static readonly ImGuiWindowFlags PopupWindowFlags = ImGuiWindowFlags.NoResize |
                                               ImGuiWindowFlags.NoScrollbar |
                                           ImGuiWindowFlags.NoScrollWithMouse;

    public const float ContentFontScale = 0.92f;

    public static Vector4 AccentColor { get; set; } = new Vector4(0x96 / 255f, 0x45 / 255f, 0xE6 / 255f, 1f);
    public static Vector4 AccentHoverColor { get; set; } = new Vector4(0x38 / 255f, 0x29 / 255f, 0x52 / 255f, 1f);
    public static Vector4 AccentActiveColor { get; set; } = AccentHoverColor;

    // --- Thème UmbraSync "Royal Smoke" — #6A0DAD accent, #1C1C1C fond, chrome désaturé ---
    public static readonly Vector4 ThemeWindowBg      = new(0.11f, 0.11f, 0.11f, 1f);
    public static readonly Vector4 ThemeChildBg        = new(0f, 0f, 0f, 0f);
    public static readonly Vector4 ThemeBorder         = new(0x38 / 255f, 0x29 / 255f, 0x52 / 255f, 1f);
    public static readonly Vector4 ThemeTitleBar       = new(0x2A / 255f, 0x1F / 255f, 0x3D / 255f, 1f);
    public static readonly Vector4 ThemeSeparator      = new(0x38 / 255f, 0x29 / 255f, 0x52 / 255f, 0.60f);
    public static readonly Vector4 ThemeFrameBg        = new(0.16f, 0.15f, 0.18f, 1f);
    public static readonly Vector4 ThemeFrameBgHovered = new(0x38 / 255f, 0x29 / 255f, 0x52 / 255f, 0.7f);
    public static readonly Vector4 ThemeFrameBgActive  = new(0x4A / 255f, 0x36 / 255f, 0x68 / 255f, 1f);
    public static readonly Vector4 ThemeButtonBg       = new(0x2A / 255f, 0x1F / 255f, 0x3D / 255f, 1f);
    public static readonly Vector4 ThemeButtonHovered   = new(0x38 / 255f, 0x29 / 255f, 0x52 / 255f, 1f);
    public static readonly Vector4 ThemeButtonActive    = new(0x4A / 255f, 0x36 / 255f, 0x68 / 255f, 1f);
    public static readonly Vector4 ThemeHeaderBg       = new(0x1C / 255f, 0x1C / 255f, 0x1C / 255f, 1f);
    public static readonly Vector4 ThemeHeaderHovered   = new(0x38 / 255f, 0x29 / 255f, 0x52 / 255f, 0.7f);
    public static readonly Vector4 ThemeHeaderActive    = new(0x4A / 255f, 0x36 / 255f, 0x68 / 255f, 1f);
    public static readonly Vector4 ThemeScrollbarBg    = new(0.08f, 0.08f, 0.08f, 0.50f);
    public static readonly Vector4 ThemeScrollbarGrab  = new(0x38 / 255f, 0x29 / 255f, 0x52 / 255f, 1f);
    public static readonly Vector4 ThemeScrollbarHover = new(0x4A / 255f, 0x36 / 255f, 0x68 / 255f, 1f);
    public static readonly Vector4 ThemeScrollbarActive = new(0x5E / 255f, 0x45 / 255f, 0x80 / 255f, 1f);
    public static readonly Vector4 ThemeTabNormal      = new(0x1C / 255f, 0x1C / 255f, 0x1C / 255f, 0.90f);
    public static readonly Vector4 ThemeTabHovered     = new(0x38 / 255f, 0x29 / 255f, 0x52 / 255f, 1f);
    public static readonly Vector4 ThemeTabActive      = new(0x4A / 255f, 0x36 / 255f, 0x68 / 255f, 1f);
    public const int ThemeColorCount = 23;
    public const int ThemeStyleVarCount = 2;

    public readonly FileDialogManager FileDialogManager;

    private const string _notesEnd = "##MARE_SYNCHRONOS_USER_NOTES_END##";

    private const string _notesStart = "##MARE_SYNCHRONOS_USER_NOTES_START##";

    private readonly ApiController _apiController;

    private readonly CacheMonitor _cacheMonitor;

    private readonly MareConfigService _configService;

    private readonly DalamudUtilService _dalamudUtil;
    private readonly IpcManager _ipcManager;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ITextureProvider _textureProvider;
    public ITextureProvider TextureProvider => _textureProvider;
    private readonly Dictionary<string, object> _selectedComboItems = new(StringComparer.Ordinal);
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private bool _cacheDirectoryHasOtherFilesThanCache;
    private static readonly Stack<float> _fontScaleStack = new();
    private static float _currentWindowFontScale = 1f;

    private bool _cacheDirectoryIsValidPath = true;

    private bool _customizePlusExists;

    private string _customServerName = "";

    private string _customServerUri = "";

    private bool _glamourerExists;

    private bool _heelsExists;

    private bool _honorificExists;
    private bool _isDirectoryWritable;
    private bool _isOneDrive;
    private bool _isPenumbraDirectory;
    private bool _moodlesExists;
    private bool _penumbraExists;
    private bool _petNamesExists;
    private bool _brioExists;
    private bool _chatTwoExists;
    private bool _chatProximityExists;
    private bool _questRebornExists;
    public bool ChatTwoExists => _chatTwoExists;

    private int _serverSelectionIndex = -1;

    public UiSharedService(ILogger<UiSharedService> logger, IpcManager ipcManager, ApiController apiController,
        CacheMonitor cacheMonitor, FileDialogManager fileDialogManager,
        MareConfigService configService, DalamudUtilService dalamudUtil, IDalamudPluginInterface pluginInterface,
        ITextureProvider textureProvider,
        ServerConfigurationManager serverManager, MareMediator mediator) : base(logger, mediator)
    {
        _ipcManager = ipcManager;
        _apiController = apiController;
        _cacheMonitor = cacheMonitor;
        FileDialogManager = fileDialogManager;
        _configService = configService;
        _dalamudUtil = dalamudUtil;
        _pluginInterface = pluginInterface;
        _textureProvider = textureProvider;
        _serverConfigurationManager = serverManager;

        _isDirectoryWritable = IsDirectoryWritable(_configService.Current.CacheFolder);

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) =>
        {
            _penumbraExists = _ipcManager.Penumbra.APIAvailable;
            _glamourerExists = _ipcManager.Glamourer.APIAvailable;
            _customizePlusExists = _ipcManager.CustomizePlus.APIAvailable;
            _heelsExists = _ipcManager.Heels.APIAvailable;
            _honorificExists = _ipcManager.Honorific.APIAvailable;
            _petNamesExists = _ipcManager.PetNames.APIAvailable;
            _moodlesExists = _ipcManager.Moodles.APIAvailable;
            _brioExists = _ipcManager.Brio.APIAvailable;
            _chatTwoExists = Services.PluginWatcherService.GetInitialPluginState(_pluginInterface, "ChatTwo")?.IsLoaded ?? false;
            _chatProximityExists = Services.PluginWatcherService.GetInitialPluginState(_pluginInterface, "ChatProximity")?.IsLoaded ?? false;
            _questRebornExists = Services.PluginWatcherService.GetInitialPluginState(_pluginInterface, "AQuestReborn")?.IsLoaded ?? false;
        });

        UidFont = _pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
        {
            e.OnPreBuild(tk => tk.AddDalamudAssetFont(Dalamud.DalamudAsset.NotoSansJpMedium, new()
            {
                SizePx = 27,
                GlyphRanges = [
                    0x0020, 0x007E,
                    0x00A0, 0x017F,
                    0
                ]
            }));
        });
        GameFont = _pluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new(GameFontFamilyAndSize.Axis12));
        IconFont = _pluginInterface.UiBuilder.IconFontFixedWidthHandle;
    }

    public ApiController ApiController => _apiController;

    public bool EditTrackerPosition { get; set; }

    public IFontHandle GameFont { get; init; }
    public bool HasValidPenumbraModPath => !(_ipcManager.Penumbra.ModDirectory ?? string.Empty).IsNullOrEmpty() && Directory.Exists(_ipcManager.Penumbra.ModDirectory);

    public IFontHandle IconFont { get; init; }
    public bool IsInGpose => _dalamudUtil.IsInGpose;

    public string PlayerName => _dalamudUtil.GetPlayerName();

    public IFontHandle UidFont { get; init; }
    public MareConfigService ConfigService => _configService;
    public Dictionary<ushort, string> WorldData => _dalamudUtil.WorldData.Value;

    public uint WorldId => _dalamudUtil.GetHomeWorldId();

    public static void AttachToolTip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
            if (text.Contains(TooltipSeparator, StringComparison.Ordinal))
            {
                var splitText = text.Split(TooltipSeparator, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < splitText.Length; i++)
                {
                    ImGui.TextUnformatted(splitText[i]);
                    if (i != splitText.Length - 1) ImGui.Separator();
                }
            }
            else
            {
                ImGui.TextUnformatted(text);
            }
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    public void DrawMoodlesAtAGlance(string? moodlesJson, float iconHeight = 24f)
    {
        var moodles = MoodleStatusInfo.ParseMoodles(moodlesJson);
        if (moodles.Count == 0) return;

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var scaledHeight = iconHeight * ImGuiHelpers.GlobalScale;

        // Pre-load wraps and calculate display sizes respecting aspect ratio
        var items = new List<(MoodleStatusInfo moodle, ImTextureID handle, Vector2 size)>();
        float totalWidth = 0f;
        foreach (var moodle in moodles)
        {
            if (moodle.IconID <= 0) continue;
            var wrap = _textureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup((uint)moodle.IconID)).GetWrapOrEmpty();
            if (wrap.Handle == IntPtr.Zero) continue;
            var aspect = wrap.Height > 0 ? (float)wrap.Width / wrap.Height : 1f;
            var displaySize = new Vector2(scaledHeight * aspect, scaledHeight);
            items.Add((moodle, wrap.Handle, displaySize));
            totalWidth += displaySize.X;
        }
        if (items.Count == 0) return;

        totalWidth += (items.Count - 1) * spacing;
        var baseX = ImGui.GetCursorPosX();
        var startX = baseX + (availableWidth - totalWidth) / 2f;
        if (startX < baseX) startX = baseX;

        ImGui.SetCursorPosX(startX);

        for (int i = 0; i < items.Count; i++)
        {
            var (moodle, handle, size) = items[i];

            if (i > 0)
                ImGui.SameLine();

            ImGui.Image(handle, size);

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 20f);
                var title = moodle.CleanTitle;
                if (!string.IsNullOrEmpty(title))
                {
                    var typeColor = moodle.Type switch
                    {
                        0 => new Vector4(0.4f, 0.9f, 0.4f, 1f),
                        1 => new Vector4(0.9f, 0.4f, 0.4f, 1f),
                        _ => new Vector4(0.5f, 0.6f, 1f, 1f),
                    };
                    ImGui.TextColored(typeColor, title);
                }
                var desc = moodle.CleanDescription;
                if (!string.IsNullOrEmpty(desc))
                    ImGui.TextUnformatted(desc);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }
    }

    public static string ByteToString(long bytes, bool addSuffix = true)
    {
        _ = addSuffix;
        double dblSByte = bytes / 1048576.0;
        if (dblSByte > 0.0 && dblSByte < 0.01)
            dblSByte = 0.01;
        return $"{dblSByte:0.00} MiB";
    }

    public static string TrisToString(long tris)
    {
        return tris > 1000 ? $"{tris / 1000.0:0.0}k" : $"{tris}";
    }

    public static void CenterNextWindow(float width, float height, ImGuiCond cond = ImGuiCond.None)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(new Vector2(center.X - width / 2, center.Y - height / 2), cond);
    }

    public static uint Color(byte r, byte g, byte b, byte a)
    { uint ret = a; ret <<= 8; ret += b; ret <<= 8; ret += g; ret <<= 8; ret += r; return ret; }

    public static uint Color(Vector4 color)
    {
        uint ret = (byte)(color.W * 255);
        ret <<= 8;
        ret += (byte)(color.Z * 255);
        ret <<= 8;
        ret += (byte)(color.Y * 255);
        ret <<= 8;
        ret += (byte)(color.X * 255);
        return ret;
    }

    public static void ColorText(string text, Vector4 color)
    {
        using var raiicolor = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
    }

    public static void ColorTextWrapped(string text, Vector4 color, float wrapPos = 0)
    {
        using var raiicolor = ImRaii.PushColor(ImGuiCol.Text, color);
        TextWrapped(text, wrapPos);
    }

    public static Vector4 HexToVector4(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return AccentColor;
        var span = hex.AsSpan();
        if (span.Length > 0 && span[0] == '#') span = span[1..];
        if (span.Length != 6 || !uint.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return AccentColor;
        return new Vector4(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }

    public static string Vector4ToHex(Vector4 color)
    {
        var r = (byte)Math.Clamp(color.X * 255f, 0, 255);
        var g = (byte)Math.Clamp(color.Y * 255f, 0, 255);
        var b = (byte)Math.Clamp(color.Z * 255f, 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    public static uint HexToUint(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return 0;
        var span = hex.AsSpan();
        if (span.Length > 0 && span[0] == '#') span = span[1..];
        if (span.Length != 6 || !uint.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return 0;
        return rgb;
    }

    public static bool CtrlPressed() => (GetKeyState(0xA2) & 0x8000) != 0 || (GetKeyState(0xA3) & 0x8000) != 0;

    public static IDisposable PushFontScale(float scale)
    {
        var previous = _currentWindowFontScale;
        _fontScaleStack.Push(previous);
        if (Math.Abs(previous - scale) > float.Epsilon)
        {
            SetFontScale(scale);
        }

        return new FontScaleScope();
    }

    private sealed class FontScaleScope : IDisposable
    {
        public void Dispose()
        {
            if (_fontScaleStack.Count == 0) return;
            var previous = _fontScaleStack.Pop();
            if (Math.Abs(previous - _currentWindowFontScale) > float.Epsilon)
            {
                SetFontScale(previous);
            }
        }
    }

    public static void SetFontScale(float scale)
    {
        ImGui.SetWindowFontScale(scale);
        _currentWindowFontScale = scale;
    }

    public static void DrawGrouped(Action imguiDrawAction, float rounding = 5f, float? expectedWidth = null, bool drawBorder = true)
    {
        var cursorPos = ImGui.GetCursorPos();
        using (ImRaii.Group())
        {
            if (expectedWidth != null)
            {
                ImGui.Dummy(new(expectedWidth.Value, 0));
                ImGui.SetCursorPos(cursorPos);
            }

            imguiDrawAction.Invoke();
        }

        if (drawBorder)
        {
            ImGui.GetWindowDrawList().AddRect(
                ImGui.GetItemRectMin() - ImGui.GetStyle().ItemInnerSpacing,
                ImGui.GetItemRectMax() + ImGui.GetStyle().ItemInnerSpacing,
                Color(ImGuiColors.DalamudGrey2), rounding);
        }
    }

    public static void DrawCard(string id, Action draw, Vector2? padding = null, Vector4? background = null,
        Vector4? border = null, float? rounding = null, bool stretchWidth = false)
    {
        var style = ImGui.GetStyle();
        var padBase = style.FramePadding;
        var pad = padding ?? new Vector2(
            padBase.X + 4f * ImGuiHelpers.GlobalScale,
            padBase.Y + 3f * ImGuiHelpers.GlobalScale);
        var cardBg = background ?? new Vector4(0x1C / 255f, 0x1C / 255f, 0x1C / 255f, 1f);
        var cardBorder = border ?? new Vector4(0x4A / 255f, 0x36 / 255f, 0x68 / 255f, 0.70f);
        float cardRounding = rounding ?? Math.Max(style.FrameRounding, 8f * ImGuiHelpers.GlobalScale);
        float borderThickness = Math.Max(1f, Math.Max(style.FrameBorderSize, 1f) * ImGuiHelpers.GlobalScale);
        float borderInset = borderThickness;

        var originalCursor = ImGui.GetCursorPos();
        if (stretchWidth)
        {
            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMin().X);
        }

        var startCursor = ImGui.GetCursorPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.PushID(id);
        ImGui.SetCursorPos(new Vector2(startCursor.X + pad.X, startCursor.Y + pad.Y));
        ImGui.BeginGroup();
        draw();
        ImGui.EndGroup();
        ImGui.PopID();

        var contentMin = ImGui.GetItemRectMin();
        var contentMax = ImGui.GetItemRectMax();
        var cardMin = contentMin - pad;
        var cardMax = contentMax + pad;
        var outerMin = cardMin;
        var outerMax = cardMax;

        if (stretchWidth)
        {
            var windowPos = ImGui.GetWindowPos();
            var regionMin = ImGui.GetWindowContentRegionMin();
            var regionMax = ImGui.GetWindowContentRegionMax();
            var scrollX = ImGui.GetScrollX();
            cardMin.X = windowPos.X + regionMin.X + scrollX;
            cardMax.X = windowPos.X + regionMax.X + scrollX;
            outerMin.X = cardMin.X;
            outerMax.X = cardMax.X;
            startCursor.X = ImGui.GetWindowContentRegionMin().X;
        }

        var drawMin = new Vector2(cardMin.X + borderInset, cardMin.Y + borderInset);
        var drawMax = new Vector2(cardMax.X - borderInset, cardMax.Y - borderInset);
        var clipMin = drawList.GetClipRectMin();
        var clipMax = drawList.GetClipRectMax();
        var clipInset = new Vector2(borderThickness * 0.5f + 0.5f, borderThickness * 0.5f + 0.5f);
        drawMin = Vector2.Max(drawMin, clipMin + clipInset);
        drawMax = Vector2.Min(drawMax, clipMax - clipInset);
        if (drawMax.X <= drawMin.X)
        {
            drawMax.X = drawMin.X + borderThickness;
        }
        if (drawMax.Y <= drawMin.Y)
        {
            drawMax.Y = drawMin.Y + borderThickness;
        }

        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(drawMin, drawMax, ImGui.ColorConvertFloat4ToU32(cardBg), cardRounding);
        if (cardBorder.W > 0f && borderThickness > 0f)
        {
            drawList.AddRect(drawMin, drawMax, ImGui.ColorConvertFloat4ToU32(cardBorder), cardRounding, ImDrawFlags.None, borderThickness);
        }
        drawList.ChannelsMerge();

        ImGui.SetCursorPos(startCursor);
        var dummyWidth = outerMax.X - outerMin.X;
        var dummyHeight = outerMax.Y - outerMin.Y;
        ImGui.Dummy(new Vector2(dummyWidth, dummyHeight));
        ImGui.SetCursorPos(new Vector2(startCursor.X, startCursor.Y + dummyHeight));

        if (!stretchWidth)
        {
            ImGui.SetCursorPosX(originalCursor.X);
        }
        else
        {
            ImGui.SetCursorPosX(startCursor.X);
        }
    }

    public static bool DrawArrowToggle(ref bool state, string id)
    {
        var framePadding = ImGui.GetStyle().FramePadding;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(framePadding.X, framePadding.Y * 0.85f));
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.08f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.16f));
        bool clicked = ImGui.ArrowButton(id, state ? ImGuiDir.Down : ImGuiDir.Right);
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
        if (clicked)
        {
            state = !state;
        }
        return state;
    }

    public static float GetCardContentPaddingX()
    {
        var style = ImGui.GetStyle();
        return style.FramePadding.X + 4f * ImGuiHelpers.GlobalScale;
    }

    public static void DrawGroupedCenteredColorText(string text, Vector4 color, float? maxWidth = null)
    {
        var availWidth = ImGui.GetContentRegionAvail().X;
        var textWidth = ImGui.CalcTextSize(text, hideTextAfterDoubleHash: false, availWidth).X;
        if (maxWidth != null && textWidth > maxWidth * ImGuiHelpers.GlobalScale) textWidth = maxWidth.Value * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth / 2f) - (textWidth / 2f));
        DrawGrouped(() =>
        {
            ColorTextWrapped(text, color, ImGui.GetCursorPosX() + textWidth);
        }, expectedWidth: maxWidth == null ? null : maxWidth * ImGuiHelpers.GlobalScale);
    }

    public static void DrawOutlinedFont(string text, Vector4 fontColor, Vector4 outlineColor, int thickness)
    {
        var original = ImGui.GetCursorPos();

        using (ImRaii.PushColor(ImGuiCol.Text, outlineColor))
        {
            ImGui.SetCursorPos(original with { Y = original.Y - thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { X = original.X - thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { Y = original.Y + thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { X = original.X + thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { X = original.X - thickness, Y = original.Y - thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { X = original.X + thickness, Y = original.Y + thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { X = original.X - thickness, Y = original.Y + thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { X = original.X + thickness, Y = original.Y - thickness });
            ImGui.TextUnformatted(text);
        }

        using (ImRaii.PushColor(ImGuiCol.Text, fontColor))
        {
            ImGui.SetCursorPos(original);
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original);
            ImGui.TextUnformatted(text);
        }
    }

    public static void DrawOutlinedFont(ImDrawListPtr drawList, string text, Vector2 textPos, uint fontColor, uint outlineColor, int thickness)
    {
        drawList.AddText(textPos with { Y = textPos.Y - thickness },
            outlineColor, text);
        drawList.AddText(textPos with { X = textPos.X - thickness },
            outlineColor, text);
        drawList.AddText(textPos with { Y = textPos.Y + thickness },
            outlineColor, text);
        drawList.AddText(textPos with { X = textPos.X + thickness },
            outlineColor, text);
        drawList.AddText(new Vector2(textPos.X - thickness, textPos.Y - thickness),
            outlineColor, text);
        drawList.AddText(new Vector2(textPos.X + thickness, textPos.Y + thickness),
            outlineColor, text);
        drawList.AddText(new Vector2(textPos.X - thickness, textPos.Y + thickness),
            outlineColor, text);
        drawList.AddText(new Vector2(textPos.X + thickness, textPos.Y - thickness),
            outlineColor, text);

        drawList.AddText(textPos, fontColor, text);
        drawList.AddText(textPos, fontColor, text);
    }

    public static void DrawTree(string leafName, Action drawOnOpened, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
    {
        using var tree = ImRaii.TreeNode(leafName, flags);
        if (tree)
        {
            drawOnOpened();
        }
    }

    public static Vector4 GetBoolColor(bool input) => input ? AccentColor : UiSharedService.AccentColor;

    public float GetIconTextButtonSize(FontAwesomeIcon icon, string text)
    {
        Vector2 vector;
        using (IconFont.Push())
            vector = ImGui.CalcTextSize(icon.ToIconString());

        Vector2 vector2 = ImGui.CalcTextSize(text);
        float num = 3f * ImGuiHelpers.GlobalScale;
        return vector.X + vector2.X + ImGui.GetStyle().FramePadding.X * 2f + num;
    }

    public static Vector2 GetIconSize(FontAwesomeIcon icon)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(icon.ToIconString());
        return iconSize;
    }

    public static string GetNotes(List<Pair> pairs)
    {
        StringBuilder sb = new();
        sb.AppendLine(_notesStart);
        foreach (var entry in pairs)
        {
            var note = entry.GetNote();
            if (note.IsNullOrEmpty()) continue;

            sb.Append(entry.UserData.UID).Append(":\"").Append(entry.GetNote()).AppendLine("\"");
        }
        sb.AppendLine(_notesEnd);

        return sb.ToString();
    }

    public static float GetWindowContentRegionWidth()
    {
        return ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
    }

    public static float GetWindowContentRegionHeight()
    {
        return ImGui.GetWindowContentRegionMax().Y - ImGui.GetWindowContentRegionMin().Y;
    }

    public bool IconButton(FontAwesomeIcon icon, float? height = null)
    {
        string text = icon.ToIconString();

        ImGui.PushID(text);
        Vector2 vector;
        using (IconFont.Push())
            vector = ImGui.CalcTextSize(text);
        ImDrawListPtr windowDrawList = ImGui.GetWindowDrawList();
        Vector2 cursorScreenPos = ImGui.GetCursorScreenPos();
        float x = vector.X + ImGui.GetStyle().FramePadding.X * 2f;
        float frameHeight = height ?? ImGui.GetFrameHeight();
        using var hoverColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, AccentHoverColor);
        using var activeColor = ImRaii.PushColor(ImGuiCol.ButtonActive, AccentActiveColor);
        bool result = ImGui.Button(string.Empty, new Vector2(x, frameHeight));
        Vector2 pos = new Vector2(cursorScreenPos.X + ImGui.GetStyle().FramePadding.X,
            cursorScreenPos.Y + (height ?? ImGui.GetFrameHeight()) / 2f - (vector.Y / 2f));
        using (IconFont.Push())
            windowDrawList.AddText(pos, ImGui.GetColorU32(ImGuiCol.Text), text);
        ImGui.PopID();

        return result;
    }

    public bool IconButtonCentered(FontAwesomeIcon icon, float? height = null, float xOffset = 0f, float yOffset = 0f, bool square = false)
    {
        string text = icon.ToIconString();

        ImGui.PushID($"centered-{text}");
        Vector2 glyphSize;
        using (IconFont.Push())
            glyphSize = ImGui.CalcTextSize(text);
        float frameHeight = height ?? ImGui.GetFrameHeight();
        float buttonWidth = square ? frameHeight : glyphSize.X + ImGui.GetStyle().FramePadding.X * 2f;
        using var hoverColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, AccentHoverColor);
        using var activeColor = ImRaii.PushColor(ImGuiCol.ButtonActive, AccentActiveColor);
        bool clicked = ImGui.Button(string.Empty, new Vector2(buttonWidth, frameHeight));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var center = (min + max) / 2f;
        Vector2 pos = new Vector2(
            center.X - glyphSize.X / 2f + xOffset,
            center.Y - glyphSize.Y / 2f + yOffset);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        using (IconFont.Push())
            drawList.AddText(pos, ImGui.GetColorU32(ImGuiCol.Text), text);
        ImGui.PopID();

        return clicked;
    }
    public bool IconPauseButtonCentered(float? height = null)
    {
        ImGui.PushID("centered-pause-custom");
        Vector2 glyphSize;
        using (IconFont.Push())
            glyphSize = ImGui.CalcTextSize(FontAwesomeIcon.Pause.ToIconString());
        float frameHeight = height ?? ImGui.GetFrameHeight();
        float buttonWidth = glyphSize.X + ImGui.GetStyle().FramePadding.X * 2f;

        using var hoverColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, AccentHoverColor);
        using var activeColor = ImRaii.PushColor(ImGuiCol.ButtonActive, AccentActiveColor);

        var drawList = ImGui.GetWindowDrawList();
        var buttonTopLeft = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.Button(string.Empty, new Vector2(buttonWidth, frameHeight));

        var textColor = ImGui.GetColorU32(ImGuiCol.Text);

        float h = frameHeight * 0.55f;                   // bar height
        float w = MathF.Max(1f, frameHeight * 0.16f);    // bar width
        float gap = MathF.Max(1f, w * 0.9f);             // gap between bars
        float total = 2f * w + gap;

        float startX = buttonTopLeft.X + (buttonWidth - total) / 2f;
        float startY = buttonTopLeft.Y + (frameHeight - h) / 2f;
        float rounding = w * 0.35f;

        drawList.AddRectFilled(new Vector2(startX, startY), new Vector2(startX + w, startY + h), textColor, rounding);
        float rightX = startX + w + gap;
        drawList.AddRectFilled(new Vector2(rightX, startY), new Vector2(rightX + w, startY + h), textColor, rounding);

        ImGui.PopID();
        return clicked;
    }

    public bool IconPlusButtonCentered(float? height = null)
    {
        ImGui.PushID("centered-plus-custom");
        Vector2 glyphSize;
        using (IconFont.Push())
            glyphSize = ImGui.CalcTextSize(FontAwesomeIcon.Plus.ToIconString());
        float frameHeight = height ?? ImGui.GetFrameHeight();
        float buttonWidth = glyphSize.X + ImGui.GetStyle().FramePadding.X * 2f;

        using var hoverColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, AccentHoverColor);
        using var activeColor = ImRaii.PushColor(ImGuiCol.ButtonActive, AccentActiveColor);

        var drawList = ImGui.GetWindowDrawList();
        var buttonTopLeft = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.Button(string.Empty, new Vector2(buttonWidth, frameHeight));

        var color = ImGui.GetColorU32(ImGuiCol.Text);

        float armThickness = MathF.Max(1f, frameHeight * 0.14f);
        float crossSize = frameHeight * 0.55f; // total length of vertical/horizontal arms
        float startX = buttonTopLeft.X + (buttonWidth - crossSize) / 2f;
        float startY = buttonTopLeft.Y + (frameHeight - crossSize) / 2f;
        float endX = startX + crossSize;
        float endY = startY + crossSize;
        float r = armThickness * 0.35f;

        float hY1 = startY + (crossSize - armThickness) / 2f;
        drawList.AddRectFilled(new Vector2(startX, hY1), new Vector2(endX, hY1 + armThickness), color, r);
        float vX1 = startX + (crossSize - armThickness) / 2f;
        drawList.AddRectFilled(new Vector2(vX1, startY), new Vector2(vX1 + armThickness, endY), color, r);

        ImGui.PopID();
        return clicked;
    }

    private bool IconTextButtonInternal(FontAwesomeIcon icon, string text, Vector4? defaultColor = null, float? width = null, bool useAccentHover = true, float? height = null)
    {
        int colorsPushed = 0;
        if (defaultColor.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, defaultColor.Value);
            colorsPushed++;
        }
        if (useAccentHover)
        {
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHoverColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActiveColor);
            colorsPushed += 2;
        }

        ImGui.PushID(text);
        Vector2 iconSize;
        using (IconFont.Push())
            iconSize = ImGui.CalcTextSize(icon.ToIconString());
        Vector2 textSize = ImGui.CalcTextSize(text);
        ImDrawListPtr windowDrawList = ImGui.GetWindowDrawList();
        Vector2 cursorScreenPos = ImGui.GetCursorScreenPos();
        float spacing = 3f * ImGuiHelpers.GlobalScale;
        float totalContentWidth = iconSize.X + spacing + textSize.X;
        float x = width ?? totalContentWidth + ImGui.GetStyle().FramePadding.X * 2f;
        float h = height ?? ImGui.GetFrameHeight();

        bool result = ImGui.Button(string.Empty, new Vector2(x, h));

        float offsetX = (x - totalContentWidth) / 2f;
        float offsetY = (h - MathF.Max(iconSize.Y, textSize.Y)) / 2f;

        Vector2 iconPos = new Vector2(cursorScreenPos.X + offsetX, cursorScreenPos.Y + offsetY);
        using (IconFont.Push())
            windowDrawList.AddText(iconPos, ImGui.GetColorU32(ImGuiCol.Text), icon.ToIconString());

        Vector2 textPos = new Vector2(iconPos.X + iconSize.X + spacing, cursorScreenPos.Y + offsetY);
        windowDrawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), text);

        ImGui.PopID();
        if (colorsPushed > 0)
        {
            ImGui.PopStyleColor(colorsPushed);
        }

        return result;
    }

    public bool IconTextButton(FontAwesomeIcon icon, string text, float? width = null, bool isInPopup = false, Vector4? buttonColor = null, float? height = null)
    {
        return IconTextButtonInternal(icon, text,
            buttonColor ?? (isInPopup ? ColorHelpers.RgbaUintToVector4(ImGui.GetColorU32(ImGuiCol.PopupBg)) : null),
            width <= 0 ? null : width,
            !isInPopup,
            height <= 0 ? null : height);
    }

    public static bool IsDirectoryWritable(string dirPath, bool throwIfFails = false)
    {
        try
        {
            using FileStream fs = File.Create(
                       Path.Combine(
                           dirPath,
                           Path.GetRandomFileName()
                       ),
                       1,
                       FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            if (throwIfFails)
                throw;

            return false;
        }
    }

    public static void SetScaledWindowSize(float width, bool centerWindow = true)
    {
        var newLineHeight = ImGui.GetCursorPosY();
        ImGui.NewLine();
        newLineHeight = ImGui.GetCursorPosY() - newLineHeight;
        var y = ImGui.GetCursorPos().Y + ImGui.GetWindowContentRegionMin().Y - newLineHeight * 2 - ImGui.GetStyle().ItemSpacing.Y;

        SetScaledWindowSize(width, y, centerWindow, scaledHeight: true);
    }

    public static void SetScaledWindowSize(float width, float height, bool centerWindow = true, bool scaledHeight = false)
    {
        ImGui.SameLine();
        var x = width * ImGuiHelpers.GlobalScale;
        var y = scaledHeight ? height : height * ImGuiHelpers.GlobalScale;

        if (centerWindow)
        {
            CenterWindow(x, y);
        }

        ImGui.SetWindowSize(new Vector2(x, y));
    }

    public static bool ShiftPressed() => (GetKeyState(0xA1) & 0x8000) != 0 || (GetKeyState(0xA0) & 0x8000) != 0;

    public static void TextWrapped(string text, float wrapPos = 0)
    {
        ImGui.PushTextWrapPos(wrapPos);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    public static Vector4 UploadColor((long, long) data) => data.Item1 == 0 ? ImGuiColors.DalamudGrey :
        data.Item1 == data.Item2 ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudYellow;

    public bool ApplyNotesFromClipboard(string notes, bool overwrite)
    {
        var splitNotes = notes.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();
        var splitNotesStart = splitNotes.FirstOrDefault();
        var splitNotesEnd = splitNotes.LastOrDefault();
        if (!string.Equals(splitNotesStart, _notesStart, StringComparison.Ordinal) || !string.Equals(splitNotesEnd, _notesEnd, StringComparison.Ordinal))
        {
            return false;
        }

        splitNotes.RemoveAll(n => string.Equals(n, _notesStart, StringComparison.Ordinal) || string.Equals(n, _notesEnd, StringComparison.Ordinal));

        foreach (var note in splitNotes)
        {
            try
            {
                var splittedEntry = note.Split(":", 2, StringSplitOptions.RemoveEmptyEntries);
                var uid = splittedEntry[0];
                var comment = splittedEntry[1].Trim('"');
                if (_serverConfigurationManager.GetNoteForUid(uid) != null && !overwrite) continue;
                _serverConfigurationManager.SetNoteForUid(uid, comment);
            }
            catch
            {
                Logger.LogWarning("Could not parse {note}", note);
            }
        }

        _serverConfigurationManager.SaveNotes();

        return true;
    }

    public void BigText(string text, Vector4? color = null)
    {
        FontText(text, UidFont, color);
    }

    public void BooleanToColoredIcon(bool value, bool inline = true)
    {
        using var colorgreen = ImRaii.PushColor(ImGuiCol.Text, AccentColor, value);
        using var colorred = ImRaii.PushColor(ImGuiCol.Text, UiSharedService.AccentColor, !value);

        if (inline) ImGui.SameLine();

        if (value)
        {
            IconText(FontAwesomeIcon.Check);
        }
        else
        {
            IconText(FontAwesomeIcon.Times);
        }
    }

    public void DrawCacheDirectorySetting()
    {
        ColorTextWrapped("Note: The storage folder should be somewhere close to root (i.e. C:\\UmbraStorage) in a new empty folder. DO NOT point this to your game folder. DO NOT point this to your Penumbra folder.", ImGuiColors.DalamudYellow);
        var cacheDirectory = _configService.Current.CacheFolder;
        ImGui.SetNextItemWidth(MathF.Min(400 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
        ImGui.InputText("Storage Folder##cache", ref cacheDirectory, 255, ImGuiInputTextFlags.ReadOnly);

        ImGui.SameLine();
        using (ImRaii.Disabled(_cacheMonitor.MareWatcher != null))
        {
            if (IconButton(FontAwesomeIcon.Folder))
            {
                FileDialogManager.OpenFolderDialog("Pick Umbra Storage Folder", (success, path) =>
                {
                    if (!success) return;

                    _isOneDrive = path.Contains("onedrive", StringComparison.OrdinalIgnoreCase);
                    _isPenumbraDirectory = string.Equals(path.ToLowerInvariant(), _ipcManager.Penumbra.ModDirectory?.ToLowerInvariant(), StringComparison.Ordinal);
                    _isDirectoryWritable = IsDirectoryWritable(path);
                    _cacheDirectoryHasOtherFilesThanCache = false;
                    var cacheDirFiles = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                    var cacheSubDirs = Directory.GetDirectories(path);

                    _cacheDirectoryHasOtherFilesThanCache = cacheDirFiles.Any(f =>
                        Path.GetFileNameWithoutExtension(f).Length != 40
                            && !Path.GetExtension(f).Equals("tmp", StringComparison.OrdinalIgnoreCase)
                            && !Path.GetExtension(f).Equals("blk", StringComparison.OrdinalIgnoreCase)
                    );

                    if (!_cacheDirectoryHasOtherFilesThanCache
                        && cacheSubDirs.Select(f => Path.GetFileName(Path.TrimEndingDirectorySeparator(f))).Any(f =>
                            !f.Equals("subst", StringComparison.OrdinalIgnoreCase)
                    ))
                        _cacheDirectoryHasOtherFilesThanCache = true;

                    _cacheDirectoryIsValidPath = PathRegex().IsMatch(path);

                    if (!string.IsNullOrEmpty(path)
                        && Directory.Exists(path)
                        && _isDirectoryWritable
                        && !_isPenumbraDirectory
                        && !_isOneDrive
                        && !_cacheDirectoryHasOtherFilesThanCache
                        && _cacheDirectoryIsValidPath)
                    {
                        _configService.Current.CacheFolder = path;
                        _configService.Save();
                        _cacheMonitor.StartMareWatcher(path);
                        _cacheMonitor.InvokeScan();
                    }
                }, _dalamudUtil.IsWine ? @"Z:\" : @"C:\");
            }
        }
        if (_cacheMonitor.MareWatcher != null)
        {
            AttachToolTip("Stop the Monitoring before changing the Storage folder. As long as monitoring is active, you cannot change the Storage folder location.");
        }

        if (_isPenumbraDirectory)
        {
            ColorTextWrapped("Do not point the storage path directly to the Penumbra directory. If necessary, make a subfolder in it.", UiSharedService.AccentColor);
        }
        else if (_isOneDrive)
        {
            ColorTextWrapped("Do not point the storage path to a folder in OneDrive. Do not use OneDrive folders for any Mod related functionality.", UiSharedService.AccentColor);
        }
        else if (!_isDirectoryWritable)
        {
            ColorTextWrapped("The folder you selected does not exist or cannot be written to. Please provide a valid path.", UiSharedService.AccentColor);
        }
        else if (_cacheDirectoryHasOtherFilesThanCache)
        {
            ColorTextWrapped("Your selected directory has files or directories inside that are not Umbra related. Use an empty directory or a previous storage directory only.", UiSharedService.AccentColor);
        }
        else if (!_cacheDirectoryIsValidPath)
        {
            ColorTextWrapped("Your selected directory contains illegal characters unreadable by FFXIV. " +
                             "Restrict yourself to latin letters (A-Z), underscores (_), dashes (-) and arabic numbers (0-9).", UiSharedService.AccentColor);
        }

        float maxCacheSize = (float)_configService.Current.MaxLocalCacheInGiB;
        ImGui.SetNextItemWidth(MathF.Min(400 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
        if (ImGui.SliderFloat("Maximum Storage Size", ref maxCacheSize, 1f, 200f, "%.2f GiB"))
        {
            _configService.Current.MaxLocalCacheInGiB = maxCacheSize;
            _configService.Save();
        }
        DrawHelpText("The storage is automatically governed by Umbra. It will clear itself automatically once it reaches the set capacity by removing the oldest unused files. You typically do not need to clear it yourself.");
    }

    public T? DrawCombo<T>(string comboName, IEnumerable<T> comboItems, Func<T, string> toName,
        Action<T?>? onSelected = null, T? initialSelectedItem = default)
    {
        var comboItemList = comboItems as IList<T> ?? comboItems.ToList();
        if (comboItemList.Count == 0) return default;

        if (!_selectedComboItems.TryGetValue(comboName, out var selectedItem) && selectedItem == null)
        {
            if (!EqualityComparer<T>.Default.Equals(initialSelectedItem, default))
            {
                selectedItem = initialSelectedItem;
                _selectedComboItems[comboName] = selectedItem!;
                if (!EqualityComparer<T>.Default.Equals(initialSelectedItem, default))
                    onSelected?.Invoke(initialSelectedItem);
            }
            else
            {
                selectedItem = comboItemList[0];
                _selectedComboItems[comboName] = selectedItem!;
            }
        }

        if (ImGui.BeginCombo(comboName, toName((T)selectedItem!)))
        {
            foreach (var item in comboItemList)
            {
                bool isSelected = EqualityComparer<T>.Default.Equals(item, (T?)selectedItem);
                if (ImGui.Selectable(toName(item), isSelected))
                {
                    _selectedComboItems[comboName] = item!;
                    onSelected?.Invoke(item!);
                }
            }

            ImGui.EndCombo();
        }

        return (T)_selectedComboItems[comboName];
    }

    public T? DrawColorCombo<T>(string comboName, IEnumerable<T> comboItems, Func<T, (uint Color, string Name)> toEntry,
        Action<T?>? onSelected = null, T? initialSelectedItem = default)
    {
        var comboItemList = comboItems as IList<T> ?? comboItems.ToList();
        if (comboItemList.Count == 0) return default;

        if (!_selectedComboItems.TryGetValue(comboName, out var selectedItem) && selectedItem == null)
        {
            if (!EqualityComparer<T>.Default.Equals(initialSelectedItem, default))
            {
                selectedItem = initialSelectedItem;
                _selectedComboItems[comboName] = selectedItem!;
                if (!EqualityComparer<T>.Default.Equals(initialSelectedItem, default))
                    onSelected?.Invoke(initialSelectedItem);
            }
            else
            {
                selectedItem = comboItemList[0];
                _selectedComboItems[comboName] = selectedItem!;
            }
        }

        var entry = toEntry((T)selectedItem!);
        ImGui.PushStyleColor(ImGuiCol.Text, ColorHelpers.RgbaUintToVector4(ColorHelpers.SwapEndianness(entry.Color)));
        if (ImGui.BeginCombo(comboName, entry.Name))
        {
            foreach (var item in comboItemList)
            {
                entry = toEntry(item);
                ImGui.PushStyleColor(ImGuiCol.Text, ColorHelpers.RgbaUintToVector4(ColorHelpers.SwapEndianness(entry.Color)));
                bool isSelected = EqualityComparer<T>.Default.Equals(item, (T)selectedItem!);
                if (ImGui.Selectable(entry.Name, isSelected))
                {
                    _selectedComboItems[comboName] = item!;
                    onSelected?.Invoke(item!);
                }
                ImGui.PopStyleColor();
            }

            ImGui.EndCombo();
        }
        ImGui.PopStyleColor();

        return (T)_selectedComboItems[comboName];
    }

    public void DrawFileScanState()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("File Scanner Status");
        ImGui.SameLine();
        if (_cacheMonitor.IsScanRunning)
        {
            ImGui.AlignTextToFramePadding();

            ImGui.TextUnformatted("Scan is running");
            ImGui.TextUnformatted("Current Progress:");
            ImGui.SameLine();
            ImGui.TextUnformatted(_cacheMonitor.TotalFiles == 1
                ? "Collecting files"
                : $"Processing {_cacheMonitor.CurrentFileProgress}/{_cacheMonitor.TotalFilesStorage} from storage ({_cacheMonitor.TotalFiles} scanned in)");
            AttachToolTip("Note: it is possible to have more files in storage than scanned in, " +
                "this is due to the scanner normally ignoring those files but the game loading them in and using them on your character, so they get " +
                "added to the local storage.");
        }
        else if (_cacheMonitor.HaltScanLocks.Any(f => f.Value.Value > 0))
        {
            ImGui.AlignTextToFramePadding();

            ImGui.TextUnformatted("Halted (" + string.Join(", ", _cacheMonitor.HaltScanLocks.Where(f => f.Value.Value > 0).Select(locker => locker.Key + ": " + locker.Value.Value)) + ")");
            ImGui.SameLine();
            if (ImGui.Button("Reset halt requests##clearlocks"))
            {
                _cacheMonitor.ResetLocks();
            }
        }
        else
        {
            ImGui.TextUnformatted("Idle");
            if (_configService.Current.InitialScanComplete)
            {
                ImGui.SameLine();
                if (IconTextButton(FontAwesomeIcon.Play, "Force rescan"))
                {
                    _cacheMonitor.InvokeScan();
                }
            }
        }
    }
    public void DrawHelpText(string helpText)
    {
        ImGui.SameLine();
        IconText(FontAwesomeIcon.QuestionCircle, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        AttachToolTip(helpText);
    }

    public bool DrawOtherPluginState(bool intro = false)
    {
        var check = FontAwesomeIcon.Check;
        var cross = FontAwesomeIcon.SquareXmark;

        if (intro)
        {
            SetFontScale(0.8f);
            BigText(Loc.Get("Settings.Plugins.Mandatory"));
            SetFontScale(1.0f);

            DrawPluginEntry("Penumbra", _penumbraExists, check, cross);
            DrawPluginEntry("Glamourer", _glamourerExists, check, cross);

            SetFontScale(0.8f);
            BigText(Loc.Get("Settings.Plugins.Optional"));
            SetFontScale(1.0f);
            UiSharedService.TextWrapped(Loc.Get("Settings.Plugins.Optional.Hint"));

            DrawPluginEntry("SimpleHeels", _heelsExists, check, cross);
            DrawPluginEntry("Customize+", _customizePlusExists, check, cross);
            DrawPluginEntry("Honorific", _honorificExists, check, cross);
            DrawPluginEntry("PetNicknames", _petNamesExists, check, cross);
            DrawPluginEntry("Moodles", _moodlesExists, check, cross);
            DrawPluginEntry("Brio", _brioExists, check, cross);
            DrawPluginEntry("A Quest Reborn", _questRebornExists, check, cross);
            DrawPluginEntry("Chat2", _chatTwoExists, check, cross);
            DrawPluginEntry("Chat Proximity", _chatProximityExists, check, cross);
        }
        else
        {
            // Mandatory plugins — single line
            ImGui.TextUnformatted(Loc.Get("Settings.Plugins.Mandatory"));
            ImGui.SameLine();
            DrawHelpText(Loc.Get("Settings.Plugins.Mandatory.Hint"));
            if (ImGui.BeginTable("##MandatoryPlugins", 2, ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("##col1", ImGuiTableColumnFlags.None, 130f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("##col2", ImGuiTableColumnFlags.None);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawPluginEntry("Penumbra", _penumbraExists, check, cross);
                ImGui.TableNextColumn();
                DrawPluginEntry("Glamourer", _glamourerExists, check, cross);
                ImGui.EndTable();
            }

            if (!_penumbraExists || !_glamourerExists)
            {
                ImGuiHelpers.ScaledDummy(2f);
                ColorTextWrapped(Loc.Get("Settings.Plugins.MissingMandatory"), AccentColor);
            }

            ImGuiHelpers.ScaledDummy(4f);

            // Optional plugins — two per line
            ImGui.TextUnformatted(Loc.Get("Settings.Plugins.Optional"));
            ImGui.SameLine();
            DrawHelpText(Loc.Get("Settings.Plugins.Optional.Hint"));

            var optionalPlugins = new (string Name, bool Exists)[]
            {
                ("SimpleHeels", _heelsExists),
                ("Customize+", _customizePlusExists),
                ("Honorific", _honorificExists),
                ("PetNicknames", _petNamesExists),
                ("Moodles", _moodlesExists),
                ("Brio", _brioExists),
                ("A Quest Reborn", _questRebornExists),
            };
            if (ImGui.BeginTable("##OptionalPlugins", 2, ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("##col1", ImGuiTableColumnFlags.None, 130f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("##col2", ImGuiTableColumnFlags.None);
                for (int pi = 0; pi < optionalPlugins.Length; pi++)
                {
                    if (pi % 2 == 0) ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    DrawPluginEntry(optionalPlugins[pi].Name, optionalPlugins[pi].Exists, check, cross);
                }
                ImGui.EndTable();
            }

            ImGuiHelpers.ScaledDummy(4f);

            // Detected plugins
            ImGui.TextUnformatted(Loc.Get("Settings.Plugins.Detected"));
            ImGui.SameLine();
            DrawHelpText(Loc.Get("Settings.Plugins.Detected.Hint"));
            if (ImGui.BeginTable("##DetectedPlugins", 2, ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("##col1", ImGuiTableColumnFlags.None, 130f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("##col2", ImGuiTableColumnFlags.None);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawPluginEntry("Chat2", _chatTwoExists, check, cross);
                ImGui.SameLine();
                DrawHelpText(Loc.Get("Settings.Plugins.Chat2.Hint"));
                ImGui.TableNextColumn();
                DrawPluginEntry("Chat Proximity", _chatProximityExists, check, cross);
                ImGui.SameLine();
                DrawHelpText(Loc.Get("Settings.Plugins.ChatProximity.Hint"));
                ImGui.EndTable();
            }
        }

        if (!_penumbraExists || !_glamourerExists)
        {
            return false;
        }

        return true;
    }

    private void DrawPluginEntry(string name, bool exists, FontAwesomeIcon check, FontAwesomeIcon cross)
    {
        ImGui.TextUnformatted(name);
        ImGui.SameLine();
        IconText(exists ? check : cross, GetBoolColor(exists));
    }

    public int DrawServiceSelection(bool selectOnChange = false, bool intro = false)
    {
        string[] comboEntries = _serverConfigurationManager.GetServerNames();

        if (_serverSelectionIndex == -1)
        {
            _serverSelectionIndex = Array.IndexOf(_serverConfigurationManager.GetServerApiUrls(), _serverConfigurationManager.CurrentApiUrl);
        }
        if (_serverSelectionIndex == -1 || _serverSelectionIndex >= comboEntries.Length)
        {
            _serverSelectionIndex = 0;
        }
        for (int i = 0; i < comboEntries.Length; i++)
        {
            if (string.Equals(_serverConfigurationManager.CurrentServer?.ServerName, comboEntries[i], StringComparison.OrdinalIgnoreCase))
                comboEntries[i] += " [Current]";
        }
        ImGui.SetNextItemWidth(MathF.Min(250 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
        if (ImGui.BeginCombo("Select Service", comboEntries[_serverSelectionIndex]))
        {
            for (int i = 0; i < comboEntries.Length; i++)
            {
                bool isSelected = _serverSelectionIndex == i;
                if (ImGui.Selectable(comboEntries[i], isSelected))
                {
                    _serverSelectionIndex = i;
                    if (selectOnChange)
                    {
                        _serverConfigurationManager.SelectServer(i);
                    }
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (intro)
            return _serverSelectionIndex;

        ImGui.SameLine();
        var text = "Connect";
        if (_serverSelectionIndex == _serverConfigurationManager.CurrentServerIndex) text = "Reconnect";
        if (IconTextButton(FontAwesomeIcon.Link, text))
        {
            _serverConfigurationManager.SelectServer(_serverSelectionIndex);
            _ = _apiController.CreateConnections();
        }

        if (ImGui.TreeNode("Add Custom Service"))
        {
            ImGui.SetNextItemWidth(MathF.Min(250 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
            ImGui.InputText("Custom Service URI", ref _customServerUri, 255);
            ImGui.SetNextItemWidth(MathF.Min(250 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
            ImGui.InputText("Custom Service Name", ref _customServerName, 255);
            if (IconTextButton(FontAwesomeIcon.Plus, "Add Custom Service")
                && !string.IsNullOrEmpty(_customServerUri)
                && !string.IsNullOrEmpty(_customServerName))
            {
                _serverConfigurationManager.AddServer(new ServerStorage()
                {
                    ServerName = _customServerName,
                    ServerUri = _customServerUri,
                });
                _customServerName = string.Empty;
                _customServerUri = string.Empty;
                _configService.Save();
            }
            ImGui.TreePop();
        }

        return _serverSelectionIndex;
    }

    public Vector2 GetIconButtonSize(FontAwesomeIcon icon)
    {
        using var font = IconFont.Push();
        return ImGuiHelpers.GetButtonSize(icon.ToIconString());
    }

    public Vector2 GetIconData(FontAwesomeIcon icon)
    {
        using var font = IconFont.Push();
        return ImGui.CalcTextSize(icon.ToIconString());
    }

    public void IconText(FontAwesomeIcon icon, uint color)
    {
        FontText(icon.ToIconString(), IconFont, color);
    }

    public void IconText(FontAwesomeIcon icon, Vector4? color = null)
    {
        IconText(icon, color == null ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(color.Value));
    }

    public IDalamudTextureWrap LoadImage(byte[] imageData)
    {
        if (imageData.Length == 0)
        {
            return _textureProvider.CreateEmpty(new()
            {
                Width = 256,
                Height = 256,
                DxgiFormat = 3,
                Pitch = 1024
            }, cpuRead: false, cpuWrite: false);
        }
        try
        {
            Logger.LogTrace("Creating texture from image data, size: {size}", imageData.Length);
            return _textureProvider.CreateFromImageAsync(imageData).Result;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to create texture from image data");
            return _textureProvider.CreateEmpty(new()
            {
                Width = 256,
                Height = 256,
                DxgiFormat = 3,
                Pitch = 1024
            }, cpuRead: false, cpuWrite: false);
        }
    }

    internal static void DistanceSeparator()
    {
        ImGuiHelpers.ScaledDummy(5);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(5);
    }

    [LibraryImport("user32")]
    internal static partial short GetKeyState(int nVirtKey);

    private static void CenterWindow(float width, float height, ImGuiCond cond = ImGuiCond.None)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetWindowPos(new Vector2(center.X - width / 2, center.Y - height / 2), cond);
    }

    [GeneratedRegex(@"^(?:[a-zA-Z]:\\[\w\s\-\\]+?|\/(?:[\w\s\-\/])+?)$", RegexOptions.ECMAScript, 5000)]
    private static partial Regex PathRegex();

    private static void FontText(string text, IFontHandle font, Vector4? color = null)
    {
        FontText(text, font, color == null ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(color.Value));
    }

    private static void FontText(string text, IFontHandle font, uint color)
    {
        using var pushedFont = font.Push();
        using var pushedColor = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
    }

    public sealed record IconScaleData(Vector2 IconSize, Vector2 NormalizedIconScale, float OffsetX, float IconScaling);

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;

        base.Dispose(disposing);

        UidFont.Dispose();
        GameFont.Dispose();
    }
}