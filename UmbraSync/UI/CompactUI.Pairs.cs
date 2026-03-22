using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using OtterGui.Text;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.User;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.UI.Components;
using OtterGuiImGuiClip = OtterGui.ImGuiClip;

namespace UmbraSync.UI;

public partial class CompactUi
{
    private readonly Stopwatch _timeout = new();
    private bool _buttonState;
    private string _characterOrCommentFilter = string.Empty;
    private Pair? _lastAddedUser;
    private string _lastAddedUserComment = string.Empty;
    private string _pairToAdd = string.Empty;
    private int _secretKeyIdx = -1;
    private bool _showModalForUserAddition;
    private bool _nearbyOpen = true;
    private readonly Dictionary<string, DrawUserPair> _drawUserPairCache = new(StringComparer.Ordinal);

    private void DrawAddCharacter()
    {
        ImGui.Dummy(new(10));
        var keys = _serverManager.CurrentServer.SecretKeys;
        if (keys.Count > 0)
        {
            if (_secretKeyIdx == -1) _secretKeyIdx = keys.First().Key;
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("CompactUi.AddCharacter.AddCurrentWithKey")))
            {
                _serverManager.CurrentServer.Authentications.Add(new MareConfiguration.Models.Authentication()
                {
                    CharacterName = _uiSharedService.PlayerName,
                    WorldId = _uiSharedService.WorldId,
                    SecretKeyIdx = _secretKeyIdx
                });

                _serverManager.Save();

                _ = _apiController.CreateConnections();
            }

            var secretKeyLabel = $"{Loc.Get("CompactUi.AddCharacter.SecretKeyLabel")}##addCharacterSecretKey";
            _uiSharedService.DrawCombo(secretKeyLabel, keys, (f) => f.Value.FriendlyName, (f) => _secretKeyIdx = f.Key);
        }
        else
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.AddCharacter.NoSecretKeys"), ImGuiColors.DalamudYellow);
        }
    }

    private void DrawAddPair()
    {
        var style = ImGui.GetStyle();
        float buttonHeight = ImGui.GetFrameHeight() + style.FramePadding.Y * 0.5f;
        float glyphWidth;
        using (_uiSharedService.IconFont.Push())
            glyphWidth = ImGui.CalcTextSize(FontAwesomeIcon.Plus.ToIconString()).X;
        var buttonWidth = glyphWidth + style.FramePadding.X * 2f;

        var availWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(MathF.Max(0, availWidth - buttonWidth - style.ItemSpacing.X));
        ImGui.InputTextWithHint("##otheruid", Loc.Get("CompactUi.AddPair.OtherUidPlaceholder"), ref _pairToAdd, 20);
        ImGui.SameLine();
        var canAdd = !_pairManager.DirectPairs.Exists(p => string.Equals(p.UserData.UID, _pairToAdd, StringComparison.Ordinal) || string.Equals(p.UserData.Alias, _pairToAdd, StringComparison.Ordinal));
        using (ImRaii.Disabled(!canAdd))
        {
            if (_uiSharedService.IconPlusButtonCentered(height: buttonHeight))
            {
                _ = _apiController.UserAddPair(new(new(_pairToAdd)));
                _pairToAdd = string.Empty;
            }
            var target = _pairToAdd.IsNullOrEmpty() ? Loc.Get("CompactUi.AddPair.OtherUserFallback") : _pairToAdd;
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.AddPair.PairWithFormat"), target));
        }

        ImGuiHelpers.ScaledDummy(2);
    }

    private void DrawFilter()
    {
        var playButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Play);

        var users = GetFilteredUsers();
        var userCount = users.Count;

        var spacing = userCount > 0
            ? playButtonSize.X + ImGui.GetStyle().ItemSpacing.X
            : 0;

        ImGui.SetNextItemWidth(WindowContentWidth - spacing);
        ImGui.InputTextWithHint("##filter", Loc.Get("CompactUi.Filter.Placeholder"), ref _characterOrCommentFilter, 255);

        if (userCount == 0) return;

        var pausedUsers = users.Where(u => u.UserPair!.OwnPermissions.IsPaused() && u.UserPair.OtherPermissions.IsPaired()).ToList();
        var resumedUsers = users.Where(u => !u.UserPair!.OwnPermissions.IsPaused() && u.UserPair.OtherPermissions.IsPaired()).ToList();

        if (pausedUsers.Count == 0 && resumedUsers.Count == 0) return;
        ImGui.SameLine();

        switch (_buttonState)
        {
            case true when pausedUsers.Count == 0:
                _buttonState = false;
                break;

            case false when resumedUsers.Count == 0:
                _buttonState = true;
                break;

            case true:
                users = pausedUsers;
                break;

            case false:
                users = resumedUsers;
                break;
        }

        if (_timeout.ElapsedMilliseconds > 5000)
            _timeout.Reset();

        var button = _buttonState ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;

        using (ImRaii.Disabled(_timeout.IsRunning))
        {
            bool clicked = button == FontAwesomeIcon.Pause
                ? _uiSharedService.IconPauseButtonCentered(playButtonSize.Y)
                : _uiSharedService.IconButtonCentered(button, playButtonSize.Y);
            if (clicked && UiSharedService.CtrlPressed())
            {
                foreach (var entry in users)
                {
                    var perm = entry.UserPair!.OwnPermissions;
                    perm.SetPaused(!perm.IsPaused());
                    _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, perm));
                }

                _timeout.Start();
                _buttonState = !_buttonState;
            }
            if (!_timeout.IsRunning)
            {
                var action = button == FontAwesomeIcon.Play ? Loc.Get("CompactUi.Pairs.ResumeAction") : Loc.Get("CompactUi.Pairs.PauseAction");
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Pairs.MultiToggleTooltip"), action, users.Count, userCount));
            }
            else
            {
                var secondsRemaining = (5000 - _timeout.ElapsedMilliseconds) / 1000;
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Pairs.NextExecutionTooltip"), secondsRemaining));
            }
        }
    }

    private void DrawPairList()
    {
        using (ImRaii.PushId("addpair")) DrawAddPair();

        using (ImRaii.PushId("pairs")) DrawPairs();
        TransferPartHeight = ImGui.GetCursorPosY();
        using (ImRaii.PushId("filter")) DrawFilter();
    }

    private void DrawPairs()
    {
        float availableHeight = ImGui.GetContentRegionAvail().Y;
        float ySize;
        if (TransferPartHeight <= 0)
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
            ySize = (ImGui.GetWindowContentRegionMax().Y - ImGui.GetWindowContentRegionMin().Y) - TransferPartHeight - ImGui.GetCursorPosY();
        }
        var allUsers = GetFilteredUsers().OrderBy(u => u.GetPairSortKey(), StringComparer.Ordinal).ToList();
        var visibleUsersSource = allUsers.Where(u => u.IsVisible).ToList();
        var nonVisibleUsers = allUsers.Where(u => !u.IsVisible).ToList();
        var nearbyEntriesForDisplay = _configService.Current.EnableAutoDetectDiscovery
            ? GetNearbyEntriesForDisplay()
            : [];

        ImGui.BeginChild("list", new Vector2(WindowContentWidth, ySize), border: false);

        var pendingCount = _nearbyPending.Pending.Count;
        if (pendingCount > 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.AutoDetect.PendingInvitation"), ImGuiColors.DalamudYellow);
            ImGuiHelpers.ScaledDummy(4);
        }

        var visibleUsers = visibleUsersSource.Select(c =>
        {
            var cacheKey = "Visible" + c.UserData.UID;
            if (!_drawUserPairCache.TryGetValue(cacheKey, out var drawPair))
            {
                drawPair = new DrawUserPair(cacheKey, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _serverManager, _configService);
                _drawUserPairCache[cacheKey] = drawPair;
            }
            else
            {
                drawPair.UpdateData();
            }
            return drawPair;
        }).ToList();

        var onlineUsers = nonVisibleUsers.Where(u => u.UserPair!.OtherPermissions.IsPaired() && (u.IsOnline || u.UserPair!.OwnPermissions.IsPaused()))
            .Select(c =>
            {
                var cacheKey = "Online" + c.UserData.UID;
                if (!_drawUserPairCache.TryGetValue(cacheKey, out var drawPair))
                {
                    drawPair = new DrawUserPair(cacheKey, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _serverManager, _configService);
                    _drawUserPairCache[cacheKey] = drawPair;
                }
                else
                {
                    drawPair.UpdateData();
                }
                return drawPair;
            }).ToList();

        var offlineUsers = nonVisibleUsers.Where(u => !u.UserPair!.OtherPermissions.IsPaired() || (!u.IsOnline && !u.UserPair!.OwnPermissions.IsPaused()))
            .Select(c =>
            {
                var cacheKey = "Offline" + c.UserData.UID;
                if (!_drawUserPairCache.TryGetValue(cacheKey, out var drawPair))
                {
                    drawPair = new DrawUserPair(cacheKey, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _serverManager, _configService);
                    _drawUserPairCache[cacheKey] = drawPair;
                }
                else
                {
                    drawPair.UpdateData();
                }
                return drawPair;
            }).ToList();

        Action? drawVisibleExtras = null;
        if (nearbyEntriesForDisplay.Count > 0)
        {
            var entriesForExtras = nearbyEntriesForDisplay;
            drawVisibleExtras = () => DrawNearbyCard(entriesForExtras);
        }

        _pairGroupsUi.Draw(visibleUsers, onlineUsers, offlineUsers, drawVisibleExtras);

        ImGui.EndChild();
    }

    private List<Services.Mediator.NearbyEntry> GetNearbyEntriesForDisplay()
    {
        if (_nearbyEntries.Count == 0)
        {
            return [];
        }

        return _nearbyEntries
            .Where(e => e.IsMatch && e.AcceptPairRequests && !string.IsNullOrEmpty(e.Token) && !IsAlreadyPairedQuickMenu(e))
            .OrderBy(e => e.Distance)
            .ToList();
    }

    private void DrawNearbyCard(IReadOnlyList<Services.Mediator.NearbyEntry> nearbyEntries)
    {
        if (nearbyEntries.Count == 0)
        {
            return;
        }

        ImGuiHelpers.ScaledDummy(4f);
        using (ImRaii.PushId("group-Nearby"))
        {
            UiSharedService.DrawCard("nearby-card", () =>
            {
                bool nearbyState = _nearbyOpen;
                UiSharedService.DrawArrowToggle(ref nearbyState, "##nearby-toggle");
                _nearbyOpen = nearbyState;

                ImGui.SameLine(0f, 6f * ImGuiHelpers.GlobalScale);
                var onUmbra = nearbyEntries.Count;
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Nearby.Header"), onUmbra));
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    _nearbyOpen = !_nearbyOpen;
                }

                if (!_nearbyOpen)
                {
                    return;
                }

                ImGuiHelpers.ScaledDummy(4f);
                var indent = 18f * ImGuiHelpers.GlobalScale;
                ImGui.Indent(indent);
                var pending = _autoDetectRequestService.GetPendingRequestsSnapshot();
                var pendingUids = new HashSet<string>(pending.Select(p => p.Uid!).Where(s => !string.IsNullOrEmpty(s)), StringComparer.Ordinal);
                var pendingTokens = new HashSet<string>(pending.Select(p => p.Token!).Where(s => !string.IsNullOrEmpty(s)), StringComparer.Ordinal);
                var actionButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.UserPlus);
                using var table = ImUtf8.Table("nearby-table", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.BordersInnerV);
                if (table)
                {
                    ImGui.TableSetupColumn(Loc.Get("CompactUi.Nearby.Table.Name"), ImGuiTableColumnFlags.WidthStretch, 1f);
                    ImGui.TableSetupColumn(Loc.Get("CompactUi.Nearby.Table.Action"), ImGuiTableColumnFlags.WidthFixed, actionButtonSize.X);

                    var rowHeight = MathF.Max(ImGui.GetFrameHeight(), ImGui.GetTextLineHeight()) + ImGui.GetStyle().ItemSpacing.Y;
                    OtterGuiImGuiClip.ClippedDraw(nearbyEntries, e =>
                    {
                        bool alreadyPaired = false;
                        if (!string.IsNullOrEmpty(e.Uid))
                        {
                            alreadyPaired = _pairManager.DirectPairs.Any(p => string.Equals(p.UserData.UID, e.Uid, StringComparison.Ordinal));
                        }
                        bool alreadyInvited = (!string.IsNullOrEmpty(e.Uid) && pendingUids.Contains(e.Uid))
                                              || (!string.IsNullOrEmpty(e.Token) && pendingTokens.Contains(e.Token));

                        ImGui.TableNextRow();

                        ImGui.TableSetColumnIndex(0);
                        var name = e.DisplayName ?? e.Name;
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(name);
                        ImGui.TableSetColumnIndex(1);
                        var curX = ImGui.GetCursorPosX();
                        var availX = ImGui.GetContentRegionAvail().X; // width of the action column
                        ImGui.SetCursorPosX(curX + MathF.Max(0, availX - actionButtonSize.X));

                        using (ImRaii.PushId(e.Token ?? e.Uid ?? e.Name))
                        {
                            if (alreadyPaired)
                            {
                                using (ImRaii.Disabled())
                                {
                                    _uiSharedService.IconButton(FontAwesomeIcon.UserPlus);
                                }
                                UiSharedService.AttachToolTip(Loc.Get("AutoDetectUi.Nearby.Reason.Paired"));
                            }
                            else if (alreadyInvited)
                            {
                                using (ImRaii.Disabled())
                                {
                                    _uiSharedService.IconButton(FontAwesomeIcon.UserPlus);
                                }
                                UiSharedService.AttachToolTip(Loc.Get("AutoDetectUi.Nearby.Reason.AlreadyInvited"));
                            }
                            else if (_uiSharedService.IconButton(FontAwesomeIcon.UserPlus))
                            {
                                _ = _autoDetectRequestService.SendRequestAsync(e.Token!, e.Uid, e.DisplayName);
                            }
                        }
                        UiSharedService.AttachToolTip(Loc.Get("CompactUi.Nearby.InviteTooltip"));
                    }, rowHeight);
                }

                ImGui.Unindent(indent);
            }, stretchWidth: true);
        }
        ImGuiHelpers.ScaledDummy(4f);
    }

    private bool IsAlreadyPairedQuickMenu(Services.Mediator.NearbyEntry entry)
    {
        try
        {
            if (!string.IsNullOrEmpty(entry.Uid) &&
                _pairManager.DirectPairs.Any(p => string.Equals(p.UserData.UID, entry.Uid, StringComparison.Ordinal)))
            {
                return true;
            }

            var key = entry.DisplayName ?? entry.Name;
            if (string.IsNullOrEmpty(key)) return false;

            return _pairManager.DirectPairs.Any(p => string.Equals(p.UserData.AliasOrUID, key, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void DrawNewUserNoteModal()
    {
        var newUserModalTitle = Loc.Get("CompactUi.NewUserModal.Title");
        if (_configService.Current.OpenPopupOnAdd && _pairManager.LastAddedUser != null)
        {
            _lastAddedUser = _pairManager.LastAddedUser;
            _pairManager.LastAddedUser = null;
            ImGui.OpenPopup(newUserModalTitle);
            _showModalForUserAddition = true;
            _lastAddedUserComment = string.Empty;
        }

        if (ImGui.BeginPopupModal(newUserModalTitle, ref _showModalForUserAddition, UiSharedService.PopupWindowFlags))
        {
            if (_lastAddedUser == null)
            {
                _showModalForUserAddition = false;
            }
            else
            {
                UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.NewUserModal.Description"), _lastAddedUser.UserData.AliasOrUID));
                ImGui.InputTextWithHint("##noteforuser", string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.NewUserModal.NotePlaceholder"), _lastAddedUser.UserData.AliasOrUID), ref _lastAddedUserComment, 100);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("CompactUi.NewUserModal.SaveButton")))
                {
                    _serverManager.SetNoteForUid(_lastAddedUser.UserData.UID, _lastAddedUserComment);
                    _lastAddedUser = null;
                    _lastAddedUserComment = string.Empty;
                    _showModalForUserAddition = false;
                }
            }

            UiSharedService.SetScaledWindowSize(275);
            ImGui.EndPopup();
        }
    }

    private List<Pair> GetFilteredUsers()
    {
        return _pairManager.DirectPairs.Where(p =>
        {
            if (_characterOrCommentFilter.IsNullOrEmpty()) return true;
            return p.UserData.AliasOrUID.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ||
                   (p.GetNote()?.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (p.PlayerName?.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ?? false);
        }).ToList();
    }
}
