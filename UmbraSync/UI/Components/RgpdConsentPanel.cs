using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using UmbraSync.Localization;
using UmbraSync.Services;

namespace UmbraSync.UI.Components;

// Contenu de la page de consentement RGPD, partagé entre l'assistant d'installation et le tour de nouveautés.
// Rien n'est coché d'avance : un consentement pré-rempli n'en est pas un. Seule exception, demandée
// explicitement par l'appelant : reprendre les réglages déjà en place d'un joueur qui renouvelle son accord.
public sealed class RgpdConsentPanel
{
    private readonly UiSharedService _uiShared;
    private readonly RgpdDataService _rgpdDataService;

    private bool _consentMandatory;
    private bool _optionNearby;
    private bool _optionPosition;
    private bool _optionTyping;
    private bool _optionPlugins;
    private bool _optionalPrefilled;

    public RgpdConsentPanel(UiSharedService uiShared, RgpdDataService rgpdDataService)
    {
        _uiShared = uiShared;
        _rgpdDataService = rgpdDataService;
    }

    // Reprend les traitements facultatifs actuellement actifs. Le consentement obligatoire reste à cocher.
    public void PrefillOptionalFromCurrent()
    {
        var current = _rgpdDataService.CurrentOptionalChoices;
        _optionNearby = current.NearbyDiscovery;
        _optionPosition = current.ProximityPosition;
        _optionTyping = current.TypingIndicator;
        _optionPlugins = current.PluginSharing;
        _optionalPrefilled = true;
    }

    public void Draw(bool drawTitle = true)
    {
        if (drawTitle)
        {
            using (_uiShared.UidFont.Push())
                ImGui.TextUnformatted(Loc.Get("Rgpd.Consent.Title"));

            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(4f);
        }

        if (_rgpdDataService.IsRgpdConsentOutdated)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.Outdated"), ImGuiColors.DalamudYellow);
            ImGuiHelpers.ScaledDummy(4f);
        }

        UiSharedService.TextWrapped(Loc.Get("Rgpd.Consent.Intro"));
        ImGuiHelpers.ScaledDummy(2f);
        UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.Reassurance"), ImGuiColors.HealerGreen);
        ImGuiHelpers.ScaledDummy(4f);

        _uiShared.BigText(Loc.Get("Rgpd.Consent.DataCollected.Header"));
        UiSharedService.TextWrapped(Loc.Get("Rgpd.Consent.DataCollected.Details"));
        ImGuiHelpers.ScaledDummy(4f);
        UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.ConnectNotice"), ImGuiColors.DalamudGrey3);
        ImGuiHelpers.ScaledDummy(4f);

        UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.ThirdParties"), ImGuiColors.DalamudGrey3);
        ImGuiHelpers.ScaledDummy(4f);

        ImGui.Checkbox(Loc.Get("Rgpd.Consent.Mandatory"), ref _consentMandatory);
        UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.Mandatory.Detail"), ImGuiColors.DalamudGrey3);

        ImGuiHelpers.ScaledDummy(6f);
        _uiShared.BigText(Loc.Get("Rgpd.Consent.Optional.Header"));
        UiSharedService.TextWrapped(Loc.Get("Rgpd.Consent.Optional.Intro"));
        if (_optionalPrefilled)
            UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.Optional.Prefilled"), ImGuiColors.DalamudYellow);
        ImGuiHelpers.ScaledDummy(2f);

        DrawOption("Rgpd.Consent.Optional.Nearby", ref _optionNearby);
        DrawOption("Rgpd.Consent.Optional.Position", ref _optionPosition);
        DrawOption("Rgpd.Consent.Optional.Typing", ref _optionTyping);
        DrawOption("Rgpd.Consent.Optional.Plugins", ref _optionPlugins);

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();

        _uiShared.BigText(Loc.Get("Rgpd.Consent.Rights"));
        UiSharedService.TextWrapped(Loc.Get("Rgpd.Consent.Rights.Summary"));
        ImGuiHelpers.ScaledDummy(4f);
        UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.Retention"), ImGuiColors.DalamudGrey3);
        ImGuiHelpers.ScaledDummy(2f);
        UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.Controller"), ImGuiColors.DalamudGrey3);
        ImGuiHelpers.ScaledDummy(4f);

        using (ImRaii.Disabled(!_consentMandatory))
        {
            if (ImGui.Button(Loc.Get("Rgpd.Consent.AcceptButton")))
            {
                _rgpdDataService.AcceptRgpdConsent(new RgpdDataService.OptionalChoices(
                    _optionNearby, _optionPosition, _optionTyping, _optionPlugins));
            }
        }

        if (!_consentMandatory)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.MandatoryWarning"), ImGuiColors.DalamudYellow);
        }
    }

    private static void DrawOption(string key, ref bool value)
    {
        ToggleSwitch.Draw(Loc.Get(key), ref value);
        UiSharedService.ColorTextWrapped(Loc.Get(key + ".Detail"), ImGuiColors.DalamudGrey3);
        ImGuiHelpers.ScaledDummy(2f);
    }
}
