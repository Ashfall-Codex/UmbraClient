using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Data.Enum;
using UmbraSync.Interop.Ipc;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Utils;

namespace UmbraSync.UI;

public class DataAnalysisUi : WindowMediatorSubscriberBase
{
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly Progress<(string, int)> _conversionProgress = new();
    private readonly IpcManager _ipcManager;
    private readonly UiSharedService _uiSharedService;
    private readonly Dictionary<string, string[]> _texturesToConvert = new(StringComparer.Ordinal);
    private Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>>? _cachedAnalysis;
    private CancellationTokenSource? _conversionCancellationTokenSource = new();
    private string _conversionCurrentFileName = string.Empty;
    private int _conversionCurrentFileProgress = 0;
    private Task? _conversionTask;
    private bool _enableBc7ConversionMode = false;
    private bool _hasUpdate = false;
    private bool _sortDirty = true;
    private bool _modalOpen = false;
    private string _selectedFileTypeTab = string.Empty;
    private string _selectedHash = string.Empty;
    private ObjectKind _selectedObjectTab;
    private bool _showModal = false;
    private int _lastSortColumnIdx = -1;
    private ImGuiSortDirection _lastSortDirection = ImGuiSortDirection.None;
    private string _lastSortedFileGroupKey = string.Empty;

    public DataAnalysisUi(ILogger<DataAnalysisUi> logger, MareMediator mediator,
        CharacterAnalyzer characterAnalyzer, IpcManager ipcManager,
        PerformanceCollectorService performanceCollectorService,
        UiSharedService uiSharedService)
        : base(logger, mediator, Loc.Get("DataAnalysis.WindowTitle"), performanceCollectorService)
    {
        _characterAnalyzer = characterAnalyzer;
        _ipcManager = ipcManager;
        _uiSharedService = uiSharedService;
        Mediator.Subscribe<CharacterDataAnalyzedMessage>(this, (_) =>
        {
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

        _conversionProgress.ProgressChanged += ConversionProgress_ProgressChanged;
    }

    protected override void DrawInternal()
    {
        DrawAnalysisContent();
    }

    public void DrawInline()
    {
        using (ImRaii.PushId("CharacterAnalysisInline"))
        {
            DrawAnalysisContent();
        }
    }

    private void DrawAnalysisContent()
    {
        if (_conversionTask != null && !_conversionTask.IsCompleted)
        {
            _showModal = true;
            if (ImGui.BeginPopupModal(Loc.Get("DataAnalysis.Modal.Title")))
            {
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.Modal.Progress"), _conversionCurrentFileProgress, _texturesToConvert.Count));
                UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.Modal.CurrentFile"), _conversionCurrentFileName));
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, Loc.Get("DataAnalysis.Modal.Cancel")))
                {
                    TryCancel(_conversionCancellationTokenSource);
                }
                UiSharedService.SetScaledWindowSize(500);
                ImGui.EndPopup();
            }
            else
            {
                _modalOpen = false;
            }
        }
        else if (_conversionTask != null && _conversionTask.IsCompleted && _texturesToConvert.Count > 0)
        {
            _conversionTask = null;
            _texturesToConvert.Clear();
            _showModal = false;
            _modalOpen = false;
            _enableBc7ConversionMode = false;
        }

        if (_showModal && !_modalOpen)
        {
            ImGui.OpenPopup(Loc.Get("DataAnalysis.Modal.Title"));
            _modalOpen = true;
        }

        if (_hasUpdate)
        {
            _cachedAnalysis = _characterAnalyzer.LastAnalysis.DeepClone();
            _hasUpdate = false;
            _sortDirty = true;
        }

        UiSharedService.TextWrapped(Loc.Get("DataAnalysis.Intro"));

        var cachedAnalysis = _cachedAnalysis;
        if (cachedAnalysis == null || cachedAnalysis.Count == 0) return;

        bool isAnalyzing = _characterAnalyzer.IsAnalysisRunning;
        bool needAnalysis = cachedAnalysis.Any(c => c.Value.Any(f => !f.Value.IsComputed));
        if (isAnalyzing)
        {
            UiSharedService.ColorTextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.Analyzing"), _characterAnalyzer.CurrentFile, _characterAnalyzer.TotalFiles),
                UiSharedService.AccentColor);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, Loc.Get("DataAnalysis.CancelAnalysis")))
            {
                _characterAnalyzer.CancelAnalyze();
            }
        }
        else
        {
            if (needAnalysis)
            {
                UiSharedService.ColorTextWrapped(Loc.Get("DataAnalysis.MissingEntriesWarning"),
                    UiSharedService.AccentColor);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, Loc.Get("DataAnalysis.StartMissing")))
                {
                    _ = _characterAnalyzer.ComputeAnalysis(print: false);
                }
            }
            else
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, Loc.Get("DataAnalysis.StartAll")))
                {
                    _ = _characterAnalyzer.ComputeAnalysis(print: false, recalculate: true);
                }
            }
        }

        ImGui.Separator();

        ImGui.TextUnformatted(Loc.Get("DataAnalysis.TotalFiles"));
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
                .Select(f => string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.FileTypeSummary"), f.Key, f.Count(),
                    UiSharedService.ByteToString(f.Sum(v => v.OriginalSize)), UiSharedService.ByteToString(f.Sum(v => v.CompressedSize)))));
            ImGui.SetTooltip(text);
        }
        ImGui.TextUnformatted(Loc.Get("DataAnalysis.TotalSizeActual"));
        ImGui.SameLine();
        ImGui.TextUnformatted(UiSharedService.ByteToString(cachedAnalysis.Sum(c => c.Value.Sum(f => f.Value.OriginalSize))));
        ImGui.TextUnformatted(Loc.Get("DataAnalysis.TotalSizeDownload"));
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, UiSharedService.AccentColor, needAnalysis))
        {
            ImGui.TextUnformatted(UiSharedService.ByteToString(cachedAnalysis.Sum(c => c.Value.Sum(f => f.Value.CompressedSize))));
            if (needAnalysis && !isAnalyzing)
            {
                ImGui.SameLine();
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    ImGui.TextUnformatted(FontAwesomeIcon.ExclamationCircle.ToIconString());
                UiSharedService.AttachToolTip(Loc.Get("DataAnalysis.TotalSizeTooltip"));
            }
        }
        ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.TotalTriangles"), UiSharedService.TrisToString(cachedAnalysis.Sum(c => c.Value.Sum(f => f.Value.Triangles)))));
        ImGui.Separator();

        DrawObjectSelectionTabs(cachedAnalysis, needAnalysis, isAnalyzing);

        ImGui.Separator();

        ImGui.TextUnformatted(Loc.Get("DataAnalysis.SelectedFile"));
        ImGui.SameLine();
        UiSharedService.ColorText(_selectedHash, UiSharedService.AccentColor);

        if (cachedAnalysis[_selectedObjectTab].TryGetValue(_selectedHash, out CharacterAnalyzer.FileDataEntry? item))
        {
            var filePaths = item.FilePaths;
            ImGui.TextUnformatted(Loc.Get("DataAnalysis.LocalPath"));
            ImGui.SameLine();
            UiSharedService.TextWrapped(filePaths[0]);
            if (filePaths.Count > 1)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.AndMore"), filePaths.Count - 1));
                ImGui.SameLine();
                _uiSharedService.IconText(FontAwesomeIcon.InfoCircle);
                UiSharedService.AttachToolTip(string.Join(Environment.NewLine, filePaths.Skip(1)));
            }

            var gamepaths = item.GamePaths;
            ImGui.TextUnformatted(Loc.Get("DataAnalysis.GamePath"));
            ImGui.SameLine();
            UiSharedService.TextWrapped(gamepaths[0]);
            if (gamepaths.Count > 1)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.AndMore"), gamepaths.Count - 1));
                ImGui.SameLine();
                _uiSharedService.IconText(FontAwesomeIcon.InfoCircle);
                UiSharedService.AttachToolTip(string.Join(Environment.NewLine, gamepaths.Skip(1)));
            }
        }
    }

    public override void OnOpen()
    {
        _hasUpdate = true;
        _selectedHash = string.Empty;
        _enableBc7ConversionMode = false;
        _texturesToConvert.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelAndDispose(ref _conversionCancellationTokenSource);
            _conversionProgress.ProgressChanged -= ConversionProgress_ProgressChanged;
        }

        base.Dispose(disposing);
    }

    private void ConversionProgress_ProgressChanged(object? sender, (string, int) e)
    {
        _conversionCurrentFileName = e.Item1;
        _conversionCurrentFileProgress = e.Item2;
    }

    private void DrawObjectSelectionTabs(Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>> cachedAnalysis, bool needAnalysis, bool isAnalyzing)
    {
        var accent = UiSharedService.AccentColor;
        if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;
        using var objectTabHoverColor = ImRaii.PushColor(ImGuiCol.TabHovered, accent);
        using var objectTabActiveColor = ImRaii.PushColor(ImGuiCol.TabActive, accent);
        using var tabbar = ImRaii.TabBar("objectSelection");
        foreach (var kvp in cachedAnalysis)
        {
            using var id = ImRaii.PushId(kvp.Key.ToString());
            string tabText = kvp.Key.ToString();
            using var tab = ImRaii.TabItem(tabText + "###" + kvp.Key.ToString());
            if (!tab.Success) continue;

            var groupedfiles = kvp.Value.Select(v => v.Value).GroupBy(f => f.FileType, StringComparer.Ordinal)
                .OrderBy(k => k.Key, StringComparer.Ordinal).ToList();

            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.FilesFor"), kvp.Key));
            ImGui.SameLine();
            ImGui.TextUnformatted(kvp.Value.Count.ToString());
            ImGui.SameLine();

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextUnformatted(FontAwesomeIcon.InfoCircle.ToIconString());
            }

            if (ImGui.IsItemHovered())
            {
                var text = string.Join(Environment.NewLine, groupedfiles
                    .Select(f => string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.FileTypeSummary"), f.Key, f.Count(),
                        UiSharedService.ByteToString(f.Sum(v => v.OriginalSize)), UiSharedService.ByteToString(f.Sum(v => v.CompressedSize)))));
                ImGui.SetTooltip(text);
            }

            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.ObjectSizeActual"), kvp.Key));
            ImGui.SameLine();
            ImGui.TextUnformatted(UiSharedService.ByteToString(kvp.Value.Sum(c => c.Value.OriginalSize)));
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.ObjectSizeDownload"), kvp.Key));
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, UiSharedService.AccentColor, needAnalysis))
            {
                ImGui.TextUnformatted(UiSharedService.ByteToString(kvp.Value.Sum(c => c.Value.CompressedSize)));
                if (needAnalysis && !isAnalyzing)
                {
                    ImGui.SameLine();
                    using (ImRaii.PushFont(UiBuilder.IconFont))
                        ImGui.TextUnformatted(FontAwesomeIcon.ExclamationCircle.ToIconString());
                    UiSharedService.AttachToolTip(Loc.Get("DataAnalysis.TotalSizeTooltip"));
                }
            }

            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.ObjectVram"), kvp.Key));
            ImGui.SameLine();
            var vramUsage = groupedfiles.SingleOrDefault(v => string.Equals(v.Key, "tex", StringComparison.Ordinal));
            if (vramUsage != null)
            {
                ImGui.TextUnformatted(UiSharedService.ByteToString(vramUsage.Sum(f => f.OriginalSize)));
            }
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.ObjectTriangles"), kvp.Key, UiSharedService.TrisToString(kvp.Value.Sum(f => f.Value.Triangles))));

            ImGui.Separator();
            if (_selectedObjectTab != kvp.Key)
            {
                _selectedHash = string.Empty;
                _selectedObjectTab = kvp.Key;
                _selectedFileTypeTab = string.Empty;
                _enableBc7ConversionMode = false;
                _texturesToConvert.Clear();
            }

            using var fileTabHoverColor = ImRaii.PushColor(ImGuiCol.TabHovered, accent);
            using var fileTabActiveColor = ImRaii.PushColor(ImGuiCol.TabActive, accent);
            using var fileTabBar = ImRaii.TabBar("fileTabs");

            foreach (var fileGroup in groupedfiles)
            {
                string fileGroupText = fileGroup.Key + " [" + fileGroup.Count() + "]";
                var requiresCompute = fileGroup.Any(k => !k.IsComputed);
                ImRaii.IEndObject fileTab;
                using (ImRaii.PushColor(ImGuiCol.Text, UiSharedService.Color(Vector4.One),
                    requiresCompute && !string.Equals(_selectedFileTypeTab, fileGroup.Key, StringComparison.Ordinal)))
                {
                    fileTab = ImRaii.TabItem(fileGroupText + "###" + fileGroup.Key);
                }

                if (!fileTab)
                {
                    fileTab.Dispose();
                    continue;
                }

                if (!string.Equals(fileGroup.Key, _selectedFileTypeTab, StringComparison.Ordinal))
                {
                    _selectedFileTypeTab = fileGroup.Key;
                    _selectedHash = string.Empty;
                    _enableBc7ConversionMode = false;
                    _texturesToConvert.Clear();
                }

                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.FileGroup.Files"), fileGroup.Key));
                ImGui.SameLine();
                ImGui.TextUnformatted(fileGroup.Count().ToString());

                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.FileGroup.SizeActual"), fileGroup.Key));
                ImGui.SameLine();
                ImGui.TextUnformatted(UiSharedService.ByteToString(fileGroup.Sum(c => c.OriginalSize)));

                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.FileGroup.SizeDownload"), fileGroup.Key));
                ImGui.SameLine();
                ImGui.TextUnformatted(UiSharedService.ByteToString(fileGroup.Sum(c => c.CompressedSize)));

                if (string.Equals(_selectedFileTypeTab, "tex", StringComparison.Ordinal))
                {
                    ImGui.Checkbox(Loc.Get("DataAnalysis.Bc7.Enable"), ref _enableBc7ConversionMode);
                    if (_enableBc7ConversionMode)
                    {
                        UiSharedService.ColorText(Loc.Get("DataAnalysis.Bc7.WarningTitle"), UiSharedService.AccentColor);
                        ImGui.SameLine();
                        UiSharedService.ColorText(Loc.Get("DataAnalysis.Bc7.WarningIrreversible"), UiSharedService.AccentColor);
                        UiSharedService.ColorTextWrapped(Loc.Get("DataAnalysis.Bc7.WarningDetails"), UiSharedService.AccentColor);
                        if (_texturesToConvert.Count > 0 && _uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.Bc7.Start"), _texturesToConvert.Count)))
                        {
                            var conversionCts = EnsureFreshCts(ref _conversionCancellationTokenSource);
                            _conversionTask = _ipcManager.Penumbra.ConvertTextureFiles(_logger, _texturesToConvert, _conversionProgress, conversionCts.Token);
                        }
                    }
                }

                ImGui.Separator();
                DrawTable(fileGroup);

                fileTab.Dispose();
            }
        }
    }

    private void DrawTable(IGrouping<string, CharacterAnalyzer.FileDataEntry> fileGroup)
    {
        var tableColumns = string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal)
            ? (_enableBc7ConversionMode ? 7 : 6)
            : (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) ? 6 : 5);
        using var table = ImRaii.Table("Analysis", tableColumns, ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, 300));
        if (!table.Success) return;
        ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.Hash"));
        ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.Filepaths"), ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.Gamepaths"), ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.FileSize"), ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.DownloadSize"), ImGuiTableColumnFlags.PreferSortDescending);
        if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal))
        {
            ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.Format"));
            if (_enableBc7ConversionMode) ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.ConvertBc7"));
        }
        if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal))
        {
            ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.Triangles"), ImGuiTableColumnFlags.PreferSortDescending);
        }
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var sortSpecs = ImGui.TableGetSortSpecs();
        if ((sortSpecs.SpecsDirty || _sortDirty) && sortSpecs.SpecsCount > 0)
        {
            var idx = sortSpecs.Specs.ColumnIndex;
            var dir = sortSpecs.Specs.SortDirection;
            if (_sortDirty || idx != _lastSortColumnIdx || dir != _lastSortDirection || !string.Equals(_lastSortedFileGroupKey, fileGroup.Key, StringComparison.Ordinal))
            {
                if (idx == 0 && dir == ImGuiSortDirection.Ascending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                if (idx == 0 && dir == ImGuiSortDirection.Descending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Key, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                if (idx == 1 && dir == ImGuiSortDirection.Ascending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.FilePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                if (idx == 1 && dir == ImGuiSortDirection.Descending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.FilePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                if (idx == 2 && dir == ImGuiSortDirection.Ascending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.GamePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                if (idx == 2 && dir == ImGuiSortDirection.Descending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.GamePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                if (idx == 3 && dir == ImGuiSortDirection.Ascending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.OriginalSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                if (idx == 3 && dir == ImGuiSortDirection.Descending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.OriginalSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                if (idx == 4 && dir == ImGuiSortDirection.Ascending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.CompressedSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                if (idx == 4 && dir == ImGuiSortDirection.Descending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.CompressedSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) && idx == 5 && dir == ImGuiSortDirection.Ascending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) && idx == 5 && dir == ImGuiSortDirection.Descending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 5 && dir == ImGuiSortDirection.Ascending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Format.Value, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 5 && dir == ImGuiSortDirection.Descending)
                    _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.Format.Value, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);

                _lastSortColumnIdx = idx;
                _lastSortDirection = dir;
                _lastSortedFileGroupKey = fileGroup.Key;
            }

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
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, UiSharedService.Color(UiSharedService.AccentColor));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, UiSharedService.Color(UiSharedService.AccentColor));
            }
            ImGui.TextUnformatted(item.Hash);
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.FilePaths.Count.ToString());
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.GamePaths.Count.ToString());
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(UiSharedService.ByteToString(item.OriginalSize));
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, UiSharedService.AccentColor, !item.IsComputed))
                ImGui.TextUnformatted(UiSharedService.ByteToString(item.CompressedSize));
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal))
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Format.Value);
                if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
                if (_enableBc7ConversionMode)
                {
                    ImGui.TableNextColumn();
                    if (item.Format.Value.StartsWith("BC", StringComparison.Ordinal) || item.Format.Value.StartsWith("DXT", StringComparison.Ordinal)
                        || item.Format.Value.StartsWith("24864", StringComparison.Ordinal)) // BC4
                    {
                        ImGui.TextUnformatted("");
                        continue;
                    }
                    var filePath = item.FilePaths[0];
                    bool toConvert = _texturesToConvert.ContainsKey(filePath);
                    if (ImGui.Checkbox("###convert" + item.Hash, ref toConvert))
                    {
                        if (toConvert && !_texturesToConvert.ContainsKey(filePath))
                        {
                            _texturesToConvert[filePath] = item.FilePaths.Skip(1).ToArray();
                        }
                        else if (!toConvert && _texturesToConvert.ContainsKey(filePath))
                        {
                            _texturesToConvert.Remove(filePath);
                        }
                    }
                }
            }
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal))
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(UiSharedService.TrisToString(item.Triangles));
                if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            }
        }
    }

    private CancellationTokenSource EnsureFreshCts(ref CancellationTokenSource? cts)
    {
        CancelAndDispose(ref cts);
        cts = new CancellationTokenSource();
        return cts;
    }

    private void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        if (cts == null) return;
        TryCancel(cts);
        cts.Dispose();
        cts = null;
    }

    private void TryCancel(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogTrace(ex, "DataAnalysisUi CTS already disposed");
        }
    }
}