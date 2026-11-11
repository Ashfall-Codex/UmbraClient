using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;
using UmbraSync.UI;
using UmbraSync.UI.Components;
using GlassLevel = UmbraSync.UI.UiSharedService.GlassLevel;

namespace UmbraSync.Services.Mediator;

public abstract class WindowMediatorSubscriberBase : Window, IMediatorSubscriber, IDisposable
{
    private const float OverlapFadeSeconds = 0.15f;
    private const int FootprintMaxAgeFrames = 2;

    private static readonly Dictionary<WindowMediatorSubscriberBase, WindowFootprint> Footprints = [];
    private static readonly Lock FootprintsLock = new();

    protected readonly ILogger _logger;
    private readonly PerformanceCollectorService _performanceCollectorService;
    private float _overlapOpacity;
    private bool _titleBarOverlapped;

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct WindowFootprint(Vector2 Min, Vector2 Max, float TitleBarHeight, int FocusOrder, int Frame);

    protected WindowMediatorSubscriberBase(ILogger logger, MareMediator mediator, string name,
        PerformanceCollectorService performanceCollectorService) : base(name)
    {
        _logger = logger;
        Mediator = mediator;
        _performanceCollectorService = performanceCollectorService;
        _logger.LogTrace("Creating {type}", GetType());

        Mediator.Subscribe<UiToggleMessage>(this, (msg) =>
        {
            if (msg.UiType == GetType())
            {
                Toggle();
            }
        });
    }

    public MareMediator Mediator { get; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public override void PreDraw()
    {
        float glass = UiSharedService.GlassAlpha(UiSharedService.ResolveGlassLevel(WindowGlassLevel));
        float covered = glass + ((1f - glass) * UpdateOverlapOpacity());
        float titleGlass = _titleBarOverlapped ? covered : glass;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        // Rayons concentriques : le contenant est toujours plus arrondi que ce qu'il contient.
        float scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, UiSharedService.RadiusWindow * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, UiSharedService.RadiusCard * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, UiSharedService.RadiusCard * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, UiSharedService.RadiusControl * scale);

        ImGui.PushStyleColor(ImGuiCol.WindowBg,
            UiSharedService.WithAlpha(UiSharedService.ThemeWindowBg, glass));
        // Éphémères et posés au-dessus de tout : le cas d'usage idéal du matériau.
        ImGui.PushStyleColor(ImGuiCol.PopupBg,
            UiSharedService.WithAlpha(UiSharedService.ThemeWindowBg, covered));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UiSharedService.ThemeChildBg);
        ImGui.PushStyleColor(ImGuiCol.Border, UiSharedService.ThemeBorder);
        ImGui.PushStyleColor(ImGuiCol.Separator, UiSharedService.ThemeSeparator);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, UiSharedService.WithAlpha(UiSharedService.ThemeTitleBar, titleGlass));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, UiSharedService.WithAlpha(UiSharedService.ThemeTitleBar, titleGlass));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, UiSharedService.WithAlpha(UiSharedService.ThemeTitleBar, titleGlass));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, UiSharedService.ThemeFrameBg);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, UiSharedService.ThemeFrameBgHovered);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, UiSharedService.ThemeFrameBgActive);
        ImGui.PushStyleColor(ImGuiCol.Button, UiSharedService.ThemeButtonBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiSharedService.ThemeButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiSharedService.ThemeButtonActive);
        ImGui.PushStyleColor(ImGuiCol.Header, UiSharedService.ThemeHeaderBg);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, UiSharedService.ThemeHeaderHovered);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, UiSharedService.ThemeHeaderActive);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, UiSharedService.ThemeSliderGrab);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, UiSharedService.ThemeSliderGrabActive);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, UiSharedService.ThemeCheckMark);
        ImGui.PushStyleColor(ImGuiCol.Tab, UiSharedService.ThemeTabNormal);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, UiSharedService.ThemeTabHovered);
        ImGui.PushStyleColor(ImGuiCol.TabActive, UiSharedService.ThemeTabActive);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(UiSharedService.ThemeColorCount);
        ImGui.PopStyleVar(UiSharedService.ThemeStyleVarCount);
    }

    private float UpdateOverlapOpacity()
    {
        int frame = ImGui.GetFrameCount();
        bool overlapped = false;
        WindowFootprint own;
        bool tracked;
        lock (FootprintsLock)
            tracked = Footprints.TryGetValue(this, out own);
        bool fresh = tracked && frame - own.Frame <= FootprintMaxAgeFrames;
        _titleBarOverlapped = false;
        if (fresh)
        {
            lock (FootprintsLock)
            foreach (var (other, below) in Footprints)
            {
                if (!IsBelow(other, below, own, frame)) continue;
                var min = Vector2.Max(own.Min, below.Min);
                var max = Vector2.Min(own.Max, below.Max);
                if (max.X <= min.X || max.Y <= min.Y) continue;
                overlapped = true;
                if (own.TitleBarHeight > 0f && min.Y < own.Min.Y + own.TitleBarHeight) _titleBarOverlapped = true;
            }
        }

        float target = overlapped ? 1f : 0f;
        if (!fresh)
        {
            _overlapOpacity = target;
            return _overlapOpacity;
        }
        float step = 1f - MathF.Exp(-MathF.Max(ImGui.GetIO().DeltaTime, 0f) / OverlapFadeSeconds);
        _overlapOpacity += (target - _overlapOpacity) * step;
        if (MathF.Abs(target - _overlapOpacity) < 0.002f) _overlapOpacity = target;
        return _overlapOpacity;
    }

    private bool IsBelow(WindowMediatorSubscriberBase other, in WindowFootprint below, in WindowFootprint own, int frame)
        => !ReferenceEquals(other, this) && frame - below.Frame <= FootprintMaxAgeFrames && below.FocusOrder < own.FocusOrder;

    private void DrawOverlapBackdrop()
    {
        if (_overlapOpacity <= 0f) return;

        WindowFootprint own;
        lock (FootprintsLock)
        {
            if (!Footprints.TryGetValue(this, out own)) return;
        }

        var style = ImGui.GetStyle();
        float border = style.WindowBorderSize;
        var areaMin = new Vector2(own.Min.X + border, own.Min.Y + MathF.Max(border, own.TitleBarHeight));
        var areaMax = own.Max - new Vector2(border);
        if (areaMax.X <= areaMin.X || areaMax.Y <= areaMin.Y) return;

        float rounding = MathF.Max(0f, style.WindowRounding - border);
        uint color = ImGui.GetColorU32(UiSharedService.WithAlpha(UiSharedService.ThemeWindowBg, _overlapOpacity));
        var drawList = ImGui.GetWindowDrawList();
        int frame = ImGui.GetFrameCount();

        lock (FootprintsLock)
        foreach (var (other, below) in Footprints)
        {
            if (!IsBelow(other, below, own, frame)) continue;
            var min = Vector2.Max(areaMin, below.Min);
            var max = Vector2.Min(areaMax, below.Max);
            if (max.X <= min.X || max.Y <= min.Y) continue;

            var corners = ImDrawFlags.RoundCornersNone;
            bool left = min.X <= areaMin.X + 0.5f;
            bool right = max.X >= areaMax.X - 0.5f;
            bool top = own.TitleBarHeight <= 0f && min.Y <= areaMin.Y + 0.5f;
            bool bottom = max.Y >= areaMax.Y - 0.5f;
            if (top && left) corners |= ImDrawFlags.RoundCornersTopLeft;
            if (top && right) corners |= ImDrawFlags.RoundCornersTopRight;
            if (bottom && left) corners |= ImDrawFlags.RoundCornersBottomLeft;
            if (bottom && right) corners |= ImDrawFlags.RoundCornersBottomRight;

            drawList.AddRectFilled(min, max, color, corners == ImDrawFlags.RoundCornersNone ? 0f : rounding, corners);
        }
    }

    private unsafe void RecordFootprint()
    {
        var window = ImGuiP.GetCurrentWindow();
        if (window.Handle == null) return;
        if ((window.Flags & (ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs)) != ImGuiWindowFlags.None)
        {
            lock (FootprintsLock)
                Footprints.Remove(this);
            return;
        }

        var pos = ImGui.GetWindowPos();
        float titleBar = (window.Flags & ImGuiWindowFlags.NoTitleBar) != ImGuiWindowFlags.None ? 0f : ImGui.GetFrameHeight();
        var footprint = new WindowFootprint(pos, pos + ImGui.GetWindowSize(), titleBar, window.FocusOrder, ImGui.GetFrameCount());
        lock (FootprintsLock)
            Footprints[this] = footprint;
    }
    
    protected virtual GlassLevel WindowGlassLevel => GlassLevel.Regular;

    protected virtual bool UseBackdropBlur => true;

    protected virtual int ContentBackgroundLayers => 1;

    public override void Draw()
    {
        RecordFootprint();
        DrawWindowSheen();
        if (!Flags.HasFlag(ImGuiWindowFlags.NoBackground))
            DrawOverlapBackdrop();
        _performanceCollectorService.LogPerformance(this, $"Draw", DrawInternal);
        ScrollEdgeEffect.DrawForCurrentWindow(WindowGlassLevel, UseBackdropBlur, ContentBackgroundLayers);
        RoundedScrollbars.DrawForCurrentWindow();
    }

    private void DrawWindowSheen()
    {
        if (Flags.HasFlag(ImGuiWindowFlags.NoBackground)) return;

        UiSharedService.DrawWindowGlass(WindowGlassLevel, UseBackdropBlur);
    }

    protected abstract void DrawInternal();

    public virtual Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected virtual void Dispose(bool disposing)
    {
        _logger.LogTrace("Disposing {type}", GetType());

        lock (FootprintsLock)
            Footprints.Remove(this);
        Mediator.UnsubscribeAll(this);
    }
}