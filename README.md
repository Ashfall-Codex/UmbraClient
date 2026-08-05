<p align="center">
  <img src="https://repo.ashfall-codex.dev/img/umbra-full.png" alt="UmbraSync" width="128" />
</p>

<h1 align="center">UmbraSync</h1>

<p align="center">
  <b>Plugin Dalamud pour FFXIV</b> : Fork enrichi de Mare Synchronos, pensé avant tout pour les rôlistes. UmbraSync va au-delà de la simple synchronisation de mods pour offrir une expérience roleplay immersive et sociale.
</p>

<p align="center">
  <code>v3.0.0</code> &middot; API <code>v4000</code> &middot; C# 13 / .NET 10 &middot; Dalamud SDK 15.0.0 (Dalamud API15)
</p>

---

## Fonctionnalités

### Synchronisation de mods

- **Penumbra** : synchronisation automatique des mods, collections, settings et mods temporaires entre joueurs appairés
- **Glamourer** : synchronisation de l'apparence complète (customization, états, verrouillages)
- **Plugins tiers** : intégration native avec Customize+, Heels, Honorific, Moodles, PetNames et Brio
- **Transfert intelligent** : envoi et réception rapides des mods, reprise automatique en cas de coupure, évite les doublons et vérifie l'intégrité des fichiers
- **Distribution CDN** : téléchargement prioritaire depuis le CDN avec bascule automatique vers le serveur principal en cas d'indisponibilité
- **Compression BC7** : les textures synchronisées sont converties côté serveur en BC7 et distribuées sous forme d'alternates, ce qui réduit la mémoire vidéo et le volume téléchargé. Trois modes au choix : qualité source, compression des nouveaux téléchargements, ou tout compressé
- **Cache local** : gestion automatique du cache de mods avec compaction, nettoyage et monitoring de taille

### AutoDetect

- **Invitation rapide** : envoi d'invitation à un joueur en un clic via le bouton **+** ou clic droit sur son nom
- **Détection de proximité** : découverte automatique des joueurs UmbraSync à portée de votre personnage
- **Annuaire SyncFinder** : consultation et adhésion aux Syncshells publiques depuis une liste centralisée
- **SyncSlot** : liaison d'une Syncshell à votre housing avec partage temporaire optionnel, détection intérieur/extérieur
- **Anti-spam** : cooldown configurable au refus d'invitation (1 min à 1h) et blacklist persistante de joueurs bloqués
- **Suppression automatique** : désactivation automatique en zone instanciée (donjon, raid, PvP) avec restauration à la sortie
- **Planification** : programmation horaire de l'AutoDetect par Syncshell (durée fixe ou plages horaires)

### Roleplay

- **Ashfall Connect** : liez votre compte à la plateforme Ashfall Connect (Discord / XIVAuth) pour héberger, éditer et **certifier** votre fiche RP enrichie. Niveaux de certification d'identité (Bronze / Argent / Or). Liaison par code à usage unique depuis le plugin
- **Profil RP** : fiche personnage complète (prénom, nom, titre, âge, race, taille, résidence, occupation, alignement, etc.) avec champs personnalisés, photo dédiée et couleur de nom configurable
- **Profil classique** : photo de profil, description personnelle, statut NSFW
- **Icône de chat** : icône XIV affichée dans le chat avant le nom RP, sélecteur intégré avec recherche par ID ou par nom, palette partagée avec l'éditeur Moodles
- **Titre Honorific intégré** : édition du titre complet directement depuis la fiche RP avec color picker (couleur + glow), préfixe optionnel. Synchronisé automatiquement via Honorific
- **Moodles RP** : édition complète des traits Moodles (titre, description, icône, type, palette de couleurs UIColor) depuis la fiche RP, avec cache local de sauvegarde et restauration automatique
- **Annuaire d'établissements** : création et gestion d'établissements RP (tavernes, boutiques, temples, etc.) avec logo, bannière, localisation housing, gérant lié à une fiche RP, événements programmés (ponctuels ou récurrents) et calendrier "À venir"
- **RP sauvage** : possibilité de s'annoncer disponible pour du RP sauvage via l'annuaire, avec affichage automatique du secteur (ward) en quartier résidentiel
- **Bulle d'écriture** : indicateur de saisie en temps réel sur les nameplates et la Party List, compatible avec le chat natif et ChatTwo, avec notification sonore configurable *(inspiré de [RTyping](https://github.com/apetih/rtyping))*
- **Colorisation des émotes** : mise en évidence des emotes dans le chat (entre `<>`, `*` et `[]`) et coloration des dialogues entre `"..."` en blanc
- **Contenu HRP** : les messages entre parenthèses (simples et doubles) sont affichés en gris italique
- **Support BBCode** : formatage riche dans les informations du profil RP
- **Adaptation aux plugins tiers** : UmbraSync détecte automatiquement la présence de ChatTwo et de Chat Proximity pour s'y adapter. La bulle d'écriture fonctionne avec ChatTwo, et la colorisation des émotes s'ajuste en fonction de la distance si Chat Proximity est installé

### Hub de Données (MCDF)

- **Hub de données** : centre unifié pour créer, stocker, importer, partager et appliquer des MCDF et des entrées Live
- **Stockage serveur** : upload de fichiers MCDF sur le serveur (stockage illimité), gestion locale et en ligne dans un tableau unifié
- **Partage ciblé** : envoi de MCDF à des paires ou Syncshells spécifiques avec notifications push en temps réel (SignalR)
- **Chiffrement** : données chiffrées par AES-GCM avec salt et nonce aléatoires, tag d'authentification 128 bits
- **Gpose Together** : échange de poses en groupe directement depuis le hub
- **Favoris** : système de favoris unifié pour les entrées Live et MCDF avec description personnalisable
- **Dossier local** : scan automatique d'un dossier local de fichiers MCDF (y compris les sous-dossiers) avec upload vers le serveur

### Partage de Housing

- **Scan de meubles** : détection automatique des meubles et décorations de votre logement
- **Snapshot de layout** : capture et partage de l'agencement complet de votre housing
- **Revêtements** : les murs, sols et plafonds moddés sont inclus dans le partage, au même titre que le mobilier
- **Scénarios PNJ** : partagez les PNJ personnalisés de votre logement avec vos paires. Ils apparaissent automatiquement quand un visiteur autorisé entre, et disparaissent à la sortie. Permissions par paire / syncshell et gestion des conflits avec vos scénarios locaux
- **Spawn natif** : les PNJ sont créés directement par le plugin, sans dépendance à un plugin tiers. Apparence issue d'un MCDF ou d'un design Glamourer, avec masquage optionnel des armes, du casque et de la visière
- **Éditeur de scènes** : placement et rotation des PNJ en temps réel via un gizmo 3D, positions persistantes et regard orientable
- **Détection de propriété** : `HousingOwnershipService` mémorise les logements dont vous êtes réellement propriétaire pour la session, ce qui conditionne les actions de partage
- **Mod housing isolé** : le mod est construit à part puis installé d'un bloc, avec nettoyage des résidus et réutilisation des fichiers déjà téléchargés
- **Chiffrement** : données protégées par AES-GCM en transit
- **Application** : import du layout partagé par un autre joueur

### Syncshells (groupes)

- **Création** : syncshells permanentes ou temporaires (avec date d'expiration)
- **Administration** : gestion des membres (ban, retrait, permissions), transfert de propriété, changement de mot de passe, renommage de la syncshell
- **Invitations temporaires** : génération d'invitations à usage unique
- **Rôles** : Owner, Moderator, Member avec permissions granulaires
- **Collection Penumbra** : liaison d'une collection Penumbra spécifique par Syncshell
- **Profil de groupe** : description, tags, logo pour la découverte publique
- **Tri des membres** : tri de la liste des membres par type de pair ou par ordre alphabétique
- **Pruning** : nettoyage automatique des membres inactifs

### Synchronisation de quêtes

- **A Quest Reborn** : support de la synchronisation des quêtes personnalisées entre joueurs via sessions partagées

### Interface utilisateur

- **UI compacte** : interface revisitée avec navigation par sidebar et onglets, tout dans une même fenêtre. Annuaire intégré avec onglets Mes lieux, Lieux favoris, Parcourir et À venir
- **Thème Royal Smoke** : palette sombre matte avec accents violet, conçue pour le confort visuel
- **Fenêtre de permissions** : contrôle fin par paire (pause, sons, animations, VFX)
- **Data Analysis** : analyse détaillée des fichiers de votre personnage (taille, triangles, résolution, type)
- **Player Analysis** : analyse par paire de la latence de synchronisation et des fichiers de mods
- **Event Viewer** : journal en temps réel des événements du plugin avec filtrage
- **Syncshell Admin** : interface d'administration dédiée aux propriétaires et modérateurs de groupes
- **Widget de téléchargement** : suivi en temps réel des transferts upload/download
- **Widget Server Bar** : indicateur de statut dans la barre de serveur FFXIV avec styles personnalisables
- **Overlay d'écriture** : indicateur visuel sur les nameplates des joueurs en train d'écrire
- **Changelog intégré** : affichage automatique des nouveautés à chaque mise à jour
- **Notifications** : système centralisé avec badge, toast et panneau dédié, respectant les préférences d'affichage utilisateur (Nowhere/Chat/Toast/Both)
- **Messages de service** : diffusion de messages par le serveur (information, avertissement, erreur) et notification explicite en cas de déconnexion forcée

### Conformité RGPD

- **Consentement versionné** : écran de consentement au premier lancement, détaillant le périmètre exact des traitements. Toute extension de ce périmètre incrémente `MareConfig.ExpectedRgpdVersion` et déclenche une nouvelle demande de consentement au lancement suivant
- **Export local** : export JSON du contenu réel conservé sur la machine — fiches RP, notes et groupes de paires, joueurs bloqués, favoris de syncshells et de MCDF, établissements, réglages et inventaire des journaux de diagnostic
- **Export serveur** : récapitulatif de ce que le serveur détient sur le compte (UID, appairages, syncshells, profils, partages, fichiers envoyés)
- **Suppression locale** : effacement des données personnelles de la machine, sauvegardes de configuration comprises
- **Effacement serveur** : suppression définitive du compte, des comptes secondaires et de toutes les données associées, avec confirmation par saisie
- **Révocation** : retrait du consentement à tout moment, qui ramène le plugin à l'écran d'accueil
- **Droits utilisateur** : accès, rectification, effacement, portabilité, opposition et limitation

> Ashfall Connect est un service distinct : supprimer un compte UmbraSync n'efface pas la fiche RP hébergée sur Connect, dont la suppression se demande séparément.

### Performance et monitoring

- **Métriques** : collecte de performances (frame time, latence IPC, débit transfert)
- **Par joueur** : suivi de la latence de synchronisation, taille des données et taux d'erreur par paire
- **Analyse personnage** : scan complet des fichiers de mods avec statistiques détaillées
- **Mode connexion lente** : bascule manuelle vers le transport SignalR `LongPolling` pour contourner les middleboxes FAI qui coupent les WebSockets inactifs
- **Auto-ajustement réseau** : détection automatique d'instabilité (3 sessions courtes en moins de 3 min) et activation du mode connexion lente avec re-test transparent toutes les 24 h
- **Diagnostic réseau** : journal optionnel (Paramètres ▸ Avancé ▸ Débogage) capturant chaque message SignalR et les événements sockets, écrit dans `NetworkDiag/` et purgé par la suppression locale RGPD

---

## Architecture

Le projet est composé de plusieurs modules :

| Composant | Technologie | Description |
|---|---|---|
| `UmbraSync/` | C# 13 / .NET 10 / Dalamud SDK | Plugin FFXIV principal |
| `UmbraAPI/` | Submodule git | API partagée avec le serveur (contrats et DTOs) |
| `Penumbra.Api/` | Submodule git | API d'intégration Penumbra |
| `Glamourer.Api/` | Submodule git | API d'intégration Glamourer |
| `ffxiv_pictomancy/` | Submodule git | Bibliothèque de dessin 3D dans le monde |

### Plugin (C#)

- **Point d'entree** : `Plugin.cs` — injection de dépendances via `Microsoft.Extensions.DependencyInjection` avec architecture hosted services
- **Communication** : SignalR (WebSocket) avec authentification JWT, reconnexion automatique, protocole MessagePack + compression LZ4Block (frames binaires pour contourner les middleboxes qui coupent les WebSockets JSON)
- **Bus de messages** : Mediator pattern central (`MareMediator`) pour la communication intra-plugin
- **UI** : ImGui avec thème violet/sombre "Royal Smoke", fenêtres modulaires, composants réutilisables (`BbCodeToolbar`, `HonorificEditor`, `MoodlesEditor`, `ChatIconPicker`)
- **IPC** : intégration bidirectionnelle avec Penumbra, Glamourer, Customize+, Heels, Honorific, Moodles, PetNames, Brio et Mare Synchronos
- **Rendu** : overlays ImGui (nameplates, bulles d'écriture, profils) et dessin 3D dans le monde via `ffxiv_pictomancy`
- **Cache** : gestion de fichiers avec compression LZ4, compaction et déduplication

---

## Build

### Prérequis

- .NET 10.0 SDK
- Environnement de développement Dalamud
- Variable `DALAMUD_DIR` pointant vers l'installation Dalamud

### Compilation

```bash
# Initialiser les submodules
git submodule update --init --recursive

# Restaurer les dépendances
dotnet restore UmbraSync.sln -p:DALAMUD_DIR="$DALAMUD_DIR"

# Build Debug
dotnet build UmbraSync.sln -c Debug --no-restore -p:DALAMUD_DIR="$DALAMUD_DIR"

# Build Release
dotnet build UmbraSync.sln -c Release --no-restore -p:DALAMUD_DIR="$DALAMUD_DIR" -p:ContinuousIntegrationBuild=true
```

---

## Commandes

La commande principale est `/usync`. Un alias `/umbrasync` est également enregistré pour la découvrabilité, les deux pointent sur le même handler.

| Commande | Description |
|---|---|
| `/usync` | Ouvre la fenêtre principale |
| `/usync toggle [on\|off]` | Active ou désactive la synchronisation |
| `/usync gpose` | Ouvre le hub de données (Character Data) |
| `/usync analyze` | Ouvre l'analyse de données du personnage |
| `/usync rescan` | Force un scan du cache de mods |

### Commandes debug

| Commande | Description |
|---|---|
| `/usync perf [secondes]` | Affiche les métriques de performance |
| `/usync medi` | Affiche les informations du système Mediator |
| `/usync npcadd` | Ajoute un PNJ housing à la scène courante |
| `/usync npcedit` | Ouvre l'éditeur de scènes PNJ |
| `/usync npcwipe` | Retire tous les PNJ housing spawnés |
| `/usync npcsharetest` | Déclenche un test de partage de scénario PNJ |

---

## Dépendances

### NuGet

| Package | Version |
|---|---|
| `Microsoft.AspNetCore.SignalR.Client` | 10.0.6 |
| `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` | 10.0.6 |
| `MessagePack` | 2.5.187 |
| `Microsoft.Extensions.Hosting` | 10.0.6 |
| `System.IdentityModel.Tokens.Jwt` | 8.16.0 |
| `K4os.Compression.LZ4.Streams` | 1.3.8 |
| `K4os.Compression.LZ4.Legacy` | 1.3.8 |
| `Downloader` | 5.1.0 |
| `Chaos.NaCl.Standard` | 1.0.0 |
| `Brio.API` | 3.0.1 |
| `Penumbra.Api` | 5.13.1 |
| `Glamourer.Api` | 2.8.0 |
| `Dalamud.NET.Sdk` | 15.0.0 |
| `DalamudPackager` | 15.0.0 |

Analyseurs statiques appliqués à la compilation : `Meziantou.Analyzer` 3.0.25 et `SonarAnalyzer.CSharp` 10.21.0.

### Submodules git

| Submodule | Source |
|---|---|
| `UmbraAPI/` | [Ashfall-Codex/UmbraAPI](https://github.com/Ashfall-Codex/UmbraAPI) |
| `Penumbra.Api/` | [Ottermandias/Penumbra.Api](https://github.com/Ottermandias/Penumbra.Api) |
| `Glamourer.Api/` | [Ottermandias/Glamourer.Api](https://github.com/Ottermandias/Glamourer.Api) |
| `ffxiv_pictomancy/` | [sourpuh/ffxiv_pictomancy](https://github.com/sourpuh/ffxiv_pictomancy) |

---

## Langues

- **Francais** (langue par defaut)
- **English**

Basée sur des fichiers `.resx` : `Localization/Strings.resx` porte les chaînes anglaises (ressource neutre, source des traductions Crowdin) et `Strings.fr.resx` les chaînes françaises. Environ 2 200 clés de traduction, avec changement de langue à chaud via les paramètres.

Une clé absente de `Strings.fr.resx` retombe sur la ressource neutre et s'affiche donc en anglais, y compris pour un utilisateur configuré en français.

---

## Structure du projet

```
UmbraSync/
├── FileCache/              # Cache de mods (compaction, monitoring, alternates BC7)
├── Interop/                # Intégration Dalamud et IPC
│   ├── Ipc/                # 9 callers IPC (Penumbra, Glamourer, Customize+, etc.)
│   ├── Penumbra/           # Composants IPC Penumbra
│   ├── GameModel/          # Interop modèles de jeu
│   └── ChatTwo/            # Compatibilité ChatTwo
├── Localization/           # Fichiers .resx (EN neutre / FR)
├── MareConfiguration/      # 14 fichiers de configuration versionnés + migrations
├── Models/                 # Modèles de données (MoodleStatusInfo, etc.)
├── PlayerData/             # Gestion des paires et données de synchronisation
│   ├── Pairs/              # PairManager, Pair, PairAnalyzer
│   ├── Factories/          # Factories de création
│   ├── Handlers/           # PairHandler, GameObjectHandler
│   ├── Redraw/             # Orchestration des redraws
│   ├── Services/           # Services de cycle de vie des données de paire
│   └── Data/               # Modèles de remplacement de fichiers
├── Services/               # 50+ services
│   ├── ActorTracking/      # Suivi des acteurs du jeu
│   ├── AutoDetect/         # Détection de proximité et découverte
│   ├── CharaData/          # Gestion des données personnage (MCDF)
│   ├── Events/             # Système d'événements (EventAggregator)
│   ├── Housing/            # Housing, scénarios PNJ et spawn natif
│   ├── Mediator/           # Bus de messages central
│   ├── Network/            # Diagnostic réseau et écoute des événements sockets
│   ├── Notification/       # Système de notifications
│   ├── Rendering/          # PictomancyService
│   └── ServerConfiguration/ # Configuration serveur
├── UI/                     # 38 fenêtres et vues ImGui
│   ├── Components/         # Composants réutilisables (DrawPairBase, GroupPanel, BbCodeRenderer/Toolbar, HonorificEditor, MoodlesEditor, ChatIconPicker)
│   │   └── Popup/          # Handlers de popups (ban, report, slot)
│   ├── Handlers/           # Handlers UI (TagHandler, UidDisplayHandler)
│   └── *.cs                # Fenêtres principales
├── Utils/                  # Utilitaires (crypto, hashing, extensions)
├── WebAPI/                 # Client HTTP et SignalR
│   ├── SignalR/            # ApiController (13 modules fonctionnels)
│   ├── Files/              # Gestion des transferts de fichiers
│   ├── AutoDetect/         # Client API de découverte
│   └── AshfallConnectService.cs  # Client HTTP Ashfall Connect
├── Plugin.cs               # Point d'entrée IDalamudPlugin
├── MarePlugin.cs           # Logique plugin (IHostedService)
└── UmbraSync.csproj        # Fichier projet

UmbraAPI/
└── UmbraSyncAPI/
    ├── SignalR/            # IMareHub (121 méthodes), IMareHubClient
    ├── Dto/                # 84 DTOs (User, Group, CharaData, Files, Housing, Establishment, WildRp, Rgpd, etc.)
    ├── Data/               # Enums et modèles de données
    └── Routes/             # Définitions de routes API
```

---

## Licence

Le code original est sous licence MIT, voir le fichier `LICENSE_MIT` pour plus de détails. Les commits après `46f2443` sont sous licence **AGPL v3**, voir le fichier `LICENSE`.
