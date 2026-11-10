using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Stagehand.Api;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Dto.StageShare;
using UmbraSync.Localization;
using UmbraSync.Modules.Housing.Services;

namespace UmbraSync.UI;

public sealed partial class CharaDataHubUi
{
    private static readonly TimeSpan StageLocationPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LocalStagesPollInterval = TimeSpan.FromSeconds(3);

    private PolledUiValue<StageLocationDto?>? _stageLocationPoll;
    private PolledUiValue<LocalStageDefinition[]>? _localStagesPoll;
    private bool _stagesInitialized;
    private string _stageSelectedFilename = string.Empty;
    private string _stageDescription = string.Empty;
    private bool _stageToAll = true;
    private readonly List<string> _stageAllowedIndividuals = new();
    private readonly List<string> _stageAllowedSyncshells = new();
    private Guid? _stageEditingId;
    private string _stageEditDescription = string.Empty;
    private bool _stageEditToAll;
    private readonly List<string> _stageEditAllowedIndividuals = new();
    private readonly List<string> _stageEditAllowedSyncshells = new();
    private string _stageFolderOverride = string.Empty;
    private bool _stageFolderOverrideLoaded;

    private void DrawStagesPage()
    {
        var manager = _stageShareManager;
        if (manager == null) return;

        if (!_uiSharedService.ApiController.IsConnected)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("HousingShare.ServerRequired"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(5);
            return;
        }

        UiSharedService.BeginSectionCard(Loc.Get("Stages.Title"), FontAwesomeIcon.Cubes);
        UiSharedService.TextWrapped(Loc.Get("Stages.Intro"));
        ImGuiHelpers.ScaledDummy(3);

        if (!manager.IsStagehandAvailable)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("Stages.StagehandMissing"), ImGuiColors.DalamudYellow);
            string reason = manager.StagehandUnavailableReason;
            if (!string.IsNullOrEmpty(reason))
                UiSharedService.TextWrapped(reason);
            ImGuiHelpers.ScaledDummy(3);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.ExternalLinkAlt, Loc.Get("Stages.StagehandRepo")))
                Util.OpenLink(Constants.StagehandRepoUrl);
            UiSharedService.AttachToolTip(Constants.StagehandRepoUrl);
            UiSharedService.EndSectionCard();
            return;
        }

        if (!_stagesInitialized && !manager.IsBusy)
        {
            _stagesInitialized = true;
            _ = manager.RefreshAsync();
        }

        if (manager.IsBusy)
        {
            UiSharedService.ColorTextWrapped(manager.ProgressStatus ?? Loc.Get("Stages.Processing"), ImGuiColors.DalamudYellow);
        }
        if (!string.IsNullOrEmpty(manager.LastError))
        {
            UiSharedService.ColorTextWrapped(manager.LastError!, ImGuiColors.DalamudRed);
        }
        else if (!string.IsNullOrEmpty(manager.LastSuccess))
        {
            UiSharedService.ColorTextWrapped(manager.LastSuccess!, ImGuiColors.HealerGreen);
        }

        _stageLocationPoll ??= new PolledUiValue<StageLocationDto?>(manager.GetLocationAsync, StageLocationPollInterval, null);
        _localStagesPoll ??= new PolledUiValue<LocalStageDefinition[]>(manager.GetLocalStagesAsync, LocalStagesPollInterval, []);
        StageLocationDto? location = _stageLocationPoll.Poll(_logger);
        LocalStageDefinition[] localStages = _localStagesPoll.Poll(_logger);

        ImGuiHelpers.ScaledDummy(3);
        ImGui.TextUnformatted(Loc.Get("Stages.CurrentLocation"));
        ImGui.SameLine();
        UiSharedService.ColorText(location == null ? Loc.Get("Stages.LocationUnknown") : FormatStageLocation(location.Value), UiSharedService.AccentColor);

        DrawStagePublishForm(manager, location, localStages);
        DrawStageOwnShares(manager, localStages);
        DrawStagesAppliedHere(manager);
        UiSharedService.EndSectionCard();
    }

    private void DrawStagePublishForm(StageShareManager manager, StageLocationDto? location, LocalStageDefinition[] localStages)
    {
        UiSharedService.BeginSectionCard(Loc.Get("Stages.Publish.Title"), FontAwesomeIcon.Upload);

        if (localStages.Length == 0)
        {
            ImGui.TextDisabled(Loc.Get("Stages.NoLocalStage"));
            DrawStageFolderOverride();
            return;
        }

        int selectedIndex = Array.FindIndex(localStages, s => string.Equals(s.Filename, _stageSelectedFilename, StringComparison.Ordinal));
        string preview = selectedIndex >= 0 ? StageLabel(localStages[selectedIndex]) : Loc.Get("Stages.SelectStage");
        ImGui.SetNextItemWidth(300f * ImGuiHelpers.GlobalScale);
        using (var combo = UiSharedService.Combo("##stageSelect", preview))
        {
            if (combo)
            {
                for (int i = 0; i < localStages.Length; i++)
                {
                    using var id = ImRaii.PushId(i);
                    if (ImGui.Selectable(StageLabel(localStages[i]), i == selectedIndex))
                        _stageSelectedFilename = localStages[i].Filename;
                }
            }
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("Stages.LocalStage"));
        _uiSharedService.DrawHelpText(Loc.Get("Stages.LocalStage.Help"));

        bool selectedMissing = false;
        if (selectedIndex >= 0)
        {
            string? path = manager.ResolveLocalStagePath(localStages[selectedIndex]);
            selectedMissing = path == null || !File.Exists(path);
            if (selectedMissing)
                UiSharedService.ColorTextWrapped(Loc.Get("Stages.Error.FileNotFound"), ImGuiColors.DalamudRed);
        }
        if (selectedMissing)
            DrawStageFolderOverride();

        ImGui.SetNextItemWidth(400f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##stageDescription", Loc.Get("Stages.Description.Hint"), ref _stageDescription, StageShareManager.MaxDescriptionLength);
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("Stages.Description"));

        DrawStageAccessEditor("stagePublishAccess", ref _stageToAll, _stageAllowedIndividuals, _stageAllowedSyncshells);

        ImGuiHelpers.ScaledDummy(3);
        bool canPublish = selectedIndex >= 0 && !selectedMissing && location != null && !manager.IsBusy;
        using (ImRaii.Disabled(!canPublish))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, Loc.Get("Stages.Publish")) && canPublish)
            {
                var (individuals, syncshells) = ResolveStageAccess(_stageToAll, _stageAllowedIndividuals, _stageAllowedSyncshells);
                _ = manager.PublishAsync(localStages[selectedIndex], location!.Value, _stageDescription, individuals, syncshells);
                _stageDescription = string.Empty;
                _stageToAll = true;
                _stageAllowedIndividuals.Clear();
                _stageAllowedSyncshells.Clear();
            }
        }
        UiSharedService.AttachToolTip(Loc.Get("Stages.Publish.Tooltip"));
    }

    private void DrawStageFolderOverride()
    {
        if (!_stageFolderOverrideLoaded)
        {
            _stageFolderOverrideLoaded = true;
            _stageFolderOverride = _mareConfigService.Current.StagehandStagesPathOverride ?? string.Empty;
        }

        ImGuiHelpers.ScaledDummy(2);
        ImGui.SetNextItemWidth(400f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputTextWithHint("##stageFolder", Loc.Get("Stages.Folder.Hint"), ref _stageFolderOverride, 512, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            _mareConfigService.Current.StagehandStagesPathOverride = string.IsNullOrWhiteSpace(_stageFolderOverride) ? null : _stageFolderOverride.Trim();
            _mareConfigService.Save();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("Stages.Folder"));
        _uiSharedService.DrawHelpText(Loc.Get("Stages.Folder.Help"));
    }

    private void DrawStageOwnShares(StageShareManager manager, LocalStageDefinition[] localStages)
    {
        UiSharedService.BeginSectionCard(Loc.Get("Stages.OwnShares"), FontAwesomeIcon.List);

        using (ImRaii.Disabled(manager.IsBusy))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Sync, Loc.Get("Stages.Refresh")))
                _ = manager.RefreshAsync();
        }

        ImGuiHelpers.ScaledDummy(3);

        if (manager.OwnShares.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Stages.NoOwnShares"));
            return;
        }

        using (var table = ImRaii.Table("stage-own-shares", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter))
        {
            if (!table) return;
            ImGui.TableSetupColumn(Loc.Get("Stages.Column.Name"));
            ImGui.TableSetupColumn(Loc.Get("Stages.Column.Location"));
            ImGui.TableSetupColumn(Loc.Get("Stages.Column.Created"));
            ImGui.TableSetupColumn(Loc.Get("Stages.Column.Access"));
            ImGui.TableSetupColumn(Loc.Get("Stages.Column.Actions"), ImGuiTableColumnFlags.WidthFixed, 200f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var entry in manager.OwnShares.ToArray())
            {
                using var rowId = ImRaii.PushId(entry.Id.ToString("N"));
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrEmpty(entry.StageName) ? entry.Id.ToString("D", CultureInfo.InvariantCulture) : entry.StageName);
                if (!string.IsNullOrEmpty(entry.Description))
                    UiSharedService.AttachToolTip(entry.Description);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatStageLocationShort(entry.Location));
                UiSharedService.AttachToolTip(FormatStageLocation(entry.Location));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingScenario.AccessSummary"), entry.AllowedIndividuals.Count, entry.AllowedSyncshells.Count));
                if (ImGui.IsItemHovered())
                    DrawStageAccessTooltip(entry);

                ImGui.TableNextColumn();
                if (ImGui.SmallButton(Loc.Get("HousingScenario.Edit")))
                {
                    if (_stageEditingId == entry.Id)
                    {
                        _stageEditingId = null;
                    }
                    else
                    {
                        _stageEditingId = entry.Id;
                        _stageEditDescription = entry.Description;
                        _stageEditAllowedIndividuals.Clear();
                        _stageEditAllowedIndividuals.AddRange(entry.AllowedIndividuals);
                        _stageEditAllowedSyncshells.Clear();
                        _stageEditAllowedSyncshells.AddRange(entry.AllowedSyncshells);
                        _stageEditToAll = IsStageAccessForAll(entry);
                    }
                }

                string? sourcePath = manager.GetSourceFile(entry.Id);
                LocalStageDefinition? source = null;
                if (sourcePath != null)
                {
                    foreach (var local in localStages)
                    {
                        if (string.Equals(manager.ResolveLocalStagePath(local), sourcePath, StringComparison.OrdinalIgnoreCase))
                        {
                            source = local;
                            break;
                        }
                    }
                }
                if (source != null)
                {
                    ImGui.SameLine();
                    bool pending = manager.PendingRepublish.ContainsKey(entry.Id);
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, pending))
                    using (ImRaii.Disabled(manager.IsBusy))
                    {
                        if (ImGui.SmallButton(Loc.Get("Stages.Republish")))
                        {
                            _ = manager.PublishAsync(source.Value, entry.Location, entry.Description,
                                [.. entry.AllowedIndividuals], [.. entry.AllowedSyncshells], entry.Id);
                        }
                    }
                    UiSharedService.AttachToolTip(pending ? Loc.Get("Stages.EditedLocally") : Loc.Get("Stages.Republish.Tooltip"));
                }

                ImGui.SameLine();
                using (ImRaii.Disabled(!UiSharedService.CtrlPressed() || manager.IsBusy))
                {
                    if (ImGui.SmallButton(Loc.Get("HousingShare.Delete")))
                    {
                        _ = manager.DeleteAsync(entry.Id);
                        if (_stageEditingId == entry.Id) _stageEditingId = null;
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get("Stages.Delete.Tooltip"));
            }
        }

        if (_stageEditingId == null) return;
        var editEntry = manager.OwnShares.FirstOrDefault(s => s.Id == _stageEditingId);
        if (editEntry == null)
        {
            _stageEditingId = null;
            return;
        }

        ImGuiHelpers.ScaledDummy(5);
        UiSharedService.DistanceSeparator();
        ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("Stages.Edit.Title"), editEntry.StageName));
        ImGui.SetNextItemWidth(400f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##stageEditDescription", Loc.Get("Stages.Description.Hint"), ref _stageEditDescription, StageShareManager.MaxDescriptionLength);
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("Stages.Description"));
        DrawStageAccessEditor("stageEditAccess", ref _stageEditToAll, _stageEditAllowedIndividuals, _stageEditAllowedSyncshells);
        ImGuiHelpers.ScaledDummy(3);
        using (ImRaii.Disabled(manager.IsBusy))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("Stages.Edit.Save")))
            {
                var (individuals, syncshells) = ResolveStageAccess(_stageEditToAll, _stageEditAllowedIndividuals, _stageEditAllowedSyncshells);
                _ = manager.UpdateVisibilityAsync(editEntry.Id, _stageEditDescription, individuals, syncshells);
                _stageEditingId = null;
            }
        }
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, Loc.Get("Stages.Edit.Cancel")))
            _stageEditingId = null;
    }

    private void DrawStagesAppliedHere(StageShareManager manager)
    {
        UiSharedService.BeginSectionCard(Loc.Get("Stages.AppliedHere"), FontAwesomeIcon.Eye);

        var available = manager.AvailableStages;
        bool catalogApplied = manager.AppliedStages.Any(a => a.Source == StageShareManager.AppliedStageSource.Catalog);
        if (available.Count == 0 && !catalogApplied)
        {
            ImGui.TextDisabled(Loc.Get("Stages.NoneApplied"));
            return;
        }

        UiSharedService.TextWrapped(Loc.Get("Stages.Here.Help"));
        ImGuiHelpers.ScaledDummy(2);
        var applied = manager.AppliedStages;
        foreach (var entry in available)
        {
            using var id = ImRaii.PushId(entry.Id.ToString("N"));
            bool visible = applied.FirstOrDefault(a => a.ShareId == entry.Id)?.Visible == true;
            using (ImRaii.Disabled(manager.IsBusy))
            {
                if (_uiSharedService.IconButton(visible ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye))
                    _ = visible ? manager.HideStageAsync(entry.Id) : manager.ShowStageAsync(entry.Id);
            }
            UiSharedService.AttachToolTip(visible ? Loc.Get("Stages.Hide") : Loc.Get("Stages.Show"));
            ImGui.SameLine();
            if (visible)
                UiSharedService.ColorText(entry.StageName, UiSharedService.AccentColor);
            else
                ImGui.TextUnformatted(entry.StageName);
            if (!string.IsNullOrEmpty(entry.Description))
                UiSharedService.AttachToolTip(entry.Description);
            ImGui.SameLine();
            ImGui.TextDisabled(FormatPairLabel(entry.OwnerUid));
        }
        DrawCatalogAppliedRows(manager);

        if (applied.Count == 0) return;
        ImGuiHelpers.ScaledDummy(3);
        using (ImRaii.Disabled(manager.IsBusy))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.EyeSlash, Loc.Get("Stages.HideAll")))
                _ = manager.RemoveAllAppliedAsync("demande manuelle");
        }
        UiSharedService.AttachToolTip(Loc.Get("Stages.HideAll.Tooltip"));
    }

    private void DrawStageAccessEditor(string id, ref bool toAll, List<string> individuals, List<string> syncshells)
    {
        using var pushId = ImRaii.PushId(id);
        ImGuiHelpers.ScaledDummy(2);
        if (ImGui.RadioButton(Loc.Get("Stages.Access.All"), toAll)) toAll = true;
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.Get("Stages.Access.Restricted"), !toAll)) toAll = false;
        _uiSharedService.DrawHelpText(Loc.Get("Stages.Access.Help"));
        if (toAll) return;

        using (ImRaii.PushIndent())
        {
            DrawPairPicker("stagePair" + id, string.Empty, individuals, uid =>
            {
                if (!individuals.Contains(uid, StringComparer.OrdinalIgnoreCase)) individuals.Add(uid);
            });
            ImGui.SameLine();
            ImGui.TextUnformatted(Loc.Get("Stages.Access.Pairs"));
            foreach (var uid in individuals.ToArray())
            {
                using var uidId = ImRaii.PushId("uid" + uid);
                ImGui.BulletText(FormatPairLabel(uid));
                ImGui.SameLine();
                if (ImGui.SmallButton(Loc.Get("HousingScenario.Remove")))
                    individuals.Remove(uid);
            }

            DrawStageSyncshellPicker("stageShell" + id, syncshells);
            ImGui.SameLine();
            ImGui.TextUnformatted(Loc.Get("Stages.Access.Syncshells"));
            foreach (var gid in syncshells.ToArray())
            {
                using var gidId = ImRaii.PushId("gid" + gid);
                ImGui.BulletText(FormatSyncshellLabel(gid));
                ImGui.SameLine();
                if (ImGui.SmallButton(Loc.Get("HousingScenario.Remove")))
                    syncshells.Remove(gid);
            }
        }
    }

    private void DrawStageSyncshellPicker(string id, List<string> chosen)
    {
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        using var combo = UiSharedService.Combo("##" + id, Loc.Get("Stages.Access.PickSyncshell"));
        if (!combo) return;

        foreach (var group in _pairManager.Groups.Values
            .OrderBy(g => _serverConfigurationManager.GetNoteForGid(g.GID) ?? g.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase))
        {
            string gid = group.GID;
            bool already = chosen.Contains(gid, StringComparer.OrdinalIgnoreCase);
            using (ImRaii.Disabled(already))
            {
                if (ImGui.Selectable(FormatSyncshellLabel(gid) + "##" + gid, false))
                    chosen.Add(gid);
            }
        }
    }

    private void DrawStageAccessTooltip(StageShareEntryDto entry)
    {
        ImGui.BeginTooltip();
        if (entry.AllowedIndividuals.Count > 0)
        {
            ImGui.TextUnformatted(Loc.Get("Stages.Access.Pairs"));
            foreach (var uid in entry.AllowedIndividuals)
                ImGui.BulletText(FormatPairLabel(uid));
        }
        if (entry.AllowedSyncshells.Count > 0)
        {
            ImGui.TextUnformatted(Loc.Get("Stages.Access.Syncshells"));
            foreach (var gid in entry.AllowedSyncshells)
                ImGui.BulletText(FormatSyncshellLabel(gid));
        }
        if (entry.AllowedIndividuals.Count == 0 && entry.AllowedSyncshells.Count == 0)
            ImGui.TextDisabled(Loc.Get("Stages.Access.Public"));
        ImGui.EndTooltip();
    }

    private (List<string> Individuals, List<string> Syncshells) ResolveStageAccess(bool toAll, List<string> individuals, List<string> syncshells)
    {
        if (!toAll) return ([.. individuals], [.. syncshells]);
        return (_pairManager.DirectPairs.Select(p => p.UserData.UID).ToList(), _pairManager.Groups.Values.Select(g => g.GID).ToList());
    }

    private bool IsStageAccessForAll(StageShareEntryDto entry)
    {
        var allUids = new HashSet<string>(_pairManager.DirectPairs.Select(p => p.UserData.UID), StringComparer.OrdinalIgnoreCase);
        var allGids = new HashSet<string>(_pairManager.Groups.Values.Select(g => g.GID), StringComparer.OrdinalIgnoreCase);
        return allUids.SetEquals(entry.AllowedIndividuals) && allGids.SetEquals(entry.AllowedSyncshells);
    }

    private static string StageLabel(LocalStageDefinition stage)
        => string.IsNullOrWhiteSpace(stage.Name) ? stage.Filename : stage.Name;

    private static string FormatStageLocation(StageLocationDto location)
        => string.Format(CultureInfo.CurrentCulture, Loc.Get("Stages.LocationFormat"),
            location.WorldId, location.TerritoryId, StagePart(location.WardId), StagePart(location.HouseId), StagePart(location.RoomId));

    private static string FormatStageLocationShort(StageLocationDto location)
        => $"W{location.WorldId} T{location.TerritoryId} {StagePart(location.WardId)}/{StagePart(location.HouseId)}/{StagePart(location.RoomId)}";

    private static string StagePart(int value)
        => value < 0 ? "–" : value.ToString(CultureInfo.InvariantCulture);
}
