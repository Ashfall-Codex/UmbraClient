using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Reflection;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.PlayerData.Factories;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.UI.Components;

namespace UmbraSync.UI;

// Tour de nouveautés affiché une fois par version majeure.minor à un joueur déjà installé.
// Il embarque la page de consentement : si le consentement est absent ou périmé, cette page est bloquante
// et les services ne démarrent qu'une fois l'accord donné et la connexion établie.
public sealed class WhatsNewUi : WindowMediatorSubscriberBase
{
    private const int PageCount = 7;
    private const int RgpdPage = 1;
    private const int LastPage = PageCount - 1;

    private static readonly Vector2 WindowSize = new(700, 650);

    private readonly UiSharedService _uiShared;
    private readonly MareConfigService _configService;
    private readonly RgpdDataService _rgpdDataService;
    private readonly RgpdConsentPanel _rgpdPanel;
    private readonly EditProfileUi _editProfileUi;
    private readonly UmbraProfileManager _umbraProfileManager;
    private readonly PairManager _pairManager;
    private readonly PairFactory _pairFactory;
    private readonly PenumbraPrecacheService _precacheService;

    private const string ProfileLoadingMarker = "Loading Data from server...";
    private static readonly TimeSpan CardPreviewTimeout = TimeSpan.FromSeconds(10);

    private static readonly RpProfileVisibility[] RpVisibilityOrder =
    [
        RpProfileVisibility.Hidden,
        RpProfileVisibility.PairsOnly,
        RpProfileVisibility.PairsAndSyncshell,
        RpProfileVisibility.Public,
    ];

    private int _page;
    private DateTime _cardPreviewRequestedAt = DateTime.MinValue;
    private int _servicesPending;

    public WhatsNewUi(ILogger<WhatsNewUi> logger, UiSharedService uiShared, MareConfigService configService,
        RgpdDataService rgpdDataService, EditProfileUi editProfileUi, UmbraProfileManager umbraProfileManager,
        PairManager pairManager, PairFactory pairFactory, PenumbraPrecacheService precacheService, MareMediator mediator, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, Loc.Get("WhatsNew.WindowTitle") + "###UmbraSyncWhatsNew", performanceCollectorService)
    {
        _uiShared = uiShared;
        _configService = configService;
        _rgpdDataService = rgpdDataService;
        _rgpdPanel = new RgpdConsentPanel(uiShared, rgpdDataService);
        _editProfileUi = editProfileUi;
        _umbraProfileManager = umbraProfileManager;
        _pairManager = pairManager;
        _pairFactory = pairFactory;
        _precacheService = precacheService;

        IsOpen = false;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Flags |= ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = WindowSize,
            MaximumSize = WindowSize,
        };
        SizeCondition = ImGuiCond.Always;
        PositionCondition = ImGuiCond.Appearing;

        Mediator.Subscribe<SwitchToWhatsNewUiMessage>(this, OnSwitchToWhatsNew);
        // Les services attendent le consentement : on les lance dès qu'il est donné.
        Mediator.Subscribe<RgpdConsentUpdatedMessage>(this, (msg) =>
        {
            if (msg.ConsentGiven) TryLaunchServices();
        });
    }

    private void OnSwitchToWhatsNew(SwitchToWhatsNewUiMessage message)
    {
        if (message.ServicesPending)
            Interlocked.Exchange(ref _servicesPending, 1);

        // Relance des services pendant le tour : on ne rouvre pas et on ne remet rien à zéro.
        if (IsOpen) return;
        if (!_configService.Current.HasPendingWhatsNewTour()) return;

        _page = 0;
        if (_rgpdDataService.IsRgpdConsentOutdated)
            _rgpdPanel.PrefillOptionalFromCurrent();
        IsOpen = true;
    }

    private void TryLaunchServices()
    {
        if (Volatile.Read(ref _servicesPending) == 0 || !_rgpdDataService.IsRgpdConsentValid) return;
        if (Interlocked.Exchange(ref _servicesPending, 0) == 0) return;

        Mediator.Publish(new SwitchToMainUiMessage());
    }

    public override void OnOpen()
    {
        var viewport = ImGui.GetMainViewport();
        float scale = ImGuiHelpers.GlobalScale;
        Position = new Vector2(
            viewport.WorkPos.X + (viewport.WorkSize.X - WindowSize.X * scale) / 2f,
            viewport.WorkPos.Y + (viewport.WorkSize.Y - WindowSize.Y * scale) / 2f);
    }

    protected override void DrawInternal()
    {
        if (_uiShared.IsInGpose) return;

        TryShowCardPreview();

        float availWidth = ImGui.GetContentRegionAvail().X;
        float footerHeight = ImGui.GetFrameHeightWithSpacing() * 2f + 14f * ImGuiHelpers.GlobalScale;

        using (var content = ImRaii.Child("##whatsnew_content", new Vector2(0, -footerHeight), false))
        {
            if (content)
            {
                switch (_page)
                {
                    case 0: DrawWelcome(availWidth); break;
                    case 1: DrawRgpd(availWidth); break;
                    case 2: DrawProfiles(availWidth); break;
                    case 3: DrawMeetings(availWidth); break;
                    case 4: DrawSync(availWidth); break;
                    case 5: DrawInterface(availWidth); break;
                    default: DrawDone(availWidth); break;
                }
                UiSharedService.EndSectionCard();
            }
        }

        DrawFooter(availWidth);
    }

    private void DrawWelcome(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.Star, Loc.Get("WhatsNew.Welcome.Title"),
            Loc.Get("WhatsNew.Welcome.Subtitle"), availWidth);

        UiSharedService.TextWrapped(Loc.Get("WhatsNew.Welcome.Intro"));
        if (!_rgpdDataService.IsRgpdConsentValid)
        {
            ImGuiHelpers.ScaledDummy(4f);
            UiSharedService.ColorTextWrapped(Loc.Get("WhatsNew.Welcome.RgpdNotice"), ImGuiColors.DalamudYellow);
        }

        ImGuiHelpers.ScaledDummy(4f);

        WizardControls.DrawField(FontAwesomeIcon.IdCard, Loc.Get("WhatsNew.Welcome.Profiles.Title"), Loc.Get("WhatsNew.Welcome.Profiles.Desc"));
        WizardControls.DrawField(FontAwesomeIcon.UserFriends, Loc.Get("WhatsNew.Welcome.Meetings.Title"), Loc.Get("WhatsNew.Welcome.Meetings.Desc"));
        WizardControls.DrawField(FontAwesomeIcon.Sync, Loc.Get("WhatsNew.Welcome.Sync.Title"), Loc.Get("WhatsNew.Welcome.Sync.Desc"));
        WizardControls.DrawField(FontAwesomeIcon.Home, Loc.Get("WhatsNew.Welcome.Housing.Title"), Loc.Get("WhatsNew.Welcome.Housing.Desc"));
        WizardControls.DrawField(FontAwesomeIcon.PaintBrush, Loc.Get("WhatsNew.Welcome.Interface.Title"), Loc.Get("WhatsNew.Welcome.Interface.Desc"));
    }

    private void DrawRgpd(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.ShieldAlt, Loc.Get("WhatsNew.Rgpd.Title"),
            Loc.Get("WhatsNew.Rgpd.Subtitle"), availWidth);

        if (_rgpdDataService.IsRgpdConsentValid)
        {
            DrawRgpdRecap();
            return;
        }

        UiSharedService.BeginSectionCard(Loc.Get("Rgpd.Consent.Title"), FontAwesomeIcon.ShieldAlt);
        _rgpdPanel.Draw(drawTitle: false);
    }

    // Consentement déjà donné : on rappelle ce qui est actif plutôt que de redemander quoi que ce soit.
    private void DrawRgpdRecap()
    {
        UiSharedService.BeginSectionCard(Loc.Get("Rgpd.Consent.Title"), FontAwesomeIcon.ShieldAlt);
        UiSharedService.ColorTextWrapped(Loc.Get("WhatsNew.Rgpd.Accepted"), ImGuiColors.HealerGreen);
        ImGuiHelpers.ScaledDummy(4f);
        UiSharedService.TextWrapped(Loc.Get("Settings.Privacy.Optional.Intro"));
        ImGuiHelpers.ScaledDummy(2f);

        var choices = _rgpdDataService.CurrentOptionalChoices;
        DrawOptionalState("Rgpd.Consent.Optional.Nearby", choices.NearbyDiscovery);
        DrawOptionalState("Rgpd.Consent.Optional.Position", choices.ProximityPosition);
        DrawOptionalState("Rgpd.Consent.Optional.Typing", choices.TypingIndicator);
        DrawOptionalState("Rgpd.Consent.Optional.Plugins", choices.PluginSharing);

        ImGuiHelpers.ScaledDummy(4f);
        UiSharedService.ColorTextWrapped(Loc.Get("WhatsNew.Rgpd.AcceptedHint"), ImGuiColors.DalamudGrey);

        UiSharedService.BeginSectionCard(Loc.Get("Rgpd.Consent.Rights"), FontAwesomeIcon.UserShield);
        UiSharedService.TextWrapped(Loc.Get("Rgpd.Consent.Rights.Summary"));
    }

    private static void DrawOptionalState(string key, bool enabled)
    {
        ImGui.TextUnformatted(Loc.Get(key));
        ImGui.SameLine();
        UiSharedService.ColorText(
            Loc.Get(enabled ? "Settings.Privacy.Optional.On" : "Settings.Privacy.Optional.Off"),
            enabled ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey);
    }

    private void DrawProfiles(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.IdCard, Loc.Get("WhatsNew.Profiles.Title"),
            Loc.Get("WhatsNew.Profiles.Subtitle"), availWidth);

        UiSharedService.BeginSectionCard(Loc.Get("WhatsNew.Profiles.Banner.Title"), FontAwesomeIcon.Image);
        UiSharedService.TextWrapped(Loc.Get("WhatsNew.Profiles.Banner.Desc"));
        ImGuiHelpers.ScaledDummy(2f);
        if (_uiShared.IconTextButton(FontAwesomeIcon.UserEdit, Loc.Get("WhatsNew.Profiles.OpenEditor")))
            _editProfileUi.IsOpen = true;

        UiSharedService.BeginSectionCard(Loc.Get("WhatsNew.Profiles.Preview.Title"), FontAwesomeIcon.Eye);
        UiSharedService.TextWrapped(Loc.Get("WhatsNew.Profiles.Preview.Desc"));
        ImGuiHelpers.ScaledDummy(2f);
        if (_uiShared.IconTextButton(FontAwesomeIcon.IdCard, Loc.Get("EditProfile.Banner.Preview")))
            _cardPreviewRequestedAt = DateTime.UtcNow;
        ImGui.SameLine();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Eye, Loc.Get("EditProfile.PreviewButton")))
            OpenProfilePreview();

        WizardControls.DrawField(FontAwesomeIcon.User, Loc.Get("WhatsNew.Profiles.PerCharacter.Title"), Loc.Get("WhatsNew.Profiles.PerCharacter.Desc"));

        UiSharedService.BeginSectionCard(Loc.Get("WhatsNew.Profiles.Visibility.Title"), FontAwesomeIcon.UserLock);
        UiSharedService.TextWrapped(Loc.Get("WhatsNew.Profiles.Visibility.Desc"));
        ImGuiHelpers.ScaledDummy(2f);
        DrawVisibilitySelector();
    }

    // L'aperçu montre le profil enregistré, sans ouvrir l'éditeur. Le profil peut ne pas être encore
    // en mémoire : le premier appel déclenche le chargement, on réessaie à chaque image jusqu'au délai.
    private void TryShowCardPreview()
    {
        if (_cardPreviewRequestedAt == DateTime.MinValue) return;

        var profile = _umbraProfileManager.GetUmbraProfile(GetOwnUserData());
        if (string.Equals(profile.Description, ProfileLoadingMarker, StringComparison.Ordinal)
            && DateTime.UtcNow - _cardPreviewRequestedAt < CardPreviewTimeout)
            return;

        _cardPreviewRequestedAt = DateTime.MinValue;
        Mediator.Publish(new TargetProfilePreviewMessage(profile));
    }

    private void OpenProfilePreview()
    {
        var me = GetOwnUserData();
        var pair = _pairManager.GetPairByUID(me.UID) ?? _pairFactory.Create(me);
        Mediator.Publish(new ProfileOpenStandaloneMessage(pair));
    }

    private UserData GetOwnUserData() => new(_uiShared.ApiController.UID, _uiShared.ApiController.DisplayName);

    private void DrawVisibilitySelector()
    {
        string[] labels =
        [
            Loc.Get("UserProfile.RpVisibility.Hidden"),
            Loc.Get("UserProfile.RpVisibility.PairsOnly"),
            Loc.Get("UserProfile.RpVisibility.PairsAndSyncshell"),
            Loc.Get("UserProfile.RpVisibility.Public"),
        ];

        int index = Array.IndexOf(RpVisibilityOrder, _editProfileUi.GetRpVisibility());
        if (index < 0) index = Array.IndexOf(RpVisibilityOrder, RpProfileVisibility.PairsAndSyncshell);

        ImGui.SetNextItemWidth(MathF.Min(260f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X));
        if (UiSharedService.Combo("##whatsnew_rpVisibility", ref index, labels, labels.Length))
            _editProfileUi.SetRpVisibility(RpVisibilityOrder[index]);
    }

    private static void DrawMeetings(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.UserFriends, Loc.Get("WhatsNew.Meetings.Title"),
            Loc.Get("WhatsNew.Meetings.Subtitle"), availWidth);

        WizardControls.DrawField(FontAwesomeIcon.UserCheck, Loc.Get("WhatsNew.Meetings.Accept.Title"), Loc.Get("WhatsNew.Meetings.Accept.Desc"));
        WizardControls.DrawField(FontAwesomeIcon.Ban, Loc.Get("WhatsNew.Meetings.Block.Title"), Loc.Get("WhatsNew.Meetings.Block.Desc"));
        WizardControls.DrawField(FontAwesomeIcon.Wifi, Loc.Get("WhatsNew.Meetings.Nearby.Title"), Loc.Get("WhatsNew.Meetings.Nearby.Desc"));
    }

    private void DrawSync(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.Sync, Loc.Get("WhatsNew.Sync.Title"),
            Loc.Get("WhatsNew.Sync.Subtitle"), availWidth);

        WizardControls.DrawField(FontAwesomeIcon.CloudDownloadAlt, Loc.Get("WhatsNew.Sync.Reliable.Title"), Loc.Get("WhatsNew.Sync.Reliable.Desc"));
        WizardControls.DrawField(FontAwesomeIcon.FolderOpen, Loc.Get("WhatsNew.Sync.Mcdf.Title"), Loc.Get("WhatsNew.Sync.Mcdf.Desc"));
        WizardControls.DrawField(FontAwesomeIcon.Home, Loc.Get("WhatsNew.Sync.Housing.Title"), Loc.Get("WhatsNew.Sync.Housing.Desc"));

        UiSharedService.BeginSectionCard(Loc.Get("WhatsNew.Sync.Precache.Title"), FontAwesomeIcon.CloudUploadAlt);
        UiSharedService.TextWrapped(Loc.Get("WhatsNew.Sync.Precache.Desc"));
        ImGuiHelpers.ScaledDummy(2f);

        bool running = _precacheService.IsUploading;
        bool connected = _uiShared.ApiController.IsConnected;
        using (ImRaii.Disabled(running || !connected))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.CloudUploadAlt, Loc.Get("Settings.Transfer.Precache.RunNow")))
                _precacheService.TriggerManualPrecache(ignoreEnableSetting: true);
        }
        if (!connected && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.Get("WhatsNew.Sync.Precache.NeedsConnection"));

        var status = running
            ? Loc.Get("Settings.Transfer.Precache.Status.Running")
            : Loc.Get("Settings.Transfer.Precache.Status.Idle");
        long bytes = _precacheService.BytesUploadedThisRun;
        ImGui.SameLine();
        ImGui.TextUnformatted(bytes > 0
            ? string.Format(Loc.CurrentCulture, Loc.Get("Settings.Transfer.Precache.Status.WithBytes"), status, UiSharedService.ByteToString(bytes))
            : string.Format(Loc.CurrentCulture, Loc.Get("Settings.Transfer.Precache.Status"), status));

        if (!string.IsNullOrEmpty(_precacheService.StatusText))
            UiSharedService.ColorTextWrapped(_precacheService.StatusText, ImGuiColors.DalamudGrey);

        // Le tour ne lance jamais rien de lui-même : l'envoi automatique n'existe que si le joueur l'avait déjà activé.
        UiSharedService.ColorTextWrapped(
            Loc.Get(_configService.Current.EnablePenumbraPrecache ? "WhatsNew.Sync.Precache.AutoOn" : "WhatsNew.Sync.Precache.AutoOff"),
            ImGuiColors.DalamudGrey);
    }

    private void DrawInterface(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.PaintBrush, Loc.Get("WhatsNew.Interface.Title"),
            Loc.Get("WhatsNew.Interface.Subtitle"), availWidth);

        WizardControls.DrawField(FontAwesomeIcon.Palette, Loc.Get("WhatsNew.Interface.Style.Title"), Loc.Get("WhatsNew.Interface.Style.Desc"));

        // Mêmes réglages que Paramètres > Général > Apparence : un seul curseur règle transparence et flou.
        UiSharedService.BeginSectionCard(Loc.Get("WhatsNew.Interface.Glass.Title"), FontAwesomeIcon.Adjust);
        UiSharedService.TextWrapped(Loc.Get("WhatsNew.Interface.Glass.Desc"));
        ImGuiHelpers.ScaledDummy(2f);

        ImGui.TextUnformatted(Loc.Get("Settings.General.UiGlass"));
        var uiGlass = _configService.Current.UiGlass;
        bool released = GlassSlider.Draw("##whatsnew_ui_glass", ref uiGlass, 260f * ImGuiHelpers.GlobalScale,
            Loc.Get("Settings.General.UiGlass.Tooltip"));
        _configService.Current.UiGlass = uiGlass;
        if (released) _configService.Save();

        var clearInGpose = _configService.Current.UiClearGlassInGpose;
        if (ToggleSwitch.Draw(Loc.Get("Settings.General.ClearGlassInGpose") + "##whatsnew_clear_gpose", ref clearInGpose))
        {
            _configService.Current.UiClearGlassInGpose = clearInGpose;
            _configService.Save();
        }

        WizardControls.DrawField(FontAwesomeIcon.Search, Loc.Get("WhatsNew.Interface.Settings.Title"), Loc.Get("WhatsNew.Interface.Settings.Desc"));
    }

    private void DrawDone(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.CheckCircle, Loc.Get("WhatsNew.Done.Title"),
            Loc.Get("WhatsNew.Done.Subtitle"), availWidth);

        UiSharedService.BeginSectionCard(Loc.Get("WhatsNew.Done.Recap.Title"), FontAwesomeIcon.ClipboardCheck);
        DrawRecapLine(_rgpdDataService.IsRgpdConsentValid, Loc.Get("WhatsNew.Done.Recap.Consent"));
        bool connected = _uiShared.ApiController.IsConnected;
        DrawRecapLine(connected, Loc.Get(connected ? "WhatsNew.Done.Recap.Connected" : "WhatsNew.Done.Recap.Connecting"));
        DrawRecapLine(true, string.Format(Loc.CurrentCulture, Loc.Get("WhatsNew.Done.Recap.Visibility"),
            GetVisibilityLabel(_editProfileUi.GetRpVisibility())));

        UiSharedService.BeginSectionCard(Loc.Get("WhatsNew.Done.Next.Title"), FontAwesomeIcon.Compass);
        UiSharedService.TextWrapped(Loc.Get("WhatsNew.Done.Intro"));
        ImGuiHelpers.ScaledDummy(2f);
        if (_uiShared.IconTextButton(FontAwesomeIcon.ListAlt, Loc.Get("WhatsNew.Done.Changelog")))
            Mediator.Publish(new OpenChangelogUiMessage());

        ImGuiHelpers.ScaledDummy(6f);
        UiSharedService.TextWrapped(Loc.Get("WhatsNew.Done.Settings.Desc"));
        ImGuiHelpers.ScaledDummy(2f);
        if (_uiShared.IconTextButton(FontAwesomeIcon.Cog, Loc.Get("WhatsNew.Done.Settings")))
            Mediator.Publish(new OpenSettingsUiMessage());
    }

    private void DrawRecapLine(bool done, string text)
    {
        _uiShared.IconText(done ? FontAwesomeIcon.Check : FontAwesomeIcon.Clock,
            done ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow);
        ImGui.SameLine();
        ImGui.TextUnformatted(text);
    }

    private static string GetVisibilityLabel(RpProfileVisibility visibility) => Loc.Get(visibility switch
    {
        RpProfileVisibility.Hidden => "UserProfile.RpVisibility.Hidden",
        RpProfileVisibility.PairsOnly => "UserProfile.RpVisibility.PairsOnly",
        RpProfileVisibility.Public => "UserProfile.RpVisibility.Public",
        _ => "UserProfile.RpVisibility.PairsAndSyncshell",
    });

    private void DrawFooter(float availWidth)
    {
        WizardControls.DrawStepDots(PageCount, _page, availWidth);
        ImGuiHelpers.ScaledDummy(4f);

        float buttonWidth = 120f * ImGuiHelpers.GlobalScale;
        bool consentValid = _rgpdDataService.IsRgpdConsentValid;

        if (_page > 0)
        {
            if (ImGui.Button(Loc.Get("WhatsNew.Nav.Previous") + "##whatsnew_prev", new Vector2(buttonWidth, 0)))
                _page--;
        }
        else if (ImGui.Button(Loc.Get("WhatsNew.Nav.Skip") + "##whatsnew_skip", new Vector2(buttonWidth, 0)))
        {
            // Le tour est facultatif, le consentement ne l'est pas : on saute directement à sa page.
            if (consentValid)
                Finish();
            else
                _page = RgpdPage;
        }

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - buttonWidth);

        bool blocked = _page == RgpdPage && !consentValid;
        if (_page < LastPage)
        {
            using (ImRaii.Disabled(blocked))
            {
                if (ImGui.Button(Loc.Get("WhatsNew.Nav.Next") + "##whatsnew_next", new Vector2(buttonWidth, 0)))
                    _page++;
            }
            if (blocked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(Loc.Get("WhatsNew.Nav.RgpdRequired"));
        }
        else if (ImGui.Button(Loc.Get("WhatsNew.Nav.Finish") + "##whatsnew_finish", new Vector2(buttonWidth, 0)))
        {
            Finish();
        }
    }

    private void Finish()
    {
        var config = _configService.Current;
        config.WhatsNewTourSeen = MareConfig.CurrentWhatsNewTourVersion;
        // Le tour tient lieu de présentation de la version : le journal ne s'ouvre pas derrière.
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
            config.LastChangelogVersionSeen = version.ToString();
        _configService.Save();

        IsOpen = false;
        TryLaunchServices();
    }
}
