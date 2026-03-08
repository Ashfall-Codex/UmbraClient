using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Data.Enum;
using UmbraSync.Localization;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Utils;

namespace UmbraSync.UI;

public class PlayerAnalysisUI : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiSharedService;
    private Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>>? _cachedAnalysis;
    private bool _hasUpdate = true;
    private bool _sortDirty = true;
    private string _selectedFileTypeTab = string.Empty;
    private string _selectedHash = string.Empty;
    private ObjectKind _selectedObjectTab;

    public PlayerAnalysisUI(ILogger<PlayerAnalysisUI> logger, Pair pair, MareMediator mediator, UiSharedService uiSharedService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, $"{Loc.Get("PlayerAnalysis.WindowTitlePrefix")} {pair.UserData.AliasOrUID}###UmbraPairAnalysis{pair.UserData.UID}", performanceCollectorService)
    {
        Pair = pair;
        _uiSharedService = uiSharedService;
        Mediator.SubscribeKeyed<PairDataAnalyzedMessage>(this, Pair.UserData.UID, (_) =>
        {
            _logger.LogInformation("PairDataAnalyzedMessage received for {uid}", Pair.UserData.UID);
            _hasUpdate = true;
        });
        SizeConstraints = new()
        {
            MinimumSize = new()
            {
                X = 800,
                Y = 600
            },
            MaximumSize = new()
            {
                X = 3840,
                Y = 2160
            }
        };
        IsOpen = true;
    }

    public Pair Pair { get; private init; }
    public PairAnalyzer? PairAnalyzer => Pair.PairAnalyzer;

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    protected override void DrawInternal()
    {
        if (PairAnalyzer == null) return;
        PairAnalyzer analyzer = PairAnalyzer!;

        if (_hasUpdate)
        {
            _cachedAnalysis = analyzer.LastAnalysis.DeepClone();
            _hasUpdate = false;
            _sortDirty = true;
        }

        UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.Intro"), Pair.UserData.AliasOrUID));

        var cachedAnalysis = _cachedAnalysis;
        if (cachedAnalysis == null || cachedAnalysis.Count == 0) return;

        bool isAnalyzing = analyzer.IsAnalysisRunning;
        bool needAnalysis = cachedAnalysis.Any(c => c.Value.Any(f => !f.Value.IsComputed));
        if (isAnalyzing)
        {
            UiSharedService.ColorTextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.Analyzing"), analyzer.CurrentFile, analyzer.TotalFiles),
                ImGuiColors.DalamudYellow);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, Loc.Get("PlayerAnalysis.CancelAnalysis")))
            {
                analyzer.CancelAnalyze();
            }
        }
        else
        {
            if (needAnalysis)
            {
                UiSharedService.ColorTextWrapped(Loc.Get("PlayerAnalysis.MissingEntriesWarning"),
                    ImGuiColors.DalamudYellow);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, Loc.Get("PlayerAnalysis.StartMissing")))
                {
                    _ = analyzer.ComputeAnalysis(print: false);
                }
            }
        }

        ImGui.Separator();

        ImGui.TextUnformatted(Loc.Get("PlayerAnalysis.TotalFiles"));
        ImGui.SameLine();
        ImGui.TextUnformatted(cachedAnalysis.Values.Sum(c => c.Values.Count).ToString(CultureInfo.CurrentCulture));
        ImGui.SameLine();
        using (var _ = ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextUnformatted(FontAwesomeIcon.InfoCircle.ToIconString());
        }
        if (ImGui.IsItemHovered())
        {
            var groupedfiles = cachedAnalysis.Values.SelectMany(f => f.Values).GroupBy(f => f.FileType, StringComparer.Ordinal);
            var text = string.Join(Environment.NewLine, groupedfiles.OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.FileTypeSummary"), f.Key, f.Count(),
                    UiSharedService.ByteToString(f.Sum(v => v.OriginalSize)), UiSharedService.ByteToString(f.Sum(v => v.CompressedSize)))));
            ImGui.SetTooltip(text);
        }
        ImGui.TextUnformatted(Loc.Get("PlayerAnalysis.TotalSizeActual"));
        ImGui.SameLine();
        ImGui.TextUnformatted(UiSharedService.ByteToString(cachedAnalysis.Sum(c => c.Value.Sum(f => f.Value.OriginalSize))));
        ImGui.TextUnformatted(Loc.Get("PlayerAnalysis.TotalSizeCompressed"));
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, needAnalysis))
        {
            ImGui.TextUnformatted(UiSharedService.ByteToString(cachedAnalysis.Sum(c => c.Value.Sum(f => f.Value.CompressedSize))));
            if (needAnalysis && !isAnalyzing)
            {
                ImGui.SameLine();
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    ImGui.TextUnformatted(FontAwesomeIcon.ExclamationCircle.ToIconString());
                UiSharedService.AttachToolTip(Loc.Get("PlayerAnalysis.TotalSizeTooltip"));
            }
        }
        ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.TotalTriangles"), UiSharedService.TrisToString(cachedAnalysis.Sum(c => c.Value.Sum(f => f.Value.Triangles)))));
        ImGui.Separator();

        var playerName = analyzer.LastPlayerName;

        if (playerName.Length == 0)
        {
            playerName = Pair.PlayerName ?? string.Empty;
            analyzer.LastPlayerName = playerName;
        }

        var accent = UiSharedService.AccentColor;
        if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;
        using var tabHoverColor = ImRaii.PushColor(ImGuiCol.TabHovered, accent);
        using var tabActiveColor = ImRaii.PushColor(ImGuiCol.TabActive, accent);
        using var tabbar = ImRaii.TabBar("objectSelection");
        foreach (var kvp in cachedAnalysis)
        {
            using var id = ImRaii.PushId(kvp.Key.ToString());
            string tabText = kvp.Key == ObjectKind.Player ? playerName : $"{playerName}'s {kvp.Key}";
            using var tab = ImRaii.TabItem(tabText + "###" + kvp.Key.ToString());
            if (tab.Success)
            {
                var groupedfiles = kvp.Value.Select(v => v.Value).GroupBy(f => f.FileType, StringComparer.Ordinal)
                    .OrderBy(k => k.Key, StringComparer.Ordinal).ToList();

                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.FilesFor"), tabText));

                ImGui.SameLine();
                ImGui.TextUnformatted(kvp.Value.Count.ToString());
                ImGui.SameLine();

                using (var _ = ImRaii.PushFont(UiBuilder.IconFont))
                {
                    ImGui.TextUnformatted(FontAwesomeIcon.InfoCircle.ToIconString());
                }
                if (ImGui.IsItemHovered())
                {
                    var text = string.Join(Environment.NewLine, groupedfiles
                        .Select(f => f.Key + ": " + f.Count() + " files, size: " + UiSharedService.ByteToString(f.Sum(v => v.OriginalSize))
                        + ", compressed: " + UiSharedService.ByteToString(f.Sum(v => v.CompressedSize))));
                    ImGui.SetTooltip(text);
                }
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.ObjectSizeActual"), kvp.Key));
                ImGui.SameLine();
                ImGui.TextUnformatted(UiSharedService.ByteToString(kvp.Value.Sum(c => c.Value.OriginalSize)));
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.ObjectSizeDownload"), kvp.Key));
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, needAnalysis))
                {
                    ImGui.TextUnformatted(UiSharedService.ByteToString(kvp.Value.Sum(c => c.Value.CompressedSize)));
                    if (needAnalysis && !isAnalyzing)
                    {
                        ImGui.SameLine();
                        using (ImRaii.PushFont(UiBuilder.IconFont))
                            ImGui.TextUnformatted(FontAwesomeIcon.ExclamationCircle.ToIconString());
                        UiSharedService.AttachToolTip(Loc.Get("PlayerAnalysis.StartAnalysisTooltip"));
                    }
                }
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.VramUsage"), kvp.Key));
                ImGui.SameLine();
                var vramUsage = groupedfiles.SingleOrDefault(v => string.Equals(v.Key, "tex", StringComparison.Ordinal));
                if (vramUsage != null)
                {
                    ImGui.TextUnformatted(UiSharedService.ByteToString(vramUsage.Sum(f => f.OriginalSize)));
                }
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.ModTriangles"), kvp.Key, UiSharedService.TrisToString(kvp.Value.Sum(f => f.Value.Triangles))));
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.ObjectTriangles"), kvp.Key, UiSharedService.TrisToString(kvp.Value.Sum(f => f.Value.Triangles))));

                ImGui.Separator();
                if (_selectedObjectTab != kvp.Key)
                {
                    _selectedHash = string.Empty;
                    _selectedObjectTab = kvp.Key;
                    _selectedFileTypeTab = string.Empty;
                }

                using var fileTabBar = ImRaii.TabBar("fileTabs");

                foreach (IGrouping<string, CharacterAnalyzer.FileDataEntry> fileGroup in groupedfiles)
                {
                    string fileGroupText = fileGroup.Key + " [" + fileGroup.Count() + "]";
                    var requiresCompute = fileGroup.Any(k => !k.IsComputed);
                    using var tabcol = ImRaii.PushColor(ImGuiCol.Tab, UiSharedService.Color(ImGuiColors.DalamudYellow), requiresCompute);
                    ImRaii.IEndObject fileTab;
                    using (var _ = ImRaii.PushColor(ImGuiCol.Text, UiSharedService.Color(new(0, 0, 0, 1)),
                        requiresCompute && !string.Equals(_selectedFileTypeTab, fileGroup.Key, StringComparison.Ordinal)))
                    {
                        fileTab = ImRaii.TabItem(fileGroupText + "###" + fileGroup.Key);
                    }

                    if (!fileTab) { fileTab.Dispose(); continue; }

                    if (!string.Equals(fileGroup.Key, _selectedFileTypeTab, StringComparison.Ordinal))
                    {
                        _selectedFileTypeTab = fileGroup.Key;
                        _selectedHash = string.Empty;
                    }

                    ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.FileGroup.Files"), fileGroup.Key));
                    ImGui.SameLine();
                    ImGui.TextUnformatted(fileGroup.Count().ToString());

                    ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.FileGroup.SizeActual"), fileGroup.Key));
                    ImGui.SameLine();
                    ImGui.TextUnformatted(UiSharedService.ByteToString(fileGroup.Sum(c => c.OriginalSize)));

                    ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.FileGroup.SizeDownload"), fileGroup.Key));
                    ImGui.SameLine();
                    ImGui.TextUnformatted(UiSharedService.ByteToString(fileGroup.Sum(c => c.CompressedSize)));

                    ImGui.Separator();
                    DrawTable(fileGroup);

                    fileTab.Dispose();
                }
            }
        }

        ImGui.Separator();

        ImGui.TextUnformatted(Loc.Get("PlayerAnalysis.SelectedFile"));
        ImGui.SameLine();
        UiSharedService.ColorText(_selectedHash, ImGuiColors.DalamudYellow);

        if (cachedAnalysis[_selectedObjectTab].TryGetValue(_selectedHash, out CharacterAnalyzer.FileDataEntry? item))
        {
            var gamepaths = item.GamePaths;
            ImGui.TextUnformatted(Loc.Get("PlayerAnalysis.GamePath"));
            ImGui.SameLine();
            UiSharedService.TextWrapped(gamepaths[0]);
            if (gamepaths.Count > 1)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("PlayerAnalysis.AndMore"), gamepaths.Count - 1));
                ImGui.SameLine();
                _uiSharedService.IconText(FontAwesomeIcon.InfoCircle);
                UiSharedService.AttachToolTip(string.Join(Environment.NewLine, gamepaths.Skip(1)));
            }
        }
    }

    private void DrawTable(IGrouping<string, CharacterAnalyzer.FileDataEntry> fileGroup)
    {
        var tableColumns = string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal)
            ? 5
            : (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) ? 5 : 4);
        using var table = ImRaii.Table("Analysis", tableColumns, ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, 300));
        if (!table.Success) return;
        ImGui.TableSetupColumn(Loc.Get("PlayerAnalysis.Table.Hash"));
        ImGui.TableSetupColumn(Loc.Get("PlayerAnalysis.Table.Gamepaths"), ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn(Loc.Get("PlayerAnalysis.Table.FileSize"), ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn(Loc.Get("PlayerAnalysis.Table.DownloadSize"), ImGuiTableColumnFlags.PreferSortDescending);
        if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal))
        {
            ImGui.TableSetupColumn(Loc.Get("PlayerAnalysis.Table.Format"));
        }
        if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal))
        {
            ImGui.TableSetupColumn(Loc.Get("PlayerAnalysis.Table.Triangles"), ImGuiTableColumnFlags.PreferSortDescending);
        }
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsDirty || _sortDirty)
        {
            var idx = sortSpecs.Specs.ColumnIndex;

            if (idx == 0 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 0 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Key, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 1 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.GamePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 1 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.GamePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 2 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.OriginalSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 2 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.OriginalSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 3 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.CompressedSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 3 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.CompressedSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) && idx == 4 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) && idx == 4 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 4 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Format.Value, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 4 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.Format.Value, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);

            sortSpecs.SpecsDirty = false;
            _sortDirty = false;
        }

        foreach (var item in fileGroup)
        {
            using var text = ImRaii.PushColor(ImGuiCol.Text, new Vector4(0, 0, 0, 1), string.Equals(item.Hash, _selectedHash, StringComparison.Ordinal));
            using var text2 = ImRaii.PushColor(ImGuiCol.Text, new Vector4(1, 1, 1, 1), !item.IsComputed);
            ImGui.TableNextColumn();
            if (string.Equals(_selectedHash, item.Hash, StringComparison.Ordinal))
            {
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, UiSharedService.Color(ImGuiColors.DalamudYellow));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, UiSharedService.Color(ImGuiColors.DalamudYellow));
            }
            ImGui.TextUnformatted(item.Hash);
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.GamePaths.Count.ToString());
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(UiSharedService.ByteToString(item.OriginalSize));
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, !item.IsComputed))
                ImGui.TextUnformatted(UiSharedService.ByteToString(item.CompressedSize));
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal))
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Format.Value);
                if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            }
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal))
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(UiSharedService.TrisToString(item.Triangles));
                if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            }
        }
    }
}