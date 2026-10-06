using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
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
    private readonly UmbraProfileManager _profileManager;
    private readonly UiSharedService _uiSharedService;
    private readonly Dictionary<string, (byte[] Data, Task<Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap> Task)> _avatarTasks = new(StringComparer.Ordinal);
    private DateTime _lastProfileRequestUtc = DateTime.MinValue;

    public UidDisplayHandler(MareMediator mediator, PairManager pairManager,
        ServerConfigurationManager serverManager, MareConfigService mareConfigService,
        UmbraProfileManager profileManager, UiSharedService uiSharedService)
    {
        _profileManager = profileManager;
        _uiSharedService = uiSharedService;
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
        UmbraProfileData? rpIdentity = null;
        if (!string.Equals(_editNickEntry, pair.UserData.UID, StringComparison.Ordinal))
        {
            ImGui.SetCursorPosY(originalY);

            if (_mareConfigService.Current.ShowRpIdentityInPairList)
            {
                rpIdentity = GetRpIdentity(pair);
                var rpName = rpIdentity == null ? null : $"{rpIdentity.RpFirstName} {rpIdentity.RpLastName}".Trim();
                // Un surnom choisi par l'utilisateur, ou l'affichage de l'UID demandé d'un clic, restent prioritaires.
                bool userChoseName = !string.IsNullOrEmpty(_serverManager.GetNoteForUid(pair.UserData.UID))
                                     || _showUidForEntry.TryGetValue(pair.UserData.UID, out var forcedUid) && forcedUid;
                if (!string.IsNullOrEmpty(rpName) && !userChoseName)
                {
                    playerText = rpName;
                    textIsUid = false;
                }

                if (rpIdentity != null && !userChoseName && !string.IsNullOrEmpty(rpName))
                {
                    var avatar = ImGui.GetFontSize() * 1.45f;
                    var screen = ImGui.GetCursorScreenPos();
                    // Le texte est remonté d'un cran par rapport aux icônes (voir DrawPairedClient) : on centre
                    // l'avatar sur les icônes, donc sur la ligne, et non sur le texte.
                    var rowCenterOffset = ImGui.GetFontSize() * 0.22f + UiSharedService.GetIconSize(FontAwesomeIcon.Moon).Y / 2f;
                    DrawMiniAvatar(pair, rpIdentity, rpName, new System.Numerics.Vector2(screen.X, screen.Y + rowCenterOffset - avatar / 2f), avatar);
                    ImGui.SetCursorPosX(textPosX + avatar + ImGui.GetStyle().ItemSpacing.X * 0.75f);
                }
            }

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

    private UmbraProfileData? GetRpIdentity(Pair pair)
    {
        if (_profileManager.TryGetKnownProfile(pair.UserData, out var known))
            return known;

        // Pour les paires visibles seulement, et au rythme d'une requête toutes les demi-secondes :
        // la liste se remplit peu à peu sans produire de rafale à l'ouverture.
        if (pair.IsVisible && DateTime.UtcNow - _lastProfileRequestUtc > TimeSpan.FromMilliseconds(500))
        {
            _lastProfileRequestUtc = DateTime.UtcNow;
            _profileManager.GetUmbraProfile(pair.UserData);
        }

        return null;
    }

    private void DrawMiniAvatar(Pair pair, UmbraProfileData profile, string rpName, System.Numerics.Vector2 pos, float size)
    {
        var dl = ImGui.GetWindowDrawList();
        var rounding = size * 0.3f;
        var max = pos + new System.Numerics.Vector2(size);
        var color = !string.IsNullOrEmpty(profile.RpNameColor) && _mareConfigService.Current.UseRpNameColors
            ? UiSharedService.HexToVector4(profile.RpNameColor)
            : UiSharedService.AccentColor;

        Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? texture = null;
        var data = profile.RpImageData.Value;
        if (data.Length > 0)
        {
            var key = pair.UserData.UID;
            if (!_avatarTasks.TryGetValue(key, out var cached)
                || (!ReferenceEquals(data, cached.Data) && !data.AsSpan().SequenceEqual(cached.Data)))
            {
                if (cached.Task != null)
                    cached.Task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
                cached = (data, Task.Run(() => _uiSharedService.LoadImageAsync(data)));
                _avatarTasks[key] = cached;
            }
            if (cached.Task.IsCompletedSuccessfully) texture = cached.Task.Result;
        }

        if (texture != null && texture.Handle != IntPtr.Zero)
        {
            dl.AddImageRounded(texture.Handle, pos, max, System.Numerics.Vector2.Zero, System.Numerics.Vector2.One,
                ImGui.GetColorU32(System.Numerics.Vector4.One), rounding);
        }
        else
        {
            dl.AddRectFilled(pos, max, ImGui.GetColorU32(color with { W = 0.2f }), rounding);
            var initials = UiSharedService.GetInitials(rpName);
            if (initials.Length > 0)
            {
                var textSize = ImGui.CalcTextSize(initials);
                dl.AddText(pos + (new System.Numerics.Vector2(size) - textSize) / 2f, ImGui.GetColorU32(color with { W = 0.9f }), initials);
            }
        }

        dl.AddRect(pos, max, ImGui.GetColorU32(color with { W = 0.5f }), rounding, ImDrawFlags.None, 1f);
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
        foreach (var pending in _avatarTasks.Values)
            pending.Task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
        _avatarTasks.Clear();
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