using Dalamud.Bindings.ImGui;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.Housing;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI;

/// <summary>
/// Overlay affiché pendant la mise en place d'une scène PNJ. Le spawn d'un PNJ moddé prend plus d'une
/// seconde (collection Penumbra, draw, Glamourer) : sur une scène complète l'attente atteint la
/// dizaine de secondes, sans rien à l'écran pour indiquer que le plugin travaille.
/// </summary>
public sealed class HousingNpcApplyOverlayUi : WindowMediatorSubscriberBase
{
    private const float PanelWidth = 420f;
    private const float PanelHeight = 64f;
    private const float BarHeight = 20f;
    private const float BarMarginX = 16f;
    private const float BarMarginTop = 36f;
    private const float CornerRounding = 10f;
    private const float BarRounding = 10f;

    private readonly HousingNpcScenarioService _service;
    private readonly HousingShareManager _housingShareManager;

    public HousingNpcApplyOverlayUi(ILogger<HousingNpcApplyOverlayUi> logger, MareMediator mediator,
        HousingNpcScenarioService service, HousingShareManager housingShareManager,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, nameof(HousingNpcApplyOverlayUi), performanceCollectorService)
    {
        _service = service;
        _housingShareManager = housingShareManager;

        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        ForceMainWindow = true;
        IsOpen = true;

        Flags |= ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;

        // Masqué en GPose : la scène y est déjà figée et l'overlay gênerait la prise de vue.
        Mediator.Subscribe<GposeStartMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<GposeEndMessage>(this, (_) => IsOpen = true);
    }

    public override bool DrawConditions() => _service.ApplyProgress.Total > 0;

    protected override void DrawInternal()
    {
        var (done, total) = _service.ApplyProgress;
        // Relu ici : la valeur a pu retomber à zéro entre DrawConditions et le draw, et la division
        // du pourcentage donnerait un NaN.
        if (total <= 0) return;

        try
        {
            DrawOverlay(done, total);
        }
        catch
        {
            // ignore errors thrown from UI
        }
    }

    private void DrawOverlay(int done, int total)
    {
        var drawList = ImGui.GetBackgroundDrawList();
        var viewport = ImGui.GetMainViewport();
        var screenCenter = viewport.GetCenter();
        var screenSize = viewport.Size;

        // Les meubles chargent avant les PNJ à l'arrivée dans un logement, et les deux bandeaux
        // peuvent se chevaucher : on se décale sous celui des meubles tant qu'il occupe la place.
        float verticalOffset = _housingShareManager.IsBusy ? PanelHeight + 8f : 0f;

        var panelStart = new Vector2(
            screenCenter.X - PanelWidth / 2f,
            screenSize.Y * 0.6f + verticalOffset);
        var panelEnd = new Vector2(
            panelStart.X + PanelWidth,
            panelStart.Y + PanelHeight);

        drawList.AddRectFilled(panelStart, panelEnd,
            UiSharedService.Color(18, 16, 22, 210), CornerRounding);
        drawList.AddRect(panelStart, panelEnd,
            UiSharedService.Color(74, 54, 104, 180), CornerRounding, ImDrawFlags.RoundCornersAll, 1.5f);

        var title = Loc.Get("HousingNpc.ApplyOverlay.Title");
        var titleSize = ImGui.CalcTextSize(title);
        var titlePos = new Vector2(
            panelStart.X + (PanelWidth - titleSize.X) / 2f,
            panelStart.Y + 6f);
        drawList.AddText(titlePos with { X = titlePos.X + 1, Y = titlePos.Y + 1 },
            UiSharedService.Color(0, 0, 0, 200), title);
        drawList.AddText(titlePos,
            UiSharedService.Color(200, 160, 255, 240), title);

        var barStart = new Vector2(panelStart.X + BarMarginX, panelStart.Y + BarMarginTop);
        var barEnd = new Vector2(panelEnd.X - BarMarginX, barStart.Y + BarHeight);
        var barWidth = barEnd.X - barStart.X;

        drawList.AddRectFilled(barStart, barEnd,
            UiSharedService.Color(25, 22, 28, 220), BarRounding);

        var percent = Math.Clamp(done / (float)total, 0f, 1f);
        var progressWidth = percent * barWidth;
        if (progressWidth > 0.5f)
        {
            drawList.AddRectFilled(barStart, new Vector2(barStart.X + progressWidth, barEnd.Y),
                UiSharedService.Color(96, 74, 128, 220), BarRounding, ImDrawFlags.RoundCornersAll);
        }

        drawList.AddRectFilled(barStart, barStart with { X = barEnd.X, Y = barStart.Y + BarHeight * 0.45f },
            UiSharedService.Color(255, 255, 255, 14), BarRounding);

        var statusText = string.Format(CultureInfo.CurrentCulture,
            Loc.Get("HousingNpc.ApplyOverlay.Progress"), done, total);
        var statusSize = ImGui.CalcTextSize(statusText);
        var barCenterY = barStart.Y + (BarHeight - statusSize.Y) / 2f;

        var statusPos = new Vector2(barStart.X + 10f, barCenterY);
        drawList.AddText(statusPos with { X = statusPos.X + 1, Y = statusPos.Y + 1 },
            UiSharedService.Color(0, 0, 0, 200), statusText);
        drawList.AddText(statusPos,
            UiSharedService.Color(255, 255, 255, 220), statusText);

        var percentText = $"{(int)(percent * 100)}%";
        var percentSize = ImGui.CalcTextSize(percentText);
        var percentPos = new Vector2(barEnd.X - percentSize.X - 10f, barCenterY);
        drawList.AddText(percentPos with { X = percentPos.X + 1, Y = percentPos.Y + 1 },
            UiSharedService.Color(0, 0, 0, 200), percentText);
        drawList.AddText(percentPos,
            UiSharedService.Color(255, 255, 255, 200), percentText);

        if (percent >= 1.0f - float.Epsilon)
        {
            var time = (float)ImGui.GetTime();
            var center = new Vector2((barStart.X + barEnd.X) / 2f, (barStart.Y + barEnd.Y) / 2f);
            var pulse = MathF.Sin(time * 6.0f) * 0.5f + 0.5f;
            drawList.AddCircleFilled(center, 3f + pulse, UiSharedService.Color(255, 245, 220, 170));
            drawList.AddCircle(center, 5f + pulse * 2f, UiSharedService.Color(200, 160, 255, 75), 48, 2f);
        }
    }
}
