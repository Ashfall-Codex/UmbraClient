using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Stagehand.Api;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Dto.DecorCatalog;
using UmbraSync.Localization;
using UmbraSync.Modules.Housing.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.UI.Components;

namespace UmbraSync.UI;

public sealed partial class CharaDataHubUi
{
    private const int DecorTabFavorites = 1;
    private const int DecorTabMine = 2;
    private const int DecorDescriptionPreviewLength = 140;

    private ProfileImageCropEditor? _decorCropEditor;
    private bool _decorInitialized;
    private string _decorSearch = string.Empty;
    private int _decorSort;
    private int _decorSubTab;
    private int _decorPage;
    private Guid? _decorEditingId;
    private string _decorEditName = string.Empty;
    private string _decorEditDescription = string.Empty;
    private string? _decorEditPreview;
    private bool _decorEditPreviewError;
    private string _decorPublishSelectedFilename = string.Empty;
    private string _decorPublishName = string.Empty;
    private string _decorPublishDescription = string.Empty;
    private byte[]? _decorPublishPreview;
    private Task<IDalamudTextureWrap>? _decorPublishPreviewTask;
    private IDalamudTextureWrap? _decorPublishPreviewTexture;
    private bool _decorPublishPreviewError;

    private void DrawDecorCatalogPage()
    {
        var manager = _decorCatalogManager;
        var stages = _stageShareManager;
        if (manager == null || stages == null) return;

        if (!_uiSharedService.ApiController.IsConnected)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("HousingShare.ServerRequired"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(5);
            return;
        }

        _decorCropEditor ??= new ProfileImageCropEditor(_logger, _uiSharedService);
        _decorCropEditor.Draw();

        UiSharedService.BeginSectionCard(Loc.Get("DecorCatalog.Title"), FontAwesomeIcon.Store);
        UiSharedService.TextWrapped(Loc.Get("DecorCatalog.Intro"));
        if (!manager.IsStagehandAvailable)
            UiSharedService.ColorTextWrapped(Loc.Get("DecorCatalog.StagehandMissing"), ImGuiColors.DalamudYellow);

        if (!_decorInitialized && !manager.IsBusy)
        {
            _decorInitialized = true;
            DecorSearch();
        }

        if (manager.IsBusy || stages.IsBusy)
            UiSharedService.ColorTextWrapped(stages.ProgressStatus ?? Loc.Get("DecorCatalog.Processing"), ImGuiColors.DalamudYellow);
        if (!string.IsNullOrEmpty(manager.LastError))
            UiSharedService.ColorTextWrapped(manager.LastError!, ImGuiColors.DalamudRed);
        else if (!string.IsNullOrEmpty(manager.LastSuccess))
            UiSharedService.ColorTextWrapped(manager.LastSuccess!, ImGuiColors.HealerGreen);

        ImGuiHelpers.ScaledDummy(3);
        DrawDecorToolbar(manager);
        ImGuiHelpers.ScaledDummy(3);
        DrawDecorGrid(manager, stages);
        DrawDecorPagination(manager);

        if (_decorEditingId != null)
            DrawDecorEdit(manager, stages);

        DrawDecorPublish(manager, stages);
        DrawCatalogAppliedCard(stages);
        UiSharedService.EndSectionCard();
    }

    private void DecorSearch()
    {
        var manager = _decorCatalogManager;
        if (manager == null) return;
        _ = manager.SearchAsync(new DecorCatalogListRequestDto
        {
            SearchText = string.IsNullOrWhiteSpace(_decorSearch) ? null : _decorSearch.Trim(),
            Sort = (DecorCatalogSort)Math.Clamp(_decorSort, 0, 2),
            OnlyFavorites = _decorSubTab == DecorTabFavorites,
            OnlyMine = _decorSubTab == DecorTabMine,
            Page = Math.Max(0, _decorPage),
        });
    }

    private void DrawDecorToolbar(DecorCatalogManager manager)
    {
        float scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(240f * scale);
        if (ImGui.InputTextWithHint("##decorSearch", Loc.Get("DecorCatalog.Search.Hint"), ref _decorSearch, 100, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            _decorPage = 0;
            DecorSearch();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(manager.IsBusy))
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Search))
            {
                _decorPage = 0;
                DecorSearch();
            }
        }

        string[] sortLabels = [Loc.Get("DecorCatalog.Sort.Recent"), Loc.Get("DecorCatalog.Sort.Popular"), Loc.Get("DecorCatalog.Sort.MostFavorited")];
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f * scale);
        using (var combo = UiSharedService.Combo("##decorSort", sortLabels[Math.Clamp(_decorSort, 0, 2)]))
        {
            if (combo)
            {
                for (int i = 0; i < sortLabels.Length; i++)
                {
                    using var id = ImRaii.PushId(i);
                    if (ImGui.Selectable(sortLabels[i], i == _decorSort) && i != _decorSort)
                    {
                        _decorSort = i;
                        _decorPage = 0;
                        DecorSearch();
                    }
                }
            }
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(manager.IsBusy))
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Sync))
                DecorSearch();
        }
        UiSharedService.AttachToolTip(Loc.Get("DecorCatalog.Refresh"));

        int previousTab = _decorSubTab;
        DrawSubTabButtons(
            [Loc.Get("DecorCatalog.Tab.All"), Loc.Get("DecorCatalog.Tab.Favorites"), Loc.Get("DecorCatalog.Tab.Mine")],
            [FontAwesomeIcon.LayerGroup, FontAwesomeIcon.Star, FontAwesomeIcon.User],
            ref _decorSubTab, UiSharedService.AccentColor);
        if (previousTab != _decorSubTab)
        {
            _decorPage = 0;
            DecorSearch();
        }
    }

    private void DrawDecorGrid(DecorCatalogManager manager, StageShareManager stages)
    {
        var entries = manager.Entries;
        if (entries.Count == 0)
        {
            string key = _decorSubTab switch
            {
                DecorTabFavorites => "DecorCatalog.Empty.Favorites",
                DecorTabMine => "DecorCatalog.Empty.Mine",
                _ => "DecorCatalog.Empty",
            };
            ImGui.TextDisabled(Loc.Get(key));
            return;
        }

        float scale = ImGuiHelpers.GlobalScale;
        float cardWidth = 300f * scale;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        int columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (cardWidth + spacing)));
        int column = 0;
        foreach (var entry in entries.ToArray())
        {
            using var id = ImRaii.PushId(entry.Id.ToString("N"));
            if (column > 0) ImGui.SameLine();
            UiSharedService.DrawCard("decor_" + entry.Id.ToString("N"), () => DrawDecorCard(manager, stages, entry, cardWidth));
            column = (column + 1) % columns;
        }
    }

    private void DrawDecorCard(DecorCatalogManager manager, StageShareManager stages, DecorCatalogEntryDto entry, float cardWidth)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float inner = cardWidth - ImGui.GetStyle().FramePadding.X * 4f;
        ImGui.Dummy(new Vector2(inner, 1f * scale));

        var previewSize = new Vector2(inner, inner * 9f / 16f);
        var image = manager.TryGetPreview(entry);
        var texture = image.HasImage ? _uiSharedService.ProfileTextures.GetTexture(image) : null;
        if (texture != null && texture.Handle != IntPtr.Zero)
        {
            ImGui.Image(texture.Handle, previewSize);
        }
        else
        {
            var pos = ImGui.GetCursorScreenPos();
            var fill = UiSharedService.AccentColor with { W = 0.16f };
            ImGui.GetWindowDrawList().AddRectFilled(pos, pos + previewSize, UiSharedService.Color(fill), UiSharedService.RadiusControl * scale);
            ImGui.Dummy(previewSize);
            var after = ImGui.GetCursorPos();
            ImGui.SetCursorScreenPos(pos + previewSize / 2f - new Vector2(10f, 10f) * scale);
            _uiSharedService.IconText(FontAwesomeIcon.Images, UiSharedService.AccentColor with { W = 0.6f });
            ImGui.SetCursorPos(after);
        }

        UiSharedService.ColorText(entry.Name, UiSharedService.AccentColor);
        string author = FormatPairLabel(entry.OwnerUid);
        if (string.Equals(author, entry.OwnerUid, StringComparison.Ordinal) && !string.IsNullOrEmpty(entry.OwnerAlias))
            author = entry.OwnerAlias;
        ImGui.TextDisabled(string.Format(CultureInfo.CurrentCulture, Loc.Get("DecorCatalog.By"), author));

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            string preview = entry.Description.Length > DecorDescriptionPreviewLength
                ? entry.Description[..DecorDescriptionPreviewLength] + "…"
                : entry.Description;
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + inner);
            ImGui.TextUnformatted(preview);
            ImGui.PopTextWrapPos();
            if (preview.Length != entry.Description.Length)
                UiSharedService.AttachToolTip(entry.Description);
        }

        bool busy = manager.IsBusy || stages.IsBusy;
        using (ImRaii.PushColor(ImGuiCol.Text, UiSharedService.AccentColor, entry.IsFavorite))
        using (ImRaii.Disabled(busy))
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Star))
                _ = manager.SetFavoriteAsync(entry.Id, !entry.IsFavorite);
        }
        UiSharedService.AttachToolTip(entry.IsFavorite ? Loc.Get("DecorCatalog.Favorite.Remove") : Loc.Get("DecorCatalog.Favorite.Add"));
        ImGui.SameLine();
        ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DecorCatalog.Favorites.Count"), entry.FavoriteCount));
        ImGui.SameLine();
        ImGui.TextDisabled(string.Format(CultureInfo.CurrentCulture, Loc.Get("DecorCatalog.Displays"), entry.DisplayCount));
        ImGui.TextDisabled(string.Format(CultureInfo.CurrentCulture, Loc.Get("DecorCatalog.Files"), entry.FileCount, UiSharedService.ByteToString(entry.TotalBytes)));

        bool displayed = stages.AppliedStages.Any(a => a.Source == StageShareManager.AppliedStageSource.Catalog && a.ShareId == entry.Id && a.Visible);
        if (displayed)
        {
            using (ImRaii.Disabled(busy))
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.EyeSlash, Loc.Get("DecorCatalog.Hide")))
                    _ = stages.HideStageAsync(entry.Id);
            }
        }
        else
        {
            using (ImRaii.Disabled(busy || !manager.IsStagehandAvailable))
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Eye, Loc.Get("DecorCatalog.DisplayHere")))
                    _ = manager.DisplayHereAsync(entry);
            }
            UiSharedService.AttachToolTip(Loc.Get("DecorCatalog.DisplayHere.Tooltip"));
        }

        if (entry.IsOwner)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.Get("DecorCatalog.Edit")))
                ToggleDecorEdit(entry);
            ImGui.SameLine();
            using (ImRaii.Disabled(!UiSharedService.CtrlPressed() || busy))
            {
                if (ImGui.SmallButton(Loc.Get("DecorCatalog.Delete")))
                {
                    _ = manager.DeleteAsync(entry.Id);
                    if (_decorEditingId == entry.Id) _decorEditingId = null;
                }
            }
            UiSharedService.AttachToolTip(Loc.Get("DecorCatalog.Delete.Tooltip"));
        }
        else if (_decorReportPopup != null)
        {
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.Flag))
            {
                _decorReportPopup.Open(entry);
                Mediator.Publish(new OpenPopupHandlerMessage(_decorReportPopup));
            }
            UiSharedService.AttachToolTip(Loc.Get("DecorCatalog.Report"));
        }
    }

    private void ToggleDecorEdit(DecorCatalogEntryDto entry)
    {
        if (_decorEditingId == entry.Id)
        {
            _decorEditingId = null;
            return;
        }

        _decorEditingId = entry.Id;
        _decorEditName = entry.Name;
        _decorEditDescription = entry.Description;
        _decorEditPreview = null;
        _decorEditPreviewError = false;
    }

    private void DrawDecorPagination(DecorCatalogManager manager)
    {
        if (manager.TotalCount <= 0) return;
        int totalPages = Math.Max(1, (manager.TotalCount + DecorCatalogManager.PageSize - 1) / DecorCatalogManager.PageSize);
        ImGuiHelpers.ScaledDummy(3);
        using var disabled = ImRaii.Disabled(manager.IsBusy);
        if (_decorPage > 0 && _uiSharedService.IconButton(FontAwesomeIcon.ChevronLeft))
        {
            _decorPage--;
            DecorSearch();
        }
        if (_decorPage > 0) ImGui.SameLine();
        ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DecorCatalog.Page"), manager.Page + 1, totalPages, manager.TotalCount));
        if (_decorPage < totalPages - 1)
        {
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.ChevronRight))
            {
                _decorPage++;
                DecorSearch();
            }
        }
    }

    private void DrawDecorEdit(DecorCatalogManager manager, StageShareManager stages)
    {
        var entry = manager.Entries.FirstOrDefault(e => e.Id == _decorEditingId);
        if (entry == null)
        {
            _decorEditingId = null;
            return;
        }

        float scale = ImGuiHelpers.GlobalScale;
        UiSharedService.BeginSectionCard(string.Format(CultureInfo.CurrentCulture, Loc.Get("DecorCatalog.Edit.Title"), entry.Name), FontAwesomeIcon.Edit);
        ImGui.SetNextItemWidth(400f * scale);
        ImGui.InputTextWithHint("##decorEditName", Loc.Get("DecorCatalog.Name.Hint"), ref _decorEditName, DecorCatalogManager.MaxNameLength);
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("DecorCatalog.Name"));
        ImGui.InputTextMultiline("##decorEditDescription", ref _decorEditDescription, DecorCatalogManager.MaxDescriptionLength, new Vector2(400f * scale, 80f * scale));
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("DecorCatalog.Description"));

        string previewState = _decorEditPreview switch
        {
            null => entry.PreviewHash != null ? Loc.Get("DecorCatalog.Preview.Kept") : Loc.Get("DecorCatalog.Preview.None"),
            "" => Loc.Get("DecorCatalog.Preview.Removed"),
            _ => Loc.Get("DecorCatalog.Preview.New"),
        };
        ImGui.TextUnformatted(Loc.Get("DecorCatalog.Preview") + " : " + previewState);
        if (_decorEditPreviewError)
            UiSharedService.ColorTextWrapped(Loc.Get("DecorCatalog.Preview.Error"), ImGuiColors.DalamudRed);
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Image, Loc.Get("DecorCatalog.Preview.Change")))
        {
            _decorEditPreviewError = false;
            _uiSharedService.FileDialogManager.OpenFileDialog(Loc.Get("DecorCatalog.Preview.Dialog"), "Image files{.png,.jpg,.jpeg}", (success, path) =>
            {
                if (!success) return;
                _decorCropEditor?.Open(path, ProfileImageKind.DecorPreview,
                    bytes => _decorEditPreview = Convert.ToBase64String(bytes),
                    () => _decorEditPreviewError = true);
            });
        }
        if (entry.PreviewHash != null || !string.IsNullOrEmpty(_decorEditPreview))
        {
            ImGui.SameLine();
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, Loc.Get("DecorCatalog.Preview.Remove")))
                _decorEditPreview = string.Empty;
        }

        ImGuiHelpers.ScaledDummy(3);
        using (ImRaii.Disabled(manager.IsBusy || string.IsNullOrWhiteSpace(_decorEditName)))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("DecorCatalog.Edit.Save")))
            {
                _ = manager.UpdateAsync(entry.Id, _decorEditName, _decorEditDescription, _decorEditPreview);
                _decorEditingId = null;
            }
        }
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, Loc.Get("DecorCatalog.Edit.Cancel")))
            _decorEditingId = null;

        LocalStageDefinition? source = FindLocalStage(stages, manager.GetSourceFile(entry.Id));
        if (source != null)
        {
            ImGui.SameLine();
            using (ImRaii.Disabled(manager.IsBusy || stages.IsBusy))
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, Loc.Get("DecorCatalog.UpdateContent")))
                {
                    _ = manager.PublishAsync(source.Value, entry.Name, entry.Description, null, entry.Id);
                    _decorEditingId = null;
                }
            }
            UiSharedService.AttachToolTip(Loc.Get("DecorCatalog.UpdateContent.Tooltip"));
        }
    }

    private LocalStageDefinition? FindLocalStage(StageShareManager stages, string? sourcePath)
    {
        if (sourcePath == null) return null;
        _localStagesPoll ??= new PolledUiValue<LocalStageDefinition[]>(stages.GetLocalStagesAsync, LocalStagesPollInterval, []);
        foreach (var local in _localStagesPoll.Poll(_logger))
        {
            if (string.Equals(stages.ResolveLocalStagePath(local), sourcePath, StringComparison.OrdinalIgnoreCase))
                return local;
        }
        return null;
    }

    private void DrawDecorPublish(DecorCatalogManager manager, StageShareManager stages)
    {
        UiSharedService.BeginSectionCard(Loc.Get("DecorCatalog.Publish.Title"), FontAwesomeIcon.Upload);
        float scale = ImGuiHelpers.GlobalScale;

        _localStagesPoll ??= new PolledUiValue<LocalStageDefinition[]>(stages.GetLocalStagesAsync, LocalStagesPollInterval, []);
        LocalStageDefinition[] localStages = _localStagesPoll.Poll(_logger);
        if (localStages.Length == 0)
        {
            ImGui.TextDisabled(Loc.Get("Stages.NoLocalStage"));
            return;
        }

        int selectedIndex = Array.FindIndex(localStages, s => string.Equals(s.Filename, _decorPublishSelectedFilename, StringComparison.Ordinal));
        string preview = selectedIndex >= 0 ? StageLabel(localStages[selectedIndex]) : Loc.Get("Stages.SelectStage");
        ImGui.SetNextItemWidth(300f * scale);
        using (var combo = UiSharedService.Combo("##decorPublishSelect", preview))
        {
            if (combo)
            {
                for (int i = 0; i < localStages.Length; i++)
                {
                    using var id = ImRaii.PushId(i);
                    if (ImGui.Selectable(StageLabel(localStages[i]), i == selectedIndex))
                    {
                        _decorPublishSelectedFilename = localStages[i].Filename;
                        if (string.IsNullOrWhiteSpace(_decorPublishName))
                            _decorPublishName = StageLabel(localStages[i]);
                    }
                }
            }
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("Stages.LocalStage"));

        bool selectedMissing = false;
        if (selectedIndex >= 0)
        {
            string? path = stages.ResolveLocalStagePath(localStages[selectedIndex]);
            selectedMissing = path == null || !File.Exists(path);
            if (selectedMissing)
                UiSharedService.ColorTextWrapped(Loc.Get("Stages.Error.FileNotFound"), ImGuiColors.DalamudRed);
        }

        ImGui.SetNextItemWidth(400f * scale);
        ImGui.InputTextWithHint("##decorPublishName", Loc.Get("DecorCatalog.Name.Hint"), ref _decorPublishName, DecorCatalogManager.MaxNameLength);
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("DecorCatalog.Name"));
        ImGui.InputTextMultiline("##decorPublishDescription", ref _decorPublishDescription, DecorCatalogManager.MaxDescriptionLength, new Vector2(400f * scale, 80f * scale));
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("DecorCatalog.Description"));

        if (UiSharedService.TryConsumeUiTask(ref _decorPublishPreviewTask, _logger, out IDalamudTextureWrap? loaded) && loaded != null)
        {
            _decorPublishPreviewTexture?.Dispose();
            _decorPublishPreviewTexture = loaded;
        }
        if (_decorPublishPreviewTexture != null && _decorPublishPreview != null)
        {
            float width = 240f * scale;
            ImGui.Image(_decorPublishPreviewTexture.Handle, new Vector2(width, width * 9f / 16f));
        }
        else
        {
            ImGui.TextDisabled(Loc.Get("DecorCatalog.Preview.None"));
        }
        if (_decorPublishPreviewError)
            UiSharedService.ColorTextWrapped(Loc.Get("DecorCatalog.Preview.Error"), ImGuiColors.DalamudRed);
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Image, _decorPublishPreview == null ? Loc.Get("DecorCatalog.Preview.Select") : Loc.Get("DecorCatalog.Preview.Change")))
        {
            _decorPublishPreviewError = false;
            _uiSharedService.FileDialogManager.OpenFileDialog(Loc.Get("DecorCatalog.Preview.Dialog"), "Image files{.png,.jpg,.jpeg}", (success, path) =>
            {
                if (!success) return;
                _decorCropEditor?.Open(path, ProfileImageKind.DecorPreview, bytes =>
                {
                    _decorPublishPreview = bytes;
                    _decorPublishPreviewTask = _uiSharedService.LoadImageAsync(bytes);
                }, () => _decorPublishPreviewError = true);
            });
        }
        if (_decorPublishPreview != null)
        {
            ImGui.SameLine();
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, Loc.Get("DecorCatalog.Preview.Remove")))
                ClearDecorPublishPreview();
        }

        ImGuiHelpers.ScaledDummy(3);
        bool canPublish = selectedIndex >= 0 && !selectedMissing && !string.IsNullOrWhiteSpace(_decorPublishName)
                          && manager.IsStagehandAvailable && !manager.IsBusy && !stages.IsBusy;
        using (ImRaii.Disabled(!canPublish))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, Loc.Get("DecorCatalog.Publish")) && canPublish)
            {
                _ = manager.PublishAsync(localStages[selectedIndex], _decorPublishName, _decorPublishDescription, _decorPublishPreview);
                _decorPublishName = string.Empty;
                _decorPublishDescription = string.Empty;
                ClearDecorPublishPreview();
            }
        }
        UiSharedService.AttachToolTip(Loc.Get("DecorCatalog.Publish.Tooltip"));
    }

    private void ClearDecorPublishPreview()
    {
        _decorPublishPreview = null;
        _decorPublishPreviewTask = null;
        _decorPublishPreviewTexture?.Dispose();
        _decorPublishPreviewTexture = null;
    }

    private void DrawCatalogAppliedCard(StageShareManager stages)
    {
        if (!stages.AppliedStages.Any(a => a.Source == StageShareManager.AppliedStageSource.Catalog)) return;

        UiSharedService.BeginSectionCard(Loc.Get("Stages.AppliedHere"), FontAwesomeIcon.Eye);
        DrawCatalogAppliedRows(stages);

        ImGuiHelpers.ScaledDummy(3);
        using (ImRaii.Disabled(stages.IsBusy))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.EyeSlash, Loc.Get("Stages.HideAll")))
                _ = stages.RemoveAllAppliedAsync("demande manuelle");
        }
        UiSharedService.AttachToolTip(Loc.Get("Stages.HideAll.Tooltip"));
    }

    private void DrawCatalogAppliedRows(StageShareManager stages)
    {
        foreach (var stage in stages.AppliedStages.Where(a => a.Source == StageShareManager.AppliedStageSource.Catalog).ToList())
        {
            using var id = ImRaii.PushId(stage.ShareId.ToString("N"));
            using (ImRaii.Disabled(stages.IsBusy))
            {
                if (_uiSharedService.IconButton(stage.Visible ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye))
                    _ = stage.Visible ? stages.HideStageAsync(stage.ShareId) : stages.ShowAppliedAsync(stage.ShareId);
            }
            UiSharedService.AttachToolTip(stage.Visible ? Loc.Get("Stages.Hide") : Loc.Get("Stages.Show"));
            ImGui.SameLine();
            if (stage.Visible)
                UiSharedService.ColorText(stage.StageName, UiSharedService.AccentColor);
            else
                ImGui.TextUnformatted(stage.StageName);
            ImGui.SameLine();
            ImGui.TextDisabled(Loc.Get("DecorCatalog.AppliedOrigin") + " · " + FormatPairLabel(stage.OwnerUid));
        }
    }
}
