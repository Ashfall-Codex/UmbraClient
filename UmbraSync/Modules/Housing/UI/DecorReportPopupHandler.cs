using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Dto.DecorCatalog;
using UmbraSync.Localization;
using UmbraSync.Modules.Housing.Services;
using UmbraSync.UI;
using UmbraSync.UI.Components.Popup;

namespace UmbraSync.Modules.Housing.UI;

public sealed class DecorReportPopupHandler : IPopupHandler
{
    private readonly DecorCatalogManager _manager;
    private readonly UiSharedService _uiSharedService;
    private DecorCatalogEntryDto? _entry;
    private string _reason = string.Empty;

    public DecorReportPopupHandler(DecorCatalogManager manager, UiSharedService uiSharedService)
    {
        _manager = manager;
        _uiSharedService = uiSharedService;
    }

    public Vector2 PopupSize => new(500, 420);

    public bool ShowClose => true;

    public void Open(DecorCatalogEntryDto entry)
    {
        _entry = entry;
        _reason = string.Empty;
    }

    public void DrawContent()
    {
        if (_entry == null) return;

        using (_uiSharedService.UidFont.Push())
            UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("DecorCatalog.Report.Title"), _entry.Name));

        ImGui.InputTextMultiline("##decorReportReason", ref _reason, DecorCatalogManager.MaxReasonLength,
            new Vector2(500f * ImGuiHelpers.GlobalScale - ImGui.GetStyle().ItemSpacing.X * 2, 180f * ImGuiHelpers.GlobalScale));
        UiSharedService.TextWrapped(Loc.Get("DecorCatalog.Report.Note"));
        UiSharedService.ColorTextWrapped(Loc.Get("ReportPopup.SpamWarning"), UiSharedService.AccentColor);

        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_reason)))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.ExclamationTriangle, Loc.Get("DecorCatalog.Report.Submit")))
            {
                ImGui.CloseCurrentPopup();
                _ = _manager.ReportAsync(_entry.Id, _reason);
            }
        }
    }
}
