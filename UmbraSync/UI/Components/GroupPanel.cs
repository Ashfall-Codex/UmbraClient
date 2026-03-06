using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using System.Globalization;
using System.Numerics;
using Dalamud.Interface.Textures.TextureWraps;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.Group;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.AutoDetect;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI.Handlers;
using NotificationType = UmbraSync.MareConfiguration.Models.NotificationType;

namespace UmbraSync.UI.Components;

internal sealed class GroupPanel
{
    private readonly Dictionary<string, bool> _expandedGroupState = new(StringComparer.Ordinal);
    private readonly CompactUi _mainUi;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly CharaDataManager _charaDataManager;
    private readonly AutoDetectRequestService _autoDetectRequestService;
    private readonly MareConfigService _mareConfig;
    private readonly Dictionary<string, bool> _showGidForEntry = new(StringComparer.Ordinal);
    private readonly UidDisplayHandler _uidDisplayHandler;
    private readonly UiSharedService _uiShared;
    private List<BannedGroupUserDto> _bannedUsers = new();
    private int _bulkInviteCount = 10;
    private List<string> _bulkOneTimeInvites = new();
    private string _editGroupComment = string.Empty;
    private string _editGroupEntry = string.Empty;
    private bool _errorGroupCreate = false;
    private string _errorGroupCreateMessage = string.Empty;
    private bool _errorGroupJoin;
    private bool _isPasswordValid;
    private GroupPasswordDto? _lastCreatedGroup = null;
    private bool _modalBanListOpened;
    private bool _modalBulkOneTimeInvitesOpened;
    private bool _modalChangePwOpened;
    private string _newSyncShellPassword = string.Empty;
    private bool _showModalBanList = false;
    private bool _showModalBulkOneTimeInvites = false;
    private bool _showModalChangePassword;
    private bool _showModalCreateGroup;
    private bool _showModalEnterPassword;
    private string _newSyncShellAlias = string.Empty;
    private bool _createIsTemporary = false;
    private int _tempSyncshellDurationHours = 24;
    private readonly int[] _temporaryDurationOptions = new[]
    {
        1,
        12,
        24,
        48,
        72,
        96,
        120,
        144,
        168
    };
    private string _syncShellPassword = string.Empty;
    private string _syncShellToJoin = string.Empty;
    private readonly Dictionary<string, DrawGroupPair> _drawGroupPairCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Pair>> _sortedPairsCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sortedPairsLastUpdate = new(StringComparer.Ordinal);
    private string? _membersWindowGid = null;
    private bool _membersLeaveConfirm = false;
    private string _syncshellFilter = string.Empty;
    private string _membersFilter = string.Empty;
    private bool _membersSortByType = false;
    private readonly UmbraProfileManager _profileManager;
    private readonly SyncshellConfigService _syncshellConfig;
    private readonly Dictionary<string, bool> _favoriteMembersExpanded = new(StringComparer.Ordinal);
    private string? _profileWindowGid = null;
    private bool _profileLoading = false;
    private GroupProfileDto? _currentProfile = null;
    private IDalamudTextureWrap? _profileTexture = null;
    private IDalamudTextureWrap? _bannerTexture = null;

    public GroupPanel(CompactUi mainUi, UiSharedService uiShared, PairManager pairManager,
        UidDisplayHandler uidDisplayHandler, ServerConfigurationManager serverConfigurationManager,
        CharaDataManager charaDataManager, AutoDetectRequestService autoDetectRequestService,
        MareConfigService mareConfig, UmbraProfileManager profileManager, SyncshellConfigService syncshellConfig)
    {
        _mainUi = mainUi;
        _uiShared = uiShared;
        _pairManager = pairManager;
        _uidDisplayHandler = uidDisplayHandler;
        _serverConfigurationManager = serverConfigurationManager;
        _charaDataManager = charaDataManager;
        _autoDetectRequestService = autoDetectRequestService;
        _mareConfig = mareConfig;
        _profileManager = profileManager;
        _syncshellConfig = syncshellConfig;
    }

    private ApiController ApiController => _uiShared.ApiController;

    public void ClearCache()
    {
        _drawGroupPairCache.Clear();
        _sortedPairsCache.Clear();
        _sortedPairsLastUpdate.Clear();
    }

    public void DrawSyncshells(Action? drawAfterAdd = null)
    {
        using var fontScale = UiSharedService.PushFontScale(UiSharedService.ContentFontScale);
        using (ImRaii.PushId("addsyncshell")) DrawAddSyncshell();
        drawAfterAdd?.Invoke();
        using (ImRaii.PushId("syncshelllist")) DrawSyncshellList();
        _mainUi.TransferPartHeight = ImGui.GetCursorPosY();
    }

    private void DrawAddSyncshell()
    {
        ImGuiHelpers.ScaledDummy(2f);
        var joinModalTitle = Loc.Get("Syncshell.Join.ModalTitle");
        var createModalTitle = Loc.Get("Syncshell.Create.ModalTitle");
        bool userCanJoinMoreGroups = _pairManager.GroupPairs.Count < ApiController.ServerInfo.MaxGroupsJoinedByUser;
        bool userCanCreateMoreGroups = _pairManager.GroupPairs.Count(u => string.Equals(u.Key.Owner.UID, ApiController.UID, StringComparison.Ordinal)) < ApiController.ServerInfo.MaxGroupsCreatedByUser;

        var availWidth = ImGui.GetContentRegionAvail().X;
        var style = ImGui.GetStyle();
        var halfWidth = (availWidth - style.ItemSpacing.X) / 2f;

        if (!userCanCreateMoreGroups) ImGui.BeginDisabled();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("Syncshell.Button.Create"), halfWidth))
        {
            _lastCreatedGroup = null;
            _errorGroupCreate = false;
            _newSyncShellAlias = string.Empty;
            _createIsTemporary = false;
            _tempSyncshellDurationHours = 24;
            _errorGroupCreateMessage = string.Empty;
            _showModalCreateGroup = true;
            ImGui.OpenPopup(createModalTitle);
        }
        if (!userCanCreateMoreGroups)
        {
            ImGui.EndDisabled();
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Tooltip.CreateDenied"), ApiController.ServerInfo.MaxGroupsCreatedByUser));
        }
        else
        {
            UiSharedService.AttachToolTip(Loc.Get("Syncshell.Tooltip.CreateAllowed"));
        }

        ImGui.SameLine();

        if (!userCanJoinMoreGroups) ImGui.BeginDisabled();
        if (_uiShared.IconTextButton(FontAwesomeIcon.SignInAlt, Loc.Get("Syncshell.Button.Join"), halfWidth))
        {
            _syncShellToJoin = string.Empty;
            _syncShellPassword = string.Empty;
            _errorGroupJoin = false;
            _showModalEnterPassword = true;
            ImGui.OpenPopup(joinModalTitle);
        }
        if (!userCanJoinMoreGroups)
        {
            ImGui.EndDisabled();
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Tooltip.JoinDenied"), ApiController.ServerInfo.MaxGroupsJoinedByUser));
        }
        else
        {
            UiSharedService.AttachToolTip(Loc.Get("Syncshell.Tooltip.JoinAllowed"));
        }

        if (ImGui.BeginPopupModal(joinModalTitle, ref _showModalEnterPassword, UiSharedService.PopupWindowFlags))
        {
            UiSharedService.TextWrapped(Loc.Get("Syncshell.Join.Warning"));
            ImGui.Separator();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##syncshellid", Loc.Get("Syncshell.Join.GidPlaceholder"), ref _syncShellToJoin, 50);
            var trimmedInput = _syncShellToJoin.Trim();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##password", Loc.Get("Syncshell.Join.PasswordPlaceholder"), ref _syncShellPassword, 255, ImGuiInputTextFlags.Password);
            if (_errorGroupJoin)
            {
                UiSharedService.ColorTextWrapped(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Loc.Get("Syncshell.Join.Error"),
                        ApiController.ServerInfo.MaxGroupsJoinedByUser,
                        ApiController.ServerInfo.MaxGroupUserCount),
                    new Vector4(1, 0, 0, 1));
            }
            bool canJoin = !string.IsNullOrWhiteSpace(trimmedInput);
            if (!canJoin) ImGui.BeginDisabled();
            if (ImGui.Button(Loc.Get("Syncshell.Button.Join"), new Vector2(-1, 0)))
            {
                var shell = trimmedInput;
                var pw = _syncShellPassword;
                _errorGroupJoin = !ApiController.GroupJoin(new(new GroupData(shell), pw)).Result;
                if (!_errorGroupJoin)
                {
                    _syncShellToJoin = string.Empty;
                    _showModalEnterPassword = false;
                }
                _syncShellPassword = string.Empty;
            }
            if (!canJoin) ImGui.EndDisabled();
            UiSharedService.SetScaledWindowSize(330);
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal(createModalTitle, ref _showModalCreateGroup, UiSharedService.PopupWindowFlags))
        {
            UiSharedService.TextWrapped(Loc.Get("Syncshell.Create.TypePrompt"));
            bool showPermanent = !_createIsTemporary;
            if (ImGui.RadioButton(Loc.Get("Syncshell.Create.TypePermanent"), showPermanent))
            {
                _createIsTemporary = false;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton(Loc.Get("Syncshell.Create.TypeTemporary"), _createIsTemporary))
            {
                _createIsTemporary = true;
                _newSyncShellAlias = string.Empty;
            }

            if (!_createIsTemporary)
            {
                UiSharedService.TextWrapped(Loc.Get("Syncshell.Create.AliasPrompt"));
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##syncshellalias", Loc.Get("Syncshell.Create.AliasPlaceholder"), ref _newSyncShellAlias, 50);
            }
            else
            {
                _newSyncShellAlias = string.Empty;
            }

            if (_createIsTemporary)
            {
                UiSharedService.TextWrapped(Loc.Get("Syncshell.Create.TempMaxDuration"));
                if (_tempSyncshellDurationHours > 168) _tempSyncshellDurationHours = 168;
                for (int i = 0; i < _temporaryDurationOptions.Length; i++)
                {
                    var option = _temporaryDurationOptions[i];
                    var isSelected = _tempSyncshellDurationHours == option;
                    string label = option switch
                    {
                        >= 24 when option % 24 == 0 => option == 24 ? Loc.Get("Syncshell.Create.Duration24h") : string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Create.DurationDays"), option / 24),
                        _ => string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Create.DurationHours"), option)
                    };

                    if (ImGui.RadioButton(label, isSelected))
                    {
                        _tempSyncshellDurationHours = option;
                    }

                    // Start a new line after every 3 buttons
                    if ((i + 1) % 3 == 0)
                    {
                        ImGui.NewLine();
                    }
                    else
                    {
                        ImGui.SameLine();
                    }
                }

                var expiresLocal = DateTime.Now.AddHours(_tempSyncshellDurationHours);
                UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Create.ExpiresAt"), expiresLocal.ToString("g", CultureInfo.CurrentCulture)));
            }

            UiSharedService.TextWrapped(Loc.Get("Syncshell.Create.ButtonPrompt"));
            var createButtonHeight = ImGui.GetFrameHeight() * 1.1f;
            var createLabel = Loc.Get("Syncshell.Create.ButtonLabel");
            var createButtonWidth = ImGui.CalcTextSize(createLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
            var cursorX = ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - createButtonWidth) * 0.5f;
            if (cursorX > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(cursorX);
            if (ImGui.Button(createLabel, new Vector2(createButtonWidth, createButtonHeight)))
            {
                try
                {
                    if (_createIsTemporary)
                    {
                        var expiresAtUtc = DateTime.UtcNow.AddHours(_tempSyncshellDurationHours);
                        _lastCreatedGroup = ApiController.GroupCreateTemporary(expiresAtUtc).Result;
                    }
                    else
                    {
                        var aliasInput = string.IsNullOrWhiteSpace(_newSyncShellAlias) ? null : _newSyncShellAlias.Trim();
                        _lastCreatedGroup = ApiController.GroupCreate(aliasInput).Result;
                        if (_lastCreatedGroup != null)
                        {
                            _newSyncShellAlias = string.Empty;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _lastCreatedGroup = null;
                    _errorGroupCreate = true;
                    if (ex.Message.Contains("name is already in use", StringComparison.OrdinalIgnoreCase))
                    {
                        _errorGroupCreateMessage = Loc.Get("Syncshell.Create.NameInUse");
                    }
                    else
                    {
                        _errorGroupCreateMessage = ex.Message;
                    }
                }
            }

            if (_lastCreatedGroup != null)
            {
                ImGui.Separator();
                _errorGroupCreate = false;
                _errorGroupCreateMessage = string.Empty;
                if (!string.IsNullOrWhiteSpace(_lastCreatedGroup.Group.Alias))
                {
                    ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Create.Result.Name"), _lastCreatedGroup.Group.Alias));
                }
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Create.Result.Id"), _lastCreatedGroup.Group.GID));
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Create.Result.Password"), _lastCreatedGroup.Password));
                ImGui.SameLine();
                if (_uiShared.IconButton(FontAwesomeIcon.Copy))
                {
                    ImGui.SetClipboardText(_lastCreatedGroup.Password);
                }
                UiSharedService.TextWrapped(Loc.Get("Syncshell.Create.Result.PasswordNote"));
                if (_lastCreatedGroup.IsTemporary && _lastCreatedGroup.ExpiresAt != null)
                {
                    var expiresLocal = _lastCreatedGroup.ExpiresAt.Value.ToLocalTime();
                    UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Create.TempExpires"), expiresLocal.ToString("g", CultureInfo.CurrentCulture)));
                }
            }

            if (_errorGroupCreate)
            {
                var msg = string.IsNullOrWhiteSpace(_errorGroupCreateMessage)
                    ? Loc.Get("Syncshell.Create.Error.General")
                    : _errorGroupCreateMessage;
                UiSharedService.ColorTextWrapped(msg, new Vector4(1, 0, 0, 1));
            }

            UiSharedService.SetScaledWindowSize(350);
            ImGui.EndPopup();
        }

        ImGuiHelpers.ScaledDummy(2);
    }

    private void DrawSyncshell(GroupFullInfoDto groupDto, List<Pair> pairsInGroup)
    {
        var name = groupDto.Group.Alias ?? groupDto.GID;
        if (!_expandedGroupState.ContainsKey(groupDto.GID))
        {
            _expandedGroupState[groupDto.GID] = false;
        }

        var style = ImGui.GetStyle();
        var compactPadding = new Vector2(
            style.FramePadding.X + 4f * ImGuiHelpers.GlobalScale,
            // small nudge accounts for card border thickness vs. list rows
            style.FramePadding.Y + 0.5f * ImGuiHelpers.GlobalScale);
        var standardPadding = new Vector2(
            style.FramePadding.X + 4f * ImGuiHelpers.GlobalScale,
            style.FramePadding.Y + 3f * ImGuiHelpers.GlobalScale);
        bool isExpanded = _expandedGroupState[groupDto.GID];
        var cardPadding = isExpanded ? compactPadding : standardPadding;

        UiSharedService.DrawCard($"syncshell-card-{groupDto.GID}", () =>
        {
            // Ensure text/icon baseline alignment with frame padding like list rows
            ImGui.AlignTextToFramePadding();
            float lineStartY = ImGui.GetCursorPosY();
            bool expandedState = _expandedGroupState[groupDto.GID];
            UiSharedService.DrawArrowToggle(ref expandedState, $"##syncshell-toggle-{groupDto.GID}");
            if (expandedState != _expandedGroupState[groupDto.GID])
            {
                _expandedGroupState[groupDto.GID] = expandedState;
            }
            ImGui.SameLine(0f, 6f * ImGuiHelpers.GlobalScale);

            var textIsGid = true;
            string groupName = groupDto.GroupAliasOrGID;

            if (string.Equals(groupDto.OwnerUID, ApiController.UID, StringComparison.Ordinal))
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.TextUnformatted(FontAwesomeIcon.Crown.ToIconString());
                ImGui.PopFont();
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Card.Owner"), groupName));
                ImGui.SameLine(0f, ImGui.GetStyle().ItemSpacing.X * 1.2f);
            }
            else if (groupDto.GroupUserInfo.IsModerator())
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.TextUnformatted(FontAwesomeIcon.UserShield.ToIconString());
                ImGui.PopFont();
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Card.Moderator"), groupName));
                ImGui.SameLine(0f, ImGui.GetStyle().ItemSpacing.X * 1.2f);
            }

            _showGidForEntry.TryGetValue(groupDto.GID, out var showGidInsteadOfName);
            var groupComment = _serverConfigurationManager.GetNoteForGid(groupDto.GID);
            if (!showGidInsteadOfName && !string.IsNullOrEmpty(groupComment))
            {
                groupName = groupComment;
                textIsGid = false;
            }

            if (!string.Equals(_editGroupEntry, groupDto.GID, StringComparison.Ordinal))
            {
                var totalMembers = pairsInGroup.Count + 1;
                var connectedMembers = pairsInGroup.Count(p => p.IsOnline) + 1;
                var maxCapacity = ApiController.ServerInfo.MaxGroupUserCount;
                ImGui.TextUnformatted($"{connectedMembers}/{totalMembers}");
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Card.ConnectedTooltip"),
                    connectedMembers, totalMembers, maxCapacity, groupDto.Group.GID));
                if (textIsGid) ImGui.PushFont(UiBuilder.MonoFont);
                ImGui.SameLine();
                ImGui.TextUnformatted(groupName);
                if (textIsGid) ImGui.PopFont();
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Card.SwitchTooltip"), groupName, pairsInGroup.Count + 1, groupDto.OwnerAliasOrUID));
                if (groupDto.IsTemporary)
                {
                    ImGui.SameLine();
                    UiSharedService.ColorText(Loc.Get("Syncshell.Card.TempLabel"), ImGuiColors.DalamudOrange);
                    if (groupDto.ExpiresAt != null)
                    {
                        var tempExpireLocal = groupDto.ExpiresAt.Value.ToLocalTime();
                        UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Card.TempExpire"), tempExpireLocal.ToString("g", CultureInfo.CurrentCulture)));
                    }
                    else
                    {
                        UiSharedService.AttachToolTip(Loc.Get("Syncshell.Card.TempTooltip"));
                    }
                }
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    var prevState = textIsGid;
                    if (_showGidForEntry.ContainsKey(groupDto.GID))
                    {
                        prevState = _showGidForEntry[groupDto.GID];
                    }

                    _showGidForEntry[groupDto.GID] = !prevState;
                }

                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    _serverConfigurationManager.SetNoteForGid(_editGroupEntry, _editGroupComment);
                    _editGroupComment = _serverConfigurationManager.GetNoteForGid(groupDto.GID) ?? string.Empty;
                    _editGroupEntry = groupDto.GID;
                }
            }
            else
            {
                var buttonSizes = _uiShared.GetIconButtonSize(FontAwesomeIcon.Bars).X + _uiShared.GetIconButtonSize(FontAwesomeIcon.LockOpen).X;
                ImGui.SetNextItemWidth(UiSharedService.GetWindowContentRegionWidth() - ImGui.GetCursorPosX() - buttonSizes - ImGui.GetStyle().ItemSpacing.X * 2);
                if (ImGui.InputTextWithHint("", Loc.Get("Syncshell.Card.CommentPlaceholder"), ref _editGroupComment, 255, ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    _serverConfigurationManager.SetNoteForGid(groupDto.GID, _editGroupComment);
                    _editGroupEntry = string.Empty;
                }

                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    _editGroupEntry = string.Empty;
                }
                UiSharedService.AttachToolTip(Loc.Get("Syncshell.Card.CommentTooltip"));
            }


            using (ImRaii.PushId(groupDto.GID + "settings")) DrawSyncShellButtons(groupDto, pairsInGroup, lineStartY);

            if (_showModalBanList && !_modalBanListOpened)
            {
                _modalBanListOpened = true;
                ImGui.OpenPopup(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Banlist.ModalTitle"), groupDto.GID));
            }

            if (!_showModalBanList) _modalBanListOpened = false;

            if (ImGui.BeginPopupModal(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Banlist.ModalTitle"), groupDto.GID), ref _showModalBanList, UiSharedService.PopupWindowFlags))
            {
                if (_uiShared.IconTextButton(FontAwesomeIcon.Retweet, Loc.Get("Syncshell.Banlist.Refresh")))
                {
                    _bannedUsers = ApiController.GroupGetBannedUsers(groupDto).Result;
                }

                if (ImGui.BeginTable("bannedusertable" + groupDto.GID, 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
                {
                    ImGui.TableSetupColumn(Loc.Get("Syncshell.Banlist.Column.Uid"), ImGuiTableColumnFlags.None, 1);
                    ImGui.TableSetupColumn(Loc.Get("Syncshell.Banlist.Column.Alias"), ImGuiTableColumnFlags.None, 1);
                    ImGui.TableSetupColumn(Loc.Get("Syncshell.Banlist.Column.By"), ImGuiTableColumnFlags.None, 1);
                    ImGui.TableSetupColumn(Loc.Get("Syncshell.Banlist.Column.Date"), ImGuiTableColumnFlags.None, 2);
                    ImGui.TableSetupColumn(Loc.Get("Syncshell.Banlist.Column.Reason"), ImGuiTableColumnFlags.None, 3);
                    ImGui.TableSetupColumn(Loc.Get("Syncshell.Banlist.Column.Actions"), ImGuiTableColumnFlags.None, 1);

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
                        if (_uiShared.IconTextButton(FontAwesomeIcon.Check, string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Banlist.Unban"), bannedUser.UID)))
                        {
                            _ = ApiController.GroupUnbanUser(bannedUser);
                            _bannedUsers.RemoveAll(b => string.Equals(b.UID, bannedUser.UID, StringComparison.Ordinal));
                        }
                    }

                    ImGui.EndTable();
                }
                UiSharedService.SetScaledWindowSize(700, 300);
                ImGui.EndPopup();
            }

            if (_showModalChangePassword && !_modalChangePwOpened)
            {
                _modalChangePwOpened = true;
                ImGui.OpenPopup("Change Syncshell Password");
            }

            if (!_showModalChangePassword) _modalChangePwOpened = false;

            if (ImGui.BeginPopupModal("Change Syncshell Password", ref _showModalChangePassword, UiSharedService.PopupWindowFlags))
            {
                UiSharedService.TextWrapped("Enter the new Syncshell password for Syncshell " + name + " here.");
                UiSharedService.TextWrapped("This action is irreversible");
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##changepw", "New password for " + name, ref _newSyncShellPassword, 255);
                if (ImGui.Button("Change password"))
                {
                    var pw = _newSyncShellPassword;
                    _isPasswordValid = ApiController.GroupChangePassword(new(groupDto.Group, pw)).Result;
                    _newSyncShellPassword = string.Empty;
                    if (_isPasswordValid) _showModalChangePassword = false;
                }

                if (!_isPasswordValid)
                {
                    UiSharedService.ColorTextWrapped("The selected password is too short. It must be at least 10 characters.", new Vector4(1, 0, 0, 1));
                }

                UiSharedService.SetScaledWindowSize(290);
                ImGui.EndPopup();
            }

            if (_showModalBulkOneTimeInvites && !_modalBulkOneTimeInvitesOpened)
            {
                _modalBulkOneTimeInvitesOpened = true;
                ImGui.OpenPopup("Create Bulk One-Time Invites");
            }

            if (!_showModalBulkOneTimeInvites) _modalBulkOneTimeInvitesOpened = false;

            if (ImGui.BeginPopupModal("Create Bulk One-Time Invites", ref _showModalBulkOneTimeInvites, UiSharedService.PopupWindowFlags))
            {
                UiSharedService.TextWrapped("This allows you to create up to 100 one-time invites at once for the Syncshell " + name + "." + Environment.NewLine
                    + "The invites are valid for 24h after creation and will automatically expire.");
                ImGui.Separator();
                if (_bulkOneTimeInvites.Count == 0)
                {
                    ImGui.SetNextItemWidth(-1);
                    ImGui.SliderInt("Amount##bulkinvites", ref _bulkInviteCount, 1, 100);
                    if (_uiShared.IconTextButton(FontAwesomeIcon.MailBulk, "Create invites"))
                    {
                        _bulkOneTimeInvites = ApiController.GroupCreateTempInvite(groupDto, _bulkInviteCount).Result;
                    }
                }
                else
                {
                    UiSharedService.TextWrapped("A total of " + _bulkOneTimeInvites.Count + " invites have been created.");
                    if (_uiShared.IconTextButton(FontAwesomeIcon.Copy, "Copy invites to clipboard"))
                    {
                        ImGui.SetClipboardText(string.Join(Environment.NewLine, _bulkOneTimeInvites));
                    }
                }

                UiSharedService.SetScaledWindowSize(290);
                ImGui.EndPopup();
            }

            bool hideOfflineUsers = pairsInGroup.Count > 1000;

            ImGui.Indent(20);
            if (expandedState)
            {
                if (!_sortedPairsCache.TryGetValue(groupDto.GID, out var sortedPairs) ||
                    Environment.TickCount64 - _sortedPairsLastUpdate[groupDto.GID] > 1000)
                {
                    sortedPairs = pairsInGroup
                        .OrderByDescending(u => string.Equals(u.UserData.UID, groupDto.OwnerUID, StringComparison.Ordinal))
                        .ThenByDescending(u => u.GroupPair[groupDto].GroupPairStatusInfo.IsModerator())
                        .ThenByDescending(u => u.GroupPair[groupDto].GroupPairStatusInfo.IsPinned())
                        .ThenBy(u => u.GetPairSortKey(), StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    _sortedPairsCache[groupDto.GID] = sortedPairs;
                    _sortedPairsLastUpdate[groupDto.GID] = Environment.TickCount64;
                }

                var visibleUsers = new List<DrawGroupPair>();
                var onlineUsers = new List<DrawGroupPair>();
                var offlineUsers = new List<DrawGroupPair>();

                foreach (var pair in sortedPairs)
                {
                    var cacheKey = groupDto.GID + pair.UserData.UID;
                    var groupPairFullInfoDto = pair.GroupPair.FirstOrDefault(
                            g => string.Equals(g.Key.Group.GID, groupDto.GID, StringComparison.Ordinal)
                        ).Value;

                    if (groupPairFullInfoDto == null) continue;

                    if (!_drawGroupPairCache.TryGetValue(cacheKey, out var drawPair))
                    {
                        drawPair = new DrawGroupPair(
                            cacheKey, pair,
                            ApiController, _mainUi.Mediator, groupDto,
                            groupPairFullInfoDto,
                            _uidDisplayHandler,
                            _uiShared,
                            _charaDataManager,
                            _autoDetectRequestService,
                            _serverConfigurationManager,
                            _mareConfig);
                        _drawGroupPairCache[cacheKey] = drawPair;
                    }
                    else
                    {
                        drawPair.UpdateData(groupDto, groupPairFullInfoDto);
                    }

                    if (pair.IsVisible)
                        visibleUsers.Add(drawPair);
                    else if (pair.IsOnline)
                        onlineUsers.Add(drawPair);
                    else
                        offlineUsers.Add(drawPair);
                }

                if (visibleUsers.Count > 0)
                {
                    ImGui.TextUnformatted("Visible");
                    UidDisplayHandler.RenderPairList(visibleUsers);
                }

                if (onlineUsers.Count > 0)
                {
                    ImGui.TextUnformatted("Online");
                    UidDisplayHandler.RenderPairList(onlineUsers);
                }

                if (offlineUsers.Count > 0)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                    ImGui.TextUnformatted("Offline/Unknown");
                    ImGui.PopStyleColor();
                    if (hideOfflineUsers)
                    {
                        UiSharedService.ColorText($"    {offlineUsers.Count} offline users omitted from display.", ImGuiColors.DalamudGrey);
                    }
                    else
                    {
                        UidDisplayHandler.RenderPairList(offlineUsers);
                    }
                }
            }
            ImGui.Unindent(20);
        }, padding: cardPadding, stretchWidth: true);

        ImGuiHelpers.ScaledDummy(style.ItemSpacing.Y);
    }

    private void DrawSyncShellButtons(GroupFullInfoDto groupDto, List<Pair> groupPairs, float lineStartY)
    {
        var infoIcon = FontAwesomeIcon.InfoCircle;

        bool invitesEnabled = !groupDto.GroupPermissions.IsDisableInvites();
        var soundsDisabled = groupDto.GroupPermissions.IsDisableSounds();
        var animDisabled = groupDto.GroupPermissions.IsDisableAnimations();
        var vfxDisabled = groupDto.GroupPermissions.IsDisableVFX();

        var userSoundsDisabled = groupDto.GroupUserPermissions.IsDisableSounds();
        var userAnimDisabled = groupDto.GroupUserPermissions.IsDisableAnimations();
        var userVFXDisabled = groupDto.GroupUserPermissions.IsDisableVFX();

        bool showInfoIcon = !invitesEnabled || soundsDisabled || animDisabled || vfxDisabled || userSoundsDisabled || userAnimDisabled || userVFXDisabled;

        var lockedIcon = invitesEnabled ? FontAwesomeIcon.LockOpen : FontAwesomeIcon.Lock;
        var animIcon = animDisabled ? FontAwesomeIcon.WindowClose : FontAwesomeIcon.Running;
        var soundsIcon = soundsDisabled ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp;
        var vfxIcon = vfxDisabled ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.Sun;
        var userAnimIcon = userAnimDisabled ? FontAwesomeIcon.WindowClose : FontAwesomeIcon.Running;
        var userSoundsIcon = userSoundsDisabled ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp;
        var userVFXIcon = userVFXDisabled ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.Sun;

        var iconSize = UiSharedService.GetIconSize(infoIcon);
        var barbuttonSize = _uiShared.GetIconButtonSize(FontAwesomeIcon.Bars);
        var isOwner = string.Equals(groupDto.OwnerUID, ApiController.UID, StringComparison.Ordinal);

        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var cardPaddingX = UiSharedService.GetCardContentPaddingX();
        var windowEndX = ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth() - cardPaddingX - 6f * ImGuiHelpers.GlobalScale;
        var pauseIcon = groupDto.GroupUserPermissions.IsPaused() ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var pauseIconSize = _uiShared.GetIconButtonSize(pauseIcon);
        float buttonLineY = lineStartY - 1f * ImGuiHelpers.GlobalScale;
        ImGui.SameLine(windowEndX - barbuttonSize.X - (showInfoIcon ? iconSize.X : 0) - (showInfoIcon ? spacingX : 0) - pauseIconSize.X - spacingX);

        if (showInfoIcon)
        {
            ImGui.SetCursorPosY(buttonLineY);
            _uiShared.IconText(infoIcon);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                if (!invitesEnabled || soundsDisabled || animDisabled || vfxDisabled)
                {
                    ImGui.TextUnformatted("Syncshell permissions");

                    if (!invitesEnabled)
                    {
                        var lockedText = "Syncshell is closed for joining";
                        _uiShared.IconText(lockedIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(lockedText);
                    }

                    if (soundsDisabled)
                    {
                        var soundsText = "Sound sync disabled through owner";
                        _uiShared.IconText(soundsIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(soundsText);
                    }

                    if (animDisabled)
                    {
                        var animText = "Animation sync disabled through owner";
                        _uiShared.IconText(animIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(animText);
                    }

                    if (vfxDisabled)
                    {
                        var vfxText = "VFX sync disabled through owner";
                        _uiShared.IconText(vfxIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(vfxText);
                    }
                }

                if (userSoundsDisabled || userAnimDisabled || userVFXDisabled)
                {
                    if (!invitesEnabled || soundsDisabled || animDisabled || vfxDisabled)
                        ImGui.Separator();

                    ImGui.TextUnformatted("Your permissions");

                    if (userSoundsDisabled)
                    {
                        var userSoundsText = "Sound sync disabled through you";
                        _uiShared.IconText(userSoundsIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userSoundsText);
                    }

                    if (userAnimDisabled)
                    {
                        var userAnimText = "Animation sync disabled through you";
                        _uiShared.IconText(userAnimIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userAnimText);
                    }

                    if (userVFXDisabled)
                    {
                        var userVFXText = "VFX sync disabled through you";
                        _uiShared.IconText(userVFXIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userVFXText);
                    }

                    if (!invitesEnabled || soundsDisabled || animDisabled || vfxDisabled)
                        UiSharedService.TextWrapped("Note that syncshell permissions for disabling take precedence over your own set permissions");
                }
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
        }

        ImGui.SetCursorPosY(buttonLineY);
        bool clickedPause = pauseIcon == FontAwesomeIcon.Pause
            ? _uiShared.IconPauseButtonCentered(pauseIconSize.Y)
            : _uiShared.IconButtonCentered(pauseIcon, pauseIconSize.Y);
        if (clickedPause)
        {
            var userPerm = groupDto.GroupUserPermissions ^ GroupUserPermissions.Paused;
            _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(groupDto.Group, new UserData(ApiController.UID), userPerm));
        }
        UiSharedService.AttachToolTip((groupDto.GroupUserPermissions.IsPaused() ? "Resume" : "Pause") + " pairing with all users in this Syncshell");
        ImGui.SameLine();

        ImGui.SetCursorPosY(buttonLineY);
        if (_uiShared.IconButton(FontAwesomeIcon.Bars))
        {
            ImGui.OpenPopup("ShellPopup");
        }

        if (ImGui.BeginPopup("ShellPopup"))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowCircleLeft, "Leave Syncshell") && UiSharedService.CtrlPressed())
            {
                _ = ApiController.GroupLeave(groupDto);
            }
            UiSharedService.AttachToolTip("Hold CTRL and click to leave this Syncshell" + (!string.Equals(groupDto.OwnerUID, ApiController.UID, StringComparison.Ordinal) ? string.Empty : Environment.NewLine
                + "WARNING: This action is irreversible" + Environment.NewLine + "Leaving an owned Syncshell will transfer the ownership to a random person in the Syncshell."));

            if (_uiShared.IconTextButton(FontAwesomeIcon.Copy, "Copy ID"))
            {
                ImGui.CloseCurrentPopup();
                ImGui.SetClipboardText(groupDto.GroupAliasOrGID);
            }
            UiSharedService.AttachToolTip("Copy Syncshell ID to Clipboard");

            if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, "Copy Notes"))
            {
                ImGui.CloseCurrentPopup();
                ImGui.SetClipboardText(UiSharedService.GetNotes(groupPairs));
            }
            UiSharedService.AttachToolTip("Copies all your notes for all users in this Syncshell to the clipboard." + Environment.NewLine + "They can be imported via Settings -> General -> Notes -> Import notes from clipboard");

            var soundsText = userSoundsDisabled ? "Enable sound sync" : "Disable sound sync";
            if (_uiShared.IconTextButton(userSoundsIcon, soundsText))
            {
                ImGui.CloseCurrentPopup();
                var perm = groupDto.GroupUserPermissions;
                perm.SetDisableSounds(!perm.IsDisableSounds());
                _mainUi.Mediator.Publish(new GroupSyncOverrideChanged(groupDto.Group.GID, perm.IsDisableSounds(), null, null));
                _ = ApiController.GroupChangeIndividualPermissionState(new(groupDto.Group, new UserData(ApiController.UID), perm));
            }
            UiSharedService.AttachToolTip("Sets your allowance for sound synchronization for users of this syncshell."
                + Environment.NewLine + "Disabling the synchronization will stop applying sound modifications for users of this syncshell."
                + Environment.NewLine + "Note: this setting can be forcefully overridden to 'disabled' through the syncshell owner."
                + Environment.NewLine + "Note: this setting does not apply to individual pairs that are also in the syncshell.");

            var animText = userAnimDisabled ? "Enable animations sync" : "Disable animations sync";
            if (_uiShared.IconTextButton(userAnimIcon, animText))
            {
                ImGui.CloseCurrentPopup();
                var perm = groupDto.GroupUserPermissions;
                perm.SetDisableAnimations(!perm.IsDisableAnimations());
                _mainUi.Mediator.Publish(new GroupSyncOverrideChanged(groupDto.Group.GID, null, perm.IsDisableAnimations(), null));
                _ = ApiController.GroupChangeIndividualPermissionState(new(groupDto.Group, new UserData(ApiController.UID), perm));
            }
            UiSharedService.AttachToolTip("Sets your allowance for animations synchronization for users of this syncshell."
                + Environment.NewLine + "Disabling the synchronization will stop applying animations modifications for users of this syncshell."
                + Environment.NewLine + "Note: this setting might also affect sound synchronization"
                + Environment.NewLine + "Note: this setting can be forcefully overridden to 'disabled' through the syncshell owner."
                + Environment.NewLine + "Note: this setting does not apply to individual pairs that are also in the syncshell.");

            var vfxText = userVFXDisabled ? "Enable VFX sync" : "Disable VFX sync";
            if (_uiShared.IconTextButton(userVFXIcon, vfxText))
            {
                ImGui.CloseCurrentPopup();
                var perm = groupDto.GroupUserPermissions;
                perm.SetDisableVFX(!perm.IsDisableVFX());
                _mainUi.Mediator.Publish(new GroupSyncOverrideChanged(groupDto.Group.GID, null, null, perm.IsDisableVFX()));
                _ = ApiController.GroupChangeIndividualPermissionState(new(groupDto.Group, new UserData(ApiController.UID), perm));
            }
            UiSharedService.AttachToolTip("Sets your allowance for VFX synchronization for users of this syncshell."
                                          + Environment.NewLine + "Disabling the synchronization will stop applying VFX modifications for users of this syncshell."
                                          + Environment.NewLine + "Note: this setting might also affect animation synchronization to some degree"
                                          + Environment.NewLine + "Note: this setting can be forcefully overridden to 'disabled' through the syncshell owner."
                                          + Environment.NewLine + "Note: this setting does not apply to individual pairs that are also in the syncshell.");

            // Combo son de notification ciblée pour ce syncshell
            {
                var gid = groupDto.Group.GID;
                var overrides = _mareConfig.Current.GroupTargetSoundOverrides;
                overrides.TryGetValue(gid, out var currentValue);
                var hasOverride = overrides.ContainsKey(gid);

                var previewLabel = !hasOverride
                    ? Loc.Get("Settings.ChatTargetSound.Override.Default")
                    : currentValue == 0
                        ? Loc.Get("Settings.ChatTargetSound.Override.Disabled")
                        : string.Format(CultureInfo.CurrentCulture, Loc.Get("Settings.ChatTargetSound.SoundItem"), currentValue);

                ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
                if (ImGui.BeginCombo(Loc.Get("Settings.ChatTargetSound.Override.Label") + "##shell_sound_" + gid, previewLabel))
                {
                    if (ImGui.Selectable(Loc.Get("Settings.ChatTargetSound.Override.Default"), !hasOverride))
                    {
                        overrides.Remove(gid);
                        _mareConfig.Save();
                    }
                    if (ImGui.Selectable(Loc.Get("Settings.ChatTargetSound.Override.Disabled"), hasOverride && currentValue == 0))
                    {
                        overrides[gid] = 0;
                        _mareConfig.Save();
                    }
                    for (var i = 1; i <= 16; i++)
                    {
                        var label = string.Format(CultureInfo.CurrentCulture, Loc.Get("Settings.ChatTargetSound.SoundItem"), i);
                        if (ImGui.Selectable(label, hasOverride && currentValue == i))
                        {
                            overrides[gid] = i;
                            _mareConfig.Save();
                        }
                    }
                    ImGui.EndCombo();
                }
            }

            if (isOwner || groupDto.GroupUserInfo.IsModerator())
            {
                ImGui.Separator();
                if (_uiShared.IconTextButton(FontAwesomeIcon.Cog, "Open Admin Panel"))
                {
                    ImGui.CloseCurrentPopup();
                    _mainUi.Mediator.Publish(new OpenSyncshellAdminPanel(groupDto));
                }
            }

            ImGui.EndPopup();
        }
    }

    private void DrawSyncshellList()
    {
        float availableHeight = ImGui.GetContentRegionAvail().Y;
        float ySize;
        if (_mainUi.TransferPartHeight <= 0)
        {
            float reserve = ImGui.GetFrameHeightWithSpacing() * 2f;
            ySize = availableHeight - reserve;
            if (ySize <= 0)
            {
                ySize = System.Math.Max(availableHeight, 1f);
            }
        }
        else
        {
            ySize = (ImGui.GetWindowContentRegionMax().Y - ImGui.GetWindowContentRegionMin().Y) - _mainUi.TransferPartHeight - ImGui.GetCursorPosY();
        }

        ImGui.SetNextItemWidth(_mainUi.WindowContentWidth);
        ImGui.InputTextWithHint("##syncshellfilter", Loc.Get("Syncshell.Filter.Placeholder"), ref _syncshellFilter, 255);
        ImGuiHelpers.ScaledDummy(4f);

        ImGui.BeginChild("list", new Vector2(_mainUi.WindowContentWidth, ySize), border: false);

        DrawSyncshellCards();
        
        ImGui.EndChild();
    }

    private void DrawSyncshellCards()
    {
        var favorites = _syncshellConfig.Current.FavoriteSyncshells;
        var groups = _pairManager.GroupPairs
            .OrderByDescending(g => favorites.Contains(g.Key.GID))
            .ThenBy(g => g.Key.Group.AliasOrGID, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrEmpty(_syncshellFilter))
        {
            groups = groups.Where(g => g.Value.Any(p =>
                p.UserData.AliasOrUID.Contains(_syncshellFilter, StringComparison.OrdinalIgnoreCase) ||
                (p.GetNote()?.Contains(_syncshellFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.PlayerName?.Contains(_syncshellFilter, StringComparison.OrdinalIgnoreCase) ?? false)
            )).ToList();
        }

        if (groups.Count == 0) return;

        float availableWidth = ImGui.GetContentRegionAvail().X;
        float cardSpacing = 8f * ImGuiHelpers.GlobalScale;
        float minCardSize = 100f * ImGuiHelpers.GlobalScale;
        float borderThickness = 2f * ImGuiHelpers.GlobalScale;
        float rounding = 8f * ImGuiHelpers.GlobalScale;
        float buttonSize = 23f * ImGuiHelpers.GlobalScale;
        float buttonSpacing = 6f * ImGuiHelpers.GlobalScale;
        float padding = 8f * ImGuiHelpers.GlobalScale;
        const int maxCardsPerRow = 4;
        int cardsPerRow = maxCardsPerRow;
        
        float cardSize = (availableWidth - (cardsPerRow - 1) * cardSpacing) / cardsPerRow;
        
        while (cardSize < minCardSize && cardsPerRow > 1)
        {
            cardsPerRow--;
            cardSize = (availableWidth - (cardsPerRow - 1) * cardSpacing) / cardsPerRow;
        }
        
        cardSize = Math.Max(cardSize, minCardSize);

        float startX = ImGui.GetCursorPosX();
        float startY = ImGui.GetCursorPosY();
        var windowPos = ImGui.GetWindowPos();
        var scrollY = ImGui.GetScrollY();
        
        int totalRows = (groups.Count + cardsPerRow - 1) / cardsPerRow;
        
        float totalHeight = totalRows * cardSize + (totalRows - 1) * cardSpacing;
        ImGui.Dummy(new Vector2(availableWidth, totalHeight));
        
        var drawList = ImGui.GetWindowDrawList();
        int cardIndex = 0;

        foreach (var entry in groups)
        {
            var groupDto = entry.Key;
            var pairsInGroup = entry.Value;
            var groupName = _serverConfigurationManager.GetNoteForGid(groupDto.GID);
            if (string.IsNullOrEmpty(groupName))
            {
                groupName = groupDto.Group.Alias ?? groupDto.GID;
            }

            var maxNameLength = 14;
            var displayName = groupName.Length > maxNameLength 
                ? groupName.Substring(0, maxNameLength - 3) + "..." 
                : groupName;

            var totalMembers = pairsInGroup.Count + 1;
            var connectedMembers = pairsInGroup.Count(p => p.IsOnline) + 1;
            var memberText = $"{connectedMembers}/{totalMembers}";

            bool isPaused = groupDto.GroupUserPermissions.IsPaused();
            bool isVfxDisabled = groupDto.GroupUserPermissions.IsDisableVFX();
            bool isSoundDisabled = groupDto.GroupUserPermissions.IsDisableSounds();
            bool isAnimDisabled = groupDto.GroupUserPermissions.IsDisableAnimations();
            bool isHousingDisabled = groupDto.GroupUserPermissions.IsDisableHousing();

            int col = cardIndex % cardsPerRow;
            int row = cardIndex / cardsPerRow;
            
            var cardMin = new Vector2(
                windowPos.X + startX + col * (cardSize + cardSpacing),
                windowPos.Y + startY + row * (cardSize + cardSpacing) - scrollY);
            var cardMax = new Vector2(cardMin.X + cardSize, cardMin.Y + cardSize);
            var bgColor = new Vector4(0x1C / 255f, 0x1C / 255f, 0x1C / 255f, 1f);
            var pausedColor = ImGuiColors.DalamudOrange;
            var cardBorderColor = new Vector4(0x4A / 255f, 0x36 / 255f, 0x68 / 255f, 0.70f);
            var borderColor = isPaused ? pausedColor with { W = 0.8f } : cardBorderColor;
            var nameColor = isPaused ? pausedColor : new Vector4(0x9B / 255f, 0x82 / 255f, 0xC0 / 255f, 1f);

            drawList.AddRectFilled(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(bgColor), rounding);
            drawList.AddRect(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(borderColor), rounding, ImDrawFlags.None, borderThickness);

            var nameSize = ImGui.CalcTextSize(displayName);
            var namePos = new Vector2(
                cardMin.X + (cardSize - nameSize.X) / 2f,
                cardMin.Y + padding);
            drawList.AddText(namePos, ImGui.ColorConvertFloat4ToU32(nameColor), displayName);

            float nextLineY = cardMin.Y + padding + nameSize.Y + 4f * ImGuiHelpers.GlobalScale;
            if (isPaused)
            {
                var pausedText = Loc.Get("Syncshell.Cards.Paused");
                var pausedTextSize = ImGui.CalcTextSize(pausedText);
                var pausedTextPos = new Vector2(
                    cardMin.X + (cardSize - pausedTextSize.X) / 2f,
                    nextLineY);
                drawList.AddText(pausedTextPos, ImGui.ColorConvertFloat4ToU32(pausedColor), pausedText);
                nextLineY += pausedTextSize.Y + 4f * ImGuiHelpers.GlobalScale;
            }

            var memberSize = ImGui.CalcTextSize(memberText);

            // Info button sizing
            float infoBtnSize = memberSize.Y + 2f * ImGuiHelpers.GlobalScale;
            float infoSpacing = 4f * ImGuiHelpers.GlobalScale;
            float totalMemberLineWidth = memberSize.X + infoSpacing + infoBtnSize;

            float memberLineStartX = cardMin.X + (cardSize - totalMemberLineWidth) / 2f;
            var memberPos = new Vector2(memberLineStartX, nextLineY);
            drawList.AddText(memberPos, ImGui.ColorConvertFloat4ToU32(new Vector4(0.7f, 0.7f, 0.7f, 1f)), memberText);

            // Info button
            float infoButtonX = memberLineStartX + memberSize.X + infoSpacing;
            float infoButtonY = nextLineY + (memberSize.Y - infoBtnSize) / 2f;
            ImGui.SetCursorScreenPos(new Vector2(infoButtonX, infoButtonY));
            using (ImRaii.PushId($"info-{groupDto.GID}"))
            {
                using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.35f, 0.5f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.3f, 0.5f)))
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f)))
                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 3f * ImGuiHelpers.GlobalScale))
                {
                    if (_uiShared.IconButtonCentered(FontAwesomeIcon.InfoCircle, infoBtnSize, square: true))
                    {
                        if (string.Equals(_profileWindowGid, groupDto.GID, StringComparison.Ordinal))
                        {
                            _profileWindowGid = null;
                            _currentProfile = null;
                            _profileTexture?.Dispose();
                            _profileTexture = null;
                            _bannerTexture?.Dispose();
                            _bannerTexture = null;
                        }
                        else
                        {
                            _profileWindowGid = groupDto.GID;
                            _profileLoading = true;
                            _currentProfile = null;
                            _profileTexture?.Dispose();
                            _profileTexture = null;
                            _bannerTexture?.Dispose();
                            _bannerTexture = null;
                            _ = LoadGroupProfileAsync(groupDto);
                        }
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get("Syncshell.Cards.ShowProfile"));
            }

            float bottomRowY = cardMax.Y - padding - buttonSize;
            float topRowY = bottomRowY - buttonSize - buttonSpacing;
            float topRowButtonsWidth = buttonSize * 4 + buttonSpacing * 3;
            float topRowStartX = cardMin.X + (cardSize - topRowButtonsWidth) / 2f;
            
            ImGui.SetCursorScreenPos(new Vector2(topRowStartX, topRowY));
            using (ImRaii.PushId($"sound-{groupDto.GID}"))
            {
                var soundIcon = FontAwesomeIcon.VolumeUp;
                var soundColor = isSoundDisabled ? ImGuiColors.DalamudRed : new Vector4(0.4f, 0.9f, 0.4f, 1f);
                using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.2f, 0.25f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.35f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.3f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.Text, soundColor))
                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4f * ImGuiHelpers.GlobalScale))
                {
                    if (_uiShared.IconButtonCentered(soundIcon, buttonSize, square: true))
                    {
                        var perm = groupDto.GroupUserPermissions;
                        var newState = !perm.IsDisableSounds();
                        perm.SetDisableSounds(newState);
                        _mainUi.Mediator.Publish(new GroupSyncOverrideChanged(groupDto.Group.GID, perm.IsDisableSounds(), null, null));
                        _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(groupDto.Group, new UserData(ApiController.UID), perm));
                        
                        // Send notification
                        var notifTitle = string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Cards.Notification.Title"), groupName);
                        var notifBody = string.Format(CultureInfo.CurrentCulture, Loc.Get(newState ? "Syncshell.Cards.Notification.SoundDisabled" : "Syncshell.Cards.Notification.SoundEnabled"), totalMembers);
                        _mainUi.Mediator.Publish(new DualNotificationMessage(notifTitle, notifBody, NotificationType.Success));
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get(isSoundDisabled ? "Syncshell.Cards.SoundDisabled" : "Syncshell.Cards.SoundEnabled"));
            }
            
            ImGui.SetCursorScreenPos(new Vector2(topRowStartX + buttonSize + buttonSpacing, topRowY));
            using (ImRaii.PushId($"anim-{groupDto.GID}"))
            {
                var animIcon = FontAwesomeIcon.Running;
                var animColor = isAnimDisabled ? ImGuiColors.DalamudRed : new Vector4(0.4f, 0.9f, 0.4f, 1f);
                using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.2f, 0.25f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.35f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.3f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.Text, animColor))
                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4f * ImGuiHelpers.GlobalScale))
                {
                    if (_uiShared.IconButtonCentered(animIcon, buttonSize, square: true))
                    {
                        var perm = groupDto.GroupUserPermissions;
                        var newState = !perm.IsDisableAnimations();
                        perm.SetDisableAnimations(newState);
                        _mainUi.Mediator.Publish(new GroupSyncOverrideChanged(groupDto.Group.GID, null, perm.IsDisableAnimations(), null));
                        _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(groupDto.Group, new UserData(ApiController.UID), perm));
                        
                        var notifTitle = string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Cards.Notification.Title"), groupName);
                        var notifBody = string.Format(CultureInfo.CurrentCulture, Loc.Get(newState ? "Syncshell.Cards.Notification.AnimDisabled" : "Syncshell.Cards.Notification.AnimEnabled"), totalMembers);
                        _mainUi.Mediator.Publish(new DualNotificationMessage(notifTitle, notifBody, NotificationType.Success));
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get(isAnimDisabled ? "Syncshell.Cards.AnimDisabled" : "Syncshell.Cards.AnimEnabled"));
            }
            
            ImGui.SetCursorScreenPos(new Vector2(topRowStartX + (buttonSize + buttonSpacing) * 2, topRowY));
            using (ImRaii.PushId($"vfx-{groupDto.GID}"))
            {
                var vfxIcon = FontAwesomeIcon.Sun;
                var vfxColor = isVfxDisabled ? ImGuiColors.DalamudRed : new Vector4(0.4f, 0.9f, 0.4f, 1f);
                using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.2f, 0.25f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.35f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.3f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.Text, vfxColor))
                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4f * ImGuiHelpers.GlobalScale))
                {
                    if (_uiShared.IconButtonCentered(vfxIcon, buttonSize, square: true))
                    {
                        var perm = groupDto.GroupUserPermissions;
                        var newState = !perm.IsDisableVFX();
                        perm.SetDisableVFX(newState);
                        _mainUi.Mediator.Publish(new GroupSyncOverrideChanged(groupDto.Group.GID, null, null, perm.IsDisableVFX()));
                        _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(groupDto.Group, new UserData(ApiController.UID), perm));
                        
                        var notifTitle = string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Cards.Notification.Title"), groupName);
                        var notifBody = string.Format(CultureInfo.CurrentCulture, Loc.Get(newState ? "Syncshell.Cards.Notification.VfxDisabled" : "Syncshell.Cards.Notification.VfxEnabled"), totalMembers);
                        _mainUi.Mediator.Publish(new DualNotificationMessage(notifTitle, notifBody, NotificationType.Success));
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get(isVfxDisabled ? "Syncshell.Cards.VfxDisabled" : "Syncshell.Cards.VfxEnabled"));
            }

            ImGui.SetCursorScreenPos(new Vector2(topRowStartX + (buttonSize + buttonSpacing) * 3, topRowY));
            using (ImRaii.PushId($"housing-{groupDto.GID}"))
            {
                var housingIcon = FontAwesomeIcon.Home;
                var housingColor = isHousingDisabled ? ImGuiColors.DalamudRed : new Vector4(0.4f, 0.9f, 0.4f, 1f);
                using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.2f, 0.25f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.35f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.3f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.Text, housingColor))
                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4f * ImGuiHelpers.GlobalScale))
                {
                    if (_uiShared.IconButtonCentered(housingIcon, buttonSize, square: true))
                    {
                        var perm = groupDto.GroupUserPermissions;
                        var newState = !perm.IsDisableHousing();
                        perm.SetDisableHousing(newState);
                        _mainUi.Mediator.Publish(new GroupSyncOverrideChanged(groupDto.Group.GID, null, null, null, perm.IsDisableHousing()));
                        _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(groupDto.Group, new UserData(ApiController.UID), perm));

                        var notifTitle = string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Cards.Notification.Title"), groupName);
                        var notifBody = string.Format(CultureInfo.CurrentCulture, Loc.Get(newState ? "Syncshell.Cards.Notification.HousingDisabled" : "Syncshell.Cards.Notification.HousingEnabled"), totalMembers);
                        _mainUi.Mediator.Publish(new DualNotificationMessage(notifTitle, notifBody, NotificationType.Success));
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get(isHousingDisabled ? "Syncshell.Cards.HousingDisabled" : "Syncshell.Cards.HousingEnabled"));
            }

            bool isAdmin = string.Equals(groupDto.OwnerUID, ApiController.UID, StringComparison.Ordinal)
                           || groupDto.GroupUserInfo.IsModerator();
            bool isFavorite = favorites.Contains(groupDto.GID);
            int bottomBtnCount = isAdmin ? 4 : 3;
            float bottomRowButtonsWidth = buttonSize * bottomBtnCount + buttonSpacing * (bottomBtnCount - 1);
            float bottomRowStartX = cardMin.X + (cardSize - bottomRowButtonsWidth) / 2f;

            ImGui.SetCursorScreenPos(new Vector2(bottomRowStartX, bottomRowY));
            using (ImRaii.PushId($"pause-{groupDto.GID}"))
            {
                var pauseIcon = isPaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
                var pauseColor = isPaused ? ImGuiColors.DalamudOrange : new Vector4(1f, 1f, 1f, 1f);
                using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.2f, 0.25f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.35f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.3f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.Text, pauseColor))
                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4f * ImGuiHelpers.GlobalScale))
                {
                    if (_uiShared.IconButtonCentered(pauseIcon, buttonSize, square: true))
                    {
                        var userPerm = groupDto.GroupUserPermissions ^ GroupUserPermissions.Paused;
                        _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(groupDto.Group, new UserData(ApiController.UID), userPerm));
                    }
                }
                var pauseActionText = isPaused ? Loc.Get("Syncshell.Cards.Resume") : Loc.Get("Syncshell.Cards.Pause");
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Cards.PauseTooltip"), pauseActionText));
            }

            int btnIdx = 1;
            ImGui.SetCursorScreenPos(new Vector2(bottomRowStartX + (buttonSize + buttonSpacing) * btnIdx, bottomRowY));
            using (ImRaii.PushId($"fav-{groupDto.GID}"))
            {
                var starColor = isFavorite ? ImGuiColors.ParsedGold : new Vector4(1f, 1f, 1f, 1f);
                using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.2f, 0.25f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.35f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.3f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.Text, starColor))
                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4f * ImGuiHelpers.GlobalScale))
                {
                    if (_uiShared.IconButtonCentered(FontAwesomeIcon.Star, buttonSize, square: true))
                    {
                        if (isFavorite)
                            favorites.Remove(groupDto.GID);
                        else
                            favorites.Add(groupDto.GID);
                        _syncshellConfig.Save();
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get(isFavorite ? "Syncshell.Cards.RemoveFavorite" : "Syncshell.Cards.AddFavorite"));
            }

            btnIdx++;
            ImGui.SetCursorScreenPos(new Vector2(bottomRowStartX + (buttonSize + buttonSpacing) * btnIdx, bottomRowY));
            using (ImRaii.PushId($"members-{groupDto.GID}"))
            {
                using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.2f, 0.25f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.35f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.3f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f)))
                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4f * ImGuiHelpers.GlobalScale))
                {
                    if (_uiShared.IconButtonCentered(FontAwesomeIcon.Users, buttonSize, square: true))
                    {
                        if (string.Equals(_membersWindowGid, groupDto.GID, StringComparison.Ordinal))
                        {
                            _membersWindowGid = null;
                        }
                        else
                        {
                            _membersWindowGid = groupDto.GID;
                            _membersFilter = string.Empty;
                        }
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get("Syncshell.Cards.ShowMembers"));
            }

            if (isAdmin)
            {
                btnIdx++;
                ImGui.SetCursorScreenPos(new Vector2(bottomRowStartX + (buttonSize + buttonSpacing) * btnIdx, bottomRowY));
                using (ImRaii.PushId($"admin-{groupDto.GID}"))
                {
                    using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.2f, 0.25f, 1f)))
                    using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.35f, 1f)))
                    using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.3f, 1f)))
                    using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f)))
                    using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4f * ImGuiHelpers.GlobalScale))
                    {
                        if (_uiShared.IconButtonCentered(FontAwesomeIcon.Crown, buttonSize, square: true))
                        {
                            _mainUi.Mediator.Publish(new OpenSyncshellAdminPanel(groupDto));
                        }
                    }
                    UiSharedService.AttachToolTip(Loc.Get("Syncshell.Cards.OpenAdmin"));
                }
            }

            var hoverAreaMax = new Vector2(cardMax.X, topRowY - buttonSpacing);
            if (ImGui.IsMouseHoveringRect(cardMin, hoverAreaMax) && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(groupName);
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Cards.OnlineMembers"), connectedMembers, totalMembers));
                if (!string.IsNullOrEmpty(groupDto.Group.Alias) && !string.Equals(groupDto.Group.Alias, groupName, StringComparison.Ordinal))
                {
                    ImGui.TextUnformatted($"ID: {groupDto.GID}");
                }
                if (isPaused)
                {
                    UiSharedService.ColorText(Loc.Get("Syncshell.Cards.Paused"), ImGuiColors.DalamudOrange);
                }
                ImGui.EndTooltip();
            }

            cardIndex++;
        }

        // Inline members for favorite syncshells
        DrawFavoriteMembersInline(groups, favorites);

        DrawMembersWindow();
        DrawProfileWindow();
    }

    private void DrawFavoriteMembersInline(List<KeyValuePair<GroupFullInfoDto, List<Pair>>> groups, HashSet<string> favorites)
    {
        var favoriteGroups = groups.Where(g => favorites.Contains(g.Key.GID)).ToList();
        if (favoriteGroups.Count == 0) return;

        ImGuiHelpers.ScaledDummy(8f);

        // Separator violet between cards and favorite members
        var separatorColor = new Vector4(0x4A / 255f, 0x36 / 255f, 0x68 / 255f, 0.70f);
        var cursorScreenPos = ImGui.GetCursorScreenPos();
        var availWidth = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddLine(
            cursorScreenPos,
            new Vector2(cursorScreenPos.X + availWidth, cursorScreenPos.Y),
            ImGui.ColorConvertFloat4ToU32(separatorColor),
            2f * ImGuiHelpers.GlobalScale);
        ImGuiHelpers.ScaledDummy(6f);

        foreach (var entry in favoriteGroups)
        {
            var groupDto = entry.Key;
            var pairsInGroup = entry.Value;
            var groupName = _serverConfigurationManager.GetNoteForGid(groupDto.GID);
            if (string.IsNullOrEmpty(groupName))
            {
                groupName = groupDto.Group.Alias ?? groupDto.GID;
            }

            if (!_favoriteMembersExpanded.TryGetValue(groupDto.GID, out var expanded))
            {
                expanded = true;
                _favoriteMembersExpanded[groupDto.GID] = expanded;
            }

            var headerLabel = string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Cards.FavoriteMembers"), groupName, pairsInGroup.Count + 1);
            var headerExpanded = expanded;
            UiSharedService.DrawArrowToggle(ref headerExpanded, $"##fav-members-toggle-{groupDto.GID}");
            if (headerExpanded != expanded)
            {
                _favoriteMembersExpanded[groupDto.GID] = headerExpanded;
            }
            ImGui.SameLine(0f, 6f * ImGuiHelpers.GlobalScale);
            UiSharedService.ColorText(headerLabel, new Vector4(0x9B / 255f, 0x82 / 255f, 0xC0 / 255f, 1f));

            // Bouton de tri par type sur la ligne du header
            var favSortIcon = _membersSortByType ? FontAwesomeIcon.UserFriends : FontAwesomeIcon.Signal;
            var favSortTooltip = _membersSortByType
                ? Loc.Get("Syncshell.Members.SortByStatus")
                : Loc.Get("Syncshell.Members.SortByType");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - _uiShared.GetIconButtonSize(favSortIcon).X);
            ImGui.PushID($"fav-sort-{groupDto.GID}");
            if (_uiShared.IconButton(favSortIcon))
            {
                _membersSortByType = !_membersSortByType;
            }
            ImGui.PopID();
            UiSharedService.AttachToolTip(favSortTooltip);

            if (headerExpanded)
            {
                ImGui.Indent(20);
                DrawMembersList(groupDto, pairsInGroup);
                ImGui.Unindent(20);
            }

            ImGuiHelpers.ScaledDummy(4f);
        }
    }

    private List<DrawGroupPair> BuildDrawPairs(GroupFullInfoDto groupDto, IEnumerable<Pair> pairs)
    {
        var sortedPairs = pairs
            .OrderByDescending(u => string.Equals(u.UserData.UID, groupDto.OwnerUID, StringComparison.Ordinal))
            .ThenByDescending(u => u.GroupPair[groupDto].GroupPairStatusInfo.IsModerator())
            .ThenByDescending(u => u.GroupPair[groupDto].GroupPairStatusInfo.IsPinned())
            .ThenByDescending(u => u.IsOnline)
            .ThenBy(u => u.GetPairSortKey(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<DrawGroupPair>();
        foreach (var pair in sortedPairs)
        {
            var cacheKey = groupDto.GID + pair.UserData.UID;
            var groupPairFullInfoDto = pair.GroupPair.FirstOrDefault(
                g => string.Equals(g.Key.Group.GID, groupDto.GID, StringComparison.Ordinal)
            ).Value;

            if (groupPairFullInfoDto == null) continue;

            if (!_drawGroupPairCache.TryGetValue(cacheKey, out var drawPair))
            {
                drawPair = new DrawGroupPair(
                    cacheKey, pair,
                    ApiController, _mainUi.Mediator, groupDto,
                    groupPairFullInfoDto,
                    _uidDisplayHandler,
                    _uiShared,
                    _charaDataManager,
                    _autoDetectRequestService,
                    _serverConfigurationManager,
                    _mareConfig);
                _drawGroupPairCache[cacheKey] = drawPair;
            }
            else
            {
                drawPair.UpdateData(groupDto, groupPairFullInfoDto);
            }

            result.Add(drawPair);
        }
        return result;
    }

    private void DrawMembersList(GroupFullInfoDto groupDto, List<Pair> pairsInGroup)
    {
        var drawPairs = BuildDrawPairs(groupDto, pairsInGroup);

        if (_membersSortByType)
        {
            DrawMembersListByType(drawPairs, groupDto.GID, "fav");
        }
        else
        {
            DrawMembersListByStatus(drawPairs, groupDto.GID, "fav");
        }
    }

    private void DrawMembersListByStatus(List<DrawGroupPair> drawPairs, string gid, string prefix)
    {
        var visibleUsers = new List<DrawGroupPair>();
        var onlineUsers = new List<DrawGroupPair>();
        var offlineUsers = new List<DrawGroupPair>();

        foreach (var dp in drawPairs)
        {
            if (dp.Pair.IsVisible)
                visibleUsers.Add(dp);
            else if (dp.Pair.IsOnline)
                onlineUsers.Add(dp);
            else
                offlineUsers.Add(dp);
        }

        bool needsSpacer = false;

        if (visibleUsers.Count > 0)
        {
            var visKey = gid + $"-{prefix}-visible";
            if (!_favoriteMembersExpanded.ContainsKey(visKey)) _favoriteMembersExpanded[visKey] = true;
            var visExpanded = _favoriteMembersExpanded[visKey];
            DrawMembersSectionHeader(
                string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.VisibleSection"), visibleUsers.Count),
                new Vector4(0.4f, 0.75f, 1f, 1f),
                ref visExpanded,
                $"##{prefix}-visible-{gid}");
            _favoriteMembersExpanded[visKey] = visExpanded;

            if (visExpanded)
            {
                UidDisplayHandler.RenderPairList(visibleUsers);
            }
            needsSpacer = true;
        }

        if (onlineUsers.Count > 0)
        {
            if (needsSpacer) ImGuiHelpers.ScaledDummy(8f);
            var onKey = gid + $"-{prefix}-online";
            if (!_favoriteMembersExpanded.ContainsKey(onKey)) _favoriteMembersExpanded[onKey] = true;
            var onExpanded = _favoriteMembersExpanded[onKey];
            DrawMembersSectionHeader(
                string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.OnlineSection"), onlineUsers.Count),
                new Vector4(0.4f, 0.9f, 0.4f, 1f),
                ref onExpanded,
                $"##{prefix}-online-{gid}");
            _favoriteMembersExpanded[onKey] = onExpanded;

            if (onExpanded)
            {
                UidDisplayHandler.RenderPairList(onlineUsers);
            }
            needsSpacer = true;
        }

        if (offlineUsers.Count > 0)
        {
            if (needsSpacer) ImGuiHelpers.ScaledDummy(8f);
            var offKey = gid + $"-{prefix}-offline";
            if (!_favoriteMembersExpanded.ContainsKey(offKey)) _favoriteMembersExpanded[offKey] = false;
            var offExpanded = _favoriteMembersExpanded[offKey];
            DrawMembersSectionHeader(
                string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.OfflineSection"), offlineUsers.Count),
                ImGuiColors.DalamudGrey,
                ref offExpanded,
                $"##{prefix}-offline-{gid}");
            _favoriteMembersExpanded[offKey] = offExpanded;

            if (offExpanded)
            {
                bool hideOffline = offlineUsers.Count > 1000;
                if (hideOffline)
                {
                    UiSharedService.ColorText($"    {offlineUsers.Count} offline users omitted from display.", ImGuiColors.DalamudGrey);
                }
                else
                {
                    UidDisplayHandler.RenderPairList(offlineUsers);
                }
            }
        }
    }
    
    private void DrawMembersListByType(List<DrawGroupPair> drawPairs, string gid, string prefix)
    {
        var visibleUsers = new List<DrawGroupPair>();
        var onlineUsers = new List<DrawGroupPair>();
        var offlineUsers = new List<DrawGroupPair>();

        foreach (var dp in drawPairs)
        {
            if (dp.Pair.IsVisible)
                visibleUsers.Add(dp);
            else if (dp.Pair.IsOnline)
                onlineUsers.Add(dp);
            else
                offlineUsers.Add(dp);
        }

        bool needsSpacer = false;

        if (visibleUsers.Count > 0)
        {
            var visKey = gid + $"-{prefix}-visible";
            if (!_favoriteMembersExpanded.ContainsKey(visKey)) _favoriteMembersExpanded[visKey] = true;
            var visExpanded = _favoriteMembersExpanded[visKey];
            DrawMembersSectionHeader(
                string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.VisibleSection"), visibleUsers.Count),
                new Vector4(0.4f, 0.75f, 1f, 1f),
                ref visExpanded,
                $"##{prefix}-visible-{gid}");
            _favoriteMembersExpanded[visKey] = visExpanded;

            if (visExpanded)
            {
                DrawTypeSubSections(visibleUsers, gid, $"{prefix}-visible");
            }
            needsSpacer = true;
        }

        if (onlineUsers.Count > 0)
        {
            if (needsSpacer) ImGuiHelpers.ScaledDummy(8f);
            var onKey = gid + $"-{prefix}-online";
            if (!_favoriteMembersExpanded.ContainsKey(onKey)) _favoriteMembersExpanded[onKey] = true;
            var onExpanded = _favoriteMembersExpanded[onKey];
            DrawMembersSectionHeader(
                string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.OnlineSection"), onlineUsers.Count),
                new Vector4(0.4f, 0.9f, 0.4f, 1f),
                ref onExpanded,
                $"##{prefix}-online-{gid}");
            _favoriteMembersExpanded[onKey] = onExpanded;

            if (onExpanded)
            {
                DrawTypeSubSections(onlineUsers, gid, $"{prefix}-online");
            }
            needsSpacer = true;
        }

        if (offlineUsers.Count > 0)
        {
            if (needsSpacer) ImGuiHelpers.ScaledDummy(8f);
            var offKey = gid + $"-{prefix}-offline";
            if (!_favoriteMembersExpanded.ContainsKey(offKey)) _favoriteMembersExpanded[offKey] = false;
            var offExpanded = _favoriteMembersExpanded[offKey];
            DrawMembersSectionHeader(
                string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.OfflineSection"), offlineUsers.Count),
                ImGuiColors.DalamudGrey,
                ref offExpanded,
                $"##{prefix}-offline-{gid}");
            _favoriteMembersExpanded[offKey] = offExpanded;

            if (offExpanded)
            {
                bool hideOffline = offlineUsers.Count > 1000;
                if (hideOffline)
                {
                    UiSharedService.ColorText($"    {offlineUsers.Count} offline users omitted from display.", ImGuiColors.DalamudGrey);
                }
                else
                {
                    DrawTypeSubSections(offlineUsers, gid, $"{prefix}-offline");
                }
            }
        }
    }
    
    private void DrawTypeSubSections(List<DrawGroupPair> pairs, string gid, string subPrefix)
    {
        var directPairs = new List<DrawGroupPair>();
        var syncshellOnlyPairs = new List<DrawGroupPair>();

        foreach (var dp in pairs)
        {
            if (dp.Pair.UserPair != null)
                directPairs.Add(dp);
            else
                syncshellOnlyPairs.Add(dp);
        }

        // S'il n'y a qu'un seul type, pas besoin de sous-sections
        if (directPairs.Count == 0 || syncshellOnlyPairs.Count == 0)
        {
            UidDisplayHandler.RenderPairList(pairs);
            return;
        }

        ImGui.Indent(12f * ImGuiHelpers.GlobalScale);

        var dirKey = gid + $"-{subPrefix}-direct";
        if (!_favoriteMembersExpanded.ContainsKey(dirKey)) _favoriteMembersExpanded[dirKey] = true;
        var dirExpanded = _favoriteMembersExpanded[dirKey];
        DrawMembersSectionHeader(
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.DirectPairsSection"), directPairs.Count),
            new Vector4(0.63f, 0.25f, 1f, 1f),
            ref dirExpanded,
            $"##{subPrefix}-direct-{gid}");
        _favoriteMembersExpanded[dirKey] = dirExpanded;

        if (dirExpanded)
        {
            UidDisplayHandler.RenderPairList(directPairs);
        }

        ImGuiHelpers.ScaledDummy(4f);

        var ssKey = gid + $"-{subPrefix}-ssonly";
        if (!_favoriteMembersExpanded.ContainsKey(ssKey)) _favoriteMembersExpanded[ssKey] = true;
        var ssExpanded = _favoriteMembersExpanded[ssKey];
        DrawMembersSectionHeader(
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.SyncshellOnlySection"), syncshellOnlyPairs.Count),
            new Vector4(0.6f, 0.6f, 0.6f, 1f),
            ref ssExpanded,
            $"##{subPrefix}-ssonly-{gid}");
        _favoriteMembersExpanded[ssKey] = ssExpanded;

        if (ssExpanded)
        {
            UidDisplayHandler.RenderPairList(syncshellOnlyPairs);
        }

        ImGui.Unindent(12f * ImGuiHelpers.GlobalScale);
    }

    private void DrawMembersWindow()
    {
        if (_membersWindowGid == null) return;

        var entry = _pairManager.GroupPairs
            .FirstOrDefault(g => string.Equals(g.Key.Group.GID, _membersWindowGid, StringComparison.Ordinal));

        if (entry.Key == null)
        {
            _membersWindowGid = null;
            return;
        }

        var groupDto = entry.Key;
        var pairsInGroup = entry.Value;
        var groupName = _serverConfigurationManager.GetNoteForGid(groupDto.GID);
        if (string.IsNullOrEmpty(groupName))
        {
            groupName = groupDto.Group.Alias ?? groupDto.GID;
        }

        var windowTitle = $"{string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.WindowTitle"), groupName)}###MembersWindow{groupDto.GID}";
        bool isOpen = true;

        ImGui.SetNextWindowSize(new Vector2(450f * ImGuiHelpers.GlobalScale, 500f * ImGuiHelpers.GlobalScale), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(windowTitle, ref isOpen, ImGuiWindowFlags.NoCollapse))
        {
            var totalMembers = pairsInGroup.Count + 1;
            var connectedMembers = pairsInGroup.Count(p => p.IsOnline) + 1;
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.OnlineTotal"), connectedMembers, totalMembers));

            var leavePopupId = $"##leave-confirm-{groupDto.GID}";

            // Bouton de tri + bouton quitter alignés à droite
            var sortIcon = _membersSortByType ? FontAwesomeIcon.UserFriends : FontAwesomeIcon.Signal;
            var sortTooltip = _membersSortByType
                ? Loc.Get("Syncshell.Members.SortByStatus")
                : Loc.Get("Syncshell.Members.SortByType");
            var leaveButtonSize = _uiShared.GetIconTextButtonSize(FontAwesomeIcon.SignOutAlt, Loc.Get("Syncshell.Members.Leave"));
            var sortButtonSize = _uiShared.GetIconButtonSize(sortIcon);
            var spacing = ImGui.GetStyle().ItemSpacing.X;

            ImGui.SameLine(ImGui.GetContentRegionAvail().X - leaveButtonSize - sortButtonSize.X - spacing);
            if (_uiShared.IconButton(sortIcon))
            {
                _membersSortByType = !_membersSortByType;
            }
            UiSharedService.AttachToolTip(sortTooltip);

            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.5f, 0.1f, 0.1f, 1f)))
            {
                if (_uiShared.IconTextButton(FontAwesomeIcon.SignOutAlt, Loc.Get("Syncshell.Members.Leave")))
                {
                    _membersLeaveConfirm = true;
                    ImGui.OpenPopup(leavePopupId);
                }
            }

            if (ImGui.BeginPopupModal(leavePopupId, ref _membersLeaveConfirm, UiSharedService.PopupWindowFlags))
            {
                bool isOwner = string.Equals(groupDto.OwnerUID, ApiController.UID, StringComparison.Ordinal);
                UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.LeaveConfirm"), groupName));
                if (isOwner)
                {
                    ImGuiHelpers.ScaledDummy(4f);
                    UiSharedService.ColorTextWrapped(Loc.Get("Syncshell.Members.LeaveOwnerWarning"), ImGuiColors.DalamudRed);
                }
                ImGuiHelpers.ScaledDummy(4f);
                using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 1f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.5f, 0.1f, 0.1f, 1f)))
                {
                    if (ImGui.Button(Loc.Get("Syncshell.Members.LeaveConfirmButton"), new Vector2(-1, 0)))
                    {
                        _ = ApiController.GroupLeave(groupDto);
                        _membersLeaveConfirm = false;
                        _membersWindowGid = null;
                        ImGui.CloseCurrentPopup();
                    }
                }
                if (ImGui.Button(Loc.Get("Syncshell.Members.LeaveCancelButton"), new Vector2(-1, 0)))
                {
                    _membersLeaveConfirm = false;
                    ImGui.CloseCurrentPopup();
                }
                UiSharedService.SetScaledWindowSize(330);
                ImGui.EndPopup();
            }

            ImGui.Separator();

            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##membersfilter", Loc.Get("Syncshell.Members.Filter.Placeholder"), ref _membersFilter, 255);
            ImGuiHelpers.ScaledDummy(4f);

            var filteredPairs = pairsInGroup.AsEnumerable();
            if (!string.IsNullOrEmpty(_membersFilter))
            {
                filteredPairs = filteredPairs.Where(p =>
                    p.UserData.AliasOrUID.Contains(_membersFilter, StringComparison.OrdinalIgnoreCase) ||
                    (p.GetNote()?.Contains(_membersFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (p.PlayerName?.Contains(_membersFilter, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            var drawPairs = BuildDrawPairs(groupDto, filteredPairs);

            if (ImGui.BeginChild("MembersList", Vector2.Zero, false))
            {
                if (_membersSortByType)
                {
                    DrawMembersListByType(drawPairs, groupDto.GID, "members");
                }
                else
                {
                    DrawMembersListByStatus(drawPairs, groupDto.GID, "members");
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();

        if (!isOpen)
        {
            _membersWindowGid = null;
        }
    }

    private static void DrawMembersSectionHeader(string label, Vector4 color, ref bool expanded, string id)
    {
        UiSharedService.DrawArrowToggle(ref expanded, id);
        ImGui.SameLine(0f, 6f * ImGuiHelpers.GlobalScale);
        UiSharedService.ColorText(label, color);
    }

    private async Task LoadGroupProfileAsync(GroupFullInfoDto groupDto)
    {
        try
        {
            var profile = await ApiController.GroupGetProfile(new GroupDto(groupDto.Group)).ConfigureAwait(false);
            _currentProfile = profile;

            if (profile?.ProfileImageBase64 is { Length: > 0 } profileImg)
            {
                try
                {
                    var bytes = Convert.FromBase64String(profileImg);
                    _profileTexture = _uiShared.LoadImage(bytes);
                }
                catch { /* ignore invalid image */ }
            }

            if (profile?.BannerImageBase64 is { Length: > 0 } bannerImg)
            {
                try
                {
                    var bytes = Convert.FromBase64String(bannerImg);
                    _bannerTexture = _uiShared.LoadImage(bytes);
                }
                catch { /* ignore invalid image */ }
            }
        }
        catch
        {
            _currentProfile = null;
        }
        finally
        {
            _profileLoading = false;
        }
    }

    private void DrawProfileWindow()
    {
        if (_profileWindowGid == null) return;

        var entry = _pairManager.GroupPairs
            .FirstOrDefault(g => string.Equals(g.Key.Group.GID, _profileWindowGid, StringComparison.Ordinal));

        if (entry.Key == null)
        {
            _profileWindowGid = null;
            return;
        }

        var groupDto = entry.Key;
        var groupName = _serverConfigurationManager.GetNoteForGid(groupDto.GID);
        if (string.IsNullOrEmpty(groupName))
        {
            groupName = groupDto.Group.Alias ?? groupDto.GID;
        }

        var windowTitle = $"{groupName} — {Loc.Get("SyncshellAdmin.Tab.Profile")}###ProfileWindow{groupDto.GID}";
        bool isOpen = true;

        ImGui.SetNextWindowSize(new Vector2(420f * ImGuiHelpers.GlobalScale, 450f * ImGuiHelpers.GlobalScale), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(windowTitle, ref isOpen, ImGuiWindowFlags.NoCollapse))
        {
            if (_profileLoading)
            {
                ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Profile.Loading"));
            }
            else if (_currentProfile == null)
            {
                ImGui.TextUnformatted(Loc.Get("SyncshellAdmin.Profile.NoProfile"));
            }
            else
            {
                var windowDrawList = ImGui.GetWindowDrawList();
                float cardRounding = 10f * ImGuiHelpers.GlobalScale;

                if (_bannerTexture != null)
                {
                    float availWidth = ImGui.GetContentRegionAvail().X;
                    float bannerHeight = availWidth * (260f / 840f);
                    var bannerMin = ImGui.GetCursorScreenPos();
                    var bannerMax = new Vector2(bannerMin.X + availWidth, bannerMin.Y + bannerHeight);
                    windowDrawList.AddImageRounded(
                        _bannerTexture.Handle, bannerMin, bannerMax,
                        Vector2.Zero, Vector2.One,
                        ImGui.ColorConvertFloat4ToU32(Vector4.One), cardRounding);
                    ImGui.Dummy(new Vector2(availWidth, bannerHeight));
                    ImGuiHelpers.ScaledDummy(6f);
                }

                UiSharedService.DrawCard("syncshell-profile-hero", () =>
                {
                    float imgSize = 80f * ImGuiHelpers.GlobalScale;
                    float imgRounding = 10f * ImGuiHelpers.GlobalScale;

                    if (_profileTexture != null)
                    {
                        var imgMin = ImGui.GetCursorScreenPos();
                        var imgMax = new Vector2(imgMin.X + imgSize, imgMin.Y + imgSize);
                        var cardDrawList = ImGui.GetWindowDrawList();
                        cardDrawList.AddImageRounded(
                            _profileTexture.Handle, imgMin, imgMax,
                            Vector2.Zero, Vector2.One,
                            ImGui.ColorConvertFloat4ToU32(Vector4.One), imgRounding);
                        ImGui.Dummy(new Vector2(imgSize, imgSize));
                        ImGui.SameLine();
                    }

                    ImGui.BeginGroup();
                    using (_uiShared.UidFont.Push())
                    {
                        UiSharedService.ColorText(groupName, UiSharedService.AccentColor);
                    }

                    if (_currentProfile.IsNsfw)
                    {
                        ImGui.SameLine();
                        UiSharedService.ColorText("NSFW", ImGuiColors.DalamudRed);
                    }

                    if (_currentProfile.Tags is { Length: > 0 })
                    {
                        ImGuiHelpers.ScaledDummy(2f);
                        foreach (var tag in _currentProfile.Tags)
                        {
                            UiSharedService.ColorText($"#{tag}", new Vector4(0.6f, 0.8f, 1f, 1f));
                            ImGui.SameLine();
                        }
                        ImGui.NewLine();
                    }
                    ImGui.EndGroup();
                }, stretchWidth: true);

                ImGuiHelpers.ScaledDummy(4f);

                if (!string.IsNullOrEmpty(_currentProfile.Description))
                {
                    UiSharedService.DrawCard("syncshell-profile-desc", () =>
                    {
                        UiSharedService.ColorText(Loc.Get("SyncshellAdmin.Profile.Description"), UiSharedService.AccentColor);
                        ImGuiHelpers.ScaledDummy(2f);
                        float wrapWidth = ImGui.GetContentRegionAvail().X;
                        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
                        ImGui.TextUnformatted(_currentProfile.Description);
                        ImGui.PopTextWrapPos();
                    }, stretchWidth: true);
                }
            }
        }
        ImGui.End();

        if (!isOpen)
        {
            _profileWindowGid = null;
            _currentProfile = null;
            _profileTexture?.Dispose();
            _profileTexture = null;
            _bannerTexture?.Dispose();
            _bannerTexture = null;
        }
    }
}