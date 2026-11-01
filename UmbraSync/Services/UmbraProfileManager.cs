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

public sealed class UmbraProfileManager : MediatorSubscriberBase, IDisposable
{
    private const string _noDescription = "-- User has no description set --";
    private const string _nsfw = "Profile not displayed - NSFW";
    private const string ImageDirectoryPrefix = "profile_cache_images_";
    private const string TempSuffix = ".tmp";
    private const int FailedProfileRetryMinMs = 30_000;
    private const int FailedProfileRetryMaxMs = 45_000;
    private const string PauseRefusalDescription = "Due to the pause status you cannot access this users profile.";
    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly MareConfigService _mareConfigService;
    private readonly RpConfigService _rpConfigService;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly ProfileImageTextureCache _profileTextures;
    private readonly ConcurrentDictionary<(UserData User, string? CharName, uint? WorldId), UmbraProfileData> _umbraProfiles = new();
    private readonly ConcurrentDictionary<string, GroupProfileDto> _groupProfiles = new(StringComparer.OrdinalIgnoreCase);

    private readonly UmbraProfileData _defaultProfileData = new(IsFlagged: false, IsNSFW: false, string.Empty, _noDescription);
    private readonly UmbraProfileData _loadingProfileData = new(IsFlagged: false, IsNSFW: false, string.Empty, "Loading Data from server...");
    private readonly UmbraProfileData _nsfwProfileData = new(IsFlagged: false, IsNSFW: false, string.Empty, _nsfw);
    private readonly string _configDir;
    private readonly ConcurrentDictionary<string, ((UserData User, string? CharName, uint? WorldId) Key, UmbraProfileData Profile)> _persistedProfiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _persistedAtUtc = new(StringComparer.Ordinal);
    private static readonly TimeSpan PersistedProfileLifetime = TimeSpan.FromDays(30);
    private readonly ConcurrentDictionary<string, LastSeenLiveData> _lastSeenLive = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LastSeenLiveData> _lastSeenPending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _removalsBeforeLoad = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PairLiveDataSeenMessage> _lastSeenBeforeLoad = new(StringComparer.Ordinal);
    private static readonly TimeSpan LastSeenRefreshInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan CacheLoadRetryDelay = TimeSpan.FromMinutes(5);
    private bool _lastSeenDirty;
    private volatile bool _cacheWritesStopped;
    private string? _cacheUid;
    private ProfileImageStore? _imageStore;
    private volatile string? _cacheLoadedForUid;
    private readonly Lock _cacheLoadLock = new();
    private readonly Lock _cacheSaveLock = new();
    private int _cacheGeneration;
    private int _cacheLoadRunning;
    private long _cacheLoadRetryAtTicks;
    private bool _disposed;
    private bool _cacheDirty;
    private Timer? _saveTimer;
    private CancellationTokenSource? _ownProfileSyncCts;
    private int _ownCharactersRequested;

    public string? CurrentUid => _apiController.IsConnected ? _apiController.UID : null;

    public UmbraProfileManager(ILogger<UmbraProfileManager> logger, MareConfigService mareConfigService,
        RpConfigService rpConfigService, MareMediator mediator, ApiController apiController,
        PairManager pairManager, DalamudUtilService dalamudUtil, ServerConfigurationManager serverConfigurationManager,
        ProfileImageTextureCache profileTextures, IDalamudPluginInterface pluginInterface) : base(logger, mediator)
    {
        _profileTextures = profileTextures;
        _mareConfigService = mareConfigService;
        _rpConfigService = rpConfigService;
        _apiController = apiController;
        _pairManager = pairManager;
        _dalamudUtil = dalamudUtil;
        _serverConfigurationManager = serverConfigurationManager;
        _configDir = pluginInterface.ConfigDirectory.FullName;

        Mediator.Subscribe<ClearProfileDataMessage>(this, (msg) =>
        {
            if (msg.UserData == null || string.Equals(msg.UserData.UID, _apiController.UID, StringComparison.Ordinal))
                Interlocked.Exchange(ref _ownCharactersRequested, 0);

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
            Interlocked.Increment(ref _cacheGeneration);
            CancelOwnProfileSync();
            _saveTimer?.Dispose();
            _saveTimer = null;
            SaveProfileCacheNow();
            _umbraProfiles.Clear();
            _profileTextures.Clear();
            _groupProfiles.Clear();
            _persistedProfiles.Clear();
            _lastSeenLive.Clear();
            _lastSeenPending.Clear();
            _lastSeenBeforeLoad.Clear();
            _removalsBeforeLoad.Clear();
            Interlocked.Exchange(ref _cacheLoadRetryAtTicks, 0);
            _cacheUid = null;
            _imageStore = null;
            _cacheLoadedForUid = null;
            Interlocked.Exchange(ref _ownCharactersRequested, 0);
        });
        Mediator.Subscribe<PairLiveDataSeenMessage>(this, RememberLastSeenLiveData);
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
            _cacheWritesStopped = false;
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
                if (_cacheWritesStopped) return;

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

    public LastSeenLiveData? GetLastSeenLiveData(UserData data, string? charName, uint? worldId)
    {
        if (worldId is not > 0) worldId = _serverConfigurationManager.GetWorldIdForUid(data.UID);
        if (string.IsNullOrEmpty(charName) || worldId is not > 0) return null;

        if (!IsCacheLoadedForCurrentUid())
        {
            StartBackgroundCacheLoad();
            return null;
        }

        var key = LastSeenKey(data.UID, charName, worldId.Value);
        if (!_lastSeenLive.ContainsKey(key) && !_lastSeenPending.ContainsKey(key)) return null;
        if (_pairManager.GetPairByUID(data.UID)?.IsPaused == true) return null;

        return IsConfirmedProfile(GetUmbraProfile(data, charName, worldId)) ? _lastSeenLive.GetValueOrDefault(key) : null;
    }

    public bool IsProfileFetchFailed(UmbraProfileData profile) => ReferenceEquals(profile, _defaultProfileData);

    private bool IsConfirmedProfile(UmbraProfileData? profile)
        => profile != null
           && !ReferenceEquals(profile, _loadingProfileData) && !ReferenceEquals(profile, _defaultProfileData)
           && !IsWithheldProfile(profile);

    private bool IsWithheldProfile(UmbraProfileData profile)
        => ReferenceEquals(profile, _nsfwProfileData) || profile.RpVisibility.HasValue || profile.IsFlagged || IsPauseRefusal(profile);

    private static bool IsPauseRefusal(UmbraProfileData profile)
        => string.Equals(profile.Description, PauseRefusalDescription, StringComparison.Ordinal);

    private void UpdateLastSeenConfirmation(UserData data, string? charName, uint? worldId, UmbraProfileData response)
    {
        if (string.IsNullOrEmpty(charName) || worldId is not > 0) return;

        var key = LastSeenKey(data.UID, charName, worldId.Value);
        if (IsConfirmedProfile(response))
        {
            if (!_lastSeenPending.TryRemove(key, out var seen)) return;
            _lastSeenLive[key] = seen;
        }
        else
        {
            _lastSeenPending.TryRemove(key, out _);
            if (!_lastSeenLive.TryRemove(key, out _)) return;
        }

        _lastSeenDirty = true;
        ScheduleCacheSave();
    }

    private bool IsProfileWithheld(UserData data, string charName, uint? worldId)
    {
        return IsWithheld(worldId) || IsWithheld(null);

        bool IsWithheld(uint? keyWorldId)
            => _umbraProfiles.TryGetValue(NormalizeKey(data, charName, keyWorldId), out var known)
               && IsWithheldProfile(known);
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
            var profile = await _apiController.TryUserGetProfile(new API.Dto.User.UserDto(data)
            {
                CharacterName = charName,
                WorldId = worldId
            }).ConfigureAwait(false);
            if (profile == null)
            {
                MarkProfileFetchFailed(key);
                return;
            }

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
                profile.RpBannerBase64,
                profile.RpCardStyle);

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
                if (ShouldReplace(localRpProfile.RpCardStyle, profileData.RpCardStyle)) { localRpProfile.RpCardStyle = profileData.RpCardStyle!; changed = true; }
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

            var effectiveProfile = !isSelf
                && ((profileData.IsNSFW && !_mareConfigService.Current.ProfilesAllowNsfw)
                    || (profileData.IsRpNSFW && !_mareConfigService.Current.ProfilesAllowRpNsfw))
                ? _nsfwProfileData
                : profileData;
            _umbraProfiles[key] = effectiveProfile;

            // Persist to disk cache (not for self)
            if (!isSelf)
            {
                // Le serveur ne renvoie un niveau de visibilité à un autre joueur que lorsque la fiche RP existe
                // mais ne lui est plus accessible : l'ancienne copie conservée sur disque doit disparaître.
                if (profile.RpVisibility.HasValue)
                    RemovePersistedProfile(data, charName, worldId);
                else if (!IsPauseRefusal(profileData))
                    UpdatePersistedProfile(data, charName, worldId, profileData);

                UpdateLastSeenConfirmation(data, charName, worldId, effectiveProfile);

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

    private void MarkProfileFetchFailed((UserData User, string? CharName, uint? WorldId) key)
    {
        _umbraProfiles[key] = _defaultProfileData;
        _ = ForgetFailedProfileAsync(key);
    }

    private async Task ForgetFailedProfileAsync((UserData User, string? CharName, uint? WorldId) key)
    {
        try
        {
            await Task.Delay(Random.Shared.Next(FailedProfileRetryMinMs, FailedProfileRetryMaxMs)).ConfigureAwait(false);
            _umbraProfiles.TryRemove(new KeyValuePair<(UserData User, string? CharName, uint? WorldId), UmbraProfileData>(key, _defaultProfileData));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Nouvel essai d'une fiche en échec impossible");
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
                Base64RpBanner: profile.RpBannerBase64,
                RpCardStyle: profile.RpCardStyle);

            if (!isSelf)
            {
                var effectiveAltProfile = (altProfileData.IsNSFW && !_mareConfigService.Current.ProfilesAllowNsfw)
                    || (altProfileData.IsRpNSFW && !_mareConfigService.Current.ProfilesAllowRpNsfw)
                    ? _nsfwProfileData
                    : altProfileData;
                _umbraProfiles[altKey] = effectiveAltProfile;

                UpdatePersistedProfile(data, altCharName, altWorldId, altProfileData);
                UpdateLastSeenConfirmation(data, altCharName, altWorldId, effectiveAltProfile);
            }
            else
            {
                // Ses propres persos restent en mémoire : jamais de copie disque ni de filtre NSFW.
                _umbraProfiles[altKey] = altProfileData;
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
            if (string.IsNullOrWhiteSpace(entry.Value.Profile.RpFirstName) && string.IsNullOrWhiteSpace(entry.Value.Profile.RpLastName)) continue;
            RemovePersistedProfile(data, entry.Value.Key.CharName, entry.Value.Key.WorldId);
            var normalizedKey = NormalizeKey(data, entry.Value.Key.CharName, entry.Value.Key.WorldId);
            if (!_umbraProfiles.TryGetValue(normalizedKey, out var known) || !IsWithheldProfile(known))
                _umbraProfiles.TryRemove(normalizedKey, out _);
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

    public void RequestOwnCharacterProfiles()
    {
        if (!_apiController.IsConnected || Interlocked.Exchange(ref _ownCharactersRequested, 1) == 1) return;
        _ = Task.Run(LoadOwnCharacterProfilesAsync);
    }

    private async Task LoadOwnCharacterProfilesAsync()
    {
        try
        {
            var uid = _apiController.UID;
            var owned = await _apiController.EstablishmentGetOwnRpProfiles().ConfigureAwait(false);
            var anchor = owned.FirstOrDefault(p => !string.IsNullOrEmpty(p.CharacterName) && p.WorldId > 0);
            if (anchor == null) return;
            await FetchAndCacheAllAltProfiles(new UserData(uid), anchor.CharacterName, anchor.WorldId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load own character profiles");
        }
    }

    public IReadOnlyList<((UserData User, string? CharName, uint? WorldId) Key, UmbraProfileData Profile)> GetOwnCharacterProfiles()
    {
        var uid = CurrentUid;
        if (uid == null) return [];

        return _umbraProfiles
            .Where(p => string.Equals(p.Key.User.UID, uid, StringComparison.Ordinal) && !string.IsNullOrEmpty(p.Key.CharName)
                        && !ReferenceEquals(p.Value, _loadingProfileData) && !ReferenceEquals(p.Value, _defaultProfileData)
                        && !ReferenceEquals(p.Value, _nsfwProfileData))
            .Select(p => (p.Key, p.Value))
            .OrderBy(p => p.Key.CharName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void ClearPersistedProfileCache(bool stopWritesUntilReconnect = false)
    {
        Interlocked.Increment(ref _cacheGeneration);
        if (stopWritesUntilReconnect)
        {
            _imageStore?.Close();
        }

        lock (_cacheLoadLock)
        {
            _persistedProfiles.Clear();
            _persistedAtUtc.Clear();
            _umbraProfiles.Clear();
            _lastSeenLive.Clear();
            _lastSeenPending.Clear();
            _lastSeenBeforeLoad.Clear();
            _removalsBeforeLoad.Clear();
            Interlocked.Exchange(ref _ownCharactersRequested, 0);
            _lastSeenDirty = true;
            _cacheDirty = true;
            SaveProfileCacheNow(force: true);
            if (stopWritesUntilReconnect)
            {
                _cacheWritesStopped = true;
                _imageStore?.Close();
                _diskGroupProfiles.Clear();
                _diskGroupProfileLoads.Clear();
            }

            _imageStore?.Clear();
            _profileTextures.Clear();
            Interlocked.Increment(ref _cacheGeneration);
        }

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
        if (Environment.TickCount64 < Interlocked.Read(ref _cacheLoadRetryAtTicks)) return;
        if (Interlocked.Exchange(ref _cacheLoadRunning, 1) == 1) return;

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
            finally
            {
                Interlocked.Exchange(ref _cacheLoadRunning, 0);
            }
        });
    }

    private void EnsureCacheLoaded()
    {
        if (IsCacheLoadedForCurrentUid()) return;

        lock (_cacheLoadLock)
        {
            if (!_apiController.IsConnected) return;
            var uid = _apiController.UID;
            if (string.Equals(_cacheLoadedForUid, uid, StringComparison.Ordinal)) return;
            if (Environment.TickCount64 < Interlocked.Read(ref _cacheLoadRetryAtTicks)) return;

            if (_cacheUid != null && !string.Equals(_cacheUid, uid, StringComparison.Ordinal)) SaveProfileCacheNow();
            _cacheDirty = false;
            _lastSeenDirty = false;
            var generation = Volatile.Read(ref _cacheGeneration);
            _persistedProfiles.Clear();
            _persistedAtUtc.Clear();
            _lastSeenLive.Clear();
            _lastSeenPending.Clear();
            _cacheUid = uid;
            var store = new ProfileImageStore(Logger, GetImageDirectory(uid));
            _imageStore = store;
            DeleteExpiredCachesOfOtherAccounts(uid);
            if (!LoadProfileCache(uid, store, generation)) return;

            if (!LoadLastSeenLiveData())
            {
                AbandonProfileCacheLoad();
                return;
            }

            if (generation != Volatile.Read(ref _cacheGeneration) || !string.Equals(_cacheUid, uid, StringComparison.Ordinal)) return;

            ApplyRemovalsBeforeLoad();
            _cacheLoadedForUid = uid;
        }

        ReplayLastSeenBeforeLoad();
    }

    private void ApplyRemovalsBeforeLoad()
    {
        foreach (var key in _removalsBeforeLoad.Keys.ToList())
        {
            if (!_removalsBeforeLoad.TryRemove(key, out _)) continue;
            _persistedProfiles.TryRemove(key, out _);
            _lastSeenPending.TryRemove(key, out _);
            _lastSeenLive.TryRemove(key, out _);
            _cacheDirty = true;
            _lastSeenDirty = true;
            ScheduleCacheSave();
        }
    }

    private void ReplayLastSeenBeforeLoad()
    {
        foreach (var key in _lastSeenBeforeLoad.Keys.ToList())
        {
            if (_lastSeenBeforeLoad.TryRemove(key, out var message)) RememberLastSeenLiveData(message);
        }
    }

    private string GetCacheFilePath(string uid) =>
        Path.Combine(_configDir, $"profile_cache_{uid}.json");

    private string GetImageDirectory(string uid) =>
        Path.Combine(_configDir, $"{ImageDirectoryPrefix}{uid}");

    private void DeleteExpiredCachesOfOtherAccounts(string uid)
    {
        try
        {
            var now = DateTime.UtcNow;
            var ownFiles = new[] { GetCacheFilePath(uid), GetLastSeenFilePath(uid), GetCacheFilePath(uid) + TempSuffix, GetLastSeenFilePath(uid) + TempSuffix };
            foreach (var file in Directory.EnumerateFiles(_configDir, "profile_cache_*.json*"))
            {
                if (ownFiles.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
                if (now - File.GetLastWriteTimeUtc(file) > PersistedProfileLifetime) TryDeleteFile(file);
            }

            foreach (var directory in Directory.EnumerateDirectories(_configDir, $"{ImageDirectoryPrefix}*"))
            {
                var owner = Path.GetFileName(directory)[ImageDirectoryPrefix.Length..];
                if (string.Equals(owner, uid, StringComparison.OrdinalIgnoreCase) || File.Exists(GetCacheFilePath(owner))) continue;
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogDebug(ex, "Nettoyage des caches d'autres comptes impossible");
        }
    }

    private void RemovePersistedProfile(UserData data, string? charName, uint? worldId)
    {
        EnsureCacheLoaded();
        var cacheKey = $"{data.UID}_{charName}_{worldId}";
        if (_cacheUid == null || !string.Equals(_cacheLoadedForUid, _cacheUid, StringComparison.Ordinal))
        {
            _removalsBeforeLoad[cacheKey] = 0;
            return;
        }

        _lastSeenPending.TryRemove(cacheKey, out _);
        if (_lastSeenLive.TryRemove(cacheKey, out _))
        {
            _lastSeenDirty = true;
            ScheduleCacheSave();
        }
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
        if (string.IsNullOrEmpty(charName) || worldId is not > 0 || _cacheWritesStopped) return;

        var generation = Volatile.Read(ref _cacheGeneration);
        EnsureCacheLoaded();
        if (_cacheWritesStopped || !IsCacheLoadedForCurrentUid() || _imageStore is not { } store) return;

        var cacheKey = $"{data.UID}_{charName}_{worldId}";
        bool hasRpName = !string.IsNullOrWhiteSpace(profile.RpFirstName) || !string.IsNullOrWhiteSpace(profile.RpLastName);
        if (!hasRpName && _persistedProfiles.Values.Any(e =>
                string.Equals(e.Key.User.UID, data.UID, StringComparison.Ordinal)
                && string.Equals(e.Key.CharName, charName, StringComparison.Ordinal)
                && (!string.IsNullOrWhiteSpace(e.Profile.RpFirstName) || !string.IsNullOrWhiteSpace(e.Profile.RpLastName))))
            return;

        var stored = profile.WithImagesStoredIn(store);
        if (stored.ImageData.IsAwaitingStorage || stored.RpImageData.IsAwaitingStorage || stored.RpBannerData.IsAwaitingStorage) return;

        lock (_cacheLoadLock)
        {
            if (generation != Volatile.Read(ref _cacheGeneration) || _cacheWritesStopped) return;

            if (!_umbraProfiles.TryUpdate(NormalizeKey(data, charName, worldId), stored, profile)) return;

            _profileTextures.Rekey(profile.ImageData, stored.ImageData);
            _profileTextures.Rekey(profile.RpImageData, stored.RpImageData);
            _profileTextures.Rekey(profile.RpBannerData, stored.RpBannerData);
            _persistedProfiles[cacheKey] = ((data, charName, worldId), stored);
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
    }

    private void ScheduleCacheSave()
    {
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => SaveProfileCacheNow(), null, 3000, Timeout.Infinite);
    }

    private void SaveProfileCacheNow(bool force = false)
    {
        lock (_cacheSaveLock)
        {
            var uid = _cacheUid;
            if (uid == null || (!force && !string.Equals(_cacheLoadedForUid, uid, StringComparison.Ordinal))) return;
            SaveLastSeenLiveData();
            if (!_cacheDirty || _cacheWritesStopped) return;
            try
            {
                _cacheDirty = false;
                var entries = _persistedProfiles.Select(kvp =>
                {
                    var entry = ProfileCacheEntry.FromProfile(kvp.Value.Key.User, kvp.Value.Key.CharName, kvp.Value.Key.WorldId, kvp.Value.Profile);
                    entry.CachedAtUtc = _persistedAtUtc.TryGetValue(kvp.Key, out var cachedAt) ? cachedAt : DateTime.UtcNow;
                    return entry;
                }).ToList();
                WriteAtomic(GetCacheFilePath(uid), JsonSerializer.Serialize(entries));
                _imageStore?.RetainOnly(ReferencedHashes(entries));
                Logger.LogDebug("Saved {count} profiles to cache for UID {uid}", entries.Count, uid);
            }
            catch (Exception ex)
            {
                _cacheDirty = true;
                Logger.LogWarning(ex, "Failed to save profile cache");
            }
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + TempSuffix;
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    private static HashSet<string> ReferencedHashes(IEnumerable<ProfileCacheEntry> entries)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.ProfilePictureHash != null) hashes.Add(entry.ProfilePictureHash);
            if (entry.RpProfilePictureHash != null) hashes.Add(entry.RpProfilePictureHash);
            if (entry.RpBannerHash != null) hashes.Add(entry.RpBannerHash);
        }

        return hashes;
    }

    private bool LoadProfileCache(string uid, ProfileImageStore store, int generation)
    {
        var path = GetCacheFilePath(uid);
        try
        {
            if (!File.Exists(path))
            {
                store.RetainOnly(new HashSet<string>(StringComparer.Ordinal));
                return true;
            }

            List<ProfileCacheEntry>? entries;
            using (var stream = File.OpenRead(path))
                entries = JsonSerializer.Deserialize<List<ProfileCacheEntry>>(stream);
            if (entries == null) return true;

            var now = DateTime.UtcNow;
            int expired = 0;
            int migrated = 0;
            var kept = new List<ProfileCacheEntry>(entries.Count);
            foreach (var entry in entries)
            {
                if (generation != Volatile.Read(ref _cacheGeneration))
                {
                    AbandonProfileCacheLoad();
                    return false;
                }

                // Les entrées écrites avant l'ajout de la date partent d'aujourd'hui.
                var cachedAt = entry.CachedAtUtc ?? now;
                if (now - cachedAt > PersistedProfileLifetime)
                {
                    expired++;
                    continue;
                }

                if (entry.MoveLegacyImagesTo(store)) migrated++;
                kept.Add(entry);

                var user = new UserData(entry.UID, entry.Alias);
                var profile = entry.ToProfileData(store);
                var cacheKey = $"{entry.UID}_{entry.CharName}_{entry.WorldId}";
                _persistedProfiles[cacheKey] = ((user, entry.CharName, entry.WorldId), profile);
                _persistedAtUtc[cacheKey] = cachedAt;
            }

            if (generation != Volatile.Read(ref _cacheGeneration))
            {
                AbandonProfileCacheLoad();
                return false;
            }

            store.RetainOnly(ReferencedHashes(kept));

            if (migrated > 0)
            {
                Logger.LogInformation("Migrated images of {count} cached profiles for UID {uid}", migrated, uid);
                foreach (var entry in kept) entry.CachedAtUtc ??= now;
                WriteMigratedCache(uid, kept, generation);
            }
            else if (expired > 0 || entries.Exists(e => e.CachedAtUtc == null))
            {
                _cacheDirty = true;
                ScheduleCacheSave();
            }

            Logger.LogInformation("Loaded {count} profiles from cache for UID {uid}", entries.Count, uid);
            return true;
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Profile cache at {path} is unreadable, starting empty", path);
            AbandonProfileCacheLoad();
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load profile cache from {path}", path);
            AbandonProfileCacheLoad();
            DelayNextCacheLoad();
            return false;
        }
    }

    private void WriteMigratedCache(string uid, List<ProfileCacheEntry> entries, int generation)
    {
        lock (_cacheSaveLock)
        {
            if (generation != Volatile.Read(ref _cacheGeneration)) return;
            try
            {
                WriteAtomic(GetCacheFilePath(uid), JsonSerializer.Serialize(entries));
                _cacheDirty = false;
            }
            catch (Exception ex)
            {
                _cacheDirty = true;
                Logger.LogWarning(ex, "Failed to save migrated profile cache");
            }
        }
    }

    private void DelayNextCacheLoad()
        => Interlocked.Exchange(ref _cacheLoadRetryAtTicks, Environment.TickCount64 + (long)CacheLoadRetryDelay.TotalMilliseconds);

    private void AbandonProfileCacheLoad()
    {
        _persistedProfiles.Clear();
        _persistedAtUtc.Clear();
        _cacheDirty = false;
    }

    private static string LastSeenKey(string uid, string charName, uint worldId) => $"{uid}_{charName}_{worldId}";

    private string GetLastSeenFilePath(string uid) =>
        Path.Combine(_configDir, $"profile_cache_lastseen_{uid}.json");

    private void RememberLastSeenLiveData(PairLiveDataSeenMessage msg)
    {
        if (_cacheWritesStopped || string.IsNullOrEmpty(msg.CharName) || msg.WorldId == 0 || !_apiController.IsConnected) return;

        var key = LastSeenKey(msg.User.UID, msg.CharName, msg.WorldId);
        if (!IsCacheLoadedForCurrentUid())
        {
            _lastSeenBeforeLoad[key] = msg;
            StartBackgroundCacheLoad();
            return;
        }

        if (IsProfileWithheld(msg.User, msg.CharName, msg.WorldId)
            || _pairManager.GetPairByUID(msg.User.UID)?.IsPaused == true
            || (string.IsNullOrEmpty(msg.HonorificData) && string.IsNullOrEmpty(msg.MoodlesData)))
        {
            _lastSeenPending.TryRemove(key, out _);
            if (!_lastSeenLive.TryRemove(key, out _)) return;
        }
        else
        {
            var now = DateTime.UtcNow;
            if (_lastSeenLive.TryGetValue(key, out var known)
                && string.Equals(known.HonorificData, msg.HonorificData, StringComparison.Ordinal)
                && string.Equals(known.MoodlesData, msg.MoodlesData, StringComparison.Ordinal)
                && now - known.SeenAtUtc < LastSeenRefreshInterval)
            {
                return;
            }

            var seen = new LastSeenLiveData(msg.User.UID, msg.CharName, msg.WorldId, msg.HonorificData, msg.MoodlesData, now);
            var current = _umbraProfiles.GetValueOrDefault(NormalizeKey(msg.User, msg.CharName, msg.WorldId));
            var confirmed = current == null || ReferenceEquals(current, _defaultProfileData)
                ? _persistedProfiles.ContainsKey(key)
                : IsConfirmedProfile(current);
            if (!confirmed)
            {
                _lastSeenPending[key] = seen;
                return;
            }

            _lastSeenPending.TryRemove(key, out _);
            _lastSeenLive[key] = seen;
        }

        _lastSeenDirty = true;
        ScheduleCacheSave();
    }

    private void SaveLastSeenLiveData()
    {
        if (!_lastSeenDirty || _cacheWritesStopped || _cacheUid == null) return;
        try
        {
            _lastSeenDirty = false;
            WriteAtomic(GetLastSeenFilePath(_cacheUid), JsonSerializer.Serialize(_lastSeenLive.Values.ToList()));
        }
        catch (Exception ex)
        {
            _lastSeenDirty = true;
            Logger.LogWarning(ex, "Failed to save last seen live data");
        }
    }

    private bool LoadLastSeenLiveData()
    {
        if (_cacheUid == null) return false;
        var path = GetLastSeenFilePath(_cacheUid);
        try
        {
            if (!File.Exists(path)) return true;
            var entries = JsonSerializer.Deserialize<List<LastSeenLiveData>>(File.ReadAllText(path));
            if (entries == null) return true;

            var now = DateTime.UtcNow;
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.UID) || string.IsNullOrEmpty(entry.CharName) || entry.WorldId == 0
                    || now - entry.SeenAtUtc > PersistedProfileLifetime)
                {
                    _lastSeenDirty = true;
                    continue;
                }

                _lastSeenLive[LastSeenKey(entry.UID, entry.CharName, entry.WorldId)] = entry;
            }

            if (_lastSeenDirty) ScheduleCacheSave();
            return true;
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Last seen live data at {path} is unreadable, starting empty", path);
            _lastSeenLive.Clear();
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load last seen live data from {path}", path);
            _lastSeenLive.Clear();
            DelayNextCacheLoad();
            return false;
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Interlocked.Increment(ref _cacheGeneration);
        try
        {
            UnsubscribeAll();
            CancelOwnProfileSync();
            _saveTimer?.Dispose();
            SaveProfileCacheNow();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Arrêt du gestionnaire de fiches incomplet");
        }
    }

    private static (UserData User, string? CharName, uint? WorldId) NormalizeKey(UserData data, string? charName, uint? worldId)
        => (new UserData(data.UID), charName, worldId);
}