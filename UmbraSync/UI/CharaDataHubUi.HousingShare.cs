using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using UmbraSync.Localization;

namespace UmbraSync.UI;

public sealed partial class CharaDataHubUi
{
    private string _housingShareDescription = string.Empty;
    private bool _housingShareInitialized;
    private readonly List<string> _housingShareAllowedIndividuals = new();
    private readonly List<string> _housingShareAllowedSyncshells = new();
    private string _housingShareIndividualDropdownSelection = string.Empty;
    private string _housingShareIndividualInput = string.Empty;
    private string _housingShareSyncshellDropdownSelection = string.Empty;
    private string _housingShareSyncshellInput = string.Empty;
    private Guid? _housingShareEditingId;
    private string _housingShareEditDescription = string.Empty;
    private readonly List<string> _housingShareEditAllowedIndividuals = new();
    private readonly List<string> _housingShareEditAllowedSyncshells = new();
    private string _housingShareEditIndividualDropdownSelection = string.Empty;
    private string _housingShareEditIndividualInput = string.Empty;
    private string _housingShareEditSyncshellDropdownSelection = string.Empty;
    private string _housingShareEditSyncshellInput = string.Empty;

    private void DrawHousingShare()
    {
        if (!_uiSharedService.ApiController.IsConnected)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("HousingShare.ServerRequired"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(5);
            return;
        }

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
            UiSharedService.ColorTextWrapped(Loc.Get("HousingShare.Processing"), ImGuiColors.DalamudYellow);
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
        bool isInHousing = currentLocation.HouseId != 0 && _dalamudUtilService.IsInHousingMode;

        if (!isInHousing)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("HousingShare.NotInHousing"), ImGuiColors.DalamudGrey3);
        }
        else
        {
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingShare.ServerInfo"),
                currentLocation.ServerId, currentLocation.TerritoryId, currentLocation.WardId, currentLocation.HouseId));
            ImGuiHelpers.ScaledDummy(3);

            // Scanner section
            UiSharedService.DistanceSeparator();
            _uiSharedService.BigText(Loc.Get("HousingShare.Scanner"));

            if (scanner.IsScanning)
            {
                UiSharedService.ColorTextWrapped(
                    string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingShare.ScanResult"), scanner.CollectedFileCount),
                    UiSharedService.AccentColor);
                ImGuiHelpers.ScaledDummy(3);

                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Stop, Loc.Get("HousingShare.StopScan")))
                {
                    scanner.StopScan();
                }
            }
            else
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Search, Loc.Get("HousingShare.ScanButton")))
                {
                    scanner.StartScan(currentLocation);
                }
            }

            // Publish section
            if (scanner.CollectedFileCount > 0)
            {
                ImGuiHelpers.ScaledDummy(5);
                UiSharedService.DistanceSeparator();
                _uiSharedService.BigText(Loc.Get("HousingShare.PublishButton"));

                UiSharedService.ColorTextWrapped(
                    string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingShare.ScanResult"), scanner.CollectedFileCount),
                    ImGuiColors.HealerGreen);

                ImGui.SetNextItemWidth(300);
                ImGui.InputTextWithHint("##housingShareDesc", Loc.Get("HousingShare.Description"), ref _housingShareDescription, 128);

                ImGuiHelpers.ScaledDummy(3);

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
                        if (ImGui.SmallButton("Retirer"))
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
                        if (ImGui.SmallButton("Retirer"))
                        {
                            _housingShareAllowedSyncshells.Remove(shell);
                        }
                    }
                }

                ImGuiHelpers.ScaledDummy(3);

                using (ImRaii.Disabled(housingShareManager.IsBusy))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, Loc.Get("HousingShare.PublishButton")))
                    {
                        _ = housingShareManager.PublishAsync(currentLocation, _housingShareDescription,
                            new List<string>(_housingShareAllowedIndividuals), new List<string>(_housingShareAllowedSyncshells));
                        _housingShareDescription = string.Empty;
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
                ImGui.TextUnformatted($"UID : {entry.AllowedIndividuals.Count}, Syncshells : {entry.AllowedSyncshells.Count}");
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
                    if (ImGui.SmallButton("Modifier"))
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
        ImGui.TextUnformatted("UID synchronis\u00e9 \u00e0 ajouter");

        foreach (var uid in _housingShareEditAllowedIndividuals.ToArray())
        {
            using (ImRaii.PushId("housingShareEditUid" + uid))
            {
                ImGui.BulletText(FormatPairLabel(uid));
                ImGui.SameLine();
                if (ImGui.SmallButton("Retirer"))
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
        ImGui.TextUnformatted("Syncshell \u00e0 ajouter");

        foreach (var shell in _housingShareEditAllowedSyncshells.ToArray())
        {
            using (ImRaii.PushId("housingShareEditShell" + shell))
            {
                ImGui.BulletText(FormatSyncshellLabel(shell));
                ImGui.SameLine();
                if (ImGui.SmallButton("Retirer"))
                {
                    _housingShareEditAllowedSyncshells.Remove(shell);
                }
            }
        }

        ImGuiHelpers.ScaledDummy(3);

        using (ImRaii.Disabled(housingShareManager.IsBusy))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, "Sauvegarder"))
            {
                _ = housingShareManager.UpdateVisibilityAsync(entry.Id, _housingShareEditDescription,
                    new List<string>(_housingShareEditAllowedIndividuals), new List<string>(_housingShareEditAllowedSyncshells));
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
}
