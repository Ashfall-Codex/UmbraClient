using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using Microsoft.Extensions.Logging;

namespace UmbraSync.UI;

public sealed class ChangelogUi : WindowMediatorSubscriberBase
{
    private const int AlwaysExpandedEntryCount = 2;

    private readonly MareConfigService _configService;
    private readonly UiSharedService _uiShared;
    private readonly Version _currentVersion;
    private readonly string _currentVersionLabel;
    private readonly IReadOnlyList<ChangelogEntry> _entries;

    private bool _showAllEntries;
    private bool _hasAcknowledgedVersion;

    public ChangelogUi(ILogger<ChangelogUi> logger, UiSharedService uiShared, MareConfigService configService,
        MareMediator mediator, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, Loc.Get("ChangelogUi.WindowTitle"), performanceCollectorService)
    {
        _uiShared = uiShared;
        _configService = configService;
        _currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        _currentVersionLabel = _currentVersion.ToString();
        _entries = BuildEntries();
        _hasAcknowledgedVersion = string.Equals(_configService.Current.LastChangelogVersionSeen, _currentVersionLabel, StringComparison.Ordinal);

        RespectCloseHotkey = true;
        SizeConstraints = new()
        {
            MinimumSize = new(520, 360),
            MaximumSize = new(900, 1200)
        };
        Flags |= ImGuiWindowFlags.NoResize;
        ShowCloseButton = true;

        if (!string.Equals(_configService.Current.LastChangelogVersionSeen, _currentVersionLabel, StringComparison.Ordinal))
        {
            IsOpen = true;
        }
        Mediator.Subscribe<OpenChangelogUiMessage>(this, (_) => IsOpen = true);
    }

    public override void OnClose()
    {
        MarkCurrentVersionAsReadIfNeeded();
        base.OnClose();
    }

    protected override void DrawInternal()
    {
        _ = _uiShared.DrawOtherPluginState();

        DrawHeader();
        DrawEntries();
        DrawFooter();
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

    private void DrawEntries()
    {
        bool expandedOldVersions = false;
        for (int index = 0; index < _entries.Count; index++)
        {
            var entry = _entries[index];
            if (!_showAllEntries && index >= AlwaysExpandedEntryCount)
            {
                if (!expandedOldVersions)
                {
                    expandedOldVersions = ImGui.CollapsingHeader(Loc.Get("ChangelogUi.FullHistory"));
                }

                if (!expandedOldVersions)
                {
                    continue;
                }
            }

            DrawEntry(entry);
        }
    }

    private void DrawEntry(ChangelogEntry entry)
    {
        using (ImRaii.PushId(entry.VersionLabel))
        {
            ImGui.Spacing();
            UiSharedService.ColorText(entry.VersionLabel, entry.Version == _currentVersion
                ? ImGuiColors.HealerGreen
                : ImGuiColors.DalamudWhite);

            ImGui.Spacing();

            foreach (var line in entry.Lines)
            {
                DrawLine(line);
            }

            ImGui.Spacing();
            ImGui.Separator();
        }
    }

    private static void DrawLine(ChangelogLine line)
    {
        using var indent = line.IndentLevel > 0 ? ImRaii.PushIndent(line.IndentLevel) : null;
        if (line.Color != null)
        {
            ImGui.TextColored(line.Color.Value, $"- {line.Text}");
        }
        else
        {
            ImGui.TextUnformatted($"- {line.Text}");
        }
    }

    private void DrawFooter()
    {
        ImGui.Spacing();
        if (!_showAllEntries && _entries.Count > AlwaysExpandedEntryCount)
        {
            if (ImGui.Button(Loc.Get("ChangelogUi.ShowAll")))
            {
                _showAllEntries = true;
            }

            ImGui.SameLine();
        }

        if (ImGui.Button(Loc.Get("ChangelogUi.MarkAsRead")))
        {
            MarkCurrentVersionAsReadIfNeeded();
            IsOpen = false;
        }
    }

    private void MarkCurrentVersionAsReadIfNeeded()
    {
        if (_hasAcknowledgedVersion)
            return;

        _configService.Current.LastChangelogVersionSeen = _currentVersionLabel;
        _configService.Save();
        _hasAcknowledgedVersion = true;
    }

    private static IReadOnlyList<ChangelogEntry> BuildEntries()
    {
        return new List<ChangelogEntry>
        {
            new(new Version(2, 3, 0, 0), "2.3.0.0", new List<ChangelogLine>
            {
                new("Nouveauté : Conforme avec le Règlement Général sur la Protection des Données (RGPD)."),
                new("Nouveauté : Support du plugin A Quest Reborn pour la synchronisation des quêtes personnalisées."),
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