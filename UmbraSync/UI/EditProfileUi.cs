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
using OtterGui.Raii;
using System.Numerics;
using System.Text.RegularExpressions;
using UmbraSync.API.Data;
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
    private bool _wasOpen;
    private bool _rpLoaded = false;
    private bool _hrpLoaded = false;
    private bool _vanityModalOpen = false;
    private string _vanityInput = string.Empty;
    private int _activeTab;
    private readonly ProfileIconPicker _profileIconPicker;
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
    private string _savedRpCustomFieldsJson = string.Empty;
    private readonly BbCodeToolbar _bbCodeToolbar;
    private readonly Lazy<List<StatusIconInfo>> _statusIcons;

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
        _statusIcons = new Lazy<List<StatusIconInfo>>(() => LoadStatusIcons());
        _moodlesEditor = new MoodlesEditor(logger, ipcManager, dalamudUtil, rpConfigService, uiSharedService, dataManager, _statusIcons);
        _profileIconPicker = new ProfileIconPicker(uiSharedService, _statusIcons);

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
                _rpLoaded = false;
                _hrpLoaded = false;
            }
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

    protected override void DrawInternal()
    {
        var accent = UiSharedService.AccentColor;
        if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Eye, Loc.Get("EditProfile.PreviewButton")))
        {
            var myUserData = new UserData(_apiController.UID, _apiController.DisplayName);
            var pair = _pairManager.GetPairByUID(_apiController.UID) ?? _pairFactory.Create(myUserData);

            var currentProfile = _rpConfigService.GetCurrentCharacterProfile();
            var hrpImageBase64 = _profileImage.Length > 0 ? Convert.ToBase64String(_profileImage) : string.Empty;
            var previewProfileData = new UmbraProfileData(
                IsFlagged: false,
                IsNSFW: _apiController.IsProfileNsfw,
                Base64ProfilePicture: hrpImageBase64,
                Description: _descriptionText,
                Base64RpProfilePicture: currentProfile.RpProfilePictureBase64,
                RpDescription: _rpDescriptionText,
                IsRpNSFW: currentProfile.IsRpNsfw,
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
                ProfileIconId: _profileIconPicker.IconId
            );

            _umbraProfileManager.SetPreviewProfile(pair.UserData, pair.PlayerName, pair.WorldId, previewProfileData);
            Mediator.Publish(new ProfileOpenStandaloneMessage(pair));
        }
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Tag, Loc.Get("EditProfile.SetCustomId.Button")))
        {
            _vanityInput = string.Empty;
            _vanityModalOpen = true;
            ImGui.OpenPopup(Loc.Get("EditProfile.SetCustomId.Title"));
        }
        UiSharedService.AttachToolTip(Loc.Get("EditProfile.SetCustomId.Tooltip"));

        DrawVanityPopup();

        DrawProfileTabButtons(accent);
    }

    public void DrawInline() => DrawInternal();

    private void DrawProfileTabButtons(Vector4 accent)
    {
        var labels = new[] { "RP", "HRP" };
        var icons = new[] { FontAwesomeIcon.Scroll, FontAwesomeIcon.User };
        const float btnH = 32f;
        const float btnSpacing = 8f;
        const float rounding = 4f;
        const float iconTextGap = 6f;

        var dl = ImGui.GetWindowDrawList();
        var availWidth = ImGui.GetContentRegionAvail().X;
        var btnW = (availWidth - btnSpacing * (labels.Length - 1)) / labels.Length;

        var borderColor = new Vector4(0.29f, 0.21f, 0.41f, 0.7f);
        var bgColor = new Vector4(0.11f, 0.11f, 0.11f, 0.9f);
        var hoverBg = new Vector4(0.17f, 0.13f, 0.22f, 1f);

        for (int i = 0; i < labels.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, btnSpacing);

            var p = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton($"##profileTab_{i}", new Vector2(btnW, btnH));
            bool hovered = ImGui.IsItemHovered();
            bool isActive = _activeTab == i;

            var bg = isActive ? accent : hovered ? hoverBg : bgColor;
            dl.AddRectFilled(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(bg), rounding);
            if (!isActive)
                dl.AddRect(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(borderColor with { W = hovered ? 0.9f : 0.5f }), rounding);

            ImGui.PushFont(UiBuilder.IconFont);
            var iconStr = icons[i].ToIconString();
            var iconSz = ImGui.CalcTextSize(iconStr);
            ImGui.PopFont();

            var labelSz = ImGui.CalcTextSize(labels[i]);
            var totalW = iconSz.X + iconTextGap + labelSz.X;
            var startX = p.X + (btnW - totalW) / 2f;

            var textColor = isActive ? new Vector4(1f, 1f, 1f, 1f) : hovered ? new Vector4(0.9f, 0.85f, 1f, 1f) : new Vector4(0.7f, 0.65f, 0.8f, 1f);
            var textColorU32 = ImGui.GetColorU32(textColor);

            ImGui.PushFont(UiBuilder.IconFont);
            dl.AddText(new Vector2(startX, p.Y + (btnH - iconSz.Y) / 2f), textColorU32, iconStr);
            ImGui.PopFont();

            dl.AddText(new Vector2(startX + iconSz.X + iconTextGap, p.Y + (btnH - labelSz.Y) / 2f), textColorU32, labels[i]);

            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked) _activeTab = i;
        }

        ImGuiHelpers.ScaledDummy(4f);

        DrawProfileContent(_activeTab == 0);
    }

    private void DrawProfileContent(bool isRp)
    {
        _moodlesEditor.EnsureRefreshed();

        var umbraProfile = _umbraProfileManager.GetUmbraProfile(new UserData(_apiController.UID));

        if (umbraProfile.IsFlagged)
        {
            _uiSharedService.BigText(isRp ? "Profil RP" : "Profil HRP");
            UiSharedService.ColorTextWrapped(umbraProfile.Description, UiSharedService.AccentColor);
            return;
        }

        _uiSharedService.BigText(isRp ? "Profil RP" : "Profil HRP");
        ImGui.SameLine();
        DrawHeaderButtons(isRp, umbraProfile);
        ImGuiHelpers.ScaledDummy(new Vector2(0f, ImGui.GetStyle().ItemSpacing.Y / 2));

        if (isRp)
        {
            if (!_rpLoaded && !string.Equals(umbraProfile.Description, "Loading Data from server...", StringComparison.Ordinal))
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
                _rpCustomFields = configProfile.RpCustomFields
                    .Select(f => new RpCustomField { Name = f.Name, Value = f.Value, Order = f.Order })
                    .ToList();

                _profileIconPicker.IconId = umbraProfile.ProfileIconId > 0 ? umbraProfile.ProfileIconId : configProfile.ProfileIconId;

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

                _rpLoaded = true;
                SnapshotSavedState(true);
            }
        }
        else
        {
            if (!_hrpLoaded && !string.Equals(umbraProfile.Description, "Loading Data from server...", StringComparison.Ordinal))
            {
                _descriptionText = umbraProfile.Description;

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

                _hrpLoaded = true;
                SnapshotSavedState(false);
            }
        }

        var pfpTexture = isRp ? _rpPfpTextureWrap : _pfpTextureWrap;
        var pfpBytes = isRp ? _rpProfileImage : _profileImage;

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
        {
            _fileDialogManager.OpenFileDialog(Loc.Get("EditProfile.SelectImageButton"), "Image files{.png,.jpg,.jpeg}", (success, name) =>
            {
                if (!success) return;
                var rpProfile = isRp ? _rpConfigService.GetCurrentCharacterProfile() : null;
                var charName = _dalamudUtil.GetPlayerName();
                var worldId = _dalamudUtil.GetHomeWorldId();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var file = await File.ReadAllBytesAsync(name).ConfigureAwait(false);
                        if (file.Length > 5 * 1024 * 1024)
                        {
                            _showFileDialogError = true;
                            return;
                        }

                        var curProfile = await _apiController.UserGetProfile(new UserDto(new UserData(_apiController.UID, _apiController.DisplayName))).ConfigureAwait(false);
                        if (isRp && rpProfile != null)
                        {
                            rpProfile.RpProfilePictureBase64 = Convert.ToBase64String(file);
                            _rpConfigService.Save();
                            _logger.LogInformation("Uploading RP image for {uid}", _apiController.UID);
                            await _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID, _apiController.DisplayName), curProfile.Disabled, curProfile.IsNSFW, curProfile.ProfilePictureBase64, curProfile.Description)
                            {
                                CharacterName = charName,
                                WorldId = worldId,
                                RpProfilePictureBase64 = rpProfile.RpProfilePictureBase64,
                                RpDescription = rpProfile.RpDescription,
                                IsRpNSFW = rpProfile.IsRpNsfw,
                                RpFirstName = rpProfile.RpFirstName,
                                RpLastName = rpProfile.RpLastName,
                                RpTitle = rpProfile.RpTitle,
                                RpAge = rpProfile.RpAge,
                                RpRace = rpProfile.RpRace,
                                RpEthnicity = rpProfile.RpEthnicity,
                                RpHeight = rpProfile.RpHeight,
                                RpBuild = rpProfile.RpBuild,
                                RpResidence = rpProfile.RpResidence,
                                RpOccupation = rpProfile.RpOccupation,
                                RpAffiliation = rpProfile.RpAffiliation,
                                RpAlignment = rpProfile.RpAlignment,
                                RpAdditionalInfo = rpProfile.RpAdditionalInfo,
                                RpNameColor = rpProfile.RpNameColor,
                                RpCustomFields = rpProfile.RpCustomFields.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(rpProfile.RpCustomFields) : null,
                                MoodlesData = curProfile.MoodlesData
                            }).ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogInformation("Uploading HRP image for {uid}", _apiController.UID);
                            await _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID, _apiController.DisplayName), curProfile.Disabled, _apiController.IsProfileNsfw, Convert.ToBase64String(file), _descriptionText)
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
                                RpHeight = curProfile.RpHeight,
                                RpBuild = curProfile.RpBuild,
                                RpOccupation = curProfile.RpOccupation,
                                RpAffiliation = curProfile.RpAffiliation,
                                RpAlignment = curProfile.RpAlignment,
                                RpAdditionalInfo = curProfile.RpAdditionalInfo,
                                RpNameColor = curProfile.RpNameColor,
                                RpCustomFields = curProfile.RpCustomFields,
                                MoodlesData = curProfile.MoodlesData
                            }).ConfigureAwait(false);
                        }
                        Mediator.Publish(new ClearProfileDataMessage(new UserData(_apiController.UID), charName, worldId));
                        Mediator.Publish(new NotificationMessage(Loc.Get("EditProfile.SaveSuccessTitle"), Loc.Get("EditProfile.SaveSuccessBody"), NotificationType.Success));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to upload profile image");
                        Mediator.Publish(new NotificationMessage(Loc.Get("EditProfile.SaveErrorTitle"), Loc.Get("EditProfile.SaveErrorBody"), NotificationType.Error));
                        _notificationTracker.Upsert(NotificationEntry.ProfileSaveFailed());
                    }
                });
            });
        }
        if (_showFileDialogError)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("EditProfile.ImageSizeError"), ImGuiColors.DalamudRed, imgSz);
        }
        ImGui.EndGroup();
        ImGui.SameLine(0, spacing);
        ImGui.BeginGroup();

        var fieldW = w - imgSz - spacing - 12f * ImGuiHelpers.GlobalScale;
        var halfFieldW = (fieldW - ImGui.GetStyle().ItemSpacing.X) / 2;

        void DrawField(string label, ref string text, int maxLength, float inputWidth)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, label);
            ImGui.SetNextItemWidth(inputWidth);
            ImGui.InputText("##" + label, ref text, maxLength);
        }

        void DrawFieldPair(string label1, ref string text1, int max1, string label2, ref string text2, int max2)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, label1);
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

            // Color picker for RP name
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("EditProfile.NameColor"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(24f * ImGuiHelpers.GlobalScale);
            if (ImGui.ColorEdit3("##rpNameColor", ref _rpNameColorVec, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
            { /* value read from ref */ }
            UiSharedService.AttachToolTip(Loc.Get("EditProfile.NameColor.Tooltip"));

            DrawFieldPair(Loc.Get("UserProfile.RpTitle"), ref _rpTitleText, 100, Loc.Get("UserProfile.RpAge"), ref _rpAgeText, 50);
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

        if (isRp)
        {
            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));

            // Honorific title section
            _honorificEditor.Draw();

            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));

            _profileIconPicker.Draw();

            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));

            DrawField(Loc.Get("UserProfile.RpResidence"), ref _rpResidenceText, 100, w);
            DrawField(Loc.Get("UserProfile.RpOccupation"), ref _rpOccupationText, 100, w);
            DrawField(Loc.Get("UserProfile.RpAffiliation"), ref _rpAffiliationText, 100, w);
            DrawField(Loc.Get("UserProfile.RpAlignment"), ref _rpAlignmentText, 100, w);

            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));

            DrawCustomFieldsSection(w);

            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));

            _moodlesEditor.DrawSection();

            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));

            using (_uiSharedService.GameFont.Push())
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("UserProfile.RpAdditionalInfo"));
                ImGui.SameLine();
                _bbCodeToolbar.Draw(ref _rpAdditionalInfoText);
                ImGui.TextColored(ImGuiColors.DalamudGrey3, Loc.Get("UserProfile.RpAdditionalInfoWrapHint"));

                ImGui.InputTextMultiline("##additional_info", ref _rpAdditionalInfoText, 3000,
                    new Vector2(w, 150 * ImGuiHelpers.GlobalScale));
            }
        }

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

    private void DrawVanityPopup()
    {
        if (_vanityModalOpen && ImGui.BeginPopupModal(Loc.Get("EditProfile.SetCustomId.Title"), ref _vanityModalOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(Loc.Get("EditProfile.SetCustomId.FormatHint"));
            ImGuiHelpers.ScaledDummy(new Vector2(0f, 5f));

            ImGui.SetNextItemWidth(400 * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("##customId", Loc.Get("EditProfile.SetCustomId.Placeholder"), ref _vanityInput, 64);

            ImGuiHelpers.ScaledDummy(new Vector2(0f, 5f));
            UiSharedService.ColorTextWrapped(Loc.Get("EditProfile.SetCustomId.ReconnectWarning"), ImGuiColors.DalamudYellow);

            ImGuiHelpers.ScaledDummy(new Vector2(0f, ImGui.GetStyle().ItemSpacing.Y));
            if (ImGui.Button(Loc.Get("EditProfile.SetCustomId.Confirm")))
            {
                _ = SubmitVanityAsync(_vanityInput);
            }
            ImGui.SameLine();
            if (ImGui.Button(Loc.Get("Common.Cancel")))
            {
                _vanityModalOpen = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void DrawHeaderButtons(bool isRp, UmbraProfileData umbraProfile)
    {
        if (isRp)
        {
            var profile = _rpConfigService.GetCurrentCharacterProfile();
            var isRpNsfw = profile.IsRpNsfw;
            if (ImGui.Checkbox(Loc.Get("UserProfile.RpNsfw"), ref isRpNsfw))
            {
                var charName = _dalamudUtil.GetPlayerName();
                var worldId = _dalamudUtil.GetHomeWorldId();
                _ = Task.Run(async () =>
                {
                    var curProfile = await _apiController.UserGetProfile(new UserDto(new UserData(_apiController.UID, _apiController.DisplayName))).ConfigureAwait(false);
                    _logger.LogInformation("Setting RP NSFW flag to {flag} for {uid}", isRpNsfw, _apiController.UID);
                    profile.IsRpNsfw = isRpNsfw;
                    _rpConfigService.Save();
                    await _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID, _apiController.DisplayName), curProfile.Disabled, curProfile.IsNSFW, curProfile.ProfilePictureBase64, curProfile.Description)
                    {
                        CharacterName = charName,
                        WorldId = worldId,
                        RpProfilePictureBase64 = curProfile.RpProfilePictureBase64,
                        RpDescription = curProfile.RpDescription,
                        IsRpNSFW = isRpNsfw,
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
                        RpCustomFields = curProfile.RpCustomFields,
                        MoodlesData = curProfile.MoodlesData
                    }).ConfigureAwait(false);
                    Mediator.Publish(new ClearProfileDataMessage(new UserData(_apiController.UID), charName, worldId));
                });
            }
        }
        else
        {
            var isNsfw = umbraProfile.IsNSFW;
            if (ImGui.Checkbox(Loc.Get("EditProfile.ProfileIsNsfw"), ref isNsfw))
            {
                var charName = _dalamudUtil.GetPlayerName();
                var worldId = _dalamudUtil.GetHomeWorldId();
                _ = Task.Run(async () =>
                {
                    var curProfile = await _apiController.UserGetProfile(new UserDto(new UserData(_apiController.UID, _apiController.DisplayName))).ConfigureAwait(false);
                    _logger.LogInformation("Setting global NSFW flag to {flag} for {uid}", isNsfw, _apiController.UID);
                    await _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID, _apiController.DisplayName), curProfile.Disabled, isNsfw, curProfile.ProfilePictureBase64, curProfile.Description)
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
                        RpCustomFields = curProfile.RpCustomFields,
                        MoodlesData = curProfile.MoodlesData
                    }).ConfigureAwait(false);
                    Mediator.Publish(new ClearProfileDataMessage(new UserData(_apiController.UID), charName, worldId));
                });
            }
            _uiSharedService.DrawHelpText(Loc.Get("EditProfile.ProfileIsNsfwHelp"));
        }

        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("EditProfile.SaveButton")))
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
                profile.RpCustomFields = _rpCustomFields
                    .Select(f => new RpCustomField { Name = f.Name, Value = f.Value, Order = f.Order })
                    .ToList();
                profile.ProfileIconId = _profileIconPicker.IconId;
                _rpConfigService.Save();
            }

            var charName = _dalamudUtil.GetPlayerName();
            var worldId = _dalamudUtil.GetHomeWorldId();
            _moodlesEditor.PersistBackupForCharacter(charName, worldId);
            var localRpProfile = _rpConfigService.GetCurrentCharacterProfile();
            var customFieldsJsonSnapshot = System.Text.Json.JsonSerializer.Serialize(_rpCustomFields);
            var moodlesDataSnapshot = _moodlesEditor.LocalMoodlesJson;

            _ = Task.Run(async () =>
            {
                try
                {
                    var curProfile = await _apiController.UserGetProfile(new UserDto(new UserData(_apiController.UID, _apiController.DisplayName))).ConfigureAwait(false);
                    if (isRp)
                    {
                        _logger.LogInformation("Saving RP profile for {uid}: {first} {last}", _apiController.UID, localRpProfile.RpFirstName, localRpProfile.RpLastName);
                        await _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID, _apiController.DisplayName), curProfile.Disabled, curProfile.IsNSFW, curProfile.ProfilePictureBase64, curProfile.Description)
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
                            RpCustomFields = customFieldsJsonSnapshot,
                            MoodlesData = moodlesDataSnapshot,
                            ProfileIconId = localRpProfile.ProfileIconId
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogInformation("Saving HRP profile for {uid}, keeping local RP data", _apiController.UID);
                        await _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID, _apiController.DisplayName), curProfile.Disabled, _apiController.IsProfileNsfw, curProfile.ProfilePictureBase64, _descriptionText)
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
                            RpCustomFields = customFieldsJsonSnapshot,
                            MoodlesData = moodlesDataSnapshot
                        }).ConfigureAwait(false);
                    }
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

        // Status message next to save button
        ImGui.SameLine();
        if ((DateTime.UtcNow - _saveConfirmTime).TotalSeconds < 4)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, Loc.Get("EditProfile.SaveConfirm"));
        }
        else if (HasUnsavedChanges(isRp))
        {
            ImGui.TextColored(new Vector4(0.85f, 0.75f, 0.20f, 1f), Loc.Get("EditProfile.UnsavedChanges"));
        }
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
            if (customFieldsJson != null)
            {
                _savedRpCustomFieldsJson = customFieldsJson;
            }
            else
            {
                _savedRpCustomFieldsJson = System.Text.Json.JsonSerializer.Serialize(_rpCustomFields);
            }
            _profileIconPicker.SnapshotSaved();
            _honorificEditor.SnapshotSaved();
        }
        else
        {
            _savedDescriptionText = _descriptionText;
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
                || !string.Equals(
                    System.Text.Json.JsonSerializer.Serialize(_rpCustomFields),
                    _savedRpCustomFieldsJson, StringComparison.Ordinal)
                || _honorificEditor.HasUnsavedChanges
                || _profileIconPicker.HasUnsavedChanges;
        }
        else
        {
            return !string.Equals(_descriptionText, _savedDescriptionText, StringComparison.Ordinal);
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

    private async Task SubmitVanityAsync(string input)
    {
        try
        {
            await _apiController.UserSetAlias(string.IsNullOrWhiteSpace(input) ? null : input).ConfigureAwait(false);
            Mediator.Publish(new NotificationMessage(Loc.Get("EditProfile.SetCustomId.SentTitle"), Loc.Get("EditProfile.SetCustomId.SentBody"), NotificationType.Success));
        }
        catch (Exception ex)
        {
            var fullMessage = ex.InnerException?.Message ?? ex.Message;
            var errorBody = fullMessage.Contains("DbUpdateException", StringComparison.OrdinalIgnoreCase)
                ? Loc.Get("EditProfile.SetCustomId.ErrorAlreadyTaken")
                : Loc.Get("EditProfile.SetCustomId.ErrorBody");
            Mediator.Publish(new NotificationMessage(Loc.Get("EditProfile.SetCustomId.ErrorTitle"), errorBody, NotificationType.Error));
        }
        finally
        {
            _vanityModalOpen = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _pfpTextureWrap?.Dispose();
    }
}