using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using UmbraSync.API.Dto.Account;
using UmbraSync.FileCache;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI.Components;
using UmbraSync.WebAPI;
using UmbraSync.WebAPI.SignalR.Utils;

namespace UmbraSync.UI;

// Assistant de premier lancement : une chose par page, un seul bouton principal en bas à droite,
// retour possible, et une phrase qui dit ce qu'il reste à faire quand « Continuer » est grisé.
// L'avancement reste déduit de la configuration : fermer le jeu en cours de route reprend à la bonne page.
public partial class IntroUi : WindowMediatorSubscriberBase
{
    private enum Mode { Setup, RgpdOnly }

    private enum Step { Welcome, Prerequisites, Terms, Data, Storage, Account, Done }

    private const int StepCount = 7;
    private static readonly Vector2 WindowSize = new(700, 650);

#if DEBUG
    private static readonly TimeSpan TermsDelay = TimeSpan.Zero;
#else
    private static readonly TimeSpan TermsDelay = TimeSpan.FromSeconds(10);
#endif

    private readonly MareConfigService _configService;
    private readonly CacheMonitor _cacheMonitor;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly AccountRegistrationService _registerService;
    private readonly RgpdDataService _rgpdDataService;
    private readonly UiSharedService _uiShared;
    private readonly RgpdConsentPanel _rgpdPanel;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private Mode _mode = Mode.Setup;
    private Step _step = Step.Welcome;
    // Vrai dès que l'accord n'avait pas encore été donné : une installation neuve n'a pas à voir
    // le tour de nouveautés ni le journal de la version qu'elle vient d'installer.
    private bool _freshInstall;
    private bool _connectionHandled;
    private bool _awaitingFinish;
    private bool _rgpdOnlyLaunched;
    private DateTime _termsShownAt = DateTime.MinValue;
    private readonly bool _isTestBuild;
    private readonly string _versionLabel;

    private string _secretKey = string.Empty;
    private bool _registrationInProgress;
    private bool _registrationSuccess;
    private string? _registrationMessage;
    private RegisterReplyDto? _registrationReply;

    public IntroUi(ILogger<IntroUi> logger, UiSharedService uiShared, MareConfigService configService,
        CacheMonitor fileCacheManager, ServerConfigurationManager serverConfigurationManager, MareMediator mareMediator,
        PerformanceCollectorService performanceCollectorService, DalamudUtilService dalamudUtilService, AccountRegistrationService registerService,
        RgpdDataService gdprDataService, IDalamudPluginInterface pluginInterface) : base(logger, mareMediator, Loc.Get("CompactUi.IntroUi.WindowTitle") + "###UmbraSyncIntro", performanceCollectorService)
    {
        _uiShared = uiShared;
        _configService = configService;
        _cacheMonitor = fileCacheManager;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtilService = dalamudUtilService;
        _registerService = registerService;
        _rgpdDataService = gdprDataService;
        _rgpdPanel = new RgpdConsentPanel(uiShared, gdprDataService);
        _isTestBuild = pluginInterface.IsDev || pluginInterface.IsTesting;
        _versionLabel = (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0)).ToString();

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

        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) =>
        {
            // Pendant l'installation, c'est « Commencer » qui ferme l'assistant, pas le lancement des services.
            if (IsOpen && _mode == Mode.Setup && _awaitingFinish) return;
            IsOpen = false;
        });
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) =>
        {
            // Installation déjà configurée (consentement retiré) : seule la page de consentement est utile.
            if (_configService.Current.HasValidSetup() && _serverConfigurationManager.HasValidConfig())
            {
                OpenRgpdOnly();
                return;
            }

            _configService.Current.UseCompactor = !dalamudUtilService.IsWine;
            OpenSetup();
        });
        Mediator.Subscribe<SwitchToRgpdConsentUiMessage>(this, (_) => OpenRgpdOnly());
    }

    private void OpenSetup()
    {
        _mode = Mode.Setup;
        _step = FirstIncompleteStep();
        _awaitingFinish = false;
        _connectionHandled = false;
        IsOpen = true;
    }

    private void OpenRgpdOnly()
    {
        _mode = Mode.RgpdOnly;
        _rgpdOnlyLaunched = false;
        if (_rgpdDataService.IsRgpdConsentOutdated)
            _rgpdPanel.PrefillOptionalFromCurrent();
        IsOpen = true;
    }

    public override void OnOpen()
    {
        var viewport = ImGui.GetMainViewport();
        float scale = ImGuiHelpers.GlobalScale;
        Position = new Vector2(
            viewport.WorkPos.X + (viewport.WorkSize.X - WindowSize.X * scale) / 2f,
            viewport.WorkPos.Y + (viewport.WorkSize.Y - WindowSize.Y * scale) / 2f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _lifetimeCts.Cancel();
                _lifetimeCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // déjà libéré par le premier passage
            }
        }
        base.Dispose(disposing);
    }

    private bool IsStepComplete(Step step) => step switch
    {
        Step.Welcome => true,
        Step.Prerequisites => _uiShared.MandatoryPluginsAvailable,
        Step.Terms => _configService.Current.AcceptedAgreement,
        Step.Data => _rgpdDataService.IsRgpdConsentValid,
        Step.Storage => !string.IsNullOrEmpty(_configService.Current.CacheFolder)
                        && _configService.Current.InitialScanComplete
                        && Directory.Exists(_configService.Current.CacheFolder),
        Step.Account => _uiShared.ApiController.IsConnected,
        _ => true,
    };

    private Step FirstIncompleteStep()
    {
        if (!_configService.Current.AcceptedAgreement) return Step.Welcome;

        foreach (var step in new[] { Step.Prerequisites, Step.Terms, Step.Data, Step.Storage, Step.Account })
        {
            if (!IsStepComplete(step)) return step;
        }

        return Step.Done;
    }

    private Vector4 GetConnectionColor()
    {
        return _uiShared.ApiController.ServerState switch
        {
            ServerState.Connecting => ImGuiColors.DalamudYellow,
            ServerState.Reconnecting => UiSharedService.AccentColor,
            ServerState.Connected => ImGuiColors.HealerGreen,
            ServerState.Disconnected => ImGuiColors.DalamudYellow,
            ServerState.Disconnecting => ImGuiColors.DalamudYellow,
            ServerState.Unauthorized => UiSharedService.AccentColor,
            ServerState.VersionMisMatch => UiSharedService.AccentColor,
            ServerState.Offline => UiSharedService.AccentColor,
            ServerState.RateLimited => ImGuiColors.DalamudYellow,
            ServerState.NoSecretKey => ImGuiColors.DalamudYellow,
            ServerState.MultiChara => ImGuiColors.DalamudYellow,
            _ => UiSharedService.AccentColor
        };
    }

    private string GetConnectionStatus()
    {
        return _uiShared.ApiController.ServerState switch
        {
            ServerState.Reconnecting => Loc.Get("CompactUi.IntroUi.ConnectionStatus.Reconnecting"),
            ServerState.Connecting => Loc.Get("CompactUi.IntroUi.ConnectionStatus.Connecting"),
            ServerState.Disconnected => Loc.Get("CompactUi.IntroUi.ConnectionStatus.Disconnected"),
            ServerState.Disconnecting => Loc.Get("CompactUi.IntroUi.ConnectionStatus.Disconnecting"),
            ServerState.Unauthorized => Loc.Get("CompactUi.IntroUi.ConnectionStatus.Unauthorized"),
            ServerState.VersionMisMatch => Loc.Get("CompactUi.IntroUi.ConnectionStatus.VersionMismatch"),
            ServerState.Offline => Loc.Get("CompactUi.IntroUi.ConnectionStatus.Offline"),
            ServerState.RateLimited => Loc.Get("CompactUi.IntroUi.ConnectionStatus.RateLimited"),
            ServerState.NoSecretKey => Loc.Get("CompactUi.IntroUi.ConnectionStatus.NoSecretKey"),
            ServerState.MultiChara => Loc.Get("CompactUi.IntroUi.ConnectionStatus.MultiChara"),
            ServerState.Connected => Loc.Get("CompactUi.IntroUi.ConnectionStatus.Connected"),
            _ => string.Empty
        };
    }

    protected override void DrawInternal()
    {
        if (_uiShared.IsInGpose) return;

        if (_mode == Mode.RgpdOnly)
        {
            DrawRgpdOnly();
            return;
        }

        if (!_configService.Current.AcceptedAgreement)
            _freshInstall = true;

        HandleConnection();

        float availWidth = ImGui.GetContentRegionAvail().X;
        float footerHeight = ImGui.GetFrameHeightWithSpacing() * 3f + 18f * ImGuiHelpers.GlobalScale;

        using (var content = ImRaii.Child("##intro_content", new Vector2(0, -footerHeight), false))
        {
            if (content)
            {
                switch (_step)
                {
                    case Step.Welcome: DrawWelcome(availWidth); break;
                    case Step.Prerequisites: DrawPrerequisites(availWidth); break;
                    case Step.Terms: DrawTerms(availWidth); break;
                    case Step.Data: DrawData(availWidth); break;
                    case Step.Storage: DrawStorage(availWidth); break;
                    case Step.Account: DrawAccount(availWidth); break;
                    default: DrawDone(availWidth); break;
                }
                UiSharedService.EndSectionCard();
            }
        }

        DrawFooter(availWidth);
    }

    // La connexion établie ferme la boucle : les services démarrent, et le joueur arrive sur la page finale
    // plutôt que de voir la fenêtre disparaître sans explication.
    private void HandleConnection()
    {
        if (_connectionHandled || !_uiShared.ApiController.IsConnected) return;
        _connectionHandled = true;

        if (_freshInstall)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
                _configService.Current.LastChangelogVersionSeen = version.ToString();
            _configService.Save();
        }

        _secretKey = string.Empty;
        _serverConfigurationManager.Save();
        _awaitingFinish = true;
        _step = Step.Done;
        Mediator.Publish(new SwitchToMainUiMessage());
    }

    private void DrawRgpdOnly()
    {
        float availWidth = ImGui.GetContentRegionAvail().X;

        // Consentement donné : les services peuvent démarrer, la fenêtre n'a plus rien à dire.
        if (_rgpdDataService.IsRgpdConsentValid)
        {
            if (!_rgpdOnlyLaunched)
            {
                _rgpdOnlyLaunched = true;
                Mediator.Publish(new SwitchToMainUiMessage());
            }
            IsOpen = false;
            return;
        }

        WizardControls.DrawStepHeader(FontAwesomeIcon.ShieldAlt, Loc.Get("Rgpd.Consent.Title"),
            Loc.Get("Wizard.Data.Subtitle"), availWidth);

        using (var content = ImRaii.Child("##intro_rgpd", Vector2.Zero, false))
        {
            if (content)
            {
                UiSharedService.BeginSectionCard(Loc.Get("Rgpd.Consent.Title"), FontAwesomeIcon.ShieldAlt);
                _rgpdPanel.Draw(drawTitle: false);
                UiSharedService.EndSectionCard();
            }
        }
    }

    // Bienvenue

    // Version de test, jamais vue : l'avertissement est porté par la première page plutôt que dans une fenêtre à part.
    private bool NeedsTestBuildNotice =>
        _isTestBuild && !string.Equals(_configService.Current.LastTestBuildWarningVersionSeen, _versionLabel, StringComparison.Ordinal);

    private void DrawWelcome(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.Star, Loc.Get("CompactUi.IntroUi.Welcome.Title"),
            Loc.Get("Wizard.Welcome.Subtitle"), availWidth);

        bool testNotice = NeedsTestBuildNotice;
        if (testNotice)
        {
            UiSharedService.BeginSectionCard(Loc.Get("TestBuildWarning.Header"), FontAwesomeIcon.FlaskVial);
            UiSharedService.ColorTextWrapped(Loc.Get("TestBuildWarning.Body"), ImGuiColors.DalamudWhite);
            ImGuiHelpers.ScaledDummy(2f);
            UiSharedService.ColorTextWrapped(Loc.Get("TestBuildWarning.Signature"), ImGuiColors.DalamudGrey);
        }

        UiSharedService.BeginSectionCard(Loc.Get("Wizard.Welcome.Card.What"), FontAwesomeIcon.Sync);
        UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Welcome.Description"));

        UiSharedService.BeginSectionCard(Loc.Get("Wizard.Welcome.Card.Note"), FontAwesomeIcon.InfoCircle);
        UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.IntroUi.Welcome.ModNote"), ImGuiColors.DalamudYellow);

        // La page est déjà dense avec l'avertissement : on garde l'essentiel.
        if (!testNotice)
        {
            UiSharedService.BeginSectionCard(Loc.Get("Wizard.Welcome.Card.Steps"), FontAwesomeIcon.ListOl);
            UiSharedService.TextWrapped(Loc.Get("Wizard.Welcome.Steps"));
        }
    }

    // Prérequis

    private void DrawPrerequisites(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.PuzzlePiece, Loc.Get("Wizard.Prereq.Title"),
            Loc.Get("Wizard.Prereq.Subtitle"), availWidth);

        UiSharedService.BeginSectionCard(Loc.Get("Settings.Plugins.Mandatory"), FontAwesomeIcon.Plug);
        DrawPluginLine("Penumbra", _uiShared.PenumbraAvailable);
        DrawPluginLine("Glamourer", _uiShared.GlamourerAvailable);
        ImGuiHelpers.ScaledDummy(4f);

        if (_uiShared.MandatoryPluginsAvailable)
            UiSharedService.ColorTextWrapped(Loc.Get("Wizard.Prereq.Ready"), ImGuiColors.HealerGreen);
        else
            UiSharedService.ColorTextWrapped(Loc.Get("Wizard.Prereq.Missing"), ImGuiColors.DalamudYellow);

        UiSharedService.EndSectionCard();
        ImGuiHelpers.ScaledDummy(4f);

        // Le détail des plugins facultatifs n'est utile qu'à qui le demande.
        if (ImGui.CollapsingHeader(Loc.Get("Wizard.Prereq.AllPlugins")))
        {
            ImGuiHelpers.ScaledDummy(4f);

            UiSharedService.BeginSectionCard(Loc.Get("Settings.Plugins.Optional"), FontAwesomeIcon.PuzzlePiece);
            UiSharedService.ColorTextWrapped(Loc.Get("Settings.Plugins.Optional.Hint"), ImGuiColors.DalamudGrey);
            ImGuiHelpers.ScaledDummy(2f);
            DrawPluginGrid("##optional_plugins", _uiShared.GetOptionalPluginStates());

            UiSharedService.BeginSectionCard(Loc.Get("Settings.Plugins.Detected"), FontAwesomeIcon.Search);
            UiSharedService.ColorTextWrapped(Loc.Get("Settings.Plugins.Detected.Hint"), ImGuiColors.DalamudGrey);
            ImGuiHelpers.ScaledDummy(2f);
            DrawPluginGrid("##detected_plugins", _uiShared.GetDetectedPluginStates());
        }
    }

    // Deux colonnes : coche verte pour un plugin présent, pastille neutre pour un absent (il est facultatif).
    private void DrawPluginGrid(string id, IReadOnlyList<(string Name, bool Exists, string? HintKey)> plugins)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchSame))
            return;

        foreach (var (name, exists, hintKey) in plugins)
        {
            ImGui.TableNextColumn();
            _uiShared.IconText(exists ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.MinusCircle,
                exists ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey3);
            ImGui.SameLine();
            ImGui.TextColored(exists ? ImGuiColors.DalamudWhite : ImGuiColors.DalamudGrey, name);
            if (hintKey != null)
                UiSharedService.AttachToolTip(Loc.Get(hintKey));
        }

        ImGui.EndTable();
    }

    private void DrawPluginLine(string name, bool available)
    {
        _uiShared.IconText(available ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.TimesCircle,
            available ? ImGuiColors.HealerGreen : UiSharedService.AccentColor);
        ImGui.SameLine();
        ImGui.TextUnformatted(name);
    }

    // Conditions

    private void DrawTerms(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.FileContract, Loc.Get("CompactUi.IntroUi.Agreement.Title"),
            Loc.Get("Wizard.Terms.Subtitle"), availWidth);

        UiSharedService.BeginSectionCard(Loc.Get("CompactUi.IntroUi.Agreement.ReadHeader"), FontAwesomeIcon.ExclamationCircle);
        string[] keys =
        [
            "AgeRequirement", "ModUpload", "Bandwidth", "Privacy", "Caution",
            "Inactivity", "AccountRemoval", "Infrastructure", "Deletion", "Disclaimer",
        ];
        foreach (var key in keys)
            DrawBullet(Loc.Get("CompactUi.IntroUi.Agreement." + key));
        UiSharedService.EndSectionCard();
        ImGuiHelpers.ScaledDummy(6f);

        if (_configService.Current.AcceptedAgreement)
        {
            _uiShared.IconText(FontAwesomeIcon.CheckCircle, ImGuiColors.HealerGreen);
            ImGui.SameLine();
            UiSharedService.ColorText(Loc.Get("Wizard.Terms.Accepted"), ImGuiColors.HealerGreen);
            return;
        }

        if (_termsShownAt == DateTime.MinValue)
            _termsShownAt = DateTime.UtcNow;

        var remaining = TermsDelay - (DateTime.UtcNow - _termsShownAt);
        if (remaining > TimeSpan.Zero)
        {
            UiSharedService.ColorTextWrapped(
                string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.IntroUi.Agreement.TimeoutLabel"),
                    (int)Math.Ceiling(remaining.TotalSeconds)),
                ImGuiColors.DalamudGrey);
            return;
        }

        if (ImGui.Button(Loc.Get("CompactUi.IntroUi.Agreement.AgreeButton") + "##accept_terms", new Vector2(availWidth, 0)))
        {
            _configService.Current.AcceptedAgreement = true;
            _configService.Save();
        }
    }

    private static void DrawBullet(string text)
    {
        float bulletWidth = 14f * ImGuiHelpers.GlobalScale;
        ImGui.Indent(bulletWidth);
        float x = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(x - bulletWidth);
        ImGui.TextColored(UiSharedService.AccentColor, "•");
        ImGui.SameLine(x);
        ImGui.TextUnformatted(text);
        ImGui.Unindent(bulletWidth);
        ImGuiHelpers.ScaledDummy(2f);
    }

    // Données personnelles

    private void DrawData(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.ShieldAlt, Loc.Get("Rgpd.Consent.Title"),
            Loc.Get("Wizard.Data.Subtitle"), availWidth);

        if (_rgpdDataService.IsRgpdConsentValid)
        {
            UiSharedService.BeginSectionCard(Loc.Get("Rgpd.Consent.Title"), FontAwesomeIcon.ShieldAlt);
            _uiShared.IconText(FontAwesomeIcon.CheckCircle, ImGuiColors.HealerGreen);
            ImGui.SameLine();
            UiSharedService.ColorText(Loc.Get("WhatsNew.Rgpd.Accepted"), ImGuiColors.HealerGreen);
            ImGuiHelpers.ScaledDummy(2f);
            UiSharedService.TextWrapped(Loc.Get("WhatsNew.Rgpd.AcceptedHint"));
            return;
        }

        UiSharedService.BeginSectionCard(Loc.Get("Rgpd.Consent.Title"), FontAwesomeIcon.ShieldAlt);
        _rgpdPanel.Draw(drawTitle: false);
    }

    // Stockage

    private void DrawStorage(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.Folder, Loc.Get("CompactUi.IntroUi.Storage.Title"),
            Loc.Get("Wizard.Storage.Subtitle"), availWidth);

        if (!_uiShared.HasValidPenumbraModPath)
        {
            UiSharedService.BeginSectionCard(Loc.Get("Wizard.Storage.Card.Penumbra"), FontAwesomeIcon.ExclamationTriangle);
            UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.IntroUi.Storage.InvalidPenumbraPath"), UiSharedService.AccentColor);
            return;
        }

        UiSharedService.BeginSectionCard(Loc.Get("Wizard.Storage.Card.Folder"), FontAwesomeIcon.FolderOpen);
        UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Storage.Description"));
        ImGuiHelpers.ScaledDummy(2f);
        _uiShared.DrawCacheDirectorySetting();

        UiSharedService.BeginSectionCard(Loc.Get("Wizard.Storage.Card.Scan"), FontAwesomeIcon.Search);
        UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.IntroUi.Storage.WarningScan"), ImGuiColors.DalamudGrey);
        ImGuiHelpers.ScaledDummy(2f);

        bool canScan = !_cacheMonitor.IsScanRunning
                       && !string.IsNullOrEmpty(_configService.Current.CacheFolder)
                       && Directory.Exists(_configService.Current.CacheFolder);
        if (canScan && !_configService.Current.InitialScanComplete)
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Play, Loc.Get("CompactUi.IntroUi.Storage.StartScanButton")))
                _cacheMonitor.InvokeScan();
        }
        else
        {
            _uiShared.DrawFileScanState();
        }

        if (_configService.Current.InitialScanComplete && !_cacheMonitor.IsScanRunning)
        {
            ImGuiHelpers.ScaledDummy(2f);
            _uiShared.IconText(FontAwesomeIcon.CheckCircle, ImGuiColors.HealerGreen);
            ImGui.SameLine();
            UiSharedService.ColorText(Loc.Get("Wizard.Storage.ScanDone"), ImGuiColors.HealerGreen);
        }

        if (!_dalamudUtilService.IsWine)
        {
            UiSharedService.BeginSectionCard(Loc.Get("Wizard.Storage.Card.Compactor"), FontAwesomeIcon.Compress);
            var useFileCompactor = _configService.Current.UseCompactor;
            if (ToggleSwitch.Draw(Loc.Get("CompactUi.IntroUi.Storage.UseCompactorLabel"), ref useFileCompactor))
            {
                _configService.Current.UseCompactor = useFileCompactor;
                _configService.Save();
            }
            UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.IntroUi.Storage.UseCompactorDescription"), ImGuiColors.DalamudGrey);
        }
    }

    // Compte

    private void DrawAccount(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.UserCircle, Loc.Get("CompactUi.IntroUi.Service.Title"),
            Loc.Get("Wizard.Account.Subtitle"), availWidth);

        var state = _uiShared.ApiController.ServerState;
        bool connecting = state is ServerState.Connecting or ServerState.Reconnecting;

        using (ImRaii.Disabled(_registrationInProgress || connecting))
        {
            DrawRegisterCard();
            DrawKeyCard(availWidth);
        }

        UiSharedService.EndSectionCard();
        if (state != ServerState.NoSecretKey)
        {
            ImGuiHelpers.ScaledDummy(4f);
            UiSharedService.ColorText(GetConnectionStatus(), GetConnectionColor());
        }

        ImGuiHelpers.ScaledDummy(6f);
        if (ImGui.CollapsingHeader(Loc.Get("Wizard.Account.Advanced")))
        {
            ImGuiHelpers.ScaledDummy(2f);
            _ = _uiShared.DrawServiceSelection(selectOnChange: true, intro: true);
        }
    }

    private void DrawRegisterCard()
    {
        UiSharedService.BeginSectionCard(Loc.Get("Wizard.Account.Card.Register"), FontAwesomeIcon.UserPlus);
        UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Service.RegisterIntro"));
        ImGuiHelpers.ScaledDummy(2f);

        using (ImRaii.Disabled(_registrationInProgress || _registrationSuccess || _secretKey.Length > 0))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("CompactUi.IntroUi.Service.RegisterButton")))
                StartRegistration();
        }

        if (_registrationInProgress)
        {
            ImGui.TextUnformatted(Loc.Get("CompactUi.IntroUi.Service.RegisterSending"));
        }
        else if (!_registrationMessage.IsNullOrEmpty())
        {
            if (_registrationSuccess)
                UiSharedService.ColorTextWrapped(_registrationMessage!, ImGuiColors.HealerGreen);
            else
                UiSharedService.ColorTextWrapped(_registrationMessage!, ImGuiColors.DalamudYellow);
        }
    }

    private void StartRegistration()
    {
        _registrationInProgress = true;
        var token = _lifetimeCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var reply = await _registerService.RegisterAccount(token).ConfigureAwait(false);
                if (!reply.Success)
                {
                    _logger.LogWarning("Registration failed: {err}", reply.ErrorMessage);
                    _registrationMessage = reply.ErrorMessage;
                    if (_registrationMessage.IsNullOrEmpty())
                        _registrationMessage = Loc.Get("CompactUi.IntroUi.Service.RegisterErrorUnknown");
                    return;
                }
                _registrationMessage = Loc.Get("CompactUi.IntroUi.Service.RegisterSuccess");
                _secretKey = reply.SecretKey;
                _registrationReply = reply;
                _registrationSuccess = true;
            }
            catch (OperationCanceledException)
            {
                _registrationSuccess = false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Registration failed");
                _registrationSuccess = false;
                _registrationMessage = Loc.Get("CompactUi.IntroUi.Service.RegisterErrorUnknown");
            }
            finally
            {
                _registrationInProgress = false;
            }
        }, token);
    }

    private void DrawKeyCard(float availWidth)
    {
        UiSharedService.BeginSectionCard(Loc.Get("Wizard.Account.Card.Key"), FontAwesomeIcon.Key);
        UiSharedService.TextWrapped(Loc.Get(_registrationSuccess
            ? "CompactUi.IntroUi.SecretKey.DisplayLabel"
            : "CompactUi.IntroUi.SecretKey.ExistingAccountInfo"));
        ImGuiHelpers.ScaledDummy(2f);

        ImGui.SetNextItemWidth(MathF.Max(120f * ImGuiHelpers.GlobalScale, availWidth - 40f * ImGuiHelpers.GlobalScale));
        ImGui.InputTextWithHint("##intro_secret_key", Loc.Get("CompactUi.IntroUi.SecretKey.EnterLabel"), ref _secretKey, 64);

        if (_secretKey.Length > 0 && _secretKey.Length != 64)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.IntroUi.SecretKey.LengthError"), UiSharedService.AccentColor);
        }
        else if (_secretKey.Length == 64 && !HexRegex().IsMatch(_secretKey))
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.IntroUi.SecretKey.FormatError"), UiSharedService.AccentColor);
        }
        else if (_secretKey.Length == 64)
        {
            ImGuiHelpers.ScaledDummy(2f);
            if (_registrationSuccess)
            {
                UiSharedService.ColorTextWrapped(Loc.Get("Wizard.Account.KeepKey"), ImGuiColors.DalamudYellow);
                if (_uiShared.IconTextButton(FontAwesomeIcon.Copy, Loc.Get("Wizard.Account.CopyKey")))
                    ImGui.SetClipboardText(_secretKey);
                ImGui.SameLine();
            }

            if (_uiShared.IconTextButton(FontAwesomeIcon.Link, Loc.Get("CompactUi.IntroUi.SecretKey.SaveConnectButton")))
                SaveKeyAndConnect();
        }
    }

    private void SaveKeyAndConnect()
    {
        if (!_serverConfigurationManager.HasServers) _serverConfigurationManager.SelectServer(0);

        string keyName;
        if (_registrationReply != null && _secretKey.Equals(_registrationReply.SecretKey, StringComparison.Ordinal))
            keyName = string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.IntroUi.SecretKey.FriendlyNameRegistered"), _registrationReply.UID, DateTime.Now);
        else
            keyName = string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.IntroUi.SecretKey.FriendlyNameDefault"), DateTime.Now);

        _serverConfigurationManager.CurrentServer.SecretKeys.Add(
            _serverConfigurationManager.CurrentServer.SecretKeys.Select(k => k.Key).LastOrDefault() + 1,
            new SecretKey
            {
                FriendlyName = keyName,
                Key = _secretKey,
            });
        _serverConfigurationManager.AddCurrentCharacterToServer(save: false);
        _ = Task.Run(async () =>
        {
            try
            {
                await _uiShared.ApiController.CreateConnections().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect after saving the secret key");
            }
        });
    }

    // Terminé

    private void DrawDone(float availWidth)
    {
        WizardControls.DrawStepHeader(FontAwesomeIcon.CheckCircle, Loc.Get("Wizard.Done.Title"),
            Loc.Get("Wizard.Done.Subtitle"), availWidth);

        UiSharedService.BeginSectionCard(Loc.Get("Wizard.Done.Recap.Title"), FontAwesomeIcon.ClipboardCheck);
        DrawRecapLine(_rgpdDataService.IsRgpdConsentValid, Loc.Get("Wizard.Done.Recap.Consent"));
        DrawRecapLine(_configService.Current.InitialScanComplete,
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Wizard.Done.Recap.Storage"), _configService.Current.CacheFolder));
        bool connected = _uiShared.ApiController.IsConnected;
        DrawRecapLine(connected, connected
            ? string.Format(CultureInfo.CurrentCulture, Loc.Get("Wizard.Done.Recap.Account"), _uiShared.ApiController.DisplayName)
            : Loc.Get("CompactUi.IntroUi.ConnectionStatus.Connecting"));

        UiSharedService.BeginSectionCard(Loc.Get("Wizard.Done.Next.Title"), FontAwesomeIcon.Compass);
        UiSharedService.TextWrapped(Loc.Get("Wizard.Done.Next.Pair"));
        ImGuiHelpers.ScaledDummy(4f);
        UiSharedService.ColorTextWrapped(Loc.Get("Wizard.Done.Next.Settings"), ImGuiColors.DalamudGrey);
    }

    private void DrawRecapLine(bool done, string text)
    {
        _uiShared.IconText(done ? FontAwesomeIcon.Check : FontAwesomeIcon.Clock,
            done ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow);
        ImGui.SameLine();
        ImGui.TextUnformatted(text);
    }

    // Barre du bas

    private string? GetBlockedHint() => _step switch
    {
        Step.Prerequisites => Loc.Get("Wizard.Hint.Prereq"),
        Step.Terms => Loc.Get("Wizard.Hint.Terms"),
        Step.Data => Loc.Get("Wizard.Hint.Data"),
        Step.Storage => Loc.Get(_cacheMonitor.IsScanRunning ? "Wizard.Hint.StorageScanning" : "Wizard.Hint.Storage"),
        Step.Account => Loc.Get("Wizard.Hint.Account"),
        _ => null,
    };

    private void DrawFooter(float availWidth)
    {
        bool complete = IsStepComplete(_step);
        bool isLast = _step == Step.Done;

        // Une phrase claire dit ce qu'il reste à faire, plutôt qu'un bouton grisé muet.
        var hint = !complete ? GetBlockedHint() : null;
        if (hint != null)
            UiSharedService.ColorTextWrapped(hint, ImGuiColors.DalamudYellow);
        else
            ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight()));

        WizardControls.DrawStepDots(StepCount, (int)_step, availWidth);
        ImGuiHelpers.ScaledDummy(4f);

        float buttonWidth = 140f * ImGuiHelpers.GlobalScale;

        using (ImRaii.Disabled(_step == Step.Welcome || _awaitingFinish))
        {
            if (ImGui.Button(Loc.Get("Wizard.Nav.Previous") + "##intro_prev", new Vector2(buttonWidth, 0)))
                _step = (Step)Math.Max(0, (int)_step - 1);
        }

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - buttonWidth);

        if (isLast)
        {
            if (ImGui.Button(Loc.Get("Wizard.Nav.Start") + "##intro_finish", new Vector2(buttonWidth, 0)))
            {
                _awaitingFinish = false;
                IsOpen = false;
            }
            return;
        }

        using (ImRaii.Disabled(!complete))
        {
            if (ImGui.Button(Loc.Get("Wizard.Nav.Continue") + "##intro_next", new Vector2(buttonWidth, 0)))
            {
                // Continuer vaut « J'ai compris » pour l'avertissement de version de test.
                if (_step == Step.Welcome && NeedsTestBuildNotice)
                {
                    _configService.Current.LastTestBuildWarningVersionSeen = _versionLabel;
                    _configService.Save();
                }
                _step = (Step)Math.Min(StepCount - 1, (int)_step + 1);
            }
        }
    }

#pragma warning disable MA0009
    [GeneratedRegex("^([A-F0-9]{2})+$")]
    private static partial Regex HexRegex();
#pragma warning restore MA0009
}
