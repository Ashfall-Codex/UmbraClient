using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.UI;

namespace UmbraSync.MareConfiguration.Configurations;

[Serializable]
public class MareConfig : IMareConfiguration
{
    public int ExpectedTOSVersion = 2;
    public int AcceptedTOSVersion { get; set; }
    public bool AcceptedAgreement { get; set; }
    public string CacheFolder { get; set; } = string.Empty;
    public bool DisableOptionalPluginWarnings { get; set; }
    public bool EnableDtrEntry { get; set; } = true;
    public int DtrStyle { get; set; }
    public bool ShowUidInDtrTooltip { get; set; } = true;
    public bool PreferNoteInDtrTooltip { get; set; }
    public bool UseColorsInDtr { get; set; } = true;
    public DtrEntry.Colors DtrColorsDefault { get; set; }
    public DtrEntry.Colors DtrColorsNotConnected { get; set; } = new(Glow: 0x0428FFu);
    public DtrEntry.Colors DtrColorsPairsInRange { get; set; } = new(Glow: 0x8D37C0u);
    public bool UseNameColors { get; set; }
    public DtrEntry.Colors NameColors { get; set; } = new(Foreground: 0x67EBF5u, Glow: 0x00303Cu);
    public DtrEntry.Colors BlockedNameColors { get; set; } = new(Foreground: 0x8AADC7, Glow: 0x000080u);
    public bool UseRpNamesOnNameplates { get; set; }
    public bool DisableNameplatesInDuty { get; set; } = true;
    public bool UseRpNamesInChat { get; set; }
    public bool DisableRpNamesInChatInDuty { get; set; } = true;
    public bool UseRpNameColors { get; set; } = true;
    public bool DisableRpNameColorsInDuty { get; set; } = true;
    public bool RespectExternalNameColors { get; set; }
    public bool EmoteHighlightEnabled { get; set; } = true;
    public ushort EmoteHighlightColorKey { get; set; } = 706;
    public bool EmoteHighlightAsterisks { get; set; } = true;
    public bool EmoteHighlightAngleBrackets { get; set; } = true;
    public bool EmoteHighlightSquareBrackets { get; set; } = true;
    public bool EmoteHighlightDoubleParentheses { get; set; } = true;
    public bool EmoteHighlightParenthesesGray { get; set; } = true;
    public ushort EmoteHighlightParenthesesColorKey { get; set; } = 4;
    public bool EmoteHighlightParenthesesItalic { get; set; } = true;
    public bool EmoteHighlightQuotes { get; set; } = false;
    public bool ChatTargetSoundMasterEnabled { get; set; }
    public bool ChatTargetSoundEnabled { get; set; } = false;
    public int ChatTargetSoundIndex { get; set; } = 1;
    public bool ChatTargetSoundReverseEnabled { get; set; }
    public bool ChatTargetSoundPairOverridesEnabled { get; set; }
    public Dictionary<string, int> PairTargetSoundOverrides { get; set; } = new(StringComparer.Ordinal);
    public bool ChatTargetSoundGroupOverridesEnabled { get; set; }
    public Dictionary<string, int> GroupTargetSoundOverrides { get; set; } = new(StringComparer.Ordinal);
    public bool EnableRightClickMenus { get; set; } = true;
    public NotificationLocation ErrorNotification { get; set; } = NotificationLocation.Both;
    public string ExportFolder { get; set; } = string.Empty;
    public NotificationLocation InfoNotification { get; set; } = NotificationLocation.Toast;
    public bool InitialScanComplete { get; set; }
    public LogLevel LogLevel { get; set; } = LogLevel.Information;
    public bool LogPerformance { get; set; }
    public bool LogEvents { get; set; } = true;
    public bool HoldCombatApplication { get; set; }
    public double MaxLocalCacheInGiB { get; set; } = 100;
    public bool OpenGposeImportOnGposeStart { get; set; }
    public bool OpenPopupOnAdd { get; set; } = true;
    public int ParallelDownloads { get; set; } = 10;
    public bool EnableDownloadQueue { get; set; } = true;
    public bool EnableParallelPairProcessing { get; set; } = true;
    public int MaxConcurrentPairApplications { get; set; } = 10;
    public int MaxDecompressionThreads { get; set; }
    public int DownloadSpeedLimitInBytes { get; set; }
    public DownloadSpeeds DownloadSpeedType { get; set; } = DownloadSpeeds.MBps;
    public float ProfileDelay { get; set; } = 1.5f;
    public bool ProfilePopoutRight { get; set; }
    public bool ProfilesAllowNsfw { get; set; }
    public bool ProfilesAllowRpNsfw { get; set; }
    public bool ProfilesShow { get; set; }
    public bool ShowCharacterNames { get; set; } = true;
    public bool ShowOfflineUsersSeparately { get; set; } = true;
    public bool ShowSyncshellOfflineUsersSeparately { get; set; } = true;
    public bool SerialApplication { get; set; }
    public bool ShowOnlineNotifications { get; set; }
    public bool ShowOnlineNotificationsOnlyForIndividualPairs { get; set; } = true;
    public bool ShowOnlineNotificationsOnlyForNamedPairs { get; set; }
    public bool ShowTransferBars { get; set; } = true;
    public bool ShowTransferWindow { get; set; }
    public bool ShowUploading { get; set; } = true;
    public bool ShowUploadingBigText { get; set; } = true;
    public bool ShowVisibleUsersSeparately { get; set; } = true;
    public string UiLanguage { get; set; } = "fr";
    public string LastChangelogVersionSeen { get; set; } = string.Empty;
    public bool DefaultDisableSounds { get; set; }
    public bool DefaultDisableAnimations { get; set; }
    public bool DefaultDisableVfx { get; set; }
    public bool DefaultDisableHousingMods { get; set; }
    public Dictionary<string, SyncOverrideEntry> PairSyncOverrides { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, SyncOverrideEntry> GroupSyncOverrides { get; set; } = new(StringComparer.Ordinal);
    public bool EnableAutoDetectDiscovery { get; set; } = true;
    public bool AllowAutoDetectPairRequests { get; set; } = true;
    public const int AutoDetectFixedMaxDistanceMeters = 50;
    public int AutoDetectMaxDistanceMeters { get; set; } = AutoDetectFixedMaxDistanceMeters;
    public int AutoDetectDeclineCooldownMinutes { get; set; } = 15;
    public List<string> AutoDetectBlockedUids { get; set; } = [];
    public bool UseInteractivePairRequestPopup { get; set; } = true;
    public int TimeSpanBetweenScansInSeconds { get; set; } = 30;
    public int TransferBarsHeight { get; set; } = 12;
    public bool TransferBarsShowText { get; set; } = true;
    public int TransferBarsWidth { get; set; } = 250;
    public bool UseAlternativeFileUpload { get; set; }
    public bool UseCompactor { get; set; }
    public bool EnablePenumbraPrecache { get; set; }
    public int PrecacheSpeedLimitInBytes { get; set; }
    public List<string> PrecacheExcludePatterns { get; set; } = new();
    public int Version { get; set; } = 1;
    public NotificationLocation WarningNotification { get; set; } = NotificationLocation.Both;
    public bool TypingIndicatorShowOnNameplates { get; set; } = true;
    public bool TypingIndicatorShowOnPartyList { get; set; } = true;
    public bool TypingIndicatorEnabled { get; set; } = true;
    public bool TypingIndicatorShowSelf { get; set; } = true;
    public TypingIndicatorBubbleSize TypingIndicatorBubbleSize { get; set; } = TypingIndicatorBubbleSize.Large;
    public bool TypingIndicatorOnlyWhenNameplateVisible { get; set; } = true;
    public TypingIndicatorNameplateStyle TypingIndicatorNameplateStyle { get; set; } = TypingIndicatorNameplateStyle.Side;
    public float TypingIndicatorNameplateOpacity { get; set; } = 1.0f;
    public float TypingIndicatorPartyOpacity { get; set; } = 0.9f;
    public bool UmbraAPI { get; set; } = true;
    public bool EnableSlotNotifications { get; set; } = true;
    public float DefaultSlotRadius { get; set; } = 10f;
    public const int ExpectedRgpdVersion = 1;
    public bool RgpdConsentGiven { get; set; }
    public DateTime? RgpdConsentDate { get; set; }
    public int AcceptedRgpdVersion { get; set; }
    public bool RgpdConsentDataCollection { get; set; }
    public bool RgpdConsentDataSharing { get; set; }
    public bool RgpdConsentThirdPartyPlugins { get; set; }

    [SuppressMessage("Major Code Smell", "S1133:Do not forget to remove this deprecated code someday", Justification = "Legacy config needed for migration")]
    [SuppressMessage("Major Code Smell", "S1123:Add an explanation", Justification = "Legacy config needed for migration")]
    [Obsolete]
    [JsonPropertyName("MareAPI")]
    public bool MareAPI { get => UmbraAPI; set => UmbraAPI = value; }
}