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
    private string? _cacheUid;
    private bool _cacheDirty;
    private Timer? _saveTimer;

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
            SaveProfileCacheNow();
            _umbraProfiles.Clear();
            _groupProfiles.Clear();
            _persistedProfiles.Clear();
            _cacheUid = null;
        });
        Mediator.Subscribe<GroupProfileUpdatedMessage>(this, (msg) =>
        {
            if (msg.Profile.Group != null)
            {
                _groupProfiles[msg.Profile.Group.GID] = msg.Profile;
            }
        });
        Mediator.Subscribe<ConnectedMessage>(this, (msg) => _ = EnsureOwnProfileSyncedAsync());
    }

    public GroupProfileDto? GetGroupProfile(string gid)
    {
        _groupProfiles.TryGetValue(gid, out var profile);
        return profile;
    }

    public void SetGroupProfile(string gid, GroupProfileDto profile)
    {
        _groupProfiles[gid] = profile;
    }

    public void ClearGroupProfile(string gid)
    {
        _groupProfiles.TryRemove(gid, out _);
    }

    public UmbraProfileData GetUmbraProfile(UserData data)
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

        return GetUmbraProfile(data, charName, worldId);
    }

    public UmbraProfileData GetUmbraProfile(UserData data, string? charName, uint? worldId)
    {
        if (worldId == 0) worldId = null;
        var key = NormalizeKey(data, charName, worldId);
        if (!_umbraProfiles.TryGetValue(key, out var profile))
        {
            _ = Task.Run(() => GetUmbraProfileFromService(data, charName, worldId));
            return (_loadingProfileData);
        }

        return (profile);
    }

    public void SetPreviewProfile(UserData data, string? charName, uint? worldId, UmbraProfileData profileData)
    {
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

            Logger.LogInformation("Profile response for {uid} (charName={charName}, worldId={worldId}): RpFirstName={first}, RpLastName={last}, RpDesc={desc}",
                data.UID, charName ?? "(null)", worldId?.ToString() ?? "(null)",
                profile.RpFirstName ?? "(null)", profile.RpLastName ?? "(null)",
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
                    Logger.LogInformation("Server corrected alt for {uid}: requested {reqChar}@{reqWorld}, got {srvChar}@{srvWorld}",
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

            UmbraProfileData profileData = new(profile.Disabled, profile.IsNSFW ?? false,
                string.IsNullOrEmpty(profile.ProfilePictureBase64) ? string.Empty : profile.ProfilePictureBase64,
                string.IsNullOrEmpty(profile.Description) ? _noDescription : profile.Description,
                profile.RpProfilePictureBase64, profile.RpDescription, profile.IsRpNSFW ?? false,
                profile.RpFirstName, profile.RpLastName, profile.RpTitle, profile.RpAge,
                profile.RpRace, profile.RpEthnicity,
                profile.RpHeight, profile.RpBuild, profile.RpResidence, profile.RpOccupation, profile.RpAffiliation,
                profile.RpAlignment, profile.RpAdditionalInfo, profile.RpNameColor,
                customFields,
                profile.MoodlesData);

            if (_apiController.IsConnected && string.Equals(_apiController.UID, data.UID, StringComparison.Ordinal) && charName != null && worldId != null)
            {
                var localRpProfile = _rpConfigService.GetCharacterProfile(charName, worldId.Value);
                bool changed = false;
                if (!string.Equals(localRpProfile.RpFirstName, profileData.RpFirstName, StringComparison.Ordinal)) { localRpProfile.RpFirstName = profileData.RpFirstName ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpLastName, profileData.RpLastName, StringComparison.Ordinal)) { localRpProfile.RpLastName = profileData.RpLastName ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpTitle, profileData.RpTitle, StringComparison.Ordinal)) { localRpProfile.RpTitle = profileData.RpTitle ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpDescription, profileData.RpDescription, StringComparison.Ordinal)) { localRpProfile.RpDescription = profileData.RpDescription ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpAge, profileData.RpAge, StringComparison.Ordinal)) { localRpProfile.RpAge = profileData.RpAge ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpRace, profileData.RpRace, StringComparison.Ordinal)) { localRpProfile.RpRace = profileData.RpRace ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpEthnicity, profileData.RpEthnicity, StringComparison.Ordinal)) { localRpProfile.RpEthnicity = profileData.RpEthnicity ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpHeight, profileData.RpHeight, StringComparison.Ordinal)) { localRpProfile.RpHeight = profileData.RpHeight ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpBuild, profileData.RpBuild, StringComparison.Ordinal)) { localRpProfile.RpBuild = profileData.RpBuild ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpResidence, profileData.RpResidence, StringComparison.Ordinal)) { localRpProfile.RpResidence = profileData.RpResidence ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpOccupation, profileData.RpOccupation, StringComparison.Ordinal)) { localRpProfile.RpOccupation = profileData.RpOccupation ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpAffiliation, profileData.RpAffiliation, StringComparison.Ordinal)) { localRpProfile.RpAffiliation = profileData.RpAffiliation ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpAlignment, profileData.RpAlignment, StringComparison.Ordinal)) { localRpProfile.RpAlignment = profileData.RpAlignment ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpAdditionalInfo, profileData.RpAdditionalInfo, StringComparison.Ordinal)) { localRpProfile.RpAdditionalInfo = profileData.RpAdditionalInfo ?? string.Empty; changed = true; }
                if (localRpProfile.IsRpNsfw != profileData.IsRpNSFW) { localRpProfile.IsRpNsfw = profileData.IsRpNSFW; changed = true; }
                if (!string.Equals(localRpProfile.RpProfilePictureBase64, profileData.Base64RpProfilePicture, StringComparison.Ordinal)) { localRpProfile.RpProfilePictureBase64 = profileData.Base64RpProfilePicture ?? string.Empty; changed = true; }
                if (!string.Equals(localRpProfile.RpNameColor, profileData.RpNameColor, StringComparison.Ordinal)) { localRpProfile.RpNameColor = profileData.RpNameColor ?? string.Empty; changed = true; }
                var serverCustomFields = profileData.RpCustomFields ?? new List<RpCustomField>();
                if (!CustomFieldsEqual(localRpProfile.RpCustomFields, serverCustomFields)) { localRpProfile.RpCustomFields = serverCustomFields; changed = true; }

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

            bool isSelf = string.Equals(_apiController.UID, data.UID, StringComparison.Ordinal);
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
                UpdatePersistedProfile(data, charName, worldId, profileData);

            Mediator.Publish(new NameplateRedrawMessage());
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to get Profile from service for user {user}", data);
            _umbraProfiles[key] = _defaultProfileData;
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
        EnsureCacheLoaded();
        return _persistedProfiles.Values.ToList().AsReadOnly();
    }

    public void ClearPersistedProfileCache()
    {
        _persistedProfiles.Clear();
        _umbraProfiles.Clear();
        _cacheDirty = true;
        SaveProfileCacheNow();
        Logger.LogInformation("Profile cache cleared by user");
    }
    
    private async Task EnsureOwnProfileSyncedAsync()
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
                await Task.Delay(500).ConfigureAwait(false);
                if (!_apiController.IsConnected) return;
                charName = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false);
                worldId = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(charName) || string.Equals(charName, "--", StringComparison.Ordinal) || worldId == 0)
            {
                Logger.LogWarning("EnsureOwnProfileSynced: Player data unavailable after retries (name={name}, worldId={worldId})", charName, worldId);
                return;
            }

            Logger.LogInformation("EnsureOwnProfileSynced: Fetching full profile from server for {name}@{worldId}", charName, worldId);
            await GetUmbraProfileFromService(new UserData(_apiController.UID), charName, worldId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "EnsureOwnProfileSynced failed");
        }
    }

    #region Persistent Profile Cache

    private void EnsureCacheLoaded()
    {
        if (!_apiController.IsConnected) return;
        var uid = _apiController.UID;
        if (string.Equals(_cacheUid, uid, StringComparison.Ordinal)) return;

        // Save previous UID's cache if any
        if (_cacheUid != null) SaveProfileCacheNow();

        _persistedProfiles.Clear();
        _cacheUid = uid;
        LoadProfileCache();
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
        EnsureCacheLoaded();
        var cacheKey = $"{data.UID}_{charName}_{worldId}";
        _persistedProfiles[cacheKey] = ((data, charName, worldId), profile);

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
            var entries = _persistedProfiles.Values.Select(v =>
                ProfileCacheEntry.FromProfile(v.Key.User, v.Key.CharName, v.Key.WorldId, v.Profile)).ToList();
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

            foreach (var entry in entries)
            {
                var user = new UserData(entry.UID, entry.Alias);
                var profile = entry.ToProfileData();
                var cacheKey = $"{entry.UID}_{entry.CharName}_{entry.WorldId}";
                _persistedProfiles[cacheKey] = ((user, entry.CharName, entry.WorldId), profile);
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

    private static bool CustomFieldsEqual(List<RpCustomField> a, List<RpCustomField> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal) ||
                !string.Equals(a[i].Value, b[i].Value, StringComparison.Ordinal) ||
                a[i].Order != b[i].Order)
                return false;
        }
        return true;
    }
}