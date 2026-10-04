using System.Globalization;
using UmbraSync.Localization;

namespace UmbraSync.UI.Components;


public sealed record SettingsEntry(int Section, string LabelKey, string? DescriptionKey = null)
{
    public string Label => Loc.Get(LabelKey);
    public string Description => DescriptionKey is null ? string.Empty : Loc.Get(DescriptionKey);
}

public static class SettingsCatalog
{
    private const int General = 0;
    private const int Performance = 1;
    private const int Storage = 2;
    private const int Transfers = 3;
    private const int AutoDetect = 4;
    private const int Chat = 5;
    private const int Account = 6;
    private const int Privacy = 7;
    private const int Advanced = 8;

    public static readonly SettingsEntry[] Entries =
    [
        new(General, "Settings.General.Notes.Title"),
        new(General, "Overwrite existing notes"),
        new(General, "Open Notes Popup on user addition"),
        new(General, "Settings.General.Language.Title"),
        new(General, "Settings.General.Appearance"),
        new(General, "Settings.General.UiGlass", "Settings.General.UiGlass.Tooltip"),
        new(General, "Settings.General.ClearGlassInGpose"),
        new(General, "Settings.General.Display.Title"),
        new(General, "Enable Game Right Click Menu Entries"),
        new(General, "Display status and visible pair count in Server Info Bar"),
        new(General, "Show visible character's UID in tooltip"),
        new(General, "Prefer notes over player names in tooltip"),
        new(General, "Settings.Dtr.ColorCode"),
        new(General, "Coloriser les plaques de nom des paires"),
        new(General, "Show separate Visible group"),
        new(General, "Show separate Offline group"),
        new(General, "Show player names"),
        new(General, "Show Profiles on Hover"),
        new(General, "Popout profiles on the right"),
        new(General, "Hover Delay"),
        new(General, "Show profiles marked as NSFW"),
        new(General, "Show RP profiles marked as NSFW"),
        new(General, "Settings.General.Notifications.Title"),
        new(General, "Info Notification Display"),
        new(General, "Warning Notification Display"),
        new(General, "Error Notification Display"),
        new(General, "Disable optional plugin warnings"),
        new(General, "Share RP profile with other Ashfall plugins"),
        new(General, "Enable online notifications"),
        new(General, "Notify only for individual pairs"),
        new(General, "Notify only for named pairs"),

        new(Performance, "Settings.Performance.Global.Title"),
        new(Performance, "Display self-analysis warnings"),
        new(Performance, "Shrink downloaded textures"),
        new(Performance, "Delete original textures from disk"),
        new(Performance, "Settings.Performance.Bc7.Use"),
        new(Performance, "Settings.Performance.Limits.Title"),
        new(Performance, "Automatically block players exceeding thresholds"),
        new(Performance, "Display auto-block warnings for individual pairs"),
        new(Performance, "Display auto-block warnings for syncshell pairs"),
        new(Performance, "Auto Block VRAM threshold"),
        new(Performance, "Auto Block Triangle threshold"),
        new(Performance, "Settings.Transfer.Redraw.Title"),
        new(Performance, "Settings.Transfer.RedrawCoordination.Enable"),
        new(Performance, "Settings.Transfer.RedrawCoordination.MinInterval"),
        new(Performance, "Settings.Performance.Whitelist.Title"),
        new(Performance, "Whitelist all individual pairs"),
        new(Performance, "Settings.Performance.Blacklist.Title"),

        new(Storage, "Settings.Storage.Cache.Title"),
        new(Storage, "Settings.Storage.Compactor.Enable"),
        new(Storage, "Settings.Storage.Validation.Title"),
        new(Storage, "Settings.Storage.Clear.Title"),
        new(Storage, "CharaDataHub.Mcdf.Local.Title"),

        new(Transfers, "Settings.Transfer.Limits.Title"),
        new(Transfers, "Settings.Transfer.SpeedLimit"),
        new(Transfers, "Settings.Transfer.ParallelDownloads"),
        new(Transfers, "Settings.Transfer.DecompressionThreads"),
        new(Transfers, "Settings.Transfer.PairProcessing.MaxConcurrent"),
        new(Transfers, "Settings.Transfer.Precache.Title"),
        new(Transfers, "Settings.Transfer.Precache.Enable"),
        new(Transfers, "Settings.Transfer.Ui.Title"),
        new(Transfers, "Settings.Transfer.Ui.ShowWindow", "Settings.Transfer.Ui.ShowWindow.Help"),
        new(Transfers, "Settings.Transfer.Ui.EditPosition"),
        new(Transfers, "Settings.Transfer.Ui.ShowBars", "Settings.Transfer.Ui.ShowBars.Help"),
        new(Transfers, "Settings.Transfer.Ui.ShowText", "Settings.Transfer.Ui.ShowText.Help"),
        new(Transfers, "Settings.Transfer.Ui.BarWidth", "Settings.Transfer.Ui.BarWidth.Help"),
        new(Transfers, "Settings.Transfer.Ui.BarHeight", "Settings.Transfer.Ui.BarHeight.Help"),
        new(Transfers, "Settings.Transfer.Ui.Uploading", "Settings.Transfer.Ui.Uploading.Help"),
        new(Transfers, "Settings.Transfer.Current.Title"),

        new(AutoDetect, "Settings.AutoDetect.Discovery.Title"),
        new(AutoDetect, "Settings.AutoDetect.Enable"),
        new(AutoDetect, "Settings.AutoDetect.AllowInvites"),
        new(AutoDetect, "Settings.AutoDetect.UseInteractivePopup"),
        new(AutoDetect, "Settings.AutoDetect.AntiSpam.Header"),
        new(AutoDetect, "Settings.AutoDetect.Slots.Title"),
        new(AutoDetect, "Settings.AutoDetect.EnableSlotNotifications"),
        new(AutoDetect, "Settings.AutoDetect.EnableEstablishmentProximity"),

        new(Chat, "Settings.RpNamesHeader"),
        new(Chat, "Settings.RpNamesOnNameplates"),
        new(Chat, "Settings.RpNamesInChat"),
        new(Chat, "Settings.ChatIcon.Self"),
        new(Chat, "Settings.ChatIcon.Others"),
        new(Chat, "Settings.RpNameColors"),
        new(Chat, "Settings.RespectExternalNameColors"),
        new(Chat, "Settings.DisableInDuty"),
        new(Chat, "Settings.EmoteHighlight.Header"),
        new(Chat, "Settings.EmoteHighlight.Enable"),
        new(Chat, "Settings.EmoteHighlight.Asterisks"),
        new(Chat, "Settings.EmoteHighlight.AngleBrackets"),
        new(Chat, "Settings.EmoteHighlight.SquareBrackets"),
        new(Chat, "Settings.EmoteHighlight.Quotes"),
        new(Chat, "Settings.EmoteHighlight.Parentheses"),
        new(Chat, "Settings.EmoteHighlight.DoubleParentheses"),
        new(Chat, "Settings.EmoteHighlight.PreserveYellShoutColor"),
        new(Chat, "Settings.EmoteHighlight.CustomYellShoutColor"),
        new(Chat, "Settings.ChatTargetSound.Header"),
        new(Chat, "Settings.ChatTargetSound.MasterEnable"),
        new(Chat, "Settings.ChatTargetSound.Reverse"),
        new(Chat, "Settings.ChatTargetSound.Enable"),
        new(Chat, "Settings.Typing.BubbleHeader"),
        new(Chat, "Settings.Typing.EnableSystem"),
        new(Chat, "Settings.Typing.ShowOnNameplates"),
        new(Chat, "Settings.Typing.BubbleSize"),
        new(Chat, "Settings.Typing.LogPartyList"),
        new(Chat, "Settings.Typing.ShowSelf"),

        new(Privacy, "Settings.Privacy.ConsentStatus"),
        new(Privacy, "Settings.Privacy.Export.Header"),
        new(Privacy, "Settings.Privacy.Delete.Header"),
        new(Privacy, "Settings.Privacy.ServerDelete.Header"),
        new(Privacy, "Settings.Privacy.Revoke.Header"),
        new(Privacy, "Settings.Privacy.Rights.Header"),

        new(Advanced, "Settings.Advanced.PluginCompatibility"),
        new(Advanced, "Settings.Advanced.Options.Title"),
        new(Advanced, "Settings.Advanced.UmbraApi", "Settings.Advanced.UmbraApi.Help"),
        new(Advanced, "Settings.Advanced.LogEvents"),
        new(Advanced, "Settings.Advanced.OpenEventViewer"),
        new(Advanced, "Settings.Advanced.HoldCombat"),
        new(Advanced, "Settings.Advanced.SerialApply", "Settings.Advanced.SerialApply.Help"),
        new(Advanced, "Settings.Advanced.CharaData"),
        new(Advanced, "Settings.Advanced.CharaData.OpenGpose", "Settings.Advanced.CharaData.OpenGpose.Help"),
        new(Advanced, "Settings.Advanced.CharaData.DownloadOnConnect", "Settings.Advanced.CharaData.DownloadOnConnect.Help"),
        new(Advanced, "Settings.Advanced.CharaData.ShowHelp"),
        new(Advanced, "Settings.Advanced.CharaData.AbbreviateNames", "Settings.Advanced.CharaData.AbbreviateNames.Help"),
        new(Advanced, "Settings.Advanced.CharaData.LastExportFolder"),
        new(Advanced, "Settings.Advanced.CharaData.ClearExportFolder", "Settings.Advanced.CharaData.ClearExportFolder.Help"),
        new(Advanced, "Settings.Advanced.Experimental.Title"),
        new(Advanced, "Settings.Advanced.Experimental.SoftRedraw", "Settings.Advanced.Experimental.SoftRedraw.Help"),
        new(Advanced, "Settings.Advanced.Experimental.EventVisibility", "Settings.Advanced.Experimental.EventVisibility.Help"),
        new(Advanced, "Settings.Transfer.CollectionOverride.Title"),
        new(Advanced, "Settings.Transfer.CollectionOverride.Enable"),
        new(Advanced, "Settings.Transfer.AutoFetchMcdfOnConnect"),
        new(Advanced, "Settings.Advanced.Debug"),
        new(Advanced, "Settings.Advanced.Debug.CopyCharaData", "Settings.Advanced.Debug.CopyCharaData.Help"),
        new(Advanced, "Settings.Advanced.Debug.LogLevel"),
        new(Advanced, "Settings.Advanced.Debug.LogPerf", "Settings.Advanced.Debug.LogPerf.Help"),
        new(Advanced, "Settings.Advanced.Debug.LogPlayerNames", "Settings.Advanced.Debug.LogPlayerNames.Help"),
        new(Advanced, "Settings.Advanced.Debug.ExternalSyncReclaim", "Settings.Advanced.Debug.ExternalSyncReclaim.Help"),
        new(Advanced, "Settings.Advanced.Debug.PrintPerf"),
        new(Advanced, "Settings.Advanced.Debug.NetworkDiag.Enable", "Settings.Advanced.Debug.NetworkDiag.Enable.Help"),
        new(Advanced, "Settings.Advanced.Debug.NetworkDiag.OpenFolder", "Settings.Advanced.Debug.NetworkDiag.OpenFolder.Help"),
        new(Advanced, "Settings.Advanced.Debug.ActiveBlocks"),

        new(Account, "Settings.Section.Account.Title", "Settings.Section.Account.Desc"),
    ];

    public static List<SettingsEntry> Search(string query)
    {
        var results = new List<SettingsEntry>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        var needle = query.Trim();
        foreach (var entry in Entries)
        {
            if (Contains(entry.Label, needle) || Contains(entry.Description, needle))
                results.Add(entry);
        }
        return results;
    }

    public static int IndexOf(string haystack, string needle, out int matchLength)
    {
        matchLength = 0;
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return -1;

        return CultureInfo.CurrentCulture.CompareInfo.IndexOf(
            haystack, needle, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace, out matchLength);
    }

    private static bool Contains(string haystack, string needle) =>
        IndexOf(haystack, needle, out _) >= 0;
}
