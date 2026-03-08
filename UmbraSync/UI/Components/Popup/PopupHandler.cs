using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI.Components.Popup;

public class PopupHandler : WindowMediatorSubscriberBase
{
    protected bool _openPopup = false;
    private readonly UiSharedService _uiSharedService;
    private IPopupHandler? _currentHandler = null;

    public PopupHandler(ILogger<PopupHandler> logger, MareMediator mediator, IEnumerable<IPopupHandler> popupHandlers,
        PerformanceCollectorService performanceCollectorService, UiSharedService uiSharedService)
        : base(logger, mediator, "MarePopupHandler", performanceCollectorService)
    {
        Flags = ImGuiWindowFlags.NoBringToFrontOnFocus
          | ImGuiWindowFlags.NoDecoration
          | ImGuiWindowFlags.NoInputs
          | ImGuiWindowFlags.NoSavedSettings
          | ImGuiWindowFlags.NoBackground
          | ImGuiWindowFlags.NoMove
          | ImGuiWindowFlags.NoNav
          | ImGuiWindowFlags.NoTitleBar
          | ImGuiWindowFlags.NoFocusOnAppearing;

        IsOpen = true;

        var handlers = popupHandlers.ToHashSet();

        Mediator.Subscribe<OpenReportPopupMessage>(this, (msg) =>
        {
            _openPopup = true;
            _currentHandler = handlers.OfType<ReportPopupHandler>().Single();
            ((ReportPopupHandler)_currentHandler).Open(msg);
            IsOpen = true;
        });

        Mediator.Subscribe<OpenBanUserPopupMessage>(this, (msg) =>
        {
            _openPopup = true;
            _currentHandler = handlers.OfType<BanUserPopupHandler>().Single();
            ((BanUserPopupHandler)_currentHandler).Open(msg);
            IsOpen = true;
        });

        Mediator.Subscribe<OpenSlotPromptMessage>(this, (msg) =>
        {
            _openPopup = true;
            _currentHandler = handlers.OfType<SlotPopupHandler>().Single();
            ((SlotPopupHandler)_currentHandler).Open(msg);
            IsOpen = true;
        });
        _uiSharedService = uiSharedService;
        DisableWindowSounds = true;
    }

    protected override void DrawInternal()
    {
        if (_currentHandler == null) return;

        if (_openPopup)
        {
            ImGui.OpenPopup(WindowName);
            _openPopup = false;
        }

        var viewportSize = ImGui.GetWindowViewport().Size;
        ImGui.SetNextWindowSize(_currentHandler!.PopupSize * ImGuiHelpers.GlobalScale);
        ImGui.SetNextWindowPos(viewportSize / 2, ImGuiCond.Always, new Vector2(0.5f));
        using var popup = ImRaii.Popup(WindowName, ImGuiWindowFlags.Modal);
        if (!popup) return;
        _currentHandler.DrawContent();
        if (_currentHandler.ShowClose)
        {
            ImGui.Separator();
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, Loc.Get("PopupHandler.CloseButton")))
            {
                ImGui.CloseCurrentPopup();
            }
        }
    }
}