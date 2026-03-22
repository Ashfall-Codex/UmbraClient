using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using System.Numerics;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.WebAPI.SignalR.Utils;

namespace UmbraSync.UI;

public partial class CompactUi
{
    private void DrawAccentSeparator()
    {
        var hSepColor = UiSharedService.AccentColor with { W = 0.6f };
        var hSepDrawList = ImGui.GetWindowDrawList();
        var hSepCursor = ImGui.GetCursorScreenPos();
        var hSepStart = new Vector2(hSepCursor.X, hSepCursor.Y);
        var hSepEnd = new Vector2(hSepCursor.X + WindowContentWidth, hSepCursor.Y);
        hSepDrawList.AddLine(hSepStart, hSepEnd, ImGui.GetColorU32(hSepColor), 1f * ImGuiHelpers.GlobalScale);
        ImGuiHelpers.ScaledDummy(2f);
    }

    private void DrawTransfers()
    {
        var currentUploads = _fileTransferManager.CurrentUploads.ToList();

        if (currentUploads.Count > 0)
        {
            ImGui.AlignTextToFramePadding();
            _uiSharedService.IconText(FontAwesomeIcon.Upload);
            ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);

            var totalUploads = currentUploads.Count;

            var doneUploads = currentUploads.Count(c => c.IsTransferred);
            var totalUploaded = currentUploads.Sum(c => c.Transferred);
            var totalToUpload = currentUploads.Sum(c => c.Total);

            ImGui.TextUnformatted($"{doneUploads}/{totalUploads}");
            var uploadText = $"({UiSharedService.ByteToString(totalUploaded)}/{UiSharedService.ByteToString(totalToUpload)})";
            var textSize = ImGui.CalcTextSize(uploadText);
            ImGui.SameLine(WindowContentWidth - textSize.X);
            ImGui.TextUnformatted(uploadText);
        }

        var currentDownloads = _currentDownloads.SelectMany(d => d.Value.Values).ToList();

        if (currentDownloads.Count > 0)
        {
            ImGui.AlignTextToFramePadding();
            _uiSharedService.IconText(FontAwesomeIcon.Download);
            ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);

            var totalDownloads = currentDownloads.Sum(c => c.TotalFiles);
            var doneDownloads = currentDownloads.Sum(c => c.TransferredFiles);
            var totalDownloaded = currentDownloads.Sum(c => c.TransferredBytes);
            var totalToDownload = currentDownloads.Sum(c => c.TotalBytes);

            ImGui.TextUnformatted($"{doneDownloads}/{totalDownloads}");
            var downloadText =
                $"({UiSharedService.ByteToString(totalDownloaded)}/{UiSharedService.ByteToString(totalToDownload)})";
            var textSize = ImGui.CalcTextSize(downloadText);
            ImGui.SameLine(WindowContentWidth - textSize.X);
            ImGui.TextUnformatted(downloadText);
        }
        ImGuiHelpers.ScaledDummy(2);
    }

    private string GetServerError()
    {
        return _apiController.ServerState switch
        {
            ServerState.Connecting => Loc.Get("CompactUi.ServerErrors.AttemptingToConnect"),
            ServerState.Reconnecting => Loc.Get("CompactUi.ServerErrors.Reconnecting"),
            ServerState.Disconnected => Loc.Get("CompactUi.ServerErrors.Disconnected"),
            ServerState.Disconnecting => Loc.Get("CompactUi.ServerErrors.Disconnecting"),
            ServerState.Unauthorized => string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.Get("CompactUi.ServerErrors.Unauthorized"), _apiController.AuthFailureMessage),
            ServerState.Offline => Loc.Get("CompactUi.ServerErrors.Offline"),
            ServerState.VersionMisMatch =>
                Loc.Get("CompactUi.ServerErrors.VersionMismatch"),
            ServerState.RateLimited => Loc.Get("CompactUi.ServerErrors.RateLimited"),
            ServerState.Connected => string.Empty,
            ServerState.NoSecretKey => Loc.Get("CompactUi.ServerErrors.NoSecretKey"),
            ServerState.MultiChara => Loc.Get("CompactUi.ServerErrors.MultiChara"),
            _ => string.Empty
        };
    }

    private Vector4 GetUidColor()
    {
        return _apiController.ServerState switch
        {
            ServerState.Connecting => ImGuiColors.DalamudYellow,
            ServerState.Reconnecting => UiSharedService.AccentColor,
            ServerState.Connected => UiSharedService.AccentColor,
            ServerState.Disconnected => ImGuiColors.DalamudYellow,
            ServerState.Disconnecting => ImGuiColors.DalamudYellow,
            ServerState.Unauthorized => UiSharedService.AccentColor,
            ServerState.VersionMisMatch => ImGuiColors.DalamudRed,
            ServerState.Offline => UiSharedService.AccentColor,
            ServerState.RateLimited => ImGuiColors.DalamudYellow,
            ServerState.NoSecretKey => ImGuiColors.DalamudYellow,
            ServerState.MultiChara => ImGuiColors.DalamudYellow,
            _ => UiSharedService.AccentColor
        };
    }

    private string GetUidText()
    {
        return _apiController.ServerState switch
        {
            ServerState.Reconnecting => Loc.Get("CompactUi.UidStatus.Reconnecting"),
            ServerState.Connecting => Loc.Get("CompactUi.UidStatus.Connecting"),
            ServerState.Disconnected => Loc.Get("CompactUi.UidStatus.Disconnected"),
            ServerState.Disconnecting => Loc.Get("CompactUi.UidStatus.Disconnecting"),
            ServerState.Unauthorized => Loc.Get("CompactUi.UidStatus.Unauthorized"),
            ServerState.VersionMisMatch => Loc.Get("CompactUi.UidStatus.VersionMismatch"),
            ServerState.Offline => Loc.Get("CompactUi.UidStatus.Offline"),
            ServerState.RateLimited => Loc.Get("CompactUi.UidStatus.RateLimited"),
            ServerState.NoSecretKey => Loc.Get("CompactUi.UidStatus.NoSecretKey"),
            ServerState.MultiChara => Loc.Get("CompactUi.UidStatus.MultiChara"),
            ServerState.Connected => _apiController.DisplayName,
            _ => string.Empty
        };
    }
}
