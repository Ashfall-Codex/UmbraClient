using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using System.Text;
using UmbraSync.API.Dto.McdfShare;
using UmbraSync.Localization;

namespace UmbraSync.UI;

public sealed partial class CharaDataHubUi
{
    private enum McdfDeleteKind { None, ServerShare, LocalFile, LocalFolder }

    private readonly record struct McdfRowAction(FontAwesomeIcon Icon, string Tooltip, bool Enabled, Action OnClick);

    private const string McdfDeletePopupId = "##mcdfDeleteConfirm";

    private McdfDeleteKind _mcdfDeleteKind;
    private string _mcdfDeleteTarget = string.Empty;
    private string _mcdfDeleteLabel = string.Empty;
    private bool _mcdfDeleteModalOpen = true;
    private bool _mcdfOpenDeleteModal;
    private string? _mcdfShownSuccess;
    private DateTime _mcdfSuccessSince;

    private void DrawMcdfPage()
    {
        EnsureMcdOnlineInitialized();

        DrawMcdfFileActions();
        DrawMcdfServerCard();
        DrawMcdfShareForm();
        DrawMcdfLocalCard();
        UiSharedService.EndSectionCard();

        DrawMcdfDownloadPopup();
        DrawMcdfDeleteConfirm();
    }


    private void DrawMcdfFileActions()
    {
        float scale = ImGuiHelpers.GlobalScale;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float buttonWidth = (ImGui.GetContentRegionAvail().X - spacing) / 2f;
        float buttonHeight = 34f * scale;

        if (DrawMcdfActionButton(FontAwesomeIcon.FileExport, Loc.Get("CharaDataHub.Mcdf.Action.Export"), _mcdfPanel == McdfPanel.Export, buttonWidth, buttonHeight))
            _mcdfPanel = _mcdfPanel == McdfPanel.Export ? McdfPanel.None : McdfPanel.Export;
        UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcdf.Export.Tooltip"));
        ImGui.SameLine();
        if (DrawMcdfActionButton(FontAwesomeIcon.FileImport, Loc.Get("CharaDataHub.Mcdf.Action.Import"), _mcdfPanel == McdfPanel.Import, buttonWidth, buttonHeight))
            _mcdfPanel = _mcdfPanel == McdfPanel.Import ? McdfPanel.None : McdfPanel.Import;
        UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcdf.Import.Tooltip"));

        if (_mcdfPanel == McdfPanel.Export) DrawMcdfInlinePanel("mcdf-export", DrawMcdfExportContent);
        else if (_mcdfPanel == McdfPanel.Import) DrawMcdfInlinePanel("mcdf-import", DrawMcdfImportContent);

        ImGuiHelpers.ScaledDummy(4f);
    }

    private bool DrawMcdfActionButton(FontAwesomeIcon icon, string label, bool active, float width, float height)
    {
        using var border = ImRaii.PushColor(ImGuiCol.Border, ImGuiColors.DalamudWhite with { W = 0.85f }, active);
        using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 1.5f * ImGuiHelpers.GlobalScale, active);
        return _uiSharedService.IconTextButton(icon, label, width, buttonColor: UiSharedService.AccentColor, height: height);
    }

    private void EnsureMcdOnlineInitialized()
    {
        if (_mcdfShareInitialized) return;
        _mcdfShareInitialized = true;
        var cts = EnsureFreshCts(ref _disposalCts);
        _ = _charaDataManager.GetAllData(cts.Token);
        _ = _mcdfShareManager.RefreshAsync(CancellationToken.None);
    }

    private void DrawMcdOnlineRefreshButton(bool withLabel = false)
    {
        bool cooldown = _charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted;
        using (ImRaii.Disabled((!_charaDataManager.GetAllDataTask?.IsCompleted ?? false) || cooldown || _mcdfShareManager.IsBusy))
        {
            if (withLabel
                    ? _uiSharedService.IconTextButton(FontAwesomeIcon.ArrowsSpin, Loc.Get("CharaDataHub.Mcd.Online.Refresh"))
                    : _uiSharedService.IconButton(FontAwesomeIcon.ArrowsSpin))
            {
                var cts = EnsureFreshCts(ref _disposalCts);
                _ = _charaDataManager.GetAllData(cts.Token);
                _ = _mcdfShareManager.RefreshAsync(CancellationToken.None);
            }
        }
        UiSharedService.AttachToolTip(cooldown ? Loc.Get("CharaDataHub.Mcd.Online.DownloadAllCooldown") : Loc.Get("CharaDataHub.Mcd.Online.Refresh"));
    }

    private void DrawMcdfServerCard()
    {
        var shares = _mcdfShareManager.OwnShares;
        long totalSize = 0;
        foreach (var share in shares) totalSize += share.DataSize;
        var summary = string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcdf.Server.Summary"), shares.Count, FormatFileSize(totalSize));
        UiSharedService.BeginSectionCard(Loc.Get("CharaDataHub.Mcdf.Server.Title"), FontAwesomeIcon.Database, summary);

        float scale = ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        string generateLabel = Loc.Get("CharaDataHub.Mcdf.NewSnapshot");
        float nameWidth = 150f * scale;
        float generateWidth = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Camera, generateLabel);
        float rightGroupWidth = nameWidth + spacing + generateWidth;
        float leftFixed = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.ArrowsSpin).X
            + UiSharedService.GetIconSize(FontAwesomeIcon.QuestionCircle).X + spacing * 2f;
        float toolbarStartX = ImGui.GetCursorPosX();
        float toolbarRight = toolbarStartX + ImGui.GetContentRegionAvail().X;

        DrawMcdfSearch("##mcdfOnlineSearch", Loc.Get("CharaDataHub.Mcd.Online.Search"), ref _mcdfOnlineSearch,
            ImGui.GetContentRegionAvail().X - leftFixed - rightGroupWidth - spacing * 3f);
        ImGui.SameLine();
        DrawMcdOnlineRefreshButton();
        _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.Mcdf.Server.Help"));

        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), toolbarRight - rightGroupWidth));
        ImGui.SetNextItemWidth(nameWidth);
        bool submit = ImGui.InputTextWithHint("##mcdfSnapshotName", Loc.Get("CharaDataHub.Mcdf.NewSnapshot.Placeholder"), ref _mcdfSnapshotName, 128,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        bool canGenerate = !_mcdfShareManager.IsBusy && !string.IsNullOrWhiteSpace(_mcdfSnapshotName);
        using (ImRaii.Disabled(!canGenerate))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Camera, generateLabel)) submit = true;
        }
        UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcdf.NewSnapshot.Tooltip"));
        if (submit && canGenerate)
        {
            _ = _mcdfShareManager.CreateShareAsync(_mcdfSnapshotName.Trim(), [], [], null, CancellationToken.None);
            _mcdfSnapshotName = string.Empty;
        }

        DrawMcdfStatus();
        ImGuiHelpers.ScaledDummy(2f);

        if (shares.Count == 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.OwnShares.None"), ImGuiColors.DalamudGrey);
            return;
        }

        bool any = false;
        foreach (var entry in shares)
        {
            if (!string.IsNullOrWhiteSpace(_mcdfOnlineSearch)
                && !(entry.Description ?? string.Empty).Contains(_mcdfOnlineSearch, StringComparison.OrdinalIgnoreCase))
                continue;
            any = true;
            DrawMcdfServerRow(entry);
        }

        if (!any)
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.NoResults"), ImGuiColors.DalamudGrey);
    }

    private void DrawMcdfServerRow(McdfShareEntryDto entry)
    {
        var idText = entry.Id.ToString("D", CultureInfo.InvariantCulture);
        var name = string.IsNullOrEmpty(entry.Description) ? idText : entry.Description;
        var favId = $"mcdf:{idText}";
        int accessCount = entry.AllowedIndividuals.Count + entry.AllowedSyncshells.Count;

        var meta = new StringBuilder();
        if (entry.DataSize > 0) meta.Append(FormatFileSize(entry.DataSize)).Append(" · ");
        meta.Append(entry.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture));
        meta.Append(" · ").Append(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcdf.Row.Downloads"), entry.DownloadCount));
        if (entry.ExpiresAtUtc.HasValue)
        {
            meta.Append(" · ").Append(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcdf.Row.Expires"),
                entry.ExpiresAtUtc.Value.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.CurrentCulture)));
        }

        var localFolder = _configService.Current.McdfLocalFolder;
        bool canDownload = !_mcdfShareManager.IsBusy && !string.IsNullOrEmpty(localFolder);

        DrawMcdfRow("share" + idText, 0f, () =>
        {
            DrawFavorite(favId, name);
            ImGui.SameLine();
            ImGui.TextUnformatted(name);
            if (accessCount > 0)
            {
                ImGui.SameLine();
                _uiSharedService.IconText(FontAwesomeIcon.Users, UiSharedService.AccentColor);
                ImGui.SameLine(0f, 4f * ImGuiHelpers.GlobalScale);
                UiSharedService.ColorText(accessCount.ToString(CultureInfo.CurrentCulture), UiSharedService.AccentColor);
                UiSharedService.AttachToolTip(BuildMcdfAccessTooltip(entry));
            }
        }, meta.ToString(),
        [
            new(FontAwesomeIcon.Download,
                string.IsNullOrEmpty(localFolder) ? Loc.Get("CharaDataHub.Mcdf.Local.NoFolder") : Loc.Get("CharaDataHub.Mcdf.Online.DownloadTooltip"),
                canDownload, () =>
                {
                    _mcdfDownloadEntry = entry;
                    _mcdfDownloadFolder = string.Empty;
                    _mcdfDownloadNewFolder = string.Empty;
                    _mcdfDownloadTask = null;
                    _mcdfOpenDownloadPopup = true;
                }),
            new(FontAwesomeIcon.ShareAlt, Loc.Get("CharaDataHub.Mcdf.Share.ActionTooltip"), true,
                () => BeginMcdfShare(idText, false, name, entry.AllowedIndividuals, entry.AllowedSyncshells, entry.ExpiresAtUtc)),
            new(FontAwesomeIcon.Trash, Loc.Get("CharaDataHub.Mcdf.Online.DeleteTooltip"), !_mcdfShareManager.IsBusy,
                () => RequestMcdfDelete(McdfDeleteKind.ServerShare, idText, name)),
        ]);
    }

    private string BuildMcdfAccessTooltip(McdfShareEntryDto entry)
    {
        var sb = new StringBuilder();
        if (entry.AllowedIndividuals.Count > 0)
        {
            sb.Append(Loc.Get("CharaDataHub.Mcdf.Share.AllowedUids"));
            foreach (var uid in entry.AllowedIndividuals)
                sb.Append("\n• ").Append(FormatUidWithName(uid));
        }
        if (entry.AllowedSyncshells.Count > 0)
        {
            if (sb.Length > 0) sb.Append(UiSharedService.TooltipSeparator);
            sb.Append(Loc.Get("CharaDataHub.Mcdf.Share.AllowedSyncshells"));
            foreach (var gid in entry.AllowedSyncshells)
                sb.Append("\n• ").Append(FormatSyncshellLabel(gid));
        }
        return sb.ToString();
    }

    // Statut des envois : l'envoi en cours et les erreurs restent affichés, un succès s'efface après quelques secondes.
    private void DrawMcdfStatus()
    {
        if (_mcdfShareManager.IsBusy)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Local.Uploading"), ImGuiColors.DalamudYellow);
            return;
        }
        if (!string.IsNullOrEmpty(_mcdfShareManager.LastError))
        {
            UiSharedService.ColorTextWrapped(_mcdfShareManager.LastError!, ImGuiColors.DalamudRed);
            return;
        }

        var success = _mcdfShareManager.LastSuccess;
        if (!string.Equals(success, _mcdfShownSuccess, StringComparison.Ordinal))
        {
            _mcdfShownSuccess = success;
            _mcdfSuccessSince = DateTime.UtcNow;
        }
        if (!string.IsNullOrEmpty(success) && (DateTime.UtcNow - _mcdfSuccessSince).TotalSeconds < 6)
            UiSharedService.ColorTextWrapped(success, ImGuiColors.HealerGreen);
    }

    private void DrawMcdfLocalCard()
    {
        var folder = _configService.Current.McdfLocalFolder;
        bool folderSet = !string.IsNullOrEmpty(folder);
        bool folderExists = folderSet && Directory.Exists(folder);

        if (folderExists && (DateTime.UtcNow - _localMcdfScanTime).TotalSeconds > 2)
            ScanLocalMcdfFolder();

        string? summary = null;
        if (folderExists)
        {
            int fileCount = 0;
            long totalSize = 0;
            foreach (var file in _localMcdfFiles)
            {
                if (string.IsNullOrEmpty(file.FilePath)) continue;
                fileCount++;
                totalSize += file.FileSize;
            }
            summary = string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcdf.Local.Summary"), fileCount, FormatFileSize(totalSize));
        }
        string? folderTooltip = folderSet ? folder + UiSharedService.TooltipSeparator + Loc.Get("CharaDataHub.Mcdf.Local.FolderSettingsHint") : null;
        UiSharedService.BeginSectionCard(Loc.Get("CharaDataHub.Mcdf.Local.CardTitle"), FontAwesomeIcon.FolderOpen, summary, folderTooltip);

        DrawMcdfLocalToolbar(folderExists);
        ImGuiHelpers.ScaledDummy(2f);

        if (!folderSet)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Local.NoFolder"), ImGuiColors.DalamudGrey);
            return;
        }
        if (!folderExists)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Local.FolderNotFound"), UiSharedService.AccentColor);
            return;
        }
        if (_localMcdfFiles.Count == 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Local.NoFiles"), ImGuiColors.DalamudGrey);
            return;
        }

        DrawMcdfLocalList();
    }

    private void DrawMcdfLocalToolbar(bool folderExists)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        float leftFixed = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.ArrowsSpin).X
            + _uiSharedService.GetIconButtonSize(FontAwesomeIcon.FolderPlus).X + spacing * 2f;

        using (ImRaii.Disabled(!folderExists))
        {
            DrawMcdfSearch("##mcdfLocalSearch", Loc.Get("CharaDataHub.Mcdf.Local.Search"), ref _mcdfLocalSearch,
                ImGui.GetContentRegionAvail().X - leftFixed);
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.ArrowsSpin))
                _localMcdfScanTime = DateTime.MinValue;
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcdf.Local.Refresh"));
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.FolderPlus))
            {
                _mcdfShowNewFolderInput = !_mcdfShowNewFolderInput;
                _mcdfNewFolderName = string.Empty;
            }
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcdf.Local.NewFolder"));
        }

        if (_mcdfShowNewFolderInput && folderExists)
            DrawMcdfNewFolderInput(scale);
    }

    private void DrawMcdfNewFolderInput(float scale)
    {
        bool createFolder = false;
        ImGui.SetNextItemWidth(250f * scale);
        if (ImGui.InputTextWithHint("##mcdfNewFolder", Loc.Get("CharaDataHub.Mcdf.Local.NewFolderPlaceholder"), ref _mcdfNewFolderName, 64,
            ImGuiInputTextFlags.EnterReturnsTrue))
        {
            createFolder = true;
        }
        ImGui.SameLine();
        using (ImRaii.PushId("mcdfNewFolderConfirm"))
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Check))
                createFolder = true;
        }
        ImGui.SameLine();
        using (ImRaii.PushId("mcdfNewFolderCancel"))
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Times))
            {
                _mcdfNewFolderName = string.Empty;
                _mcdfShowNewFolderInput = false;
            }
        }

        if (!createFolder || string.IsNullOrWhiteSpace(_mcdfNewFolderName)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(_configService.Current.McdfLocalFolder, _mcdfNewFolderName.Trim()));
            _localMcdfScanTime = DateTime.MinValue;
            _mcdfNewFolderName = string.Empty;
            _mcdfShowNewFolderInput = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create MCDF folder {Name}", _mcdfNewFolderName);
        }
    }

    private void DrawMcdfLocalList()
    {
        float indent = 22f * ImGuiHelpers.GlobalScale;
        bool searching = !string.IsNullOrWhiteSpace(_mcdfLocalSearch);
        string? lastFolder = null;
        bool folderCollapsed = false;
        bool any = false;

        foreach (var entry in _localMcdfFiles)
        {
            bool isFile = !string.IsNullOrEmpty(entry.FilePath);
            bool folderMatches = searching && entry.SubFolder.Contains(_mcdfLocalSearch, StringComparison.OrdinalIgnoreCase);
            if (searching && !folderMatches && (!isFile || !entry.Description.Contains(_mcdfLocalSearch, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!string.Equals(lastFolder, entry.SubFolder, StringComparison.Ordinal))
            {
                lastFolder = entry.SubFolder;
                folderCollapsed = false;
                if (!string.IsNullOrEmpty(entry.SubFolder))
                {
                    // Une recherche déplie les dossiers : un résultat replié serait invisible.
                    folderCollapsed = !searching && _collapsedMcdfFolders.Contains(entry.SubFolder);
                    DrawMcdfFolderRow(entry.SubFolder, folderCollapsed);
                    any = true;
                }
            }

            if (folderCollapsed || !isFile) continue;
            any = true;
            DrawMcdfLocalFileRow(entry, string.IsNullOrEmpty(entry.SubFolder) ? 0f : indent);
        }

        if (!any)
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.NoResults"), ImGuiColors.DalamudGrey);
    }

    private void DrawMcdfFolderRow(string subFolder, bool collapsed)
    {
        int count = 0;
        foreach (var file in _localMcdfFiles)
        {
            if (!string.IsNullOrEmpty(file.FilePath) && string.Equals(file.SubFolder, subFolder, StringComparison.Ordinal))
                count++;
        }

        bool toggle = false;
        DrawMcdfRow("folder_" + subFolder, 0f, () =>
        {
            ImGui.BeginGroup();
            _uiSharedService.IconText(collapsed ? FontAwesomeIcon.CaretRight : FontAwesomeIcon.CaretDown, UiSharedService.AccentColor);
            ImGui.SameLine();
            _uiSharedService.IconText(collapsed ? FontAwesomeIcon.FolderClosed : FontAwesomeIcon.FolderOpen, UiSharedService.AccentColor);
            ImGui.SameLine();
            UiSharedService.ColorText(subFolder, UiSharedService.AccentColor);
            ImGui.SameLine();
            UiSharedService.ColorText(count.ToString(CultureInfo.CurrentCulture), ImGuiColors.DalamudGrey);
            ImGui.EndGroup();
            if (ImGui.IsItemClicked()) toggle = true;
        }, null,
        [
            new(FontAwesomeIcon.Trash, Loc.Get("CharaDataHub.Mcdf.Local.DeleteFolderTooltip"), true,
                () => RequestMcdfDelete(McdfDeleteKind.LocalFolder, subFolder, subFolder)),
        ], header: true);

        if (!toggle) return;
        if (collapsed) _collapsedMcdfFolders.Remove(subFolder);
        else _collapsedMcdfFolders.Add(subFolder);
    }

    private void DrawMcdfLocalFileRow(LocalMcdfEntry entry, float indent)
    {
        var meta = FormatFileSize(entry.FileSize) + " · " + entry.LastModified.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
        DrawMcdfRow(entry.FilePath, indent, () =>
        {
            _uiSharedService.IconText(FontAwesomeIcon.File, ImGuiColors.DalamudGrey);
            ImGui.SameLine();
            ImGui.TextUnformatted(entry.Description);
        }, meta,
        [
            new(FontAwesomeIcon.Upload, Loc.Get("CharaDataHub.Mcdf.Local.UploadTooltip"), !_mcdfShareManager.IsBusy, () =>
            {
                _logger.LogInformation("Uploading local MCDF file '{Description}' from {FilePath}", entry.Description, entry.FilePath);
                _ = _mcdfShareManager.CreateShareFromFileAsync(entry.Description, entry.FilePath, CancellationToken.None);
            }),
            new(FontAwesomeIcon.ShareAlt, Loc.Get("CharaDataHub.Mcdf.Share.ActionTooltip"), true,
                () => BeginMcdfShare(entry.FilePath, true, entry.Description)),
            new(FontAwesomeIcon.Trash, Loc.Get("CharaDataHub.Mcdf.Local.DeleteTooltip"), true,
                () => RequestMcdfDelete(McdfDeleteKind.LocalFile, entry.FilePath, entry.Description)),
        ]);
    }

    // Ligne de liste : fond arrondi qui s'éclaircit au survol, contenu à gauche, informations grises
    // puis boutons calés à droite. Un en-tête (dossier) n'a de fond qu'au survol.
    private void DrawMcdfRow(string id, float indent, Action drawLeft, string? meta, McdfRowAction[] actions, bool header = false)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float frameHeight = ImGui.GetFrameHeight();
        float rowHeight = frameHeight + 8f * scale;
        float buttonGap = 4f * scale;
        float width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorPos();
        var screen = ImGui.GetCursorScreenPos();
        var rowMax = screen + new Vector2(width, rowHeight);

        using var rowId = ImRaii.PushId(id);

        bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && ImGui.IsMouseHoveringRect(screen, rowMax);
        if (hovered || !header)
        {
            ImGui.GetWindowDrawList().AddRectFilled(screen, rowMax,
                ImGui.GetColorU32(hovered ? UiSharedService.ThemeFrameBgHovered with { W = 0.45f } : UiSharedService.ThemeHeaderBg),
                UiSharedService.RadiusControl * scale);
        }

        float contentY = start.Y + (rowHeight - frameHeight) / 2f;
        ImGui.SetCursorPos(new Vector2(start.X + 8f * scale + indent, contentY));
        ImGui.AlignTextToFramePadding();
        drawLeft();
        float leftRight = ImGui.GetItemRectMax().X - screen.X + start.X;

        float actionsWidth = 0f;
        foreach (var action in actions)
            actionsWidth += _uiSharedService.GetIconButtonSize(action.Icon).X;
        actionsWidth += buttonGap * Math.Max(0, actions.Length - 1);
        float x = start.X + width - 6f * scale - actionsWidth;

        if (!string.IsNullOrEmpty(meta))
        {
            float metaX = x - 14f * scale - ImGui.CalcTextSize(meta).X;
            // Trop étroit pour tout afficher : les informations passent en infobulle plutôt que sous le nom.
            if (metaX > leftRight + ImGui.GetStyle().ItemSpacing.X)
            {
                ImGui.SetCursorPos(new Vector2(metaX, contentY));
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(ImGuiColors.DalamudGrey, meta);
            }
            else if (hovered && !ImGui.IsAnyItemHovered())
            {
                ImGui.SetTooltip(meta);
            }
        }

        for (int i = 0; i < actions.Length; i++)
        {
            var action = actions[i];
            ImGui.SetCursorPos(new Vector2(x, contentY));
            using (ImRaii.PushId(i))
            using (ImRaii.Disabled(!action.Enabled))
            {
                if (_uiSharedService.IconButton(action.Icon))
                    action.OnClick();
            }
            UiSharedService.AttachToolTip(action.Tooltip);
            x += _uiSharedService.GetIconButtonSize(action.Icon).X + buttonGap;
        }

        ImGui.SetCursorPos(start);
        ImGui.Dummy(new Vector2(width, rowHeight));
    }

    private static void DrawMcdfSearch(string id, string hint, ref string value, float totalWidth)
    {
        var icon = FontAwesomeIcon.Search;
        float iconWidth = UiSharedService.GetIconSize(icon).X + ImGui.GetStyle().ItemSpacing.X;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ImGuiColors.DalamudGrey, icon.ToIconString());
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Max(120f * ImGuiHelpers.GlobalScale, totalWidth - iconWidth));
        ImGui.InputTextWithHint(id, hint, ref value, 128);
    }

    // Panneau ouvert depuis la barre d'outils, posé dans la carte sous celle-ci, sur toute sa largeur.
    private static void DrawMcdfInlinePanel(string id, Action<float> drawContent)
    {
        float innerWidth = ImGui.GetContentRegionAvail().X - 2f * UiSharedService.GetCardContentPaddingX();
        ImGuiHelpers.ScaledDummy(2f);
        UiSharedService.DrawCard(id, () =>
        {
            float wrapPos = ImGui.GetCursorPosX() + innerWidth;
            drawContent(wrapPos);
            ImGui.Dummy(new Vector2(innerWidth, 0f));
        }, background: UiSharedService.ThemeHighlightBg, border: UiSharedService.AccentColor with { W = 0.5f });
    }

    private void DrawMcdfExportContent(float wrapPos)
    {
        UiSharedService.DrawCardTitle(FontAwesomeIcon.FileExport, Loc.Get("CharaDataHub.Mcdf.Export.Title"));
        UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Export.Help"), ImGuiColors.DalamudGrey, wrapPos);
        ImGuiHelpers.ScaledDummy(3f);

        ImGui.Checkbox("##readExport", ref _readExport);
        ImGui.SameLine();
        UiSharedService.TextWrapped(Loc.Get("CharaDataHub.Mcdf.Export.Consent"), wrapPos);

        if (!_readExport) return;

        ImGuiHelpers.ScaledDummy(3f);
        ImGui.SetNextItemWidth(MathF.Min(320f * ImGuiHelpers.GlobalScale, wrapPos - ImGui.GetCursorPosX()));
        ImGui.InputTextWithHint("##exportDescription", Loc.Get("CharaDataHub.Mcdf.Export.DescriptionHint"), ref _exportDescription, 255);
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("CharaDataHub.Mcdf.Export.Button")))
        {
            string defaultFileName = string.IsNullOrEmpty(_exportDescription)
                ? "export.mcdf"
                : SanitizeFileName(_exportDescription, "export") + ".mcdf";
            _uiSharedService.FileDialogManager.SaveFileDialog(Loc.Get("CharaDataHub.Mcdf.Export.DialogTitle"), ".mcdf", defaultFileName, ".mcdf", (success, path) =>
            {
                if (!success) return;

                _configService.Current.LastSavedCharaDataLocation = Path.GetDirectoryName(path) ?? string.Empty;
                _configService.Save();

                _charaDataManager.SaveMareCharaFile(_exportDescription, path);
                _exportDescription = string.Empty;
                _localMcdfScanTime = DateTime.MinValue;
            }, Directory.Exists(_configService.Current.LastSavedCharaDataLocation) ? _configService.Current.LastSavedCharaDataLocation : null);
        }
        UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Export.Note"), UiSharedService.AccentColor, wrapPos);
    }

    private void DrawMcdfImportContent(float wrapPos)
    {
        UiSharedService.DrawCardTitle(FontAwesomeIcon.FileImport, Loc.Get("CharaDataHub.Mcdf.Import.Title"));
        UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Import.Help"), ImGuiColors.DalamudGrey, wrapPos);
        ImGuiHelpers.ScaledDummy(3f);

        if (_charaDataManager.LoadedMcdfHeader != null && !_charaDataManager.LoadedMcdfHeader.IsCompleted)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Import.Loading"), UiSharedService.AccentColor, wrapPos);
            return;
        }

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.FolderOpen, Loc.Get("CharaDataHub.Mcdf.Import.Load")))
        {
            _fileDialogManager.OpenFileDialog(Loc.Get("CharaDataHub.Mcdf.Import.PickFile"), ".mcdf", (success, paths) =>
            {
                if (!success) return;
                if (paths.FirstOrDefault() is not { } path) return;

                _configService.Current.LastSavedCharaDataLocation = Path.GetDirectoryName(path) ?? string.Empty;
                _configService.Save();

                _charaDataManager.LoadMcdf(path);
            }, 1, Directory.Exists(_configService.Current.LastSavedCharaDataLocation) ? _configService.Current.LastSavedCharaDataLocation : null);
        }
        UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcdf.Import.LoadTooltip"));

        if (_charaDataManager.LoadedMcdfHeader?.IsCompletedSuccessfully ?? false)
        {
            var loaded = _charaDataManager.LoadedMcdfHeader.Result.LoadedFile;
            float labelColumn = 120f * ImGuiHelpers.GlobalScale;
            ImGuiHelpers.ScaledDummy(3f);
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("CharaDataHub.Mcdf.Import.LoadedFile"));
            ImGui.SameLine(labelColumn);
            UiSharedService.TextWrapped(loaded.FilePath, wrapPos);
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("CharaDataHub.Apply.Description"));
            ImGui.SameLine(labelColumn);
            UiSharedService.TextWrapped(loaded.CharaFileData.Description, wrapPos);
            ImGuiHelpers.ScaledDummy(3f);

            var mcdfLocalFolder = _configService.Current.McdfLocalFolder;
            if (!string.IsNullOrEmpty(mcdfLocalFolder))
            {
                var importDir = Path.Combine(mcdfLocalFolder, "Import");
                var destPath = Path.Combine(importDir, Path.GetFileName(loaded.FilePath));
                bool alreadyExists = File.Exists(destPath);

                using (ImRaii.Disabled(alreadyExists))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("CharaDataHub.Mcdf.Import.SaveToImport")))
                    {
                        try
                        {
                            Directory.CreateDirectory(importDir);
                            File.Copy(loaded.FilePath, destPath, false);
                            _localMcdfScanTime = DateTime.MinValue;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to copy MCDF to import folder");
                        }
                    }
                }
                UiSharedService.AttachToolTip(alreadyExists
                    ? Loc.Get("CharaDataHub.Mcdf.Import.AlreadyExists")
                    : string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcdf.Import.SaveToImportTooltip"), importDir));
            }
            else
            {
                UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Import.NoFolderConfigured"), ImGuiColors.DalamudGrey, wrapPos);
            }

            ImGuiHelpers.ScaledDummy(3f);
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Import.ApplyHint"), ImGuiColors.DalamudGrey, wrapPos);
        }

        if ((_charaDataManager.LoadedMcdfHeader?.IsFaulted ?? false) || (_charaDataManager.McdfApplicationTask?.IsFaulted ?? false))
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Import.ReadError"), UiSharedService.AccentColor, wrapPos);
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Import.ReadErrorNote"), UiSharedService.AccentColor, wrapPos);
        }
    }

    private void RequestMcdfDelete(McdfDeleteKind kind, string target, string label)
    {
        _mcdfDeleteKind = kind;
        _mcdfDeleteTarget = target;
        _mcdfDeleteLabel = label;
        _mcdfOpenDeleteModal = true;
    }

    private void ClearMcdfDelete()
    {
        _mcdfDeleteKind = McdfDeleteKind.None;
        _mcdfDeleteTarget = string.Empty;
        _mcdfDeleteLabel = string.Empty;
    }

    private void DrawMcdfDeleteConfirm()
    {
        if (_mcdfOpenDeleteModal)
        {
            _mcdfOpenDeleteModal = false;
            _mcdfDeleteModalOpen = true;
            ImGui.OpenPopup(McdfDeletePopupId);
        }

        if (!ImGui.BeginPopupModal(McdfDeletePopupId, ref _mcdfDeleteModalOpen, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;

        string message = _mcdfDeleteKind switch
        {
            McdfDeleteKind.ServerShare => string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcdf.Delete.ServerConfirm"), _mcdfDeleteLabel),
            McdfDeleteKind.LocalFile => string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcdf.Delete.LocalConfirm"), _mcdfDeleteLabel),
            McdfDeleteKind.LocalFolder => string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcdf.Local.DeleteFolderConfirm"), _mcdfDeleteLabel,
                _localMcdfFiles.Count(f => !string.IsNullOrEmpty(f.FilePath) && string.Equals(f.SubFolder, _mcdfDeleteTarget, StringComparison.Ordinal))),
            _ => string.Empty,
        };

        UiSharedService.TextWrapped(message, ImGui.GetFontSize() * 26f);
        ImGuiHelpers.ScaledDummy(5f);
        using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DalamudRed with { W = 0.55f }))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ImGuiColors.DalamudRed with { W = 0.75f }))
        {
            if (ImGui.Button(Loc.Get("CharaDataHub.Mcdf.Local.DeleteFolderYes")))
            {
                ExecuteMcdfDelete();
                ClearMcdfDelete();
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button(Loc.Get("CharaDataHub.Mcdf.Local.DeleteFolderNo")))
        {
            ClearMcdfDelete();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void ExecuteMcdfDelete()
    {
        switch (_mcdfDeleteKind)
        {
            case McdfDeleteKind.ServerShare:
                if (!Guid.TryParse(_mcdfDeleteTarget, out var shareId)) return;
                _configService.Current.FavoriteCodes.Remove($"mcdf:{shareId:D}");
                _configService.Save();
                _ = _mcdfShareManager.DeleteShareAsync(shareId);
                break;

            case McdfDeleteKind.LocalFile:
                try
                {
                    _logger.LogInformation("Deleting local MCDF file '{Description}' at {FilePath}", _mcdfDeleteLabel, _mcdfDeleteTarget);
                    File.Delete(_mcdfDeleteTarget);
                    _localMcdfScanTime = DateTime.MinValue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete local MCDF file at {FilePath}", _mcdfDeleteTarget);
                }
                break;

            case McdfDeleteKind.LocalFolder:
                try
                {
                    var folderPath = Path.Combine(_configService.Current.McdfLocalFolder, _mcdfDeleteTarget);
                    if (Directory.Exists(folderPath))
                    {
                        Directory.Delete(folderPath, true);
                        _localMcdfScanTime = DateTime.MinValue;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete MCDF folder {Folder}", _mcdfDeleteTarget);
                }
                break;
        }
    }
}
