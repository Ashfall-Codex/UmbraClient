using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using UmbraSync.Interop.Ipc;
using UmbraSync.MareConfiguration;
using UmbraSync.Models;
using UmbraSync.Services;

namespace UmbraSync.UI.Components;

public sealed partial class MoodlesEditor
{
    private readonly ILogger _logger;
    private readonly IpcManager _ipcManager;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly RpConfigService _rpConfigService;
    private readonly UiSharedService _uiSharedService;
    private readonly IDataManager _dataManager;
    private readonly Lazy<List<StatusIconInfo>> _statusIcons;

    private string _localMoodlesJson = string.Empty;
    private bool _moodleOperationInProgress;
    private bool _moodleRestoreAttempted;
    private int _moodleRestoreRetries;
    private DateTime _lastMoodleRestoreAttempt = DateTime.MinValue;
    private bool _moodlesFetching;
    private DateTime _lastMoodlesFetch = DateTime.MinValue;

    private bool _addPopupOpen;
    private int _editIndex = -1;
    private int _newIconId = 210456;
    private string _newTitle = "";
    private string _newDescription = "";
    private int _newType = 0;
    private bool _iconSelectorOpen;
    private string _iconIdInput = "210456";
    private string _iconSearchText = "";
    private string _lastIconSearchText = "";
    private List<StatusIconInfo>? _filteredIcons;

    private (uint id, Vector3 rgb, string label)[]? _colorPalette;
    private bool _titlePaletteExpanded;
    private bool _descPaletteExpanded;

    public MoodlesEditor(
        ILogger logger,
        IpcManager ipcManager,
        DalamudUtilService dalamudUtil,
        RpConfigService rpConfigService,
        UiSharedService uiSharedService,
        IDataManager dataManager,
        Lazy<List<StatusIconInfo>> statusIcons)
    {
        _logger = logger;
        _ipcManager = ipcManager;
        _dalamudUtil = dalamudUtil;
        _rpConfigService = rpConfigService;
        _uiSharedService = uiSharedService;
        _dataManager = dataManager;
        _statusIcons = statusIcons;
    }

    public string LocalMoodlesJson => _localMoodlesJson;

    public void PersistBackupForCharacter(string charName, uint worldId)
    {
        if (string.IsNullOrEmpty(charName) || worldId == 0) return;
        SaveBackup(_localMoodlesJson, charName, worldId);
    }

    public void ResetSession()
    {
        _localMoodlesJson = string.Empty;
        _lastMoodlesFetch = DateTime.MinValue;
        _moodleRestoreAttempted = false;
        _moodleRestoreRetries = 0;
        _lastMoodleRestoreAttempt = DateTime.MinValue;
    }

    public void ResetRestoreState()
    {
        _moodleRestoreAttempted = false;
        _moodleRestoreRetries = 0;
        _lastMoodleRestoreAttempt = DateTime.MinValue;
    }

    public void EnsureRefreshed()
    {
        if (string.IsNullOrEmpty(_localMoodlesJson) && !_moodlesFetching
            && (DateTime.UtcNow - _lastMoodlesFetch).TotalSeconds > 3)
        {
            _ = RefreshAsync();
        }
    }

    public async Task RunRestoreLoopAsync()
    {
        await Task.Delay(2000).ConfigureAwait(false);
        for (int i = 0; i < 6; i++)
        {
            if (_moodleRestoreAttempted) break;
            await RefreshAsync().ConfigureAwait(false);
            if (_moodleRestoreAttempted) break;
            await Task.Delay(4000).ConfigureAwait(false);
        }
    }

    public async Task RefreshAsync()
    {
        if (_moodlesFetching) return;
        _moodlesFetching = true;
        try
        {
            if (!_ipcManager.Moodles.APIAvailable) return;
            var ptr = await _dalamudUtil.GetPlayerPointerAsync().ConfigureAwait(false);
            if (ptr == IntPtr.Zero) return;
            _localMoodlesJson = await _ipcManager.Moodles.GetStatusAsync(ptr).ConfigureAwait(false) ?? string.Empty;
            _lastMoodlesFetch = DateTime.UtcNow;

            var charName = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false);
            var worldId = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(charName) || worldId == 0) return;

            var moodles = MoodleStatusInfo.ParseMoodles(_localMoodlesJson);
            if (moodles.Count > 0)
            {
                var profile = _rpConfigService.GetCharacterProfile(charName, worldId);
                var backupMoodles = MoodleStatusInfo.ParseMoodles(profile.MoodlesBackupJson);
                var ipcTitles = new HashSet<string>(moodles.Select(m => m.CleanTitle), StringComparer.OrdinalIgnoreCase);
                var backupTitles = new HashSet<string>(backupMoodles.Select(m => m.CleanTitle), StringComparer.OrdinalIgnoreCase);
                if (_moodleRestoreAttempted || ipcTitles.SetEquals(backupTitles) || string.IsNullOrEmpty(profile.MoodlesBackupJson))
                {
                    SaveBackup(_localMoodlesJson, charName, worldId);
                }
                else
                {
                    _logger.LogWarning("Moodles IPC returned {ipcCount} moodles ({ipcTitles}) but backup has {backupCount} ({backupTitles}) — possible stale data from character switch, skipping backup save",
                        moodles.Count, string.Join(", ", ipcTitles), backupMoodles.Count, string.Join(", ", backupTitles));
                }
                _moodleRestoreAttempted = true;
            }
            else if (!_moodleRestoreAttempted
                     && (DateTime.UtcNow - _lastMoodleRestoreAttempt).TotalSeconds > 15)
            {
                var profile = _rpConfigService.GetCharacterProfile(charName, worldId);
                if (!string.IsNullOrEmpty(profile.MoodlesBackupJson))
                {
                    var backupMoodles = MoodleStatusInfo.ParseMoodles(profile.MoodlesBackupJson);
                    if (backupMoodles.Count > 0)
                    {
                        _moodleRestoreRetries++;
                        _lastMoodleRestoreAttempt = DateTime.UtcNow;
                        _logger.LogInformation("Restoring {count} moodles from local backup (attempt {attempt}/5)",
                            backupMoodles.Count, _moodleRestoreRetries);
                        await _ipcManager.Moodles.SetStatusAsync(ptr, profile.MoodlesBackupJson).ConfigureAwait(false);
                        _localMoodlesJson = await _ipcManager.Moodles.GetStatusAsync(ptr).ConfigureAwait(false) ?? string.Empty;

                        var restoredMoodles = MoodleStatusInfo.ParseMoodles(_localMoodlesJson);
                        if (restoredMoodles.Count > 0)
                        {
                            _logger.LogInformation("Successfully restored {count} moodles from backup", restoredMoodles.Count);
                            _moodleRestoreAttempted = true;
                        }
                        else if (_moodleRestoreRetries >= 5)
                        {
                            _logger.LogWarning("Failed to restore moodles after {retries} attempts, giving up", _moodleRestoreRetries);
                            _moodleRestoreAttempted = true;
                        }
                    }
                    else
                    {
                        _moodleRestoreAttempted = true;
                    }
                }
                else
                {
                    _moodleRestoreAttempted = true;
                }
            }
        }
        finally
        {
            _moodlesFetching = false;
        }
    }

    public void DrawSection()
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Traits du personnage");
        ImGui.SameLine();
        bool addClicked = false;
        if (_moodleOperationInProgress)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "(...)");
        }
        else if (_ipcManager.Moodles.APIAvailable)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiSharedService.ThemeButtonHovered);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiSharedService.ThemeButtonActive);
            ImGui.PushID("addMoodleBtn");
            addClicked = _uiSharedService.IconButton(FontAwesomeIcon.Plus);
            ImGui.PopID();
            UiSharedService.AttachToolTip("Ajouter un trait");

            var backupProfile = _rpConfigService.GetCurrentCharacterProfile();
            if (MoodleStatusInfo.ParseMoodles(_localMoodlesJson).Count == 0
                && !string.IsNullOrEmpty(backupProfile.MoodlesBackupJson)
                && MoodleStatusInfo.ParseMoodles(backupProfile.MoodlesBackupJson).Count > 0)
            {
                ImGui.SameLine();
                ImGui.PushID("restoreMoodleBtn");
                if (_uiSharedService.IconButton(FontAwesomeIcon.Undo))
                {
                    _ = Task.Run(async () =>
                    {
                        if (_moodleOperationInProgress) return;
                        _moodleOperationInProgress = true;
                        try
                        {
                            var ptr = await _dalamudUtil.GetPlayerPointerAsync().ConfigureAwait(false);
                            if (ptr == IntPtr.Zero) return;
                            var bp = _rpConfigService.GetCurrentCharacterProfile();
                            _logger.LogInformation("Manual restore of moodles from local backup");
                            await _ipcManager.Moodles.SetStatusAsync(ptr, bp.MoodlesBackupJson).ConfigureAwait(false);
                            _localMoodlesJson = await _ipcManager.Moodles.GetStatusAsync(ptr).ConfigureAwait(false) ?? string.Empty;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to restore moodles from backup");
                        }
                        finally
                        {
                            _moodleOperationInProgress = false;
                        }
                    });
                }
                ImGui.PopID();
                UiSharedService.AttachToolTip("Restaurer les traits depuis le backup local");
            }

            ImGui.PopStyleColor(3);
        }

        if (addClicked)
        {
            _editIndex = -1;
            _newIconId = 210456;
            _iconIdInput = "210456";
            _newTitle = "";
            _newDescription = "";
            _newType = 0;
            _iconSelectorOpen = false;
            _addPopupOpen = true;
            ImGui.OpenPopup("##AddMoodlePopup");
        }

        DrawAddPopup();

        if (!string.IsNullOrEmpty(_localMoodlesJson))
        {
            DrawEditable();
        }
    }

    private void DrawEditable()
    {
        var moodles = MoodleStatusInfo.ParseMoodles(_localMoodlesJson);
        if (moodles.Count == 0) return;

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        const float iconHeight = 40f;
        var scaledHeight = iconHeight * ImGuiHelpers.GlobalScale;
        var textureProvider = _uiSharedService.TextureProvider;

        var items = new List<(MoodleStatusInfo moodle, ImTextureID handle, Vector2 size, int index)>();
        float totalWidth = 0f;
        for (int i = 0; i < moodles.Count; i++)
        {
            var moodle = moodles[i];
            if (moodle.IconID <= 0) continue;
            var wrap = textureProvider.GetFromGameIcon(new GameIconLookup((uint)moodle.IconID)).GetWrapOrEmpty();
            if (wrap.Handle == IntPtr.Zero) continue;
            var aspect = wrap.Height > 0 ? (float)wrap.Width / wrap.Height : 1f;
            var displaySize = new Vector2(scaledHeight * aspect, scaledHeight);
            items.Add((moodle, wrap.Handle, displaySize, i));
            totalWidth += displaySize.X;
        }
        if (items.Count == 0) return;

        totalWidth += (items.Count - 1) * spacing;
        var baseX = ImGui.GetCursorPosX();
        var startX = baseX + (availableWidth - totalWidth) / 2f;
        if (startX < baseX) startX = baseX;

        ImGui.SetCursorPosX(startX);

        for (int i = 0; i < items.Count; i++)
        {
            var (moodle, handle, size, moodleIndex) = items[i];

            if (i > 0)
                ImGui.SameLine();

            var groupPos = ImGui.GetCursorPos();
            var screenPos = ImGui.GetCursorScreenPos();
            ImGui.BeginGroup();

            ImGui.Image(handle, size);

            if (!_moodleOperationInProgress)
            {
                var btnSize = 16f * ImGuiHelpers.GlobalScale;
                var removeBtnPos = new Vector2(screenPos.X + size.X - btnSize + 2, screenPos.Y - 2);
                ImGui.SetCursorScreenPos(removeBtnPos);
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, btnSize / 2f);
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 0.85f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.3f, 0.3f, 1f));
                if (ImGui.Button($"X##removeMoodle_{moodleIndex}", new Vector2(btnSize, btnSize)))
                {
                    var idx = moodleIndex;
                    _ = Task.Run(() => RemoveAsync(idx));
                }
                ImGui.PopStyleColor(3);

                var editBtnPos = new Vector2(screenPos.X - 2, screenPos.Y - 2);
                ImGui.SetCursorScreenPos(editBtnPos);
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.35f, 0.6f, 0.85f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.5f, 0.8f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.35f, 0.6f, 1f, 1f));
                bool editClicked;
                using (_uiSharedService.IconFont.Push())
                {
                    editClicked = ImGui.Button($"{FontAwesomeIcon.Pen.ToIconString()}##editMoodle_{moodleIndex}", new Vector2(btnSize, btnSize));
                }
                ImGui.PopStyleColor(3);
                ImGui.PopStyleVar(2);
                if (editClicked)
                {
                    OpenEditPopup(moodleIndex, moodle);
                }
            }

            ImGui.EndGroup();

            if (i < items.Count - 1)
                ImGui.SetCursorPos(new Vector2(groupPos.X + size.X + spacing, groupPos.Y));

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 20f);
                var title = moodle.CleanTitle;
                if (!string.IsNullOrEmpty(title))
                {
                    var typeColor = moodle.Type switch
                    {
                        0 => new Vector4(0.4f, 0.9f, 0.4f, 1f),
                        1 => new Vector4(0.9f, 0.4f, 0.4f, 1f),
                        _ => new Vector4(0.5f, 0.6f, 1f, 1f),
                    };
                    ImGui.TextColored(typeColor, title);
                }
                var desc = moodle.CleanDescription;
                if (!string.IsNullOrEmpty(desc))
                    ImGui.TextUnformatted(desc);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }
    }

    private void DrawAddPopup()
    {
        if (!_addPopupOpen) return;

        ImGui.SetNextWindowSize(new Vector2(500, 0) * ImGuiHelpers.GlobalScale, ImGuiCond.Always);
        if (ImGui.BeginPopupModal("##AddMoodlePopup", ref _addPopupOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            bool isEditing = _editIndex >= 0;
            ImGui.TextColored(UiSharedService.AccentColor, isEditing ? "Modifier un trait" : "Ajouter un trait");
            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));

            var textureProvider = _uiSharedService.TextureProvider;
            try
            {
                var previewWrap = textureProvider.GetFromGameIcon(new GameIconLookup((uint)_newIconId)).GetWrapOrEmpty();
                if (previewWrap.Handle != IntPtr.Zero)
                {
                    var previewSize = 48f * ImGuiHelpers.GlobalScale;
                    var aspect = previewWrap.Height > 0 ? (float)previewWrap.Width / previewWrap.Height : 1f;
                    ImGui.Image(previewWrap.Handle, new Vector2(previewSize * aspect, previewSize));
                    ImGui.SameLine();
                }
            }
            catch { /* Icon not found */ }

            ImGui.BeginGroup();
            ImGui.TextUnformatted($"Icône : {_newIconId}");
            if (ImGui.Button(_iconSelectorOpen ? "Fermer le sélecteur" : "Choisir une icône"))
            {
                _iconSelectorOpen = !_iconSelectorOpen;
            }
            ImGui.EndGroup();

            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputText("##iconIdDirect", ref _iconIdInput, 10, ImGuiInputTextFlags.CharsDecimal)
                && int.TryParse(_iconIdInput, out var parsed) && parsed > 0)
                _newIconId = parsed;
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "ID direct");

            if (_iconSelectorOpen)
            {
                ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));
                DrawIconSelectorGrid(textureProvider);
            }

            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));

            ImGui.TextColored(ImGuiColors.DalamudGrey, "Titre");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##moodleTitle", ref _newTitle, 100);

            DrawColorPalette("title", "Couleur du titre", ref _newTitle);

            ImGui.TextColored(ImGuiColors.DalamudGrey, "Description");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextMultiline("##moodleDesc", ref _newDescription, 500,
                new Vector2(-1, 80 * ImGuiHelpers.GlobalScale));
            DrawColorPalette("desc", "Couleur de la description", ref _newDescription);

            ImGui.TextColored(ImGuiColors.DalamudGrey, "Type");
            ImGui.SetNextItemWidth(-1);
            var typeNames = new[] { "Positif (Buff)", "Négatif (Debuff)", "Neutre" };
            ImGui.Combo("##moodleType", ref _newType, typeNames, typeNames.Length);

            ImGuiHelpers.ScaledDummy(new Vector2(0f, 8f));

            var buttonWidth = 120 * ImGuiHelpers.GlobalScale;
            var totalButtonsWidth = buttonWidth * 2 + ImGui.GetStyle().ItemSpacing.X;
            ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - totalButtonsWidth) / 2f + ImGui.GetCursorPosX());

            var canAdd = !string.IsNullOrWhiteSpace(_newTitle) && !_moodleOperationInProgress;
            if (!canAdd) ImGui.BeginDisabled();
            if (ImGui.Button(isEditing ? "Modifier" : "Ajouter", new Vector2(buttonWidth, 0)))
            {
                var moodle = new MoodleFullStatus
                {
                    IconID = _newIconId,
                    Title = _newTitle,
                    Description = _newDescription,
                    Type = _newType,
                };
                if (isEditing)
                {
                    var idx = _editIndex;
                    _ = Task.Run(() => EditAsync(idx, moodle));
                }
                else
                {
                    _ = Task.Run(() => AddAsync(moodle));
                }
                _editIndex = -1;
                _addPopupOpen = false;
                ImGui.CloseCurrentPopup();
            }
            if (!canAdd) ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Annuler", new Vector2(buttonWidth, 0)))
            {
                _editIndex = -1;
                _addPopupOpen = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void DrawIconSelectorGrid(ITextureProvider textureProvider)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##iconSearch", "Rechercher (nom ou ID)...", ref _iconSearchText, 64);

        var allIcons = _statusIcons.Value;
        if (allIcons.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Aucune icône disponible.");
            return;
        }

        if (_filteredIcons == null || !string.Equals(_lastIconSearchText, _iconSearchText, StringComparison.Ordinal))
        {
            _lastIconSearchText = _iconSearchText;
            if (string.IsNullOrWhiteSpace(_iconSearchText))
            {
                _filteredIcons = allIcons;
            }
            else
            {
                var search = _iconSearchText.Trim();
                _filteredIcons = allIcons.Where(i =>
                    i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || i.IconId.ToString().Contains(search, StringComparison.Ordinal)
                ).ToList();
            }
        }

        var icons = _filteredIcons;
        if (icons.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Aucun résultat.");
            return;
        }

        const int iconsPerRow = 10;
        var iconSize = 40f * ImGuiHelpers.GlobalScale;
        var spacing = 4f * ImGuiHelpers.GlobalScale;
        var rowHeight = iconSize + spacing;
        var totalRows = (icons.Count + iconsPerRow - 1) / iconsPerRow;
        var childHeight = 200f * ImGuiHelpers.GlobalScale;

        ImGui.BeginChild("##iconGrid", new Vector2(-1, childHeight), true);

        var scrollY = ImGui.GetScrollY();
        var firstVisibleRow = Math.Max(0, (int)(scrollY / rowHeight) - 1);
        var visibleRows = (int)(childHeight / rowHeight) + 3;
        var lastVisibleRow = Math.Min(totalRows - 1, firstVisibleRow + visibleRows);

        if (firstVisibleRow > 0)
            ImGui.Dummy(new Vector2(0, firstVisibleRow * rowHeight));

        for (int row = firstVisibleRow; row <= lastVisibleRow; row++)
        {
            for (int col = 0; col < iconsPerRow; col++)
            {
                var iconIndex = row * iconsPerRow + col;
                if (iconIndex >= icons.Count) break;
                var info = icons[iconIndex];

                if (col > 0) ImGui.SameLine(0, spacing);

                IDalamudTextureWrap? wrap;
                try
                {
                    wrap = textureProvider.GetFromGameIcon(new GameIconLookup(info.IconId)).GetWrapOrEmpty();
                }
                catch
                {
                    ImGui.Dummy(new Vector2(iconSize, iconSize));
                    continue;
                }

                if (wrap.Handle == IntPtr.Zero)
                {
                    ImGui.Dummy(new Vector2(iconSize, iconSize));
                    continue;
                }

                bool isSelected = _newIconId == (int)info.IconId;
                if (isSelected)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, UiSharedService.ThemeButtonActive);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiSharedService.ThemeButtonActive);
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiSharedService.ThemeButtonHovered);
                }

                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
                ImGui.PushID((int)info.IconId);
                if (ImGui.ImageButton(wrap.Handle, new Vector2(iconSize, iconSize)))
                {
                    _newIconId = (int)info.IconId;
                    _iconIdInput = info.IconId.ToString();
                }
                ImGui.PopID();
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(2);

                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted($"#{info.IconId} — {info.Name}");
                    ImGui.EndTooltip();
                }
            }
        }

        var remainingRows = totalRows - lastVisibleRow - 1;
        if (remainingRows > 0)
            ImGui.Dummy(new Vector2(0, remainingRows * rowHeight));

        ImGui.EndChild();
    }

    private void OpenEditPopup(int moodleIndex, MoodleStatusInfo moodle)
    {
        _editIndex = moodleIndex;
        _newIconId = moodle.IconID > 0 ? moodle.IconID : 210456;
        _iconIdInput = _newIconId.ToString(CultureInfo.InvariantCulture);
        _newTitle = moodle.Title ?? string.Empty;
        _newDescription = moodle.Description ?? string.Empty;
        _newType = moodle.Type;
        _iconSelectorOpen = false;
        _addPopupOpen = true;
        ImGui.OpenPopup("##AddMoodlePopup");
    }

    private async Task RemoveAsync(int index)
    {
        if (_moodleOperationInProgress) return;
        _moodleOperationInProgress = true;
        try
        {
            if (!_ipcManager.Moodles.APIAvailable) return;
            var ptr = await _dalamudUtil.GetPlayerPointerAsync().ConfigureAwait(false);
            if (ptr == IntPtr.Zero) return;

            var freshJson = await _ipcManager.Moodles.GetStatusAsync(ptr).ConfigureAwait(false);
            if (string.IsNullOrEmpty(freshJson)) return;

            var newJson = MoodleStatusInfo.RemoveMoodleAtIndex(freshJson, index);
            await _ipcManager.Moodles.SetStatusAsync(ptr, newJson).ConfigureAwait(false);
            _localMoodlesJson = await _ipcManager.Moodles.GetStatusAsync(ptr).ConfigureAwait(false) ?? string.Empty;
            var cn = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false);
            var wid = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(cn) && wid > 0) SaveBackup(_localMoodlesJson, cn, wid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove moodle at index {index}", index);
        }
        finally
        {
            _moodleOperationInProgress = false;
        }
    }

    private async Task EditAsync(int index, MoodleFullStatus moodle)
    {
        if (_moodleOperationInProgress) return;
        _moodleOperationInProgress = true;
        try
        {
            if (!_ipcManager.Moodles.APIAvailable) return;
            var ptr = await _dalamudUtil.GetPlayerPointerAsync().ConfigureAwait(false);
            if (ptr == IntPtr.Zero) return;

            var freshJson = await _ipcManager.Moodles.GetStatusAsync(ptr).ConfigureAwait(false);
            if (string.IsNullOrEmpty(freshJson)) return;

            var newJson = MoodleStatusInfo.ReplaceMoodleAtIndex(freshJson, index, moodle);
            await _ipcManager.Moodles.SetStatusAsync(ptr, newJson).ConfigureAwait(false);
            _localMoodlesJson = await _ipcManager.Moodles.GetStatusAsync(ptr).ConfigureAwait(false) ?? string.Empty;
            var cn = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false);
            var wid = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(cn) && wid > 0) SaveBackup(_localMoodlesJson, cn, wid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to edit moodle at index {index}", index);
        }
        finally
        {
            _moodleOperationInProgress = false;
        }
    }

    private async Task AddAsync(MoodleFullStatus moodle)
    {
        if (_moodleOperationInProgress) return;
        _moodleOperationInProgress = true;
        try
        {
            if (!_ipcManager.Moodles.APIAvailable) return;
            var ptr = await _dalamudUtil.GetPlayerPointerAsync().ConfigureAwait(false);
            if (ptr == IntPtr.Zero) return;

            var freshJson = await _ipcManager.Moodles.GetStatusAsync(ptr).ConfigureAwait(false) ?? string.Empty;
            var newJson = MoodleStatusInfo.AddMoodle(freshJson, moodle);
            await _ipcManager.Moodles.SetStatusAsync(ptr, newJson).ConfigureAwait(false);
            _localMoodlesJson = await _ipcManager.Moodles.GetStatusAsync(ptr).ConfigureAwait(false) ?? string.Empty;
            var cn = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false);
            var wid = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(cn) && wid > 0) SaveBackup(_localMoodlesJson, cn, wid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add moodle");
        }
        finally
        {
            _moodleOperationInProgress = false;
        }
    }

    private void SaveBackup(string moodlesJson, string charName, uint worldId)
    {
        var profile = _rpConfigService.GetCharacterProfile(charName, worldId);
        var moodles = MoodleStatusInfo.ParseMoodles(moodlesJson);
        if (moodles.Count == 0)
        {
            if (!string.IsNullOrEmpty(profile.MoodlesBackupJson))
            {
                _logger.LogInformation("Clearing moodles backup for {char}@{world} (no active traits)", charName, worldId);
                profile.MoodlesBackupJson = string.Empty;
                profile.MoodlesBackupTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _rpConfigService.Save();
            }
            return;
        }

        profile.MoodlesBackupJson = moodlesJson;
        profile.MoodlesBackupTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _rpConfigService.Save();
    }

    private void DrawColorPalette(string idSuffix, string label, ref string target)
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        var palette = GetColorPalette();
        var swatchSize = new Vector2(22f * ImGuiHelpers.GlobalScale);
        const int swatchesPerRow = 15;
        ref bool expanded = ref (string.Equals(idSuffix, "title", StringComparison.Ordinal) ? ref _titlePaletteExpanded : ref _descPaletteExpanded);
        int visibleCount = expanded ? palette.Length : Math.Min(swatchesPerRow, palette.Length);

        for (int i = 0; i < visibleCount; i++)
        {
            if (i > 0 && i % swatchesPerRow != 0) ImGui.SameLine();
            var sw = palette[i];
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(sw.rgb, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(Vector3.Min(sw.rgb * 1.25f, Vector3.One), 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(Vector3.Min(sw.rgb * 1.4f, Vector3.One), 1f));
            if (ImGui.Button($"##swatch_{idSuffix}_{i}", swatchSize))
            {
                target = WrapTitleWithColor(target, sw.id.ToString(CultureInfo.InvariantCulture));
            }
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{sw.label} (UIColor {sw.id})");
        }

        if (palette.Length > swatchesPerRow)
        {
            if (ImGui.Button((expanded ? "Réduire la liste" : "Tout afficher") + $"##togglePalette_{idSuffix}"))
            {
                expanded = !expanded;
            }
            ImGui.SameLine();
        }
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.4f, 0.4f, 0.4f, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.6f, 0.3f, 0.3f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.8f, 0.2f, 0.2f, 1f));
        if (ImGui.Button($"Retirer la couleur##clearColor_{idSuffix}"))
        {
            target = StripColorTags(target);
        }
        ImGui.PopStyleColor(3);
    }

    private (uint id, Vector3 rgb, string label)[] GetColorPalette()
    {
        if (_colorPalette != null) return _colorPalette;

        var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.UIColor>();
        if (sheet == null) { _colorPalette = []; return _colorPalette; }

        var hues = new (float h, string label)[]
        {
            (0f, "Rouge"),
            (15f, "Rouge orangé"),
            (30f, "Orange"),
            (45f, "Ambre"),
            (60f, "Jaune"),
            (90f, "Vert lime"),
            (120f, "Vert"),
            (160f, "Émeraude"),
            (180f, "Cyan"),
            (200f, "Bleu ciel"),
            (220f, "Bleu"),
            (250f, "Indigo"),
            (275f, "Violet"),
            (300f, "Magenta"),
            (330f, "Rose"),
        };

        var brightnesses = new (float s, float v, string suffix)[]
        {
            (0.55f, 1.00f, " clair"),
            (0.90f, 0.95f, ""),
            (0.95f, 0.55f, " foncé"),
        };

        var neutrals = new (Vector3 rgb, string label)[]
        {
            (new(1f, 1f, 1f), "Blanc"),
            (new(0.80f, 0.80f, 0.80f), "Gris clair"),
            (new(0.55f, 0.55f, 0.55f), "Gris"),
            (new(0.30f, 0.30f, 0.30f), "Gris foncé"),
            (new(0.10f, 0.10f, 0.10f), "Noir"),
        };

        var result = new List<(uint id, Vector3 rgb, string label)>();
        var seenIds = new HashSet<uint>();

        foreach (var (h, label) in hues)
        {
            foreach (var (s, v, suffix) in brightnesses)
            {
                var rgb = HsvToRgb(h, s, v);
                var id = FindClosestUiColorId(rgb);
                if (!seenIds.Add(id)) continue;
                var actual = GetUiColorRgb(sheet, id) ?? rgb;
                result.Add((id, actual, label + suffix));
            }
        }

        foreach (var (rgb, label) in neutrals)
        {
            var id = FindClosestUiColorId(rgb);
            if (!seenIds.Add(id)) continue;
            var actual = GetUiColorRgb(sheet, id) ?? rgb;
            result.Add((id, actual, label));
        }

        _colorPalette = result.ToArray();
        return _colorPalette;
    }

    private uint FindClosestUiColorId(Vector3 rgb)
    {
        var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.UIColor>();
        if (sheet == null) return 1;

        int targetR = (int)Math.Clamp(rgb.X * 255f, 0f, 255f);
        int targetG = (int)Math.Clamp(rgb.Y * 255f, 0f, 255f);
        int targetB = (int)Math.Clamp(rgb.Z * 255f, 0f, 255f);

        uint bestRow = 1;
        int bestDist = int.MaxValue;
        foreach (var row in sheet)
        {
            if (row.RowId == 0) continue;
            uint fg = row.Dark;
            if ((fg & 0xFF) == 0) continue;
            if (row.RowId >= 500 && (fg & 0xFFFFFF00) == 0) continue;
            int r = (int)((fg >> 24) & 0xFF);
            int g = (int)((fg >> 16) & 0xFF);
            int b = (int)((fg >> 8) & 0xFF);
            int dr = r - targetR, dg = g - targetG, db = b - targetB;
            int dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestRow = row.RowId;
                if (bestDist == 0) break;
            }
        }
        return bestRow;
    }

    private static Vector3? GetUiColorRgb(Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.UIColor> sheet, uint id)
    {
        var rowOpt = sheet.GetRowOrDefault(id);
        if (rowOpt == null) return null;
        uint fg = rowOpt.Value.Dark;
        return new Vector3(
            ((fg >> 24) & 0xFF) / 255f,
            ((fg >> 16) & 0xFF) / 255f,
            ((fg >> 8) & 0xFF) / 255f);
    }

    private static Vector3 HsvToRgb(float h, float s, float v)
    {
        h = (h % 360f + 360f) % 360f;
        float c = v * s;
        float hp = h / 60f;
        float x = c * (1f - MathF.Abs((hp % 2f) - 1f));
        float r, g, b;
        if (hp < 1) { r = c; g = x; b = 0; }
        else if (hp < 2) { r = x; g = c; b = 0; }
        else if (hp < 3) { r = 0; g = c; b = x; }
        else if (hp < 4) { r = 0; g = x; b = c; }
        else if (hp < 5) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        float m = v - c;
        return new Vector3(r + m, g + m, b + m);
    }

    private static string WrapTitleWithColor(string title, string colorName)
    {
        var stripped = StripColorTags(title);
        return $"[color={colorName}]{stripped}[/color]";
    }

    [GeneratedRegex(@"\[/?color(?:=[^\]]*)?]", RegexOptions.NonBacktracking)]
    private static partial Regex ColorTagRegex();

    private static string StripColorTags(string text)
    {
        return ColorTagRegex().Replace(text, string.Empty).Trim();
    }
}
