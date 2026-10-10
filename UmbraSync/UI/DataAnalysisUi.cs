using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
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
using UmbraSync.UI.Components;

namespace UmbraSync.UI;

public class DataAnalysisUi : WindowMediatorSubscriberBase
{
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly Progress<(string, int)> _conversionProgress = new();
    private readonly IpcManager _ipcManager;
    private readonly UiSharedService _uiSharedService;
    private readonly Dictionary<string, string[]> _texturesToConvert = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fileNames = new(StringComparer.Ordinal);
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

    private readonly record struct StatTile(FontAwesomeIcon Icon, string Label, string Value, Vector4 ValueColor, Func<string>? Tooltip = null);

    private readonly record struct Chip(string Label, string? Count, bool Warn = false, string? Tooltip = null);

    public DataAnalysisUi(ILogger<DataAnalysisUi> logger, MareMediator mediator,
        CharacterAnalyzer characterAnalyzer, IpcManager ipcManager,
        PerformanceCollectorService performanceCollectorService,
        UiSharedService uiSharedService)
        : base(logger, mediator, Loc.Get("DataAnalysis.WindowTitle") + "###UmbraSyncDataAnalysis", performanceCollectorService)
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
        DrawConversionModal();

        if (_hasUpdate)
        {
            _cachedAnalysis = _characterAnalyzer.LastAnalysis.DeepClone();
            _hasUpdate = false;
            _sortDirty = true;
            RebuildFileNames();
        }

        var cachedAnalysis = _cachedAnalysis;
        if (cachedAnalysis == null || cachedAnalysis.Count == 0)
        {
            _uiSharedService.DrawEmptyState(FontAwesomeIcon.Microscope,
                Loc.Get("DataAnalysis.Empty.Title"), Loc.Get("DataAnalysis.Empty.Hint"));
            return;
        }

        bool isAnalyzing = _characterAnalyzer.IsAnalysisRunning;
        bool needAnalysis = cachedAnalysis.Any(c => c.Value.Any(f => !f.Value.IsComputed));

        DrawHeaderCard(needAnalysis, isAnalyzing);
        DrawTotals(cachedAnalysis, needAnalysis, isAnalyzing);

        if (!cachedAnalysis.ContainsKey(_selectedObjectTab))
            SelectObject(cachedAnalysis.Keys.First());

        if (cachedAnalysis.Count > 1)
            DrawObjectSelection(cachedAnalysis);

        DrawFiles(cachedAnalysis[_selectedObjectTab], cachedAnalysis.Count > 1);
    }

    private void DrawConversionModal()
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
    }

    private void RebuildFileNames()
    {
        _fileNames.Clear();
        if (_cachedAnalysis == null) return;

        foreach (var entry in _cachedAnalysis.Values.SelectMany(v => v.Values))
        {
            var name = entry.FilePaths.Count > 0 ? Path.GetFileName(entry.FilePaths[0]) : string.Empty;
            _fileNames[entry.Hash] = string.IsNullOrEmpty(name) ? entry.Hash : name;
        }
    }

    private string GetFileName(CharacterAnalyzer.FileDataEntry entry)
        => _fileNames.TryGetValue(entry.Hash, out var name) ? name : entry.Hash;

    private void DrawHeaderCard(bool needAnalysis, bool isAnalyzing)
    {
        UiSharedService.BeginSectionCard(Loc.Get("DataAnalysis.WindowTitle"), FontAwesomeIcon.Microscope);
        UiSharedService.ColorTextWrapped(Loc.Get("DataAnalysis.Intro"), ImGuiColors.DalamudGrey);
        ImGuiHelpers.ScaledDummy(2f);

        if (isAnalyzing)
        {
            string cancelLabel = Loc.Get("DataAnalysis.CancelAnalysis");
            float cancelWidth = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.StopCircle, cancelLabel);
            int total = _characterAnalyzer.TotalFiles;
            float ratio = total > 0 ? Math.Clamp(_characterAnalyzer.CurrentFile / (float)total, 0f, 1f) : 0f;
            string progress = string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.Analyzing"), _characterAnalyzer.CurrentFile, total);
            float barWidth = MathF.Max(80f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - cancelWidth - ImGui.GetStyle().ItemSpacing.X);

            using (ImRaii.PushColor(ImGuiCol.PlotHistogram, UiSharedService.AccentColor))
            using (ImRaii.PushColor(ImGuiCol.FrameBg, UiSharedService.ThemeFrameBg))
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, UiSharedService.RadiusControl * ImGuiHelpers.GlobalScale))
            {
                ImGui.ProgressBar(ratio, new Vector2(barWidth, ImGui.GetFrameHeight()), progress);
            }
            ImGui.SameLine();
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, cancelLabel))
            {
                _characterAnalyzer.CancelAnalyze();
            }
        }
        else if (needAnalysis)
        {
            UiSharedService.DrawNotice(Loc.Get("DataAnalysis.MissingEntriesWarning"), ImGuiColors.DalamudYellow, FontAwesomeIcon.ExclamationCircle);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, Loc.Get("DataAnalysis.StartMissing")))
            {
                _ = _characterAnalyzer.ComputeAnalysis(print: false);
            }
        }
        else if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, Loc.Get("DataAnalysis.StartAll")))
        {
            _ = _characterAnalyzer.ComputeAnalysis(print: false, recalculate: true);
        }

        UiSharedService.EndSectionCard();
    }

    private static void DrawTotals(Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>> cachedAnalysis, bool needAnalysis, bool isAnalyzing)
    {
        var allFiles = cachedAnalysis.Values.SelectMany(v => v.Values).ToList();
        bool downloadPending = needAnalysis && !isAnalyzing;

        DrawStatTiles(
        [
            new StatTile(FontAwesomeIcon.File, Loc.Get("DataAnalysis.Tile.Files"),
                allFiles.Count.ToString(CultureInfo.CurrentCulture), UiSharedService.ThemeNavTextActive,
                () => FileTypeBreakdown(allFiles)),
            new StatTile(FontAwesomeIcon.Hdd, Loc.Get("DataAnalysis.Tile.SizeActual"),
                UiSharedService.ByteToString(allFiles.Sum(f => f.OriginalSize)), UiSharedService.ThemeNavTextActive),
            new StatTile(FontAwesomeIcon.CloudDownloadAlt, Loc.Get("DataAnalysis.Tile.SizeDownload"),
                UiSharedService.ByteToString(allFiles.Sum(f => f.CompressedSize)),
                downloadPending ? ImGuiColors.DalamudOrange : UiSharedService.ThemeNavTextActive,
                downloadPending ? () => Loc.Get("DataAnalysis.TotalSizeTooltip") : null),
            new StatTile(FontAwesomeIcon.Microchip, Loc.Get("DataAnalysis.Tile.Vram"),
                UiSharedService.ByteToString(allFiles.Where(f => string.Equals(f.FileType, "tex", StringComparison.Ordinal)).Sum(f => f.OriginalSize)),
                UiSharedService.ThemeNavTextActive, () => Loc.Get("DataAnalysis.Tile.VramTooltip")),
            new StatTile(FontAwesomeIcon.Cube, Loc.Get("DataAnalysis.Tile.Triangles"),
                UiSharedService.TrisToString(allFiles.Sum(f => f.Triangles)), UiSharedService.ThemeNavTextActive,
                () => Loc.Get("DataAnalysis.Tile.TrianglesTooltip")),
        ]);
    }

    private static string FileTypeBreakdown(IEnumerable<CharacterAnalyzer.FileDataEntry> files)
        => string.Join(Environment.NewLine, files.GroupBy(f => f.FileType, StringComparer.Ordinal)
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.FileTypeSummary"), f.Key, f.Count(),
                UiSharedService.ByteToString(f.Sum(v => v.OriginalSize)), UiSharedService.ByteToString(f.Sum(v => v.CompressedSize)))));

    private static string FilesSummary(ICollection<CharacterAnalyzer.FileDataEntry> files)
        => string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.Summary"), files.Count,
            UiSharedService.ByteToString(files.Sum(f => f.OriginalSize)), UiSharedService.ByteToString(files.Sum(f => f.CompressedSize)));

    private void DrawObjectSelection(Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>> cachedAnalysis)
    {
        UiSharedService.DrawCardTitle(FontAwesomeIcon.Users, Loc.Get("DataAnalysis.Objects.Title"));

        var kinds = cachedAnalysis.Keys.ToList();
        var chips = kinds.Select(k => new Chip(ObjectKindLabel(k), cachedAnalysis[k].Count.ToString(CultureInfo.CurrentCulture),
            cachedAnalysis[k].Values.Any(f => !f.IsComputed), Loc.Get("DataAnalysis.FileGroup.NeedsAnalysis"))).ToList();
        int clicked = DrawChips("objects", chips, kinds.IndexOf(_selectedObjectTab));
        if (clicked >= 0)
            SelectObject(kinds[clicked]);

        ImGuiHelpers.ScaledDummy(4f);
    }

    private static string ObjectKindLabel(ObjectKind kind) => kind switch
    {
        ObjectKind.Player => Loc.Get("DataAnalysis.Object.Player"),
        ObjectKind.MinionOrMount => Loc.Get("DataAnalysis.Object.MinionOrMount"),
        ObjectKind.Companion => Loc.Get("DataAnalysis.Object.Companion"),
        ObjectKind.Pet => Loc.Get("DataAnalysis.Object.Pet"),
        _ => kind.ToString(),
    };

    private void SelectObject(ObjectKind kind)
    {
        if (_selectedObjectTab == kind) return;
        _selectedObjectTab = kind;
        _selectedHash = string.Empty;
        _selectedFileTypeTab = string.Empty;
        _enableBc7ConversionMode = false;
        _texturesToConvert.Clear();
        _sortDirty = true;
    }

    private void SelectFileType(string fileType)
    {
        if (string.Equals(_selectedFileTypeTab, fileType, StringComparison.Ordinal)) return;
        _selectedFileTypeTab = fileType;
        _selectedHash = string.Empty;
        _enableBc7ConversionMode = false;
        _texturesToConvert.Clear();
    }

    private void DrawFiles(Dictionary<string, CharacterAnalyzer.FileDataEntry> objectFiles, bool multipleObjects)
    {
        var groupedFiles = objectFiles.Values.GroupBy(f => f.FileType, StringComparer.Ordinal)
            .OrderBy(k => k.Key, StringComparer.Ordinal).ToList();
        if (groupedFiles.Count == 0) return;

        // Avec plusieurs objets, le résumé dit de quoi est fait l'objet choisi ; seul, il répéterait les tuiles.
        UiSharedService.DrawCardTitle(FontAwesomeIcon.LayerGroup, Loc.Get("DataAnalysis.Files.Title"),
            trailing: multipleObjects ? FilesSummary(objectFiles.Values) : null);

        if (!groupedFiles.Exists(g => string.Equals(g.Key, _selectedFileTypeTab, StringComparison.Ordinal)))
            SelectFileType(groupedFiles[0].Key);

        var chips = groupedFiles.Select(g => new Chip(g.Key, g.Count().ToString(CultureInfo.CurrentCulture),
            g.Any(f => !f.IsComputed), Loc.Get("DataAnalysis.FileGroup.NeedsAnalysis"))).ToList();
        int clicked = DrawChips("fileTypes", chips, groupedFiles.FindIndex(g => string.Equals(g.Key, _selectedFileTypeTab, StringComparison.Ordinal)));
        if (clicked >= 0)
            SelectFileType(groupedFiles[clicked].Key);

        var fileGroup = groupedFiles.First(g => string.Equals(g.Key, _selectedFileTypeTab, StringComparison.Ordinal));
        ImGuiHelpers.ScaledDummy(2f);
        UiSharedService.ColorText(FilesSummary(fileGroup.ToList()), ImGuiColors.DalamudGrey);

        if (string.Equals(_selectedFileTypeTab, "tex", StringComparison.Ordinal))
            DrawBc7Controls();

        ImGuiHelpers.ScaledDummy(2f);

        // Le tableau prend la hauteur restante, moins la carte de détail du fichier sélectionné.
        float detailsHeight = ImGui.GetTextLineHeightWithSpacing() * 4f + ImGui.GetFrameHeight() + 16f * ImGuiHelpers.GlobalScale;
        float tableHeight = MathF.Max(160f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y - detailsHeight);
        DrawTable(fileGroup, tableHeight);

        ImGuiHelpers.ScaledDummy(4f);
        DrawSelectedFile(objectFiles);
    }

    private void DrawBc7Controls()
    {
        ImGuiHelpers.ScaledDummy(2f);
        ToggleSwitch.Draw(Loc.Get("DataAnalysis.Bc7.Enable"), ref _enableBc7ConversionMode);
        if (!_enableBc7ConversionMode) return;

        UiSharedService.DrawNotice(Loc.Get("DataAnalysis.Bc7.WarningTitle") + " " + Loc.Get("DataAnalysis.Bc7.WarningIrreversible")
            + Environment.NewLine + Loc.Get("DataAnalysis.Bc7.WarningDetails"), ImGuiColors.DalamudOrange);

        if (_texturesToConvert.Count == 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("DataAnalysis.Bc7.SelectHint"), ImGuiColors.DalamudGrey3);
            return;
        }

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.Bc7.Start"), _texturesToConvert.Count)))
        {
            var conversionCts = EnsureFreshCts(ref _conversionCancellationTokenSource);
            _conversionTask = _ipcManager.Penumbra.ConvertTextureFiles(_logger, _texturesToConvert, _conversionProgress, conversionCts.Token);
        }
    }

    private void DrawSelectedFile(Dictionary<string, CharacterAnalyzer.FileDataEntry> objectFiles)
    {
        objectFiles.TryGetValue(_selectedHash, out var item);

        UiSharedService.DrawCard("selected-file", () =>
        {
            UiSharedService.DrawCardTitle(FontAwesomeIcon.FileAlt, Loc.Get("DataAnalysis.SelectedFile"),
                trailing: item == null ? null : GetFileName(item));

            if (item == null)
            {
                UiSharedService.ColorTextWrapped(Loc.Get("DataAnalysis.SelectedFile.None"), ImGuiColors.DalamudGrey3);
                return;
            }

            string hashLabel = Loc.Get("DataAnalysis.Table.Hash");
            string localLabel = Loc.Get("DataAnalysis.LocalPath");
            string gameLabel = Loc.Get("DataAnalysis.GamePath");
            float labelWidth = new[] { hashLabel, localLabel, gameLabel }.Max(l => ImGui.CalcTextSize(l).X) + 12f * ImGuiHelpers.GlobalScale;

            DrawDetailRow(hashLabel, item.Hash, [], labelWidth, UiSharedService.ThemeTextAccent);
            DrawDetailRow(localLabel, item.FilePaths.FirstOrDefault() ?? string.Empty, item.FilePaths.Skip(1).ToList(), labelWidth, ImGuiColors.DalamudWhite);
            DrawDetailRow(gameLabel, item.GamePaths.FirstOrDefault() ?? string.Empty, item.GamePaths.Skip(1).ToList(), labelWidth, ImGuiColors.DalamudWhite);
        }, stretchWidth: true);
    }

    private static void DrawDetailRow(string label, string value, List<string> others, float labelWidth, Vector4 valueColor)
    {
        float rightEdge = ImGui.GetWindowContentRegionMax().X - UiSharedService.GetCardContentPaddingX();
        float rowStart = ImGui.GetCursorPosX();
        UiSharedService.ColorText(label, ImGuiColors.DalamudGrey);
        ImGui.SameLine(rowStart + labelWidth);
        UiSharedService.ColorTextWrapped(value, valueColor, rightEdge);
        if (others.Count == 0) return;

        ImGui.SetCursorPosX(rowStart + labelWidth);
        UiSharedService.ColorText(string.Format(CultureInfo.CurrentCulture, Loc.Get("DataAnalysis.AndMore"), others.Count), UiSharedService.ThemeTextAccent);
        UiSharedService.AttachToolTip(string.Join(Environment.NewLine, others));
    }

    // Tuiles de chiffres clés, réparties sur autant de colonnes que la largeur le permet.
    private static void DrawStatTiles(IReadOnlyList<StatTile> tiles)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float gap = 6f * scale;
        float pad = 8f * scale;
        float avail = ImGui.GetContentRegionAvail().X;
        int columns = Math.Clamp((int)((avail + gap) / (120f * scale + gap)), 1, tiles.Count);
        float tileWidth = (avail - gap * (columns - 1)) / columns;
        float lineHeight = ImGui.GetTextLineHeight();
        float valueSize = ImGui.GetFontSize() * 1.3f;
        float tileHeight = pad * 2f + lineHeight + 4f * scale + valueSize;
        float rounding = UiSharedService.RadiusCard * scale;
        var drawList = ImGui.GetWindowDrawList();

        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (i % columns != 0) ImGui.SameLine(0, gap);

            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(tileWidth, tileHeight);
            ImGui.InvisibleButton($"##stat-tile-{i}", new Vector2(tileWidth, tileHeight));
            bool hovered = ImGui.IsItemHovered();
            if (hovered && tile.Tooltip != null)
                UiSharedService.AttachToolTip(tile.Tooltip());

            drawList.AddRectFilled(min, max, ImGui.GetColorU32(UiSharedService.ThemeCardBg), rounding);
            drawList.AddRect(min, max, ImGui.GetColorU32(UiSharedService.ThemeCardBorder with { W = hovered ? 0.8f : 0.45f }), rounding, ImDrawFlags.None, scale);

            var iconText = tile.Icon.ToIconString();
            Vector2 iconSize;
            using (ImRaii.PushFont(UiBuilder.IconFont))
                iconSize = ImGui.CalcTextSize(iconText);
            drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), new Vector2(min.X + pad, min.Y + pad), ImGui.GetColorU32(UiSharedService.AccentColor), iconText);
            float labelX = min.X + pad + iconSize.X + 6f * scale;
            drawList.AddText(new Vector2(labelX, min.Y + pad), ImGui.GetColorU32(ImGuiColors.DalamudGrey),
                UiSharedService.TruncateToWidth(tile.Label, max.X - pad - labelX));
            drawList.AddText(ImGui.GetFont(), valueSize, new Vector2(min.X + pad, min.Y + pad + lineHeight + 4f * scale),
                ImGui.GetColorU32(tile.ValueColor), tile.Value);
        }

        ImGuiHelpers.ScaledDummy(4f);
    }

    // Puces sélectionnables qui passent à la ligne ; un point orange signale des fichiers non analysés.
    private static int DrawChips(string id, IReadOnlyList<Chip> chips, int selected)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float gap = 4f * scale;
        float padX = 10f * scale;
        float height = ImGui.GetFrameHeight();
        float startX = ImGui.GetCursorPosX();
        float maxX = startX + ImGui.GetContentRegionAvail().X;
        float lineX = startX;
        int clicked = -1;
        var drawList = ImGui.GetWindowDrawList();

        using var idScope = ImRaii.PushId(id);
        for (int i = 0; i < chips.Count; i++)
        {
            var chip = chips[i];
            bool isSelected = i == selected;
            var labelSize = ImGui.CalcTextSize(chip.Label);
            float countWidth = chip.Count == null ? 0f : ImGui.CalcTextSize(chip.Count).X + 6f * scale;
            float width = labelSize.X + countWidth + padX * 2f;

            if (i > 0 && lineX + gap + width <= maxX)
            {
                ImGui.SameLine(0, gap);
                lineX += gap + width;
            }
            else
            {
                lineX = startX + width;
            }

            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(width, height);
            if (ImGui.InvisibleButton($"##chip-{i}", new Vector2(width, height)))
                clicked = i;
            bool hovered = ImGui.IsItemHovered();
            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (hovered && chip.Warn && chip.Tooltip != null)
                UiSharedService.AttachToolTip(chip.Tooltip);

            var background = isSelected ? UiSharedService.AccentColor with { W = 0.85f }
                : hovered ? UiSharedService.ThemeFrameBgHovered
                : UiSharedService.ThemeFrameBg;
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(background), height / 2f);
            if (!isSelected)
                drawList.AddRect(min, max, ImGui.GetColorU32(UiSharedService.ThemeCardBorder with { W = 0.35f }), height / 2f, ImDrawFlags.None, scale);

            float textY = min.Y + (height - labelSize.Y) / 2f;
            drawList.AddText(new Vector2(min.X + padX, textY),
                ImGui.GetColorU32(isSelected ? UiSharedService.ThemeNavTextActive : UiSharedService.ThemeNavText), chip.Label);
            if (chip.Count != null)
            {
                drawList.AddText(new Vector2(min.X + padX + labelSize.X + 6f * scale, textY),
                    ImGui.GetColorU32(isSelected ? UiSharedService.ThemeNavTextActive with { W = 0.75f } : ImGuiColors.DalamudGrey3), chip.Count);
            }

            if (chip.Warn)
            {
                float radius = 3f * scale;
                drawList.AddCircleFilled(new Vector2(max.X - radius - 3f * scale, min.Y + radius + 3f * scale), radius, ImGui.GetColorU32(ImGuiColors.DalamudOrange));
            }
        }

        return clicked;
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

    private void DrawTable(IGrouping<string, CharacterAnalyzer.FileDataEntry> fileGroup, float height)
    {
        bool isTex = string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal);
        bool isMdl = string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal);
        var tableColumns = isTex ? (_enableBc7ConversionMode ? 7 : 6) : (isMdl ? 6 : 5);
        using var table = ImRaii.Table("Analysis", tableColumns,
            ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit
            | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX,
            new Vector2(0, height));
        if (!table.Success) return;
        ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.File"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.Filepaths"), ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.Gamepaths"), ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.FileSize"), ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.DownloadSize"), ImGuiTableColumnFlags.PreferSortDescending);
        if (isTex)
        {
            ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.Format"));
            if (_enableBc7ConversionMode) ImGui.TableSetupColumn(Loc.Get("DataAnalysis.Table.ConvertBc7"), ImGuiTableColumnFlags.NoSort);
        }
        if (isMdl)
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
                SortSelectedObject(fileGroup.Key, idx, dir == ImGuiSortDirection.Descending);
                _lastSortColumnIdx = idx;
                _lastSortDirection = dir;
                _lastSortedFileGroupKey = fileGroup.Key;
            }

            sortSpecs.SpecsDirty = false;
            _sortDirty = false;
        }

        using var headerColor = ImRaii.PushColor(ImGuiCol.Header, UiSharedService.AccentColor with { W = 0.35f });
        using var headerHoveredColor = ImRaii.PushColor(ImGuiCol.HeaderHovered, UiSharedService.ThemeFrameBgHovered);
        using var headerActiveColor = ImRaii.PushColor(ImGuiCol.HeaderActive, UiSharedService.AccentColor with { W = 0.5f });

        foreach (var item in fileGroup)
        {
            using var id = ImRaii.PushId(item.Hash);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            bool selected = string.Equals(item.Hash, _selectedHash, StringComparison.Ordinal);
            if (ImGui.Selectable(GetFileName(item), selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap))
                _selectedHash = item.Hash;
            UiSharedService.AttachToolTip(item.Hash);

            ImGui.TableNextColumn();
            UiSharedService.ColorText(item.FilePaths.Count.ToString(CultureInfo.CurrentCulture), ImGuiColors.DalamudGrey);
            ImGui.TableNextColumn();
            UiSharedService.ColorText(item.GamePaths.Count.ToString(CultureInfo.CurrentCulture), ImGuiColors.DalamudGrey);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(UiSharedService.ByteToString(item.OriginalSize));
            ImGui.TableNextColumn();
            UiSharedService.ColorText(UiSharedService.ByteToString(item.CompressedSize), item.IsComputed ? ImGuiColors.DalamudWhite : ImGuiColors.DalamudOrange);
            if (!item.IsComputed)
                UiSharedService.AttachToolTip(Loc.Get("DataAnalysis.TotalSizeTooltip"));

            if (isTex)
            {
                ImGui.TableNextColumn();
                UiSharedService.ColorText(item.Format.Value, ImGuiColors.DalamudGrey);
                if (_enableBc7ConversionMode)
                {
                    ImGui.TableNextColumn();
                    DrawBc7Checkbox(item);
                }
            }

            if (isMdl)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(UiSharedService.TrisToString(item.Triangles));
            }
        }
    }

    private void DrawBc7Checkbox(CharacterAnalyzer.FileDataEntry item)
    {
        if (item.Format.Value.StartsWith("BC", StringComparison.Ordinal) || item.Format.Value.StartsWith("DXT", StringComparison.Ordinal)
            || item.Format.Value.StartsWith("24864", StringComparison.Ordinal)) // BC4
        {
            return;
        }

        var filePath = item.FilePaths[0];
        bool toConvert = _texturesToConvert.ContainsKey(filePath);
        if (ImGui.Checkbox("###convert" + item.Hash, ref toConvert))
        {
            if (toConvert)
                _texturesToConvert[filePath] = item.FilePaths.Skip(1).ToArray();
            else
                _texturesToConvert.Remove(filePath);
        }
    }

    private void SortSelectedObject(string fileType, int column, bool descending)
    {
        var source = _cachedAnalysis![_selectedObjectTab];
        bool isTex = string.Equals(fileType, "tex", StringComparison.Ordinal);
        bool isMdl = string.Equals(fileType, "mdl", StringComparison.Ordinal);

        IEnumerable<KeyValuePair<string, CharacterAnalyzer.FileDataEntry>>? sorted = column switch
        {
            0 => Sort(source, k => GetFileName(k.Value), descending, StringComparer.OrdinalIgnoreCase),
            1 => Sort(source, k => k.Value.FilePaths.Count, descending),
            2 => Sort(source, k => k.Value.GamePaths.Count, descending),
            3 => Sort(source, k => k.Value.OriginalSize, descending),
            4 => Sort(source, k => k.Value.CompressedSize, descending),
            5 when isMdl => Sort(source, k => k.Value.Triangles, descending),
            5 when isTex => Sort(source, k => k.Value.Format.Value, descending, StringComparer.Ordinal),
            _ => null,
        };

        if (sorted != null)
            _cachedAnalysis[_selectedObjectTab] = sorted.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
    }

    private static IOrderedEnumerable<T> Sort<T, TKey>(IEnumerable<T> source, Func<T, TKey> key, bool descending, IComparer<TKey>? comparer = null)
        => descending ? source.OrderByDescending(key, comparer) : source.OrderBy(key, comparer);

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
