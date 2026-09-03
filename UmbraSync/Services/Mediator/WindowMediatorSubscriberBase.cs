using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using UmbraSync.UI;
using GlassLevel = UmbraSync.UI.UiSharedService.GlassLevel;

namespace UmbraSync.Services.Mediator;

public abstract class WindowMediatorSubscriberBase : Window, IMediatorSubscriber, IDisposable
{
    protected readonly ILogger _logger;
    private readonly PerformanceCollectorService _performanceCollectorService;

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

        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        // Rayons concentriques : le contenant est toujours plus arrondi que ce qu'il contient.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, UiSharedService.RadiusWindow);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, UiSharedService.RadiusCard);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, UiSharedService.RadiusCard);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, UiSharedService.RadiusControl);

        ImGui.PushStyleColor(ImGuiCol.WindowBg,
            UiSharedService.WithAlpha(UiSharedService.ThemeWindowBg, glass));
        // Éphémères et posés au-dessus de tout : le cas d'usage idéal du matériau.
        ImGui.PushStyleColor(ImGuiCol.PopupBg,
            UiSharedService.WithAlpha(UiSharedService.ThemeWindowBg, glass));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UiSharedService.ThemeChildBg);
        ImGui.PushStyleColor(ImGuiCol.Border, UiSharedService.ThemeBorder);
        ImGui.PushStyleColor(ImGuiCol.Separator, UiSharedService.ThemeSeparator);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, UiSharedService.WithAlpha(UiSharedService.ThemeTitleBar, glass));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, UiSharedService.WithAlpha(UiSharedService.ThemeTitleBar, glass));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, UiSharedService.WithAlpha(UiSharedService.ThemeTitleBar, glass));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, UiSharedService.ThemeFrameBg);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, UiSharedService.ThemeFrameBgHovered);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, UiSharedService.ThemeFrameBgActive);
        ImGui.PushStyleColor(ImGuiCol.Button, UiSharedService.ThemeButtonBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiSharedService.ThemeButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiSharedService.ThemeButtonActive);
        ImGui.PushStyleColor(ImGuiCol.Header, UiSharedService.ThemeHeaderBg);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, UiSharedService.ThemeHeaderHovered);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, UiSharedService.ThemeHeaderActive);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, UiSharedService.ThemeScrollbarBg);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, UiSharedService.ThemeScrollbarGrab);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, UiSharedService.ThemeScrollbarHover);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, UiSharedService.ThemeScrollbarActive);
        ImGui.PushStyleColor(ImGuiCol.Tab, UiSharedService.ThemeTabNormal);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, UiSharedService.ThemeTabHovered);
        ImGui.PushStyleColor(ImGuiCol.TabActive, UiSharedService.ThemeTabActive);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(UiSharedService.ThemeColorCount);
        ImGui.PopStyleVar(UiSharedService.ThemeStyleVarCount);
    }
    
    protected virtual GlassLevel WindowGlassLevel => GlassLevel.Opaque;

    public override void Draw()
    {
        DrawWindowSheen();
        _performanceCollectorService.LogPerformance(this, $"Draw", DrawInternal);
    }

    private void DrawWindowSheen()
    {
        if (WindowGlassLevel == GlassLevel.Opaque) return;
        if (Flags.HasFlag(ImGuiWindowFlags.NoBackground)) return;

        float alpha = UiSharedService.GlassAlpha(UiSharedService.ResolveGlassLevel(WindowGlassLevel));
        if (alpha >= 1f) return;

        var pos = ImGui.GetWindowPos();
        UiSharedService.DrawGlassSheen(
            ImGui.GetWindowDrawList(),
            pos,
            pos + ImGui.GetWindowSize(),
            UiSharedService.RadiusWindow,
            alpha);
    }

    protected abstract void DrawInternal();

    public virtual Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected virtual void Dispose(bool disposing)
    {
        _logger.LogTrace("Disposing {type}", GetType());

        Mediator.UnsubscribeAll(this);
    }
}