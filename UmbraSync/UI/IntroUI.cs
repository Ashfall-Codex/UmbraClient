using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using UmbraSync.API.Dto.Account;
using UmbraSync.FileCache;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.WebAPI;
using UmbraSync.WebAPI.SignalR.Utils;

namespace UmbraSync.UI;

public partial class IntroUi : WindowMediatorSubscriberBase
{
    private readonly MareConfigService _configService;
    private readonly CacheMonitor _cacheMonitor;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly AccountRegistrationService _registerService;
    private readonly RgpdDataService _rgpdDataService;
    private readonly UiSharedService _uiShared;
    private bool _readFirstPage;
    private bool _rgpdConsentDataCollection = true;
    private bool _rgpdConsentDataSharing = true;
    private bool _rgpdConsentThirdPartyPlugins;

    private string _secretKey = string.Empty;
    private string _timeoutLabel = string.Empty;
    private Task? _timeoutTask;
    private bool _registrationInProgress;
    private bool _registrationSuccess;
    private string? _registrationMessage;
    private RegisterReplyDto? _registrationReply;

    public IntroUi(ILogger<IntroUi> logger, UiSharedService uiShared, MareConfigService configService,
        CacheMonitor fileCacheManager, ServerConfigurationManager serverConfigurationManager, MareMediator mareMediator,
        PerformanceCollectorService performanceCollectorService, DalamudUtilService dalamudUtilService, AccountRegistrationService registerService,
        RgpdDataService gdprDataService) : base(logger, mareMediator, Loc.Get("CompactUi.IntroUi.WindowTitle"), performanceCollectorService)
    {
        _uiShared = uiShared;
        _configService = configService;
        _cacheMonitor = fileCacheManager;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtilService = dalamudUtilService;
        _registerService = registerService;
        _rgpdDataService = gdprDataService;
        IsOpen = false;
        ShowCloseButton = false;
        RespectCloseHotkey = false;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(650, 500),
            MaximumSize = new Vector2(650, 2000),
        };

        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) =>
        {
            _configService.Current.UseCompactor = !dalamudUtilService.IsWine;
            IsOpen = true;
        });
        Mediator.Subscribe<SwitchToRgpdConsentUiMessage>(this, (_) => IsOpen = true);
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

        if (!_configService.Current.AcceptedAgreement && !_readFirstPage)
        {
            _uiShared.BigText(Loc.Get("CompactUi.IntroUi.Welcome.Title"));
            ImGui.Separator();
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Welcome.Description"));
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Welcome.SetupInfo"));

            UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.IntroUi.Welcome.ModNote"), ImGuiColors.DalamudYellow);
            if (!_uiShared.DrawOtherPluginState(intro: true)) return;
            ImGui.Separator();
            var nextLabel = $"{Loc.Get("CompactUi.IntroUi.Welcome.NextButton")}##toAgreement";
            if (ImGui.Button(nextLabel))
            {
                _readFirstPage = true;
#if !DEBUG
                _timeoutTask = Task.Run(async () =>
                {
                    for (int i = 10; i >= 0; i--)
                    {
                        _timeoutLabel = string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.IntroUi.Agreement.TimeoutLabel"), i);
                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                });
#else
                _timeoutTask = Task.CompletedTask;
#endif
            }
        }
        else if (!_configService.Current.AcceptedAgreement && _readFirstPage)
        {
            using (_uiShared.UidFont.Push())
            {
                ImGui.TextUnformatted(Loc.Get("CompactUi.IntroUi.Agreement.Title"));
            }

            ImGui.Separator();
            UiSharedService.SetFontScale(1.5f);
            string readThis = Loc.Get("CompactUi.IntroUi.Agreement.ReadHeader");
            Vector2 textSize = ImGui.CalcTextSize(readThis);
            ImGui.SetCursorPosX(ImGui.GetWindowSize().X / 2 - textSize.X / 2);
            UiSharedService.ColorText(readThis, UiSharedService.AccentColor);
            UiSharedService.SetFontScale(1.0f);
            ImGui.Separator();
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Agreement.AgeRequirement"));
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Agreement.ModUpload"));
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Agreement.Bandwidth"));
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Agreement.Privacy"));
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Agreement.Caution"));
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Agreement.Inactivity"));
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Agreement.AccountRemoval"));
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Agreement.Infrastructure"));
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Agreement.Deletion"));
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Agreement.Disclaimer"));

            ImGui.Separator();
            if (_timeoutTask?.IsCompleted ?? true)
            {
                var agreeLabel = $"{Loc.Get("CompactUi.IntroUi.Agreement.AgreeButton")}##toSetup";
                if (ImGui.Button(agreeLabel))
                {
                    _configService.Current.AcceptedAgreement = true;
                    _configService.Save();
                }
            }
            else
            {
                UiSharedService.TextWrapped(_timeoutLabel);
            }
        }
        else if (_configService.Current.AcceptedAgreement && !_rgpdDataService.IsRgpdConsentValid)
        {
            DrawRgpdConsentPage();
        }
        else if (_configService.Current.AcceptedAgreement
                 && (string.IsNullOrEmpty(_configService.Current.CacheFolder)
                     || !_configService.Current.InitialScanComplete
                     || !Directory.Exists(_configService.Current.CacheFolder)))
        {
            using (_uiShared.UidFont.Push())
                ImGui.TextUnformatted(Loc.Get("CompactUi.IntroUi.Storage.Title"));

            ImGui.Separator();

            if (!_uiShared.HasValidPenumbraModPath)
            {
                UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.IntroUi.Storage.InvalidPenumbraPath"), UiSharedService.AccentColor);
            }
            else
            {
                UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Storage.Description"));
                UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Storage.Note"));
                UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.IntroUi.Storage.WarningFileCache"), ImGuiColors.DalamudYellow);
                UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.IntroUi.Storage.WarningScan"), ImGuiColors.DalamudYellow);
                _uiShared.DrawCacheDirectorySetting();
            }

            if (!_cacheMonitor.IsScanRunning && !string.IsNullOrEmpty(_configService.Current.CacheFolder) && _uiShared.HasValidPenumbraModPath && Directory.Exists(_configService.Current.CacheFolder))
            {
                var startScanLabel = $"{Loc.Get("CompactUi.IntroUi.Storage.StartScanButton")}##startScan";
                if (ImGui.Button(startScanLabel))
                {
                    _cacheMonitor.InvokeScan();
                }
            }
            else
            {
                _uiShared.DrawFileScanState();
            }
            if (!_dalamudUtilService.IsWine)
            {
                var useFileCompactor = _configService.Current.UseCompactor;
                if (ImGui.Checkbox(Loc.Get("CompactUi.IntroUi.Storage.UseCompactorLabel"), ref useFileCompactor))
                {
                    _configService.Current.UseCompactor = useFileCompactor;
                    _configService.Save();
                }
                UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.IntroUi.Storage.UseCompactorDescription"), ImGuiColors.DalamudYellow);
            }
        }
        else if (!_uiShared.ApiController.IsConnected)
        {
            using (_uiShared.UidFont.Push())
                ImGui.TextUnformatted(Loc.Get("CompactUi.IntroUi.Service.Title"));
            ImGui.Separator();
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Service.RegisterIntro"));
            UiSharedService.TextWrapped(Loc.Get("CompactUi.IntroUi.Service.RegisterSupport"));

            ImGui.Separator();

            ImGui.BeginDisabled(_registrationInProgress || _uiShared.ApiController.ServerState == ServerState.Connecting || _uiShared.ApiController.ServerState == ServerState.Reconnecting);
            _ = _uiShared.DrawServiceSelection(selectOnChange: true, intro: true);

            if (true) // Enable registration button for all servers
            {
                ImGui.BeginDisabled(_registrationInProgress || _registrationSuccess || _secretKey.Length > 0);
                ImGui.Separator();
                ImGui.TextUnformatted(Loc.Get("CompactUi.IntroUi.Service.RegisterInfo"));
                if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("CompactUi.IntroUi.Service.RegisterButton")))
                {
                    _registrationInProgress = true;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var reply = await _registerService.RegisterAccount(CancellationToken.None).ConfigureAwait(false);
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
                    });
                }
                ImGui.EndDisabled(); // _registrationInProgress || _registrationSuccess
                if (_registrationInProgress)
                {
                    ImGui.TextUnformatted(Loc.Get("CompactUi.IntroUi.Service.RegisterSending"));
                }
                else if (!_registrationMessage.IsNullOrEmpty())
                {
                    if (!_registrationSuccess)
                        ImGui.TextColored(ImGuiColors.DalamudYellow, _registrationMessage);
                    else
                        ImGui.TextWrapped(_registrationMessage);
                }
            }

            ImGui.Separator();

            var text = Loc.Get("CompactUi.IntroUi.SecretKey.EnterLabel");

            if (_registrationSuccess)
            {
                text = Loc.Get("CompactUi.IntroUi.SecretKey.DisplayLabel");
            }
            else
            {
                ImGui.TextUnformatted(Loc.Get("CompactUi.IntroUi.SecretKey.ExistingAccountInfo"));
            }

            var textSize = ImGui.CalcTextSize(text);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(text);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(UiSharedService.GetWindowContentRegionWidth() - ImGui.GetWindowContentRegionMin().X - textSize.X);
            ImGui.InputText("", ref _secretKey, 64);
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
                using var saveDisabled = ImRaii.Disabled(_uiShared.ApiController.ServerState == ServerState.Connecting || _uiShared.ApiController.ServerState == ServerState.Reconnecting);
                if (ImGui.Button(Loc.Get("CompactUi.IntroUi.SecretKey.SaveConnectButton")))
                {
                    string keyName;
                    if (!_serverConfigurationManager.HasServers) _serverConfigurationManager.SelectServer(0);
                    if (_registrationReply != null && _secretKey.Equals(_registrationReply.SecretKey, StringComparison.Ordinal))
                        keyName = string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.IntroUi.SecretKey.FriendlyNameRegistered"), _registrationReply.UID, DateTime.Now);
                    else
                        keyName = string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.IntroUi.SecretKey.FriendlyNameDefault"), DateTime.Now);
                    _serverConfigurationManager.CurrentServer!.SecretKeys.Add(_serverConfigurationManager.CurrentServer.SecretKeys.Select(k => k.Key).LastOrDefault() + 1, new SecretKey()
                    {
                        FriendlyName = keyName,
                        Key = _secretKey,
                    });
                    _serverConfigurationManager.AddCurrentCharacterToServer(save: false);
                    _ = Task.Run(() => _uiShared.ApiController.CreateConnections());
                }
            }

            if (_uiShared.ApiController.ServerState != ServerState.NoSecretKey)
            {
                UiSharedService.ColorText(GetConnectionStatus(), GetConnectionColor());
            }

            ImGui.EndDisabled(); // _registrationInProgress
        }
        else
        {
            _secretKey = string.Empty;
            _serverConfigurationManager.Save();
            Mediator.Publish(new SwitchToMainUiMessage());
            IsOpen = false;
        }
    }

    private void DrawRgpdConsentPage()
    {
        using (_uiShared.UidFont.Push())
            ImGui.TextUnformatted(Loc.Get("Rgpd.Consent.Title"));

        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        UiSharedService.TextWrapped(Loc.Get("Rgpd.Consent.Intro"));
        ImGuiHelpers.ScaledDummy(2f);
        UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.Reassurance"), ImGuiColors.HealerGreen);
        ImGuiHelpers.ScaledDummy(4f);

        _uiShared.BigText(Loc.Get("Rgpd.Consent.DataCollected.Header"));
        UiSharedService.TextWrapped(Loc.Get("Rgpd.Consent.DataCollected.Details"));
        ImGuiHelpers.ScaledDummy(4f);

        // Mandatory consents
        ImGui.Checkbox(Loc.Get("Rgpd.Consent.DataCollection"), ref _rgpdConsentDataCollection);
        UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.DataCollection.Detail"), ImGuiColors.DalamudGrey3);

        ImGui.Checkbox(Loc.Get("Rgpd.Consent.DataSharing"), ref _rgpdConsentDataSharing);
        UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.DataSharing.Detail"), ImGuiColors.DalamudGrey3);

        // Optional consent
        ImGui.Checkbox(Loc.Get("Rgpd.Consent.ThirdParty"), ref _rgpdConsentThirdPartyPlugins);
        UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.ThirdParty.Detail"), ImGuiColors.DalamudGrey3);

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();

        // Rights summary
        _uiShared.BigText(Loc.Get("Rgpd.Consent.Rights"));
        UiSharedService.TextWrapped(Loc.Get("Rgpd.Consent.Rights.Summary"));
        ImGuiHelpers.ScaledDummy(4f);

        // Accept button (requires mandatory consents)
        bool canAccept = _rgpdConsentDataCollection && _rgpdConsentDataSharing;
        using (ImRaii.Disabled(!canAccept))
        {
            if (ImGui.Button(Loc.Get("Rgpd.Consent.AcceptButton")))
            {
                _rgpdDataService.AcceptRgpdConsent(_rgpdConsentDataCollection, _rgpdConsentDataSharing, _rgpdConsentThirdPartyPlugins);
            }
        }

        if (!canAccept)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.MandatoryWarning"), ImGuiColors.DalamudYellow);
        }
    }

#pragma warning disable MA0009
    [GeneratedRegex("^([A-F0-9]{2})+")]
    private static partial Regex HexRegex();
#pragma warning restore MA0009
}