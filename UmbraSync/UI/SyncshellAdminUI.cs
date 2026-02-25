using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.Group;
using UmbraSync.API.Dto.Slot;
using UmbraSync.Localization;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.AutoDetect;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;
using NotificationType = UmbraSync.MareConfiguration.Models.NotificationType;

namespace UmbraSync.UI;

public class SyncshellAdminUI : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly bool _isModerator = false;
    private readonly bool _isOwner = false;
    private readonly List<string> _oneTimeInvites = [];
    private readonly PairManager _pairManager;
    private readonly UiSharedService _uiSharedService;
    private readonly SyncshellDiscoveryService _syncshellDiscoveryService;
    private readonly NotificationTracker _notificationTracker;
    private List<BannedGroupUserDto> _bannedUsers = [];
    private int _multiInvites;
    private string _newPassword;
    private bool _pwChangeSuccess;
    private Task<int>? _pruneTestTask;
    private Task<int>? _pruneTask;
    private int _pruneDays = 14;
    private bool _autoDetectStateInitialized;
    private bool _autoDetectStateLoading;
    private bool _autoDetectToggleInFlight;
    private bool _autoDetectVisible;
    private bool _autoDetectPasswordDisabled;
    private string? _autoDetectMessage;
    private int _desiredCapacity;
    private bool _capacityApplyInFlight;
    private string? _capacityMessage;
    private bool _autoDetectDesiredVisibility;
    private int _adDurationHours = 2;
    private AutoDetectMode _adMode = AutoDetectMode.Duration;
    private readonly bool[] _adWeekdays = new bool[7];
    private int _adStartHour = 21;
    private int _adStartMinute = 0;
    private int _adEndHour = 23;
    private int _adEndMinute = 0;
    private string _adTimeZone = "Europe/Paris";
    private List<SlotInfoResponseDto> _slots = [];
    private SlotInfoResponseDto? _selectedSlot = null;
    private string _slotName = string.Empty;
    private string _slotDescription = string.Empty;
    private uint _slotServerId;
    private uint _slotTerritoryId;
    private uint _slotDivisionId;
    private uint _slotWardId;
    private uint _slotPlotId;
    private float _slotX;
    private float _slotY;
    private float _slotZ;
    private float _slotRadius = 10f;
    private bool _slotLoading = false;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileDialogManager _fileDialogManager;
    private readonly UmbraProfileManager _profileManager;
    // Group profile fields
    private bool _profileLoading;
    private bool _profileSaving;
    private bool _profileLoaded;
    private string _profileDescription = string.Empty;
    private List<string> _profileTags = [];
    private string _newTag = string.Empty;
    private bool _profileNsfw;
    private bool _profileDisabled;
    private byte[] _profileImageBytes = [];
    private byte[] _bannerImageBytes = [];
    private IDalamudTextureWrap? _profileTexture;
    private IDalamudTextureWrap? _bannerTexture;
    private string? _profileMessage;
    private bool _showProfilePreview;
    private DateTime _profileSaveConfirmTime = DateTime.MinValue;
    private string _savedProfileDescription = string.Empty;
    private List<string> _savedProfileTags = [];
    private bool _savedProfileNsfw;
    private bool _savedProfileDisabled;
    private byte[] _savedProfileImageBytes = [];
    private byte[] _savedBannerImageBytes = [];

    public SyncshellAdminUI(ILogger<SyncshellAdminUI> logger, MareMediator mediator, ApiController apiController,
        UiSharedService uiSharedService, PairManager pairManager, SyncshellDiscoveryService syncshellDiscoveryService,
        GroupFullInfoDto groupFullInfo, PerformanceCollectorService performanceCollectorService, NotificationTracker notificationTracker,
        DalamudUtilService dalamudUtilService, FileDialogManager fileDialogManager, UmbraProfileManager profileManager)
        : base(logger, mediator, string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.WindowTitle"), groupFullInfo.GroupAliasOrGID), performanceCollectorService)
    {
        GroupFullInfo = groupFullInfo;
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _syncshellDiscoveryService = syncshellDiscoveryService;
        _notificationTracker = notificationTracker;
        _dalamudUtilService = dalamudUtilService;
        _fileDialogManager = fileDialogManager;
        _profileManager = profileManager;
        _isOwner = string.Equals(GroupFullInfo.OwnerUID, _apiController.UID, StringComparison.Ordinal);
        _isModerator = GroupFullInfo.GroupUserInfo.IsModerator();
        _newPassword = string.Empty;
        _multiInvites = 30;
        _pwChangeSuccess = true;
        _autoDetectVisible = groupFullInfo.AutoDetectVisible;
        _autoDetectDesiredVisibility = _autoDetectVisible;
        _autoDetectPasswordDisabled = groupFullInfo.PasswordTemporarilyDisabled;
        _desiredCapacity = groupFullInfo.MaxUserCount;
        _capacityApplyInFlight = false;
        _capacityMessage = null;
        Mediator.Subscribe<SyncshellAutoDetectStateChanged>(this, OnSyncshellAutoDetectStateChanged);
        IsOpen = true;
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new(700, 500),
            MaximumSize = new(700, 2000),
        };

        _ = LoadSlotData();
    }

    private async Task LoadSlotData()
    {
        _slotLoading = true;
        _slots = await _apiController.SlotGetInfoForGroup(new(GroupFullInfo.Group)).ConfigureAwait(false);
        _slotLoading = false;
    }

    public GroupFullInfoDto GroupFullInfo { get; private set; }

    protected override void DrawInternal()
    {
        if (!_isModerator && !_isOwner) return;

        GroupFullInfo = _pairManager.Groups[GroupFullInfo.Group];
        if (!_autoDetectToggleInFlight && !_autoDetectStateLoading)
        {
            _autoDetectVisible = GroupFullInfo.AutoDetectVisible;
            _autoDetectPasswordDisabled = GroupFullInfo.PasswordTemporarilyDisabled;
        }

        using var id = ImRaii.PushId("syncshell_admin_" + GroupFullInfo.GID);

        using (_uiSharedService.UidFont.Push())
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.AdminPanelTitle"), GroupFullInfo.GroupAliasOrGID));

        ImGui.Separator();
        var perm = GroupFullInfo.GroupPermissions;

        using var tabColor = ImRaii.PushColor(ImGuiCol.Tab, UiSharedService.AccentColor);
        using var tabHoverColor = ImRaii.PushColor(ImGuiCol.TabHovered, UiSharedService.AccentHoverColor);
        using var tabActiveColor = ImRaii.PushColor(ImGuiCol.TabActive, UiSharedService.AccentActiveColor);
        using var tabbar = ImRaii.TabBar("syncshell_tab_" + GroupFullInfo.GID);

        if (tabbar)
        {
            var inviteTab = ImRaii.TabItem(Loc.Get("SyncshellAdmin.Tab.Invites"));
            if (inviteTab)
            {
                bool isInvitesDisabled = perm.IsDisableInvites();

                if (_uiSharedService.IconTextButton(isInvitesDisabled ? FontAwesomeIcon.Unlock : FontAwesomeIcon.Lock,
                    isInvitesDisabled ? Loc.Get("SyncshellAdmin.Invites.Unlock") : Loc.Get("SyncshellAdmin.Invites.Lock")))
                {
                    perm.SetDisableInvites(!isInvitesDisabled);
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGuiHelpers.ScaledDummy(2f);

                UiSharedService.TextWrapped(Loc.Get("SyncshellAdmin.Invites.Description"));
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Envelope, Loc.Get("SyncshellAdmin.Invites.Single")))
                {
                    ImGui.SetClipboardText(_apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), 1).Result.FirstOrDefault() ?? string.Empty);
                }
                UiSharedService.AttachToolTip(Loc.Get("SyncshellAdmin.Invites.SingleTooltip"));
                ImGui.InputInt("##amountofinvites", ref _multiInvites);
                ImGui.SameLine();
                using (ImRaii.Disabled(_multiInvites <= 1 || _multiInvites > 100))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Envelope, string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.Invites.Multi"), _multiInvites)))
                    {
                        _oneTimeInvites.AddRange(_apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), _multiInvites).Result);
                    }
                }

                if (_oneTimeInvites.Any())
                {
                    var invites = string.Join(Environment.NewLine, _oneTimeInvites);
                    ImGui.InputTextMultiline(Loc.Get("SyncshellAdmin.Invites.GeneratedLabel"), ref invites, 5000, new(0, 0), ImGuiInputTextFlags.ReadOnly);
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Copy, Loc.Get("SyncshellAdmin.Invites.Copy")))
                    {
                        ImGui.SetClipboardText(invites);
                    }
                }
            }
            inviteTab.Dispose();

            var mgmtTab = ImRaii.TabItem(Loc.Get("SyncshellAdmin.Tab.UserManagement"));
            if (mgmtTab)
            {
                var userNode = ImRaii.TreeNode(Loc.Get("SyncshellAdmin.Users.Tree"));
                if (userNode)
                {
                    if (!_pairManager.GroupPairs.TryGetValue(GroupFullInfo, out var pairs))
                    {
                        UiSharedService.ColorTextWrapped(Loc.Get("SyncshellAdmin.Users.Empty"), ImGuiColors.DalamudYellow);
                    }
                    else
                    {
                        using var table = ImRaii.Table("userList#" + GroupFullInfo.Group.GID, 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY);
                        if (table)
                        {
                            ImGui.TableSetupColumn(Loc.Get("SyncshellAdmin.Users.ColAlias"), ImGuiTableColumnFlags.None, 3);
                            ImGui.TableSetupColumn(Loc.Get("SyncshellAdmin.Users.ColOnline"), ImGuiTableColumnFlags.None, 2);
                            ImGui.TableSetupColumn(Loc.Get("SyncshellAdmin.Users.ColFlags"), ImGuiTableColumnFlags.None, 1);
                            ImGui.TableSetupColumn(Loc.Get("SyncshellAdmin.Users.ColActions"), ImGuiTableColumnFlags.None, 2);
                            ImGui.TableHeadersRow();

                            var groupedPairs = new Dictionary<Pair, GroupUserInfo?>(pairs.Select(p => new KeyValuePair<Pair, GroupUserInfo?>(p,
                                p.GroupPair.TryGetValue(GroupFullInfo, out GroupPairFullInfoDto? value) ? value.GroupPairStatusInfo : null)));

                            foreach (var pair in groupedPairs.OrderBy(p =>
                            {
                                if (p.Value == null) return 10;
                                if (p.Value.Value.IsModerator()) return 0;
                                if (p.Value.Value.IsPinned()) return 1;
                                return 10;
                            }).ThenBy(p => p.Key.GetNote() ?? p.Key.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase))
                            {
                                using var tableId = ImRaii.PushId("userTable_" + pair.Key.UserData.UID);

                                ImGui.TableNextColumn(); // alias/uid/note
                                var note = pair.Key.GetNote();
                                var text = note == null ? pair.Key.UserData.AliasOrUID : note + " (" + pair.Key.UserData.AliasOrUID + ")";
                                ImGui.AlignTextToFramePadding();
                                ImGui.TextUnformatted(text);

                                ImGui.TableNextColumn(); // online/name
                                string onlineText = pair.Key.IsOnline ? Loc.Get("SyncshellAdmin.Users.Online") : Loc.Get("SyncshellAdmin.Users.Offline");
                                string? name = pair.Key.GetNoteOrName();
                                if (!string.IsNullOrEmpty(name))
                                {
                                    onlineText += " (" + name + ")";
                                }
                                var boolcolor = UiSharedService.GetBoolColor(pair.Key.IsOnline);
                                ImGui.AlignTextToFramePadding();
                                UiSharedService.ColorText(onlineText, boolcolor);

                                ImGui.TableNextColumn(); // special flags
                                if (pair.Value != null && (pair.Value.Value.IsModerator() || pair.Value.Value.IsPinned()))
                                {
                                    if (pair.Value.Value.IsModerator())
                                    {
                                        _uiSharedService.IconText(FontAwesomeIcon.UserShield);
                                        UiSharedService.AttachToolTip(Loc.Get("SyncshellAdmin.Users.ModeratorTooltip"));
                                    }
                                    if (pair.Value.Value.IsPinned())
                                    {
                                        _uiSharedService.IconText(FontAwesomeIcon.Thumbtack);
                                        UiSharedService.AttachToolTip(Loc.Get("SyncshellAdmin.Users.PinnedTooltip"));
                                    }
                                }
                                else
                                {
                                    _uiSharedService.IconText(FontAwesomeIcon.None);
                                }

                                ImGui.TableNextColumn(); // actions
                                if (_isOwner)
                                {
                                    if (_uiSharedService.IconButton(FontAwesomeIcon.UserShield))
                                    {
                                        GroupUserInfo userInfo = pair.Value ?? GroupUserInfo.None;

                                        userInfo.SetModerator(!userInfo.IsModerator());

                                        _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(GroupFullInfo.Group, pair.Key.UserData, userInfo));
                                    }
                                    UiSharedService.AttachToolTip(pair.Value != null && pair.Value.Value.IsModerator() ? Loc.Get("SyncshellAdmin.Users.Demod") : Loc.Get("SyncshellAdmin.Users.Mod"));
                                    ImGui.SameLine();
                                }

                                if (_isOwner || (pair.Value == null || (pair.Value != null && !pair.Value.Value.IsModerator())))
                                {
                                    if (_uiSharedService.IconButton(FontAwesomeIcon.Thumbtack))
                                    {
                                        GroupUserInfo userInfo = pair.Value ?? GroupUserInfo.None;

                                        userInfo.SetPinned(!userInfo.IsPinned());

                                        _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(GroupFullInfo.Group, pair.Key.UserData, userInfo));
                                    }
                                    UiSharedService.AttachToolTip(pair.Value != null && pair.Value.Value.IsPinned() ? Loc.Get("SyncshellAdmin.Users.Unpin") : Loc.Get("SyncshellAdmin.Users.Pin"));
                                    ImGui.SameLine();


                                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                                    {
                                        if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                                        {
                                            _ = _apiController.GroupRemoveUser(new GroupPairDto(GroupFullInfo.Group, pair.Key.UserData));
                                        }
                                    }
                                    UiSharedService.AttachToolTip(Loc.Get("SyncshellAdmin.Users.Remove") + UiSharedService.TooltipSeparator + Loc.Get("SyncshellAdmin.Users.RemoveHint"));

                                    ImGui.SameLine();
                                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                                    {
                                        if (_uiSharedService.IconButton(FontAwesomeIcon.Ban))
                                        {
                                            Mediator.Publish(new OpenBanUserPopupMessage(pair.Key, GroupFullInfo));
                                        }
                                    }
                                    UiSharedService.AttachToolTip(Loc.Get("SyncshellAdmin.Users.Ban") + UiSharedService.TooltipSeparator + Loc.Get("SyncshellAdmin.Users.BanHint"));
                                }
                            }
                        }
                    }
                }
                userNode.Dispose();
                var clearNode = ImRaii.TreeNode(Loc.Get("SyncshellAdmin.Cleanup.Tree"));
                if (clearNode)
                {
                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Broom, Loc.Get("SyncshellAdmin.Cleanup.Clear")))
                        {
                            _ = _apiController.GroupClear(new(GroupFullInfo.Group));
                        }
                    }
                    UiSharedService.AttachToolTip(Loc.Get("SyncshellAdmin.Cleanup.ClearTooltip") + UiSharedService.TooltipSeparator + Loc.Get("SyncshellAdmin.Cleanup.CtrlHint"));

                    ImGuiHelpers.ScaledDummy(2f);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(2f);

                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Unlink, Loc.Get("SyncshellAdmin.Cleanup.CheckInactive")))
                    {
                        _pruneTestTask = _apiController.GroupPrune(new(GroupFullInfo.Group), _pruneDays, execute: false);
                        _pruneTask = null;
                    }
                    UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.Cleanup.PruneTooltip"), _pruneDays)
                        + Environment.NewLine + Loc.Get("SyncshellAdmin.Cleanup.PruneReview")
                        + UiSharedService.TooltipSeparator + Loc.Get("SyncshellAdmin.Cleanup.PruneNote"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150);
                    _uiSharedService.DrawCombo(Loc.Get("SyncshellAdmin.Cleanup.PruneDaysLabel"), [7, 14, 30, 90], (count) =>
                    {
                        return string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.Cleanup.PruneDaysEntry"), count);
                    },
                    (selected) =>
                    {
                        _pruneDays = selected;
                        _pruneTestTask = null;
                        _pruneTask = null;
                    },
                    _pruneDays);

                    if (_pruneTestTask != null)
                    {
                        if (!_pruneTestTask.IsCompleted)
                        {
                            UiSharedService.ColorTextWrapped(Loc.Get("SyncshellAdmin.Cleanup.PruneCalculating"), ImGuiColors.DalamudYellow);
                        }
                        else
                        {
                            ImGui.AlignTextToFramePadding();
                            UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.Cleanup.PruneFound"), _pruneTestTask.Result, _pruneDays));
                            if (_pruneTestTask.Result > 0)
                            {
                                using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                                {
                                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Broom, Loc.Get("SyncshellAdmin.Cleanup.PruneExecute")))
                                    {
                                        _pruneTask = _apiController.GroupPrune(new(GroupFullInfo.Group), _pruneDays, execute: true);
                                        _pruneTestTask = null;
                                    }
                                }
                                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.Cleanup.PruneExecuteTooltip"), _pruneTestTask?.Result ?? 0)
                                    + UiSharedService.TooltipSeparator + Loc.Get("SyncshellAdmin.Cleanup.CtrlHint"));
                            }
                        }
                    }
                    if (_pruneTask != null)
                    {
                        if (!_pruneTask.IsCompleted)
                        {
                            UiSharedService.ColorTextWrapped(Loc.Get("SyncshellAdmin.Cleanup.Pruning"), ImGuiColors.DalamudYellow);
                        }
                        else
                        {
                            UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.Cleanup.PrunedResult"), _pruneTask.Result));
                        }
                    }
                }
                clearNode.Dispose();

                var banNode = ImRaii.TreeNode(Loc.Get("SyncshellAdmin.Bans.Tree"));
                if (banNode)
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Retweet, Loc.Get("SyncshellAdmin.Bans.Refresh")))
                    {
                        _bannedUsers = _apiController.GroupGetBannedUsers(new GroupDto(GroupFullInfo.Group)).Result;
                    }

                    if (ImGui.BeginTable("bannedusertable" + GroupFullInfo.GID, 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
                    {
                        ImGui.TableSetupColumn(Loc.Get("SyncshellAdmin.Bans.ColUid"), ImGuiTableColumnFlags.None, 1);
                        ImGui.TableSetupColumn(Loc.Get("SyncshellAdmin.Bans.ColAlias"), ImGuiTableColumnFlags.None, 1);
                        ImGui.TableSetupColumn(Loc.Get("SyncshellAdmin.Bans.ColBy"), ImGuiTableColumnFlags.None, 1);
                        ImGui.TableSetupColumn(Loc.Get("SyncshellAdmin.Bans.ColDate"), ImGuiTableColumnFlags.None, 2);
                        ImGui.TableSetupColumn(Loc.Get("SyncshellAdmin.Bans.ColReason"), ImGuiTableColumnFlags.None, 3);
                        ImGui.TableSetupColumn(Loc.Get("SyncshellAdmin.Bans.ColActions"), ImGuiTableColumnFlags.None, 1);

                        ImGui.TableHeadersRow();

                        foreach (var bannedUser in _bannedUsers.ToList())
                        {
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(bannedUser.UID);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(bannedUser.UserAlias ?? string.Empty);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(bannedUser.BannedBy);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(bannedUser.BannedOn.ToLocalTime().ToString(CultureInfo.CurrentCulture));
                            ImGui.TableNextColumn();
                            UiSharedService.TextWrapped(bannedUser.Reason);
                            ImGui.TableNextColumn();
                            using var pushId = ImRaii.PushId(bannedUser.UID);
                            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Check, Loc.Get("SyncshellAdmin.Bans.Unban")))
                            {
                                _ = Task.Run(async () => await _apiController.GroupUnbanUser(bannedUser).ConfigureAwait(false));
                                _bannedUsers.RemoveAll(b => string.Equals(b.UID, bannedUser.UID, StringComparison.Ordinal));
                            }
                        }

                        ImGui.EndTable();
                    }
                }
                banNode.Dispose();
            }
            mgmtTab.Dispose();

            var discoveryTab = ImRaii.TabItem(Loc.Get("SyncshellAdmin.Tab.AutoDetect"));
            if (discoveryTab)
            {
                DrawAutoDetectTab();
            }
            discoveryTab.Dispose();

            var permissionTab = ImRaii.TabItem(Loc.Get("SyncshellAdmin.Tab.Permissions"));
            if (permissionTab)
            {
                bool isDisableAnimations = perm.IsDisableAnimations();
                bool isDisableSounds = perm.IsDisableSounds();
                bool isDisableVfx = perm.IsDisableVFX();

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Permissions.Sound"));
                _uiSharedService.BooleanToColoredIcon(!isDisableSounds);
                ImGui.SameLine(230);
                if (_uiSharedService.IconTextButton(isDisableSounds ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp,
                    isDisableSounds ? Loc.Get("SyncshellAdmin.Permissions.EnableSound") : Loc.Get("SyncshellAdmin.Permissions.DisableSound")))
                {
                    perm.SetDisableSounds(!perm.IsDisableSounds());
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Permissions.Animation"));
                _uiSharedService.BooleanToColoredIcon(!isDisableAnimations);
                ImGui.SameLine(230);
                if (_uiSharedService.IconTextButton(isDisableAnimations ? FontAwesomeIcon.WindowClose : FontAwesomeIcon.Running,
                    isDisableAnimations ? Loc.Get("SyncshellAdmin.Permissions.EnableAnimation") : Loc.Get("SyncshellAdmin.Permissions.DisableAnimation")))
                {
                    perm.SetDisableAnimations(!perm.IsDisableAnimations());
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Permissions.Vfx"));
                _uiSharedService.BooleanToColoredIcon(!isDisableVfx);
                ImGui.SameLine(230);
                if (_uiSharedService.IconTextButton(isDisableVfx ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.Sun,
                    isDisableVfx ? Loc.Get("SyncshellAdmin.Permissions.EnableVfx") : Loc.Get("SyncshellAdmin.Permissions.DisableVfx")))
                {
                    perm.SetDisableVFX(!perm.IsDisableVFX());
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }
            }
            permissionTab.Dispose();
            var capacityTab = ImRaii.TabItem(Loc.Get("SyncshellAdmin.Tab.Capacity"));
            if (capacityTab)
            {
                if (!_capacityApplyInFlight
                    && _desiredCapacity != GroupFullInfo.MaxUserCount
                    && (_desiredCapacity < 1 || _desiredCapacity > _apiController.ServerInfo.MaxGroupUserCount))
                {
                    _desiredCapacity = GroupFullInfo.MaxUserCount;
                }

                // Use the greater of ServerInfo cap and the group's current MaxUserCount.
                // This avoids clamping down to an outdated server cap (e.g., 100)
                // when the server already accepted and persisted a higher value (e.g., 200).
                int serverCap = Math.Max(_apiController.ServerInfo.MaxGroupUserCount, GroupFullInfo.MaxUserCount);
                int currentMembers = 1;
                if (_pairManager.GroupPairs.TryGetValue(GroupFullInfo, out var pairsInGroup))
                    currentMembers += pairsInGroup.Count;
                int minCap = Math.Max(1, currentMembers);
                _desiredCapacity = Math.Clamp(_desiredCapacity, minCap, serverCap);

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Capacity.Label"));
                _uiSharedService.DrawHelpText(string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.Capacity.Help"), minCap, serverCap));
                ImGui.InputInt("##capacity_input", ref _desiredCapacity);
                if (_desiredCapacity < minCap) _desiredCapacity = minCap;
                if (_desiredCapacity > serverCap) _desiredCapacity = serverCap;
                // Removed capacity slider: keep only direct numeric input per request

                if (!string.IsNullOrEmpty(_capacityMessage))
                {
                    UiSharedService.ColorTextWrapped(_capacityMessage!, ImGuiColors.DalamudYellow);
                }

                bool changed = _desiredCapacity != GroupFullInfo.MaxUserCount;
                using (ImRaii.Disabled(_capacityApplyInFlight || !changed))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("SyncshellAdmin.Capacity.Apply")))
                    {
                        _capacityApplyInFlight = true;
                        try
                        {
                            if (_desiredCapacity < currentMembers)
                            {
                                _capacityMessage = string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.Capacity.BlockLowerThanMembers"), currentMembers);
                            }
                            else
                            {
                                var ok = _apiController.GroupSetMaxUserCount(new(GroupFullInfo.Group), _desiredCapacity).Result;
                                _capacityMessage = ok ? Loc.Get("SyncshellAdmin.Capacity.Changed") : Loc.Get("SyncshellAdmin.Capacity.ChangeFailed");
                            }
                        }
                        finally
                        {
                            _capacityApplyInFlight = false;
                        }
                    }
                }

                if (_capacityApplyInFlight)
                {
                    ImGui.SameLine();
                    UiSharedService.ColorText(Loc.Get("SyncshellAdmin.Capacity.Busy"), ImGuiColors.DalamudYellow);
                }
            }
            capacityTab.Dispose();

            var slotTab = ImRaii.TabItem(Loc.Get("SyncshellAdmin.Tab.Slot"));
            if (slotTab)
            {
                DrawSlotTab();
            }
            slotTab.Dispose();

            var profileTab = ImRaii.TabItem(Loc.Get("SyncshellAdmin.Tab.Profile"));
            if (profileTab)
            {
                DrawProfileTab();
            }
            profileTab.Dispose();

            DrawProfilePreview();

            if (_isOwner)
            {
                var ownerTab = ImRaii.TabItem(Loc.Get("SyncshellAdmin.Tab.Owner"));
                if (ownerTab)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Owner.NewPassword"));
                    var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                    var buttonSize = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Passport, Loc.Get("SyncshellAdmin.Owner.ChangePassword"));
                    var textSize = ImGui.CalcTextSize(Loc.Get("SyncshellAdmin.Owner.NewPassword")).X;
                    var spacing = ImGui.GetStyle().ItemSpacing.X;

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(availableWidth - buttonSize - textSize - spacing * 2);
                    ImGui.InputTextWithHint("##changepw", Loc.Get("SyncshellAdmin.Owner.PasswordPlaceholder"), ref _newPassword, 50);
                    ImGui.SameLine();
                    using (ImRaii.Disabled(_newPassword.Length < 10))
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Passport, Loc.Get("SyncshellAdmin.Owner.ChangePassword")))
                        {
                            _pwChangeSuccess = _apiController.GroupChangePassword(new GroupPasswordDto(GroupFullInfo.Group, _newPassword)).Result;
                            _newPassword = string.Empty;
                        }
                    }
                    UiSharedService.AttachToolTip(Loc.Get("SyncshellAdmin.Owner.ChangePasswordTooltip"));

                    if (!_pwChangeSuccess)
                    {
                        UiSharedService.ColorTextWrapped(Loc.Get("SyncshellAdmin.Owner.ChangePasswordFailed"), ImGuiColors.DalamudYellow);
                    }

                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("SyncshellAdmin.Owner.DeleteSyncshell")) && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
                    {
                        IsOpen = false;
                        _ = _apiController.GroupDelete(new(GroupFullInfo.Group));
                    }
                    UiSharedService.AttachToolTip(Loc.Get("SyncshellAdmin.Owner.DeleteTooltip"));
                }
                ownerTab.Dispose();
            }
        }
    }

    private void DrawSlotTab()
    {
        if (_slotLoading)
        {
            ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Slot.Loading"));
            return;
        }

        UiSharedService.TextWrapped(Loc.Get("SyncshellAdmin.Slot.Description"));
        ImGuiHelpers.ScaledDummy(5);
        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
        UiSharedService.TextWrapped(Loc.Get("SyncshellAdmin.Slot.AutoLeaveWarning"));
        ImGui.PopStyleColor();
        ImGuiHelpers.ScaledDummy(5);

        if (ImGui.BeginTable("slots_table", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            ImGui.TableSetupColumn(Loc.Get("SyncshellAdmin.Slot.Name"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Loc.Get("SyncshellAdmin.Slot.Location"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Loc.Get("SyncshellAdmin.Slot.Actions"), ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var slot in _slots.ToList())
            {
                using var slotId = ImRaii.PushId("slot_" + slot.SlotId);

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(slot.SlotName);
                if (!string.IsNullOrEmpty(slot.SlotDescription))
                {
                    ImGui.SameLine();
                    _uiSharedService.IconText(FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudGrey);
                    UiSharedService.AttachToolTip(slot.SlotDescription);
                }

                ImGui.TableNextColumn();
                if (slot.Location != null)
                {
                    string worldName = _dalamudUtilService.WorldData.Value.TryGetValue((ushort)slot.Location.ServerId, out var world) ? world : slot.Location.ServerId.ToString();
                    string territoryName = _dalamudUtilService.TerritoryData.Value.TryGetValue(slot.Location.TerritoryId, out var territory) ? territory : slot.Location.TerritoryId.ToString();
                    ImGui.TextUnformatted($"{worldName}, {territoryName}");
                    ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "S:{0} P:{1} (X:{2:F1} Y:{3:F1} Z:{4:F1})", slot.Location.WardId, slot.Location.PlotId, slot.Location.X, slot.Location.Y, slot.Location.Z));
                }
                else
                {
                    ImGui.TextDisabled("No location data");
                }

                ImGui.TableNextColumn();
                if (_uiSharedService.IconButton(FontAwesomeIcon.Edit))
                {
                    _selectedSlot = slot;
                    _slotName = slot.SlotName;
                    _slotDescription = slot.SlotDescription ?? string.Empty;
                    _slotServerId = slot.Location?.ServerId ?? 0;
                    _slotTerritoryId = slot.Location?.TerritoryId ?? 0;
                    _slotDivisionId = slot.Location?.DivisionId ?? 0;
                    _slotWardId = slot.Location?.WardId ?? 0;
                    _slotPlotId = slot.Location?.PlotId ?? 0;
                    _slotX = slot.Location?.X ?? 0;
                    _slotY = slot.Location?.Y ?? 0;
                    _slotZ = slot.Location?.Z ?? 0;
                    _slotRadius = slot.Location?.Radius ?? 25f;
                }
                UiSharedService.AttachToolTip(Loc.Get("SyncshellAdmin.Slot.EditTooltip"));
                ImGui.SameLine();
                if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                {
                    _ = Task.Run(async () =>
                    {
                        var request = new SlotUpdateRequestDto
                        {
                            Group = new GroupDto(GroupFullInfo.Group),
                            SlotId = slot.SlotId,
                            IsDelete = true
                        };
                        if (await _apiController.SlotUpdate(request).ConfigureAwait(false))
                        {
                            await LoadSlotData().ConfigureAwait(false);
                            Mediator.Publish(new NotificationMessage(Loc.Get("SyncshellAdmin.Slot.DeleteSuccessTitle"), Loc.Get("SyncshellAdmin.Slot.DeleteSuccessMessage"), NotificationType.Info));
                        }
                    });
                }
                UiSharedService.AttachToolTip(Loc.Get("SyncshellAdmin.Slot.DeleteTooltip"));
            }
            ImGui.EndTable();
        }

        if (_selectedSlot == null)
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("SyncshellAdmin.Slot.AddButton")))
            {
                _selectedSlot = new SlotInfoResponseDto { SlotId = Guid.Empty };
                _slotName = string.Empty;
                _slotDescription = string.Empty;
                _slotServerId = 0;
                _slotTerritoryId = 0;
                _slotDivisionId = 0;
                _slotWardId = 0;
                _slotPlotId = 0;
                _slotX = 0;
                _slotY = 0;
                _slotZ = 0;
                _slotRadius = 10f;
            }
        }
        else
        {
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(10);
            ImGui.TextUnformatted(_selectedSlot.SlotId == Guid.Empty ? Loc.Get("SyncshellAdmin.Slot.AddingNew") : Loc.Get("SyncshellAdmin.Slot.Editing") + ": " + _selectedSlot.SlotName);

            ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Slot.Name"));
            ImGui.InputText("##slotname", ref _slotName, 100);
            _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.Slot.NameHelp"));

            ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Slot.SlotDescription"));
            ImGui.InputTextMultiline("##slotdesc", ref _slotDescription, 500, new Vector2(-1, 60));
            _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.Slot.SlotDescriptionHelp"));

            ImGuiHelpers.ScaledDummy(5);
            ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Slot.Radius"));
            ImGui.SliderFloat("##slotradius", ref _slotRadius, 5f, 20f, "%.1f m");
            _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.Slot.RadiusHelp"));

            ImGuiHelpers.ScaledDummy(5);
            ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Slot.Location"));
            ImGui.Separator();

            ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Slot.Step1Outside"));
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.MapMarkerAlt, Loc.Get("SyncshellAdmin.Slot.GetOutsidePos")))
            {
                var player = _dalamudUtilService.GetPlayerCharacter();
                if (player != null)
                {
                    _slotX = player.Position.X;
                    _slotY = player.Position.Y;
                    _slotZ = player.Position.Z;

                    var mapData = _dalamudUtilService.GetMapData();
                    _slotTerritoryId = mapData.TerritoryId;
                    _slotServerId = mapData.ServerId;

                    _logger.LogInformation("Captured outside position: {x}, {y}, {z} on Territory {t}", _slotX, _slotY, _slotZ, _slotTerritoryId);
                }
            }
            _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.Slot.GetOutsidePosHelp"));
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.Slot.LocationCoords"), _slotX, _slotY, _slotZ));

            ImGuiHelpers.ScaledDummy(5);
            ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Slot.Step2Inside"));

            // Vérifier si le joueur est en mode housing
            var isInHousingEditMode = _dalamudUtilService.IsInHousingMode;

            using (ImRaii.Disabled(!isInHousingEditMode))
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Home, Loc.Get("SyncshellAdmin.Slot.GetHousingInfo")))
                {
                    var currentMapData = _dalamudUtilService.GetMapData();
                    _slotServerId = currentMapData.ServerId;
                    _slotDivisionId = currentMapData.DivisionId;
                    _slotWardId = currentMapData.WardId;
                    _slotPlotId = currentMapData.HouseId;
                    _logger.LogInformation("Captured housing info: S:{s} T:{t} D:{d} W:{w} P:{p}", _slotServerId, _slotTerritoryId, _slotDivisionId, _slotWardId, _slotPlotId);
                }
            }

            if (!isInHousingEditMode)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.5f, 0.2f, 1.0f));
                ImGui.TextWrapped(Loc.Get("SyncshellAdmin.Slot.MustBeInHousingEditMode"));
                ImGui.PopStyleColor();
            }
            else
            {
                _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.Slot.GetHousingInfoHelp"));
            }
            if (_slotServerId != 0)
            {
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.Slot.HousingSummary"), _slotServerId, _slotTerritoryId, _slotWardId, _slotPlotId));
            }

            ImGuiHelpers.ScaledDummy(10);
            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_slotName) || (_slotPlotId == 0 && _slotRadius < 0.001f)))
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("SyncshellAdmin.Slot.Save")))
                {
                    var slotId = _selectedSlot.SlotId;
                    _ = Task.Run(async () =>
                    {
                        var request = new SlotUpdateRequestDto
                        {
                            Group = new GroupDto(GroupFullInfo.Group),
                            SlotId = slotId,
                            SlotName = _slotName,
                            SlotDescription = _slotDescription,
                            Location = new SlotLocationDto
                            {
                                ServerId = _slotServerId,
                                TerritoryId = _slotTerritoryId,
                                DivisionId = _slotDivisionId,
                                WardId = _slotWardId,
                                PlotId = _slotPlotId,
                                X = _slotX,
                                Y = _slotY,
                                Z = _slotZ,
                                Radius = _slotRadius
                            }
                        };
                        _logger.LogInformation("Saving slot: Name={name}, Server={server}, Territory={territory}, Division={division}, Ward={ward}, Plot={plot}, Pos=({x},{y},{z}), Radius={radius}",
                            _slotName, _slotServerId, _slotTerritoryId, _slotDivisionId, _slotWardId, _slotPlotId, _slotX, _slotY, _slotZ, _slotRadius);
                        var success = await _apiController.SlotUpdate(request).ConfigureAwait(false);
                        if (success)
                        {
                            await LoadSlotData().ConfigureAwait(false);
                            _selectedSlot = null;
                            Mediator.Publish(new NotificationMessage(Loc.Get("SyncshellAdmin.Slot.SaveSuccessTitle"), Loc.Get("SyncshellAdmin.Slot.SaveSuccessMessage"), NotificationType.Info));
                        }
                        else
                        {
                            _logger.LogWarning("Failed to save slot {name} - server returned false", _slotName);
                            Mediator.Publish(new NotificationMessage(Loc.Get("SyncshellAdmin.Slot.SaveErrorTitle"), Loc.Get("SyncshellAdmin.Slot.SaveErrorMessage"), NotificationType.Error));
                        }
                    });
                }
            }
            ImGui.SameLine();
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, Loc.Get("SyncshellAdmin.Slot.Cancel")))
            {
                _selectedSlot = null;
            }
        }
    }

    private void DrawProfileTab()
    {
        if (!_profileLoaded && !_profileLoading)
        {
            _profileLoading = true;
            _ = LoadGroupProfileAsync();
        }

        UiSharedService.TextWrapped(Loc.Get("SyncshellAdmin.Profile.Intro"));
        ImGuiHelpers.ScaledDummy(4);

        if (_profileLoading)
        {
            ImGui.TextDisabled(Loc.Get("SyncshellAdmin.Profile.Loading"));
            return;
        }

        if (!string.IsNullOrEmpty(_profileMessage))
        {
            UiSharedService.ColorTextWrapped(_profileMessage!, ImGuiColors.DalamudYellow);
            ImGuiHelpers.ScaledDummy(4);
        }

        // Profile image
        ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Profile.ProfileImage"));
        if (_profileTexture != null && _profileImageBytes.Length > 0)
        {
            float imgSize = 128f;
            float imgRounding = 10f * ImGuiHelpers.GlobalScale;
            var drawList = ImGui.GetWindowDrawList();
            var imgMin = ImGui.GetCursorScreenPos();
            var imgMax = new Vector2(imgMin.X + imgSize, imgMin.Y + imgSize);
            drawList.AddImageRounded(
                _profileTexture.Handle, imgMin, imgMax,
                Vector2.Zero, Vector2.One,
                ImGui.ColorConvertFloat4ToU32(Vector4.One), imgRounding);
            ImGui.Dummy(new Vector2(imgSize, imgSize));
        }
        else
        {
            ImGui.TextDisabled(Loc.Get("SyncshellAdmin.Profile.NoImage"));
        }
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, Loc.Get("SyncshellAdmin.Profile.UploadImage")))
        {
            _fileDialogManager.OpenFileDialog(
                Loc.Get("SyncshellAdmin.Profile.UploadImage"),
                "Image files{.png,.jpg,.jpeg}",
                (success, name) =>
                {
                    if (!success) return;
                    _ = Task.Run(async () =>
                    {
                        var bytes = await File.ReadAllBytesAsync(name).ConfigureAwait(false);
                        if (bytes.Length > 2 * 1024 * 1024)
                        {
                            _profileMessage = Loc.Get("SyncshellAdmin.Profile.ImageTooLarge");
                            return;
                        }
                        _profileImageBytes = bytes;
                        _profileTexture?.Dispose();
                        _profileTexture = _uiSharedService.LoadImage(bytes);
                    });
                });
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(_profileImageBytes.Length == 0))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("SyncshellAdmin.Profile.ClearImage")))
            {
                _profileImageBytes = [];
                _profileTexture?.Dispose();
                _profileTexture = null;
            }
        }

        ImGuiHelpers.ScaledDummy(4);

        // Banner
        ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Profile.BannerImage"));
        if (_bannerTexture != null && _bannerImageBytes.Length > 0)
        {
            float availWidth = ImGui.GetContentRegionAvail().X;
            float bannerHeight = availWidth * (260f / 840f);
            float bannerRounding = 10f * ImGuiHelpers.GlobalScale;
            var bannerDrawList = ImGui.GetWindowDrawList();
            var bannerMin = ImGui.GetCursorScreenPos();
            var bannerMax = new Vector2(bannerMin.X + availWidth, bannerMin.Y + bannerHeight);
            bannerDrawList.AddImageRounded(
                _bannerTexture.Handle, bannerMin, bannerMax,
                Vector2.Zero, Vector2.One,
                ImGui.ColorConvertFloat4ToU32(Vector4.One), bannerRounding);
            ImGui.Dummy(new Vector2(availWidth, bannerHeight));
        }
        else
        {
            ImGui.TextDisabled(Loc.Get("SyncshellAdmin.Profile.NoBanner"));
        }
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, Loc.Get("SyncshellAdmin.Profile.UploadBanner")))
        {
            _fileDialogManager.OpenFileDialog(
                Loc.Get("SyncshellAdmin.Profile.UploadBanner"),
                "Image files{.png,.jpg,.jpeg}",
                (success, name) =>
                {
                    if (!success) return;
                    _ = Task.Run(async () =>
                    {
                        var bytes = await File.ReadAllBytesAsync(name).ConfigureAwait(false);
                        if (bytes.Length > 2 * 1024 * 1024)
                        {
                            _profileMessage = Loc.Get("SyncshellAdmin.Profile.ImageTooLarge");
                            return;
                        }
                        _bannerImageBytes = bytes;
                        _bannerTexture?.Dispose();
                        _bannerTexture = _uiSharedService.LoadImage(bytes);
                    });
                });
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(_bannerImageBytes.Length == 0))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("SyncshellAdmin.Profile.ClearBanner")))
            {
                _bannerImageBytes = [];
                _bannerTexture?.Dispose();
                _bannerTexture = null;
            }
        }

        ImGuiHelpers.ScaledDummy(4);

        // Description
        ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Profile.Description"));
        ImGui.InputTextMultiline("##profile_desc", ref _profileDescription, 1500, new Vector2(-1, 80));

        ImGuiHelpers.ScaledDummy(4);

        // Tags
        ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Profile.Tags"));
        for (int i = _profileTags.Count - 1; i >= 0; i--)
        {
            ImGui.TextUnformatted(_profileTags[i]);
            ImGui.SameLine();
            using var tagId = ImRaii.PushId("tag_" + i);
            if (_uiSharedService.IconButton(FontAwesomeIcon.Times))
            {
                _profileTags.RemoveAt(i);
            }
        }
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##new_tag", ref _newTag, 50);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_newTag) || _profileTags.Count >= 20))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("SyncshellAdmin.Profile.AddTag")))
            {
                _profileTags.Add(_newTag.Trim());
                _newTag = string.Empty;
            }
        }

        ImGuiHelpers.ScaledDummy(4);

        // NSFW + Disabled
        ImGui.Checkbox(Loc.Get("SyncshellAdmin.Profile.NSFW"), ref _profileNsfw);
        _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.Profile.NSFWHelp"));
        ImGui.Checkbox(Loc.Get("SyncshellAdmin.Profile.Disabled"), ref _profileDisabled);
        _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.Profile.DisabledHelp"));

        ImGuiHelpers.ScaledDummy(6);

        using (ImRaii.Disabled(_profileSaving))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("SyncshellAdmin.Profile.Save")))
            {
                _ = SaveGroupProfileAsync();
            }
        }
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Eye, Loc.Get("SyncshellAdmin.Profile.Preview")))
        {
            _showProfilePreview = true;
        }
        ImGui.SameLine();
        if (ImGui.Button(Loc.Get("SyncshellAdmin.Profile.Cancel")))
        {
            _profileLoaded = false;
            _profileMessage = null;
        }

        ImGui.SameLine();
        if ((DateTime.UtcNow - _profileSaveConfirmTime).TotalSeconds < 4)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, Loc.Get("SyncshellAdmin.Profile.Saved"));
        }
        else if (HasProfileUnsavedChanges())
        {
            ImGui.TextColored(new Vector4(0.85f, 0.75f, 0.20f, 1f), Loc.Get("SyncshellAdmin.Profile.UnsavedChanges"));
        }
    }

    private async Task LoadGroupProfileAsync()
    {
        try
        {
            var profile = await _apiController.GroupGetProfile(new(GroupFullInfo.Group)).ConfigureAwait(false);
            if (profile != null)
            {
                _profileDescription = profile.Description ?? string.Empty;
                _profileTags = profile.Tags?.ToList() ?? [];
                _profileNsfw = profile.IsNsfw;
                _profileDisabled = profile.IsDisabled;

                if (!string.IsNullOrEmpty(profile.ProfileImageBase64))
                {
                    _profileImageBytes = Convert.FromBase64String(profile.ProfileImageBase64);
                    _profileTexture = _uiSharedService.LoadImage(_profileImageBytes);
                }
                else
                {
                    _profileImageBytes = [];
                    _profileTexture = null;
                }

                if (!string.IsNullOrEmpty(profile.BannerImageBase64))
                {
                    _bannerImageBytes = Convert.FromBase64String(profile.BannerImageBase64);
                    _bannerTexture = _uiSharedService.LoadImage(_bannerImageBytes);
                }
                else
                {
                    _bannerImageBytes = [];
                    _bannerTexture = null;
                }

                _profileManager.SetGroupProfile(GroupFullInfo.GID, profile);
            }
            _profileLoaded = true;
            SnapshotProfileState();
        }
        catch (Exception ex)
        {
            _profileMessage = string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.Profile.SaveFailed"), ex.Message);
        }
        finally
        {
            _profileLoading = false;
        }
    }

    private async Task SaveGroupProfileAsync()
    {
        _profileSaving = true;
        _profileMessage = null;
        try
        {
            var dto = new GroupProfileDto
            {
                Group = GroupFullInfo.Group,
                Description = string.IsNullOrWhiteSpace(_profileDescription) ? null : _profileDescription,
                Tags = _profileTags.Count > 0 ? _profileTags.ToArray() : null,
                ProfileImageBase64 = _profileImageBytes.Length > 0 ? Convert.ToBase64String(_profileImageBytes) : null,
                BannerImageBase64 = _bannerImageBytes.Length > 0 ? Convert.ToBase64String(_bannerImageBytes) : null,
                IsNsfw = _profileNsfw,
                IsDisabled = _profileDisabled,
            };

            await _apiController.GroupSetProfile(dto).ConfigureAwait(false);
            _profileManager.SetGroupProfile(GroupFullInfo.GID, dto);
            _profileMessage = null;
            SnapshotProfileState();
            _profileSaveConfirmTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _profileMessage = Loc.Get("SyncshellAdmin.Profile.SaveFailed");
            _logger.LogWarning(ex, "Failed to save group profile for {gid}", GroupFullInfo.GID);
        }
        finally
        {
            _profileSaving = false;
        }
    }

    private void DrawAutoDetectTab()
    {
        if (!_autoDetectStateInitialized && !_autoDetectStateLoading)
        {
            _autoDetectStateInitialized = true;
            _autoDetectStateLoading = true;
            _ = EnsureAutoDetectStateAsync();
        }

        UiSharedService.TextWrapped(Loc.Get("SyncshellAdmin.AutoDetect.Description"));
        ImGuiHelpers.ScaledDummy(4);

        if (_autoDetectStateLoading)
        {
            ImGui.TextDisabled(Loc.Get("SyncshellAdmin.AutoDetect.Loading"));
        }

        if (!string.IsNullOrEmpty(_autoDetectMessage))
        {
            UiSharedService.ColorTextWrapped(_autoDetectMessage!, ImGuiColors.DalamudYellow);
        }

        DrawAutoDetectStatus();

        using (ImRaii.Disabled(_autoDetectToggleInFlight || _autoDetectStateLoading))
        {
            ImGui.Checkbox(Loc.Get("SyncshellAdmin.AutoDetect.CheckboxLabel"), ref _autoDetectDesiredVisibility);
        }
        _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.AutoDetect.CheckboxHelp"));

        if (_autoDetectDesiredVisibility)
        {
            ImGuiHelpers.ScaledDummy(4);
            ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.AutoDetect.Options"));
            ImGui.Separator();

            // Mode selection via radio buttons
            var modeFulltime = _adMode == AutoDetectMode.Fulltime;
            var modeDuration = _adMode == AutoDetectMode.Duration;
            var modeRecurring = _adMode == AutoDetectMode.Recurring;

            if (ImGui.RadioButton(Loc.Get("SyncshellAdmin.AutoDetect.Fulltime"), modeFulltime))
            {
                _adMode = AutoDetectMode.Fulltime;
            }
            _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.AutoDetect.FulltimeHelp"));

            if (ImGui.RadioButton(Loc.Get("SyncshellAdmin.AutoDetect.Duration"), modeDuration))
            {
                _adMode = AutoDetectMode.Duration;
            }
            _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.AutoDetect.DurationModeHelp"));

            if (ImGui.RadioButton(Loc.Get("SyncshellAdmin.AutoDetect.Recurring"), modeRecurring))
            {
                _adMode = AutoDetectMode.Recurring;
            }
            _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.AutoDetect.RecurringHelp"));

            // Duration in hours (only for Duration mode)
            if (_adMode == AutoDetectMode.Duration)
            {
                ImGuiHelpers.ScaledDummy(4);
                int duration = _adDurationHours;
                ImGui.PushItemWidth(120 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt(Loc.Get("SyncshellAdmin.AutoDetect.DurationHours"), ref duration))
                {
                    _adDurationHours = Math.Clamp(duration, 1, 240);
                }
                ImGui.PopItemWidth();
                _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.AutoDetect.DurationHelp"));
            }

            ImGuiHelpers.ScaledDummy(4);
            if (_adMode == AutoDetectMode.Recurring)
            {
                ImGuiHelpers.ScaledDummy(4);
                ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.AutoDetect.Weekdays"));
                string[] daysFr = new[]
                {
                    Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Mon"),
                    Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Tue"),
                    Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Wed"),
                    Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Thu"),
                    Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Fri"),
                    Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Sat"),
                    Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Sun"),
                };
                for (int i = 0; i < 7; i++)
                {
                    ImGui.SameLine(0);
                    bool v = _adWeekdays[i];
                    if (ImGui.Checkbox($"##adwd{i}", ref v))
                    {
                        _adWeekdays[i] = v;
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted(daysFr[i]);
                    if (i < 6) ImGui.SameLine();
                }
                ImGui.NewLine();
                _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.AutoDetect.WeekdaysHelp"));

                ImGuiHelpers.ScaledDummy(4);
                ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.AutoDetect.TimeRangeLabel"));
                ImGui.PushItemWidth(60 * ImGuiHelpers.GlobalScale);
                _ = ImGui.InputInt($"{Loc.Get("SyncshellAdmin.AutoDetect.StartHour")}##adStartHour", ref _adStartHour); ImGui.SameLine();
                _ = ImGui.InputInt($"{Loc.Get("SyncshellAdmin.AutoDetect.StartMinute")}##adStartMinute", ref _adStartMinute);
                _adStartHour = Math.Clamp(_adStartHour, 0, 23);
                _adStartMinute = Math.Clamp(_adStartMinute, 0, 59);
                ImGui.SameLine();
                ImGui.TextUnformatted("→"); ImGui.SameLine();
                _ = ImGui.InputInt($"{Loc.Get("SyncshellAdmin.AutoDetect.EndHour")}##adEndHour", ref _adEndHour); ImGui.SameLine();
                _ = ImGui.InputInt($"{Loc.Get("SyncshellAdmin.AutoDetect.EndMinute")}##adEndMinute", ref _adEndMinute);
                _adEndHour = Math.Clamp(_adEndHour, 0, 23);
                _adEndMinute = Math.Clamp(_adEndMinute, 0, 59);
                ImGui.PopItemWidth();
                _uiSharedService.DrawHelpText(Loc.Get("SyncshellAdmin.AutoDetect.TimeRangeHelp"));
            }
        }

        if (_autoDetectPasswordDisabled && _autoDetectVisible)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("SyncshellAdmin.AutoDetect.PasswordDisabled"), ImGuiColors.DalamudYellow);
        }

        ImGuiHelpers.ScaledDummy(6);
        using (ImRaii.Disabled(_autoDetectToggleInFlight || _autoDetectStateLoading))
        {
            if (ImGui.Button(Loc.Get("SyncshellAdmin.AutoDetect.Submit")))
            {
                _ = SubmitAutoDetectAsync();
            }
            ImGui.SameLine();
            if (ImGui.Button(Loc.Get("SyncshellAdmin.AutoDetect.Refresh")))
            {
                _autoDetectStateLoading = true;
                _ = EnsureAutoDetectStateAsync(true);
            }
        }
    }

    private void DrawAutoDetectStatus()
    {
        var parts = new List<string>
        {
            _autoDetectVisible
                ? Loc.Get("SyncshellAdmin.AutoDetect.Status.Active")
                : Loc.Get("SyncshellAdmin.AutoDetect.Status.Inactive")
        };

        if (_autoDetectDesiredVisibility != _autoDetectVisible)
        {
            parts.Add(Loc.Get("SyncshellAdmin.AutoDetect.Status.Pending"));
        }

        if (_autoDetectDesiredVisibility)
        {
            if (_adMode == AutoDetectMode.Fulltime)
            {
                parts.Add(Loc.Get("SyncshellAdmin.AutoDetect.Status.Fulltime"));
            }
            else if (_adMode == AutoDetectMode.Recurring)
            {
                var selectedDays = GetSelectedWeekdays();
                if (selectedDays.Count > 0)
                {
                    var start = $"{_adStartHour:00}:{_adStartMinute:00}";
                    var end = $"{_adEndHour:00}:{_adEndMinute:00}";
                    parts.Add(string.Format(CultureInfo.CurrentCulture,
                        Loc.Get("SyncshellAdmin.AutoDetect.Status.Recurring"),
                        string.Join(", ", selectedDays),
                        start,
                        end,
                        _adTimeZone));
                }
                else
                {
                    parts.Add(Loc.Get("SyncshellAdmin.AutoDetect.Status.NoWeekdays"));
                }
            }
            else
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture,
                    Loc.Get("SyncshellAdmin.AutoDetect.Status.OneTime"),
                    _adDurationHours));
            }
        }

        var statusColor = _autoDetectVisible ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudYellow;
        UiSharedService.ColorTextWrapped(string.Join(" ", parts), statusColor);
        ImGuiHelpers.ScaledDummy(3);
    }

    private List<string> GetSelectedWeekdays()
    {
        var labels = new List<string>(7);
        string[] dayLabels =
        {
            Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Mon"),
            Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Tue"),
            Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Wed"),
            Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Thu"),
            Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Fri"),
            Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Sat"),
            Loc.Get("SyncshellAdmin.AutoDetect.Weekday.Sun"),
        };

        for (int i = 0; i < _adWeekdays.Length && i < dayLabels.Length; i++)
        {
            if (_adWeekdays[i]) labels.Add(dayLabels[i]);
        }

        return labels;
    }

    private async Task EnsureAutoDetectStateAsync(bool force = false)
    {
        try
        {
            var state = await _syncshellDiscoveryService.GetStateAsync(GroupFullInfo.GID, CancellationToken.None).ConfigureAwait(false);
            if (state != null)
            {
                ApplyAutoDetectState(state.AutoDetectVisible, state.PasswordTemporarilyDisabled, true);
                ApplyAutoDetectSchedule(state);
                _autoDetectMessage = null;
            }
            else if (force)
            {
                _autoDetectMessage = Loc.Get("SyncshellAdmin.AutoDetect.StateFetchFailed");
            }
        }
        catch (Exception ex)
        {
            _autoDetectMessage = force ? string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.AutoDetect.RefreshError"), ex.Message) : _autoDetectMessage;
        }
        finally
        {
            _autoDetectStateLoading = false;
        }
    }

    private async Task SubmitAutoDetectAsync()
    {
        if (_autoDetectToggleInFlight)
        {
            return;
        }

        _autoDetectToggleInFlight = true;
        _autoDetectMessage = null;

        try
        {
            // Determine mode override
            AutoDetectMode? modeOverride = null;
            if (!_autoDetectDesiredVisibility)
            {
                modeOverride = AutoDetectMode.Off;
            }
            else if (_adMode == AutoDetectMode.Fulltime)
            {
                modeOverride = AutoDetectMode.Fulltime;
            }

            // Duration only used when visible and in Duration mode
            int? duration = _autoDetectDesiredVisibility && _adMode == AutoDetectMode.Duration ? _adDurationHours : null;

            // Scheduling fields only if recurring is enabled
            int[]? weekdaysArr = null;
            TimeSpan? start = null;
            TimeSpan? end = null;
            string? tz = null;
            if (_autoDetectDesiredVisibility && _adMode == AutoDetectMode.Recurring)
            {
                List<int> weekdays = new();
                for (int i = 0; i < 7; i++) if (_adWeekdays[i]) weekdays.Add(i);
                weekdaysArr = weekdays.Count > 0 ? weekdays.ToArray() : Array.Empty<int>();
                start = new TimeSpan(_adStartHour, _adStartMinute, 0);
                end = new TimeSpan(_adEndHour, _adEndMinute, 0);
                tz = _adTimeZone;
            }

            var ok = await _syncshellDiscoveryService.SetVisibilityAsync(
                GroupFullInfo.GID,
                _autoDetectDesiredVisibility,
                duration,
                weekdaysArr,
                start,
                end,
                tz,
                CancellationToken.None,
                modeOverride).ConfigureAwait(false);

            if (!ok)
            {
                _autoDetectMessage = Loc.Get("SyncshellAdmin.AutoDetect.SubmitFailed");
                return;
            }

            await EnsureAutoDetectStateAsync(true).ConfigureAwait(false);
            _autoDetectMessage = _autoDetectDesiredVisibility
                ? Loc.Get("SyncshellAdmin.AutoDetect.VisibleOn")
                : Loc.Get("SyncshellAdmin.AutoDetect.VisibleOff");

            if (_autoDetectDesiredVisibility)
            {
                PublishSyncshellPublicNotification();
            }
        }
        catch (Exception ex)
        {
            _autoDetectMessage = string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.AutoDetect.SubmitError"), ex.Message);
        }
        finally
        {
            _autoDetectToggleInFlight = false;
        }
    }

    private void ApplyAutoDetectState(bool visible, bool passwordDisabled, bool fromServer)
    {
        _autoDetectVisible = visible;
        _autoDetectDesiredVisibility = visible;
        _autoDetectPasswordDisabled = passwordDisabled;
        if (fromServer)
        {
            GroupFullInfo.AutoDetectVisible = visible;
            GroupFullInfo.PasswordTemporarilyDisabled = passwordDisabled;
        }
    }

    private void ResetAutoDetectScheduleFields()
    {
        _adMode = AutoDetectMode.Duration;
        _adDurationHours = 2;
        Array.Clear(_adWeekdays);
        _adStartHour = 21;
        _adStartMinute = 0;
        _adEndHour = 23;
        _adEndMinute = 0;
        _adTimeZone = "Europe/Paris";
    }

    private void ApplyAutoDetectSchedule(SyncshellDiscoveryStateDto state)
    {
        // Use Mode from server if available, otherwise infer
        if (state.Mode.HasValue)
        {
            if (state.Mode.Value == AutoDetectMode.Fulltime)
            {
                _adMode = AutoDetectMode.Fulltime;
                return;
            }
            else if (state.Mode.Value == AutoDetectMode.Off)
            {
                ResetAutoDetectScheduleFields();
                return;
            }
        }

        var hasServerSchedule = state.DisplayDurationHours.HasValue
            || (state.ActiveWeekdays != null && state.ActiveWeekdays.Length > 0)
            || !string.IsNullOrWhiteSpace(state.TimeStartLocal)
            || !string.IsNullOrWhiteSpace(state.TimeEndLocal)
            || !string.IsNullOrWhiteSpace(state.TimeZone);

        if (!hasServerSchedule)
        {
            // No schedule and no explicit mode: if visible, assume Fulltime; otherwise reset
            if (state.AutoDetectVisible)
            {
                _adMode = AutoDetectMode.Fulltime;
            }
            else
            {
                ResetAutoDetectScheduleFields();
            }
            return;
        }

        if (state.DisplayDurationHours.HasValue)
        {
            _adDurationHours = Math.Clamp(state.DisplayDurationHours.Value, 1, 240);
        }

        var isRecurring = state.ActiveWeekdays != null && state.ActiveWeekdays.Length > 0;
        _adMode = isRecurring ? AutoDetectMode.Recurring : AutoDetectMode.Duration;
        Array.Clear(_adWeekdays);
        if (state.ActiveWeekdays != null)
        {
            foreach (var day in state.ActiveWeekdays)
            {
                if (day >= 0 && day < _adWeekdays.Length) _adWeekdays[day] = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(state.TimeStartLocal) && TimeSpan.TryParse(state.TimeStartLocal, CultureInfo.InvariantCulture, out var start))
        {
            _adStartHour = Math.Clamp(start.Hours, 0, 23);
            _adStartMinute = Math.Clamp(start.Minutes, 0, 59);
        }
        if (!string.IsNullOrWhiteSpace(state.TimeEndLocal) && TimeSpan.TryParse(state.TimeEndLocal, CultureInfo.InvariantCulture, out var end))
        {
            _adEndHour = Math.Clamp(end.Hours, 0, 23);
            _adEndMinute = Math.Clamp(end.Minutes, 0, 59);
        }
        if (!string.IsNullOrWhiteSpace(state.TimeZone))
        {
            _adTimeZone = state.TimeZone!;
        }
    }

    private void OnSyncshellAutoDetectStateChanged(SyncshellAutoDetectStateChanged msg)
    {
        if (!string.Equals(msg.Gid, GroupFullInfo.GID, StringComparison.OrdinalIgnoreCase)) return;
        ApplyAutoDetectState(msg.Visible, msg.PasswordTemporarilyDisabled, true);
        _autoDetectMessage = null;
    }

    private void SnapshotProfileState()
    {
        _savedProfileDescription = _profileDescription;
        _savedProfileTags = [.. _profileTags];
        _savedProfileNsfw = _profileNsfw;
        _savedProfileDisabled = _profileDisabled;
        _savedProfileImageBytes = _profileImageBytes;
        _savedBannerImageBytes = _bannerImageBytes;
    }

    private bool HasProfileUnsavedChanges()
    {
        if (!string.Equals(_profileDescription, _savedProfileDescription, StringComparison.Ordinal))
            return true;
        if (_profileNsfw != _savedProfileNsfw || _profileDisabled != _savedProfileDisabled)
            return true;
        if (!_profileImageBytes.SequenceEqual(_savedProfileImageBytes))
            return true;
        if (!_bannerImageBytes.SequenceEqual(_savedBannerImageBytes))
            return true;
        if (_profileTags.Count != _savedProfileTags.Count)
            return true;
        for (int i = 0; i < _profileTags.Count; i++)
        {
            if (!string.Equals(_profileTags[i], _savedProfileTags[i], StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private void DrawProfilePreview()
    {
        if (!_showProfilePreview) return;

        ImGui.SetNextWindowSize(new Vector2(420, 500) * ImGuiHelpers.GlobalScale, ImGuiCond.FirstUseEver);
        if (ImGui.Begin(string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.Profile.PreviewTitle"), GroupFullInfo.GroupAliasOrGID), ref _showProfilePreview))
        {
            float availWidth = ImGui.GetContentRegionAvail().X;

            if (_bannerTexture != null && _bannerImageBytes.Length > 0)
            {
                float bannerHeight = availWidth * (260f / 840f);
                float bannerRounding = 10f * ImGuiHelpers.GlobalScale;
                var bannerDrawList = ImGui.GetWindowDrawList();
                var bannerMin = ImGui.GetCursorScreenPos();
                var bannerMax = new Vector2(bannerMin.X + availWidth, bannerMin.Y + bannerHeight);
                bannerDrawList.AddImageRounded(
                    _bannerTexture.Handle, bannerMin, bannerMax,
                    Vector2.Zero, Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(Vector4.One), bannerRounding);
                ImGui.Dummy(new Vector2(availWidth, bannerHeight));
                ImGuiHelpers.ScaledDummy(6);
            }

            UiSharedService.DrawCard("preview-hero", () =>
            {
                float imgSize = 80f * ImGuiHelpers.GlobalScale;
                float imgRounding = 10f * ImGuiHelpers.GlobalScale;

                if (_profileTexture != null && _profileImageBytes.Length > 0)
                {
                    var cardDrawList = ImGui.GetWindowDrawList();
                    var imgMin = ImGui.GetCursorScreenPos();
                    var imgMax = new Vector2(imgMin.X + imgSize, imgMin.Y + imgSize);
                    cardDrawList.AddImageRounded(
                        _profileTexture.Handle, imgMin, imgMax,
                        Vector2.Zero, Vector2.One,
                        ImGui.ColorConvertFloat4ToU32(Vector4.One), imgRounding);
                    ImGui.Dummy(new Vector2(imgSize, imgSize));
                    ImGui.SameLine();
                }

                ImGui.BeginGroup();
                using (_uiSharedService.UidFont.Push())
                {
                    UiSharedService.ColorText(GroupFullInfo.GroupAliasOrGID, UiSharedService.AccentColor);
                }
                if (_profileNsfw)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f), "NSFW");
                }
                if (_profileTags.Count > 0)
                {
                    foreach (var tag in _profileTags)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(ImGuiColors.DalamudGrey, tag);
                    }
                }
                ImGui.EndGroup();
            }, stretchWidth: true);

            ImGuiHelpers.ScaledDummy(4);

            if (!string.IsNullOrWhiteSpace(_profileDescription))
            {
                UiSharedService.DrawCard("preview-desc", () =>
                {
                    UiSharedService.ColorText(Loc.Get("SyncshellAdmin.Profile.Description"), UiSharedService.AccentColor);
                    ImGuiHelpers.ScaledDummy(2);
                    float wrapWidth = ImGui.GetContentRegionAvail().X;
                    ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
                    using (_uiSharedService.GameFont.Push())
                    {
                        ImGui.TextUnformatted(_profileDescription);
                    }
                    ImGui.PopTextWrapPos();
                }, stretchWidth: true);
            }
        }
        ImGui.End();
    }

    public override void OnClose()
    {
        _profileTexture?.Dispose();
        _bannerTexture?.Dispose();
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    private void PublishSyncshellPublicNotification()
    {
        try
        {
            var title = string.Format(CultureInfo.CurrentCulture, Loc.Get("SyncshellAdmin.AutoDetect.NotificationTitle"), GroupFullInfo.GroupAliasOrGID);
            var message = Loc.Get("SyncshellAdmin.AutoDetect.NotificationMessage");
            Mediator.Publish(new DualNotificationMessage(title, message, NotificationType.Info, TimeSpan.FromSeconds(4)));
            _notificationTracker.Upsert(NotificationEntry.SyncshellPublic(GroupFullInfo.GID, GroupFullInfo.GroupAliasOrGID));
        }
        catch
        {
            // swallow any notification errors to not break UI flow
        }
    }
}