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
using Microsoft.Extensions.Logging;

namespace UmbraSync.UI;

public sealed class ChangelogUi : WindowMediatorSubscriberBase
{
    private const int AlwaysExpandedEntryCount = 1;

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
    private readonly UiSharedService _uiShared;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Version _currentVersion;
    private readonly string _currentVersionLabel;
    private readonly IReadOnlyList<ChangelogEntry> _entries;
    private readonly bool _isUmbraSyncUpdated;
    private IReadOnlyList<(string Name, string Version)> _updatedExternalPlugins;
    private readonly Dictionary<string, string> _currentExternalSnapshot;

    private bool _historyOpen;
    private bool? _pendingHistoryOpenState;
    private bool _hasAcknowledgedVersion;

    public ChangelogUi(ILogger<ChangelogUi> logger, UiSharedService uiShared, MareConfigService configService,
        MareMediator mediator, PerformanceCollectorService performanceCollectorService,
        IDalamudPluginInterface pluginInterface)
        : base(logger, mediator, Loc.Get("ChangelogUi.WindowTitle"), performanceCollectorService)
    {
        _uiShared = uiShared;
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
            MinimumSize = new(520, 360),
            MaximumSize = new(900, 1200)
        };
        Flags |= ImGuiWindowFlags.NoResize;
        ShowCloseButton = true;

        if (_isUmbraSyncUpdated)
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
        DrawHeader();
        DrawActions();
        ImGui.Separator();
        DrawEntries();
    }

    private void DrawHeader()
    {
        using (_uiShared.UidFont.Push())
        {
            ImGui.TextUnformatted(Loc.Get("ChangelogUi.HeaderTitle"));
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, string.Format(CultureInfo.CurrentCulture, Loc.Get("ChangelogUi.LoadedVersion"), _currentVersionLabel));
        ImGui.Separator();
    }

    private void DrawActions()
    {
        var hasHistory = _entries.Count > AlwaysExpandedEntryCount;
        if (hasHistory)
        {
            var label = _historyOpen ? Loc.Get("ChangelogUi.HideAll") : Loc.Get("ChangelogUi.ShowAll");
            if (ImGui.Button(label))
                _pendingHistoryOpenState = !_historyOpen;
            ImGui.SameLine();
        }
        if (ImGui.Button(Loc.Get("ChangelogUi.MarkAsRead")))
        {
            MarkCurrentVersionAsReadIfNeeded();
            IsOpen = false;
        }
    }

    private void DrawEntries()
    {
        ImGuiHelpers.ScaledDummy(2f);

        for (int i = 0; i < AlwaysExpandedEntryCount && i < _entries.Count; i++)
            DrawEntry(_entries[i]);

        if (_entries.Count <= AlwaysExpandedEntryCount)
            return;

        ImGuiHelpers.ScaledDummy(4f);

        if (_pendingHistoryOpenState.HasValue)
        {
            ImGui.SetNextItemOpen(_pendingHistoryOpenState.Value, ImGuiCond.Always);
            _pendingHistoryOpenState = null;
        }

        var historyLabel = $"{Loc.Get("ChangelogUi.FullHistory")} ({_entries.Count - AlwaysExpandedEntryCount})";
        _historyOpen = ImGui.CollapsingHeader(historyLabel);
        if (_historyOpen)
        {
            for (int j = AlwaysExpandedEntryCount; j < _entries.Count; j++)
                DrawEntry(_entries[j]);
        }
    }

    private void DrawEntry(ChangelogEntry entry)
    {
        using (ImRaii.PushId(entry.VersionLabel))
        {
            UiSharedService.DrawCard($"changelog_{entry.VersionLabel}", () =>
            {
                DrawVersionPill(entry);
                ImGuiHelpers.ScaledDummy(4f);
                foreach (var line in entry.Lines)
                {
                    DrawLine(line);
                }
            }, stretchWidth: true);
            ImGuiHelpers.ScaledDummy(4f);
        }
    }

    private void DrawVersionPill(ChangelogEntry entry)
    {
        var isCurrent = entry.Version == _currentVersion;
        var color = isCurrent ? ImGuiColors.HealerGreen : ImGuiColors.DalamudWhite;
        var bg = color; bg.W = 0.18f;
        var border = color; border.W = 0.55f;

        var padX = 8f * ImGuiHelpers.GlobalScale;
        var padY = 2f * ImGuiHelpers.GlobalScale;
        var rounding = 4f * ImGuiHelpers.GlobalScale;
        var label = isCurrent ? $"{entry.VersionLabel}  \u2022  actuelle" : entry.VersionLabel;
        var textSize = ImGui.CalcTextSize(label);
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(textSize.X + padX * 2, textSize.Y + padY * 2);
        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(bg), rounding);
        dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(border), rounding);
        ImGui.SetCursorScreenPos(min + new Vector2(padX, padY));
        ImGui.TextColored(color, label);
        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y + ImGui.GetStyle().ItemSpacing.Y));
    }

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
            || text.StartsWith("Mise \u00e0 jour", StringComparison.OrdinalIgnoreCase)) return LineKind.Other;
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

    private static void DrawLine(ChangelogLine line)
    {
        using var indent = line.IndentLevel > 0 ? ImRaii.PushIndent(line.IndentLevel) : null;
        var kind = DetectLineKind(line.Text);
        var (autoColor, icon) = GetLineStyle(kind);
        var color = line.Color ?? autoColor;

        var iconSpacing = 6f * ImGuiHelpers.GlobalScale;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(color, icon.ToIconString());
        ImGui.SameLine(0, iconSpacing);
        var wrapPos = ImGui.GetWindowContentRegionMax().X - 4f * ImGuiHelpers.GlobalScale;
        ImGui.PushTextWrapPos(wrapPos);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(line.Text);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
        ImGui.Dummy(new Vector2(0, 2f * ImGuiHelpers.GlobalScale));
    }

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
            new(new Version(3, 0, 1, 8007), "3.0.1.8007", new List<ChangelogLine>
            {
                new("Nouveauté : Vous pouvez refuser les scénarios PNJ des autres joueurs, d'un coup avec le bouton « PNJ » de la fenêtre principale, ou joueur par joueur via le clic droit sur une paire (« Masquer ses scénarios PNJ »). L'effet est immédiat, sans avoir à ressortir du logement, et le choix reste sur votre client : la personne concernée n'en est pas informée."),
                new("Correction : Le premier PNJ d'une scène apparaissait avec votre apparence de base au lieu de la sienne, et le supprimer faisait retomber le PNJ suivant dans le même état. Le jeu réserve un emplacement d'acteur au mode photo, et le PNJ qui l'occupait y était confondu avec votre personnage : cet emplacement est désormais mis de côté. Le PNJ « sacrificiel » caché dans le décor n'a plus lieu d'être. Et si une apparence ne prenait quand même pas, elle est réappliquée automatiquement."),
                new("Amélioration : L'éditeur de scènes indique combien de PNJ UmbraSync affiche réellement dans la pièce, et propose de les retirer sur-le-champ ou de désactiver toutes les scènes du logement — sans rien supprimer dans les deux cas. Un compteur à zéro alors que des PNJ sont visibles signifie qu'ils viennent d'un autre plugin. Rappel utile : vos scènes activées replacent leurs PNJ à chaque entrée chez vous, couper le partage ne les enlève pas de votre propre logement."),
                new("Correction : Les PNJ d'une scène partagée disparaissaient définitivement dès qu'une manipulation dans l'éditeur relançait le placement des vôtres. Ils ne revenaient qu'après un changement de zone."),
                new("Amélioration : Votre titre personnalisé peut désormais garder l'apparence des titres du jeu. Une couleur unie était jusqu'ici toujours imposée, sans moyen de revenir au rendu de base : décochez « Couleur personnalisée » dans l'éditeur de profil."),
                new("Nouveauté : Vous pouvez confier la modification d'une de vos scènes PNJ publiées à d'autres joueurs. Ils peuvent la récupérer, la remanier et vous la renvoyer ; ils ne peuvent ni la supprimer, ni la déplacer, ni changer qui la voit. Si deux personnes modifient la même scène en même temps, la seconde est prévenue au lieu d'écraser le travail de la première."),
            }),
            new(new Version(3, 0, 0, 8006), "3.0.0.8006", new List<ChangelogLine>
            {
                new("Nouveauté : Un bandeau de progression s'affiche pendant la mise en place d'une scène PNJ."),
                new("Correction : Crash du jeu en logement avec les PNJ. Quand le jeu libérait les PNJ d'une scène, UmbraSync continuait de les suivre et le jeu se fermait brutalement."),
                new("Correction : Le premier PNJ d'une scène apparaissait sans ses mods, avec une apparence de base ou celle d'un personnage croisé ailleurs. Le jeu commençait à l'afficher avant que Penumbra ait fini de préparer ses mods ; il attend désormais que tout soit en place."),
                new("Correction : Les PNJ ne réapparaissaient pas quand on ressortait d'un logement puis qu'on y rentrait à nouveau."),
                new("Correction : SyncSlot. En entrant dans l'établissement de quelqu'un d'autre, vous restiez indéfiniment membre de la syncshell du précédent : le départ automatique ne se déclenchait jamais."),
                new("Amélioration : Les deux boutons d'import de PNJ deviennent « Importer SANS mods » et « Importer AVEC mods »."),
                new("Autre : Les scènes PNJ indiquent dans les logs le mode d'import utilisé et le nombre de mods réellement transmis, pour diagnostiquer plus vite une apparence incomplète."),
            }),
            new(new Version(3, 0, 0, 8005), "3.0.0.8005", new List<ChangelogLine>
            {
                new("Nouveauté : Ashfall Connect. Liez votre compte à la plateforme Ashfall Connect pour gérer, enrichir et certifier votre fiche RP. Liaison par code à usage unique depuis le plugin, niveaux de certification Bronze / Argent / Or via Discord et XIVAuth, et bouton « Profil enrichi » dans l'éditeur de profil."),
                new("Nouveauté : Housing PNJ. Peuplez votre intérieur de personnages : vous les habillez, les placez, les animez, et vos paires les voient apparaître en arrivant chez vous."),
                new("Nouveauté : Import de vos scénarios A Realm Repopulated existants : apparence, position, orientation et séquence d'actions de chaque PNJ sont récupérées. Placez-vous dans la pièce voulue avant d'importer."),
                new("Nouveauté : Compression BC7 des textures. Les textures des joueurs autour de vous sont compressées côté serveur : elles se téléchargent environ deux fois plus vite et pèsent nettement moins lourd sur votre carte graphique, donc moins de saccades et de crashs quand il y a du monde. Activée par défaut, désactivable dans Réglages → Performance, avec une liste de paires à conserver en qualité source."),
                new("Nouveauté : Les revêtements de murs, sols et plafonds moddés sont désormais inclus dans le partage housing. Jusqu'ici seul le mobilier était transmis. Ces éléments ne se rechargent pas à chaud : un message invite vos visiteurs à ressortir puis rentrer pour les afficher."),
                new("Nouveauté : La capacité maximale d'une syncshell peut aller jusqu'à 300 membres."),
                new("Amélioration : Redraw intelligent. Un simple changement de texture ou de matériau réapplique l'apparence sans redraw complet. Les changements de modèle, cheveux, visage ou queue déclenchent toujours un redraw complet. Activé par défaut, désactivable dans Réglages -> Transferts."),
                new("Amélioration : Détection de visibilité événementielle. L'apparition et la disparition des joueurs sont détectées à l'instant où elles se produisent, au lieu d'une vérification périodique : réaction plus immédiate et charge continue réduite. Repli automatique sur l'ancienne méthode si nécessaire, désactivable dans Réglages → Transferts."),
                new("Amélioration : Stabilité en logement bondé et en événement à 20+ personnes. Fini les apparences qui « sautent » — qui repassaient en version de base avant de se resynchroniser."),
                new("Amélioration : Le réglage « Téléchargements parallèles » est devenu un vrai plafond global partagé, réglable jusqu'à 20. Plus de téléchargements en surnombre qui saturaient la connexion sur les grosses scènes, et les emplacements ne sont plus monopolisés pendant la décompression."),
                new("Amélioration : Partage housing plus fiable. Un meuble dont tous les fichiers ne peuvent pas être transmis est écarté du partage au lieu d'arriver à moitié (fini les meubles noirs), vos mods sources restent actifs, le mod housing est construit à part puis installé d'un bloc, et les fichiers déjà présents sont réutilisés d'une visite à l'autre."),
                new("Correction : Personnages fantômes. Lors d'un changement de personnage, l'ancien pouvait rester affiché en ligne plusieurs minutes chez vos paires."),
                new("Correction : En cas de coupure vers le CDN, le téléchargement bascule réellement sur le serveur principal. Et quand un fichier reste introuvable, UmbraSync applique l'apparence disponible au lieu de réessayer indéfiniment."),
                new("Correction : SyncSlots dont on ne sortait jamais. Vous restiez considéré comme présent après avoir quitté le quartier résidentiel : l'encart « Zone SyncSlot » s'affichait partout et vous restiez membre de la syncshell indéfiniment. La sortie est maintenant détectée dès que vous quittez le quartier, y compris pour un établissement lié à une syncshell."),
                new("Autre : A Realm Repopulated ajouté à la liste des plugins compatibles détectés pour importer des scenarios PNJ."),
                new("Autre : Mise à jour API Penumbra & Glamourer."),
            }),
            new(new Version(2, 6, 0, 6026), "2.6.0.6026", new List<ChangelogLine>
            {
                new("Amélioration : Retrait du masquage 3D des bulles d'écriture derrière les décors qui était source de freezes et de crashs sur certains drivers (notamment AMD). Les bulles reviennent à leur affichage d'avant."),
                new("Amélioration : Nettoyage automatique au démarrage des fichiers temporaires laissés par un téléchargement interrompu."),
                new("Amélioration : Détection plus fiable des connexions coupées silencieusement : une reconnexion est désormais forcée automatiquement."),
                new("Amélioration : Téléchargements plus robustes sur connexions lentes ou instables (délai d'inactivité plus tolérant, tentatives mieux réparties)."),
                new("Amélioration : Envoi de votre apparence plus rapide (téléversements en parallèle au lieu d'un par un)."),
                new("Correction : Des fichiers de mods pouvaient se re-télécharger en boucle à cause d'une comparaison de hash trop stricte."),
                new("Correction : Après un crash du jeu, les mods d'autres joueurs restés « collés » sont nettoyés au redémarrage et l'apparence se ré-applique correctement."),
                new("Autre : Retrait de la library 'Ashfall.Engine'."),
                new("Autre : Mise à jour API Glamourer & Penumbra."),
            }),
            new(new Version(2, 5, 9, 6002), "2.5.9.6002", new List<ChangelogLine>
            {
                new("Nouveauté : Les propriétaires d'une syncshell peuvent désormais la renommer depuis le panneau d'administration (onglet Owner Settings)."),
                new("Nouveauté : Option « Coordonner les redraws Penumbra » dans Réglages → Transferts."),
                new("Correction : Crash violent à l'entrée de certains housings partagés. Les chemins reçus sont désormais filtrés pour ne plus pouvoir rediriger d'assets critiques."),
                new("Amélioration : Charge GPU étalée à l'application des paires. Réduit les crashs sur certains drivers, notamment AMD RX 9060 / 9070."),
                new("Amélioration : Réglages GPU automatiquement adaptés sur cartes AMD (occlusion en mode raycast, applications espacées) pour limiter les crashs de driver. Ajustable manuellement dans Réglages."),
                new("Amélioration : « Paires simultanées max » accepte désormais 1 (sérialisation totale) ; défaut abaissé de 10 à 3. Le toggle « Activer le traitement parallèle » est retiré (redondant)."),
                new("Autre : Refonte interne du pipeline d'application (téléchargement, Penumbra et personnalisation séparés)."),
            }),
            new(new Version(2, 5, 8, 5024), "2.5.8.5024", new List<ChangelogLine>
            {
                new("Amélioration : Rajout d'une option 'Re-télécharger les fichiers' (clic droit) pour forcer le rechargement d'une personne affichée avec des mods incomplets."),
                new("Correction : Dans certains cas, des fichiers de mods échouaient au téléchargement sans être réessayés, ce qui affichait certains personnages avec des mods incomplets."),
            }),
            new(new Version(2, 5, 8, 5023), "2.5.8.5023", new List<ChangelogLine>
            {
                new("Nouveauté : Options pour /crier et /hurler dans Réglages → Chat → Coloration des emotes. Vous pouvez désormais conserver la couleur native du canal au lieu de la couleur d'emote, ou définir une couleur dédiée."),
                new("Correction : Bulle d'écriture qui clignotait sur certaines inclinaisons de caméra côté Windows."),
                new("Autre : Mise à jour API Umbra pour futurs fonctionnalités."),
            }),
            new(new Version(2, 5, 7, 5019), "2.5.7.5019", new List<ChangelogLine>
            {
                new("Nouveauté : Notification une mise à jour d'UmbraSync ou d'un plugin lié, invitant à redémarrer le jeu en cas de souci."),
                new("Correction : Bouton « Rejoindre » des syncshells publiques ne réagissant que sur la première ligne."),
                new("Correction : Notification Toast doublé par-dessus la popup lors d'une demande de pair entrante."),
                new("Correction : Bulle d'écriture qui clignotait avec certains utilisateurs. La bulle se masque maintenant après la fin réelle de la frappe."),
                new("Correction : Aide du paramètre « Pairs simultanés max » alignée sur la limite réelle."),
                new("Autre : Mise à jour API Penumbra."),
                new("Autre : Suppression du code mort."),
            }),
            new(new Version(2, 5, 6, 5018), "2.5.6.5018", new List<ChangelogLine>
            {
                new("Amélioration : Connexion initiale beaucoup plus rapide. Vos paires et syncshells arrivent d'un seul coup au lieu d'apparaître progressivement, et le serveur fait jusqu'à deux tiers de requêtes en moins."),
                new("Amélioration : Moins de saturation réseau au démarrage. Le client n'envoie plus une rafale de requêtes par syncshell rejointe."),
                new("Amélioration : Affichage de la liste de paires plus stable au connect."),
                new("Autre : Refonte interne du protocole démarrage de connexion (côté client et serveur)."),
            }),
            new(new Version(2, 5, 5, 5017), "2.5.5.5017", new List<ChangelogLine>
            {
                new("Correction : Résolution des déconnexions à répétition. Tout est maintenant étalé pour démarrer plus en douceur."),
                new("Amélioration : Démarrage de la connexion plus fluide. Les chargements (rappels d'événements, profil RP, annuaire) sont étalés sur quelques secondes au lieu de partir tous d'un coup."),
                new("Amélioration : L'annuaire des établissements ne se charge plus tout seul au démarrage. Il se rafraîchit uniquement quand vous l'ouvrez."),
                new("Amélioration : Détection plus rapide des pertes de connexion (vérification toutes les 15 s au lieu de 30)."),
                new("Nouveauté : Option « Récupération automatique des partages MCDF à la connexion » dans Réglages → Transferts. Désactivée par défaut pour alléger le démarrage. Vous pouvez toujours rafraîchir manuellement depuis le Hub."),
                new("Nouveauté : Option « Activer le journal de diagnostic réseau » dans Réglages → Avancé → Débogage. À activer uniquement si vous avez des soucis de réseau."),
                new("Autre : Améliorations internes côté serveur pour mieux maintenir vivantes les connexions sensibles."),
            }),
            new(new Version(2, 5, 4, 5006), "2.5.4.5006", new List<ChangelogLine>
            {
                new("Amélioration : Le mode « Connexion lente » et l'auto-ajustement réseau sont désormais dissociés dans Réglages → Performance → Réseau.."),
                new("Amélioration : L'auto-ajustement gagne une période de grâce au démarrage du jeu et à la connexion d'un personnage."),
                new("Correction : Les évènements serveur arrivant pendant le rechargement initial des paires sont mis en file d'attente puis rejoués proprement."),
                new("Correction : Annulation propre des transferts. Couper le plugin ou tomber en déconnexion stoppe immédiatement les uploads, downloads et décompressions en cours, au lieu de les laisser tourner plusieurs minutes en arrière-plan."),
                new("Amélioration : Recherche des paires par UID désormais en O(1). Les fenêtres avec beaucoup de paires (annuaire, syncshells, fenêtre principale) répondent plus vite, particulièrement côté Mac et sur les configurations un peu justes."),
                new("Autre : Côté serveur — notifications de connexion parallélisées, suivi multi-connexions corrigé (SessionSec/Transport conservés), KeepAlive serveur ramené à 15 s, requêtes lecture seule en AsNoTracking. Connexion initiale et reconnexions sensiblement plus rapides."),
            }),
            new(new Version(2, 5, 3, 5002), "2.5.3.5002", new List<ChangelogLine>
            {
                new("Nouveauté : Auto-ajustement réseau. Le plugin détecte automatiquement les boucles de déconnexion/reconnexion courtes et bascule sur le mode connexion lente si nécessaire."),
                new("Amélioration : Réduction du coût CPU par frame de l'overlay typing et de la découverte nearby."),
                new("Correction : Ajout d'une scrollbar manquante dans l'annuaire."),
            }),
            new(new Version(2, 5, 2, 5001), "2.5.2.5001", new List<ChangelogLine>
            {
                new("Autre : Mise à jour API Glamourer."),
                new("Autre : Mise à jour API Penumbra."),
                new("Autre : Mise à jour de la library 'Pictomancy'."),
            }),
            new(new Version(2, 5, 2, 4030), "2.5.2.4030", new List<ChangelogLine>
            {
                new("Autre : Migration vers Dalamud API 15."),
                new("Autre : Migration vers Umbra API 4000."),
                new("Autre : Modernisation interne du plugin pour suivre les nouveautés de Dalamud 15 (système de fenêtres et énumérations)."),                                                   
                new("Autre : Suppression de la dépendance OtterGui."),   
            }),
            new(new Version(2, 5, 1, 4028), "2.5.1.4028", new List<ChangelogLine>
            {
                new("Nouveauté : Mode « Connexion lente » (Réglages → Performance → Réseau). Bascule la connexion sur un transport plus tolérant et étale les requêtes initiales. Conçu pour les utilisateurs avec un débit lent, limité, instable ou dégradé. Désactivé par défaut."),
                new("Nouveauté : Option « Étaler le chargement initial après connexion » (sous-réglage de Performance → Réseau). Permet d'étaler les requêtes de chargement initial (pairs, syncshells, membres) au lieu de les lancer toutes en parallèle."),
                new("Amélioration : Statut d'appairage explicite (unilatéral / bidirectionnel) propagé en temps réel. Quand quelqu'un vous supprime de ses pairs, votre statut passe immédiatement à unilatéral sans avoir à vous reconnecter."),
                new("Amélioration : Permissions persistante sur les pairs : vos préférences personnalisées (animations, sons, VFX, pause) sont désormais conservées même si vous supprimez puis re-ajoutez la pair."),
                new("Amélioration : Le plugin annule désormais les demandes d'appairage en double si vous êtes déjà appairé(e) directement avec l'utilisateur, plutôt que de spammer le serveur."),
                new("Correction : Bug « already paired » qui empêchait de re-créer un appairage cassé après une déco-reco. Le serveur resynchronise désormais automatiquement votre liste au lieu de bloquer l'opération."),
                new("Correction : Diverses corrections d'incohérences sur l'affichage des pairs unilatéraux dans l'interface."),
                new("Autre : Refonte interne du modèle de permissions côté serveur."),
                new("Autre : Mise à jour des dépendances .NET / SignalR / EF Core vers 10.0.6."),
            }),
            new(new Version(2, 5, 0, 4025), "2.5.0.4025", new List<ChangelogLine>
            {
                new("Nouveauté : Possibilité de définir une icône qui s'affiche dans le tchat avant le nom RP. Configurable dans l'éditeur de profil RP."),
                new("Nouveauté : Niveau de RP affiché sur le profil et dans les annonces de RP libre. Permet aux joueurs de signaler leur expérience RP et leur disponibilité pour aider les autres."),
                new("Amélioration : Possibilité d'éditer un trait Moodles sans devoir le supprimer."),
                new("Amélioration : Possibilité d'ajouter une couleur de texte pour la description d'un trait Moodles."),
                new("Amélioration : Nouvelles options de récurrence pour les événements d'établissement (toutes les 2 semaines, tous les 2 mois, tous les 3 mois, tous les ans)."),
                new("Amélioration : Les listes de l'annuaire (Parcourir, Lieux favoris, Mes lieux) sont désormais triées par ordre alphabétique."),
                new("Amélioration : Stabilité de connexion accrue sur les réseaux instables (ping WebSocket bas niveau toutes les 10 s, ping serveur toutes les 15 s) pour éviter les déconnexions liées au NAT/firewall."),
                new("Amélioration : Notifications des événements d'établissements favoris désormais envoyées à l'ouverture (et plus avec un délai aléatoire). Gère les événements récurrents (chaque occurrence est notifiée) et les événements en cours à la connexion."),
                new("Amélioration : Logos des établissements agrandis partout (annuaire, événements à venir, profil), avec placeholder coloré quand aucun logo n'est défini."),
                new("Amélioration : Refonte visuelle des cartes de l'annuaire (catégorie en pill colorée, descriptions sur une seule ligne, troncature propre, hover discret)."),
                new("Correction : Correction de l'injection de la couleur sur le trait Moodles."),
                new("Correction : Résolution de la non-persistance aléatoire des traits Moodles après une déco-reco du personnage ou redémarrage serveur."),
                new("Correction : Résolution de la non-persistance du titre Honorific après une déco-reco du personnage ou redémarrage serveur."),
                new("Correction : Comportement Auto-block amélioré et réellement désactivable."),
                new("Correction : La bannière peut désormais être ajoutée dès la création d'un établissement (le sélecteur de fichier ne s'ouvrait pas)."),
                new("Correction : Le numéro d'appartement est désormais affiché dans la fiche d'un établissement (auparavant seul le secteur était visible)."),
                new("Correction : La bulle d'écriture disparaît avec la nameplate quand le joueur est trop loin."),
                new("Correction : L'onglet « Images » d'un établissement n'est plus visible pour les visiteurs (réservé au propriétaire)."),
                new("Autre : Ajout de la library 'Pictomancy'."),
                new("Autre : Ajout de la library 'Ashfall.Engine'."),
                new("Autre : Mise à jour API Penumbra & Glamourer."),
            }),  
            new(new Version(2, 4, 2, 4009), "2.4.2.4009", new List<ChangelogLine>
            {
                new("Correction : Sur les connexions instables ou à faible débit, le plugin pouvait rester bloqué dans une boucle de déconnexion/reconnexion. Le cycle de récupération est désormais plus robuste."),
            }),  
            new(new Version(2, 4, 1, 4004), "2.4.1.4004", new List<ChangelogLine>
            {
                new("Amélioration : Possibilité de s'annoncer disponible pour du RP sauvage."),
                new("Amélioration : Stockage MCDF illimité, fichiers Live maintenus à 30."),
                new("Amélioration : Le stockage MCDF local prend désormais en compte les sous-dossiers."),
                new("Amélioration : Amélioration de l'interface Création de données."),
                new("Correction : Quand le MCDF était volumineux, le serveur coupait la connexion."),
                new("Correction : Divers ajustements interface."),
                new("Autre : Activation du support A Quest Reborn."),

            }),  
            new(new Version(2, 4, 0, 3031), "2.4.0.3031", new List<ChangelogLine>
            {
                new("Nouveauté : Système d'annuaire d'établissement, vous pouvez désormais lister votre établissement."),
                new("Nouveauté : Un système de cooldown est désormais disponible quand vous refusez une invitation AutoDetect."),
                new("Amélioration : Réécriture de la logique des Pause Syncshell / Individuelle."),
                new("Amélioration : Il est désormais possible de gérer le sous-titre honorific dans la fiche de personnage."),
                new("Amélioration : Réécriture du Hub de Données."),
                new("Amélioration : Indication sur l'overlay & amélioration notification quand on est dans une zone SyncSlot."),
                new("Correction : Le système de SyncSlot lançait le timer dans le Housing."),
                new("Correction : Limitation de l'ID personnalisé à 15 caractères max."),
                new("Correction : Les notifications pouvaient persister malgré la désactivation dans les réglages."),
                new("Correction : Optimisation du cache et suppression plus rapide du cache de housing moddé"),
                new("Correction : Mise à jour du lien discord dans À propos"),
                new("Amélioration : Diverses améliorations de l'Interface."),
                new("Amélioration : Optimisation du code."),
                new("Mise à jour API Penumbra & Glamourer."),
                new("Mise à jour des dépendances du code source."),
            }),   
            new(new Version(2, 3, 4, 3012), "2.3.4.3012", new List<ChangelogLine>
            {
                new("Correction : Dans certains cas, l'application des mods se faisaient avant la fin du téléchargement."),
                new("Correction : Des traits de personnage (Via Moodles) pouvaient s'appliquer pour des alts"),

            }),   
            new(new Version(2, 3, 3, 3010), "2.3.3.3010", new List<ChangelogLine>
            {
                new("Divers ajustement / derniers détails pour la mécanique de téléchargement et mises à jours des API Penumbra & Glamourer."),

            }),   
            new(new Version(2, 3, 3, 3009), "2.3.3.3009", new List<ChangelogLine>
            {
                new("CORRECTION CRITIQUE : Réécriture client / serveur du chargement et téléchargement de mods."),
                new("CORRECTION : Meilleure intégration avec ChatAlerts")

            }),    
            new(new Version(2, 3, 2, 8003), "2.3.2.8003", new List<ChangelogLine>
            {
                new("Nouveauté : Il est désormais possible de définir un son lorsque quelqu'un vous écrit et que vous le cibler / votre cible parle."),
                new("Nouveauté : Il est désormais possible de définir une collection spécifique Penumbra par Syncshell."),
                new("Amélioration : Les invitations de jumelage (reçues et envoyées) expirent automatiquement au bout de 10 minutes."),
                new("Amélioration : Les invitations expirent immédiatement si l'autre partie se déconnecte, avec notification de déconnexion."),
                new("Amélioration : Horodatage « Reçue il y a … » sur les invitations entrantes."),
                new("Amélioration : Coloration des textes entre guillemets \"...\" en blanc pour distinguer les dialogues dans une émote."),
                new("Amélioration : Migration vers Brio.API pour l'interconnexion avec Brio et ses fonctionnalités."),
                new("Amélioration : Possibilité de trier la liste des membres syncshell par type de pair où par ordre alphabétique."),
                new("Amélioration : Révision du système de cache de profil RP & Traits Moodles."),
                new("Amélioration : Réécriture du système d'envoi / réception d'invitation interactive."),
                new("Amélioration : Mise à jour API Umbra en version ."),
                new("Amélioration : Divers ajustement graphique & nettoyage du code."),

            }),
            new(new Version(2, 3, 1, 3002), "2.3.1.3002", new List<ChangelogLine>
            {   
                new("Amélioration : Réécriture complète du partage de meubles housing moddé avec une meilleure logique."),
                new("Amélioration : Un message indique clairement quand un utilisateur n'a pas configuré son profil RP."),
                new("Amélioration : Le nom personnalisé s'affiche correctement quand vous avez configuré groupe d'ami."),
                new("Amélioration : Le nom personnalisé s'affiche correctement lors d'une émote classique."),
                new("Amélioration : Détection et adaptation de UmbraSync lorsque ChatAlerts est detecté."),
                new("Amélioration : Il vous est désormais possible de choisir la priorité des couleurs entre UmbraSync et SimpleTweak."),
                new("Correctif : Dans certain cas, la barre de chargement pouvait entrainer une erreur."),
                new("Correctif : Si les mods sont introuvable sur le CDN, le plugin dévie plus rapidement sur la source principal."),
                new("Correctif : Si le CDN est injoignable, le plugin dévie plus rapidement sur la source principale."),
                new("Correctif : Dépendant de la couleur choisie, elle pouvait mal s'afficher dans le chat."),
                new("Correctif : Dans certain cas, la couleur du nom du chat ne correspondait plus avec la couleur du profil RP."),
                new("Correctif : Dans certain cas, les traits Moodles se supprimaient au redemarrage du jeu."),
                new("Correctif : Correction de la détection des meubles moddé lors du scan."),

            }),
            new(new Version(2, 3, 0, 0), "2.3.0.0", new List<ChangelogLine>
            {
                new("Nouveauté : Conforme avec le Règlement Général sur la Protection des Données (RGPD)."),
                new("Nouveauté : Support du plugin A Quest Reborn pour la synchronisation des quêtes personnalisées."),
                new("Nouveauté : Possibilité d'ajouter une syncshell comme favorite."),
                new("Nouveauté : Possibilité de partager un fichier de housing avec meubles moddé."),
                new("Nouveauté : Possibilité d'ajouter des éléments de profil personnalisés."),
                new("Nouveauté : Possibilité de colorer le nom, prénom et titre RP de son personnage."),
                new("Nouveauté : Refonte du hub de donnée avec création de liste de profil RP en cache."),
                new("Nouveauté : Support des doubles parenthèse comme contenu HRP."),
                new("Correctif : Création d'un cache Moodles propre à UmbraSync afin de sauvegarder les traits RP en cas de panne."),
                new("Correctif : Le profil RP pouvait disparaitre après un changement de Monde où application d'ID personnalisé."),
                new("Autres : Suppression de la fonctionnalité des Pings."),
                new("Autres : Nettoyage du code source."),
                new("Retrouvez la note de version complète sur Discord."),
            }),
            new(new Version(2, 2, 2, 1), "2.2.2.1", new List<ChangelogLine>
            {
                new("Nouveauté : Support du format BBCode dans les informations du Profil RP."),
                new("Correctif : Dans certains cas, la bulle d'écriture ne s'affichait plus."),
                new("Correctif : Meilleure gestion du timeout / perte de connexion lors d'un téléchargement de mod."),
            }),
            new(new Version(2, 2, 2, 0), "2.2.2.0", new List<ChangelogLine>
            {
                new("Nouveauté : Intégration de Moodles dans les profils RP."),
                new("Nouveauté : Prise en charge du plugin Chat Proximity pour adapter la colorisation des émotes dans le chat en fonction de la distance."),
                new("Amélioration : Modification de divers aspect de l'interface."),
                new("Amélioration : La taille max des images pour les profils passent à 5Mo."),
                new("Amélioration : Ajout de catégorie et des informations Moodles dans le profil RP."),
                new("Correctif : La notification de connexion n'apparait plus au démarrage."),
                new("Correctif : Dans certains cas, la bulle d'écriture ne s'affichait plus."),
                new("Correctif : Dans certains cas, le téléchargement de mod s'annulait."),
                new("Correctif : La suppression d'un marqueur n'était pas effectif pour les autres personnes."),
                new("Mise à jour SDK Dalamud."),
            }),
            new(new Version(2, 2, 1, 0), "2.2.1.0", new List<ChangelogLine>
            {
                new("Nouvelle fonctionnalité : Possibilité de personnaliser l'identité de son personnage via le profil RP."),
                new("Nouvelle fonctionnalité : Possibilité de colorer les émotes dans le chat ( Entre <>, * et [] )."),
                new("Nouvelle fonctionnalité : Les messages HRP entre parenthèse sont affiché grisée et en italique."),
                new("Amélioration : Ajout d'un délais de 2 secondes avant de passer en mode ping."),
                new("Correctif : Dans certains cas, le profil RôlePlay ne s'affichait pas."),
                new("Correctif : Dans certains cas, le téléchargement de mod pouvait se bloquer."),
                new("Correctif : La pause pouvait provoquer une erreur arrêtant la synchronisation de la cible."),
                new("Optimisation diverses du code."),
            }),
            new(new Version(2, 2, 0, 0), "2.2.0.0", new List<ChangelogLine>
            {
                new("Restructuration interface et architecture des Syncshell"),
                new("Implémentation système de ping"),
                new("Divers correctifs"),
                new("Plus d'informations sur le Discord"),
            }),
            new(new Version(2, 1, 3, 1), "2.1.3.1", new List<ChangelogLine>
            {
                new("Résolution d'un problème critique pouvant faire un téléchargement en boucle"),
            }),
        };
    }

    private readonly record struct ChangelogEntry(Version Version, string VersionLabel, IReadOnlyList<ChangelogLine> Lines);

    private readonly record struct ChangelogLine(string Text, int IndentLevel = 0, System.Numerics.Vector4? Color = null);
}