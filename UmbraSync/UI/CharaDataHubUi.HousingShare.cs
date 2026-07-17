using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Dto.HousingScenario;
using UmbraSync.Localization;
using UmbraSync.Services.Housing;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI;

public sealed partial class CharaDataHubUi
{
    private int _housingSubTab;
    private bool _housingScenarioInitialized;
    private List<HousingNpcScenario> _localScenes = new();
    private string _housingScenarioSelectedId = string.Empty;
    private string _housingScenarioDescription = string.Empty;
    private bool _housingScenarioToAll = true;
    private readonly List<string> _housingScenarioAllowedIndividuals = new();
    private readonly List<string> _housingScenarioAllowedSyncshells = new();
    private string _housingScenarioIndividualDropdownSelection = string.Empty;
    private string _housingScenarioIndividualInput = string.Empty;
    private string _housingScenarioSyncshellDropdownSelection = string.Empty;
    private string _housingScenarioSyncshellInput = string.Empty;
    private Guid? _housingScenarioEditingId;
    private string _housingScenarioEditDescription = string.Empty;
    private bool _housingScenarioEditToAll;
    private readonly List<string> _housingScenarioEditAllowedIndividuals = new();
    private readonly List<string> _housingScenarioEditAllowedSyncshells = new();
    private string _housingScenarioEditIndividualDropdownSelection = string.Empty;
    private string _housingScenarioEditIndividualInput = string.Empty;
    private string _housingScenarioEditSyncshellDropdownSelection = string.Empty;
    private string _housingScenarioEditSyncshellInput = string.Empty;

    private string _housingShareDescription = string.Empty;
    private bool _housingShareInitialized;
    private bool _housingShareToAll = true;
    private readonly List<string> _housingShareAllowedIndividuals = new();
    private readonly List<string> _housingShareAllowedSyncshells = new();
    private string _housingShareIndividualDropdownSelection = string.Empty;
    private string _housingShareIndividualInput = string.Empty;
    private string _housingShareSyncshellDropdownSelection = string.Empty;
    private string _housingShareSyncshellInput = string.Empty;
    private bool _housingShareDisableSourceMods;
    private Guid? _housingShareEditingId;
    private string _housingShareEditDescription = string.Empty;
    private bool _housingShareEditToAll;
    private readonly List<string> _housingShareEditAllowedIndividuals = new();
    private readonly List<string> _housingShareEditAllowedSyncshells = new();
    private string _housingShareEditIndividualDropdownSelection = string.Empty;
    private string _housingShareEditIndividualInput = string.Empty;
    private string _housingShareEditSyncshellDropdownSelection = string.Empty;
    private string _housingShareEditSyncshellInput = string.Empty;

    private void DrawHousingShare(Vector4 accent)
    {
        if (!_uiSharedService.ApiController.IsConnected)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("HousingShare.ServerRequired"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(5);
            return;
        }

        var housingLabels = new[] { "Meubles", "PNJ" };
        var housingIcons = new[] { FontAwesomeIcon.Couch, FontAwesomeIcon.Users };
        DrawSubTabButtons(housingLabels, housingIcons, ref _housingSubTab, accent);

        ImGuiHelpers.ScaledDummy(4f);

        switch (_housingSubTab)
        {
            case 0:
                using (var id = ImRaii.PushId("housingFurniture"))
                    DrawHousingFurnitureSection();
                break;
            case 1:
                using (var id = ImRaii.PushId("housingScenario"))
                    DrawHousingScenarioSection();
                break;
        }
    }

    private void DrawHousingFurnitureSection()
    {
        var housingShareManager = _housingShareManager_housing;
        var scanner = _housingScanner;
        if (housingShareManager == null || scanner == null) return;

        if (!_housingShareInitialized && !housingShareManager.IsBusy)
        {
            _housingShareInitialized = true;
            _ = housingShareManager.RefreshAsync();
        }

        _uiSharedService.BigText(Loc.Get("HousingShare.Title"));

        if (housingShareManager.IsBusy)
        {
            var progressText = housingShareManager.ProgressStatus ?? Loc.Get("HousingShare.Processing");
            UiSharedService.ColorTextWrapped(progressText, ImGuiColors.DalamudYellow);
        }
        if (!string.IsNullOrEmpty(housingShareManager.LastError))
        {
            UiSharedService.ColorTextWrapped(housingShareManager.LastError!, ImGuiColors.DalamudRed);
        }
        else if (!string.IsNullOrEmpty(housingShareManager.LastSuccess))
        {
            UiSharedService.ColorTextWrapped(housingShareManager.LastSuccess!, ImGuiColors.HealerGreen);
        }

        ImGuiHelpers.ScaledDummy(5);

        var currentLocation = _dalamudUtilService.GetMapDataAsync().GetAwaiter().GetResult();
        bool isInsideHouse = currentLocation.HouseId != 0;
        bool isInHousingEditMode = _dalamudUtilService.IsInHousingMode;

        if (!isInsideHouse)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("HousingShare.NotInHousing"), ImGuiColors.DalamudGrey3);
        }
        else
        {
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingShare.ServerInfo"),
                currentLocation.ServerId, currentLocation.TerritoryId, currentLocation.WardId, currentLocation.HouseId));
            ImGuiHelpers.ScaledDummy(3);

            if (!isInHousingEditMode)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.5f, 0.2f, 1.0f));
                ImGui.TextWrapped(Loc.Get("HousingShare.MustBeInHousingEditMode"));
                ImGui.PopStyleColor();
                ImGuiHelpers.ScaledDummy(3);
            }
            
            ImGuiHelpers.ScaledDummy(3);
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.75f, 0.3f, 1.0f));
            _uiSharedService.IconText(FontAwesomeIcon.ExclamationTriangle);
            ImGui.SameLine();
            ImGui.TextWrapped(Loc.Get("HousingShare.Warning.DefaultCollection"));
            ImGui.PopStyleColor();
            ImGuiHelpers.ScaledDummy(3);
            UiSharedService.DistanceSeparator();
            _uiSharedService.BigText(Loc.Get("HousingShare.Scanner"));

            if (scanner.IsScanning)
            {
                UiSharedService.ColorTextWrapped(
                    string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingShare.ScanResult"), scanner.CollectedFurnitureCount),
                    UiSharedService.AccentColor);
                ImGuiHelpers.ScaledDummy(3);

                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Stop, Loc.Get("HousingShare.StopScan")))
                {
                    scanner.StopScan();
                }
            }
            else
            {
                using (ImRaii.Disabled(!isInHousingEditMode))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Search, Loc.Get("HousingShare.ScanButton")))
                    {
                        scanner.StartScan(currentLocation);
                    }
                }
            }

            // Publish section
            if (scanner.CollectedFurnitureCount > 0)
            {
                ImGuiHelpers.ScaledDummy(5);
                UiSharedService.DistanceSeparator();
                _uiSharedService.BigText(Loc.Get("HousingShare.PublishButton"));

                UiSharedService.ColorTextWrapped(
                    string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingShare.ScanResult"), scanner.CollectedFurnitureCount),
                    ImGuiColors.HealerGreen);

                ImGui.SetNextItemWidth(300);
                ImGui.InputTextWithHint("##housingShareDesc", Loc.Get("HousingShare.Description"), ref _housingShareDescription, 128);

                ImGuiHelpers.ScaledDummy(3);

                // Visibilité : checkbox tout partager
                ImGui.Checkbox("Partager à tous mes paires et syncshells", ref _housingShareToAll);

                if (!_housingShareToAll)
                {
                    // Visibility: Allowed individuals
                    DrawHousingShareIndividualDropdown();
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(220f);
                    if (ImGui.InputTextWithHint("##housingShareUidInput", "UID ou vanity", ref _housingShareIndividualInput, 32))
                    {
                        _housingShareIndividualDropdownSelection = string.Empty;
                    }
                    ImGui.SameLine();
                    var normalizedUid = NormalizeUidCandidate(_housingShareIndividualInput);
                    using (ImRaii.Disabled(string.IsNullOrEmpty(normalizedUid)
                        || _housingShareAllowedIndividuals.Any(p => string.Equals(p, normalizedUid, StringComparison.OrdinalIgnoreCase))))
                    {
                        if (ImGui.SmallButton("Ajouter##housingUid"))
                        {
                            _housingShareAllowedIndividuals.Add(normalizedUid);
                            _housingShareIndividualInput = string.Empty;
                            _housingShareIndividualDropdownSelection = string.Empty;
                        }
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted("UID synchronis\u00e9 \u00e0 ajouter");
                    _uiSharedService.DrawHelpText("Choisissez un pair synchronis\u00e9 dans la liste ou saisissez un UID. Les utilisateurs list\u00e9s pourront r\u00e9cup\u00e9rer ce partage de maison.");

                    foreach (var uid in _housingShareAllowedIndividuals.ToArray())
                    {
                        using (ImRaii.PushId("housingShareUid" + uid))
                        {
                            ImGui.BulletText(FormatPairLabel(uid));
                            ImGui.SameLine();
                            if (ImGui.SmallButton(Loc.Get("HousingScenario.Remove")))
                            {
                                _housingShareAllowedIndividuals.Remove(uid);
                            }
                        }
                    }

                    // Visibility: Allowed syncshells
                    DrawHousingShareSyncshellDropdown();
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(220f);
                    if (ImGui.InputTextWithHint("##housingShareSyncshellInput", "GID ou alias", ref _housingShareSyncshellInput, 32))
                    {
                        _housingShareSyncshellDropdownSelection = string.Empty;
                    }
                    ImGui.SameLine();
                    var normalizedSyncshell = NormalizeSyncshellCandidate(_housingShareSyncshellInput);
                    using (ImRaii.Disabled(string.IsNullOrEmpty(normalizedSyncshell)
                        || _housingShareAllowedSyncshells.Any(p => string.Equals(p, normalizedSyncshell, StringComparison.OrdinalIgnoreCase))))
                    {
                        if (ImGui.SmallButton("Ajouter##housingSyncshell"))
                        {
                            _housingShareAllowedSyncshells.Add(normalizedSyncshell);
                            _housingShareSyncshellInput = string.Empty;
                            _housingShareSyncshellDropdownSelection = string.Empty;
                        }
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted("Syncshell \u00e0 ajouter");
                    _uiSharedService.DrawHelpText("S\u00e9lectionnez une syncshell synchronis\u00e9e ou saisissez un identifiant. Les syncshells list\u00e9es auront acc\u00e8s au partage.");

                    foreach (var shell in _housingShareAllowedSyncshells.ToArray())
                    {
                        using (ImRaii.PushId("housingShareShell" + shell))
                        {
                            ImGui.BulletText(FormatSyncshellLabel(shell));
                            ImGui.SameLine();
                            if (ImGui.SmallButton(Loc.Get("HousingScenario.Remove")))
                            {
                                _housingShareAllowedSyncshells.Remove(shell);
                            }
                        }
                    }
                }

                ImGuiHelpers.ScaledDummy(3);

                ImGui.Checkbox(Loc.Get("HousingShare.DisableSourceAfterPublish"), ref _housingShareDisableSourceMods);
                _uiSharedService.DrawHelpText(Loc.Get("HousingShare.DisableSourceAfterPublish.Help"));

                ImGuiHelpers.ScaledDummy(3);

                using (ImRaii.Disabled(!isInHousingEditMode || housingShareManager.IsBusy))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, Loc.Get("HousingShare.PublishButton")))
                    {
                        var individuals = _housingShareToAll
                            ? _pairManager.DirectPairs.Select(p => p.UserData.UID).ToList()
                            : new List<string>(_housingShareAllowedIndividuals);
                        var syncshells = _housingShareToAll
                            ? _pairManager.Groups.Values.Select(g => g.GID).ToList()
                            : new List<string>(_housingShareAllowedSyncshells);
                        _ = housingShareManager.PublishAsync(currentLocation, _housingShareDescription, individuals, syncshells, _housingShareDisableSourceMods);
                        _housingShareDescription = string.Empty;
                        _housingShareDisableSourceMods = false;
                        _housingShareToAll = true;
                        _housingShareAllowedIndividuals.Clear();
                        _housingShareAllowedSyncshells.Clear();
                        _housingShareIndividualInput = string.Empty;
                        _housingShareSyncshellInput = string.Empty;
                        _housingShareIndividualDropdownSelection = string.Empty;
                        _housingShareSyncshellDropdownSelection = string.Empty;
                    }
                }
            }

            // Applied mods status
            if (housingShareManager.IsApplied)
            {
                ImGuiHelpers.ScaledDummy(5);
                UiSharedService.DistanceSeparator();
                UiSharedService.ColorTextWrapped(Loc.Get("HousingShare.ModsCurrentlyApplied"), ImGuiColors.HealerGreen);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("HousingShare.RemoveMods")))
                {
                    _ = housingShareManager.RemoveAppliedModsAsync();
                }
            }
        }

        // Own shares list
        ImGuiHelpers.ScaledDummy(5);
        UiSharedService.DistanceSeparator();
        _uiSharedService.BigText(Loc.Get("HousingShare.OwnShares"));

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Sync, Loc.Get("HousingShare.Refresh")))
        {
            _ = housingShareManager.RefreshAsync();
        }

        ImGuiHelpers.ScaledDummy(3);

        if (housingShareManager.OwnShares.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("HousingShare.NoOwnShares"));
        }
        else if (ImGui.BeginTable("housing-own-shares", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter))
        {
            ImGui.TableSetupColumn(Loc.Get("HousingShare.Description"));
            ImGui.TableSetupColumn(Loc.Get("HousingShare.Location"));
            ImGui.TableSetupColumn(Loc.Get("HousingShare.CreatedAt"));
            ImGui.TableSetupColumn("Acc\u00e8s");
            ImGui.TableSetupColumn(Loc.Get("HousingShare.Actions"), ImGuiTableColumnFlags.WidthFixed, 140);
            ImGui.TableHeadersRow();

            foreach (var entry in housingShareManager.OwnShares)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrEmpty(entry.Description) ? entry.Id.ToString("D", CultureInfo.InvariantCulture) : entry.Description);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"S{entry.Location.ServerId} W{entry.Location.WardId} H{entry.Location.HouseId}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

                // Access column
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingScenario.AccessSummary"), entry.AllowedIndividuals.Count, entry.AllowedSyncshells.Count));
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    if (entry.AllowedIndividuals.Count > 0)
                    {
                        ImGui.TextUnformatted("UID autoris\u00e9s:");
                        foreach (var uid in entry.AllowedIndividuals)
                            ImGui.BulletText(FormatUidWithName(uid));
                    }
                    else
                    {
                        ImGui.TextDisabled("Aucun UID autoris\u00e9");
                    }
                    ImGui.Separator();
                    if (entry.AllowedSyncshells.Count > 0)
                    {
                        ImGui.TextUnformatted("Syncshells autoris\u00e9es:");
                        foreach (var gid in entry.AllowedSyncshells)
                            ImGui.BulletText(FormatSyncshellLabel(gid));
                    }
                    else
                    {
                        ImGui.TextDisabled("Aucune syncshell autoris\u00e9e");
                    }
                    ImGui.EndTooltip();
                }

                // Actions column
                ImGui.TableNextColumn();
                using (ImRaii.PushId("housingShare" + entry.Id))
                {
                    if (ImGui.SmallButton(Loc.Get("HousingScenario.Edit")))
                    {
                        if (_housingShareEditingId == entry.Id)
                        {
                            _housingShareEditingId = null;
                        }
                        else
                        {
                            _housingShareEditingId = entry.Id;
                            _housingShareEditDescription = entry.Description;
                            _housingShareEditAllowedIndividuals.Clear();
                            _housingShareEditAllowedIndividuals.AddRange(entry.AllowedIndividuals);
                            _housingShareEditAllowedSyncshells.Clear();
                            _housingShareEditAllowedSyncshells.AddRange(entry.AllowedSyncshells);
                            
                            var allUids = new HashSet<string>(_pairManager.DirectPairs.Select(p => p.UserData.UID), StringComparer.OrdinalIgnoreCase);
                            var allGids = new HashSet<string>(_pairManager.Groups.Values.Select(g => g.GID), StringComparer.OrdinalIgnoreCase);
                            
                            _housingShareEditToAll = allUids.SetEquals(entry.AllowedIndividuals) && allGids.SetEquals(entry.AllowedSyncshells);
                            _housingShareEditIndividualInput = string.Empty;
                            _housingShareEditSyncshellInput = string.Empty;
                            _housingShareEditIndividualDropdownSelection = string.Empty;
                            _housingShareEditSyncshellDropdownSelection = string.Empty;
                        }
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton(Loc.Get("HousingShare.Delete")))
                    {
                        _ = housingShareManager.DeleteAsync(entry.Id);
                        if (_housingShareEditingId == entry.Id) _housingShareEditingId = null;
                    }
                }
            }

            ImGui.EndTable();
        }

        // Inline edit section
        if (_housingShareEditingId != null)
        {
            var editEntry = housingShareManager.OwnShares.FirstOrDefault(s => s.Id == _housingShareEditingId);
            if (editEntry != null)
            {
                DrawHousingShareEditSection(housingShareManager, editEntry);
            }
            else
            {
                _housingShareEditingId = null;
            }
        }
    }

    private void DrawHousingShareEditSection(Services.Housing.HousingShareManager housingShareManager, API.Dto.HousingShare.HousingShareEntryDto entry)
    {
        ImGuiHelpers.ScaledDummy(3);
        UiSharedService.DistanceSeparator();
        _uiSharedService.BigText($"Modifier le partage : {(string.IsNullOrEmpty(entry.Description) ? entry.Id.ToString("D", CultureInfo.InvariantCulture) : entry.Description)}");

        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("##housingShareEditDesc", Loc.Get("HousingShare.Description"), ref _housingShareEditDescription, 128);

        ImGuiHelpers.ScaledDummy(3);

        // Visibilité : checkbox tout partager
        ImGui.Checkbox("Partager à tous mes paires et syncshells##edit", ref _housingShareEditToAll);

        if (!_housingShareEditToAll)
        {
            // Edit: Allowed individuals
            DrawHousingShareEditIndividualDropdown();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220f);
            if (ImGui.InputTextWithHint("##housingShareEditUidInput", "UID ou vanity", ref _housingShareEditIndividualInput, 32))
            {
                _housingShareEditIndividualDropdownSelection = string.Empty;
            }
            ImGui.SameLine();
            var normalizedUid = NormalizeUidCandidate(_housingShareEditIndividualInput);
            using (ImRaii.Disabled(string.IsNullOrEmpty(normalizedUid)
                || _housingShareEditAllowedIndividuals.Any(p => string.Equals(p, normalizedUid, StringComparison.OrdinalIgnoreCase))))
            {
                if (ImGui.SmallButton("Ajouter##housingEditUid"))
                {
                    _housingShareEditAllowedIndividuals.Add(normalizedUid);
                    _housingShareEditIndividualInput = string.Empty;
                    _housingShareEditIndividualDropdownSelection = string.Empty;
                }
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(Loc.Get("HousingScenario.UidToAdd"));

            foreach (var uid in _housingShareEditAllowedIndividuals.ToArray())
            {
                using (ImRaii.PushId("housingShareEditUid" + uid))
                {
                    ImGui.BulletText(FormatPairLabel(uid));
                    ImGui.SameLine();
                    if (ImGui.SmallButton(Loc.Get("HousingScenario.Remove")))
                    {
                        _housingShareEditAllowedIndividuals.Remove(uid);
                    }
                }
            }

            // Edit: Allowed syncshells
            DrawHousingShareEditSyncshellDropdown();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220f);
            if (ImGui.InputTextWithHint("##housingShareEditSyncshellInput", "GID ou alias", ref _housingShareEditSyncshellInput, 32))
            {
                _housingShareEditSyncshellDropdownSelection = string.Empty;
            }
            ImGui.SameLine();
            var normalizedSyncshell = NormalizeSyncshellCandidate(_housingShareEditSyncshellInput);
            using (ImRaii.Disabled(string.IsNullOrEmpty(normalizedSyncshell)
                || _housingShareEditAllowedSyncshells.Any(p => string.Equals(p, normalizedSyncshell, StringComparison.OrdinalIgnoreCase))))
            {
                if (ImGui.SmallButton("Ajouter##housingEditSyncshell"))
                {
                    _housingShareEditAllowedSyncshells.Add(normalizedSyncshell);
                    _housingShareEditSyncshellInput = string.Empty;
                    _housingShareEditSyncshellDropdownSelection = string.Empty;
                }
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(Loc.Get("HousingScenario.SyncshellToAdd"));

            foreach (var shell in _housingShareEditAllowedSyncshells.ToArray())
            {
                using (ImRaii.PushId("housingShareEditShell" + shell))
                {
                    ImGui.BulletText(FormatSyncshellLabel(shell));
                    ImGui.SameLine();
                    if (ImGui.SmallButton(Loc.Get("HousingScenario.Remove")))
                    {
                        _housingShareEditAllowedSyncshells.Remove(shell);
                    }
                }
            }
        }

        ImGuiHelpers.ScaledDummy(3);

        using (ImRaii.Disabled(housingShareManager.IsBusy))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("HousingScenario.Save")))
            {
                var editIndividuals = _housingShareEditToAll
                    ? _pairManager.DirectPairs.Select(p => p.UserData.UID).ToList()
                    : new List<string>(_housingShareEditAllowedIndividuals);
                var editSyncshells = _housingShareEditToAll
                    ? _pairManager.Groups.Values.Select(g => g.GID).ToList()
                    : new List<string>(_housingShareEditAllowedSyncshells);
                _ = housingShareManager.UpdateVisibilityAsync(entry.Id, _housingShareEditDescription, editIndividuals, editSyncshells);
                _housingShareEditingId = null;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Annuler"))
        {
            _housingShareEditingId = null;
        }
    }

    private void DrawHousingShareIndividualDropdown()
    {
        ImGui.SetNextItemWidth(220f);
        var previewSource = string.IsNullOrEmpty(_housingShareIndividualDropdownSelection)
            ? _housingShareIndividualInput
            : _housingShareIndividualDropdownSelection;
        var previewLabel = string.IsNullOrEmpty(previewSource)
            ? "S\u00e9lectionner un pair synchronis\u00e9..."
            : FormatPairLabel(previewSource);

        using var combo = ImRaii.Combo("##housingShareUidDropdown", previewLabel, ImGuiComboFlags.None);
        if (!combo) return;

        foreach (var pair in _pairManager.DirectPairs
            .OrderBy(p => p.GetNoteOrName() ?? p.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase))
        {
            var normalized = pair.UserData.UID;
            var display = FormatPairLabel(normalized);
            bool selected = string.Equals(normalized, _housingShareIndividualDropdownSelection, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(display, selected))
            {
                _housingShareIndividualDropdownSelection = normalized;
                _housingShareIndividualInput = normalized;
            }
        }
    }

    private void DrawHousingShareSyncshellDropdown()
    {
        ImGui.SetNextItemWidth(220f);
        var previewSource = string.IsNullOrEmpty(_housingShareSyncshellDropdownSelection)
            ? _housingShareSyncshellInput
            : _housingShareSyncshellDropdownSelection;
        var previewLabel = string.IsNullOrEmpty(previewSource)
            ? "S\u00e9lectionner une syncshell..."
            : FormatSyncshellLabel(previewSource);

        using var combo = ImRaii.Combo("##housingShareSyncshellDropdown", previewLabel, ImGuiComboFlags.None);
        if (!combo) return;

        foreach (var group in _pairManager.Groups.Values
            .OrderBy(g => _serverConfigurationManager.GetNoteForGid(g.GID) ?? g.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase))
        {
            var gid = group.GID;
            var display = FormatSyncshellLabel(gid);
            bool selected = string.Equals(gid, _housingShareSyncshellDropdownSelection, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(display, selected))
            {
                _housingShareSyncshellDropdownSelection = gid;
                _housingShareSyncshellInput = gid;
            }
        }
    }

    private void DrawHousingShareEditIndividualDropdown()
    {
        ImGui.SetNextItemWidth(220f);
        var previewSource = string.IsNullOrEmpty(_housingShareEditIndividualDropdownSelection)
            ? _housingShareEditIndividualInput
            : _housingShareEditIndividualDropdownSelection;
        var previewLabel = string.IsNullOrEmpty(previewSource)
            ? "S\u00e9lectionner un pair synchronis\u00e9..."
            : FormatPairLabel(previewSource);

        using var combo = ImRaii.Combo("##housingShareEditUidDropdown", previewLabel, ImGuiComboFlags.None);
        if (!combo) return;

        foreach (var pair in _pairManager.DirectPairs
            .OrderBy(p => p.GetNoteOrName() ?? p.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase))
        {
            var normalized = pair.UserData.UID;
            var display = FormatPairLabel(normalized);
            bool selected = string.Equals(normalized, _housingShareEditIndividualDropdownSelection, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(display, selected))
            {
                _housingShareEditIndividualDropdownSelection = normalized;
                _housingShareEditIndividualInput = normalized;
            }
        }
    }

    private void DrawHousingShareEditSyncshellDropdown()
    {
        ImGui.SetNextItemWidth(220f);
        var previewSource = string.IsNullOrEmpty(_housingShareEditSyncshellDropdownSelection)
            ? _housingShareEditSyncshellInput
            : _housingShareEditSyncshellDropdownSelection;
        var previewLabel = string.IsNullOrEmpty(previewSource)
            ? "S\u00e9lectionner une syncshell..."
            : FormatSyncshellLabel(previewSource);

        using var combo = ImRaii.Combo("##housingShareEditSyncshellDropdown", previewLabel, ImGuiComboFlags.None);
        if (!combo) return;

        foreach (var group in _pairManager.Groups.Values
            .OrderBy(g => _serverConfigurationManager.GetNoteForGid(g.GID) ?? g.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase))
        {
            var gid = group.GID;
            var display = FormatSyncshellLabel(gid);
            bool selected = string.Equals(gid, _housingShareEditSyncshellDropdownSelection, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(display, selected))
            {
                _housingShareEditSyncshellDropdownSelection = gid;
                _housingShareEditSyncshellInput = gid;
            }
        }
    }

    private void DrawHousingScenarioSection()
    {
        _uiSharedService.BigText(Loc.Get("HousingScenario.Title"));

        ImGuiHelpers.ScaledDummy(5);

        // Limite connue : les mods de tenue passent, les coiffures ne s'affichent pas encore
        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
        _uiSharedService.IconText(FontAwesomeIcon.InfoCircle);
        ImGui.SameLine();
        ImGui.TextWrapped(Loc.Get("HousingScenario.HairNotSupported"));
        ImGui.PopStyleColor();

        ImGuiHelpers.ScaledDummy(5);

        var scenarioManager = _housingScenarioManager;
        if (scenarioManager == null || _housingNpcScenarioService == null) return;

        // Init au premier affichage
        if (!_housingScenarioInitialized && !scenarioManager.IsBusy)
        {
            _housingScenarioInitialized = true;
            RefreshLocalScenarios();
            _ = scenarioManager.RefreshAsync();
        }

        if (scenarioManager.IsBusy)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("HousingScenario.OperationInProgress"), ImGuiColors.DalamudYellow);
        }
        if (!string.IsNullOrEmpty(scenarioManager.LastError))
        {
            UiSharedService.ColorTextWrapped(scenarioManager.LastError!, ImGuiColors.DalamudRed);
        }
        else if (!string.IsNullOrEmpty(scenarioManager.LastSuccess))
        {
            UiSharedService.ColorTextWrapped(scenarioManager.LastSuccess!, ImGuiColors.HealerGreen);
        }

        ImGuiHelpers.ScaledDummy(5);

        var currentLocation = _dalamudUtilService.GetMapDataAsync().GetAwaiter().GetResult();
        bool isInsideHouse = currentLocation.HouseId != 0;

        if (!isInsideHouse)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("HousingScenario.NotInHousing"), ImGuiColors.DalamudGrey3);
        }
        else
        {
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture,
                Loc.Get("HousingScenario.CurrentLocation"),
                currentLocation.ServerId, currentLocation.WardId, currentLocation.HouseId));
            ImGuiHelpers.ScaledDummy(3);
            
            bool isInHousingEditMode = _dalamudUtilService.IsInHousingMode;
            if (!isInHousingEditMode)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.5f, 0.2f, 1.0f));
                ImGui.TextWrapped(Loc.Get("HousingScenario.MustBeInHousingEditMode"));
                ImGui.PopStyleColor();
                ImGuiHelpers.ScaledDummy(3);
            }

            using (ImRaii.Disabled(!isInHousingEditMode))
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Users, Loc.Get("HousingScenario.OpenEditor")))
                    Mediator.Publish(new UiToggleMessage(typeof(HousingNpcSceneEditorUi)));

                ImGuiHelpers.ScaledDummy(3);

                DrawHousingScenarioPublishForm(scenarioManager, currentLocation);
            }
        }

        // État scénario appliqué (côté visiteur)
        if (scenarioManager.IsApplied)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DistanceSeparator();
            UiSharedService.ColorTextWrapped(Loc.Get("HousingScenario.AppliedNotice"), ImGuiColors.HealerGreen);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("HousingScenario.RemoveManually")))
            {
                _ = scenarioManager.RemoveAppliedAsync();
            }
        }

        // Mes scénarios partagés
        ImGuiHelpers.ScaledDummy(5);
        UiSharedService.DistanceSeparator();
        _uiSharedService.BigText(Loc.Get("HousingScenario.OwnSharesTitle"));

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Sync, Loc.Get("HousingScenario.Refresh")))
        {
            RefreshLocalScenarios();
            _ = scenarioManager.RefreshAsync();
        }
        ImGuiHelpers.ScaledDummy(3);

        DrawHousingScenarioOwnSharesTable(scenarioManager);

        // Edit modal
        if (_housingScenarioEditingId != null)
        {
            var entry = scenarioManager.OwnShares.FirstOrDefault(s => s.Id == _housingScenarioEditingId);
            if (entry != null)
            {
                DrawHousingScenarioEditSection(scenarioManager, entry);
            }
            else
            {
                _housingScenarioEditingId = null;
            }
        }
    }

    private void RefreshLocalScenarios()
    {
        _localScenes = _housingNpcScenarioService?.ScenesForCurrentRoom() ?? new();
    }

    private void DrawHousingScenarioPublishForm(HousingScenarioManager scenarioManager, API.Dto.CharaData.LocationInfo currentLocation)
    {
        // Nos scènes sont déjà scopées à la pièce courante : pas de filtrage par location à faire.
        var matching = _localScenes;

        if (matching.Count == 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("HousingScenario.NoneMatching"), ImGuiColors.DalamudGrey3);
            return;
        }

        var selectedInfo = matching.FirstOrDefault(s => string.Equals(s.Id, _housingScenarioSelectedId, StringComparison.Ordinal));
        if (selectedInfo == null)
        {
            selectedInfo = matching[0];
            _housingScenarioSelectedId = selectedInfo.Id;
        }

        ImGui.SetNextItemWidth(360f);
        using (var combo = ImRaii.Combo("##scenarioPick", FormatScenarioLabel(selectedInfo)))
        {
            if (combo)
            {
                foreach (var s in matching)
                {
                    bool isSelected = string.Equals(s.Id, _housingScenarioSelectedId, StringComparison.Ordinal);
                    if (ImGui.Selectable(FormatScenarioLabel(s), isSelected))
                    {
                        _housingScenarioSelectedId = s.Id;
                    }
                }
            }
        }

        ImGuiHelpers.ScaledDummy(3);

        ImGui.SetNextItemWidth(360);
        ImGui.InputTextWithHint("##scenarioDescription", Loc.Get("HousingScenario.DescriptionHint"), ref _housingScenarioDescription, 128);

        ImGuiHelpers.ScaledDummy(3);

        ImGui.Checkbox(Loc.Get("HousingScenario.ShareToAll") + "##scenario", ref _housingScenarioToAll);

        if (!_housingScenarioToAll)
        {
            DrawHousingScenarioIndividualDropdown();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220f);
            if (ImGui.InputTextWithHint("##scenarioUidInput", Loc.Get("HousingScenario.UidHint"), ref _housingScenarioIndividualInput, 32))
            {
                _housingScenarioIndividualDropdownSelection = string.Empty;
            }
            ImGui.SameLine();
            var normalizedUid = NormalizeUidCandidate(_housingScenarioIndividualInput);
            using (ImRaii.Disabled(string.IsNullOrEmpty(normalizedUid)
                || _housingScenarioAllowedIndividuals.Any(p => string.Equals(p, normalizedUid, StringComparison.OrdinalIgnoreCase))))
            {
                if (ImGui.SmallButton("Ajouter##scenarioUid"))
                {
                    _housingScenarioAllowedIndividuals.Add(normalizedUid);
                    _housingScenarioIndividualInput = string.Empty;
                    _housingScenarioIndividualDropdownSelection = string.Empty;
                }
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(Loc.Get("HousingScenario.UidToAdd"));

            foreach (var uid in _housingScenarioAllowedIndividuals.ToArray())
            {
                using (ImRaii.PushId("scenarioUid" + uid))
                {
                    ImGui.BulletText(FormatPairLabel(uid));
                    ImGui.SameLine();
                    if (ImGui.SmallButton(Loc.Get("HousingScenario.Remove")))
                    {
                        _housingScenarioAllowedIndividuals.Remove(uid);
                    }
                }
            }

            DrawHousingScenarioSyncshellDropdown();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220f);
            if (ImGui.InputTextWithHint("##scenarioSyncshellInput", Loc.Get("HousingScenario.SyncshellHint"), ref _housingScenarioSyncshellInput, 32))
            {
                _housingScenarioSyncshellDropdownSelection = string.Empty;
            }
            ImGui.SameLine();
            var normalizedSyncshell = NormalizeSyncshellCandidate(_housingScenarioSyncshellInput);
            using (ImRaii.Disabled(string.IsNullOrEmpty(normalizedSyncshell)
                || _housingScenarioAllowedSyncshells.Any(p => string.Equals(p, normalizedSyncshell, StringComparison.OrdinalIgnoreCase))))
            {
                if (ImGui.SmallButton("Ajouter##scenarioSyncshell"))
                {
                    _housingScenarioAllowedSyncshells.Add(normalizedSyncshell);
                    _housingScenarioSyncshellInput = string.Empty;
                    _housingScenarioSyncshellDropdownSelection = string.Empty;
                }
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(Loc.Get("HousingScenario.SyncshellToAdd"));

            foreach (var shell in _housingScenarioAllowedSyncshells.ToArray())
            {
                using (ImRaii.PushId("scenarioShell" + shell))
                {
                    ImGui.BulletText(FormatSyncshellLabel(shell));
                    ImGui.SameLine();
                    if (ImGui.SmallButton(Loc.Get("HousingScenario.Remove")))
                    {
                        _housingScenarioAllowedSyncshells.Remove(shell);
                    }
                }
            }
        }

        ImGuiHelpers.ScaledDummy(3);

        using (ImRaii.Disabled(scenarioManager.IsBusy))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, Loc.Get("HousingScenario.Publish")))
            {
                var individuals = _housingScenarioToAll
                    ? _pairManager.DirectPairs.Select(p => p.UserData.UID).ToList()
                    : new List<string>(_housingScenarioAllowedIndividuals);
                var syncshells = _housingScenarioToAll
                    ? _pairManager.Groups.Values.Select(g => g.GID).ToList()
                    : new List<string>(_housingScenarioAllowedSyncshells);

                _ = scenarioManager.PublishAsync(currentLocation, selectedInfo, _housingScenarioDescription, individuals, syncshells);

                _housingScenarioDescription = string.Empty;
                _housingScenarioToAll = true;
                _housingScenarioAllowedIndividuals.Clear();
                _housingScenarioAllowedSyncshells.Clear();
                _housingScenarioIndividualInput = string.Empty;
                _housingScenarioSyncshellInput = string.Empty;
                _housingScenarioIndividualDropdownSelection = string.Empty;
                _housingScenarioSyncshellDropdownSelection = string.Empty;
            }
        }
    }

    private void DrawHousingScenarioOwnSharesTable(HousingScenarioManager scenarioManager)
    {
        if (scenarioManager.OwnShares.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("HousingScenario.NoOwnShares"));
            return;
        }

        if (!ImGui.BeginTable("scenario-own-shares", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter))
            return;

        ImGui.TableSetupColumn(Loc.Get("HousingScenario.ColDescription"));
        ImGui.TableSetupColumn(Loc.Get("HousingScenario.ColLocation"));
        ImGui.TableSetupColumn(Loc.Get("HousingScenario.ColCreated"));
        ImGui.TableSetupColumn(Loc.Get("HousingScenario.ColAccess"));
        ImGui.TableSetupColumn(Loc.Get("HousingScenario.ColActions"), ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableHeadersRow();

        foreach (var entry in scenarioManager.OwnShares)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrEmpty(entry.Description) ? entry.Id.ToString("D", CultureInfo.InvariantCulture) : entry.Description);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"S{entry.Location.ServerId} W{entry.Location.WardId} H{entry.Location.HouseId}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingScenario.AccessSummary"), entry.AllowedIndividuals.Count, entry.AllowedSyncshells.Count));
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                if (entry.AllowedIndividuals.Count > 0)
                {
                    ImGui.TextUnformatted(Loc.Get("HousingScenario.AllowedUids"));
                    foreach (var uid in entry.AllowedIndividuals)
                        ImGui.BulletText(FormatUidWithName(uid));
                }
                else
                {
                    ImGui.TextDisabled(Loc.Get("HousingScenario.NoAllowedUid"));
                }
                ImGui.Separator();
                if (entry.AllowedSyncshells.Count > 0)
                {
                    ImGui.TextUnformatted(Loc.Get("HousingScenario.AllowedSyncshells"));
                    foreach (var gid in entry.AllowedSyncshells)
                        ImGui.BulletText(FormatSyncshellLabel(gid));
                }
                else
                {
                    ImGui.TextDisabled(Loc.Get("HousingScenario.NoAllowedSyncshell"));
                }
                ImGui.EndTooltip();
            }

            ImGui.TableNextColumn();
            using (ImRaii.PushId("scenarioShare" + entry.Id))
            {
                if (ImGui.SmallButton(Loc.Get("HousingScenario.Edit")))
                {
                    if (_housingScenarioEditingId == entry.Id)
                    {
                        _housingScenarioEditingId = null;
                    }
                    else
                    {
                        _housingScenarioEditingId = entry.Id;
                        _housingScenarioEditDescription = entry.Description;
                        _housingScenarioEditAllowedIndividuals.Clear();
                        _housingScenarioEditAllowedIndividuals.AddRange(entry.AllowedIndividuals);
                        _housingScenarioEditAllowedSyncshells.Clear();
                        _housingScenarioEditAllowedSyncshells.AddRange(entry.AllowedSyncshells);

                        var allUids = new HashSet<string>(_pairManager.DirectPairs.Select(p => p.UserData.UID), StringComparer.OrdinalIgnoreCase);
                        var allGids = new HashSet<string>(_pairManager.Groups.Values.Select(g => g.GID), StringComparer.OrdinalIgnoreCase);
                        _housingScenarioEditToAll = allUids.SetEquals(entry.AllowedIndividuals) && allGids.SetEquals(entry.AllowedSyncshells);
                        _housingScenarioEditIndividualInput = string.Empty;
                        _housingScenarioEditSyncshellInput = string.Empty;
                        _housingScenarioEditIndividualDropdownSelection = string.Empty;
                        _housingScenarioEditSyncshellDropdownSelection = string.Empty;
                    }
                }
                ImGui.SameLine();
                if (ImGui.SmallButton(Loc.Get("HousingScenario.Delete")))
                {
                    _ = scenarioManager.DeleteAsync(entry.Id);
                    if (_housingScenarioEditingId == entry.Id) _housingScenarioEditingId = null;
                }
            }
        }

        ImGui.EndTable();
    }

    private void DrawHousingScenarioEditSection(HousingScenarioManager scenarioManager, HousingScenarioEntryDto entry)
    {
        ImGuiHelpers.ScaledDummy(3);
        UiSharedService.DistanceSeparator();
        _uiSharedService.BigText($"Modifier : {(string.IsNullOrEmpty(entry.Description) ? entry.Id.ToString("D", CultureInfo.InvariantCulture) : entry.Description)}");

        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("##scenarioEditDesc", Loc.Get("HousingScenario.ColDescription"), ref _housingScenarioEditDescription, 128);

        ImGuiHelpers.ScaledDummy(3);
        ImGui.Checkbox(Loc.Get("HousingScenario.ShareToAll") + "##scenarioEdit", ref _housingScenarioEditToAll);

        if (!_housingScenarioEditToAll)
        {
            DrawHousingScenarioEditIndividualDropdown();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220f);
            if (ImGui.InputTextWithHint("##scenarioEditUidInput", Loc.Get("HousingScenario.UidHint"), ref _housingScenarioEditIndividualInput, 32))
            {
                _housingScenarioEditIndividualDropdownSelection = string.Empty;
            }
            ImGui.SameLine();
            var normalizedUid = NormalizeUidCandidate(_housingScenarioEditIndividualInput);
            using (ImRaii.Disabled(string.IsNullOrEmpty(normalizedUid)
                || _housingScenarioEditAllowedIndividuals.Any(p => string.Equals(p, normalizedUid, StringComparison.OrdinalIgnoreCase))))
            {
                if (ImGui.SmallButton("Ajouter##scenarioEditUid"))
                {
                    _housingScenarioEditAllowedIndividuals.Add(normalizedUid);
                    _housingScenarioEditIndividualInput = string.Empty;
                    _housingScenarioEditIndividualDropdownSelection = string.Empty;
                }
            }

            foreach (var uid in _housingScenarioEditAllowedIndividuals.ToArray())
            {
                using (ImRaii.PushId("scenarioEditUid" + uid))
                {
                    ImGui.BulletText(FormatPairLabel(uid));
                    ImGui.SameLine();
                    if (ImGui.SmallButton(Loc.Get("HousingScenario.Remove")))
                    {
                        _housingScenarioEditAllowedIndividuals.Remove(uid);
                    }
                }
            }

            DrawHousingScenarioEditSyncshellDropdown();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220f);
            if (ImGui.InputTextWithHint("##scenarioEditSyncshellInput", Loc.Get("HousingScenario.SyncshellHint"), ref _housingScenarioEditSyncshellInput, 32))
            {
                _housingScenarioEditSyncshellDropdownSelection = string.Empty;
            }
            ImGui.SameLine();
            var normalizedSyncshell = NormalizeSyncshellCandidate(_housingScenarioEditSyncshellInput);
            using (ImRaii.Disabled(string.IsNullOrEmpty(normalizedSyncshell)
                || _housingScenarioEditAllowedSyncshells.Any(p => string.Equals(p, normalizedSyncshell, StringComparison.OrdinalIgnoreCase))))
            {
                if (ImGui.SmallButton("Ajouter##scenarioEditSyncshell"))
                {
                    _housingScenarioEditAllowedSyncshells.Add(normalizedSyncshell);
                    _housingScenarioEditSyncshellInput = string.Empty;
                    _housingScenarioEditSyncshellDropdownSelection = string.Empty;
                }
            }

            foreach (var shell in _housingScenarioEditAllowedSyncshells.ToArray())
            {
                using (ImRaii.PushId("scenarioEditShell" + shell))
                {
                    ImGui.BulletText(FormatSyncshellLabel(shell));
                    ImGui.SameLine();
                    if (ImGui.SmallButton(Loc.Get("HousingScenario.Remove")))
                    {
                        _housingScenarioEditAllowedSyncshells.Remove(shell);
                    }
                }
            }
        }

        ImGuiHelpers.ScaledDummy(3);
        using (ImRaii.Disabled(scenarioManager.IsBusy))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("HousingScenario.Save")))
            {
                var editIndividuals = _housingScenarioEditToAll
                    ? _pairManager.DirectPairs.Select(p => p.UserData.UID).ToList()
                    : new List<string>(_housingScenarioEditAllowedIndividuals);
                var editSyncshells = _housingScenarioEditToAll
                    ? _pairManager.Groups.Values.Select(g => g.GID).ToList()
                    : new List<string>(_housingScenarioEditAllowedSyncshells);
                _ = scenarioManager.UpdateVisibilityAsync(entry.Id, _housingScenarioEditDescription, editIndividuals, editSyncshells);
                _housingScenarioEditingId = null;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Annuler##scenarioEdit"))
        {
            _housingScenarioEditingId = null;
        }
    }

    private void DrawHousingScenarioIndividualDropdown()
    {
        ImGui.SetNextItemWidth(220f);
        var previewSource = string.IsNullOrEmpty(_housingScenarioIndividualDropdownSelection)
            ? _housingScenarioIndividualInput
            : _housingScenarioIndividualDropdownSelection;
        var previewLabel = string.IsNullOrEmpty(previewSource)
            ? Loc.Get("HousingScenario.SelectPair")
            : FormatPairLabel(previewSource);

        using var combo = ImRaii.Combo("##scenarioUidDropdown", previewLabel, ImGuiComboFlags.None);
        if (!combo) return;

        foreach (var pair in _pairManager.DirectPairs
            .OrderBy(p => p.GetNoteOrName() ?? p.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase))
        {
            var normalized = pair.UserData.UID;
            var display = FormatPairLabel(normalized);
            bool selected = string.Equals(normalized, _housingScenarioIndividualDropdownSelection, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(display, selected))
            {
                _housingScenarioIndividualDropdownSelection = normalized;
                _housingScenarioIndividualInput = normalized;
            }
        }
    }

    private void DrawHousingScenarioSyncshellDropdown()
    {
        ImGui.SetNextItemWidth(220f);
        var previewSource = string.IsNullOrEmpty(_housingScenarioSyncshellDropdownSelection)
            ? _housingScenarioSyncshellInput
            : _housingScenarioSyncshellDropdownSelection;
        var previewLabel = string.IsNullOrEmpty(previewSource)
            ? "Sélectionner une syncshell..."
            : FormatSyncshellLabel(previewSource);

        using var combo = ImRaii.Combo("##scenarioSyncshellDropdown", previewLabel, ImGuiComboFlags.None);
        if (!combo) return;

        foreach (var group in _pairManager.Groups.Values
            .OrderBy(g => _serverConfigurationManager.GetNoteForGid(g.GID) ?? g.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase))
        {
            var gid = group.GID;
            var display = FormatSyncshellLabel(gid);
            bool selected = string.Equals(gid, _housingScenarioSyncshellDropdownSelection, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(display, selected))
            {
                _housingScenarioSyncshellDropdownSelection = gid;
                _housingScenarioSyncshellInput = gid;
            }
        }
    }

    private void DrawHousingScenarioEditIndividualDropdown()
    {
        ImGui.SetNextItemWidth(220f);
        var previewSource = string.IsNullOrEmpty(_housingScenarioEditIndividualDropdownSelection)
            ? _housingScenarioEditIndividualInput
            : _housingScenarioEditIndividualDropdownSelection;
        var previewLabel = string.IsNullOrEmpty(previewSource)
            ? Loc.Get("HousingScenario.SelectPair")
            : FormatPairLabel(previewSource);

        using var combo = ImRaii.Combo("##scenarioEditUidDropdown", previewLabel, ImGuiComboFlags.None);
        if (!combo) return;

        foreach (var pair in _pairManager.DirectPairs
            .OrderBy(p => p.GetNoteOrName() ?? p.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase))
        {
            var normalized = pair.UserData.UID;
            var display = FormatPairLabel(normalized);
            bool selected = string.Equals(normalized, _housingScenarioEditIndividualDropdownSelection, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(display, selected))
            {
                _housingScenarioEditIndividualDropdownSelection = normalized;
                _housingScenarioEditIndividualInput = normalized;
            }
        }
    }

    private void DrawHousingScenarioEditSyncshellDropdown()
    {
        ImGui.SetNextItemWidth(220f);
        var previewSource = string.IsNullOrEmpty(_housingScenarioEditSyncshellDropdownSelection)
            ? _housingScenarioEditSyncshellInput
            : _housingScenarioEditSyncshellDropdownSelection;
        var previewLabel = string.IsNullOrEmpty(previewSource)
            ? "Sélectionner une syncshell..."
            : FormatSyncshellLabel(previewSource);

        using var combo = ImRaii.Combo("##scenarioEditSyncshellDropdown", previewLabel, ImGuiComboFlags.None);
        if (!combo) return;

        foreach (var group in _pairManager.Groups.Values
            .OrderBy(g => _serverConfigurationManager.GetNoteForGid(g.GID) ?? g.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase))
        {
            var gid = group.GID;
            var display = FormatSyncshellLabel(gid);
            bool selected = string.Equals(gid, _housingScenarioEditSyncshellDropdownSelection, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(display, selected))
            {
                _housingScenarioEditSyncshellDropdownSelection = gid;
                _housingScenarioEditSyncshellInput = gid;
            }
        }
    }

    private static string FormatScenarioLabel(HousingNpcScenario scene)
        => $"{scene.Title}  ({scene.Entries.Count} PNJ)";
}
