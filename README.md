<p align="center">
  <img src="https://repo.ashfall-codex.dev/img/umbra-full.png" alt="UmbraSync" width="128" />
</p>

<h1 align="center">UmbraSync</h1>

<p align="center">
  <b>Plugin Dalamud pour FFXIV</b> : Fork enrichi de Mare Synchronos, pensé avant tout pour les rôlistes. UmbraSync va au-delà de la simple synchronisation de mods pour offrir une expérience roleplay immersive et sociale.
</p>

<p align="center">
  <code>v2.3.2</code> &middot; API <code>v3000</code> &middot; C# 13 / .NET 10 &middot; Dalamud SDK 14.0.2
</p>

---

## Fonctionnalités

### Synchronisation de mods

- **Penumbra** : synchronisation automatique des mods, collections, settings et mods temporaires entre joueurs appairés
- **Glamourer** : synchronisation de l'apparence complète (customization, états, verrouillages)
- **Plugins tiers** : intégration native avec Customize+, Heels, Honorific, Moodles, PetNames et Brio
- **Transfert intelligent** : envoi et réception rapides des mods, reprise automatique en cas de coupure, évite les doublons et vérifie l'intégrité des fichiers
- **Cache local** : gestion automatique du cache de mods avec compaction, nettoyage et monitoring de taille

### AutoDetect

- **Invitation rapide** : envoi d'invitation à un joueur en un clic via le bouton **+** ou clic droit sur son nom
- **Détection de proximité** : découverte automatique des joueurs UmbraSync à portée de votre personnage
- **Annuaire SyncFinder** : consultation et adhésion aux Syncshells publiques depuis une liste centralisée
- **SyncSlot** : liaison d'une Syncshell à votre housing avec partage temporaire optionnel
- **Planification** : programmation horaire de l'AutoDetect par Syncshell (durée fixe ou plages horaires)

### Roleplay

- **Profil RP** : fiche personnage complète (prénom, nom, titre, âge, race, taille, résidence, occupation, alignement, etc.) avec champs personnalisés, photo dédiée et couleur de nom configurable
- **Profil classique** : photo de profil, description personnelle, statut NSFW
- **Moodles RP** : intégration des Moodles dans les profils avec cache local de sauvegarde
- **Bulle d'écriture** : indicateur de saisie en temps réel sur les nameplates et la Party List, compatible avec le chat natif et ChatTwo *(inspiré de [RTyping](https://github.com/apetih/rtyping))*
- **Colorisation des émotes** : mise en évidence des emotes dans le chat (entre `<>`, `*` et `[]`)
- **Contenu HRP** : les messages entre parenthèses (simples et doubles) sont affichés en gris italique
- **Support BBCode** : formatage riche dans les informations du profil RP
- **Adaptation aux plugins tiers** : UmbraSync détecte automatiquement la présence de ChatTwo et de Chat Proximity pour s'y adapter. La bulle d'écriture fonctionne avec ChatTwo, et la colorisation des émotes s'ajuste en fonction de la distance si Chat Proximity est installé

### Partage MCDF (Mare Character Data Format)

- **Hub de données** : centre de gestion pour créer, importer, partager et appliquer des MCDF
- **Partage direct** : envoi de MCDF à vos paires sans passer par un cloud tiers, sans limite de stockage
- **Chiffrement** : données chiffrées par AES-GCM avec salt et nonce aléatoires, tag d'authentification 128 bits
- **Gpose Together** : échange de poses en groupe directement depuis le hub
- **Permissions** : contrôle d'accès par individu ou par Syncshell avec expiration configurable
- **MCD Online** : consultation de profils MCDF en ligne

### Partage de Housing

- **Scan de meubles** : détection automatique des meubles et décorations de votre logement
- **Snapshot de layout** : capture et partage de l'agencement complet de votre housing
- **Chiffrement** : données protégées par AES-GCM en transit
- **Application** : import du layout partagé par un autre joueur

### Syncshells (groupes)

- **Création** : syncshells permanentes ou temporaires (avec date d'expiration)
- **Administration** : gestion des membres (ban, retrait, permissions), transfert de propriété, changement de mot de passe
- **Invitations temporaires** : génération d'invitations à usage unique
- **Rôles** : Owner, Moderator, Member avec permissions granulaires
- **Profil de groupe** : description, tags, logo pour la découverte publique
- **Pruning** : nettoyage automatique des membres inactifs

### Synchronisation de quêtes

- **A Quest Reborn** : support de la synchronisation des quêtes personnalisées entre joueurs via sessions partagées

### Interface utilisateur

- **UI compacte** : interface revisitée avec navigation par sidebar et onglets, tout dans une même fenêtre
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
- **Notifications** : système centralisé avec badge, toast et panneau dédié

### Conformité RGPD

- **Consentement** : fenêtre de consentement au premier lancement, versionné et révocable
- **Export de données** : exportation complète de vos données personnelles
- **Suppression** : suppression de compte et de toutes les données associées
- **Droits utilisateur** : accès, effacement, opposition conformes au règlement

### Performance et monitoring

- **Métriques** : collecte de performances (frame time, latence IPC, débit transfert)
- **Par joueur** : suivi de la latence de synchronisation, taille des données et taux d'erreur par paire
- **Analyse personnage** : scan complet des fichiers de mods avec statistiques détaillées

---

## Architecture

Le projet est composé de plusieurs modules :

| Composant | Technologie | Description |
|---|---|---|
| `UmbraSync/` | C# 13 / .NET 10 / Dalamud SDK | Plugin FFXIV principal |
| `UmbraAPI/` | C# / .NET 10 | API partagée (contrats et DTOs) |
| `Penumbra.Api/` | Submodule git | API d'intégration Penumbra |
| `Glamourer.Api/` | Submodule git | API d'intégration Glamourer |
| `OtterGui/` | Submodule git | Bibliothèque UI ImGui |

### Plugin (C#)

- **Point d'entree** : `Plugin.cs` — injection de dépendances via `Microsoft.Extensions.DependencyInjection` avec architecture hosted services
- **Communication** : SignalR (WebSocket) avec authentification JWT, reconnexion automatique
- **Bus de messages** : Mediator pattern central (`MareMediator`) pour la communication intra-plugin
- **UI** : ImGui avec thème violet/sombre "Royal Smoke", fenêtres modulaires
- **IPC** : intégration bidirectionnelle avec Penumbra, Glamourer, Customize+, Heels, Honorific, Moodles, PetNames, Brio et Mare Synchronos
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

---

## Dépendances

### NuGet

| Package | Version |
|---|---|
| `Microsoft.AspNetCore.SignalR.Client` | 9.0.8 |
| `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` | 9.0.8 |
| `MessagePack` | 2.5.187 |
| `Microsoft.Extensions.Hosting` | 9.0.8 |
| `System.IdentityModel.Tokens.Jwt` | 8.14.0 |
| `K4os.Compression.LZ4.Streams` | 1.3.8 |
| `Downloader` | 3.3.4 |
| `Chaos.NaCl.Standard` | 1.0.0 |
| `Brio.API` | 3.0.1 |
| `Penumbra.Api` | 5.13.0 |
| `Glamourer.Api` | 2.6.0 |
| `Dalamud.NET.Sdk` | 14.0.2 |

### Submodules git

| Submodule | Source |
|---|---|
| `UmbraAPI/` | [Ashfall-Codex/UmbraAPI](https://github.com/Ashfall-Codex/UmbraAPI) |
| `Penumbra.Api/` | [Ottermandias/Penumbra.Api](https://github.com/Ottermandias/Penumbra.Api) |
| `Glamourer.Api/` | [Ottermandias/Glamourer.Api](https://github.com/Ottermandias/Glamourer.Api) |
| `OtterGui/` | [Ottermandias/OtterGui](https://github.com/Ottermandias/OtterGui) |

---

## Langues

- **Francais** (langue par defaut)
- **English**

Basée sur des fichiers `.resx` (`Localization/Strings.resx` pour le français, `Strings.fr.resx` pour l'anglais) avec plus de 500 clés de traduction. Changement de langue à chaud via les paramètres.

---

## Structure du projet

```
UmbraSync/
├── Communication/          # (hérité) Communication WebSocket
├── FileCache/              # Gestion du cache de mods (compaction, monitoring)
├── Interop/                # Intégration Dalamud et IPC
│   ├── Ipc/                # 9 callers IPC (Penumbra, Glamourer, Customize+, etc.)
│   ├── Penumbra/           # Composants IPC Penumbra
│   ├── GameModel/          # Interop modèles de jeu
│   └── ChatTwo/            # Compatibilité ChatTwo
├── Localization/           # Fichiers .resx (FR/EN)
├── MareConfiguration/      # 14 classes de configuration
├── Models/                 # Modèles de données (MoodleStatusInfo, etc.)
├── PlayerData/             # Gestion des paires et données de synchronisation
│   ├── Pairs/              # PairManager, Pair, PairAnalyzer
│   ├── Factories/          # Factories de création
│   ├── Handlers/           # PairHandler, GameObjectHandler
│   └── Data/               # Modèles de remplacement de fichiers
├── Services/               # 36+ services
│   ├── AutoDetect/         # Détection de proximité et découverte
│   ├── CharaData/          # Gestion des données personnage (MCDF)
│   ├── Events/             # Système d'événements (EventAggregator)
│   ├── Housing/            # Fonctionnalités housing
│   ├── Mediator/           # Bus de messages central
│   ├── Notification/       # Système de notifications
│   └── ServerConfiguration/ # Configuration serveur
├── UI/                     # 18+ fenêtres ImGui
│   ├── Components/         # Composants réutilisables (DrawPairBase, GroupPanel, BBCode)
│   ├── Handlers/           # Handlers UI (TagHandler, UidDisplayHandler)
│   └── *.cs                # Fenêtres principales
├── Utils/                  # Utilitaires (crypto, hashing, extensions)
├── WebAPI/                 # Client HTTP et SignalR
│   ├── SignalR/            # ApiController (9 modules fonctionnels)
│   ├── Files/              # Gestion des transferts de fichiers
│   └── AutoDetect/         # Client API de découverte
├── Plugin.cs               # Point d'entrée IDalamudPlugin
├── MarePlugin.cs           # Logique plugin (IHostedService)
└── UmbraSync.csproj        # Fichier projet

UmbraAPI/
└── UmbraSyncAPI/
    ├── SignalR/             # IMareHub (183 méthodes), IMareHubClient
    ├── Dto/                 # 58 DTOs (User, Group, CharaData, Files, Housing, etc.)
    ├── Data/                # Enums et modèles de données
    └── Routes/              # Définitions de routes API
```

---

## Licence

Le code original est sous licence MIT, voir le fichier `LICENSE_MIT` pour plus de détails. Les commits après `46f2443` sont sous licence **AGPL v3**, voir le fichier `LICENSE`.
