using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using OtterGui.Text;
using System.Globalization;
using System.Numerics;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI;

public partial class CompactUi
{
    private bool _selfAnalysisOpen = false;
    private const long SelfAnalysisSizeWarningThreshold = 300L * 1024 * 1024;
    private const long SelfAnalysisTriangleWarningThreshold = 150_000;

    private void DrawDefaultSyncSettings()
    {
        ImGuiHelpers.ScaledDummy(3f);
        using (ImRaii.PushId("sync-defaults"))
        {
            var soundLabel = Loc.Get("CompactUi.SyncDefaults.AudioLabel");
            var animLabel = Loc.Get("CompactUi.SyncDefaults.AnimationLabel");
            var vfxLabel = Loc.Get("CompactUi.SyncDefaults.VfxLabel");
            var housingLabel = Loc.Get("CompactUi.SyncDefaults.HousingLabel");
            var soundSubject = Loc.Get("CompactUi.SyncDefaults.AudioSubject");
            var animSubject = Loc.Get("CompactUi.SyncDefaults.AnimationSubject");
            var vfxSubject = Loc.Get("CompactUi.SyncDefaults.VfxSubject");
            var housingSubject = Loc.Get("CompactUi.SyncDefaults.HousingSubject");

            bool soundsDisabled = _configService.Current.DefaultDisableSounds;
            bool animsDisabled = _configService.Current.DefaultDisableAnimations;
            bool vfxDisabled = _configService.Current.DefaultDisableVfx;
            bool housingDisabled = _configService.Current.DefaultDisableHousingMods;
            bool showNearby = _configService.Current.EnableAutoDetectDiscovery;
            int pendingInvites = _nearbyPending.Pending.Count;

            var soundIcon = soundsDisabled ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp;
            var animIcon = animsDisabled ? FontAwesomeIcon.WindowClose : FontAwesomeIcon.Running;
            var vfxIcon = vfxDisabled ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.Sun;
            var housingIcon = housingDisabled ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.Home;

            var extraPadding = new Vector2(6f, 4f) * ImGuiHelpers.GlobalScale;
            var originalPadding = ImGui.GetStyle().FramePadding;
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, originalPadding + extraPadding);

            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float audioWidth = _uiSharedService.GetIconTextButtonSize(soundIcon, soundLabel);
            float animWidth = _uiSharedService.GetIconTextButtonSize(animIcon, animLabel);
            float vfxWidth = _uiSharedService.GetIconTextButtonSize(vfxIcon, vfxLabel);
            float housingWidth = _uiSharedService.GetIconTextButtonSize(housingIcon, housingLabel);
            float totalWidth = audioWidth + animWidth + vfxWidth + housingWidth + spacing * 3f;
            float available = ImGui.GetContentRegionAvail().X;
            float startCursorX = ImGui.GetCursorPosX();
            if (totalWidth < available)
            {
                ImGui.SetCursorPosX(startCursorX + (available - totalWidth) / 2f);
            }

            DrawDefaultSyncButton(soundIcon, soundLabel, audioWidth, soundsDisabled,
                state =>
                {
                    _configService.Current.DefaultDisableSounds = state;
                    _configService.Save();
                    Mediator.Publish(new ApplyDefaultsToAllSyncsMessage(soundSubject, state));
                },
                () => DisableStateTooltip(soundSubject, _configService.Current.DefaultDisableSounds));

            DrawDefaultSyncButton(animIcon, animLabel, animWidth, animsDisabled,
                state =>
                {
                    _configService.Current.DefaultDisableAnimations = state;
                    _configService.Save();
                    Mediator.Publish(new ApplyDefaultsToAllSyncsMessage(animSubject, state));
                },
                () => DisableStateTooltip(animSubject, _configService.Current.DefaultDisableAnimations), spacing);

            DrawDefaultSyncButton(vfxIcon, vfxLabel, vfxWidth, vfxDisabled,
                state =>
                {
                    _configService.Current.DefaultDisableVfx = state;
                    _configService.Save();
                    Mediator.Publish(new ApplyDefaultsToAllSyncsMessage(vfxSubject, state));
                },
                () => DisableStateTooltip(vfxSubject, _configService.Current.DefaultDisableVfx), spacing);

            DrawDefaultSyncButton(housingIcon, housingLabel, housingWidth, housingDisabled,
                state =>
                {
                    _configService.Current.DefaultDisableHousingMods = state;
                    _configService.Save();
                    Mediator.Publish(new ApplyDefaultsToAllSyncsMessage(housingSubject, state));
                },
                () => DisableStateTooltip(housingSubject, _configService.Current.DefaultDisableHousingMods), spacing);

            ImGui.PopStyleVar();

            if (showNearby && pendingInvites > 0)
            {
                ImGuiHelpers.ScaledDummy(3f);
                UiSharedService.ColorTextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.SyncDefaults.AutoDetectPending"), pendingInvites), ImGuiColors.DalamudYellow);
            }

            DrawSelfAnalysisPreview();
        }
    }

    private void DrawSelfAnalysisPreview()
    {
        using (ImRaii.PushId("self-analysis"))
        {
            var headerSummary = _characterAnalyzer.CurrentSummary;
            bool highlightWarning = !headerSummary.IsEmpty
                                     && !headerSummary.HasUncomputedEntries
                                     && headerSummary.TotalCompressedSize >= SelfAnalysisSizeWarningThreshold;

            Vector4? cardBg = null;
            Vector4? cardBorder = null;
            if (highlightWarning)
            {
                var y = ImGuiColors.DalamudYellow;
                cardBg = new Vector4(y.X, y.Y, y.Z, 0.12f);
                cardBorder = new Vector4(y.X, y.Y, y.Z, 0.75f);
            }

            UiSharedService.DrawCard("self-analysis-card", () =>
            {
                bool arrowState = _selfAnalysisOpen;
                UiSharedService.DrawArrowToggle(ref arrowState, "##self-analysis-toggle");
                _selfAnalysisOpen = arrowState;

                ImGui.SameLine(0f, 6f * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Loc.Get("CompactUi.SelfAnalysis.Header"));
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    _selfAnalysisOpen = !_selfAnalysisOpen;
                }

                if (!_selfAnalysisOpen)
                {
                    return;
                }

                ImGuiHelpers.ScaledDummy(4f);

                var summary = _characterAnalyzer.CurrentSummary;
                bool isAnalyzing = _characterAnalyzer.IsAnalysisRunning;

                if (isAnalyzing)
                {
                    UiSharedService.ColorTextWrapped(
                        string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.SelfAnalysis.AnalyzingStatus"), _characterAnalyzer.CurrentFile, System.Math.Max(_characterAnalyzer.TotalFiles, 1)),
                        ImGuiColors.DalamudYellow);
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, Loc.Get("CompactUi.SelfAnalysis.CancelButton")))
                    {
                        _characterAnalyzer.CancelAnalyze();
                    }
                    UiSharedService.AttachToolTip(Loc.Get("CompactUi.SelfAnalysis.CancelTooltip"));
                }
                else
                {
                    bool recalculate = !summary.HasUncomputedEntries && !summary.IsEmpty;
                    var label = Loc.Get(recalculate ? "CompactUi.SelfAnalysis.RecalculateButton" : "CompactUi.SelfAnalysis.StartButton");
                    var icon = recalculate ? FontAwesomeIcon.Sync : FontAwesomeIcon.PlayCircle;
                    if (_uiSharedService.IconTextButton(icon, label))
                    {
                        _ = _characterAnalyzer.ComputeAnalysis(print: false, recalculate: recalculate);
                    }
                    UiSharedService.AttachToolTip(recalculate
                        ? Loc.Get("CompactUi.SelfAnalysis.RecalculateTooltip")
                        : Loc.Get("CompactUi.SelfAnalysis.StartTooltip"));
                }

                if (summary.IsEmpty && !isAnalyzing)
                {
                    UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.SelfAnalysis.NoData"),
                        ImGuiColors.DalamudGrey2);
                    return;
                }

                if (summary.HasUncomputedEntries && !isAnalyzing)
                {
                    UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.SelfAnalysis.UncomputedWarning"),
                        ImGuiColors.DalamudYellow);
                }

                ImGuiHelpers.ScaledDummy(3f);

                UiSharedService.DrawGrouped(() =>
                {
                    using var table = ImUtf8.Table("self-analysis-stats", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings);
                    if (table)
                    {
                        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthStretch, 0.55f);
                        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.45f);

                        DrawSelfAnalysisStatRow(Loc.Get("CompactUi.SelfAnalysis.Stat.Files"), summary.TotalFiles.ToString("N0", CultureInfo.CurrentCulture));

                        var compressedValue = UiSharedService.ByteToString(summary.TotalCompressedSize);
                        Vector4? compressedColor = null;
                        FontAwesomeIcon? compressedIcon = null;
                        Vector4? compressedIconColor = null;
                        string? compressedTooltip = null;
                        if (summary.HasUncomputedEntries)
                        {
                            compressedColor = ImGuiColors.DalamudYellow;
                            compressedTooltip = Loc.Get("CompactUi.SelfAnalysis.Tooltip.ComputeSizes");
                        }
                        else if (summary.TotalCompressedSize >= SelfAnalysisSizeWarningThreshold)
                        {
                            compressedColor = ImGuiColors.DalamudYellow;
                            compressedTooltip = Loc.Get("CompactUi.SelfAnalysis.Tooltip.SizeWarning");
                            compressedIcon = FontAwesomeIcon.ExclamationTriangle;
                            compressedIconColor = ImGuiColors.DalamudYellow;
                        }

                        DrawSelfAnalysisStatRow(Loc.Get("CompactUi.SelfAnalysis.Stat.CompressedSize"), compressedValue, compressedColor, compressedTooltip, compressedIcon, compressedIconColor);
                        DrawSelfAnalysisStatRow(Loc.Get("CompactUi.SelfAnalysis.Stat.ExtractedSize"), UiSharedService.ByteToString(summary.TotalOriginalSize));

                        Vector4? trianglesColor = null;
                        FontAwesomeIcon? trianglesIcon = null;
                        Vector4? trianglesIconColor = null;
                        string? trianglesTooltip = null;
                        if (summary.TotalTriangles >= SelfAnalysisTriangleWarningThreshold)
                        {
                            trianglesColor = ImGuiColors.DalamudYellow;
                            trianglesTooltip = Loc.Get("CompactUi.SelfAnalysis.Tooltip.TriangleWarning");
                            trianglesIcon = FontAwesomeIcon.ExclamationTriangle;
                            trianglesIconColor = ImGuiColors.DalamudYellow;
                        }
                        DrawSelfAnalysisStatRow(Loc.Get("CompactUi.SelfAnalysis.Stat.Triangles"), UiSharedService.TrisToString(summary.TotalTriangles), trianglesColor, trianglesTooltip, trianglesIcon, trianglesIconColor);
                    }
                }, rounding: 4f, expectedWidth: ImGui.GetContentRegionAvail().X, drawBorder: false);

                string lastAnalysisText;
                Vector4 lastAnalysisColor = ImGuiColors.DalamudGrey2;
                if (isAnalyzing)
                {
                    lastAnalysisText = Loc.Get("CompactUi.SelfAnalysis.LastAnalysis.InProgress");
                    lastAnalysisColor = ImGuiColors.DalamudYellow;
                }
                else if (_characterAnalyzer.LastCompletedAnalysis.HasValue)
                {
                    var localTime = _characterAnalyzer.LastCompletedAnalysis.Value.ToLocalTime();
                    lastAnalysisText = string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.SelfAnalysis.LastAnalysis.At"), localTime.ToString("g", CultureInfo.CurrentCulture));
                }
                else
                {
                    lastAnalysisText = Loc.Get("CompactUi.SelfAnalysis.LastAnalysis.Never");
                }

                ImGuiHelpers.ScaledDummy(2f);
                UiSharedService.ColorTextWrapped(lastAnalysisText, lastAnalysisColor);

                ImGuiHelpers.ScaledDummy(3f);

                if (_uiSharedService.IconTextButton(FontAwesomeIcon.PersonCircleQuestion, Loc.Get("CompactUi.SelfAnalysis.OpenDetailsButton")))
                {
                    Mediator.Publish(new UiToggleMessage(typeof(DataAnalysisUi)));
                }
            }, background: cardBg, border: cardBorder, stretchWidth: true);
        }
    }

    private static void DrawSelfAnalysisStatRow(string label, string value, Vector4? valueColor = null, string? tooltip = null, FontAwesomeIcon? icon = null, Vector4? iconColor = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);
        ImGui.TableNextColumn();
        if (icon.HasValue)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (iconColor.HasValue)
                {
                    using var iconColorPush = ImRaii.PushColor(ImGuiCol.Text, iconColor.Value);
                    ImGui.TextUnformatted(icon.Value.ToIconString());
                }
                else
                {
                    ImGui.TextUnformatted(icon.Value.ToIconString());
                }
            }
            ImGui.SameLine(0f, 4f);
        }

        if (valueColor.HasValue)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, valueColor.Value);
            ImGui.TextUnformatted(value);
        }
        else
        {
            ImGui.TextUnformatted(value);
        }

        if (!string.IsNullOrEmpty(tooltip))
        {
            UiSharedService.AttachToolTip(tooltip);
        }
    }

    private void DrawDefaultSyncButton(FontAwesomeIcon icon, string label, float width, bool currentState,
        Action<bool> onToggle, Func<string> tooltipProvider, float spacingOverride = -1f)
    {
        if (spacingOverride >= 0f)
        {
            ImGui.SameLine(0, spacingOverride);
        }

        var colorsPushed = 0;
        if (currentState)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.35f, 0.35f, 1f));
            colorsPushed++;
        }

        if (_uiSharedService.IconTextButton(icon, label, width))
        {
            var newState = !currentState;
            onToggle(newState);
        }

        if (colorsPushed > 0)
        {
            ImGui.PopStyleColor(colorsPushed);
        }

        UiSharedService.AttachToolTip(tooltipProvider());
    }

    private static string DisableStateTooltip(string context, bool disabled)
    {
        var state = Loc.Get(disabled ? "CompactUi.SyncDefaults.State.Disabled" : "CompactUi.SyncDefaults.State.Enabled");
        return string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.SyncDefaults.Tooltip"), context, state);
    }
}
