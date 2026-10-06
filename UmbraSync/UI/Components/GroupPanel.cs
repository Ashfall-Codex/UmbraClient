using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger _logger;
    private Task<bool>? _joinTask;
    private Task<GroupPasswordDto>? _createTask;
    private bool _createTaskWasNamed;
    private Task<List<BannedGroupUserDto>>? _bannedUsersTask;
    private Task<bool>? _passwordChangeTask;
    private Task<List<string>>? _bulkInvitesTask;
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
    private readonly Dictionary<string, (long Tick, List<DrawGroupPair> Pairs)> _drawPairsCache = new(StringComparer.Ordinal);
    private const long DrawPairsCacheMs = 1000;
    private string? _membersWindowGid = null;
    private bool _membersLeaveConfirm = false;
    private string _syncshellFilter = string.Empty;
    private string _membersFilter = string.Empty;
    private bool _membersSortByType = false;
    private readonly SyncshellConfigService _syncshellConfig;
    private readonly SlotService _slotService;
    private readonly UmbraProfileManager _profileManager;
    private readonly Dictionary<string, bool> _favoriteMembersExpanded = new(StringComparer.Ordinal);
    private string? _profileWindowGid = null;
    private bool _profileLoading = false;
    private GroupProfileDto? _currentProfile = null;
    private IDalamudTextureWrap? _profileTexture = null;
    private IDalamudTextureWrap? _bannerTexture = null;

    public GroupPanel(ILogger logger, CompactUi mainUi, UiSharedService uiShared, PairManager pairManager,
        UidDisplayHandler uidDisplayHandler, ServerConfigurationManager serverConfigurationManager,
        CharaDataManager charaDataManager, AutoDetectRequestService autoDetectRequestService,
        MareConfigService mareConfig, SyncshellConfigService syncshellConfig, SlotService slotService,
        UmbraProfileManager profileManager)
    {
        _profileManager = profileManager;
        _logger = logger;
        _mainUi = mainUi;
        _uiShared = uiShared;
        _pairManager = pairManager;
        _uidDisplayHandler = uidDisplayHandler;
        _serverConfigurationManager = serverConfigurationManager;
        _charaDataManager = charaDataManager;
        _autoDetectRequestService = autoDetectRequestService;
        _mareConfig = mareConfig;
        _syncshellConfig = syncshellConfig;
        _slotService = slotService;
    }

    private ApiController ApiController => _uiShared.ApiController;

    private readonly Dictionary<string, (string? Source, Task<IDalamudTextureWrap?> Task)> _shellIconTasks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _shellProfileRequests = new(StringComparer.Ordinal);
    private DateTime _lastShellProfileRequestUtc = DateTime.MinValue;

    public void ClearCache()
    {
        foreach (var pending in _shellIconTasks.Values)
            pending.Task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); }, TaskScheduler.Default);
        _shellIconTasks.Clear();
        _shellProfileRequests.Clear();
        _drawGroupPairCache.Clear();
        _sortedPairsCache.Clear();
        _sortedPairsLastUpdate.Clear();
        _drawPairsCache.Clear();
    }

    /// <summary>
    /// Récupère les réponses des appels serveur déclenchés par les boutons. Les lire avec
    /// <c>.Result</c> en plein dessin figeait le jeu le temps de l'aller-retour avec le hub.
    /// </summary>
    private void ConsumePendingCalls()
    {
        if (UiSharedService.TryConsumeUiTask(ref _joinTask, _logger, out var joined))
        {
            _errorGroupJoin = !joined;
            if (joined)
            {
                _syncShellToJoin = string.Empty;
                _showModalEnterPassword = false;
            }
        }

        if (_createTask is { IsCompleted: true })
        {
            var finished = _createTask;
            _createTask = null;

            if (finished.IsCompletedSuccessfully)
            {
                _lastCreatedGroup = finished.Result;
                if (_lastCreatedGroup != null && _createTaskWasNamed) _newSyncShellAlias = string.Empty;
            }
            else
            {
                _lastCreatedGroup = null;
                _errorGroupCreate = true;

                // Le message utile est celui de l'exception d'origine : l'agrégat qui l'enveloppe
                // n'annonce que « One or more errors occurred ».
                var cause = finished.Exception?.InnerException ?? finished.Exception;
                _logger.LogWarning(cause, "Création de syncshell en échec");
                _errorGroupCreateMessage = cause?.Message.Contains("name is already in use", StringComparison.OrdinalIgnoreCase) == true
                    ? Loc.Get("Syncshell.Create.NameInUse")
                    : cause?.Message ?? string.Empty;
            }
        }

        if (UiSharedService.TryConsumeUiTask(ref _bannedUsersTask, _logger, out var bans) && bans != null)
            _bannedUsers = bans;

        if (UiSharedService.TryConsumeUiTask(ref _passwordChangeTask, _logger, out var pwOk))
        {
            _isPasswordValid = pwOk;
            if (pwOk) _showModalChangePassword = false;
        }

        if (UiSharedService.TryConsumeUiTask(ref _bulkInvitesTask, _logger, out var bulkInvites) && bulkInvites != null)
            _bulkOneTimeInvites = bulkInvites;
    }

    public void DrawSyncshells(Action? drawAfterAdd = null)
    {
        ConsumePendingCalls();

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
            bool canJoin = !string.IsNullOrWhiteSpace(trimmedInput) && _joinTask == null;
            if (!canJoin) ImGui.BeginDisabled();
            if (ImGui.Button(Loc.Get("Syncshell.Button.Join"), new Vector2(-1, 0)))
            {
                _joinTask = ApiController.GroupJoin(new(new GroupData(trimmedInput), _syncShellPassword));
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
            using (ImRaii.Disabled(_createTask != null))
            {
                if (ImGui.Button(createLabel, new Vector2(createButtonWidth, createButtonHeight)))
                {
                    _createTaskWasNamed = !_createIsTemporary;
                    _createTask = _createIsTemporary
                        ? ApiController.GroupCreateTemporary(DateTime.UtcNow.AddHours(_tempSyncshellDurationHours))
                        : ApiController.GroupCreate(string.IsNullOrWhiteSpace(_newSyncShellAlias) ? null : _newSyncShellAlias.Trim());
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
            _expandedGroupState[groupDto.GID] = expandedState;
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
                var maxCapacity = groupDto.MaxUserCount > 0 ? groupDto.MaxUserCount : ApiController.ServerInfo.MaxGroupUserCount;
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
                var buttonSizes = _uiShared.GetIconButtonSize(FontAwesomeIcon.EllipsisH).X + _uiShared.GetIconButtonSize(FontAwesomeIcon.LockOpen).X;
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
                using (ImRaii.Disabled(_bannedUsersTask != null))
                {
                    if (_uiShared.IconTextButton(FontAwesomeIcon.Retweet, Loc.Get("Syncshell.Banlist.Refresh")))
                    {
                        _bannedUsersTask = ApiController.GroupGetBannedUsers(groupDto);
                    }
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
                using (ImRaii.Disabled(_passwordChangeTask != null))
                {
                    if (ImGui.Button("Change password"))
                    {
                        _passwordChangeTask = ApiController.GroupChangePassword(new(groupDto.Group, _newSyncShellPassword));
                        _newSyncShellPassword = string.Empty;
                    }
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
                    ThemedSlider.Int("Amount##bulkinvites", ref _bulkInviteCount, 1, 100);
                    using (ImRaii.Disabled(_bulkInvitesTask != null))
                    {
                        if (_uiShared.IconTextButton(FontAwesomeIcon.MailBulk, "Create invites"))
                        {
                            _bulkInvitesTask = ApiController.GroupCreateTempInvite(groupDto, _bulkInviteCount);
                        }
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
                    if (GetOrCreateDrawPair(groupDto, pair) is not { } drawPair) continue;

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
        var barbuttonSize = _uiShared.GetIconButtonSize(FontAwesomeIcon.EllipsisH);
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
            _mainUi.Mediator.Publish(new GroupWidePauseMessage(groupDto.Group, groupDto.GroupUserPermissions, ApiController.UID));
        }
        UiSharedService.AttachToolTip((groupDto.GroupUserPermissions.IsPaused() ? "Resume" : "Pause") + " pairing with all users in this Syncshell");
        ImGui.SameLine();

        ImGui.SetCursorPosY(buttonLineY);
        if (_uiShared.IconButton(FontAwesomeIcon.EllipsisH))
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
            DrawGroupTargetSoundCombo(groupDto);

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

    private void DrawGroupTargetSoundCombo(GroupFullInfoDto groupDto)
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

        float scale = ImGuiHelpers.GlobalScale;
        float cardSpacing = 6f * scale;
        float pad = 10f * scale;
        float tileSize = 48f * scale;
        float cardHeight = tileSize + pad * 2f;
        float rounding = 8f * scale;
        float buttonSize = 24f * scale;
        float buttonSpacing = 4f * scale;
        var drawList = ImGui.GetWindowDrawList();
        var viewTop = ImGui.GetWindowPos().Y;
        var viewBottom = viewTop + ImGui.GetWindowHeight();

        foreach (var entry in groups)
        {
            var groupDto = entry.Key;
            var pairsInGroup = entry.Value;
            var cardMin = ImGui.GetCursorScreenPos();
            float cardWidth = ImGui.GetContentRegionAvail().X;
            var cardMax = cardMin + new Vector2(cardWidth, cardHeight);

            // Hors de la zone visible : on réserve la place sans rien dessiner.
            if (cardMax.Y < viewTop || cardMin.Y > viewBottom)
            {
                ImGui.Dummy(new Vector2(cardWidth, cardHeight + cardSpacing));
                continue;
            }

            var groupName = _serverConfigurationManager.GetNoteForGid(groupDto.GID);
            if (string.IsNullOrEmpty(groupName))
                groupName = groupDto.Group.Alias ?? groupDto.GID;

            int totalMembers = pairsInGroup.Count + 1;
            int connectedMembers = pairsInGroup.Count(p => p.IsOnline) + 1;
            int visibleMembers = pairsInGroup.Count(p => p.IsVisible);
            int maxMembers = groupDto.MaxUserCount > 0 ? groupDto.MaxUserCount : ApiController.ServerInfo.MaxGroupUserCount;

            bool isPaused = groupDto.GroupUserPermissions.IsPaused();
            bool isActiveSlot = string.Equals(_slotService.ActiveSlotGid, groupDto.GID, StringComparison.Ordinal);
            bool isSlotLeaving = isActiveSlot && _slotService.IsLeaveTimerRunning;
            bool isOwner = string.Equals(groupDto.OwnerUID, ApiController.UID, StringComparison.Ordinal);
            bool isModerator = groupDto.GroupUserInfo.IsModerator();
            bool isAdmin = isOwner || isModerator;
            bool isFavorite = favorites.Contains(groupDto.GID);

            var pausedColor = ImGuiColors.DalamudOrange;
            var accent = UiSharedService.AccentColor;
            var shellProfile = GetShellProfile(groupDto);
            var tint = GetSyncshellColor(groupDto.GID, shellProfile);
            var slotActiveColor = new Vector4(0.3f, 0.85f, 0.3f, 0.9f);
            var slotLeavingColor = new Vector4(0.9f, 0.25f, 0.25f, 0.9f);

            // Zone cliquable de toute la carte : un clic ouvre la liste des membres.
            bool cardClicked = ImGui.InvisibleButton($"##card-{groupDto.GID}", new Vector2(cardWidth, cardHeight));
            ImGui.SetItemAllowOverlap();
            bool cardHovered = ImGui.IsItemHovered();
            if (cardHovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (cardClicked)
                _membersWindowGid = string.Equals(_membersWindowGid, groupDto.GID, StringComparison.Ordinal) ? null : groupDto.GID;
            if (cardClicked && string.Equals(_membersWindowGid, groupDto.GID, StringComparison.Ordinal))
                _membersFilter = string.Empty;

            // Pas d'infobulle de carte au-dessus des boutons d'action : ils ont la leur, et les deux se superposaient.
            float actionsZoneStart = cardMax.X - pad * 2f - (buttonSize * 4 + buttonSpacing * 3);
            if (cardHovered && ImGui.GetMousePos().X < actionsZoneStart)
                DrawSyncshellTooltip(groupDto, groupName, connectedMembers, totalMembers, isPaused);

            var borderColor = isSlotLeaving ? slotLeavingColor
                : isActiveSlot ? slotActiveColor
                : isPaused ? pausedColor with { W = 0.8f }
                : UiSharedService.ThemeCardBorder;
            drawList.AddRectFilled(cardMin, cardMax, ImGui.GetColorU32(UiSharedService.ThemeHeaderBg with { W = cardHovered ? 1f : 0.9f }), rounding);
            drawList.AddRectFilled(cardMin, cardMax, ImGui.GetColorU32(accent with { W = cardHovered ? 0.14f : 0.07f }), rounding);
            drawList.AddRect(cardMin, cardMax, ImGui.GetColorU32(borderColor), rounding, ImDrawFlags.None, 1.5f * scale);

            // Pastille d'initiales, teintée d'après le nom : stable d'une session à l'autre.
            var tileMin = cardMin + new Vector2(pad, pad);
            var tileMax = tileMin + new Vector2(tileSize);
            float tileRounding = 10f * scale;
            drawList.AddRectFilled(tileMin, tileMax, ImGui.GetColorU32(tint with { W = isPaused ? 0.12f : 0.22f }), tileRounding);
            drawList.AddRect(tileMin, tileMax, ImGui.GetColorU32(tint with { W = isPaused ? 0.35f : 0.65f }), tileRounding, ImDrawFlags.None, scale);
            var icon = GetSyncshellIcon(groupDto.GID, shellProfile);
            if (icon != null && icon.Handle != IntPtr.Zero)
            {
                drawList.AddImageRounded(icon.Handle, tileMin, tileMax, Vector2.Zero, Vector2.One,
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, isPaused ? 0.5f : 1f)), tileRounding);
                drawList.AddRect(tileMin, tileMax, ImGui.GetColorU32(tint with { W = isPaused ? 0.35f : 0.65f }), tileRounding, ImDrawFlags.None, scale);
            }
            else
            {
                // Pas (encore) d'image de profil : les initiales font office d'icône.
                var initials = GetSyncshellInitials(groupName);
                using (_uiShared.UidFont.Push())
                {
                    var initialsSize = ImGui.CalcTextSize(initials);
                    drawList.AddText(tileMin + (new Vector2(tileSize) - initialsSize) / 2f,
                        ImGui.GetColorU32(isPaused ? ImGuiColors.DalamudGrey : tint with { W = 1f }), initials);
                }
            }

            if (isActiveSlot)
            {
                var dotColor = isSlotLeaving ? slotLeavingColor with { W = 1f } : slotActiveColor with { W = 1f };
                float dotRadius = 5f * scale;
                var dotCenter = new Vector2(tileMax.X - dotRadius * 0.6f, tileMin.Y + dotRadius * 0.6f);
                drawList.AddCircleFilled(dotCenter, dotRadius, ImGui.GetColorU32(dotColor));
                drawList.AddCircle(dotCenter, dotRadius, ImGui.GetColorU32(UiSharedService.ThemeHeaderBg), 0, 1.5f * scale);
                ImGui.SetCursorScreenPos(dotCenter - new Vector2(dotRadius + 2, dotRadius + 2));
                using (ImRaii.PushId($"slot-indicator-{groupDto.GID}"))
                {
                    ImGui.InvisibleButton("##slotDot", new Vector2((dotRadius + 2) * 2, (dotRadius + 2) * 2));
                    UiSharedService.AttachToolTip(Loc.Get(isSlotLeaving ? "Syncshell.Cards.LeavingSlotZone" : "Syncshell.Cards.InSlotZone"));
                }
            }

            // Actions à droite, centrées verticalement : favori, pause, membres, menu.
            int actionCount = 4;
            float actionsWidth = buttonSize * actionCount + buttonSpacing * (actionCount - 1);
            float actionsX = cardMax.X - pad - actionsWidth;
            float actionsY = cardMin.Y + (cardHeight - buttonSize) / 2f;

            DrawSyncshellActionButton($"fav-{groupDto.GID}", FontAwesomeIcon.Star, new Vector2(actionsX, actionsY), buttonSize,
                isFavorite ? ImGuiColors.ParsedGold : Vector4.One,
                Loc.Get(isFavorite ? "Syncshell.Cards.RemoveFavorite" : "Syncshell.Cards.AddFavorite"), () =>
                {
                    if (isFavorite) favorites.Remove(groupDto.GID);
                    else favorites.Add(groupDto.GID);
                    _syncshellConfig.Save();
                });

            var pauseActionText = isPaused ? Loc.Get("Syncshell.Cards.Resume") : Loc.Get("Syncshell.Cards.Pause");
            DrawSyncshellActionButton($"pause-{groupDto.GID}", isPaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause,
                new Vector2(actionsX + (buttonSize + buttonSpacing), actionsY), buttonSize,
                isPaused ? pausedColor : Vector4.One,
                string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Cards.PauseTooltip"), pauseActionText), () =>
                {
                    var userPerm = groupDto.GroupUserPermissions ^ GroupUserPermissions.Paused;
                    _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(groupDto.Group, new UserData(ApiController.UID), userPerm));
                });

            bool membersOpen = string.Equals(_membersWindowGid, groupDto.GID, StringComparison.Ordinal);
            DrawSyncshellActionButton($"members-{groupDto.GID}", FontAwesomeIcon.Users,
                new Vector2(actionsX + (buttonSize + buttonSpacing) * 2, actionsY), buttonSize,
                membersOpen ? accent : Vector4.One, Loc.Get("Syncshell.Cards.ShowMembers"), () =>
                {
                    if (membersOpen)
                    {
                        _membersWindowGid = null;
                    }
                    else
                    {
                        _membersWindowGid = groupDto.GID;
                        _membersFilter = string.Empty;
                    }
                });

            var menuId = $"syncshell-menu-{groupDto.GID}";
            if (DrawSyncshellActionButton($"menu-{groupDto.GID}", FontAwesomeIcon.EllipsisH,
                    new Vector2(actionsX + (buttonSize + buttonSpacing) * 3, actionsY), buttonSize,
                    Vector4.One, Loc.Get("Syncshell.Cards.Menu")))
                ImGui.OpenPopup(menuId);
            DrawSyncshellMenu(menuId, groupDto, groupName, totalMembers, isAdmin);

            float textX = tileMax.X + pad;
            float textWidth = MathF.Max(40f * scale, actionsX - textX - pad);
            float lineHeight = ImGui.GetTextLineHeight();
            float barHeight = 5f * scale;
            float blockHeight = lineHeight * 2f + barHeight + 8f * scale;
            float textY = cardMin.Y + (cardHeight - blockHeight) / 2f;
            string? roleLabel = isOwner ? Loc.Get("GroupPair.Owner") : isModerator ? Loc.Get("GroupPair.Moderator") : null;
            float roleWidth = roleLabel == null ? 0f : ImGui.CalcTextSize(roleLabel).X + 12f * scale;
            float nameMax = textWidth - (roleLabel != null && textWidth > 200f * scale ? roleWidth + 6f * scale : 0f);
            var shownName = UiSharedService.TruncateToWidth(groupName, nameMax);
            var nameColor = isPaused ? pausedColor : UiSharedService.ThemeTextAccent;
            drawList.AddText(new Vector2(textX, textY), ImGui.GetColorU32(nameColor), shownName);
            if (roleLabel != null && textWidth > 200f * scale)
            {
                var nameSize = ImGui.CalcTextSize(shownName);
                var pillMin = new Vector2(textX + nameSize.X + 8f * scale, textY);
                var pillMax = pillMin + new Vector2(roleWidth, lineHeight);
                drawList.AddRectFilled(pillMin, pillMax, ImGui.GetColorU32(accent with { W = 0.25f }), lineHeight / 2f);
                drawList.AddText(pillMin + new Vector2(6f * scale, 0f), ImGui.GetColorU32(Vector4.One with { W = 0.9f }), roleLabel);
            }

            string details = $"{totalMembers}/{maxMembers} · " + string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Cards.DetailsOnline"), connectedMembers);
            if (visibleMembers > 0)
                details += " · " + string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Cards.DetailsVisible"), visibleMembers);
            if (isPaused)
                details = Loc.Get("Syncshell.Cards.Paused") + " · " + details;
            drawList.AddText(new Vector2(textX, textY + lineHeight + 3f * scale),
                ImGui.GetColorU32(isPaused ? pausedColor with { W = 0.9f } : ImGuiColors.DalamudGrey), UiSharedService.TruncateToWidth(details, textWidth));

            float barWidth = MathF.Min(textWidth, 170f * scale);
            float barY = textY + lineHeight * 2f + 8f * scale;
            float fill = maxMembers > 0 ? Math.Clamp(totalMembers / (float)maxMembers, 0f, 1f) : 0f;
            drawList.AddRectFilled(new Vector2(textX, barY), new Vector2(textX + barWidth, barY + barHeight),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.08f)), barHeight / 2f);
            if (fill > 0f)
                drawList.AddRectFilled(new Vector2(textX, barY), new Vector2(textX + MathF.Max(barHeight, barWidth * fill), barY + barHeight),
                    ImGui.GetColorU32(fill > 0.9f ? ImGuiColors.DalamudOrange : tint with { W = 0.9f }), barHeight / 2f);

            DrawDisabledPermissionBadges(groupDto, new Vector2(textX + barWidth + 10f * scale, barY + barHeight / 2f), textX + textWidth);

            ImGui.SetCursorScreenPos(new Vector2(cardMin.X, cardMax.Y + cardSpacing));
        }

        // Inline members for favorite syncshells
        DrawFavoriteMembersInline(groups, favorites);

        DrawMembersWindow();
        DrawProfileWindow();
    }


    private GroupProfileDto? GetShellProfile(GroupFullInfoDto groupDto)
    {
        var cached = _profileManager.GetGroupProfile(groupDto.GID);
        if (cached != null) return cached;

        if (DateTime.UtcNow - _lastShellProfileRequestUtc < TimeSpan.FromMilliseconds(400)) return null;
        if (!_shellProfileRequests.Add(groupDto.GID)) return null;
        _lastShellProfileRequestUtc = DateTime.UtcNow;
        _ = FetchShellProfileAsync(groupDto);
        return null;
    }

    private async Task FetchShellProfileAsync(GroupFullInfoDto groupDto)
    {
        try
        {
            var profile = await ApiController.GroupGetProfile(new GroupDto(groupDto.Group)).ConfigureAwait(false);
            if (profile != null)
                _profileManager.SetGroupProfile(groupDto.GID, profile);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Profil de syncshell indisponible pour {gid}", groupDto.GID);
        }
    }

    private IDalamudTextureWrap? GetSyncshellIcon(string gid, GroupProfileDto? profile)
    {
        // Une syncshell NSFW n'affiche pas son image tant que l'utilisateur n'a pas choisi de les voir.
        string? source = profile is { IsDisabled: false } && !(profile.IsNsfw && !_mareConfig.Current.ProfilesAllowNsfw)
            ? profile.ProfileImageBase64
            : null;

        if (string.IsNullOrEmpty(source))
        {
            if (_shellIconTasks.Remove(gid, out var stale))
                stale.Task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); }, TaskScheduler.Default);
            return null;
        }

        if (_shellIconTasks.TryGetValue(gid, out var entry) && string.Equals(entry.Source, source, StringComparison.Ordinal))
            return entry.Task.IsCompletedSuccessfully ? entry.Task.Result : null;

        // Nouvelle image (première fois, ou le propriétaire vient de la changer) : on remplace l'ancienne.
        if (entry.Task != null)
            entry.Task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); }, TaskScheduler.Default);
        _shellIconTasks[gid] = (source, LoadSyncshellIconAsync(source));
        return null;
    }

    private async Task<IDalamudTextureWrap?> LoadSyncshellIconAsync(string imageBase64)
    {
        try
        {
            return await _uiShared.LoadImageAsync(Convert.FromBase64String(imageBase64)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Image de syncshell illisible");
            return null;
        }
    }

    private static Vector4 GetSyncshellColor(string gid, GroupProfileDto? profile)
        => ColorSwatchPicker.TryParse(profile?.BorderColor, out var chosen) ? chosen : GetSyncshellTint(gid);

    private static Vector4 GetSyncshellTint(string gid)
    {
        uint hash = 2166136261;
        foreach (var c in gid)
            hash = (hash ^ c) * 16777619;
        float hue = (hash % 360) / 360f;
        float r = 0f, g = 0f, b = 0f;
        ImGui.ColorConvertHSVtoRGB(hue, 0.5f, 0.95f, ref r, ref g, ref b);
        return new Vector4(r, g, b, 1f);
    }

    private static string GetSyncshellInitials(string name)
    {
        var parts = name.Split([' ', '-', '\'', '_', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var words = parts.Where(p => p.Any(char.IsLetterOrDigit)).ToArray();
        if (words.Length == 0) return "?";
        if (words.Length == 1)
        {
            var letters = words[0].Where(char.IsLetterOrDigit).Take(2).ToArray();
            return new string(letters).ToUpperInvariant();
        }

        return new string(words.Take(2).Select(w => char.ToUpperInvariant(w.First(char.IsLetterOrDigit))).ToArray());
    }

    private bool DrawSyncshellActionButton(string id, FontAwesomeIcon icon, Vector2 screenPos, float size, Vector4 iconColor, string tooltip, Action? onClick = null)
    {
        bool clicked = false;
        ImGui.SetCursorScreenPos(screenPos);
        using (ImRaii.PushId(id))
        {
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.2f, 0.25f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.35f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.3f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.Text, iconColor))
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 6f * ImGuiHelpers.GlobalScale))
            {
                if (_uiShared.IconButtonCentered(icon, size, square: true))
                {
                    clicked = true;
                    onClick?.Invoke();
                }
            }
            UiSharedService.AttachToolTip(tooltip);
        }

        return clicked;
    }

    private void DrawSyncshellTooltip(GroupFullInfoDto groupDto, string groupName, int connectedMembers, int totalMembers, bool isPaused)
    {
        using var tooltipStyle = UiSharedService.PushTooltipStyle();
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(groupName);
        ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Cards.OnlineMembers"), connectedMembers, totalMembers));
        var capacity = groupDto.MaxUserCount > 0 ? groupDto.MaxUserCount : ApiController.ServerInfo.MaxGroupUserCount;
        ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Cards.MaxCapacity"), capacity));
        if (!string.IsNullOrEmpty(groupDto.Group.Alias) && !string.Equals(groupDto.Group.Alias, groupName, StringComparison.Ordinal))
            ImGui.TextUnformatted($"ID: {groupDto.GID}");
        if (isPaused)
            UiSharedService.ColorText(Loc.Get("Syncshell.Cards.Paused"), ImGuiColors.DalamudOrange);
        ImGui.EndTooltip();
    }

    private void DrawSyncshellMenu(string menuId, GroupFullInfoDto groupDto, string groupName, int totalMembers, bool isAdmin)
    {
        using var style = PopupMenu.PushStyle();
        if (!ImGui.BeginPopup(menuId)) return;

        if (PopupMenu.Row(FontAwesomeIcon.IdCard, Loc.Get("Syncshell.Cards.ShowProfile"), $"{menuId}-profile"))
        {
            ToggleGroupProfileWindow(groupDto);
            ImGui.CloseCurrentPopup();
        }

        if (isAdmin && PopupMenu.Row(FontAwesomeIcon.Crown, Loc.Get("Syncshell.Cards.OpenAdmin"), $"{menuId}-admin"))
        {
            _mainUi.Mediator.Publish(new OpenSyncshellAdminPanel(groupDto));
            ImGui.CloseCurrentPopup();
        }

        PopupMenu.Section(Loc.Get("Syncshell.Cards.Perm.Header"));
        var perm = groupDto.GroupUserPermissions;
        if (PopupMenu.ToggleRow(FontAwesomeIcon.VolumeUp, Loc.Get("Syncshell.Cards.Perm.Sound"), !perm.IsDisableSounds(), $"{menuId}-sound"))
            ToggleShellPermission(groupDto, ShellPermission.Sound, groupName, totalMembers);
        if (PopupMenu.ToggleRow(FontAwesomeIcon.Running, Loc.Get("Syncshell.Cards.Perm.Anim"), !perm.IsDisableAnimations(), $"{menuId}-anim"))
            ToggleShellPermission(groupDto, ShellPermission.Animations, groupName, totalMembers);
        if (PopupMenu.ToggleRow(FontAwesomeIcon.Sun, Loc.Get("Syncshell.Cards.Perm.Vfx"), !perm.IsDisableVFX(), $"{menuId}-vfx"))
            ToggleShellPermission(groupDto, ShellPermission.Vfx, groupName, totalMembers);
        if (PopupMenu.ToggleRow(FontAwesomeIcon.Home, Loc.Get("Syncshell.Cards.Perm.Housing"), !perm.IsDisableHousing(), $"{menuId}-housing"))
            ToggleShellPermission(groupDto, ShellPermission.Housing, groupName, totalMembers);

        ImGui.EndPopup();
    }

    private enum ShellPermission { Sound, Animations, Vfx, Housing }

    private void ToggleShellPermission(GroupFullInfoDto groupDto, ShellPermission kind, string groupName, int totalMembers)
    {
        var perm = groupDto.GroupUserPermissions;
        bool newState;
        string notificationKey;
        switch (kind)
        {
            case ShellPermission.Sound:
                newState = !perm.IsDisableSounds();
                perm.SetDisableSounds(newState);
                _mainUi.Mediator.Publish(new GroupSyncOverrideChanged(groupDto.Group.GID, perm.IsDisableSounds(), null, null));
                notificationKey = newState ? "Syncshell.Cards.Notification.SoundDisabled" : "Syncshell.Cards.Notification.SoundEnabled";
                break;
            case ShellPermission.Animations:
                newState = !perm.IsDisableAnimations();
                perm.SetDisableAnimations(newState);
                _mainUi.Mediator.Publish(new GroupSyncOverrideChanged(groupDto.Group.GID, null, perm.IsDisableAnimations(), null));
                notificationKey = newState ? "Syncshell.Cards.Notification.AnimDisabled" : "Syncshell.Cards.Notification.AnimEnabled";
                break;
            case ShellPermission.Vfx:
                newState = !perm.IsDisableVFX();
                perm.SetDisableVFX(newState);
                _mainUi.Mediator.Publish(new GroupSyncOverrideChanged(groupDto.Group.GID, null, null, perm.IsDisableVFX()));
                notificationKey = newState ? "Syncshell.Cards.Notification.VfxDisabled" : "Syncshell.Cards.Notification.VfxEnabled";
                break;
            default:
                newState = !perm.IsDisableHousing();
                perm.SetDisableHousing(newState);
                _mainUi.Mediator.Publish(new GroupSyncOverrideChanged(groupDto.Group.GID, null, null, null, perm.IsDisableHousing()));
                notificationKey = newState ? "Syncshell.Cards.Notification.HousingDisabled" : "Syncshell.Cards.Notification.HousingEnabled";
                break;
        }

        _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(groupDto.Group, new UserData(ApiController.UID), perm));
        var notifTitle = string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Cards.Notification.Title"), groupName);
        var notifBody = string.Format(CultureInfo.CurrentCulture, Loc.Get(notificationKey), totalMembers);
        _mainUi.Mediator.Publish(new DualNotificationMessage(notifTitle, notifBody, NotificationType.Success));
    }

    private void DrawDisabledPermissionBadges(GroupFullInfoDto groupDto, Vector2 centerLeft, float maxX)
    {
        var perm = groupDto.GroupUserPermissions;
        var disabled = new List<FontAwesomeIcon>(4);
        if (perm.IsDisableSounds()) disabled.Add(FontAwesomeIcon.VolumeMute);
        if (perm.IsDisableAnimations()) disabled.Add(FontAwesomeIcon.Running);
        if (perm.IsDisableVFX()) disabled.Add(FontAwesomeIcon.Sun);
        if (perm.IsDisableHousing()) disabled.Add(FontAwesomeIcon.Home);
        if (disabled.Count == 0) return;

        var dl = ImGui.GetWindowDrawList();
        var x = centerLeft.X;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            foreach (var icon in disabled)
            {
                var text = icon.ToIconString();
                var size = ImGui.CalcTextSize(text);
                if (x + size.X > maxX) break;
                dl.AddText(new Vector2(x, centerLeft.Y - size.Y / 2f), ImGui.GetColorU32(ImGuiColors.DalamudRed with { W = 0.85f }), text);
                x += size.X + 6f * ImGuiHelpers.GlobalScale;
            }
        }
    }

    private void ToggleGroupProfileWindow(GroupFullInfoDto groupDto)
    {
        bool closing = string.Equals(_profileWindowGid, groupDto.GID, StringComparison.Ordinal);
        _profileTexture?.Dispose();
        _profileTexture = null;
        _bannerTexture?.Dispose();
        _bannerTexture = null;
        _currentProfile = null;
        if (closing)
        {
            _profileWindowGid = null;
            return;
        }

        _profileWindowGid = groupDto.GID;
        _profileLoading = true;
        _ = LoadGroupProfileAsync(groupDto);
    }

    private void DrawFavoriteMembersInline(List<KeyValuePair<GroupFullInfoDto, List<Pair>>> groups, HashSet<string> favorites)
    {
        var favoriteGroups = groups.Where(g => favorites.Contains(g.Key.GID)).ToList();
        if (favoriteGroups.Count == 0) return;

        ImGuiHelpers.ScaledDummy(8f);

        // Separator violet between cards and favorite members
        var separatorColor = UiSharedService.ThemeCardBorder;
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
            UiSharedService.ColorText(headerLabel, UiSharedService.ThemeTextAccent);

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

    /// <summary>
    /// Le tri complet des membres (clés sur un dictionnaire à clé record, plus allocation des clés de
    /// tri) refait à chaque frame pour des centaines de membres faisait dépasser la frame à l'ouverture
    /// de la liste des syncshells. Le résultat est donc gardé une seconde ; l'état visible/en ligne,
    /// lui, reste lu en direct au moment de répartir les sections.
    /// </summary>
    private List<DrawGroupPair> BuildDrawPairs(GroupFullInfoDto groupDto, IEnumerable<Pair> pairs, string cacheScope)
    {
        var key = groupDto.GID + "|" + cacheScope;
        var now = Environment.TickCount64;
        if (_drawPairsCache.TryGetValue(key, out var cached) && now - cached.Tick < DrawPairsCacheMs)
            return cached.Pairs;

        var built = BuildDrawPairsUncached(groupDto, pairs);
        _drawPairsCache[key] = (now, built);
        return built;
    }

    private List<DrawGroupPair> BuildDrawPairsUncached(GroupFullInfoDto groupDto, IEnumerable<Pair> pairs)
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
            if (GetOrCreateDrawPair(groupDto, pair) is { } drawPair) result.Add(drawPair);
        }
        return result;
    }

    /// <summary>
    /// Ligne d'affichage d'un membre, prise au cache ou construite. Le cache évite de reconstruire
    /// un <see cref="DrawGroupPair"/> à chaque frame. Renvoie null quand le membre n'appartient
    /// finalement pas à cette syncshell.
    /// </summary>
    private DrawGroupPair? GetOrCreateDrawPair(GroupFullInfoDto groupDto, Pair pair)
    {
        var cacheKey = groupDto.GID + pair.UserData.UID;
        var groupPairFullInfoDto = pair.GroupPair.FirstOrDefault(
            g => string.Equals(g.Key.Group.GID, groupDto.GID, StringComparison.Ordinal)
        ).Value;

        if (groupPairFullInfoDto == null) return null;

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

        return drawPair;
    }

    private void DrawMembersList(GroupFullInfoDto groupDto, List<Pair> pairsInGroup)
    {
        var drawPairs = BuildDrawPairs(groupDto, pairsInGroup, "fav");

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
        => DrawMembersSections(drawPairs, gid, prefix, (users, _) => UidDisplayHandler.RenderPairList(users));

    private void DrawMembersListByType(List<DrawGroupPair> drawPairs, string gid, string prefix)
        => DrawMembersSections(drawPairs, gid, prefix, (users, sectionPrefix) => DrawTypeSubSections(users, gid, sectionPrefix));
    
    private void DrawMembersSections(List<DrawGroupPair> drawPairs, string gid, string prefix,
        Action<List<DrawGroupPair>, string> renderSection)
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
            if (DrawMemberSection(gid, $"{prefix}-visible", defaultExpanded: true,
                    string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.VisibleSection"), visibleUsers.Count),
                    new Vector4(0.4f, 0.75f, 1f, 1f), needsSpacer))
            {
                renderSection(visibleUsers, $"{prefix}-visible");
            }
            needsSpacer = true;
        }

        if (onlineUsers.Count > 0)
        {
            if (DrawMemberSection(gid, $"{prefix}-online", defaultExpanded: true,
                    string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.OnlineSection"), onlineUsers.Count),
                    new Vector4(0.4f, 0.9f, 0.4f, 1f), needsSpacer))
            {
                renderSection(onlineUsers, $"{prefix}-online");
            }
            needsSpacer = true;
        }

        if (offlineUsers.Count > 0)
        {
            bool offlineExpanded = DrawMemberSection(gid, $"{prefix}-offline", defaultExpanded: false,
                string.Format(CultureInfo.CurrentCulture, Loc.Get("Syncshell.Members.OfflineSection"), offlineUsers.Count),
                ImGuiColors.DalamudGrey, needsSpacer);

            // Au-delà du millier, la liste coûte plus cher à dessiner qu'elle n'apporte.
            if (offlineExpanded && offlineUsers.Count > 1000)
                UiSharedService.ColorText($"    {offlineUsers.Count} offline users omitted from display.", ImGuiColors.DalamudGrey);
            else if (offlineExpanded)
                renderSection(offlineUsers, $"{prefix}-offline");
        }
    }

    /// <summary>En-tête repliable d'une section de membres. Renvoie vrai si le contenu doit être dessiné.</summary>
    private bool DrawMemberSection(string gid, string sectionPrefix, bool defaultExpanded,
        string label, Vector4 color, bool needsSpacer)
    {
        if (needsSpacer) ImGuiHelpers.ScaledDummy(8f);

        var key = gid + "-" + sectionPrefix;
        if (!_favoriteMembersExpanded.ContainsKey(key)) _favoriteMembersExpanded[key] = defaultExpanded;

        var expanded = _favoriteMembersExpanded[key];
        DrawMembersSectionHeader(label, color, ref expanded, $"##{sectionPrefix}-{gid}");
        _favoriteMembersExpanded[key] = expanded;

        return expanded;
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
            UiSharedService.DrawWindowGlass();
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

            var drawPairs = BuildDrawPairs(groupDto, filteredPairs, "members|" + _membersFilter);

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
            UiSharedService.DrawWindowGlass();
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