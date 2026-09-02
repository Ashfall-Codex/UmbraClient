using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using UmbraSync.Services;
using UmbraSync.Services.Housing;
using UmbraSync.Services.Mediator;
using UmbraSync.Localization;

namespace UmbraSync.UI;

public sealed class HousingNpcSceneEditorUi : WindowMediatorSubscriberBase
{
    private readonly HousingNpcScenarioService _service;
    private readonly HousingScenarioManager _scenarioManager;
    private readonly UiSharedService _uiShared;
    private readonly IDataManager _dataManager;
    private readonly ITextureProvider _textureProvider;
    private readonly NpcPoseCatalog _poseCatalog;
    private string _selectedSceneId = string.Empty;
    private string _emoteFilter = string.Empty;
    private List<(ushort Id, string Name, uint Icon)>? _emotes;
    private List<(ushort Id, string Key)>? _timelines;
    private string _timelineFilter = string.Empty;
    private readonly HashSet<uint> _badIcons = new();
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal); 
    private readonly Dictionary<string, int> _addActionKind = new(StringComparer.Ordinal);
    private IReadOnlyList<(string Path, string Title)>? _arrList;
    private List<(Guid Id, string Name)>? _glamourerDesigns;
    private string _glamourerFilter = string.Empty;
    private bool _loadingDesigns;
    private string _replaceAppearanceEntryId = string.Empty;
    private bool _designPopupRequested;

    public HousingNpcSceneEditorUi(ILogger<HousingNpcSceneEditorUi> logger, MareMediator mediator,
        HousingNpcScenarioService service, HousingScenarioManager scenarioManager, UiSharedService uiShared,
        IDataManager dataManager, ITextureProvider textureProvider, NpcPoseCatalog poseCatalog,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, Loc.Get("HousingNpc.Editor.Title") + "###HousingNpcSceneEditor", performanceCollectorService)
    {
        _service = service;
        _scenarioManager = scenarioManager;
        _uiShared = uiShared;
        _dataManager = dataManager;
        _textureProvider = textureProvider;
        _poseCatalog = poseCatalog;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 360),
            MaximumSize = new Vector2(950, 1300),
        };
    }

    protected override void DrawInternal()
    {
        var loc = _service.CurrentLocation;
        if (loc == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.EnterHousing"));
            return;
        }

        var l = loc.Value;
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            string.Format(Loc.Get("HousingNpc.Editor.RoomInfo"), l.ServerId, l.WardId, l.HouseId, l.DivisionId, l.RoomId));
        ImGui.Separator();

        var scenes = _service.ScenesForCurrentRoom();

        DrawSpawnedSummary(scenes);

        if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("HousingNpc.Editor.NewScene")))
            _ = _service.CreateSceneAsync(Loc.Get("HousingNpc.Editor.NewScene"));
        ImGui.SameLine();
        if (_uiShared.IconTextButton(FontAwesomeIcon.FileImport, Loc.Get("HousingNpc.Editor.ImportArr")))
        {
            _arrList = _service.ListArrScenarios();
            ImGui.OpenPopup("arr-import");
        }
        UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.ImportArrTip"));
        DrawArrImportPopup();
        ImGuiHelpers.ScaledDummy(4f);

        if (scenes.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.NoScenes"));
            DrawDelegatedShares();
            DrawOrphanScenes();
            return;
        }

        if (scenes.All(s => !string.Equals(s.Id, _selectedSceneId, StringComparison.Ordinal)))
            _selectedSceneId = scenes[0].Id;

        bool dirty = false;
        string? sceneToRemove = null;

        foreach (var scene in scenes)
        {
            using var id = ImRaii.PushId("scene-" + scene.Id);

            var enabled = scene.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled)) { scene.Enabled = enabled; dirty = true; }
            UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.SceneToggleTip"));
            ImGui.SameLine();

            bool delegated = scene.LinkedShareIsDelegated;
            var label = delegated
                ? $"{scene.Title} ({scene.Entries.Count})  •  {Loc.Get("HousingNpc.Editor.DelegatedBadge")}"
                : $"{scene.Title} ({scene.Entries.Count})";

            bool selected = string.Equals(scene.Id, _selectedSceneId, StringComparison.Ordinal);
            if (ImGui.Selectable($"{label}##sel", selected,
                    ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X - 30 * ImGuiHelpers.GlobalScale, 0)))
                _selectedSceneId = scene.Id;
            if (delegated) UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.DelegatedBadgeTip"));

            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DalamudRed))
            {
                if (_uiShared.IconButton(FontAwesomeIcon.Trash) && UiSharedService.CtrlPressed())
                    sceneToRemove = scene.Id;
            }
            UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.DeleteSceneTip"));
        }

        if (sceneToRemove != null) { _ = _service.RemoveSceneAsync(sceneToRemove); return; }

        var current = scenes.FirstOrDefault(s => string.Equals(s.Id, _selectedSceneId, StringComparison.Ordinal));
        if (current == null)
        {
            if (dirty) _ = _service.PersistAndRefreshAsync();
            return;
        }

        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        // --- Scène sélectionnée : nom + PNJ ---
        var title = current.Title;
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.SceneName"));
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.InputText("##title", ref title, 64)) { current.Title = title; dirty = true; }

        ImGuiHelpers.ScaledDummy(2f);
        if (_uiShared.IconTextButton(FontAwesomeIcon.User, Loc.Get("HousingNpc.Editor.CaptureSelf")))
            _ = _service.AddFromSelfAsync(current.Id, string.Empty);
        UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.CaptureSelfTip"));
        ImGui.SameLine();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Magic, Loc.Get("HousingNpc.Editor.CaptureSelfLive")))
            _ = _service.AddFromSelfLiveAsync(current.Id, string.Empty);
        UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.CaptureSelfLiveTip"));
        ImGui.SameLine();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Vest, Loc.Get("HousingNpc.Editor.CaptureGlamourer")))
        {
            _replaceAppearanceEntryId = string.Empty;
            _glamourerFilter = string.Empty;
            _loadingDesigns = true;
            _glamourerDesigns = null;
            _ = LoadGlamourerDesignsAsync();
            ImGui.OpenPopup("##glamourerDesigns");
        }
        UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.CaptureGlamourerTip"));
        if (_designPopupRequested)
        {
            _designPopupRequested = false;
            ImGui.OpenPopup("##glamourerDesigns");
        }
        DrawGlamourerDesignPopup(current.Id);
        if (_uiShared.IconTextButton(FontAwesomeIcon.FileImport, Loc.Get("HousingNpc.Editor.ImportChara")))
        {
            var sceneId = current.Id;
            _uiShared.FileDialogManager.OpenFileDialog(Loc.Get("HousingNpc.Editor.ImportPickFile"), ".chara", (success, paths) =>
            {
                if (!success) return;
                if (paths.FirstOrDefault() is not { } path) return;
                _ = _service.AddFromCharaFileAsync(sceneId, path);
            }, 1);
        }
        UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.ImportCharaTip"));
        ImGui.SameLine();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Sync, Loc.Get("HousingNpc.Editor.Refresh")))
            _ = _service.RefreshAsync();

        ImGuiHelpers.ScaledDummy(4f);

        if (current.Entries.Count == 0)
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.NoNpc"));

        if (current.Entries.Count > 1)
        {
            bool allCollapsed = current.Entries.All(e => _collapsed.Contains(e.Id));
            if (_uiShared.IconTextButton(allCollapsed ? FontAwesomeIcon.ExpandAlt : FontAwesomeIcon.CompressAlt,
                    Loc.Get(allCollapsed ? "HousingNpc.Editor.ExpandAll" : "HousingNpc.Editor.CollapseAll")))
            {
                foreach (var e in current.Entries)
                {
                    if (allCollapsed) _collapsed.Remove(e.Id); else _collapsed.Add(e.Id);
                }
            }
            ImGuiHelpers.ScaledDummy(2f);
        }

        string? entryToRemove = null;
        foreach (var entry in current.Entries)
        {
            using var id = ImRaii.PushId("npc-" + entry.Id);
            UiSharedService.DrawCard($"card-{entry.Id}", () =>
            {
                bool collapsed = _collapsed.Contains(entry.Id);
                if (_uiShared.IconButton(collapsed ? FontAwesomeIcon.ChevronRight : FontAwesomeIcon.ChevronDown))
                {
                    if (collapsed) _collapsed.Remove(entry.Id); else _collapsed.Add(entry.Id);
                    collapsed = !collapsed;
                }
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.CollapseTip"));
                ImGui.SameLine();

                // Sélection explicite : ce PNJ devient la cible du gizmo en monde. Bouton toujours
                // visible (indispensable quand la liste est longue) ; le sélectionné est mis en avant.
                bool isSelected = string.Equals(entry.Id, _service.SelectedEntryId, StringComparison.Ordinal);
                using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.HealerGreen, isSelected))
                {
                    if (_uiShared.IconButton(FontAwesomeIcon.LocationCrosshairs))
                        _service.SelectedEntryId = isSelected ? string.Empty : entry.Id;
                }
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.SelectTip"));
                ImGui.SameLine();
                _uiShared.IconText(isSelected ? FontAwesomeIcon.LocationArrow : FontAwesomeIcon.User);
                ImGui.SameLine();
                var name = entry.DisplayName;
                if (collapsed)
                {
                    ImGui.TextUnformatted(string.IsNullOrWhiteSpace(name) ? Loc.Get("HousingNpc.Editor.NpcName") : name);
                    if (entry.Actions.Count > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(ImGuiColors.DalamudGrey, string.Format(Loc.Get("HousingNpc.Editor.Sequence"), entry.Actions.Count));
                    }
                    return;
                }
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                if (ImGui.InputTextWithHint("##name", Loc.Get("HousingNpc.Editor.NpcName"), ref name, 64)) { entry.DisplayName = name; dirty = true; }

                var pos = new Vector3(entry.X, entry.Y, entry.Z);
                ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputFloat3(Loc.Get("HousingNpc.Editor.Position"), ref pos)) { entry.X = pos.X; entry.Y = pos.Y; entry.Z = pos.Z; dirty = true; }

                var rot = entry.Rotation;
                ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
                if (ImGui.SliderFloat(Loc.Get("HousingNpc.Editor.Rotation"), ref rot, -3.14159f, 3.14159f)) { entry.Rotation = rot; dirty = true; }

                var face = entry.FacePlayer;
                if (ImGui.Checkbox(Loc.Get("HousingNpc.Editor.FacePlayer"), ref face)) { entry.FacePlayer = face; dirty = true; }
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.FacePlayerTip"));

                var hideWeapon = entry.Appearance.HideWeapon;
                if (ImGui.Checkbox(Loc.Get("HousingNpc.Editor.HideWeapon"), ref hideWeapon)) { entry.Appearance.HideWeapon = hideWeapon; dirty = true; }
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.HideWeaponTip"));

                if (DrawBasePose(entry)) dirty = true;

                if (DrawActions(current.Id, entry)) dirty = true;

                ImGui.Separator();
                ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.ReplaceAppearance"));
                if (_uiShared.IconTextButton(FontAwesomeIcon.Vest, Loc.Get("HousingNpc.Editor.ReplaceFromDesign")))
                {
                    _replaceAppearanceEntryId = entry.Id;
                    _glamourerFilter = string.Empty;
                    _loadingDesigns = true;
                    _glamourerDesigns = null;
                    _ = LoadGlamourerDesignsAsync();
                    _designPopupRequested = true;
                }
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.ReplaceFromDesignTip"));
                ImGui.SameLine();
                if (_uiShared.IconTextButton(FontAwesomeIcon.Magic, Loc.Get("HousingNpc.Editor.ReplaceFromSelfLive")))
                    _ = _service.ReplaceEntryAppearanceFromSelfAsync(current.Id, entry.Id, includeLive: true);
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.ReplaceFromSelfLiveTip"));
                ImGui.SameLine();
                if (_uiShared.IconTextButton(FontAwesomeIcon.User, Loc.Get("HousingNpc.Editor.ReplaceFromSelf")))
                    _ = _service.ReplaceEntryAppearanceFromSelfAsync(current.Id, entry.Id, includeLive: false);
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.ReplaceFromSelfTip"));
                ImGui.SameLine();
                if (_uiShared.IconTextButton(FontAwesomeIcon.FileImport, Loc.Get("HousingNpc.Editor.ReplaceFromChara")))
                {
                    // Capturés maintenant : le callback survit à la disparition de la ligne d'UI.
                    var sceneId = current.Id;
                    var targetEntryId = entry.Id;
                    _uiShared.FileDialogManager.OpenFileDialog(Loc.Get("HousingNpc.Editor.ImportPickFile"), ".chara", (success, paths) =>
                    {
                        if (!success) return;
                        if (paths.FirstOrDefault() is not { } path) return;
                        _ = _service.ReplaceEntryAppearanceFromCharaFileAsync(sceneId, targetEntryId, path);
                    }, 1);
                }
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.ReplaceFromCharaTip"));

                ImGui.Separator();
                if (_uiShared.IconTextButton(FontAwesomeIcon.Crosshairs, Loc.Get("HousingNpc.Editor.PlaceHere")))
                    _ = _service.MoveEntryToPlayerAsync(current.Id, entry.Id);
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DalamudRed))
                {
                    if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("HousingNpc.Editor.Delete")) && UiSharedService.CtrlPressed())
                        entryToRemove = entry.Id;
                }
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.DeleteTip"));
            }, stretchWidth: true);
            ImGuiHelpers.ScaledDummy(3f);
        }

        if (entryToRemove != null) { _ = _service.RemoveEntryAsync(current.Id, entryToRemove); return; }

        ImGui.Separator();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Check, Loc.Get("HousingNpc.Editor.Apply")))
            _ = _service.PersistAndRefreshAsync();
        if (dirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudYellow, Loc.Get("HousingNpc.Editor.Unsaved"));
        }

        if (!string.IsNullOrEmpty(current.LinkedShareId))
        {
            var republishLabel = current.LinkedShareIsDelegated
                ? Loc.Get("HousingNpc.Editor.DelegatedRepublish")
                : Loc.Get("HousingNpc.Editor.OwnRepublish");

            ImGui.SameLine();
            using (ImRaii.Disabled(_scenarioManager.IsBusy))
            {
                if (_uiShared.IconTextButton(FontAwesomeIcon.PaperPlane, republishLabel))
                {
                    _service.PersistScenes();
                    _ = _scenarioManager.RepublishEditedSceneAsync(current);
                }
            }
            UiSharedService.AttachToolTip(Loc.Get(current.LinkedShareIsDelegated
                ? "HousingNpc.Editor.DelegatedRepublishTip"
                : "HousingNpc.Editor.OwnRepublishTip"));

            // Un refus (conflit d'édition, droit retiré) doit se voir ici : c'est le seul endroit où
            // l'on republie, et le hub housing n'est pas forcément ouvert.
            if (!string.IsNullOrEmpty(_scenarioManager.LastError))
            {
                UiSharedService.ColorTextWrapped(_scenarioManager.LastError, ImGuiColors.DalamudRed);
                DrawForceRepublish(current);
            }
            else if (!string.IsNullOrEmpty(_scenarioManager.LastSuccess))
                UiSharedService.ColorTextWrapped(_scenarioManager.LastSuccess, ImGuiColors.HealerGreen);
        }

        DrawDelegatedShares();
        DrawOrphanScenes();
    }
    
    private void DrawForceRepublish(HousingNpcScenario current)
    {
        if (_scenarioManager.ConflictShareId is not { } conflict) return;
        if (!Guid.TryParseExact(current.LinkedShareId, "N", out var linked) || linked != conflict) return;

        using (ImRaii.Disabled(_scenarioManager.IsBusy))
        using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DalamudOrange))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.ExclamationTriangle, Loc.Get("HousingNpc.Editor.ForceRepublish"))
                && UiSharedService.CtrlPressed())
            {
                _service.PersistScenes();
                _ = _scenarioManager.RepublishEditedSceneAsync(current, overwriteRemote: true);
            }
        }
        UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.ForceRepublishTip"));
    }

    /// <summary>
    /// Scènes appartenant à d'autres joueurs, dont ils nous ont confié la modification. Les
    /// récupérer crée une copie de travail locale, éditable comme n'importe quelle scène.
    /// </summary>
    private void DrawDelegatedShares()
    {
        var editable = _scenarioManager.EditableSharesHere;
        if (editable.Count == 0) return;

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        if (!ImGui.CollapsingHeader(Loc.Get("HousingNpc.Editor.DelegatedHeader")))
            return;

        foreach (var share in editable)
        {
            using var id = ImRaii.PushId("delegated-" + share.Id);

            var owner = string.IsNullOrEmpty(share.OwnerAlias) ? share.OwnerUid : share.OwnerAlias;
            using (ImRaii.Disabled(_scenarioManager.IsBusy))
            {
                if (_uiShared.IconTextButton(FontAwesomeIcon.Download, Loc.Get("HousingNpc.Editor.DelegatedImport")))
                    _ = _scenarioManager.ImportSharedSceneForEditingAsync(share.Id);
            }
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingNpc.Editor.DelegatedImportTip"), owner));

            ImGui.SameLine();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(share.Description) ? owner : $"{share.Description} — {owner}");
        }
    }
    
    private void DrawSpawnedSummary(List<HousingNpcScenario> scenes)
    {
        var (total, shared) = _service.SpawnedCounts;
        int enabledScenes = scenes.Count(s => s.Enabled);

        if (total == 0 && enabledScenes == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.NoneSpawned"));
            UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.NoneSpawnedTip"));
            ImGuiHelpers.ScaledDummy(4f);
            return;
        }

        var summary = shared > 0
            ? string.Format(Loc.Get("HousingNpc.Editor.SpawnedSummaryWithShared"), total, shared)
            : string.Format(Loc.Get("HousingNpc.Editor.SpawnedSummary"), total);
        ImGui.TextColored(total > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudGrey, summary);
        UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.SpawnedSummaryTip"));

        using (ImRaii.Disabled(total == 0))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.EyeSlash, Loc.Get("HousingNpc.Editor.DespawnVisible")))
                _ = _service.DespawnVisibleAsync();
        }
        UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.DespawnVisibleTip"));

        if (enabledScenes > 0)
        {
            ImGui.SameLine();
            if (_uiShared.IconTextButton(FontAwesomeIcon.PowerOff, Loc.Get("HousingNpc.Editor.DisableAllScenes")))
                _ = _service.SetAllScenesEnabledAsync(false);
            UiSharedService.AttachToolTip(string.Format(Loc.Get("HousingNpc.Editor.DisableAllScenesTip"), enabledScenes));
        }

        ImGuiHelpers.ScaledDummy(4f);
    }

    private void DrawOrphanScenes()
    {
        var orphans = _service.OrphanScenes();
        if (orphans.Count == 0) return;

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        if (!ImGui.CollapsingHeader(string.Format(Loc.Get("HousingNpc.Editor.OrphanHeader"), orphans.Count)))
            return;

        ImGui.TextWrapped(Loc.Get("HousingNpc.Editor.OrphanHelp"));
        ImGuiHelpers.ScaledDummy(3f);

        foreach (var scene in orphans)
        {
            using var id = ImRaii.PushId("orphan-" + scene.Id);

            var compatible = _service.IsLayoutCompatible(scene);
            bool needsConfirm = compatible != true;

            using (ImRaii.Disabled(needsConfirm && !UiSharedService.CtrlPressed()))
            {
                if (_uiShared.IconTextButton(FontAwesomeIcon.PeopleCarry, Loc.Get("HousingNpc.Editor.OrphanMoveHere")))
                    _ = _service.ReassignSceneToCurrentAsync(scene.Id);
            }
            UiSharedService.AttachToolTip(compatible switch
            {
                true => Loc.Get("HousingNpc.Editor.OrphanMoveTipSameLayout"),
                false => Loc.Get("HousingNpc.Editor.OrphanMoveTipOtherLayout"),
                null => Loc.Get("HousingNpc.Editor.OrphanMoveTipUnknownLayout"),
            });

            ImGui.SameLine();
            ImGui.TextUnformatted($"{scene.Title} ({scene.Entries.Count})");
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey,
                string.Format(Loc.Get("HousingNpc.Editor.OrphanOrigin"), scene.ServerId, scene.WardId, scene.HouseId, scene.RoomId));

            if (compatible == false)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudYellow, Loc.Get("HousingNpc.Editor.OrphanLayoutWarn"));
            }
            else if (compatible == null)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey3, Loc.Get("HousingNpc.Editor.OrphanLayoutUnknown"));
            }
        }
    }


    private void DrawArrImportPopup()
    {
        using var popup = ImRaii.Popup("arr-import");
        if (!popup) return;

        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.ImportArrPick"));
        ImGui.Separator();

        if (_arrList == null || _arrList.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.ImportArrEmpty"));
        }
        else
        {
            foreach (var (path, title) in _arrList)
            {
                if (ImGui.Selectable(title + "##" + path))
                {
                    _ = _service.ImportArrScenarioAsync(path);
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.Separator();
        if (_uiShared.IconTextButton(FontAwesomeIcon.FolderOpen, Loc.Get("HousingNpc.Editor.ImportArrBrowse")))
        {
            ImGui.CloseCurrentPopup();
            _uiShared.FileDialogManager.OpenFileDialog(Loc.Get("HousingNpc.Editor.ImportArrPickFile"), ".json", (success, paths) =>
            {
                if (!success) return;
                if (paths.FirstOrDefault() is not { } p) return;
                _ = _service.ImportArrScenarioAsync(p);
            }, 1);
        }
    }


    private async Task LoadGlamourerDesignsAsync()
    {
        try
        {
            var designs = await _service.GetGlamourerDesignsAsync().ConfigureAwait(false);
            _glamourerDesigns = designs;
        }
        finally
        {
            _loadingDesigns = false;
        }
    }

    private void DrawGlamourerDesignPopup(string sceneId)
    {
        using var popup = ImRaii.Popup("##glamourerDesigns");
        if (!popup) return;

        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.CaptureGlamourerPick"));
        ImGui.Separator();

        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##glamourerFilter", Loc.Get("HousingNpc.Editor.Filter"), ref _glamourerFilter, 64);

        if (_loadingDesigns)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.CaptureGlamourerLoading"));
            return;
        }
        if (_glamourerDesigns == null || _glamourerDesigns.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.CaptureGlamourerEmpty"));
            return;
        }

        using var child = ImRaii.Child("##glamourerList", new Vector2(280f * ImGuiHelpers.GlobalScale, 320f * ImGuiHelpers.GlobalScale), true);
        foreach (var (id, name) in _glamourerDesigns)
        {
            if (!string.IsNullOrEmpty(_glamourerFilter)
                && name.IndexOf(_glamourerFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (ImGui.Selectable(name + "##" + id))
            {
                if (string.IsNullOrEmpty(_replaceAppearanceEntryId))
                {
                    _ = _service.AddFromGlamourerDesignAsync(sceneId, id, name);
                }
                else
                {
                    _ = _service.ReplaceEntryAppearanceFromDesignAsync(sceneId, _replaceAppearanceEntryId, id, name);
                    _replaceAppearanceEntryId = string.Empty;
                }
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private bool DrawBasePose(HousingNpcEntry entry)
    {
        var pose = entry.Actions.OfType<NpcEmoteAction>().FirstOrDefault(a => a.StayInPose);
        var picked = DrawEmoteCombo("basepose" + entry.Id, pose?.Emote ?? 0, out var changed,
            Loc.Get("HousingNpc.Editor.BasePose"));
        UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.BasePoseTip"));

        bool dirty = false;
        if (changed)
        {
            if (picked == 0)
            {
                if (pose != null) { entry.Actions.Remove(pose); dirty = true; }
            }
            else if (pose == null)
            {
                // En tête de séquence : la pose est prise avant tout le reste.
                entry.Actions.Insert(0, new NpcEmoteAction { Emote = picked, StayInPose = true });
                dirty = true;
            }
            else
            {
                pose.Emote = picked;
                dirty = true;
            }
        }

        ImGui.SameLine();
        if (DrawPoseVariantCombo(entry)) dirty = true;

        return dirty;
    }

    /// <summary>
    /// Choix de la variante de posture (les poses que /changepose fait défiler). Écrire
    /// <c>EmoteController.CPoseState</c> sur un acteur qu'on a créé ne la change pas — vérifié en
    /// jeu : on passe par l'override d'animation, que <see cref="NpcPoseCatalog"/> résout.
    /// </summary>
    private bool DrawPoseVariantCombo(HousingNpcEntry entry)
    {
        var options = _poseCatalog.Options;
        var current = _poseCatalog.Find(entry.PoseKey);
        var label = current == null
            ? Loc.Get("HousingNpc.Editor.PoseVariantDefault")
            : PoseVariantLabel(current);

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        using var combo = ImRaii.Combo(Loc.Get("HousingNpc.Editor.PoseVariant") + "##posevar" + entry.Id, label);
        UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.PoseVariantTip"));
        if (!combo) return false;

        bool changed = false;
        if (ImGui.Selectable(Loc.Get("HousingNpc.Editor.PoseVariantDefault"), current == null) && current != null)
        {
            entry.PoseKey = string.Empty;
            changed = true;
        }

        NpcPoseCatalog.PoseCategory? section = null;
        foreach (var option in options)
        {
            if (section != option.Category)
            {
                ImGui.Separator();
                ImGui.TextColored(ImGuiColors.DalamudGrey, PoseCategoryLabel(option.Category));
                section = option.Category;
            }

            bool selected = string.Equals(entry.PoseKey, option.Key, StringComparison.Ordinal);
            if (ImGui.Selectable(PoseNumberLabel(option) + "##" + option.Key, selected) && !selected)
            {
                entry.PoseKey = option.Key;
                changed = true;
            }
        }

        if (options.Count == 0)
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.PoseVariantEmpty"));

        return changed;
    }

    private static string PoseNumberLabel(NpcPoseCatalog.PoseOption option)
        => string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingNpc.Editor.PoseVariantItem"), option.Number);

    private static string PoseVariantLabel(NpcPoseCatalog.PoseOption option)
        => PoseCategoryLabel(option.Category) + " — " + PoseNumberLabel(option);

    private static string PoseCategoryLabel(NpcPoseCatalog.PoseCategory category) => category switch
    {
        NpcPoseCatalog.PoseCategory.Standing => Loc.Get("HousingNpc.Editor.PoseCatStanding"),
        NpcPoseCatalog.PoseCategory.WeaponDrawn => Loc.Get("HousingNpc.Editor.PoseCatWeaponDrawn"),
        NpcPoseCatalog.PoseCategory.Chair => Loc.Get("HousingNpc.Editor.PoseCatChair"),
        NpcPoseCatalog.PoseCategory.GroundSit => Loc.Get("HousingNpc.Editor.PoseCatGroundSit"),
        NpcPoseCatalog.PoseCategory.Lying => Loc.Get("HousingNpc.Editor.PoseCatLying"),
        _ => "?",
    };

    private bool DrawActions(string sceneId, HousingNpcEntry entry)
    {
        bool changed = false;
        float scale = ImGuiHelpers.GlobalScale;

        var looping = entry.Looping;
        if (ImGui.Checkbox(Loc.Get("HousingNpc.Editor.LoopSeq"), ref looping)) { entry.Looping = looping; changed = true; }
        UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.LoopSeqTip"));
        if (entry.Looping)
        {
            ImGui.SameLine();
            var ld = entry.LoopDelay;
            ImGui.SetNextItemWidth(70 * scale);
            if (ImGui.InputFloat("s##loopdelay", ref ld, 0f, 0f, "%.1f")) { entry.LoopDelay = MathF.Max(0f, ld); changed = true; }
            UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.LoopDelayTip"));
        }

        ImGui.Separator();
        ImGui.TextColored(ImGuiColors.DalamudGrey, string.Format(Loc.Get("HousingNpc.Editor.Sequence"), entry.Actions.Count));

        int moveUp = -1, moveDown = -1, removeAt = -1;
        for (int i = 0; i < entry.Actions.Count; i++)
        {
            var action = entry.Actions[i];
            using var aid = ImRaii.PushId("act" + i);

            var en = action.Enabled;
            if (ImGui.Checkbox("##en", ref en)) { action.Enabled = en; changed = true; }
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, (i + 1) + ".");
            ImGui.SameLine();
            ImGui.TextUnformatted(ActionTypeName(action));
            ImGui.SameLine();
            if (_uiShared.IconButton(FontAwesomeIcon.ArrowUp) && i > 0) moveUp = i;
            ImGui.SameLine();
            if (_uiShared.IconButton(FontAwesomeIcon.ArrowDown) && i < entry.Actions.Count - 1) moveDown = i;
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DalamudRed))
            {
                if (_uiShared.IconButton(FontAwesomeIcon.Trash)) removeAt = i;
            }

            ImGui.Indent();
            if (DrawActionParams(sceneId, entry, i, action)) changed = true;
            ImGui.Unindent();
        }

        if (moveUp > 0)
        {
            (entry.Actions[moveUp - 1], entry.Actions[moveUp]) = (entry.Actions[moveUp], entry.Actions[moveUp - 1]);
            changed = true;
        }
        if (moveDown >= 0 && moveDown < entry.Actions.Count - 1)
        {
            (entry.Actions[moveDown], entry.Actions[moveDown + 1]) = (entry.Actions[moveDown + 1], entry.Actions[moveDown]);
            changed = true;
        }
        if (removeAt >= 0) { entry.Actions.RemoveAt(removeAt); changed = true; }

        ImGuiHelpers.ScaledDummy(2f);
        if (DrawAddAction(sceneId, entry)) changed = true;
        return changed;
    }

    private bool DrawActionParams(string sceneId, HousingNpcEntry entry, int index, NpcAction action)
    {
        bool changed = false;
        float scale = ImGuiHelpers.GlobalScale;
        switch (action)
        {
            case NpcEmoteAction e:
            {
                var em = DrawEmoteCombo("ae" + index + entry.Id, e.Emote, out var ec);
                if (ec) { e.Emote = em; changed = true; }
                ImGui.SameLine();
                var loop = e.Loop;
                if (ImGui.Checkbox(Loc.Get("HousingNpc.Editor.Loop"), ref loop)) { e.Loop = loop; changed = true; }
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.LoopTip"));
                ImGui.SameLine();
                var stay = e.StayInPose;
                if (ImGui.Checkbox(Loc.Get("HousingNpc.Editor.StayPose"), ref stay)) { e.StayInPose = stay; changed = true; }
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.StayPoseTip"));
                ImGui.SameLine();
                var ed = e.Duration;
                ImGui.SetNextItemWidth(70f);
                if (ImGui.InputFloat(Loc.Get("HousingNpc.Editor.EmoteSec"), ref ed, 0f, 0f, "%.1f")) { e.Duration = MathF.Max(0f, ed); changed = true; }
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.EmoteSecTip"));
                break;
            }
            case NpcMovementAction m:
            {
                var p = new Vector3(m.X, m.Y, m.Z);
                ImGui.SetNextItemWidth(200 * scale);
                if (ImGui.InputFloat3(Loc.Get("HousingNpc.Editor.Position"), ref p)) { m.X = p.X; m.Y = p.Y; m.Z = p.Z; changed = true; }
                var sp = DrawSpeedCombo("ms" + index, m.Speed, out var sc);
                if (sc) { m.Speed = sp; changed = true; }
                if (m.Speed == NpcMoveSpeed.Custom)
                {
                    ImGui.SameLine();
                    var cs = m.CustomSpeed;
                    ImGui.SetNextItemWidth(70 * scale);
                    if (ImGui.InputFloat("y/s##cs", ref cs, 0f, 0f, "%.1f")) { m.CustomSpeed = MathF.Max(0f, cs); changed = true; }
                }
                break;
            }
            case NpcPathAction path:
            {
                if (_uiShared.IconTextButton(FontAwesomeIcon.MapMarkerAlt, Loc.Get("HousingNpc.Editor.AddWaypoint")))
                    _ = _service.AddPathPointAtPlayerAsync(sceneId, entry.Id, index, false);
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.AddWaypointTip"));
                int rm = -1;
                for (int pi = 0; pi < path.Points.Count; pi++)
                {
                    var pt = path.Points[pi];
                    using var pid = ImRaii.PushId("pt" + pi);
                    ImGui.TextColored(ImGuiColors.DalamudGrey, string.Format(Loc.Get("HousingNpc.Editor.Point"), pi + 1));
                    ImGui.SameLine();
                    var sp = DrawSpeedCombo("ps" + pi, pt.Speed, out var sc);
                    if (sc) { pt.Speed = sp; changed = true; }
                    ImGui.SameLine();
                    using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DalamudRed))
                    {
                        if (_uiShared.IconButton(FontAwesomeIcon.Trash)) rm = pi;
                    }
                }
                if (rm >= 0) { path.Points.RemoveAt(rm); changed = true; }
                break;
            }
            case NpcRotationAction r:
            {
                var rr = r.TargetRotation;
                ImGui.SetNextItemWidth(160 * scale);
                if (ImGui.SliderFloat(Loc.Get("HousingNpc.Editor.Rotation"), ref rr, -3.14159f, 3.14159f)) { r.TargetRotation = rr; changed = true; }
                ImGui.SameLine();
                if (_uiShared.IconTextButton(FontAwesomeIcon.LocationArrow, Loc.Get("HousingNpc.Editor.UseMyFacing")))
                    _ = _service.SetActionRotationToPlayerAsync(sceneId, entry.Id, index);
                break;
            }
            case NpcWaitAction w:
            {
                var d = w.Duration;
                ImGui.SetNextItemWidth(80 * scale);
                if (ImGui.InputFloat(Loc.Get("HousingNpc.Editor.WaitSec"), ref d, 0f, 0f, "%.1f")) { w.Duration = MathF.Max(0f, d); changed = true; }
                break;
            }
            case NpcVisibilityAction v:
            {
                var visible = v.Visible;
                if (ImGui.Checkbox(Loc.Get("HousingNpc.Editor.Visible"), ref visible)) { v.Visible = visible; changed = true; }
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.VisibleTip"));
                break;
            }
            case NpcTimelineAction t:
            {
                var ids = string.Join(", ", t.TimelineIds);
                ImGui.SetNextItemWidth(150 * scale);
                if (ImGui.InputTextWithHint("##tlids", Loc.Get("HousingNpc.Editor.TimelineIdsHint"), ref ids, 64))
                {
                    t.TimelineIds = ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => ushort.TryParse(x, out var id) ? id : (ushort)0)
                        .Where(id => id != 0)
                        .ToList();
                    changed = true;
                }
                UiSharedService.AttachToolTip(Loc.Get("HousingNpc.Editor.TimelineIdsTip"));

                ImGui.SameLine();
                var addTimeline = DrawTimelinePicker("tl" + index + entry.Id);
                if (addTimeline != 0) { t.TimelineIds.Add(addTimeline); changed = true; }

                ImGui.SameLine();
                var td = t.Duration;
                ImGui.SetNextItemWidth(80 * scale);
                if (ImGui.InputFloat(Loc.Get("HousingNpc.Editor.WaitSec"), ref td, 0f, 0f, "%.1f")) { t.Duration = MathF.Max(0f, td); changed = true; }
                break;
            }
            case NpcSyncAction:
                ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.SyncHelp"));
                break;
        }
        return changed;
    }

    private bool DrawAddAction(string sceneId, HousingNpcEntry entry)
    {
        bool changed = false;
        if (!_addActionKind.TryGetValue(entry.Id, out int kind)) kind = 0;
        string[] names =
        {
            Loc.Get("HousingNpc.Editor.ActEmote"), Loc.Get("HousingNpc.Editor.ActMove"),
            Loc.Get("HousingNpc.Editor.ActPath"), Loc.Get("HousingNpc.Editor.ActRotation"),
            Loc.Get("HousingNpc.Editor.ActWait"), Loc.Get("HousingNpc.Editor.ActIdle"),
            Loc.Get("HousingNpc.Editor.ActVisibility"), Loc.Get("HousingNpc.Editor.ActTimeline"),
            Loc.Get("HousingNpc.Editor.ActSync"),
        };
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("##addkind", ref kind, names, names.Length)) _addActionKind[entry.Id] = kind;
        ImGui.SameLine();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("HousingNpc.Editor.AddAction")))
        {
            switch (kind)
            {
                case 0: entry.Actions.Add(new NpcEmoteAction()); changed = true; break;
                case 1: _ = _service.AddMovementAtPlayerAsync(sceneId, entry.Id, false); break;
                case 2: entry.Actions.Add(new NpcPathAction()); changed = true; break;
                case 3: entry.Actions.Add(new NpcRotationAction { TargetRotation = entry.Rotation }); changed = true; break;
                case 4: entry.Actions.Add(new NpcWaitAction { Duration = 1f }); changed = true; break;
                case 5: entry.Actions.Add(new NpcIdleAction()); changed = true; break;
                case 6: entry.Actions.Add(new NpcVisibilityAction()); changed = true; break;
                case 7: entry.Actions.Add(new NpcTimelineAction()); changed = true; break;
                case 8: entry.Actions.Add(new NpcSyncAction()); changed = true; break;
            }
        }
        return changed;
    }

    private static NpcMoveSpeed DrawSpeedCombo(string id, NpcMoveSpeed current, out bool changed)
    {
        changed = false;
        var result = current;
        ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##spd" + id, SpeedName(current)))
        {
            foreach (var s in new[] { NpcMoveSpeed.Walk, NpcMoveSpeed.Run, NpcMoveSpeed.Custom })
            {
                if (ImGui.Selectable(SpeedName(s), s == current)) { result = s; changed = true; }
            }
            ImGui.EndCombo();
        }
        return result;
    }

    private static string SpeedName(NpcMoveSpeed s) => s switch
    {
        NpcMoveSpeed.Run => Loc.Get("HousingNpc.Editor.SpeedRun"),
        NpcMoveSpeed.Custom => Loc.Get("HousingNpc.Editor.SpeedCustom"),
        _ => Loc.Get("HousingNpc.Editor.SpeedWalk"),
    };

    private static string ActionTypeName(NpcAction a) => a switch
    {
        NpcEmoteAction => Loc.Get("HousingNpc.Editor.ActEmote"),
        NpcMovementAction => Loc.Get("HousingNpc.Editor.ActMove"),
        NpcPathAction => Loc.Get("HousingNpc.Editor.ActPath"),
        NpcRotationAction => Loc.Get("HousingNpc.Editor.ActRotation"),
        NpcWaitAction => Loc.Get("HousingNpc.Editor.ActWait"),
        NpcIdleAction => Loc.Get("HousingNpc.Editor.ActIdle"),
        NpcVisibilityAction => Loc.Get("HousingNpc.Editor.ActVisibility"),
        NpcTimelineAction => Loc.Get("HousingNpc.Editor.ActTimeline"),
        NpcSyncAction => Loc.Get("HousingNpc.Editor.ActSync"),
        _ => "?",
    };

    private List<(ushort Id, string Name, uint Icon)> Emotes()
    {
        if (_emotes != null) return _emotes;
        var list = new List<(ushort, string, uint)> { ((ushort)0, Loc.Get("HousingNpc.Editor.EmoteNone"), 0u) };
        try
        {
            foreach (var e in _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>())
            {
                var name = e.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name) || name.All(c => c is '-' or ' ')) continue;
                if (e.RowId == 0 || e.RowId > ushort.MaxValue) continue;
                list.Add(((ushort)e.RowId, name, e.Icon));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Chargement de la feuille Emote échoué"); }

        DisambiguateEmoteNames(list);
        _emotes = list;
        return _emotes;
    }

    private void DisambiguateEmoteNames(List<(ushort Id, string Name, uint Icon)> list)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, name, _) in list)
            seen[name] = seen.GetValueOrDefault(name) + 1;

        for (int i = 0; i < list.Count; i++)
        {
            var (id, name, icon) = list[i];
            if (id == 0 || seen.GetValueOrDefault(name) <= 1) continue;
            list[i] = (id, $"{name} ({EmoteCommandOrId(id)})", icon);
        }
    }

    private string EmoteCommandOrId(ushort emoteId)
    {
        try
        {
            var command = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()
                .GetRowOrDefault(emoteId)?.TextCommand.ValueNullable?.Command.ExtractText();
            if (!string.IsNullOrWhiteSpace(command)) return command;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Commande de texte introuvable pour l'émote {Emote}", emoteId);
        }
        return emoteId.ToString(CultureInfo.InvariantCulture);
    }

    private List<(ushort Id, string Key)> Timelines()
    {
        if (_timelines != null) return _timelines;
        var list = new List<(ushort, string)>();
        try
        {
            foreach (var t in _dataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>())
            {
                if (t.RowId == 0 || t.RowId > ushort.MaxValue) continue;
                var key = t.Key.ExtractText();
                if (string.IsNullOrWhiteSpace(key)) continue;
                list.Add(((ushort)t.RowId, key));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Chargement de la feuille ActionTimeline échoué"); }
        _timelines = list;
        return _timelines;
    }

    private const int MaxTimelineResults = 200;
    private ushort DrawTimelinePicker(string id)
    {
        ushort result = 0;
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        using var combo = ImRaii.Combo(Loc.Get("HousingNpc.Editor.TimelineFind") + "##tlp" + id, string.Empty);
        if (!combo) return 0;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##tls" + id, Loc.Get("HousingNpc.Editor.Search"), ref _timelineFilter, 50);
        if (string.IsNullOrWhiteSpace(_timelineFilter))
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.TimelineFindHint"));
            return 0;
        }

        int shown = 0;
        foreach (var (tid, key) in Timelines())
        {
            if (!key.Contains(_timelineFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (++shown > MaxTimelineResults)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("HousingNpc.Editor.TimelineTooMany"));
                break;
            }
            if (ImGui.Selectable($"{key}  ({tid})##tl{id}_{tid}")) result = tid;
        }
        return result;
    }

    private void DrawEmoteIcon(uint iconId, float size)
    {
        if (iconId != 0 && !_badIcons.Contains(iconId))
        {
            try
            {
                var wrap = _textureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrDefault();
                if (wrap != null) { ImGui.Image(wrap.Handle, new Vector2(size)); return; }
            }
            catch
            {
                _badIcons.Add(iconId);
            }
        }
        ImGui.Dummy(new Vector2(size));
    }

    private string EmoteName(ushort id)
    {
        if (id == 0) return Loc.Get("HousingNpc.Editor.EmoteNone");
        foreach (var (eid, name, _) in Emotes())
            if (eid == id) return name;
        return $"Emote {id}";
    }

    private ushort DrawEmoteCombo(string id, ushort current, out bool changed, string? label = null)
    {
        changed = false;
        ushort result = current;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        using var combo = ImRaii.Combo((label ?? Loc.Get("HousingNpc.Editor.Emote")) + "##" + id, EmoteName(current));
        if (combo)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##es" + id, Loc.Get("HousingNpc.Editor.Search"), ref _emoteFilter, 50);
            var iconSize = ImGui.GetTextLineHeight();
            foreach (var (eid, ename, eicon) in Emotes())
            {
                if (!string.IsNullOrEmpty(_emoteFilter) && !ename.Contains(_emoteFilter, StringComparison.OrdinalIgnoreCase)) continue;
                DrawEmoteIcon(eicon, iconSize);
                ImGui.SameLine();
                // L'identifiant ImGui d'un Selectable dérive de son libellé : deux émotes homonymes
                // partageaient le même, et seule la première répondait au clic. On y ajoute l'id.
                if (ImGui.Selectable(ename + "##" + id + "_" + eid.ToString(CultureInfo.InvariantCulture), eid == current))
                {
                    result = eid;
                    changed = true;
                }
            }
        }
        return result;
    }
}
