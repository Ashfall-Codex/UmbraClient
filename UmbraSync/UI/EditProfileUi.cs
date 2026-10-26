using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Utility;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using System.Text.RegularExpressions;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Dto.User;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Factories;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Interop.Ipc;
using UmbraSync.Models;
using UmbraSync.Services.Notification;
using UmbraSync.UI.Components;
using NotificationType = UmbraSync.MareConfiguration.Models.NotificationType;

namespace UmbraSync.UI;

public class EditProfileUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly FileDialogManager _fileDialogManager;
    private readonly UmbraProfileManager _umbraProfileManager;
    private readonly PairManager _pairManager;
    private readonly PairFactory _pairFactory;
    private readonly RpConfigService _rpConfigService;
    private readonly UiSharedService _uiSharedService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly IpcManager _ipcManager;
    private readonly NotificationTracker _notificationTracker;
    private readonly IDataManager _dataManager;
    private readonly HonorificEditor _honorificEditor;
    private readonly MoodlesEditor _moodlesEditor;
    private string _descriptionText = string.Empty;
    private string _rpDescriptionText = string.Empty;
    private string _rpFirstNameText = string.Empty;
    private string _rpLastNameText = string.Empty;
    private string _rpTitleText = string.Empty;
    private string _rpAgeText = string.Empty;
    private string _rpRaceText = string.Empty;
    private string _rpEthnicityText = string.Empty;
    private string _rpHeightText = string.Empty;
    private string _rpBuildText = string.Empty;
    private string _rpResidenceText = string.Empty;
    private string _rpOccupationText = string.Empty;
    private string _rpAffiliationText = string.Empty;
    private string _rpAlignmentText = string.Empty;
    private string _rpAdditionalInfoText = string.Empty;
    private List<RpCustomField> _rpCustomFields = new();
    private IDalamudTextureWrap? _pfpTextureWrap;
    private IDalamudTextureWrap? _rpPfpTextureWrap;
    private byte[] _profileImage = [];
    private byte[] _rpProfileImage = [];
    private bool _showFileDialogError = false;
    private byte[] _rpBannerImage = [];
    private IDalamudTextureWrap? _rpBannerTexture;
    private bool _showBannerError;
    private bool _rpBannerDirty;
    private volatile bool _bannerTextureStale;
    private bool _cardPreviewOpen;
    private volatile bool _previewRefreshPending;
    private bool _wasOpen;
    private bool _rpLoaded = false;
    private string _rpLoadedForKey = string.Empty;
    private bool _hrpLoaded = false;
    private int _activeTab;
    private readonly ChatIconPicker _chatIconPicker;
    private byte _rpLevel;
    private byte _savedRpLevel;
    private RpProfileVisibility _rpVisibility = RpProfileVisibility.PairsAndSyncshell;
    private RpProfileVisibility _savedRpVisibility = RpProfileVisibility.PairsAndSyncshell;
    private DateTime _saveConfirmTime = DateTime.MinValue;
    private string _savedDescriptionText = string.Empty;
    private string _savedRpFirstNameText = string.Empty;
    private string _savedRpLastNameText = string.Empty;
    private string _savedRpTitleText = string.Empty;
    private string _savedRpAgeText = string.Empty;
    private string _savedRpRaceText = string.Empty;
    private string _savedRpEthnicityText = string.Empty;
    private string _savedRpHeightText = string.Empty;
    private string _savedRpBuildText = string.Empty;
    private string _savedRpResidenceText = string.Empty;
    private string _savedRpOccupationText = string.Empty;
    private string _savedRpAffiliationText = string.Empty;
    private string _savedRpAlignmentText = string.Empty;
    private string _savedRpAdditionalInfoText = string.Empty;
    private Vector3 _rpNameColorVec;
    private string _savedRpNameColorHex = string.Empty;
    private Vector3 _rpCardTextColorVec = new(ImGuiColors.DalamudGrey.X, ImGuiColors.DalamudGrey.Y, ImGuiColors.DalamudGrey.Z);
    private Vector3 _rpAccentColorVec = new(UiSharedService.DefaultAccentColor.X, UiSharedService.DefaultAccentColor.Y, UiSharedService.DefaultAccentColor.Z);
    private ProfileCardStyle _cardStyle = ProfileCardStyle.Default;
    private ProfileCardStyle _savedCardStyle = ProfileCardStyle.Default;
    private bool _standalonePreviewShown;
    private DateTime _lastPreviewPushUtc = DateTime.MinValue;
    private static readonly TimeSpan PreviewRefreshInterval = TimeSpan.FromMilliseconds(200);
    private string _savedRpCustomFieldsJson = string.Empty;
    private string _hydratedRpSnapshot = string.Empty;
    private bool _serverHasNewerVersion = false;
    private DateTime _lastRpPollUtc = DateTime.MinValue;
    private readonly BbCodeToolbar _bbCodeToolbar;
    private enum RpPage { Identity, Appearance, ChatVisibility, Story }
    private int _rpPage;
    private bool _additionalInfoPreview;
    private bool _rpNsfw;
    private bool _savedRpNsfw;
    private bool _hrpNsfw;
    private bool _savedHrpNsfw;
    private bool _rpPortraitDirty;
    private bool _hrpPortraitDirty;
    private volatile bool _rpPortraitStale;
    private volatile bool _hrpPortraitStale;
    private readonly Dictionary<string, (byte[] Source, string Base64)> _base64Cache = new(StringComparer.Ordinal);
    private readonly ProfileImageCropEditor _cropEditor;
    public EditProfileUi(ILogger<EditProfileUi> logger, MareMediator mediator,
        ApiController apiController, UiSharedService uiSharedService, FileDialogManager fileDialogManager,
        UmbraProfileManager umbraProfileManager, PairManager pairManager, PairFactory pairFactory,
        RpConfigService rpConfigService, DalamudUtilService dalamudUtil, IpcManager ipcManager,
        NotificationTracker notificationTracker, IDataManager dataManager,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, $"{Loc.Get("EditProfile.WindowTitle")}###UmbraSyncEditProfileUI", performanceCollectorService)
    {
        IsOpen = false;
        this.SizeConstraints = new()
        {
            MinimumSize = new(768, 512),
            MaximumSize = new(768, 2000)
        };
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _fileDialogManager = fileDialogManager;
        _umbraProfileManager = umbraProfileManager;
        _pairManager = pairManager;
        _pairFactory = pairFactory;
        _rpConfigService = rpConfigService;
        _dalamudUtil = dalamudUtil;
        _ipcManager = ipcManager;
        _notificationTracker = notificationTracker;
        _dataManager = dataManager;
        _bbCodeToolbar = new BbCodeToolbar(uiSharedService);
        _honorificEditor = new HonorificEditor(logger, ipcManager, dalamudUtil, rpConfigService);
        var statusIcons = new Lazy<List<StatusIconInfo>>(LoadStatusIcons);
        _moodlesEditor = new MoodlesEditor(logger, ipcManager, dalamudUtil, rpConfigService, uiSharedService, dataManager, statusIcons);
        _chatIconPicker = new ChatIconPicker();
        _cropEditor = new ProfileImageCropEditor(logger, uiSharedService);

        Mediator.Subscribe<GposeStartMessage>(this, (_) => { _wasOpen = IsOpen; IsOpen = false; });
        Mediator.Subscribe<GposeEndMessage>(this, (_) => IsOpen = _wasOpen);
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<DalamudLoginMessage>(this, (msg) =>
        {
            _rpLoaded = false;
            _hrpLoaded = false;
            _moodlesEditor.ResetSession();
            _honorificEditor.ResetRestoreState();
            _ = Task.Run(_honorificEditor.RunRestoreLoopAsync);
            _ = Task.Run(_moodlesEditor.RunRestoreLoopAsync);
        });
        Mediator.Subscribe<ClearProfileDataMessage>(this, (msg) =>
        {
            if (msg.UserData != null && string.Equals(msg.UserData.UID, _apiController.UID, StringComparison.Ordinal))
            {
                _pfpTextureWrap?.Dispose();
                _pfpTextureWrap = null;
                _rpPfpTextureWrap?.Dispose();
                _rpPfpTextureWrap = null;
                _rpBannerTexture?.Dispose();
                _rpBannerTexture = null;
                _bannerTextureStale = true;
                _rpLoaded = false;
                _hrpLoaded = false;
            }
        });
        Mediator.Subscribe<RemoveWindowMessage>(this, (msg) =>
        {
            if (msg.Window is StandaloneProfileUi ui && string.Equals(ui.Pair.UserData.UID, _apiController.UID, StringComparison.Ordinal))
                _standalonePreviewShown = false;
        });
        Mediator.Subscribe<TargetProfilePreviewMessage>(this, (msg) =>
        {
            if (msg.Profile == null) _cardPreviewOpen = false;
        });
        Mediator.Subscribe<MoodlesMessage>(this, (msg) => _ = Task.Run(_moodlesEditor.RefreshAsync));
        Mediator.Subscribe<MoodlesReadyMessage>(this, (msg) => _ = Task.Run(_moodlesEditor.RunRestoreLoopAsync));
        Mediator.Subscribe<ZoneSwitchEndMessage>(this, (msg) =>
        {
            _moodlesEditor.ResetRestoreState();
            _honorificEditor.ResetRestoreState();
            _ = Task.Run(_moodlesEditor.RunRestoreLoopAsync);
            _ = Task.Run(_honorificEditor.RunRestoreLoopAsync);
        });
        Mediator.Subscribe<HonorificMessage>(this, (msg) =>
        {
            _honorificEditor.RefreshFromData(msg.NewHonorificTitle);
            if (!string.IsNullOrEmpty(msg.NewHonorificTitle))
            {
                _ = Task.Run(() => _honorificEditor.TrySaveBackupFromBase64Async(msg.NewHonorificTitle));
            }
        });
        Mediator.Subscribe<HonorificReadyMessage>(this, (msg) => _ = Task.Run(_honorificEditor.RunRestoreLoopAsync));
        Mediator.Subscribe<ConnectedMessage>(this, (msg) => _ = Task.Run(_moodlesEditor.RunRestoreLoopAsync));
        Mediator.Subscribe<ConnectedMessage>(this, (msg) => _ = Task.Run(_honorificEditor.RunRestoreLoopAsync));
    }

    public override void OnOpen() => RefreshFromServer();
    public void RefreshFromServer()
    {
        _rpLoaded = false;
        _hrpLoaded = false;
        var charName = _dalamudUtil.GetPlayerName();
        var worldId = _dalamudUtil.GetHomeWorldId();
        if (!string.IsNullOrEmpty(_apiController.UID)
            && !string.IsNullOrEmpty(charName) && !string.Equals(charName, "--", StringComparison.Ordinal)
            && worldId > 0)
        {
            _ = _umbraProfileManager.GetUmbraProfileFromService(
                new UserData(_apiController.UID),
                charName,
                worldId);
        }
    }


    private UmbraProfileData BuildPreviewProfileData()
    {
        return new UmbraProfileData(
            IsFlagged: false,
            IsNSFW: _hrpNsfw,
            Base64ProfilePicture: CachedBase64("hrp", _profileImage),
            Description: _descriptionText,
            Base64RpProfilePicture: _rpProfileImage.Length > 0 ? CachedBase64("rp", _rpProfileImage) : null,
            RpDescription: _rpDescriptionText,
            IsRpNSFW: _rpNsfw,
            RpFirstName: _rpFirstNameText,
            RpLastName: _rpLastNameText,
            RpTitle: _rpTitleText,
            RpAge: _rpAgeText,
            RpRace: _rpRaceText,
            RpEthnicity: _rpEthnicityText,
            RpHeight: _rpHeightText,
            RpBuild: _rpBuildText,
            RpResidence: _rpResidenceText,
            RpOccupation: _rpOccupationText,
            RpAffiliation: _rpAffiliationText,
            RpAlignment: _rpAlignmentText,
            RpAdditionalInfo: _rpAdditionalInfoText,
            RpNameColor: UiSharedService.Vector4ToHex(new Vector4(_rpNameColorVec, 1f)),
            RpCustomFields: _rpCustomFields.Count > 0 ? _rpCustomFields : null,
            ChatIcon: _chatIconPicker.SelectedIcon,
            RpLevel: _rpLevel,
            Base64RpBanner: _rpBannerImage.Length > 0 ? CachedBase64("banner", _rpBannerImage) : null,
            RpCardStyle: _cardStyle.Serialize()
        );
    }

    private string CachedBase64(string key, byte[] data)
    {
        if (data.Length == 0) return string.Empty;
        if (_base64Cache.TryGetValue(key, out var cached) && ReferenceEquals(cached.Source, data)) return cached.Base64;
        var base64 = Convert.ToBase64String(data);
        _base64Cache[key] = (data, base64);
        return base64;
    }
    private void PushStandalonePreview()
    {
        var myUserData = new UserData(_apiController.UID, _apiController.DisplayName);
        var pair = _pairManager.GetPairByUID(_apiController.UID) ?? _pairFactory.Create(myUserData);

        SetProfilePreview();
        _standalonePreviewShown = true;
        Mediator.Publish(new ProfileOpenStandaloneMessage(pair, _dalamudUtil.GetPlayerName(), _dalamudUtil.GetHomeWorldId()));
    }

    public RpProfileVisibility GetRpVisibility() => _rpConfigService.GetCurrentCharacterProfile().RpVisibility;
    
    public bool SetRpVisibility(RpProfileVisibility visibility)
    {
        var charName = _dalamudUtil.GetPlayerName();
        var worldId = _dalamudUtil.GetHomeWorldId();
        if (string.IsNullOrEmpty(charName) || string.Equals(charName, "--", StringComparison.Ordinal) || worldId == 0)
            return false;

        var profile = _rpConfigService.GetCurrentCharacterProfile();
        if (profile.RpVisibility == visibility) return true;
        profile.RpVisibility = visibility;
        _rpConfigService.Save();
        _rpVisibility = visibility;
        _savedRpVisibility = visibility;

        _ = Task.Run(async () =>
        {
            try
            {
                var curProfile = await _apiController.UserGetProfile(new UserDto(new UserData(_apiController.UID, _apiController.DisplayName))).ConfigureAwait(false);
                _logger.LogInformation("Setting RP visibility to {visibility} for {uid}", visibility, _apiController.UID);
                await _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID, _apiController.DisplayName), curProfile.Disabled, curProfile.IsNSFW, curProfile.ProfilePictureBase64, curProfile.Description)
                {
                    CharacterName = charName,
                    WorldId = worldId,
                    RpProfilePictureBase64 = curProfile.RpProfilePictureBase64,
                    RpDescription = curProfile.RpDescription,
                    IsRpNSFW = curProfile.IsRpNSFW,
                    RpFirstName = curProfile.RpFirstName,
                    RpLastName = curProfile.RpLastName,
                    RpTitle = curProfile.RpTitle,
                    RpAge = curProfile.RpAge,
                    RpRace = curProfile.RpRace,
                    RpEthnicity = curProfile.RpEthnicity,
                    RpHeight = curProfile.RpHeight,
                    RpBuild = curProfile.RpBuild,
                    RpResidence = curProfile.RpResidence,
                    RpOccupation = curProfile.RpOccupation,
                    RpAffiliation = curProfile.RpAffiliation,
                    RpAlignment = curProfile.RpAlignment,
                    RpAdditionalInfo = curProfile.RpAdditionalInfo,
                    RpNameColor = curProfile.RpNameColor,
                    RpCardStyle = curProfile.RpCardStyle,
                    RpCustomFields = curProfile.RpCustomFields,
                    MoodlesData = curProfile.MoodlesData,
                    RpVisibility = visibility
                }).ConfigureAwait(false);
                Mediator.Publish(new ClearProfileDataMessage(new UserData(_apiController.UID), charName, worldId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save RP visibility");
                Mediator.Publish(new NotificationMessage(Loc.Get("EditProfile.SaveErrorTitle"), Loc.Get("EditProfile.SaveErrorBody"), NotificationType.Error));
            }
        });
        return true;
    }

    private void PushPreview()
    {
        var previewProfileData = SetProfilePreview();
        Mediator.Publish(new TargetProfilePreviewMessage(previewProfileData));
    }
    
    private UmbraProfileData SetProfilePreview()
    {
        var previewProfileData = BuildPreviewProfileData();
        var myUserData = new UserData(_apiController.UID, _apiController.DisplayName);
        var (charName, worldId) = _umbraProfileManager.ResolveCharacter(myUserData);
        _umbraProfileManager.SetPreviewProfile(myUserData, charName, worldId, previewProfileData);
        return previewProfileData;
    }

    public override void PreDraw()
    {
        UiSharedService.BeginProfileAccent(_cardStyle.AccentColor);
        base.PreDraw();
    }

    public override void PostDraw()
    {
        base.PostDraw();
        UiSharedService.EndProfileAccent();
    }

    protected override void DrawInternal()
    {

        using var profileAccent = UiSharedService.PushProfileAccent(_cardStyle.AccentColor);
        using var profileColors = UiSharedService.PushThemeContentColors();
        if (_previewRefreshPending && DateTime.UtcNow - _lastPreviewPushUtc >= PreviewRefreshInterval)
        {
            _previewRefreshPending = false;
            _lastPreviewPushUtc = DateTime.UtcNow;
            if (_cardPreviewOpen) PushPreview();
            else if (_standalonePreviewShown) SetProfilePreview();
        }

        var accent = UiSharedService.AccentColor;
        if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;

        DrawProfileTabButtons(accent);
        _cropEditor.Draw();
    }

    public void DrawInline() => DrawInternal();

    private void DrawProfileTabButtons(Vector4 accent)
    {
        var labels = new[] { "RP", "HRP" };
        var icons = new[] { FontAwesomeIcon.Scroll, FontAwesomeIcon.User };
        Components.AccentTabBar.Draw("profile", labels, icons, accent, ref _activeTab);

        DrawProfileContent(_activeTab == 0);
    }

    private void DrawRefreshControl()
    {
        if (_uiSharedService.IconButton(FontAwesomeIcon.Sync))
        {
            RefreshFromServer();
        }
        UiSharedService.AttachToolTip("Recharger le profil depuis le serveur");

        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.ExternalLinkAlt, Loc.Get("EditProfile.EnrichedProfile.Button")))
            Dalamud.Utility.Util.OpenLink("https://connect.ashfall-codex.dev/account");
        UiSharedService.AttachToolTip(Loc.Get("EditProfile.EnrichedProfile.Tooltip"));

        if (_serverHasNewerVersion)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow))
                ImGui.TextUnformatted("Mise à jour disponible");
            UiSharedService.AttachToolTip("Le profil a été modifié sur le serveur (Ashfall Connect ?). Cliquez sur le bouton de rechargement pour voir les changements.");
        }
    }

    private static string ComputeRpSnapshotFromProfile(UmbraProfileData p)
    {
        var customJson = p.RpCustomFields == null
            ? string.Empty
            : System.Text.Json.JsonSerializer.Serialize(p.RpCustomFields.OrderBy(f => f.Order).Select(f => new { f.Name, f.Value }));
        return string.Join('\u0001',
            p.RpFirstName ?? "", p.RpLastName ?? "", p.RpTitle ?? "",
            p.RpAge ?? "", p.RpRace ?? "", p.RpEthnicity ?? "",
            p.RpHeight ?? "", p.RpBuild ?? "", p.RpResidence ?? "",
            p.RpOccupation ?? "", p.RpAffiliation ?? "", p.RpAlignment ?? "",
            p.RpAdditionalInfo ?? "", p.RpNameColor ?? "", p.RpCardStyle ?? "",
            p.IsRpNSFW.ToString(), p.RpLevel.ToString(), p.RpVisibility?.ToString() ?? "",
            customJson);
    }

    private async Task PollServerForRpUpdatesAsync()
    {
        if (string.IsNullOrEmpty(_apiController.UID) || !_apiController.IsConnected) return;
        try
        {
            var charName = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false);
            var worldId = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false);
            var dto = await _apiController.UserGetProfile(new UmbraSync.API.Dto.User.UserDto(new UserData(_apiController.UID))
            {
                CharacterName = charName,
                WorldId = worldId,
            }).ConfigureAwait(false);

            List<RpCustomField>? customFields = null;
            if (!string.IsNullOrEmpty(dto.RpCustomFields))
            {
                try { customFields = System.Text.Json.JsonSerializer.Deserialize<List<RpCustomField>>(dto.RpCustomFields); }
                catch { customFields = null; }
            }
            var pseudoProfile = new UmbraProfileData(
                IsFlagged: false, IsNSFW: dto.IsNSFW ?? false,
                Base64ProfilePicture: string.Empty, Description: string.Empty,
                Base64RpProfilePicture: null, RpDescription: dto.RpDescription, IsRpNSFW: dto.IsRpNSFW ?? false,
                RpFirstName: dto.RpFirstName, RpLastName: dto.RpLastName, RpTitle: dto.RpTitle, RpAge: dto.RpAge,
                RpRace: dto.RpRace, RpEthnicity: dto.RpEthnicity,
                RpHeight: dto.RpHeight, RpBuild: dto.RpBuild, RpResidence: dto.RpResidence,
                RpOccupation: dto.RpOccupation, RpAffiliation: dto.RpAffiliation,
                RpAlignment: dto.RpAlignment, RpAdditionalInfo: dto.RpAdditionalInfo,
                RpNameColor: dto.RpNameColor,
                RpCustomFields: customFields,
                ChatIcon: dto.ChatIcon ?? 0, RpLevel: dto.RpLevel ?? 0, RpVisibility: dto.RpVisibility,
                RpCardStyle: dto.RpCardStyle);

            var serverSnapshot = ComputeRpSnapshotFromProfile(pseudoProfile);
            if (!string.Equals(serverSnapshot, _hydratedRpSnapshot, StringComparison.Ordinal))
            {
                _serverHasNewerVersion = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Échec du poll RP profile (silencieux)");
        }
    }

    private void DrawProfileContent(bool isRp)
    {
        _moodlesEditor.EnsureRefreshed();
        
        if (isRp && _rpLoaded && !_serverHasNewerVersion
            && DateTime.UtcNow - _lastRpPollUtc > TimeSpan.FromSeconds(30))
        {
            _lastRpPollUtc = DateTime.UtcNow;
            _ = PollServerForRpUpdatesAsync();
        }

        var umbraProfile = _umbraProfileManager.GetUmbraProfile(new UserData(_apiController.UID));

        if (umbraProfile.IsFlagged)
        {
            _uiSharedService.BigText(isRp ? "Profil RP" : "Profil HRP");
            UiSharedService.ColorTextWrapped(umbraProfile.Description, UiSharedService.AccentColor);
            return;
        }

        _uiSharedService.BigText(isRp ? "Profil RP" : "Profil HRP");
        if (isRp)
        {
            ImGui.SameLine();
            DrawRefreshControl();
        }
        else
        {
            var (icon, label) = ProfilePreviewButtonContent();
            SameLineRightAligned(_uiSharedService.GetIconTextButtonSize(icon, label));
            DrawProfilePreviewButton(icon, label);
        }
        if (!isRp) DrawNsfwToggle(false);
        ImGuiHelpers.ScaledDummy(2f);
        DrawSaveCard(isRp);
        ImGuiHelpers.ScaledDummy(new Vector2(0f, ImGui.GetStyle().ItemSpacing.Y / 2));

        if (isRp)
        {
            var curName = _dalamudUtil.GetPlayerName();
            var curWorld = _dalamudUtil.GetHomeWorldId();
            bool playerReady = !string.IsNullOrEmpty(curName)
                && !string.Equals(curName, "--", StringComparison.Ordinal) && curWorld > 0;
            // Clé perso courante (même format que RpConfigService.GetCharacterKey).
            var curKey = playerReady ? $"{curName}@{curWorld}" : string.Empty;

            // Switch d'alt fenêtre ouverte : si le perso a changé depuis le dernier chargement,
            // on recharge pour ne pas garder les champs (ni réécrire le config) de l'ancien perso.
            if (playerReady && !string.Equals(curKey, _rpLoadedForKey, StringComparison.Ordinal))
                _rpLoaded = false;

            if (playerReady && !_rpLoaded && !string.Equals(umbraProfile.Description, "Loading Data from server...", StringComparison.Ordinal))
            {
                _rpDescriptionText = umbraProfile.RpDescription ?? string.Empty;
                _rpFirstNameText = umbraProfile.RpFirstName ?? string.Empty;
                _rpLastNameText = umbraProfile.RpLastName ?? string.Empty;
                _rpTitleText = umbraProfile.RpTitle ?? string.Empty;
                _rpAgeText = umbraProfile.RpAge ?? string.Empty;
                _rpRaceText = umbraProfile.RpRace ?? string.Empty;
                _rpEthnicityText = umbraProfile.RpEthnicity ?? string.Empty;
                _rpHeightText = umbraProfile.RpHeight ?? string.Empty;
                _rpBuildText = umbraProfile.RpBuild ?? string.Empty;
                _rpResidenceText = umbraProfile.RpResidence ?? string.Empty;
                _rpOccupationText = umbraProfile.RpOccupation ?? string.Empty;
                _rpAffiliationText = umbraProfile.RpAffiliation ?? string.Empty;
                _rpAlignmentText = umbraProfile.RpAlignment ?? string.Empty;
                _rpAdditionalInfoText = umbraProfile.RpAdditionalInfo ?? string.Empty;

                var configProfile = _rpConfigService.GetCurrentCharacterProfile();
                // Source de vérité = serveur (Connect peut le modifier entre deux ouvertures du
                // plugin). Fallback config locale uniquement si le serveur a renvoyé null (perso
                // jamais sync). Une liste vide côté serveur signifie "tout supprimé" et est respectée.
                var customFieldsSource = umbraProfile.RpCustomFields ?? configProfile.RpCustomFields;
                _rpCustomFields = customFieldsSource
                    .Select(f => new RpCustomField { Name = f.Name, Value = f.Value, Order = f.Order })
                    .ToList();
                configProfile.RpCustomFields = _rpCustomFields
                    .Select(f => new RpCustomField { Name = f.Name, Value = f.Value, Order = f.Order })
                    .ToList();
                configProfile.RpFirstName = _rpFirstNameText;
                configProfile.RpLastName = _rpLastNameText;
                configProfile.RpTitle = _rpTitleText;
                configProfile.RpAge = _rpAgeText;
                configProfile.RpRace = _rpRaceText;
                configProfile.RpEthnicity = _rpEthnicityText;
                configProfile.RpHeight = _rpHeightText;
                configProfile.RpBuild = _rpBuildText;
                configProfile.RpResidence = _rpResidenceText;
                configProfile.RpOccupation = _rpOccupationText;
                configProfile.RpAffiliation = _rpAffiliationText;
                configProfile.RpAlignment = _rpAlignmentText;
                configProfile.RpAdditionalInfo = _rpAdditionalInfoText;
                configProfile.RpNameColor = umbraProfile.RpNameColor ?? string.Empty;
                configProfile.RpCardStyle = umbraProfile.RpCardStyle ?? string.Empty;
                if (umbraProfile.RpVisibility.HasValue) configProfile.RpVisibility = umbraProfile.RpVisibility.Value;
                _rpConfigService.Save();

                _chatIconPicker.SelectedIcon = umbraProfile.ChatIcon > 0 ? umbraProfile.ChatIcon : configProfile.ChatIcon;
                _rpLevel = umbraProfile.RpLevel != 0 ? umbraProfile.RpLevel : configProfile.RpLevel;
                _rpVisibility = configProfile.RpVisibility;

                var nameColorHex = umbraProfile.RpNameColor ?? string.Empty;
                if (!string.IsNullOrEmpty(nameColorHex))
                {
                    var v4 = UiSharedService.HexToVector4(nameColorHex);
                    _rpNameColorVec = new Vector3(v4.X, v4.Y, v4.Z);
                }
                else
                {
                    var accent = UiSharedService.AccentColor;
                    _rpNameColorVec = new Vector3(accent.X, accent.Y, accent.Z);
                }

                SetCardStyle(umbraProfile.CardStyle.Value);

                _rpNsfw = umbraProfile.IsRpNSFW;

                if (!_rpPortraitDirty)
                {
                    try
                    {
                        _rpProfileImage = !string.IsNullOrEmpty(umbraProfile.Base64RpProfilePicture) ? Convert.FromBase64String(umbraProfile.Base64RpProfilePicture) : [];
                        _rpPfpTextureWrap?.Dispose();
                        _rpPfpTextureWrap = _rpProfileImage.Length > 0 ? _uiSharedService.LoadImage(_rpProfileImage) : null;
                        _logger.LogDebug("Loaded RP profile image, size: {size}", _rpProfileImage.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load RP profile image");
                        _rpProfileImage = [];
                        _rpPfpTextureWrap = null;
                    }
                }

                if (!_rpBannerDirty)
                {
                    _rpBannerImage = umbraProfile.RpBannerData.Value;
                    _bannerTextureStale = true;
                }

                _rpLoaded = true;
                _rpLoadedForKey = curKey;
                _hydratedRpSnapshot = ComputeRpSnapshotFromProfile(umbraProfile);
                _serverHasNewerVersion = false;
                _lastRpPollUtc = DateTime.UtcNow;
                SnapshotSavedState(true);
            }
        }
        else
        {
            if (!_hrpLoaded && !string.Equals(umbraProfile.Description, "Loading Data from server...", StringComparison.Ordinal))
            {
                _descriptionText = umbraProfile.Description;
                _hrpNsfw = umbraProfile.IsNSFW;

                if (!_hrpPortraitDirty)
                {
                    try
                    {
                        _profileImage = umbraProfile.ImageData.Value;
                        _pfpTextureWrap?.Dispose();
                        _pfpTextureWrap = _profileImage.Length > 0 ? _uiSharedService.LoadImage(_profileImage) : null;
                        _logger.LogDebug("Loaded HRP profile image, size: {size}", _profileImage.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load HRP profile image");
                        _profileImage = [];
                        _pfpTextureWrap = null;
                    }
                }

                _hrpLoaded = true;
                SnapshotSavedState(false);
            }
        }

        if (isRp)
        {
            UnderlineTabs.Draw("rpPages",
                [Loc.Get("EditProfile.Page.Identity"), Loc.Get("EditProfile.Page.Appearance"),
                 Loc.Get("EditProfile.Page.ChatVisibility"), Loc.Get("EditProfile.Page.Story")],
                [FontAwesomeIcon.IdCard, FontAwesomeIcon.Palette, FontAwesomeIcon.Comments, FontAwesomeIcon.BookOpen],
                ref _rpPage);
            ImGuiHelpers.ScaledDummy(2f);
        }

        RefreshPortraitTextures();
        
        string bodyId = isRp ? $"##profileBody{_rpPage}" : "##profileBodyHrp";
        using (var body = ImRaii.Child(bodyId, Vector2.Zero))
        {
            if (body)
            {
                if (!isRp)
                    DrawIdentityCard(false);
                else
                {
                    switch ((RpPage)_rpPage)
                    {
                        case RpPage.Identity:
                            DrawIdentityCard(true);
                            break;
                        case RpPage.Appearance:
                            UiSharedService.BeginSectionCard(Loc.Get("EditProfile.Card.ProfileCard"), FontAwesomeIcon.Image);
                            DrawProfileCardSection();
                            break;
                        case RpPage.ChatVisibility:
                            DrawChatVisibilityPage();
                            break;
                        default:
                            DrawStoryPage();
                            break;
                    }
                }
                UiSharedService.EndSectionCard();
            }
        }
    }

    private void DrawIdentityCard(bool isRp)
    {
        var pfpTexture = isRp ? _rpPfpTextureWrap : _pfpTextureWrap;
        var pfpBytes = isRp ? _rpProfileImage : _profileImage;

        UiSharedService.BeginSectionCard(
            isRp ? Loc.Get("EditProfile.Card.Identity") : Loc.Get("PopoutProfile.DescriptionLabel"),
            isRp ? FontAwesomeIcon.IdCard : FontAwesomeIcon.AlignLeft);

        var w = ImGui.GetContentRegionAvail().X;
        float imgSz = 150f * ImGuiHelpers.GlobalScale;
        float spacing = 16f * ImGuiHelpers.GlobalScale;

        ImGui.BeginGroup();
        var portraitRounding = 10f * ImGuiHelpers.GlobalScale;
        if (pfpTexture != null && pfpBytes.Length > 0)
        {
            float ratio = (float)pfpTexture.Width / pfpTexture.Height;
            Vector2 drawSize;
            if (ratio > 1) drawSize = new Vector2(imgSz, imgSz / ratio);
            else drawSize = new Vector2(imgSz * ratio, imgSz);

            var cursor = ImGui.GetCursorPos();
            var offsetX = (imgSz - drawSize.X) / 2f;
            var offsetY = (imgSz - drawSize.Y) / 2f;
            var screenPos = ImGui.GetCursorScreenPos();
            var pMin = new Vector2(screenPos.X + offsetX, screenPos.Y + offsetY);
            var pMax = new Vector2(pMin.X + drawSize.X, pMin.Y + drawSize.Y);

            ImGui.GetWindowDrawList().AddImageRounded(pfpTexture.Handle, pMin, pMax,
                Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), portraitRounding);

            ImGui.SetCursorPos(cursor + new Vector2(0, imgSz + ImGui.GetStyle().ItemSpacing.Y));
        }
        else
        {
            var cursor = ImGui.GetCursorPos();
            var cursorScreen = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(cursorScreen, cursorScreen + new Vector2(imgSz),
                ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f)), portraitRounding);
            ImGui.GetWindowDrawList().AddRect(cursorScreen, cursorScreen + new Vector2(imgSz),
                ImGui.GetColorU32(ImGuiCol.Border), portraitRounding);
            var iconSize = UiSharedService.GetIconSize(FontAwesomeIcon.Image);
            ImGui.SetCursorPos(cursor + new Vector2((imgSz - iconSize.X) / 2, (imgSz - iconSize.Y) / 2));
            _uiSharedService.IconText(FontAwesomeIcon.Image, ImGuiColors.DalamudGrey);
            ImGui.SetCursorPos(cursor + new Vector2(0, imgSz + ImGui.GetStyle().ItemSpacing.Y));
        }

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.FileImage, Loc.Get("EditProfile.SelectImageButton"), imgSz))
            SelectPortrait(isRp);
        UiSharedService.AttachToolTip(Loc.Get("EditProfile.Portrait.Recommended"));
        if (_showFileDialogError)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("EditProfile.ImageSizeError"), ImGuiColors.DalamudRed, imgSz);
        }
        ImGui.EndGroup();
        ImGui.SameLine(0, spacing);
        ImGui.BeginGroup();

        var fieldW = w - imgSz - spacing - 12f * ImGuiHelpers.GlobalScale;
        var halfFieldW = (fieldW - ImGui.GetStyle().ItemSpacing.X) / 2;

        void DrawFieldPair(string label1, ref string text1, int max1, string label2, ref string text2, int max2, string? help1 = null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, label1);
            if (help1 != null) _uiSharedService.DrawHelpText(help1);
            ImGui.SameLine(halfFieldW + ImGui.GetStyle().ItemSpacing.X);
            ImGui.TextColored(ImGuiColors.DalamudGrey, label2);
            ImGui.SetNextItemWidth(halfFieldW);
            ImGui.InputText("##" + label1, ref text1, max1);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(halfFieldW);
            ImGui.InputText("##" + label2, ref text2, max2);
        }

        if (isRp)
        {
            DrawFieldPair(Loc.Get("UserProfile.RpFirstName"), ref _rpFirstNameText, 30, Loc.Get("UserProfile.RpLastName"), ref _rpLastNameText, 30);

            var vanillaFirstName = GetVanillaFirstName();
            if (!string.IsNullOrEmpty(_rpFirstNameText) && vanillaFirstName != null
                && !RpFirstNameContainsVanilla(_rpFirstNameText, vanillaFirstName))
            {
                UiSharedService.ColorTextWrapped(string.Format(Loc.Get("EditProfile.RpFirstNameMismatch"), vanillaFirstName), ImGuiColors.DalamudRed);
            }

            DrawFieldPair(Loc.Get("UserProfile.RpTitle"), ref _rpTitleText, 100, Loc.Get("UserProfile.RpAge"), ref _rpAgeText, 50,
                Loc.Get("EditProfile.Help.Title"));
            DrawFieldPair(Loc.Get("UserProfile.RpRace"), ref _rpRaceText, 50, Loc.Get("UserProfile.RpEthnicity"), ref _rpEthnicityText, 50);
            DrawFieldPair(Loc.Get("UserProfile.RpHeight"), ref _rpHeightText, 50, Loc.Get("UserProfile.RpBuild"), ref _rpBuildText, 100);
        }
        else
        {
            using (_uiSharedService.GameFont.Push())
            {
                using var _ = ImRaii.PushId("hrp_desc");
                ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("PopoutProfile.DescriptionLabel"));
                ImGui.SetNextItemWidth(fieldW);
                ImGui.InputTextMultiline("##description_multi", ref _descriptionText, 1000,
                        new Vector2(fieldW, 150 * ImGuiHelpers.GlobalScale));
            }
        }
        ImGui.EndGroup();
    }

    private void RefreshPortraitTextures()
    {
        if (_rpPortraitStale)
        {
            _rpPortraitStale = false;
            _rpPfpTextureWrap?.Dispose();
            _rpPfpTextureWrap = _rpProfileImage.Length > 0 ? _uiSharedService.LoadImage(_rpProfileImage) : null;
        }
        if (_hrpPortraitStale)
        {
            _hrpPortraitStale = false;
            _pfpTextureWrap?.Dispose();
            _pfpTextureWrap = _profileImage.Length > 0 ? _uiSharedService.LoadImage(_profileImage) : null;
        }
    }

    private void SelectPortrait(bool isRp)
    {
        _showFileDialogError = false;
        _fileDialogManager.OpenFileDialog(Loc.Get("EditProfile.SelectImageButton"), "Image files{.png,.jpg,.jpeg}", (success, name) =>
        {
            if (!success) return;
            _cropEditor.Open(name, ProfileImageKind.Portrait, portrait =>
            {
                if (isRp)
                {
                    _rpProfileImage = portrait;
                    _rpPortraitDirty = true;
                    _rpPortraitStale = true;
                }
                else
                {
                    _profileImage = portrait;
                    _hrpPortraitDirty = true;
                    _hrpPortraitStale = true;
                }
                _previewRefreshPending = true;
            }, () => _showFileDialogError = true);
        });
    }

    private void DrawChatVisibilityPage()
    {
        UiSharedService.BeginSectionCard(Loc.Get("EditProfile.Card.TitleChat"), FontAwesomeIcon.Crown);

        _honorificEditor.Draw();

        ImGuiHelpers.ScaledDummy(new Vector2(0f, 6f));

        _chatIconPicker.Draw();

        UiSharedService.BeginSectionCard(Loc.Get("EditProfile.Card.Visibility"), FontAwesomeIcon.SlidersH);

        DrawRpLevelSelector();

        ImGuiHelpers.ScaledDummy(new Vector2(0f, 6f));

        DrawRpVisibilitySelector();

        ImGuiHelpers.ScaledDummy(new Vector2(0f, 6f));

        DrawNsfwToggle(true);
    }

    private void DrawStoryPage()
    {
        UiSharedService.BeginSectionCard(Loc.Get("EditProfile.Card.Details"), FontAwesomeIcon.MapMarkerAlt);
        var w = ImGui.GetContentRegionAvail().X;

        DrawStoryField(Loc.Get("UserProfile.RpResidence"), ref _rpResidenceText, w);
        DrawStoryField(Loc.Get("UserProfile.RpOccupation"), ref _rpOccupationText, w, Loc.Get("EditProfile.Help.Occupation"));
        DrawStoryField(Loc.Get("UserProfile.RpAffiliation"), ref _rpAffiliationText, w);
        DrawStoryField(Loc.Get("UserProfile.RpAlignment"), ref _rpAlignmentText, w);

        ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));

        DrawCustomFieldsSection(w);

        UiSharedService.BeginSectionCard(Loc.Get("EditProfile.Card.Traits"), FontAwesomeIcon.Star);

        _moodlesEditor.DrawSection();

        UiSharedService.BeginSectionCard(Loc.Get("UserProfile.RpAdditionalInfo"), FontAwesomeIcon.FeatherAlt);
        DrawAdditionalInfo();
    }

    private void DrawStoryField(string label, ref string text, float width, string? help = null)
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        if (help != null) _uiSharedService.DrawHelpText(help);
        ImGui.SetNextItemWidth(width);
        ImGui.InputText("##" + label, ref text, 100);
    }

    // Écrire en BBCode, ou voir le rendu final tel que sur la fiche.
    private void DrawAdditionalInfo()
    {
        var accent = UiSharedService.AccentColor with { W = 0.35f };
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.PenNib, Loc.Get("EditProfile.Story.Write"),
                buttonColor: _additionalInfoPreview ? null : accent))
            _additionalInfoPreview = false;
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Eye, Loc.Get("EditProfile.Story.Preview"),
                buttonColor: _additionalInfoPreview ? accent : null))
            _additionalInfoPreview = true;
        ImGuiHelpers.ScaledDummy(2f);

        using (_uiSharedService.GameFont.Push())
        {
            if (_additionalInfoPreview)
            {
                if (string.IsNullOrWhiteSpace(_rpAdditionalInfoText))
                    ImGui.TextColored(ImGuiColors.DalamudGrey3, Loc.Get("EditProfile.Story.PreviewEmpty"));
                else
                    BbCodeRenderer.Render(_rpAdditionalInfoText, ImGui.GetContentRegionAvail().X - (4f * ImGuiHelpers.GlobalScale));
                return;
            }

            _bbCodeToolbar.Draw(ref _rpAdditionalInfoText);
            ImGui.TextColored(ImGuiColors.DalamudGrey3, Loc.Get("UserProfile.RpAdditionalInfoWrapHint"));
            ResizableTextArea.Draw("##additional_info", ref _rpAdditionalInfoText, 3000, defaultHeight: 260f, minHeight: 120f, maxHeight: 900f);
        }
    }

    private void DrawProfileCardSection()
    {
        if (_bannerTextureStale)
        {
            _bannerTextureStale = false;
            _rpBannerTexture?.Dispose();
            _rpBannerTexture = _rpBannerImage.Length > 0 ? _uiSharedService.LoadImage(_rpBannerImage) : null;
        }

        var scale = ImGuiHelpers.GlobalScale;
        float labelWidth = new[]
            {
                "EditProfile.ProfileAccent", "EditProfile.NameColor", "EditProfile.CardTextColor",
                "EditProfile.Banner.Dimming", "EditProfile.Banner.Fade", "EditProfile.Banner.FadeRadius",
                "EditProfile.Background.Image", "EditProfile.Background.Dimming", "EditProfile.Background.Fade",
                "EditProfile.Background.Veil", "EditProfile.Background.TitleColor", "EditProfile.Background.LabelColor",
                "EditProfile.Background.TextColor",
            }
            .Max(key => ImGui.CalcTextSize(Loc.Get(key)).X) + (12f * scale);
        float sliderWidth = MathF.Min(240f * scale, MathF.Max(ImGui.GetContentRegionAvail().X - labelWidth, 80f * scale));

        DrawCardStrip();
        ImGuiHelpers.ScaledDummy(2f);
        DrawCardColors(labelWidth);

        UiSharedService.BeginSectionCard(Loc.Get("EditProfile.Card.Banner"), FontAwesomeIcon.FileImage);
        DrawBannerSettings(labelWidth, sliderWidth);

        UiSharedService.BeginSectionCard(Loc.Get("EditProfile.Background"), FontAwesomeIcon.Scroll);
        DrawSheetBackgroundSettings(labelWidth, sliderWidth);

        UiSharedService.BeginSectionCard(Loc.Get("EditProfile.Card.SheetColors"), FontAwesomeIcon.Palette);
        DrawSheetColors(labelWidth);
    }
    
    private void DrawCardStrip()
    {
        using var accent = UiSharedService.PushProfileAccent(_cardStyle.AccentColor);
        var scale = ImGuiHelpers.GlobalScale;
        var width = ImGui.GetContentRegionAvail().X;
        var height = Math.Clamp(width / 5f, 72f * scale, 112f * scale);
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, height);
        ImGui.Dummy(new Vector2(width, height));
        UiSharedService.AttachToolTip(Loc.Get("EditProfile.CardStrip.Tooltip"));

        var drawList = ImGui.GetWindowDrawList();
        var rounding = UiSharedService.RadiusCard * scale;
        var background = UiSharedService.ThemeWindowBg;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(background), rounding);
        if (_rpBannerTexture != null)
            ProfileBanner.Draw(drawList, _rpBannerTexture, min, max, background, rounding, _cardStyle);

        var padding = 10f * scale;
        drawList.PushClipRect(min, max, true);

        var textX = min.X + padding;
        if (_rpPfpTextureWrap != null && _rpProfileImage.Length > 0)
        {
            var side = height - (padding * 2f);
            var portraitMin = new Vector2(min.X + padding, min.Y + padding);
            var (uv0, uv1) = ProfileBanner.CoverUv(_rpPfpTextureWrap.Width, _rpPfpTextureWrap.Height, side, side);
            drawList.AddImageRounded(_rpPfpTextureWrap.Handle, portraitMin, portraitMin + new Vector2(side),
                uv0, uv1, ImGui.GetColorU32(Vector4.One), rounding);
            textX = portraitMin.X + side + padding;
        }

        var y = min.Y + padding;
        var name = $"{_rpFirstNameText} {_rpLastNameText}".Trim();
        if (name.Length > 0)
        {
            using (_uiSharedService.UidFont.Push())
            {
                drawList.AddText(new Vector2(textX, y), ImGui.GetColorU32(new Vector4(_rpNameColorVec, 1f)), name);
                y += ImGui.GetTextLineHeight();
            }
        }

        var infoColor = ImGui.GetColorU32(UiSharedService.ProfileCardTextColor(_cardStyle.TextColor));
        var identity = string.Join(" · ", new[] { _rpRaceText, _rpEthnicityText, _rpAgeText }.Where(v => !string.IsNullOrWhiteSpace(v)));
        bool anyLine = false;
        foreach (var line in new[] { _rpTitleText, identity, _rpOccupationText })
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            drawList.AddText(new Vector2(textX, y), infoColor, line);
            y += ImGui.GetTextLineHeight();
            anyLine = true;
        }
        if (!anyLine)
            drawList.AddText(new Vector2(textX, y), infoColor, Loc.Get("EditProfile.CardStrip.Sample"));

        if (_rpBannerTexture == null)
        {
            var hint = Loc.Get("EditProfile.CardStrip.NoBanner");
            drawList.AddText(max - ImGui.CalcTextSize(hint) - new Vector2(padding), ImGui.GetColorU32(ImGuiColors.DalamudGrey3), hint);
        }

        drawList.PopClipRect();
        drawList.AddRect(min, max, ImGui.GetColorU32(UiSharedService.ThemeCardBorder), rounding, ImDrawFlags.None, 1f * scale);
    }

    private void DrawPreviewButtons()
    {
        var (profileIcon, profileLabel) = ProfilePreviewButtonContent();
        var cardIcon = _cardPreviewOpen ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.IdCard;
        var cardLabel = Loc.Get(_cardPreviewOpen ? "EditProfile.CardPreview.Close" : "EditProfile.Banner.Preview");
        float width = _uiSharedService.GetIconTextButtonSize(profileIcon, profileLabel)
            + ImGui.GetStyle().ItemSpacing.X
            + _uiSharedService.GetIconTextButtonSize(cardIcon, cardLabel);

        SameLineRightAligned(width);
        DrawProfilePreviewButton(profileIcon, profileLabel);
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(cardIcon, cardLabel))
            ToggleCardPreview();
        UiSharedService.AttachToolTip(Loc.Get("EditProfile.Banner.Preview.Tooltip"));
    }

    private (FontAwesomeIcon Icon, string Label) ProfilePreviewButtonContent()
        => _standalonePreviewShown
            ? (FontAwesomeIcon.EyeSlash, Loc.Get("EditProfile.PreviewButton.Close"))
            : (FontAwesomeIcon.Eye, Loc.Get("EditProfile.PreviewButton"));

    private void DrawProfilePreviewButton(FontAwesomeIcon icon, string label)
    {
        if (_uiSharedService.IconTextButton(icon, label))
            ToggleStandalonePreview();
        UiSharedService.AttachToolTip(Loc.Get("EditProfile.PreviewButton.Tooltip"));
    }

    private void ToggleStandalonePreview()
    {
        if (_standalonePreviewShown)
        {
            _standalonePreviewShown = false;
            Mediator.Publish(new ProfileCloseStandaloneMessage(_apiController.UID));
        }
        else
        {
            PushStandalonePreview();
        }
    }
    
    private static void SameLineRightAligned(float width)
    {
        ImGui.SameLine();
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail < width)
        {
            ImGui.NewLine();
            avail = ImGui.GetContentRegionAvail().X;
        }
        if (avail > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - width);
    }

    private void ToggleCardPreview()
    {
        _cardPreviewOpen = !_cardPreviewOpen;
        if (_cardPreviewOpen)
            PushPreview();
        else
            Mediator.Publish(new TargetProfilePreviewMessage(null));
    }

    private void DrawCardColors(float labelWidth)
    {
        var scale = ImGuiHelpers.GlobalScale;

        DrawAppearanceLabel(Loc.Get("EditProfile.ProfileAccent"), labelWidth);
        ImGui.SetNextItemWidth(24f * scale);
        if (ImGui.ColorEdit3("##rpAccentColor", ref _rpAccentColorVec, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
            UpdateCardStyle(_cardStyle with { AccentColor = UiSharedService.Vector4ToHex(new Vector4(_rpAccentColorVec, 1f)) });
        UiSharedService.AttachToolTip(Loc.Get("EditProfile.ProfileAccent.Tooltip"));
        ImGui.SameLine();
        _uiSharedService.DrawHelpText(Loc.Get("EditProfile.ProfileAccent.Help"));
        DrawPreviewButtons();

        DrawAppearanceLabel(Loc.Get("EditProfile.NameColor"), labelWidth);
        ImGui.SetNextItemWidth(24f * scale);
        if (ImGui.ColorEdit3("##rpNameColor", ref _rpNameColorVec, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
            _previewRefreshPending = true;
        UiSharedService.AttachToolTip(Loc.Get("EditProfile.NameColor.Tooltip"));

        DrawAppearanceLabel(Loc.Get("EditProfile.CardTextColor"), labelWidth);
        ImGui.SetNextItemWidth(24f * scale);
        if (ImGui.ColorEdit3("##rpCardTextColor", ref _rpCardTextColorVec, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
            UpdateCardStyle(_cardStyle with { TextColor = UiSharedService.Vector4ToHex(new Vector4(_rpCardTextColorVec, 1f)) });
        UiSharedService.AttachToolTip(Loc.Get("EditProfile.CardTextColor.Tooltip"));
        ImGui.SameLine();
        _uiSharedService.DrawHelpText(Loc.Get("EditProfile.CardTextColor.Help"));

        var resetLabel = Loc.Get("EditProfile.CardStyle.Reset");
        SameLineRightAligned(_uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Undo, resetLabel));
        using (ImRaii.Disabled(_cardStyle.IsDefault))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Undo, resetLabel))
                UpdateCardStyle(ProfileCardStyle.Default);
        }
    }

    private void DrawSheetBackgroundSettings(float labelWidth, float sliderWidth)
    {
        DrawAppearanceLabel(Loc.Get("EditProfile.Background.Image"), labelWidth);
        DrawSheetBackgroundCombo(sliderWidth);
        ImGui.SameLine();
        _uiSharedService.DrawHelpText(Loc.Get("EditProfile.Background.Help"));

        using (ImRaii.Disabled(!_cardStyle.HasBackground))
        {
            DrawAppearanceLabel(Loc.Get("EditProfile.Background.Dimming"), labelWidth);
            int dimming = (int)MathF.Round(_cardStyle.BackgroundDimming * 100f);
            ImGui.SetNextItemWidth(sliderWidth);
            ThemedSlider.AlignWithFramePaddedLabel();
            if (ThemedSlider.Int("##sheetBackgroundDimming", ref dimming, (int)(ProfileCardStyle.MinBackgroundDimming * 100f),
                    (int)MathF.Round(ProfileCardStyle.MaxBackgroundDimming * 100f), "%d %"))
                UpdateCardStyle(_cardStyle with { BackgroundDimming = dimming / 100f });
            UiSharedService.AttachToolTip(Loc.Get("EditProfile.Background.Dimming.Tooltip"));

            DrawAppearanceLabel(Loc.Get("EditProfile.Background.Fade"), labelWidth);
            bool fade = _cardStyle.BackgroundFade;
            if (ToggleSwitch.Draw("##sheetBackgroundFade", ref fade))
                UpdateCardStyle(_cardStyle with { BackgroundFade = fade });
            UiSharedService.AttachToolTip(Loc.Get("EditProfile.Background.Fade.Tooltip"));

            using (ImRaii.Disabled(!_cardStyle.BackgroundFade))
            {
                DrawAppearanceLabel(Loc.Get("EditProfile.Background.Veil"), labelWidth);
                int veil = (int)MathF.Round(_cardStyle.BackgroundVeil * 100f);
                ImGui.SetNextItemWidth(sliderWidth);
                ThemedSlider.AlignWithFramePaddedLabel();
                if (ThemedSlider.Int("##sheetBackgroundVeil", ref veil, (int)(ProfileCardStyle.MinBackgroundVeil * 100f),
                        (int)MathF.Round(ProfileCardStyle.MaxBackgroundVeil * 100f), "%d %"))
                    UpdateCardStyle(_cardStyle with { BackgroundVeil = veil / 100f });
                UiSharedService.AttachToolTip(Loc.Get("EditProfile.Background.Veil.Tooltip"));
            }
        }
    }

    private void DrawSheetColors(float labelWidth)
    {
        var sheet = ProfileBackgrounds.Find(_cardStyle.Background) is { } background
            ? ProfileSheetBackground.PaletteFor(background.Family)
            : null;
        var accent = UiSharedService.TryHexToVector4(_cardStyle.AccentColor, out var custom) ? custom : UiSharedService.DefaultAccentColor;
        var text = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.Text));

        DrawSheetColor("EditProfile.Background.TitleColor", "##sheetTitleColor", _cardStyle.SheetTitleColor, sheet?.Accent ?? accent,
            hex => _cardStyle with { SheetTitleColor = hex }, labelWidth);
        DrawSheetColor("EditProfile.Background.LabelColor", "##sheetLabelColor", _cardStyle.SheetLabelColor, sheet?.Label ?? ImGuiColors.DalamudGrey,
            hex => _cardStyle with { SheetLabelColor = hex }, labelWidth);
        DrawSheetColor("EditProfile.Background.TextColor", "##sheetTextColor", _cardStyle.SheetTextColor, sheet?.Value ?? text,
            hex => _cardStyle with { SheetTextColor = hex }, labelWidth);
    }

    // Sans couleur choisie, la pastille montre la teinte assortie au fond ; le bouton d'annulation y revient.
    private void DrawSheetColor(string labelKey, string id, string? hex, Vector4 fallback, Func<string?, ProfileCardStyle> apply, float labelWidth)
    {
        DrawAppearanceLabel(Loc.Get(labelKey), labelWidth);
        var color = UiSharedService.TryHexToVector4(hex, out var custom) ? custom : fallback;
        var rgb = new Vector3(color.X, color.Y, color.Z);
        ImGui.SetNextItemWidth(24f * ImGuiHelpers.GlobalScale);
        if (ImGui.ColorEdit3(id, ref rgb, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
            UpdateCardStyle(apply(UiSharedService.Vector4ToHex(new Vector4(rgb, 1f))));
        UiSharedService.AttachToolTip(Loc.Get(labelKey + ".Tooltip"));

        if (hex == null) return;
        ImGui.SameLine();
        using (ImRaii.PushId(id))
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Undo))
                UpdateCardStyle(apply(null));
        }
        UiSharedService.AttachToolTip(Loc.Get("EditProfile.Background.Color.Reset"));
    }

    private void DrawBannerSettings(float labelWidth, float sliderWidth)
    {
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.FileImage, Loc.Get("EditProfile.Banner.Select")))
            SelectBanner();

        if (_rpBannerImage.Length > 0)
        {
            ImGui.SameLine();
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("EditProfile.Banner.Remove")))
            {
                _rpBannerImage = [];
                _rpBannerDirty = true;
                _bannerTextureStale = true;
                _previewRefreshPending = true;
            }
        }

        ImGui.SameLine();
        _uiSharedService.DrawHelpText(Loc.Get("EditProfile.Banner.Recommended"));

        if (_showBannerError)
            UiSharedService.ColorTextWrapped(Loc.Get("EditProfile.Banner.SizeError"), ImGuiColors.DalamudRed);

        ImGuiHelpers.ScaledDummy(2f);

        bool hasBanner = _rpBannerImage.Length > 0;
        using (ImRaii.Disabled(!hasBanner))
        {
            DrawAppearanceLabel(Loc.Get("EditProfile.Banner.Dimming"), labelWidth);
            int dimming = (int)MathF.Round((1f - _cardStyle.BannerOpacity) * 100f);
            int maxDimming = (int)MathF.Round((1f - ProfileCardStyle.MinBannerOpacity) * 100f);
            ImGui.SetNextItemWidth(sliderWidth);
            ThemedSlider.AlignWithFramePaddedLabel();
            if (ThemedSlider.Int("##bannerDimming", ref dimming, 0, maxDimming, "%d %"))
                UpdateCardStyle(_cardStyle with { BannerOpacity = 1f - (dimming / 100f) });
            UiSharedService.AttachToolTip(Loc.Get("EditProfile.Banner.Dimming.Tooltip"));

            DrawAppearanceLabel(Loc.Get("EditProfile.Banner.Fade"), labelWidth);
            bool fade = _cardStyle.FadeEdges;
            if (ToggleSwitch.Draw("##bannerFade", ref fade))
                UpdateCardStyle(_cardStyle with { FadeEdges = fade });
            UiSharedService.AttachToolTip(Loc.Get("EditProfile.Banner.Fade.Tooltip"));

            using (ImRaii.Disabled(!_cardStyle.FadeEdges))
            {
                DrawAppearanceLabel(Loc.Get("EditProfile.Banner.FadeRadius"), labelWidth);
                int radius = (int)MathF.Round(_cardStyle.FadeRadius * 100f);
                ImGui.SetNextItemWidth(sliderWidth);
                ThemedSlider.AlignWithFramePaddedLabel();
                if (ThemedSlider.Int("##bannerFadeRadius", ref radius, (int)(ProfileCardStyle.MinFadeRadius * 100f),
                        (int)MathF.Round(ProfileCardStyle.MaxFadeRadius * 100f), "%d %"))
                    UpdateCardStyle(_cardStyle with { FadeRadius = radius / 100f });
                UiSharedService.AttachToolTip(Loc.Get("EditProfile.Banner.FadeRadius.Tooltip"));
            }
        }
        if (!hasBanner)
            UiSharedService.ColorTextWrapped(Loc.Get("EditProfile.Banner.RenderingNeedsBanner"), ImGuiColors.DalamudGrey3);
    }

    private void DrawSheetBackgroundCombo(float width)
    {
        var none = Loc.Get("EditProfile.Background.None");
        var current = ProfileBackgrounds.Find(_cardStyle.Background);
        ImGui.SetNextItemWidth(width);
        if (!UiSharedService.BeginCombo("##sheetBackground", current == null ? none : SheetBackgroundName(current)))
            return;

        if (ImGui.Selectable(none, current == null))
            UpdateCardStyle(_cardStyle with { Background = 0 });

        ProfileBackgroundFamily? family = null;
        foreach (var background in ProfileBackgrounds.All)
        {
            if (background.Family != family)
            {
                family = background.Family;
                ImGui.Separator();
                ImGui.TextColored(ImGuiColors.DalamudGrey3, Loc.Get($"EditProfile.Background.Family.{family}"));
            }

            bool selected = background.IconId == _cardStyle.Background;
            if (ImGui.Selectable($"{SheetBackgroundName(background)}##sheetBg{background.IconId}", selected))
                UpdateCardStyle(_cardStyle with { Background = background.IconId });
            if (selected)
                ImGui.SetItemDefaultFocus();
            if (ImGui.IsItemHovered())
                DrawSheetBackgroundThumbnail(background.IconId);
        }

        UiSharedService.EndCombo();
    }

    private void DrawSheetBackgroundThumbnail(int iconId)
    {
        if (!ProfileSheetBackground.TryGetTexture(_uiSharedService.TextureProvider, iconId, out var texture)) return;
        var width = 240f * ImGuiHelpers.GlobalScale;
        ImGui.BeginTooltip();
        ImGui.Image(texture.Handle, new Vector2(width, width * texture.Height / texture.Width));
        ImGui.EndTooltip();
    }

    private static string SheetBackgroundName(ProfileBackground background)
        => Loc.Get($"EditProfile.Background.{background.Key}");

    private static void DrawAppearanceLabel(string label, float labelWidth)
    {
        float startX = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        ImGui.SameLine(startX + labelWidth);
    }

    private void UpdateCardStyle(ProfileCardStyle style)
    {
        SetCardStyle(style.Normalized());
        _previewRefreshPending = true;
    }

    private void SetCardStyle(ProfileCardStyle style)
    {
        _cardStyle = style;
        var color = UiSharedService.ProfileCardTextColor(style.TextColor);
        _rpCardTextColorVec = new Vector3(color.X, color.Y, color.Z);
        var accent = UiSharedService.TryHexToVector4(style.AccentColor, out var custom) ? custom : UiSharedService.DefaultAccentColor;
        _rpAccentColorVec = new Vector3(accent.X, accent.Y, accent.Z);
    }

    private void SelectBanner()
    {
        _showBannerError = false;
        _fileDialogManager.OpenFileDialog(Loc.Get("EditProfile.Banner.Select"), "Image files{.png,.jpg,.jpeg}", (success, name) =>
        {
            if (!success) return;
            _cropEditor.Open(name, ProfileImageKind.Banner, banner =>
            {
                _rpBannerImage = banner;
                _rpBannerDirty = true;
                _bannerTextureStale = true;
                _previewRefreshPending = true;
            }, () => _showBannerError = true);
        });
    }

    private void DrawCustomFieldsSection(float availableWidth)
    {
        var customFields = _rpCustomFields;

        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("UserProfile.RpCustomFields"));
        ImGui.SameLine();

        if (customFields.Count < 10)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiSharedService.ThemeButtonHovered);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiSharedService.ThemeButtonActive);
            if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
            {
                customFields.Add(new RpCustomField
                {
                    Name = string.Empty,
                    Value = string.Empty,
                    Order = customFields.Count
                });
            }
            UiSharedService.AttachToolTip(Loc.Get("UserProfile.RpCustomFieldAdd"));
            ImGui.PopStyleColor(3);
        }

        int? removeIndex = null;
        int? moveUpIndex = null;
        int? moveDownIndex = null;

        for (int i = 0; i < customFields.Count; i++)
        {
            var field = customFields[i];
            ImGui.PushID($"custom_field_{i}");

            var labelWidth = 120f * ImGuiHelpers.GlobalScale;
            var buttonAreaWidth = 90f * ImGuiHelpers.GlobalScale;
            var valueWidth = availableWidth - labelWidth - buttonAreaWidth - ImGui.GetStyle().ItemSpacing.X * 3;

            using (_uiSharedService.GameFont.Push())
            {
                ImGui.SetNextItemWidth(labelWidth);
                var name = field.Name;
                if (ImGui.InputTextWithHint("##name", Loc.Get("UserProfile.RpCustomFieldName"), ref name, 30))
                {
                    field.Name = name;
                }
            }
            ImGui.SameLine();
            using (_uiSharedService.GameFont.Push())
            {
                ImGui.SetNextItemWidth(valueWidth);
                var value = field.Value;
                if (ImGui.InputTextWithHint("##value", Loc.Get("UserProfile.RpCustomFieldValue"), ref value, 200))
                {
                    field.Value = value;
                }
            }
            ImGui.SameLine();

            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiSharedService.ThemeButtonHovered);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiSharedService.ThemeButtonActive);

            ImGui.PushID("up");
            if (i > 0 && _uiSharedService.IconButton(FontAwesomeIcon.ArrowUp))
                moveUpIndex = i;
            else if (i == 0)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.3f);
                _uiSharedService.IconButton(FontAwesomeIcon.ArrowUp);
                ImGui.PopStyleVar();
            }
            ImGui.PopID();
            ImGui.SameLine();

            ImGui.PushID("down");
            if (i < customFields.Count - 1 && _uiSharedService.IconButton(FontAwesomeIcon.ArrowDown))
                moveDownIndex = i;
            else if (i == customFields.Count - 1)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.3f);
                _uiSharedService.IconButton(FontAwesomeIcon.ArrowDown);
                ImGui.PopStyleVar();
            }
            ImGui.PopID();
            ImGui.SameLine();

            ImGui.PushID("del");
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
            if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                removeIndex = i;
            ImGui.PopStyleColor();
            ImGui.PopID();

            ImGui.PopStyleColor(3);
            ImGui.PopID();
        }

        if (moveUpIndex.HasValue)
        {
            var idx = moveUpIndex.Value;
            (customFields[idx], customFields[idx - 1]) = (customFields[idx - 1], customFields[idx]);
            for (int i = 0; i < customFields.Count; i++) customFields[i].Order = i;
        }
        if (moveDownIndex.HasValue)
        {
            var idx = moveDownIndex.Value;
            (customFields[idx], customFields[idx + 1]) = (customFields[idx + 1], customFields[idx]);
            for (int i = 0; i < customFields.Count; i++) customFields[i].Order = i;
        }
        if (removeIndex.HasValue)
        {
            customFields.RemoveAt(removeIndex.Value);
            for (int i = 0; i < customFields.Count; i++) customFields[i].Order = i;
        }

        if (customFields.Count == 0) return;

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("EditProfile.CustomFields.Justify"));
        ImGui.SameLine();
        bool justify = _cardStyle.JustifyCustomFields;
        if (ToggleSwitch.Draw("##customFieldsJustify", ref justify))
            UpdateCardStyle(_cardStyle with { JustifyCustomFields = justify });
        UiSharedService.AttachToolTip(Loc.Get("EditProfile.CustomFields.Justify.Tooltip"));
    }

    private List<StatusIconInfo> LoadStatusIcons()
    {
        var sheet = _dataManager.GetExcelSheet<Status>(Dalamud.Game.ClientLanguage.English);
        if (sheet == null) return [];

        var seen = new HashSet<uint>();
        var list = new List<StatusIconInfo>();
        foreach (var row in sheet)
        {
            if (row.Icon == 0) continue;
            var name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!seen.Add(row.Icon)) continue;
            list.Add(new StatusIconInfo(row.Icon, name));
        }
        list.Sort((a, b) => a.IconId.CompareTo(b.IconId));
        return list;
    }

    private void DrawNsfwToggle(bool isRp)
    {
        if (isRp)
        {
            if (ToggleSwitch.Draw(Loc.Get("UserProfile.RpNsfw"), ref _rpNsfw))
                _previewRefreshPending = true;
            _uiSharedService.DrawHelpText(Loc.Get("EditProfile.RpNsfw.Help"));
        }
        else
        {
            if (ToggleSwitch.Draw(Loc.Get("EditProfile.ProfileIsNsfw"), ref _hrpNsfw))
                _previewRefreshPending = true;
            _uiSharedService.DrawHelpText(Loc.Get("EditProfile.ProfileIsNsfwHelp"));
        }
    }
    private void DrawSaveCard(bool isRp)
    {
        var scale = ImGuiHelpers.GlobalScale;
        bool justSaved = (DateTime.UtcNow - _saveConfirmTime).TotalSeconds < 4;
        bool unsaved = !justSaved && HasUnsavedChanges(isRp);

        FontAwesomeIcon statusIcon = FontAwesomeIcon.CheckCircle;
        Vector4 statusColor = ImGuiColors.HealerGreen;
        string title = Loc.Get("EditProfile.SaveCard.UpToDate");
        if (unsaved)
        {
            statusIcon = FontAwesomeIcon.ExclamationCircle;
            statusColor = ImGuiColors.DalamudYellow;
            title = Loc.Get("EditProfile.UnsavedChanges");
        }
        else if (justSaved)
        {
            title = Loc.Get("EditProfile.SaveConfirm");
        }

        var accent = UiSharedService.AccentColor;
        var padding = new Vector2(12f, 8f) * scale;
        float height = (padding.Y * 2f) + ImGui.GetFrameHeight() + scale;

        using (ImRaii.PushColor(ImGuiCol.ChildBg, accent with { W = 0.07f }).Push(ImGuiCol.Border, accent with { W = 0.45f }))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, UiSharedService.RadiusCard * scale)
                   .Push(ImGuiStyleVar.ChildBorderSize, scale)
                   .Push(ImGuiStyleVar.WindowPadding, padding))
        using (var card = ImRaii.Child("##profileSaveCard", new Vector2(-1f, height), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (card)
            {
                ImGui.AlignTextToFramePadding();
                _uiSharedService.IconText(statusIcon, statusColor);
                ImGui.SameLine();
                ImGui.TextColored(statusColor, title);
                if (unsaved)
                    UiSharedService.AttachToolTip(Loc.Get("EditProfile.SaveCard.Unsaved.Desc"));

                string label = Loc.Get("EditProfile.SaveButton");
                float buttonWidth = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Save, label);
                ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - buttonWidth));
                DrawSaveButton(isRp, label, emphasized: unsaved);
            }
        }

        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false)
            && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            SaveProfile(isRp);
    }
    
    private void DrawSaveButton(bool isRp, string label, bool emphasized)
    {
        if (!emphasized)
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, label))
                SaveProfile(isRp);
            UiSharedService.AttachToolTip(Loc.Get("EditProfile.SaveButton.Tooltip"));
            return;
        }

        var accent = UiSharedService.AccentColor;
        float luminance = (0.2126f * accent.X) + (0.7152f * accent.Y) + (0.0722f * accent.Z);
        var textColor = luminance > 0.6f ? UiSharedService.ThemeWindowBg : UiSharedService.ThemeNavTextActive;
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Vector4.Lerp(accent, UiSharedService.ThemeNavTextActive, 0.15f))
                   .Push(ImGuiCol.ButtonActive, Vector4.Lerp(accent, UiSharedService.ThemeWindowBg, 0.2f))
                   .Push(ImGuiCol.Text, textColor))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, label, isInPopup: true, buttonColor: accent))
                SaveProfile(isRp);
        }
        UiSharedService.AttachToolTip(Loc.Get("EditProfile.SaveButton.Tooltip"));
    }

    private void SaveProfile(bool isRp)
    {
        if (isRp)
        {
            var vanillaFirst = GetVanillaFirstName();
            if (vanillaFirst != null && !string.IsNullOrEmpty(_rpFirstNameText)
                && !RpFirstNameContainsVanilla(_rpFirstNameText, vanillaFirst))
            {
                Mediator.Publish(new NotificationMessage(Loc.Get("EditProfile.SaveErrorTitle"),
                    string.Format(Loc.Get("EditProfile.RpFirstNameMismatch"), vanillaFirst), NotificationType.Error));
                return;
            }

            var profile = _rpConfigService.GetCurrentCharacterProfile();
            profile.RpDescription = _rpDescriptionText;
            profile.IsRpNsfw = _rpNsfw;
            if (_rpPortraitDirty)
                profile.RpProfilePictureBase64 = Convert.ToBase64String(_rpProfileImage);
            profile.RpFirstName = _rpFirstNameText;
            profile.RpLastName = _rpLastNameText;
            profile.RpTitle = _rpTitleText;
            profile.RpAge = _rpAgeText;
            profile.RpRace = _rpRaceText;
            profile.RpEthnicity = _rpEthnicityText;
            profile.RpHeight = _rpHeightText;
            profile.RpBuild = _rpBuildText;
            profile.RpResidence = _rpResidenceText;
            profile.RpOccupation = _rpOccupationText;
            profile.RpAffiliation = _rpAffiliationText;
            profile.RpAlignment = _rpAlignmentText;
            profile.RpAdditionalInfo = _rpAdditionalInfoText;
            profile.RpNameColor = UiSharedService.Vector4ToHex(new Vector4(_rpNameColorVec, 1f));
            profile.RpCardStyle = _cardStyle.Serialize();
            profile.RpCustomFields = _rpCustomFields
                .Select(f => new RpCustomField { Name = f.Name, Value = f.Value, Order = f.Order })
                .ToList();
            profile.ChatIcon = _chatIconPicker.SelectedIcon;
            profile.RpLevel = _rpLevel;
            profile.RpVisibility = _rpVisibility;
            _rpConfigService.Save();
        }

        var charName = _dalamudUtil.GetPlayerName();
        var worldId = _dalamudUtil.GetHomeWorldId();
        _moodlesEditor.PersistBackupForCharacter(charName, worldId);
        var localRpProfile = _rpConfigService.GetCurrentCharacterProfile();
        var customFieldsJsonSnapshot = System.Text.Json.JsonSerializer.Serialize(_rpCustomFields);
        var moodlesDataSnapshot = _moodlesEditor.LocalMoodlesJson;
        var bannerSnapshot = isRp && _rpBannerDirty ? Convert.ToBase64String(_rpBannerImage) : null;
        var hrpPortraitSnapshot = !isRp && _hrpPortraitDirty ? Convert.ToBase64String(_profileImage) : null;
        var rpPortraitSaved = isRp && _rpPortraitDirty;
        var hrpNsfw = _hrpNsfw;

        _ = Task.Run(async () =>
        {
            try
            {
                var curProfile = await _apiController.UserGetProfile(new UserDto(new UserData(_apiController.UID, _apiController.DisplayName))).ConfigureAwait(false);
                if (isRp)
                    _logger.LogInformation("Saving RP profile for {uid}", _apiController.UID);
                else
                    _logger.LogInformation("Saving HRP profile for {uid}, keeping local RP data", _apiController.UID);

                await _apiController.UserSetProfile(new UserProfileDto(
                    new UserData(_apiController.UID, _apiController.DisplayName),
                    curProfile.Disabled,
                    isRp ? curProfile.IsNSFW : hrpNsfw,
                    hrpPortraitSnapshot ?? curProfile.ProfilePictureBase64,
                    isRp ? curProfile.Description : _descriptionText)
                {
                    CharacterName = charName,
                    WorldId = worldId,
                    RpProfilePictureBase64 = localRpProfile.RpProfilePictureBase64,
                    RpDescription = localRpProfile.RpDescription,
                    IsRpNSFW = localRpProfile.IsRpNsfw,
                    RpFirstName = localRpProfile.RpFirstName,
                    RpLastName = localRpProfile.RpLastName,
                    RpTitle = localRpProfile.RpTitle,
                    RpAge = localRpProfile.RpAge,
                    RpRace = localRpProfile.RpRace,
                    RpEthnicity = localRpProfile.RpEthnicity,
                    RpHeight = localRpProfile.RpHeight,
                    RpBuild = localRpProfile.RpBuild,
                    RpResidence = localRpProfile.RpResidence,
                    RpOccupation = localRpProfile.RpOccupation,
                    RpAffiliation = localRpProfile.RpAffiliation,
                    RpAlignment = localRpProfile.RpAlignment,
                    RpAdditionalInfo = localRpProfile.RpAdditionalInfo,
                    RpNameColor = localRpProfile.RpNameColor,
                    RpCardStyle = localRpProfile.RpCardStyle,
                    RpCustomFields = customFieldsJsonSnapshot,
                    MoodlesData = moodlesDataSnapshot,
                    ChatIcon = isRp ? localRpProfile.ChatIcon : null,
                    RpLevel = isRp ? localRpProfile.RpLevel : null,
                    RpVisibility = isRp ? localRpProfile.RpVisibility : null,
                    RpBannerBase64 = bannerSnapshot
                }).ConfigureAwait(false);

                if (bannerSnapshot != null) _rpBannerDirty = false;
                if (rpPortraitSaved) _rpPortraitDirty = false;
                if (hrpPortraitSnapshot != null) _hrpPortraitDirty = false;
                Mediator.Publish(new ClearProfileDataMessage(new UserData(_apiController.UID), charName, worldId));
                Mediator.Publish(new NotificationMessage(Loc.Get("EditProfile.SaveSuccessTitle"), Loc.Get("EditProfile.SaveSuccessBody"), NotificationType.Success));
                SnapshotSavedState(isRp, customFieldsJsonSnapshot);

                // Apply Honorific title if changed
                if (isRp && _ipcManager.Honorific.APIAvailable)
                    _ = _honorificEditor.ApplyAsync();
                _saveConfirmTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save profile");
                Mediator.Publish(new NotificationMessage(Loc.Get("EditProfile.SaveErrorTitle"), Loc.Get("EditProfile.SaveErrorBody"), NotificationType.Error));
                _notificationTracker.Upsert(NotificationEntry.ProfileSaveFailed());
            }
        });
    }

    private void SnapshotSavedState(bool isRp, string? customFieldsJson = null)
    {
        if (isRp)
        {
            _savedRpFirstNameText = _rpFirstNameText;
            _savedRpLastNameText = _rpLastNameText;
            _savedRpTitleText = _rpTitleText;
            _savedRpAgeText = _rpAgeText;
            _savedRpRaceText = _rpRaceText;
            _savedRpEthnicityText = _rpEthnicityText;
            _savedRpHeightText = _rpHeightText;
            _savedRpBuildText = _rpBuildText;
            _savedRpResidenceText = _rpResidenceText;
            _savedRpOccupationText = _rpOccupationText;
            _savedRpAffiliationText = _rpAffiliationText;
            _savedRpAlignmentText = _rpAlignmentText;
            _savedRpAdditionalInfoText = _rpAdditionalInfoText;
            _savedRpNameColorHex = UiSharedService.Vector4ToHex(new Vector4(_rpNameColorVec, 1f));
            _savedRpNsfw = _rpNsfw;
            _savedCardStyle = _cardStyle;
            if (customFieldsJson != null)
            {
                _savedRpCustomFieldsJson = customFieldsJson;
            }
            else
            {
                _savedRpCustomFieldsJson = System.Text.Json.JsonSerializer.Serialize(_rpCustomFields);
            }
            _chatIconPicker.SnapshotSaved();
            _savedRpLevel = _rpLevel;
            _savedRpVisibility = _rpVisibility;
            _honorificEditor.SnapshotSaved();
        }
        else
        {
            _savedDescriptionText = _descriptionText;
            _savedHrpNsfw = _hrpNsfw;
        }
    }

    private bool HasUnsavedChanges(bool isRp)
    {
        if (isRp)
        {
            return !string.Equals(_rpFirstNameText, _savedRpFirstNameText, StringComparison.Ordinal)
                || !string.Equals(_rpLastNameText, _savedRpLastNameText, StringComparison.Ordinal)
                || !string.Equals(_rpTitleText, _savedRpTitleText, StringComparison.Ordinal)
                || !string.Equals(_rpAgeText, _savedRpAgeText, StringComparison.Ordinal)
                || !string.Equals(_rpRaceText, _savedRpRaceText, StringComparison.Ordinal)
                || !string.Equals(_rpEthnicityText, _savedRpEthnicityText, StringComparison.Ordinal)
                || !string.Equals(_rpHeightText, _savedRpHeightText, StringComparison.Ordinal)
                || !string.Equals(_rpBuildText, _savedRpBuildText, StringComparison.Ordinal)
                || !string.Equals(_rpResidenceText, _savedRpResidenceText, StringComparison.Ordinal)
                || !string.Equals(_rpOccupationText, _savedRpOccupationText, StringComparison.Ordinal)
                || !string.Equals(_rpAffiliationText, _savedRpAffiliationText, StringComparison.Ordinal)
                || !string.Equals(_rpAlignmentText, _savedRpAlignmentText, StringComparison.Ordinal)
                || !string.Equals(_rpAdditionalInfoText, _savedRpAdditionalInfoText, StringComparison.Ordinal)
                || !string.Equals(UiSharedService.Vector4ToHex(new Vector4(_rpNameColorVec, 1f)), _savedRpNameColorHex, StringComparison.Ordinal)
                || !_cardStyle.Equals(_savedCardStyle)
                || !string.Equals(
                    System.Text.Json.JsonSerializer.Serialize(_rpCustomFields),
                    _savedRpCustomFieldsJson, StringComparison.Ordinal)
                || _honorificEditor.HasUnsavedChanges
                || _chatIconPicker.HasUnsavedChanges
                || _rpLevel != _savedRpLevel
                || _rpVisibility != _savedRpVisibility
                || _rpBannerDirty
                || _rpNsfw != _savedRpNsfw
                || _rpPortraitDirty;
        }
        else
        {
            return !string.Equals(_descriptionText, _savedDescriptionText, StringComparison.Ordinal)
                || _hrpNsfw != _savedHrpNsfw
                || _hrpPortraitDirty;
        }
    }

    private string? GetVanillaFirstName()
    {
        var fullName = _dalamudUtil.GetPlayerName();
        if (string.IsNullOrEmpty(fullName)) return null;
        var spaceIndex = fullName.IndexOf(' ');
        return spaceIndex >= 0 ? fullName[..spaceIndex] : fullName;
    }

    private static bool RpFirstNameContainsVanilla(string rpFirstName, string vanillaFirstName)
    {
        foreach (var part in rpFirstName.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part, vanillaFirstName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void DrawRpLevelSelector()
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("UserProfile.RpLevel"));

        var unset = (byte)0;
        var beginner = (byte)1;
        var regular = (byte)2;
        var mentor = (byte)3;

        if (ImGui.RadioButton(Loc.Get("UserProfile.RpLevel.Unset"), _rpLevel == unset))
            _rpLevel = unset;
        UiSharedService.AttachToolTip(Loc.Get("UserProfile.RpLevel.Unset.Tooltip"));
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.Get("UserProfile.RpLevel.Beginner"), _rpLevel == beginner))
            _rpLevel = beginner;
        UiSharedService.AttachToolTip(Loc.Get("UserProfile.RpLevel.Beginner.Tooltip"));
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.Get("UserProfile.RpLevel.Regular"), _rpLevel == regular))
            _rpLevel = regular;
        UiSharedService.AttachToolTip(Loc.Get("UserProfile.RpLevel.Regular.Tooltip"));
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.Get("UserProfile.RpLevel.Mentor"), _rpLevel == mentor))
            _rpLevel = mentor;
        UiSharedService.AttachToolTip(Loc.Get("UserProfile.RpLevel.Mentor.Tooltip"));
    }

    private static readonly RpProfileVisibility[] RpVisibilityOrder =
    [
        RpProfileVisibility.Hidden,
        RpProfileVisibility.PairsOnly,
        RpProfileVisibility.PairsAndSyncshell,
        RpProfileVisibility.Public,
    ];

    private void DrawRpVisibilitySelector()
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("UserProfile.RpVisibility"));

        string[] labels =
        [
            Loc.Get("UserProfile.RpVisibility.Hidden"),
            Loc.Get("UserProfile.RpVisibility.PairsOnly"),
            Loc.Get("UserProfile.RpVisibility.PairsAndSyncshell"),
            Loc.Get("UserProfile.RpVisibility.Public"),
        ];

        int index = Array.IndexOf(RpVisibilityOrder, _rpVisibility);
        if (index < 0) index = Array.IndexOf(RpVisibilityOrder, RpProfileVisibility.PairsAndSyncshell);

        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        if (UiSharedService.Combo("##rpVisibility", ref index, labels, labels.Length))
            _rpVisibility = RpVisibilityOrder[index];

        _uiSharedService.DrawHelpText(Loc.Get("UserProfile.RpVisibility.Help"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _pfpTextureWrap?.Dispose();
        _rpBannerTexture?.Dispose();
        _rpBannerTexture = null;
        _cropEditor.Dispose();
    }
}