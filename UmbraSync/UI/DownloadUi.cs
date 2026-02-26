using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI.Files;
using UmbraSync.WebAPI.Files.Models;

namespace UmbraSync.UI;

public class DownloadUi : WindowMediatorSubscriberBase
{
    private readonly MareConfigService _configService;
    private readonly ConcurrentDictionary<GameObjectHandler, ConcurrentDictionary<string, FileDownloadStatus>> _currentDownloads = new();
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileUploadManager _fileTransferManager;
    private readonly UiSharedService _uiShared;
    private readonly ConcurrentDictionary<GameObjectHandler, bool> _uploadingPlayers = new();

    public DownloadUi(ILogger<DownloadUi> logger, DalamudUtilService dalamudUtilService, MareConfigService configService,
        FileUploadManager fileTransferManager, MareMediator mediator, UiSharedService uiShared, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, Loc.Get("DownloadUi.WindowTitle"), performanceCollectorService)
    {
        _dalamudUtilService = dalamudUtilService;
        _configService = configService;
        _fileTransferManager = fileTransferManager;
        _uiShared = uiShared;

        SizeConstraints = new WindowSizeConstraints()
        {
            MaximumSize = new Vector2(500, 90),
            MinimumSize = new Vector2(500, 90),
        };

        Flags |= ImGuiWindowFlags.NoMove;
        Flags |= ImGuiWindowFlags.NoBackground;
        Flags |= ImGuiWindowFlags.NoInputs;
        Flags |= ImGuiWindowFlags.NoNavFocus;
        Flags |= ImGuiWindowFlags.NoResize;
        Flags |= ImGuiWindowFlags.NoScrollbar;
        Flags |= ImGuiWindowFlags.NoTitleBar;
        Flags |= ImGuiWindowFlags.NoDecoration;
        Flags |= ImGuiWindowFlags.NoFocusOnAppearing;

        DisableWindowSounds = true;

        ForceMainWindow = true;

        IsOpen = true;

        Mediator.Subscribe<DownloadStartedMessage>(this, (msg) => _currentDownloads[msg.DownloadId] = msg.DownloadStatus);
        Mediator.Subscribe<DownloadFinishedMessage>(this, (msg) => _currentDownloads.TryRemove(msg.DownloadId, out _));
        Mediator.Subscribe<GposeStartMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<GposeEndMessage>(this, (_) => IsOpen = true);
        Mediator.Subscribe<PlayerUploadingMessage>(this, (msg) =>
        {
            if (msg.IsUploading)
            {
                _uploadingPlayers[msg.Handler] = true;
            }
            else
            {
                _uploadingPlayers.TryRemove(msg.Handler, out _);
            }
        });
    }

    protected override void DrawInternal()
    {
        if (_configService.Current.ShowTransferWindow)
        {
            try
            {
                if (_fileTransferManager.CurrentUploads.Count > 0)
                {
                    var currentUploads = _fileTransferManager.CurrentUploads.ToList();
                    var totalUploads = currentUploads.Count;

                    var doneUploads = currentUploads.Count(c => c.IsTransferred);
                    var totalUploaded = currentUploads.Sum(c => c.Transferred);
                    var totalToUpload = currentUploads.Sum(c => c.Total);

                    UiSharedService.DrawOutlinedFont($"▲", ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.SameLine();
                    var xDistance = ImGui.GetCursorPosX();
                    UiSharedService.DrawOutlinedFont(string.Format(CultureInfo.CurrentCulture, Loc.Get("DownloadUi.Uploads.Status"), doneUploads, totalUploads),
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.NewLine();
                    ImGui.SameLine(xDistance);
                    UiSharedService.DrawOutlinedFont(
                        $"{UiSharedService.ByteToString(totalUploaded, addSuffix: false)}/{UiSharedService.ByteToString(totalToUpload)}",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);

                    if (_currentDownloads.Count > 0) ImGui.Separator();
                }
            }
            catch
            {
                // ignore errors thrown from UI
            }

            try
            {
                foreach (var item in _currentDownloads.ToArray())
                {
                    var statusValues = item.Value?.Values.ToArray();
                    if (statusValues == null || statusValues.Length == 0) continue;
                    var dlSlot = statusValues.Count(c => c?.DownloadStatus == DownloadStatus.WaitingForSlot);
                    var dlQueue = statusValues.Count(c => c?.DownloadStatus == DownloadStatus.WaitingForQueue);
                    var dlProg = statusValues.Count(c => c?.DownloadStatus == DownloadStatus.Downloading);
                    var dlDecomp = statusValues.Count(c => c?.DownloadStatus == DownloadStatus.Decompressing);
                    var totalFiles = statusValues.Sum(c => c?.TotalFiles ?? 0);
                    var transferredFiles = statusValues.Sum(c => c?.TransferredFiles ?? 0);
                    var totalBytes = statusValues.Sum(c => c?.TotalBytes ?? 0);
                    var transferredBytes = statusValues.Sum(c => c?.TransferredBytes ?? 0);

                    UiSharedService.DrawOutlinedFont($"▼", ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.SameLine();
                    var xDistance = ImGui.GetCursorPosX();
                    UiSharedService.DrawOutlinedFont(
                        string.Format(CultureInfo.CurrentCulture, Loc.Get("DownloadUi.Downloads.Status"), item.Key.Name, dlSlot, dlQueue, dlProg, dlDecomp),
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.NewLine();
                    ImGui.SameLine(xDistance);
                    UiSharedService.DrawOutlinedFont(
                        $"{transferredFiles}/{totalFiles} ({UiSharedService.ByteToString(transferredBytes, addSuffix: false)}/{UiSharedService.ByteToString(totalBytes)})",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                }
            }
            catch
            {
                // ignore errors thrown from UI
            }
        }

        if (_configService.Current.ShowTransferBars)
        {
            const int transparency = 100;
            const int dlBarBorder = 3;

            foreach (var transfer in _currentDownloads.ToArray())
            {
                var screenPos = _dalamudUtilService.WorldToScreen(transfer.Key.GetGameObject());
                if (screenPos == Vector2.Zero) continue;

                var statusValues = transfer.Value?.Values.ToArray();
                if (statusValues == null || statusValues.Length == 0) continue;
                var totalBytes = statusValues.Sum(c => c?.TotalBytes ?? 0);
                var transferredBytes = statusValues.Sum(c => c?.TransferredBytes ?? 0);
                var displayTotalBytes = Math.Max(totalBytes, transferredBytes);

                var maxDlText = $"{UiSharedService.ByteToString(displayTotalBytes, addSuffix: false)}/{UiSharedService.ByteToString(displayTotalBytes)}";
                var textSize = _configService.Current.TransferBarsShowText ? ImGui.CalcTextSize(maxDlText) : new Vector2(10, 10);

                int dlBarHeight = _configService.Current.TransferBarsHeight > ((int)textSize.Y + 5) ? _configService.Current.TransferBarsHeight : (int)textSize.Y + 5;
                int dlBarWidth = _configService.Current.TransferBarsWidth > ((int)textSize.X + 10) ? _configService.Current.TransferBarsWidth : (int)textSize.X + 10;

                var dlBarStart = new Vector2(screenPos.X - dlBarWidth / 2f, screenPos.Y - dlBarHeight / 2f);
                var dlBarEnd = new Vector2(screenPos.X + dlBarWidth / 2f, screenPos.Y + dlBarHeight / 2f);
                var drawList = ImGui.GetBackgroundDrawList();

                // Make the bar pill-shaped by using large rounding (half the height)
                var barRounding = dlBarHeight / 2f;

                // Outer shadow
                drawList.AddRectFilled(
                    dlBarStart with { X = dlBarStart.X - dlBarBorder - 1, Y = dlBarStart.Y - dlBarBorder - 1 },
                    dlBarEnd with { X = dlBarEnd.X + dlBarBorder + 1, Y = dlBarEnd.Y + dlBarBorder + 1 },
                    UiSharedService.Color(0, 0, 0, transparency), barRounding + dlBarBorder + 1);

                // Border
                drawList.AddRectFilled(
                    dlBarStart with { X = dlBarStart.X - dlBarBorder, Y = dlBarStart.Y - dlBarBorder },
                    dlBarEnd with { X = dlBarEnd.X + dlBarBorder, Y = dlBarEnd.Y + dlBarBorder },
                    UiSharedService.Color(40, 30, 50, transparency), barRounding + dlBarBorder);

                // Track background
                drawList.AddRectFilled(
                    dlBarStart, dlBarEnd,
                    UiSharedService.Color(25, 22, 28, transparency), barRounding);
                var dlProgressPercent = displayTotalBytes == 0
                    ? 0
                    : Math.Min(transferredBytes / (double)displayTotalBytes, 1);
                var progressEndXRaw = dlBarStart.X + (float)(dlProgressPercent * dlBarWidth);
                static float SnapPx(float x) => MathF.Floor(x) + 0.5f; // align on pixel grid to avoid seams
                var progressEndX = SnapPx(MathF.Min(progressEndXRaw, dlBarEnd.X));
                var progressWidth = Math.Max(0.001f, progressEndX - dlBarStart.X);

                var fillColor = UiSharedService.Color(96, 74, 128, transparency); // violet foncé uni
                if (progressWidth > 0.5f)
                {
                    drawList.AddRectFilled(
                        dlBarStart,
                        new Vector2(progressEndX, dlBarEnd.Y),
                        fillColor,
                        barRounding,
                        ImDrawFlags.RoundCornersAll);
                }

                var glossTop = dlBarStart;
                var glossBottom = dlBarStart with { Y = dlBarStart.Y + dlBarHeight * 0.55f };
                var glossAlphaTop = 22;   // faint white
                var glossAlphaMid = 8;
                var glossAlphaBot = 0;
                try
                {
                    drawList.AddRectFilledMultiColor(
                        glossTop,
                        glossBottom,
                        UiSharedService.Color(255, 255, 255, (byte)glossAlphaTop),
                        UiSharedService.Color(255, 255, 255, (byte)glossAlphaTop),
                        UiSharedService.Color(255, 255, 255, (byte)glossAlphaBot),
                        UiSharedService.Color(255, 255, 255, (byte)glossAlphaBot)
                    );
                }
                catch
                {
                    drawList.AddRectFilled(glossTop, glossBottom, UiSharedService.Color(255, 255, 255, (byte)glossAlphaMid), barRounding);
                }

                var showProgressText = _configService.Current.TransferBarsShowText && dlProgressPercent < 1.0 - double.Epsilon;
                if (showProgressText)
                {
                    var downloadText = $"{UiSharedService.ByteToString(transferredBytes, addSuffix: false)}/{UiSharedService.ByteToString(displayTotalBytes)}";
                    UiSharedService.DrawOutlinedFont(drawList, downloadText,
                        screenPos with { X = screenPos.X - textSize.X / 2f - 1, Y = screenPos.Y - textSize.Y / 2f - 1 },
                        UiSharedService.Color(255, 255, 255, transparency),
                        UiSharedService.Color(0, 0, 0, transparency), 1);
                }

                if (dlProgressPercent >= 1.0 - double.Epsilon)
                {
                    var time = (float)ImGui.GetTime();
                    var center = new Vector2((dlBarStart.X + dlBarEnd.X) / 2f, (dlBarStart.Y + dlBarEnd.Y) / 2f);
                    var pulse = (MathF.Sin(time * 6.0f) * 0.5f + 0.5f); // 0..1
                    var baseR = Math.Min(4f, dlBarHeight * 0.22f);
                    var outerR = baseR + 2f + pulse * 2f;
                    var colInner = UiSharedService.Color(255, 245, 220, 170);
                    var colOuter = UiSharedService.Color(200, 160, 255, 75);
                    drawList.AddCircleFilled(center, baseR + pulse, colInner);
                    drawList.AddCircle(center, outerR, colOuter, 48, 2f);
                }
            }

            if (_configService.Current.ShowUploading)
            {
                foreach (var player in _uploadingPlayers.Keys.ToArray())
                {
                    var screenPos = _dalamudUtilService.WorldToScreen(player.GetGameObject());
                    if (screenPos == Vector2.Zero) continue;

                    try
                    {
                        using var _ = _uiShared.UidFont.Push();
                        var uploadText = Loc.Get("DownloadUi.UploadingLabel");

                        var textSize = ImGui.CalcTextSize(uploadText);

                        var drawList = ImGui.GetBackgroundDrawList();
                        UiSharedService.DrawOutlinedFont(drawList, uploadText,
                            screenPos with { X = screenPos.X - textSize.X / 2f - 1, Y = screenPos.Y - textSize.Y / 2f - 1 },
                            UiSharedService.Color(255, 255, 0, transparency),
                            UiSharedService.Color(0, 0, 0, transparency), 2);
                    }
                    catch
                    {
                        // ignore errors thrown on UI
                    }
                }
            }
        }
    }

    public override bool DrawConditions()
    {
        if (_uiShared.EditTrackerPosition) return true;
        if (!_configService.Current.ShowTransferWindow && !_configService.Current.ShowTransferBars) return false;
        if (_currentDownloads.Count == 0 && _fileTransferManager.CurrentUploads.Count == 0 && _uploadingPlayers.Count == 0) return false;
        if (!IsOpen) return false;
        return true;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        if (_uiShared.EditTrackerPosition)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
            Flags &= ~ImGuiWindowFlags.NoBackground;
            Flags &= ~ImGuiWindowFlags.NoInputs;
            Flags &= ~ImGuiWindowFlags.NoResize;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
            Flags |= ImGuiWindowFlags.NoBackground;
            Flags |= ImGuiWindowFlags.NoInputs;
            Flags |= ImGuiWindowFlags.NoResize;
        }

        var maxHeight = ImGui.GetTextLineHeight() * (_configService.Current.ParallelDownloads + 3);
        SizeConstraints = new()
        {
            MinimumSize = new Vector2(300, maxHeight),
            MaximumSize = new Vector2(300, maxHeight),
        };
    }
}