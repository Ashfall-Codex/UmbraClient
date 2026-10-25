using System.Globalization;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.UI.Components;
using Microsoft.Extensions.Logging;

namespace UmbraSync.UI;

public sealed class ChangelogUi : WindowMediatorSubscriberBase
{
    private static readonly string[] WatchedExternalPlugins =
    [
        "Penumbra",
        "Glamourer",
        "CustomizePlus",
        "SimpleHeels",
        "Honorific",
        "Moodles",
        "PetRenamer",
        "Brio",
    ];

    private readonly MareConfigService _configService;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Version _currentVersion;
    private readonly string _currentVersionLabel;
    private readonly IReadOnlyList<ChangelogEntry> _entries;
    private readonly bool _isUmbraSyncUpdated;
    private IReadOnlyList<(string Name, string Version)> _updatedExternalPlugins;
    private readonly Dictionary<string, string> _currentExternalSnapshot;

    private bool _hasAcknowledgedVersion;

    public ChangelogUi(ILogger<ChangelogUi> logger, MareConfigService configService,
        MareMediator mediator, PerformanceCollectorService performanceCollectorService,
        IDalamudPluginInterface pluginInterface)
        : base(logger, mediator, Loc.Get("ChangelogUi.WindowTitle") + "###UmbraSyncChangelog", performanceCollectorService)
    {
        _configService = configService;
        _pluginInterface = pluginInterface;
        _currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        _currentVersionLabel = _currentVersion.ToString();
        _entries = BuildEntries();
        _isUmbraSyncUpdated = !string.Equals(_configService.Current.LastChangelogVersionSeen, _currentVersionLabel, StringComparison.Ordinal);
        _hasAcknowledgedVersion = !_isUmbraSyncUpdated;

        (_updatedExternalPlugins, _currentExternalSnapshot) = ComputeExternalPluginState();

        RespectCloseHotkey = true;
        SizeConstraints = new()
        {
            MinimumSize = new(420, 320),
            MaximumSize = new(900, 1400)
        };
        Size = new Vector2(560, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        ShowCloseButton = true;

        if (_isUmbraSyncUpdated && _configService.Current.HasValidSetup() && _configService.Current.HasValidRgpdConsent())
        {
            IsOpen = true;
        }
        Mediator.Subscribe<OpenChangelogUiMessage>(this, (_) => IsOpen = true);

        if (_isUmbraSyncUpdated || _updatedExternalPlugins.Count > 0)
        {
            Mediator.Subscribe<DalamudLoginMessage>(this, _ => OnLoginPublishUpdateNotice());
        }
    }

    private bool _restartNoticePublished;

    private void OnLoginPublishUpdateNotice()
    {
        if (_restartNoticePublished) return;
        _restartNoticePublished = true;

        var title = Loc.Get("ChangelogUi.RestartNotice.Title");
        var parts = new List<string>();
        if (_isUmbraSyncUpdated)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, Loc.Get("ChangelogUi.RestartNotice.BodyPluginUpdated"), _currentVersionLabel));
        }
        if (_updatedExternalPlugins.Count > 0)
        {
            var pluginList = string.Join(", ", _updatedExternalPlugins.Select(p => $"{p.Name} {p.Version}"));
            parts.Add(string.Format(CultureInfo.CurrentCulture, Loc.Get("ChangelogUi.RestartNotice.BodyExternalUpdated"), pluginList));
        }
        if (parts.Count == 0) return;

        var body = string.Join("\n", parts);
        // DualNotificationMessage respecte les settings utilisateur (toast/chat/both) via InfoNotification.
        Mediator.Publish(new DualNotificationMessage(title, body, MareConfiguration.Models.NotificationType.Warning, TimeSpan.FromSeconds(10)));

        PersistExternalSnapshotIfNeeded();
    }

    private void PersistExternalSnapshotIfNeeded()
    {
        if (_currentExternalSnapshot.Count == 0) return;

        bool dirty = false;
        var stored = _configService.Current.LastSeenExternalPluginVersions;
        foreach (var kv in _currentExternalSnapshot)
        {
            if (!stored.TryGetValue(kv.Key, out var existing) || !string.Equals(existing, kv.Value, StringComparison.Ordinal))
            {
                stored[kv.Key] = kv.Value;
                dirty = true;
            }
        }
        if (dirty)
            _configService.Save();

        _updatedExternalPlugins = Array.Empty<(string Name, string Version)>();
    }

    private (IReadOnlyList<(string Name, string Version)> Updated, Dictionary<string, string> Snapshot) ComputeExternalPluginState()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        var updated = new List<(string Name, string Version)>();
        var stored = _configService.Current.LastSeenExternalPluginVersions;
        bool firstRun = stored.Count == 0;

        foreach (var name in WatchedExternalPlugins)
        {
            var state = PluginWatcherService.GetInitialPluginState(_pluginInterface, name);
            if (state == null || !state.IsLoaded) continue;

            var versionLabel = state.Version.ToString();
            snapshot[name] = versionLabel;

            if (firstRun) continue;
            if (!stored.TryGetValue(name, out var previous) || !string.Equals(previous, versionLabel, StringComparison.Ordinal))
            {
                updated.Add((name, versionLabel));
            }
        }

        return (updated, snapshot);
    }

    public override void OnClose()
    {
        MarkCurrentVersionAsReadIfNeeded();
        base.OnClose();
    }

    protected override void DrawInternal()
    {
        float availWidth = ImGui.GetContentRegionAvail().X;

        WizardControls.DrawStepHeader(FontAwesomeIcon.FileAlt, Loc.Get("ChangelogUi.HeaderTitle"),
            string.Format(CultureInfo.CurrentCulture, Loc.Get("ChangelogUi.LoadedVersion"), _currentVersionLabel), availWidth);

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth);
        ImGui.TextColored(UiSharedService.AccentColor, Loc.Get("ChangelogUi.Tagline"));
        ImGui.PopTextWrapPos();
        ImGuiHelpers.ScaledDummy(6f);

        // La liste laisse la place du bouton, sinon elle le pousse hors de la fenêtre.
        float footer = ImGui.GetFrameHeightWithSpacing() + 4f * ImGuiHelpers.GlobalScale;
        using (var list = ImRaii.Child("##changelog_entries", new Vector2(0f, -footer), false))
        {
            if (list)
                DrawEntries();
        }

        if (ImGui.Button(Loc.Get("ChangelogUi.MarkAsRead"), new Vector2(ImGui.GetContentRegionAvail().X, 0f)))
        {
            MarkCurrentVersionAsReadIfNeeded();
            IsOpen = false;
        }
    }

    // Seule la version courante est présentée : l'historique complet n'est plus affiché.
    private void DrawEntries()
    {
        if (_entries.Count > 0)
            DrawEntry(_entries[0]);
        UiSharedService.EndSectionCard();
    }

    // Une carte par version ; les lignes sont rangées par nature (nouveautés, améliorations, corrections…).
    private void DrawEntry(ChangelogEntry entry)
    {
        bool isCurrent = entry.Version == _currentVersion;
        UiSharedService.BeginSectionCard(entry.VersionLabel, FontAwesomeIcon.CodeBranch,
            trailing: isCurrent ? Loc.Get("ChangelogUi.CurrentTag") : null);

        foreach (var kind in SectionOrder)
        {
            var lines = entry.Lines.Where(l => DetectLineKind(l.Text) == kind).ToList();
            if (lines.Count == 0) continue;

            if (kind != LineKind.Generic)
            {
                var (color, icon) = GetLineStyle(kind);
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    ImGui.TextColored(color, icon.ToIconString());
                ImGui.SameLine();
                ImGui.TextColored(color, Loc.Get(SectionTitleKey(kind)));
            }

            foreach (var line in lines)
                DrawBullet(line, kind);

            ImGuiHelpers.ScaledDummy(4f);
        }
    }

    private static void DrawBullet(ChangelogLine line, LineKind kind)
    {
        float bulletWidth = 14f * ImGuiHelpers.GlobalScale;
        float indent = bulletWidth * (1 + Math.Max(0, line.IndentLevel));
        ImGui.Indent(indent);
        float x = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(x - bulletWidth);
        ImGui.TextColored(line.Color ?? ImGuiColors.DalamudGrey, "\u2022");
        ImGui.SameLine(x);
        ImGui.TextUnformatted(StripKindPrefix(line.Text, kind));
        ImGui.Unindent(indent);
        ImGuiHelpers.ScaledDummy(1f);
    }

    // Le titre de section dit déjà la nature : « Nouveauté : » en tête de ligne serait redondant.
    private static string StripKindPrefix(string text, LineKind kind)
    {
        if (kind == LineKind.Generic) return text;

        int colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon is > 0 and < 24 ? text[(colon + 1)..].TrimStart() : text;
    }

    private static readonly LineKind[] SectionOrder =
        [LineKind.New, LineKind.Improve, LineKind.Fix, LineKind.Other, LineKind.Generic];

    private static string SectionTitleKey(LineKind kind) => kind switch
    {
        LineKind.New => "ChangelogUi.Section.New",
        LineKind.Improve => "ChangelogUi.Section.Improve",
        LineKind.Fix => "ChangelogUi.Section.Fix",
        _ => "ChangelogUi.Section.Other",
    };

    private static readonly Vector4 ColorNew = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 ColorImprove = new(0.6f, 0.8f, 1f, 1f);
    private static readonly Vector4 ColorFix = new(1f, 0.75f, 0.3f, 1f);
    private static readonly Vector4 ColorOther = ImGuiColors.DalamudGrey;

    private enum LineKind { New, Improve, Fix, Other, Generic }

    private static LineKind DetectLineKind(string text)
    {
        if (text.StartsWith("Nouveaut", StringComparison.OrdinalIgnoreCase)) return LineKind.New;
        if (text.StartsWith("Am\u00e9lioration", StringComparison.OrdinalIgnoreCase)) return LineKind.Improve;
        if (text.StartsWith("Correct", StringComparison.OrdinalIgnoreCase)) return LineKind.Fix;
        if (text.StartsWith("Autre", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Mise \u00e0 jour", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Optimisation", StringComparison.OrdinalIgnoreCase)) return LineKind.Other;
        return LineKind.Generic;
    }

    private static (Vector4 color, FontAwesomeIcon icon) GetLineStyle(LineKind kind) => kind switch
    {
        LineKind.New => (ColorNew, FontAwesomeIcon.Star),
        LineKind.Improve => (ColorImprove, FontAwesomeIcon.ArrowUp),
        LineKind.Fix => (ColorFix, FontAwesomeIcon.Wrench),
        LineKind.Other => (ColorOther, FontAwesomeIcon.Cog),
        _ => (ImGuiColors.DalamudWhite, FontAwesomeIcon.CircleNotch),
    };

    private void MarkCurrentVersionAsReadIfNeeded()
    {
        bool dirty = false;

        if (!_hasAcknowledgedVersion)
        {
            _configService.Current.LastChangelogVersionSeen = _currentVersionLabel;
            _hasAcknowledgedVersion = true;
            dirty = true;
        }

        if (_currentExternalSnapshot.Count > 0)
        {
            var stored = _configService.Current.LastSeenExternalPluginVersions;
            foreach (var kv in _currentExternalSnapshot)
            {
                if (!stored.TryGetValue(kv.Key, out var existing) || !string.Equals(existing, kv.Value, StringComparison.Ordinal))
                {
                    stored[kv.Key] = kv.Value;
                    dirty = true;
                }
            }
        }

        if (dirty)
            _configService.Save();

        _updatedExternalPlugins = Array.Empty<(string Name, string Version)>();
    }

    private static IReadOnlyList<ChangelogEntry> BuildEntries()
    {
        return new List<ChangelogEntry>
        {
            new(new Version(3, 1, 2, 1025), "3.1.2.1025", new List<ChangelogLine>
            {
                new("Nouveauté : Fonds de fiche RP tirés des plaques d'aventurier du jeu, avec assombrissement, fondu et intensité réglables."),
                new("Nouveauté : Couleurs de la fiche au choix (titres, libellés, contenu), avec ou sans fond. Sans fond, la fiche s'affiche d'un seul bloc, avec des titres ornés."),
                new("Nouveauté : Jusqu'à 3 établissements sur une fiche. Demandez l'affiliation depuis l'onglet « Affiliation » d'un établissement ; le propriétaire accepte ou refuse depuis ses notifications ou l'onglet « Membres »."),
                new("Nouveauté : Social → Profils affiche « Mon personnage » et « Mes autres personnages », pour consulter les fiches de vos autres personnages."),
                new("Amélioration : Page « Apparence » de l'éditeur réorganisée (Carte de profil, Bannière, Fond de fiche, Couleurs de la fiche), et profil au survol aux couleurs de la fiche. L'éditeur des traits indique quand le plugin Moodles est nécessaire."),
                new("Amélioration : Un établissement s'affiche sur son personnage gérant et sur les personnages affiliés ; tant qu'aucun gérant n'est désigné, il reste sur tous les personnages du propriétaire."),
                new("Correction : La fiche d'un de vos autres personnages n'affiche plus le titre Honorific ni les Moodles du personnage joué."),
                new("Correction : La carte d'établissement s'affiche pour tous les établissements, et plus seulement pour une partie de l'annuaire."),
            }),
        };
    }

    private readonly record struct ChangelogEntry(Version Version, string VersionLabel, IReadOnlyList<ChangelogLine> Lines);

    private readonly record struct ChangelogLine(string Text, int IndentLevel = 0, System.Numerics.Vector4? Color = null);
}