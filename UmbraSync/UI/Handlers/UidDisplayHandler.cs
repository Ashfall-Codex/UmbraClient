using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI.Components;

namespace UmbraSync.UI.Handlers;

public class UidDisplayHandler
{
    private readonly MareConfigService _mareConfigService;
    private readonly MareMediator _mediator;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverManager;
    private readonly Dictionary<string, bool> _showUidForEntry = new(StringComparer.Ordinal);
    private string _editNickEntry = string.Empty;
    private string _editUserComment = string.Empty;
    private string _lastMouseOverUid = string.Empty;
    private bool _popupShown = false;
    private DateTime? _popupTime;

    public UidDisplayHandler(MareMediator mediator, PairManager pairManager,
        ServerConfigurationManager serverManager, MareConfigService mareConfigService)
    {
        _mediator = mediator;
        _pairManager = pairManager;
        _serverManager = serverManager;
        _mareConfigService = mareConfigService;
    }

    public static void RenderPairList(IEnumerable<DrawPairBase> pairs)
    {
        var textHeight = ImGui.GetFontSize();
        var style = ImGui.GetStyle();
        var framePadding = style.FramePadding;
        var spacing = style.ItemSpacing;
        var lineHeight = textHeight + framePadding.Y * 2 + spacing.Y;
        var startY = ImGui.GetCursorStartPos().Y;
        var cursorY = ImGui.GetCursorPosY();
        var contentHeight = UiSharedService.GetWindowContentRegionHeight();

        foreach (var entry in pairs)
        {
            if ((startY + cursorY) < -lineHeight || (startY + cursorY) > contentHeight)
            {
                cursorY += lineHeight;
                ImGui.SetCursorPosY(cursorY);
                continue;
            }

            using (ImRaii.PushId(entry.ImGuiID)) entry.DrawPairedClient();
            cursorY += lineHeight;
        }
    }

    public void DrawPairText(string id, Pair pair, float textPosX, float originalY, Func<float> editBoxWidth)
    {
        ImGui.SetCursorPosX(textPosX);
        (bool textIsUid, string playerText) = GetPlayerText(pair);
        if (!string.Equals(_editNickEntry, pair.UserData.UID, StringComparison.Ordinal))
        {
            ImGui.SetCursorPosY(originalY);

            using (ImRaii.PushFont(UiBuilder.MonoFont, textIsUid)) ImGui.TextUnformatted(playerText);

            if (ImGui.IsItemHovered())
            {
                if (!string.Equals(_lastMouseOverUid, id))
                {
                    _popupTime = DateTime.UtcNow.AddSeconds(_mareConfigService.Current.ProfileDelay);
                }

                _lastMouseOverUid = id;

                if (_popupTime > DateTime.UtcNow || !_mareConfigService.Current.ProfilesShow)
                {
                    // Build tooltip; prepend last-seen when player is offline or not visible
                    string tooltip = string.Format(CultureInfo.CurrentCulture, Loc.Get("UidDisplay.Tooltip.Main"), pair.UserData.AliasOrUID);

                    if (!pair.IsOnline || !pair.IsVisible)
                    {
                        var lastSeen = _serverManager.GetNameForUid(pair.UserData.UID);
                        if (!string.IsNullOrEmpty(lastSeen))
                        {
                            tooltip = string.Format(CultureInfo.CurrentCulture, Loc.Get("UidDisplay.Tooltip.LastSeenPrefix"), lastSeen) + Environment.NewLine + tooltip;
                        }
                    }

                    ImGui.SetTooltip(tooltip);
                }
                else if (_popupTime < DateTime.UtcNow && !_popupShown)
                {
                    _popupShown = true;
                    _mediator.Publish(new ProfilePopoutToggle(pair));
                }
            }
            else
            {
                if (string.Equals(_lastMouseOverUid, id))
                {
                    _mediator.Publish(new ProfilePopoutToggle(null));
                    _lastMouseOverUid = string.Empty;
                    _popupShown = false;
                }
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                var prevState = textIsUid;
                if (_showUidForEntry.ContainsKey(pair.UserData.UID))
                {
                    prevState = _showUidForEntry[pair.UserData.UID];
                }
                _showUidForEntry[pair.UserData.UID] = !prevState;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                var nickEntryPair = _pairManager.DirectPairs.Find(p => string.Equals(p.UserData.UID, _editNickEntry, StringComparison.Ordinal));
                nickEntryPair?.SetNote(_editUserComment);
                _editUserComment = pair.GetNote() ?? string.Empty;
                _editNickEntry = pair.UserData.UID;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Middle))
            {
                _mediator.Publish(new ProfileOpenStandaloneMessage(pair));
            }
        }
        else
        {
            // Keep edit box aligned with the row's text line.
            ImGui.SetCursorPosY(originalY);

            ImGui.SetNextItemWidth(editBoxWidth.Invoke());
            if (ImGui.InputTextWithHint("##" + pair.UserData.UID, Loc.Get("UidDisplay.EditPlaceholder"), ref _editUserComment, 255, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                _serverManager.SetNoteForUid(pair.UserData.UID, _editUserComment);
                _serverManager.SaveNotes();
                _editNickEntry = string.Empty;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _editNickEntry = string.Empty;
            }
            UiSharedService.AttachToolTip(Loc.Get("UidDisplay.EditTooltip"));
        }
    }

    public (bool isUid, string text) GetPlayerText(Pair pair)
    {
        bool showUidInsteadOfName = ShowUidInsteadOfName(pair);
        if (showUidInsteadOfName)
        {
            return (true, pair.UserData.UID);
        }

        var textIsUid = true;
        string? playerText = _serverManager.GetNoteForUid(pair.UserData.UID);
        if (playerText != null)
        {
            if (string.IsNullOrEmpty(playerText))
            {
                playerText = pair.UserData.AliasOrUID;
            }
            else
            {
                textIsUid = false;
            }
        }
        else
        {
            playerText = pair.UserData.AliasOrUID;
        }

        if (_mareConfigService.Current.ShowCharacterNames && textIsUid && pair.IsOnline && pair.IsVisible)
        {
            var name = pair.PlayerName;
            if (name != null)
            {
                playerText = name;
                textIsUid = false;
                var note = pair.GetNote();
                if (note != null)
                {
                    playerText = note;
                }
            }
        }

        return (textIsUid, playerText);
    }

    internal void Clear()
    {
        _editNickEntry = string.Empty;
        _editUserComment = string.Empty;
    }

    internal void OpenProfile(Pair entry)
    {
        _mediator.Publish(new ProfileOpenStandaloneMessage(entry));
    }

    internal void OpenAnalysis(Pair entry)
    {
        _mediator.Publish(new OpenPairAnalysisWindow(entry));
    }

    private bool ShowUidInsteadOfName(Pair pair)
    {
        if (_showUidForEntry.TryGetValue(pair.UserData.UID, out var showUidInsteadOfName))
        {
            return showUidInsteadOfName;
        }

        return false;
    }
}