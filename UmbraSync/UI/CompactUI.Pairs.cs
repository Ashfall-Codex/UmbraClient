using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.User;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.UI.Components;

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
                _ = AddPairByUidAsync(_pairToAdd);
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

        // UserPair peut être null le temps d'une frame : DirectPairs est un Lazy recréé de façon
        // débouncée, et une rupture de paire vide UserPair avant ce rafraîchissement. La liste tenue
        // par l'interface contient alors encore l'entrée, sans ses permissions.
        var pairedUsers = users.Where(u => u.UserPair != null).ToList();
        var pausedUsers = pairedUsers.Where(u => u.UserPair!.OwnPermissions.IsPaused() && u.UserPair.OtherPermissions.IsPaired()).ToList();
        var resumedUsers = pairedUsers.Where(u => !u.UserPair!.OwnPermissions.IsPaused() && u.UserPair.OtherPermissions.IsPaired()).ToList();

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
                    if (entry.UserPair is not { } userPair) continue;
                    var perm = userPair.OwnPermissions;
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
        var allUsers = GetFilteredUsers().OrderBy(u => _uidDisplayHandler.GetSortName(u), UI.Handlers.UidDisplayHandler.NameComparer).ToList();
        var visibleUsersSource = allUsers.Where(u => u.IsVisible).ToList();
        var nonVisibleUsers = allUsers.Where(u => !u.IsVisible).ToList();
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

        // Même raison qu'au-dessus : une paire rompue reste dans la liste jusqu'au prochain
        // rafraîchissement, mais n'a plus de permissions à interroger.
        var pairedNonVisible = nonVisibleUsers.Where(u => u.UserPair != null).ToList();

        var onlineUsers = pairedNonVisible.Where(u => u.UserPair!.OtherPermissions.IsPaired() && (u.IsOnline || u.UserPair!.OwnPermissions.IsPaused()))
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

        var offlineUsers = pairedNonVisible.Where(u => !u.UserPair!.OtherPermissions.IsPaired() || (!u.IsOnline && !u.UserPair!.OwnPermissions.IsPaused()))
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

        _pairGroupsUi.Draw(visibleUsers, onlineUsers, offlineUsers);

        ImGui.EndChild();
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

    private async Task AddPairByUidAsync(string uidOrAlias)
    {
        try
        {
            await _apiController.UserAddPair(new UserDto(new(uidOrAlias))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adding pair from the main window failed");
            Mediator.Publish(new NotificationMessage(Loc.Get("Notification.Nearby.Failed.Title"), Loc.Get("Notification.AddPair.Failed.Body"), NotificationType.Warning));
        }
    }
}
