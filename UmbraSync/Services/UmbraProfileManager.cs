using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.Group;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;

namespace UmbraSync.Services;

public class UmbraProfileManager : MediatorSubscriberBase
{
    private const string _noDescription = "-- User has no description set --";
    private const string _nsfw = "Profile not displayed - NSFW";
    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly MareConfigService _mareConfigService;
    private readonly RpConfigService _rpConfigService;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly ConcurrentDictionary<(UserData User, string? CharName, uint? WorldId), UmbraProfileData> _umbraProfiles = new();
    private readonly ConcurrentDictionary<string, GroupProfileDto> _groupProfiles = new(StringComparer.OrdinalIgnoreCase);

    private readonly UmbraProfileData _defaultProfileData = new(IsFlagged: false, IsNSFW: false, string.Empty, _noDescription);
    private readonly UmbraProfileData _loadingProfileData = new(IsFlagged: false, IsNSFW: false, string.Empty, "Loading Data from server...");
    private readonly UmbraProfileData _nsfwProfileData = new(IsFlagged: false, IsNSFW: false, string.Empty, _nsfw);
    private readonly string _configDir;
    private readonly ConcurrentDictionary<string, ((UserData User, string? CharName, uint? WorldId) Key, UmbraProfileData Profile)> _persistedProfiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _persistedAtUtc = new(StringComparer.Ordinal);
    private static readonly TimeSpan PersistedProfileLifetime = TimeSpan.FromDays(30);
    private string? _cacheUid;
    private volatile string? _cacheLoadedForUid;
    private readonly Lock _cacheLoadLock = new();
    private bool _cacheDirty;
    private Timer? _saveTimer;
    private CancellationTokenSource? _ownProfileSyncCts;

    public string? CurrentUid => _apiController.IsConnected ? _apiController.UID : null;

    public UmbraProfileManager(ILogger<UmbraProfileManager> logger, MareConfigService mareConfigService,
        RpConfigService rpConfigService, MareMediator mediator, ApiController apiController,
        PairManager pairManager, DalamudUtilService dalamudUtil, ServerConfigurationManager serverConfigurationManager,
        IDalamudPluginInterface pluginInterface) : base(logger, mediator)
    {
        _mareConfigService = mareConfigService;
        _rpConfigService = rpConfigService;
        _apiController = apiController;
        _pairManager = pairManager;
        _dalamudUtil = dalamudUtil;
        _serverConfigurationManager = serverConfigurationManager;
        _configDir = pluginInterface.ConfigDirectory.FullName;

        Mediator.Subscribe<ClearProfileDataMessage>(this, (msg) =>
        {
            if (msg.UserData != null)
            {
                foreach (var k in _umbraProfiles.Keys.Where(k =>
                    string.Equals(k.User.UID, msg.UserData.UID, StringComparison.Ordinal) &&
                    (msg.CharacterName == null || string.Equals(k.CharName, msg.CharacterName, StringComparison.Ordinal)) &&
                    (msg.WorldId == null || k.WorldId == msg.WorldId)).ToList())
                {
                    _umbraProfiles.TryRemove(k, out _);
                }
            }
            else
                _umbraProfiles.Clear();
        });
        Mediator.Subscribe<DisconnectedMessage>(this, (_) =>
        {
            CancelOwnProfileSync();
            SaveProfileCacheNow();
            _umbraProfiles.Clear();
            _groupProfiles.Clear();
            _persistedProfiles.Clear();
            _cacheUid = null;
            _cacheLoadedForUid = null;
        });
        Mediator.Subscribe<GroupProfileUpdatedMessage>(this, (msg) =>
        {
            if (msg.Profile.Group != null)
            {
                StoreGroupProfile(msg.Profile.Group.GID, msg.Profile);
            }
        });
        Mediator.Subscribe<ConnectedMessage>(this, (msg) =>
        {
            CancelOwnProfileSync();
            _ownProfileSyncCts = new CancellationTokenSource();
            _ = DelayedEnsureOwnProfileSyncedAsync(_ownProfileSyncCts.Token);
       
            StartBackgroundCacheLoad();
        });
    }

    public GroupProfileDto? GetGroupProfile(string gid)
    {
        _groupProfiles.TryGetValue(gid, out var profile);
        return profile;
    }

    public void SetGroupProfile(string gid, GroupProfileDto profile) => StoreGroupProfile(gid, profile);

    private void StoreGroupProfile(string gid, GroupProfileDto profile)
    {
        _groupProfiles[gid] = profile;
        _diskGroupProfiles[gid] = profile;
        PersistGroupProfileInBackground(gid, profile);
    }

    // --- Cache disque des profils de syncshell (icône, couleur du contour) ---
    // Les icônes s'affichent aussitôt au lancement au lieu d'arriver une à une après les requêtes. La fraîcheur
    // est assurée par la requête de relecture de chaque session et par les mises à jour poussées par le serveur :
    // dès qu'une image diffère de celle du disque, la copie locale est remplacée.

    private readonly ConcurrentDictionary<string, GroupProfileDto?> _diskGroupProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _diskGroupProfileLoads = new(StringComparer.OrdinalIgnoreCase);

    private string GroupProfileCacheDir => Path.Combine(_configDir, "syncshell_profiles");

    private string GroupProfileCachePath(string gid)
    {
        // Le GID vient du serveur : on ne garde que des caractères sûrs pour un nom de fichier.
        var safe = new string(gid.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return Path.Combine(GroupProfileCacheDir, safe + ".json");
    }

    private sealed class GroupProfileCacheEntry
    {
        public string Gid { get; set; } = string.Empty;
        public string? ProfileImageBase64 { get; set; }
        public string? BorderColor { get; set; }
        public bool IsNsfw { get; set; }
        public bool IsDisabled { get; set; }
        public DateTime CachedAtUtc { get; set; }
    }

    /// <summary>
    /// Profil de syncshell connu du disque, mais pas encore confirmé par le serveur cette session.
    /// Ne bloque jamais : la lecture du fichier se fait en arrière-plan et le premier appel renvoie null.
    /// </summary>
    public GroupProfileDto? GetCachedGroupProfile(string gid)
    {
        if (_diskGroupProfiles.TryGetValue(gid, out var known)) return known;
        if (!_diskGroupProfileLoads.TryAdd(gid, 0)) return null;

        _ = Task.Run(() =>
        {
            try
            {
                var path = GroupProfileCachePath(gid);
                if (!File.Exists(path)) { _diskGroupProfiles.TryAdd(gid, null); return; }

                var entry = JsonSerializer.Deserialize<GroupProfileCacheEntry>(File.ReadAllText(path));
                if (entry == null || DateTime.UtcNow - entry.CachedAtUtc > PersistedProfileLifetime)
                {
                    TryDeleteFile(path);
                    _diskGroupProfiles.TryAdd(gid, null);
                    return;
                }

                _diskGroupProfiles.TryAdd(gid, new GroupProfileDto
                {
                    Group = new GroupData(gid),
                    ProfileImageBase64 = entry.ProfileImageBase64,
                    BorderColor = entry.BorderColor,
                    IsNsfw = entry.IsNsfw,
                    IsDisabled = entry.IsDisabled,
                });
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Cache de profil de syncshell illisible pour {gid}", gid);
                _diskGroupProfiles.TryAdd(gid, null);
            }
        });
        return null;
    }

    private void PersistGroupProfileInBackground(string gid, GroupProfileDto profile)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var path = GroupProfileCachePath(gid);

                // Une fiche que l'utilisateur a choisi de ne pas afficher n'a pas à rester sur son disque.
                if (profile.IsNsfw && !_mareConfigService.Current.ProfilesAllowNsfw)
                {
                    TryDeleteFile(path);
                    return;
                }

                Directory.CreateDirectory(GroupProfileCacheDir);
                var entry = new GroupProfileCacheEntry
                {
                    Gid = gid,
                    ProfileImageBase64 = profile.ProfileImageBase64,
                    BorderColor = string.IsNullOrEmpty(profile.BorderColor) ? null : profile.BorderColor,
                    IsNsfw = profile.IsNsfw,
                    IsDisabled = profile.IsDisabled,
                    CachedAtUtc = DateTime.UtcNow,
                };

                // Écriture sans rien changer si le contenu est identique : évite d'user le disque à chaque connexion.
                if (File.Exists(path))
                {
                    var previous = JsonSerializer.Deserialize<GroupProfileCacheEntry>(File.ReadAllText(path));
                    if (previous != null
                        && string.Equals(previous.ProfileImageBase64, entry.ProfileImageBase64, StringComparison.Ordinal)
                        && string.Equals(previous.BorderColor, entry.BorderColor, StringComparison.Ordinal)
                        && previous.IsNsfw == entry.IsNsfw && previous.IsDisabled == entry.IsDisabled
                        && DateTime.UtcNow - previous.CachedAtUtc < TimeSpan.FromDays(7))
                        return;
                }

                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(entry));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Écriture du cache de profil de syncshell impossible pour {gid}", gid);
            }
        });
    }

    /// <summary>Supprime les profils conservés pour des syncshells que l'utilisateur n'a plus (ou périmés).</summary>
    public void PruneGroupProfileCache(IReadOnlyCollection<string> currentGids)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(GroupProfileCacheDir)) return;
                var keep = new HashSet<string>(currentGids.Select(g => Path.GetFileName(GroupProfileCachePath(g))), StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(GroupProfileCacheDir))
                {
                    var name = Path.GetFileName(file);
                    var expired = DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > PersistedProfileLifetime;
                    if (!keep.Contains(name) || expired)
                        TryDeleteFile(file);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Nettoyage du cache de profils de syncshell impossible");
            }
        });
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { /* fichier déjà supprimé ou verrouillé : sans conséquence */ }
    }

    public void ClearGroupProfile(string gid)
    {
        _groupProfiles.TryRemove(gid, out _);
        _diskGroupProfiles.TryRemove(gid, out _);
        _diskGroupProfileLoads.TryRemove(gid, out _);
        TryDeleteFile(GroupProfileCachePath(gid));
    }

    /// <summary>
    /// Fiche déjà connue (mémoire ou cache disque) pour le personnage courant de cette personne.
    /// Ne lance jamais de requête : utilisable à chaque frame pour chaque ligne d'une liste.
    /// </summary>
    public bool TryGetKnownProfile(UserData data, out UmbraProfileData profile)
    {
        profile = _defaultProfileData;
        var (charName, worldId) = ResolveCharacter(data);
        if (string.IsNullOrEmpty(charName)) return false;
        if (worldId == 0) worldId = null;

        if (_umbraProfiles.TryGetValue(NormalizeKey(data, charName, worldId), out var known)
            && !ReferenceEquals(known, _loadingProfileData) && !ReferenceEquals(known, _defaultProfileData)
            && !ReferenceEquals(known, _nsfwProfileData))
        {
            profile = known;
            return true;
        }

        if (!IsCacheLoadedForCurrentUid())
        {
            StartBackgroundCacheLoad();
            return false;
        }

        if (worldId is > 0 && _persistedProfiles.TryGetValue($"{data.UID}_{charName}_{worldId}", out var persisted))
        {
            profile = persisted.Profile;
            return true;
        }

        return false;
    }

    public (string? CharName, uint? WorldId) ResolveCharacter(UserData data)
    {
        var pair = _pairManager.GetPairByUID(data.UID);
        string? charName;
        uint? worldId;

        if (pair != null)
        {
            // Utilisateur online : utiliser les données du pair
            charName = pair.PlayerName;
            worldId = pair.WorldId == 0 ? null : pair.WorldId;
        }
        else if (string.Equals(data.UID, _apiController.UID, StringComparison.Ordinal))
        {
            // C'est nous-même : utiliser nos propres données
            charName = _dalamudUtil.GetPlayerName();
            worldId = _dalamudUtil.GetHomeWorldId();
        }
        else
        {
            // Utilisateur offline : utiliser les dernières données connues
            charName = _serverConfigurationManager.GetNameForUid(data.UID);
            worldId = _serverConfigurationManager.GetWorldIdForUid(data.UID);
        }

        return (charName, worldId);
    }

    public UmbraProfileData GetUmbraProfile(UserData data)
    {
        var (charName, worldId) = ResolveCharacter(data);
        return GetUmbraProfile(data, charName, worldId);
    }

    public UmbraProfileData GetUmbraProfile(UserData data, string? charName, uint? worldId)
    {
        if (worldId == 0) worldId = null;
        var key = NormalizeKey(data, charName, worldId);
        if (!_umbraProfiles.TryGetValue(key, out var profile))
        {
            if (_umbraProfiles.TryAdd(key, _loadingProfileData))
                _ = Task.Run(() => GetUmbraProfileFromService(data, charName, worldId));
            return (_loadingProfileData);
        }

        return (profile);
    }

    public void SetPreviewProfile(UserData data, string? charName, uint? worldId, UmbraProfileData profileData)
    {
        if (worldId == 0) worldId = null;
        var key = NormalizeKey(data, charName, worldId);
        _umbraProfiles[key] = profileData;
    }

    public async Task GetUmbraProfileFromService(UserData data, string? charName = null, uint? worldId = null)
    {
        if (worldId == 0) worldId = null;
        var key = NormalizeKey(data, charName, worldId);
        try
        {
            _umbraProfiles[key] = _loadingProfileData;
            var profile = await _apiController.UserGetProfile(new API.Dto.User.UserDto(data)
            {
                CharacterName = charName,
                WorldId = worldId
            }).ConfigureAwait(false);

            Logger.LogDebug("Profile response for {uid}: RP name {name}, RP description {desc}",
                data.UID,
                string.IsNullOrEmpty(profile.RpFirstName) && string.IsNullOrEmpty(profile.RpLastName) ? "(empty)" : "(set)",
                string.IsNullOrEmpty(profile.RpDescription) ? "(empty)" : "(set)");

            if (!string.IsNullOrEmpty(profile.CharacterName))
                _serverConfigurationManager.SetNameForUid(data.UID, profile.CharacterName);
            if (profile.WorldId is > 0)
                _serverConfigurationManager.SetWorldIdForUid(data.UID, profile.WorldId.Value);

            if (!string.IsNullOrEmpty(profile.CharacterName) && profile.WorldId is > 0)
            {
                _serverConfigurationManager.AddEncounteredAlt(data.UID, profile.CharacterName, profile.WorldId.Value);

                // Clean up stale local entry if server returned different data than what we requested
                if (charName != null && worldId is > 0
                    && (!string.Equals(profile.CharacterName, charName, StringComparison.Ordinal) || profile.WorldId.Value != worldId.Value))
                {
                    // Passé en Debug : ces deux noms de personnage n'ont rien à faire dans un
                    // journal que l'utilisateur partagera pour un tout autre problème.
                    Logger.LogDebug("Server corrected alt for {uid}: requested {reqChar}@{reqWorld}, got {srvChar}@{srvWorld}",
                        data.UID, charName, worldId, profile.CharacterName, profile.WorldId.Value);
                    _serverConfigurationManager.RemoveEncounteredAlt(data.UID, charName, worldId.Value);
                    RemovePersistedProfile(data, charName, worldId);
                    _umbraProfiles.TryRemove(NormalizeKey(data, charName, worldId), out _);
                }
            }

            List<RpCustomField>? customFields = null;
            if (!string.IsNullOrEmpty(profile.RpCustomFields))
            {
                try { customFields = JsonSerializer.Deserialize<List<RpCustomField>>(profile.RpCustomFields); }
                catch (JsonException ex) { Logger.LogWarning(ex, "Failed to deserialize RpCustomFields for {uid}", data.UID); }
            }

            bool isSelf = string.Equals(_apiController.UID, data.UID, StringComparison.Ordinal);

            // Local fallback for self: if the server returned 0 (e.g. server not yet redeployed,
            // or transient inconsistency), prefer the locally-cached value from RpConfigService so
            // the user always sees their own settings reflected in the UI.
            ushort effectiveChatIcon = profile.ChatIcon ?? 0;
            byte effectiveRpLevel = profile.RpLevel ?? 0;
            if (isSelf && !string.IsNullOrEmpty(charName) && worldId is > 0)
            {
                var localRp = _rpConfigService.GetCharacterProfile(charName, worldId.Value);
                if (effectiveChatIcon == 0 && localRp.ChatIcon != 0)
                    effectiveChatIcon = localRp.ChatIcon;
                if (effectiveRpLevel == 0 && localRp.RpLevel != 0)
                    effectiveRpLevel = localRp.RpLevel;
            }

            UmbraProfileData profileData = new(profile.Disabled, profile.IsNSFW ?? false,
                string.IsNullOrEmpty(profile.ProfilePictureBase64) ? string.Empty : profile.ProfilePictureBase64,
                string.IsNullOrEmpty(profile.Description) ? _noDescription : profile.Description,
                profile.RpProfilePictureBase64, profile.RpDescription, profile.IsRpNSFW ?? false,
                profile.RpFirstName, profile.RpLastName, profile.RpTitle, profile.RpAge,
                profile.RpRace, profile.RpEthnicity,
                profile.RpHeight, profile.RpBuild, profile.RpResidence, profile.RpOccupation, profile.RpAffiliation,
                profile.RpAlignment, profile.RpAdditionalInfo, profile.RpNameColor,
                customFields,
                profile.MoodlesData,
                effectiveChatIcon,
                effectiveRpLevel,
                profile.RpVisibility,
                profile.RpBannerBase64);

            if (_apiController.IsConnected && isSelf && charName != null && worldId != null)
            {
                var localRpProfile = _rpConfigService.GetCharacterProfile(charName, worldId.Value);
                bool changed = false;
                static bool ShouldReplace(string? localValue, string? serverValue)
                    => string.IsNullOrEmpty(localValue) && !string.IsNullOrEmpty(serverValue);

                if (ShouldReplace(localRpProfile.RpFirstName, profileData.RpFirstName)) { localRpProfile.RpFirstName = profileData.RpFirstName!; changed = true; }
                if (ShouldReplace(localRpProfile.RpLastName, profileData.RpLastName)) { localRpProfile.RpLastName = profileData.RpLastName!; changed = true; }
                if (ShouldReplace(localRpProfile.RpTitle, profileData.RpTitle)) { localRpProfile.RpTitle = profileData.RpTitle!; changed = true; }
                if (ShouldReplace(localRpProfile.RpDescription, profileData.RpDescription)) { localRpProfile.RpDescription = profileData.RpDescription!; changed = true; }
                if (ShouldReplace(localRpProfile.RpAge, profileData.RpAge)) { localRpProfile.RpAge = profileData.RpAge!; changed = true; }
                if (ShouldReplace(localRpProfile.RpRace, profileData.RpRace)) { localRpProfile.RpRace = profileData.RpRace!; changed = true; }
                if (ShouldReplace(localRpProfile.RpEthnicity, profileData.RpEthnicity)) { localRpProfile.RpEthnicity = profileData.RpEthnicity!; changed = true; }
                if (ShouldReplace(localRpProfile.RpHeight, profileData.RpHeight)) { localRpProfile.RpHeight = profileData.RpHeight!; changed = true; }
                if (ShouldReplace(localRpProfile.RpBuild, profileData.RpBuild)) { localRpProfile.RpBuild = profileData.RpBuild!; changed = true; }
                if (ShouldReplace(localRpProfile.RpResidence, profileData.RpResidence)) { localRpProfile.RpResidence = profileData.RpResidence!; changed = true; }
                if (ShouldReplace(localRpProfile.RpOccupation, profileData.RpOccupation)) { localRpProfile.RpOccupation = profileData.RpOccupation!; changed = true; }
                if (ShouldReplace(localRpProfile.RpAffiliation, profileData.RpAffiliation)) { localRpProfile.RpAffiliation = profileData.RpAffiliation!; changed = true; }
                if (ShouldReplace(localRpProfile.RpAlignment, profileData.RpAlignment)) { localRpProfile.RpAlignment = profileData.RpAlignment!; changed = true; }
                if (ShouldReplace(localRpProfile.RpAdditionalInfo, profileData.RpAdditionalInfo)) { localRpProfile.RpAdditionalInfo = profileData.RpAdditionalInfo!; changed = true; }
                if (localRpProfile.IsRpNsfw != profileData.IsRpNSFW) { localRpProfile.IsRpNsfw = profileData.IsRpNSFW; changed = true; }
                if (ShouldReplace(localRpProfile.RpProfilePictureBase64, profileData.Base64RpProfilePicture)) { localRpProfile.RpProfilePictureBase64 = profileData.Base64RpProfilePicture!; changed = true; }
                if (ShouldReplace(localRpProfile.RpNameColor, profileData.RpNameColor)) { localRpProfile.RpNameColor = profileData.RpNameColor!; changed = true; }
                var serverCustomFields = profileData.RpCustomFields ?? new List<RpCustomField>();
                if (localRpProfile.RpCustomFields.Count == 0 && serverCustomFields.Count > 0) { localRpProfile.RpCustomFields = serverCustomFields; changed = true; }

                if (string.IsNullOrEmpty(localRpProfile.MoodlesBackupJson) && !string.IsNullOrEmpty(profileData.MoodlesData))
                {
                    localRpProfile.MoodlesBackupJson = profileData.MoodlesData;
                    changed = true;
                    Logger.LogInformation("Restored MoodlesBackupJson from server for {uid}", data.UID);
                }

                if (changed)
                {
                    Logger.LogInformation("Local RP profile updated from server for {uid}", data.UID);
                    _rpConfigService.Save();
                }
            }

            if (profileData.IsNSFW && !_mareConfigService.Current.ProfilesAllowNsfw && !isSelf)
            {
                _umbraProfiles[key] = _nsfwProfileData;
            }
            else if (profileData.IsRpNSFW && !_mareConfigService.Current.ProfilesAllowRpNsfw && !isSelf)
            {
                _umbraProfiles[key] = _nsfwProfileData;
            }
            else
            {
                _umbraProfiles[key] = profileData;
            }

            // Persist to disk cache (not for self)
            if (!isSelf)
            {
                // Le serveur ne renvoie un niveau de visibilité à un autre joueur que lorsque la fiche RP existe
                // mais ne lui est plus accessible : l'ancienne copie conservée sur disque doit disparaître.
                if (profile.RpVisibility.HasValue)
                    RemovePersistedProfile(data, charName, worldId);

                UpdatePersistedProfile(data, charName, worldId, profileData);

                // Fetch all alt profiles for this UID in the background (only if we have valid encounter data)
                if (!string.IsNullOrEmpty(charName) && worldId is > 0)
                {
                    var fetchCharName = charName;
                    var fetchWorldId = worldId.Value;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await FetchAndCacheAllAltProfiles(data, fetchCharName, fetchWorldId).ConfigureAwait(false);
                        }
                        catch (Exception ex2)
                        {
                            Logger.LogWarning(ex2, "Failed to fetch alt profiles for {uid}", data.UID);
                        }
                    });
                }
            }

            Mediator.Publish(new NameplateRedrawMessage());
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to get Profile from service for user {user}", data);
            _umbraProfiles[key] = _defaultProfileData;
        }
    }

    private async Task FetchAndCacheAllAltProfiles(UserData data, string encounteredCharName, uint encounteredWorldId)
    {
        var allProfiles = await _apiController.UserGetAllCharacterProfiles(new API.Dto.User.UserDto(data)
        {
            CharacterName = encounteredCharName,
            WorldId = encounteredWorldId
        }).ConfigureAwait(false);
        if (allProfiles.Count == 0) return;

        Logger.LogInformation("Fetched {count} alt profiles for {uid}", allProfiles.Count, data.UID);
        bool isSelf = string.Equals(_apiController.UID, data.UID, StringComparison.Ordinal);

        if (!isSelf) PurgePersistedProfilesNoLongerVisible(data, allProfiles);

        foreach (var profile in allProfiles)
        {
            var altCharName = profile.CharacterName;
            var altWorldId = profile.WorldId;
            if (string.IsNullOrEmpty(altCharName) || altWorldId is null or 0) continue;

            _serverConfigurationManager.AddEncounteredAlt(data.UID, altCharName, altWorldId.Value);

            var altKey = NormalizeKey(data, altCharName, altWorldId);
            if (_umbraProfiles.ContainsKey(altKey)) continue;

            List<RpCustomField>? customFields = null;
            if (!string.IsNullOrEmpty(profile.RpCustomFields))
            {
                try { customFields = JsonSerializer.Deserialize<List<RpCustomField>>(profile.RpCustomFields); }
                catch (JsonException ex) { Logger.LogWarning(ex, "Failed to deserialize RpCustomFields for an alt of {uid}", data.UID); }
            }

            var altProfileData = new UmbraProfileData(profile.Disabled, profile.IsNSFW ?? false,
                string.IsNullOrEmpty(profile.ProfilePictureBase64) ? string.Empty : profile.ProfilePictureBase64,
                string.IsNullOrEmpty(profile.Description) ? _noDescription : profile.Description,
                profile.RpProfilePictureBase64, profile.RpDescription, profile.IsRpNSFW ?? false,
                profile.RpFirstName, profile.RpLastName, profile.RpTitle, profile.RpAge,
                profile.RpRace, profile.RpEthnicity,
                profile.RpHeight, profile.RpBuild, profile.RpResidence, profile.RpOccupation, profile.RpAffiliation,
                profile.RpAlignment, profile.RpAdditionalInfo, profile.RpNameColor,
                customFields,
                profile.MoodlesData,
                profile.ChatIcon ?? 0,
                profile.RpLevel ?? 0,
                Base64RpBanner: profile.RpBannerBase64);

            if (!isSelf)
            {
                if (altProfileData.IsNSFW && !_mareConfigService.Current.ProfilesAllowNsfw)
                    _umbraProfiles[altKey] = _nsfwProfileData;
                else if (altProfileData.IsRpNSFW && !_mareConfigService.Current.ProfilesAllowRpNsfw)
                    _umbraProfiles[altKey] = _nsfwProfileData;
                else
                    _umbraProfiles[altKey] = altProfileData;

                UpdatePersistedProfile(data, altCharName, altWorldId, altProfileData);
            }
        }

        Mediator.Publish(new NameplateRedrawMessage());
    }

    // Le serveur ne renvoie que les personnages dont la fiche nous est visible : une copie disque d'un autre
    // personnage du même compte qui n'en fait plus partie a été masquée ou supprimée depuis.
    private void PurgePersistedProfilesNoLongerVisible(UserData data, IEnumerable<API.Dto.User.UserProfileDto> visibleProfiles)
    {
        EnsureCacheLoaded();
        var visibleKeys = visibleProfiles
            .Where(p => !string.IsNullOrEmpty(p.CharacterName) && p.WorldId is > 0)
            .Select(p => $"{data.UID}_{p.CharacterName}_{p.WorldId}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in _persistedProfiles.ToList())
        {
            if (!string.Equals(entry.Value.Key.User.UID, data.UID, StringComparison.Ordinal)) continue;
            if (visibleKeys.Contains(entry.Key)) continue;
            RemovePersistedProfile(data, entry.Value.Key.CharName, entry.Value.Key.WorldId);
            _umbraProfiles.TryRemove(NormalizeKey(data, entry.Value.Key.CharName, entry.Value.Key.WorldId), out _);
        }
    }

    public List<(string CharName, uint WorldId)> GetEncounteredAlts(string uid)
    {
        var alts = _serverConfigurationManager.GetEncounteredAlts(uid);
        return alts.Select(key =>
        {
            var sep = key.LastIndexOf('@');
            if (sep < 0) return (key, (uint)0);
            return (key[..sep], uint.Parse(key[(sep + 1)..], CultureInfo.InvariantCulture));
        }).Where(a => a.Item2 > 0).ToList();
    }

    public IReadOnlyCollection<((UserData User, string? CharName, uint? WorldId) Key, UmbraProfileData Profile)> GetCachedProfiles()
    {
        if (!IsCacheLoadedForCurrentUid())
        {
            StartBackgroundCacheLoad();
            return [];
        }

        return _persistedProfiles.Values.ToList().AsReadOnly();
    }

    public void ClearPersistedProfileCache()
    {
        _persistedProfiles.Clear();
        _persistedAtUtc.Clear();
        _umbraProfiles.Clear();
        _cacheDirty = true;
        SaveProfileCacheNow();
        Logger.LogInformation("Profile cache cleared by user");
    }
    
    private async Task DelayedEnsureOwnProfileSyncedAsync(CancellationToken token)
    {
        try
        {
            int initialDelayMs = Random.Shared.Next(2000, 5000);
            await Task.Delay(initialDelayMs, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || !_apiController.IsConnected) return;
            await EnsureOwnProfileSyncedAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on disconnect/dispose
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "DelayedEnsureOwnProfileSynced failed");
        }
    }

    private async Task EnsureOwnProfileSyncedAsync(CancellationToken token = default)
    {
        try
        {
            if (!_apiController.IsConnected || string.IsNullOrEmpty(_apiController.UID))
                return;

            // Attendre que le joueur soit complètement chargé (max ~10s)
            string charName = "--";
            uint worldId = 0;
            for (int i = 0; i < 20 && (string.Equals(charName, "--", StringComparison.Ordinal) || string.IsNullOrEmpty(charName) || worldId == 0); i++)
            {
                await Task.Delay(500, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || !_apiController.IsConnected) return;
                charName = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false);
                worldId = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(charName) || string.Equals(charName, "--", StringComparison.Ordinal) || worldId == 0)
            {
                Logger.LogWarning("EnsureOwnProfileSynced: Player data unavailable after retries");
                return;
            }

            Logger.LogInformation("EnsureOwnProfileSynced: Fetching full profile from server for {uid}@{worldId}", _apiController.UID, worldId);
            await GetUmbraProfileFromService(new UserData(_apiController.UID), charName, worldId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on disconnect/dispose
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "EnsureOwnProfileSynced failed");
        }
    }

    private void CancelOwnProfileSync()
    {
        try
        {
            _ownProfileSyncCts?.Cancel();
            _ownProfileSyncCts?.Dispose();
        }
        catch { /* ignore */ }
        finally
        {
            _ownProfileSyncCts = null;
        }
    }

    #region Persistent Profile Cache

    private bool IsCacheLoadedForCurrentUid()
        => _apiController.IsConnected
           && string.Equals(_cacheLoadedForUid, _apiController.UID, StringComparison.Ordinal);

    private void StartBackgroundCacheLoad()
    {
        if (!_apiController.IsConnected || IsCacheLoadedForCurrentUid()) return;
        _ = Task.Run(() =>
        {
            try
            {
                EnsureCacheLoaded();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Chargement du cache de profils en arrière-plan impossible");
            }
        });
    }

    private void EnsureCacheLoaded()
    {
        if (!_apiController.IsConnected) return;
        var uid = _apiController.UID;
        lock (_cacheLoadLock)
        {
            if (string.Equals(_cacheLoadedForUid, uid, StringComparison.Ordinal)) return;
            if (_cacheUid != null && !string.Equals(_cacheUid, uid, StringComparison.Ordinal)) SaveProfileCacheNow();
            _persistedProfiles.Clear();
            _persistedAtUtc.Clear();
            _cacheUid = uid;
            LoadProfileCache();
            _cacheLoadedForUid = uid;
        }
    }

    private string GetCacheFilePath(string uid) =>
        Path.Combine(_configDir, $"profile_cache_{uid}.json");

    private void RemovePersistedProfile(UserData data, string? charName, uint? worldId)
    {
        EnsureCacheLoaded();
        var cacheKey = $"{data.UID}_{charName}_{worldId}";
        if (_persistedProfiles.TryRemove(cacheKey, out _))
        {
            _cacheDirty = true;
            ScheduleCacheSave();
        }
    }

    private void UpdatePersistedProfile(UserData data, string? charName, uint? worldId, UmbraProfileData profile)
    {
        // Une fiche que l'utilisateur a choisi de ne pas afficher n'a pas à être conservée sur son disque.
        if ((profile.IsNSFW && !_mareConfigService.Current.ProfilesAllowNsfw)
            || (profile.IsRpNSFW && !_mareConfigService.Current.ProfilesAllowRpNsfw))
        {
            RemovePersistedProfile(data, charName, worldId);
            return;
        }

        // Une clé incomplète (personnage ou monde inconnus) correspond à une requête faite sans
        // savoir quel personnage afficher : le serveur répond alors par une fiche vide, qui ne
        // doit ni polluer le cache ni écraser la vraie fiche du personnage.
        if (string.IsNullOrEmpty(charName) || worldId is not > 0) return;

        EnsureCacheLoaded();
        var cacheKey = $"{data.UID}_{charName}_{worldId}";
        bool hasRpName = !string.IsNullOrWhiteSpace(profile.RpFirstName) || !string.IsNullOrWhiteSpace(profile.RpLastName);
        if (!hasRpName && _persistedProfiles.Values.Any(e =>
                string.Equals(e.Key.User.UID, data.UID, StringComparison.Ordinal)
                && string.Equals(e.Key.CharName, charName, StringComparison.Ordinal)
                && (!string.IsNullOrWhiteSpace(e.Profile.RpFirstName) || !string.IsNullOrWhiteSpace(e.Profile.RpLastName))))
            return;

        _persistedProfiles[cacheKey] = ((data, charName, worldId), profile);
        _persistedAtUtc[cacheKey] = DateTime.UtcNow;

        foreach (var key in _persistedProfiles.Keys.ToList())
        {
            if (string.Equals(key, cacheKey, StringComparison.Ordinal)) continue;
            if (!_persistedProfiles.TryGetValue(key, out var existing)) continue;
            if (string.Equals(existing.Key.User.UID, data.UID, StringComparison.Ordinal)
                && string.Equals(existing.Key.CharName, charName, StringComparison.Ordinal))
            {
                _persistedProfiles.TryRemove(key, out _);
            }
        }

        _cacheDirty = true;
        ScheduleCacheSave();
    }

    private void ScheduleCacheSave()
    {
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => SaveProfileCacheNow(), null, 3000, Timeout.Infinite);
    }

    private void SaveProfileCacheNow()
    {
        if (!_cacheDirty || _cacheUid == null) return;
        try
        {
            var entries = _persistedProfiles.Select(kvp =>
            {
                var entry = ProfileCacheEntry.FromProfile(kvp.Value.Key.User, kvp.Value.Key.CharName, kvp.Value.Key.WorldId, kvp.Value.Profile);
                entry.CachedAtUtc = _persistedAtUtc.TryGetValue(kvp.Key, out var cachedAt) ? cachedAt : DateTime.UtcNow;
                return entry;
            }).ToList();
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(GetCacheFilePath(_cacheUid), json);
            _cacheDirty = false;
            Logger.LogDebug("Saved {count} profiles to cache for UID {uid}", entries.Count, _cacheUid);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to save profile cache");
        }
    }

    private void LoadProfileCache()
    {
        if (_cacheUid == null) return;
        var path = GetCacheFilePath(_cacheUid);
        try
        {
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<ProfileCacheEntry>>(json);
            if (entries == null) return;

            var now = DateTime.UtcNow;
            int expired = 0;
            foreach (var entry in entries)
            {
                // Les entrées écrites avant l'ajout de la date partent d'aujourd'hui.
                var cachedAt = entry.CachedAtUtc ?? now;
                if (now - cachedAt > PersistedProfileLifetime)
                {
                    expired++;
                    continue;
                }

                var user = new UserData(entry.UID, entry.Alias);
                var profile = entry.ToProfileData();
                var cacheKey = $"{entry.UID}_{entry.CharName}_{entry.WorldId}";
                _persistedProfiles[cacheKey] = ((user, entry.CharName, entry.WorldId), profile);
                _persistedAtUtc[cacheKey] = cachedAt;
            }

            if (expired > 0 || entries.Exists(e => e.CachedAtUtc == null))
            {
                _cacheDirty = true;
                ScheduleCacheSave();
            }

            Logger.LogInformation("Loaded {count} profiles from cache for UID {uid}", entries.Count, _cacheUid);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load profile cache from {path}", path);
        }
    }

    #endregion
    
    private static (UserData User, string? CharName, uint? WorldId) NormalizeKey(UserData data, string? charName, uint? worldId)
        => (new UserData(data.UID), charName, worldId);
}