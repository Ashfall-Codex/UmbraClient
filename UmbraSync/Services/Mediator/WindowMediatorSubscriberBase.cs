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
    private static readonly Dictionary<WindowMediatorSubscriberBase, WindowFootprint> Footprints = [];
    private static readonly Lock FootprintsLock = new();

    protected readonly ILogger _logger;
    private readonly PerformanceCollectorService _performanceCollectorService;
    private readonly List<(Vector2 Min, Vector2 Max)> _opaqueRegions = [];
    private bool _titleBarOverlapped;

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct WindowFootprint(ImGuiWindowPtr Window, float TitleBarHeight);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct LiveFootprint(Vector2 Min, Vector2 Max, float TitleBarHeight, int FocusOrder, bool Resizable);

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
        UpdateTitleBarOverlap();
        float titleGlass = _titleBarOverlapped ? 1f : glass;

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
            UiSharedService.WithAlpha(UiSharedService.ThemeWindowBg, glass));
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

    private static unsafe bool TryReadLive(in WindowFootprint footprint, out LiveFootprint live)
    {
        live = default;
        var window = footprint.Window;
        if (window.Handle == null) return false;
        if (!(window.Active || window.WasActive) || window.Hidden) return false;
        if (window.DockIsActive && !window.DockTabIsVisible) return false;
        if ((window.Flags & (ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs)) != ImGuiWindowFlags.None) return false;

        var min = window.Pos;
        var max = min + window.Size;
        if (max.X <= min.X || max.Y <= min.Y) return false;
        live = new LiveFootprint(min, max, footprint.TitleBarHeight, window.FocusOrder,
            !window.Collapsed && (window.Flags & ImGuiWindowFlags.NoResize) == ImGuiWindowFlags.None);
        return true;
    }

    private bool TryReadOwn(out LiveFootprint own)
    {
        own = default;
        return Footprints.TryGetValue(this, out var footprint) && TryReadLive(footprint, out own);
    }

    private void UpdateTitleBarOverlap()
    {
        _titleBarOverlapped = false;
        lock (FootprintsLock)
        {
            if (!TryReadOwn(out var own) || own.TitleBarHeight <= 0f) return;

            var titleMin = own.Min;
            var titleMax = new Vector2(own.Max.X, MathF.Min(own.Max.Y, own.Min.Y + own.TitleBarHeight));
            foreach (var (other, footprint) in Footprints)
            {
                if (ReferenceEquals(other, this) || !TryReadLive(footprint, out var below) || below.FocusOrder >= own.FocusOrder) continue;
                if (MathF.Min(titleMax.X, below.Max.X) > MathF.Max(titleMin.X, below.Min.X)
                    && MathF.Min(titleMax.Y, below.Max.Y) > MathF.Max(titleMin.Y, below.Min.Y))
                {
                    _titleBarOverlapped = true;
                    return;
                }
            }
        }
    }

    private void CollectOpaqueRegions(out LiveFootprint own)
    {
        _opaqueRegions.Clear();
        float border = ImGui.GetStyle().WindowBorderSize;
        lock (FootprintsLock)
        {
            if (!TryReadOwn(out own)) return;
            var areaMin = new Vector2(own.Min.X + border, own.Min.Y + MathF.Max(border, own.TitleBarHeight));
            var areaMax = own.Max - new Vector2(border);
            if (areaMax.X <= areaMin.X || areaMax.Y <= areaMin.Y) return;

            float grip = own.Resizable ? MathF.Floor(ImGui.GetFontSize() * 1.35f) : 0f;
            foreach (var (other, footprint) in Footprints)
            {
                if (ReferenceEquals(other, this) || !TryReadLive(footprint, out var below) || below.FocusOrder >= own.FocusOrder) continue;
                var min = Vector2.Max(areaMin, below.Min);
                var max = Vector2.Min(areaMax, below.Max);
                if (max.X <= min.X || max.Y <= min.Y) continue;
                AddExcludingCorner(min, max, own.Max - new Vector2(grip), own.Max);
            }
        }
    }

    private void AddExcludingCorner(Vector2 min, Vector2 max, Vector2 cornerMin, Vector2 cornerMax)
    {
        bool hitsCorner = cornerMax.X > cornerMin.X && max.X > cornerMin.X && min.X < cornerMax.X && max.Y > cornerMin.Y && min.Y < cornerMax.Y;
        if (!hitsCorner)
        {
            _opaqueRegions.Add((min, max));
            return;
        }

        if (cornerMin.Y > min.Y)
            _opaqueRegions.Add((min, new Vector2(max.X, cornerMin.Y)));
        float bandTop = MathF.Max(min.Y, cornerMin.Y);
        if (cornerMin.X > min.X && max.Y > bandTop)
            _opaqueRegions.Add((new Vector2(min.X, bandTop), new Vector2(cornerMin.X, max.Y)));
    }

    private void DrawOverlapBackdrop(in LiveFootprint own)
    {
        if (_opaqueRegions.Count == 0) return;

        var style = ImGui.GetStyle();
        float border = style.WindowBorderSize;
        var shapeMin = own.Min + new Vector2(border);
        var shapeMax = own.Max - new Vector2(border);
        float rounding = MathF.Max(0f, style.WindowRounding - border);
        var corners = own.TitleBarHeight > 0f ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersAll;
        uint color = ImGui.GetColorU32(UiSharedService.WithAlpha(UiSharedService.ThemeWindowBg, 1f));
        var drawList = ImGui.GetWindowDrawList();

        foreach (var (min, max) in _opaqueRegions)
        {
            drawList.PushClipRect(min, max, false);
            drawList.AddRectFilled(shapeMin, shapeMax, color, rounding, corners);
            drawList.PopClipRect();
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

        float titleBar = (window.Flags & ImGuiWindowFlags.NoTitleBar) != ImGuiWindowFlags.None ? 0f : ImGui.GetFrameHeight();
        lock (FootprintsLock)
            Footprints[this] = new WindowFootprint(window, titleBar);
    }

    protected virtual GlassLevel WindowGlassLevel => GlassLevel.Regular;

    protected virtual bool UseBackdropBlur => true;

    protected virtual int ContentBackgroundLayers => 1;

    public override void Draw()
    {
        RecordFootprint();
        _opaqueRegions.Clear();
        if (!Flags.HasFlag(ImGuiWindowFlags.NoBackground))
        {
            CollectOpaqueRegions(out var own);
            DrawOverlapBackdrop(own);
        }
        DrawWindowSheen();
        _performanceCollectorService.LogPerformance(this, $"Draw", DrawInternal);
        ScrollEdgeEffect.OpaqueRegions = _opaqueRegions;
        try
        {
            ScrollEdgeEffect.DrawForCurrentWindow(WindowGlassLevel, UseBackdropBlur, ContentBackgroundLayers);
        }
        finally
        {
            ScrollEdgeEffect.OpaqueRegions = null;
        }
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