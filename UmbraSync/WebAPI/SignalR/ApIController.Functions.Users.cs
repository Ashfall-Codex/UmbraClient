using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Dto.User;

namespace UmbraSync.WebAPI.SignalR;

public partial class ApiController
{
    public bool IsProfileNsfw { get; set; }

    public async Task<bool> PushCharacterData(CharacterData data, List<UserData> visibleCharacters, CancellationToken ct = default)
    {
        if (!IsConnected) return false;

        try
        {
            Logger.LogDebug("Pushing Character data {hash} to {visible}", data.DataHash, string.Join(", ", visibleCharacters.Select(v => v.AliasOrUID)));
            return await PushCharacterDataInternal(SanitizeForPush(data), [.. visibleCharacters], ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("Push of character data was cancelled");
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during push of character data");
            return false;
        }
    }

    // Même validation que le serveur (MareHub.User.UserPushData) : une seule entrée invalide
    // y fait rejeter tout le push, on retire donc ici les entrées qu'il refuserait.
    private CharacterData SanitizeForPush(CharacterData data)
    {
        bool changed = false;
        Dictionary<ObjectKind, List<FileReplacementData>> sanitized = [];
        foreach (var (kind, replacements) in data.FileReplacements)
        {
            List<FileReplacementData> kept = new(replacements.Count);
            foreach (var replacement in replacements)
            {
                var validGamePaths = replacement.GamePaths.Where(IsValidPushGamePath).ToArray();
                bool validHash = string.IsNullOrEmpty(replacement.Hash) || SafeIsMatch(PushHashRegex(), replacement.Hash);
                bool validFileSwapPath = string.IsNullOrEmpty(replacement.FileSwapPath) || SafeIsMatch(PushGamePathRegex(), replacement.FileSwapPath);

                if (validGamePaths.Length == replacement.GamePaths.Length && validHash && validFileSwapPath)
                {
                    kept.Add(replacement);
                    continue;
                }

                changed = true;
                LogInvalidPushEntry(replacement, validHash, validFileSwapPath);
                if (validGamePaths.Length == 0 || !validHash || !validFileSwapPath) continue;

                kept.Add(new FileReplacementData
                {
                    GamePaths = validGamePaths,
                    Hash = replacement.Hash,
                    FileSwapPath = replacement.FileSwapPath,
                });
            }

            sanitized[kind] = kept;
        }

        if (!changed) return data;

        return new CharacterData
        {
            FileReplacements = sanitized,
            GlamourerData = data.GlamourerData,
            ManipulationData = data.ManipulationData,
            HeelsData = data.HeelsData,
            CustomizePlusData = data.CustomizePlusData,
            HonorificData = data.HonorificData,
            PetNamesData = data.PetNamesData,
            MoodlesData = data.MoodlesData,
        };
    }

    private static bool IsValidPushGamePath(string gamePath)
    {
        return SafeIsMatch(PushGamePathRegex(), gamePath)
            && PushAllowedExtensions.Any(e => gamePath.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SafeIsMatch(Regex regex, string input)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private void LogInvalidPushEntry(FileReplacementData replacement, bool validHash, bool validFileSwapPath)
    {
        var key = string.Join('|', replacement.GamePaths) + "|" + replacement.Hash + "|" + replacement.FileSwapPath;
        bool firstTime;
        lock (_reportedInvalidPushEntries)
        {
            firstTime = _reportedInvalidPushEntries.Count < 1000 && _reportedInvalidPushEntries.Add(key);
        }

        var invalidPaths = string.Join(", ", replacement.GamePaths.Where(p => !IsValidPushGamePath(p)));
        if (firstTime)
        {
            Logger.LogWarning("Entrée de mod refusée par le serveur, retirée du push : chemins invalides [{paths}], hash valide {validHash} ({hash}), swap valide {validSwap} ({swap})",
                invalidPaths, validHash, replacement.Hash, validFileSwapPath, replacement.FileSwapPath);
        }
        else
        {
            Logger.LogDebug("Entrée de mod invalide retirée du push : [{paths}]", invalidPaths);
        }
    }

    // InvokeAsync : l'appelant apprend l'échec (déconnexion, erreur serveur) au lieu d'annoncer un faux succès
    public async Task UserAddPair(UserDto user)
    {
        if (!IsConnected) return;
        await _mareHub!.SendAsync(nameof(UserAddPair), user).ConfigureAwait(false);
    }


    public async Task UserDelete()
    {
        CheckConnection();
        await _mareHub!.SendAsync(nameof(UserDelete)).ConfigureAwait(false);
        await CreateConnections().ConfigureAwait(false);
    }

    public async Task<List<OnlineUserIdentDto>> UserGetOnlinePairs()
    {
        return await _mareHub!.InvokeAsync<List<OnlineUserIdentDto>>(nameof(UserGetOnlinePairs)).ConfigureAwait(false);
    }

    public async Task<List<UserFullPairDto>> UserGetPairedClients()
    {
        return await _mareHub!.InvokeAsync<List<UserFullPairDto>>(nameof(UserGetPairedClients)).ConfigureAwait(false);
    }

    public async Task UserUpdateDefaultPermissions(DefaultPermissionsDto defaultPermissions)
    {
        if (!IsConnected) return;
        await _mareHub!.SendAsync(nameof(UserUpdateDefaultPermissions), defaultPermissions).ConfigureAwait(false);
    }

    public async Task SetBulkPermissions(BulkPermissionsDto bulkPermissions)
    {
        if (!IsConnected) return;
        await _mareHub!.SendAsync(nameof(SetBulkPermissions), bulkPermissions).ConfigureAwait(false);
    }

    public async Task<UserProfileDto> UserGetProfile(UserDto dto)
    {
        if (!IsConnected)
        {
            Logger.LogTrace("UserGetProfile: Not connected, returning empty profile");
            return new UserProfileDto(dto.User, false, null, null, null);
        }

        try
        {
            Logger.LogTrace("Fetching profile for {uid}", dto.User.UID);
            var result = await _mareHub!.InvokeAsync<UserProfileDto>(nameof(UserGetProfile), dto).ConfigureAwait(false);
            Logger.LogTrace("Profile fetched successfully for {uid}", dto.User.UID);
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error fetching profile for {uid}", dto.User.UID);
            return new UserProfileDto(dto.User, false, null, null, null);
        }
    }
    public async Task<List<UserProfileDto>> UserGetAllCharacterProfiles(UserDto dto)
    {
        if (!IsConnected)
        {
            Logger.LogTrace("UserGetAllCharacterProfiles: Not connected, returning empty list");
            return [];
        }

        try
        {
            Logger.LogTrace("Fetching all character profiles for {uid}", dto.User.UID);
            var result = await _mareHub!.InvokeAsync<List<UserProfileDto>>(nameof(UserGetAllCharacterProfiles), dto).ConfigureAwait(false);
            Logger.LogTrace("Fetched {count} character profiles for {uid}", result.Count, dto.User.UID);
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error fetching all character profiles for {uid}", dto.User.UID);
            return [];
        }
    }

    public async Task UserPushData(UserCharaDataMessageDto dto)
    {
        await TryUserPushData(dto, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> TryUserPushData(UserCharaDataMessageDto dto, CancellationToken ct)
    {
        if (_mareHub == null) return false;

        try
        {
            await _mareHub.InvokeAsync(nameof(UserPushData), dto, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to Push character data");
            return false;
        }
    }

    public async Task UserRemovePair(UserDto userDto)
    {
        if (!IsConnected) return;
        await _mareHub!.SendAsync(nameof(UserRemovePair), userDto).ConfigureAwait(false);
    }

    public async Task UserReportProfile(UserProfileReportDto userDto)
    {
        if (!IsConnected) return;
        await _mareHub!.SendAsync(nameof(UserReportProfile), userDto).ConfigureAwait(false);
    }

    public async Task UserSetPairPermissions(UserPermissionsDto userPermissions)
    {
        await _mareHub!.SendAsync(nameof(UserSetPairPermissions), userPermissions).ConfigureAwait(false);
    }

    public async Task UserSetAlias(string? alias)
    {
        if (!IsConnected)
        {
            Logger.LogWarning("Cannot set alias: Not connected to server");
            throw new InvalidOperationException("Not connected to server");
        }

        try
        {
            Logger.LogInformation("Sending UserSetAlias to server ({action})", alias == null ? "clearing" : "setting");
            await _mareHub!.InvokeAsync(nameof(UserSetAlias), alias).ConfigureAwait(false);
            Logger.LogInformation("UserSetAlias successfully sent to server");
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Error during UserSetAlias: {Message}", ex.Message);
            throw new InvalidOperationException("Failed to set user alias", ex);
        }
    }

    public async Task UserSetProfile(UserProfileDto userDescription)
    {
        if (!IsConnected)
        {
            Logger.LogWarning("Cannot set profile: Not connected to server");
            return;
        }

        try
        {
            Logger.LogInformation("Sending UserSetProfile to server for {uid}", userDescription.User.UID);
            await _mareHub!.InvokeAsync(nameof(UserSetProfile), userDescription).ConfigureAwait(false);
            Logger.LogInformation("UserSetProfile successfully sent to server");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error during UserSetProfile for {userDescription.User.UID}", ex);
        }
    }


    public async Task UserSetTypingState(bool isTyping)
    {
        CheckConnection();
        await _mareHub!.SendAsync(nameof(UserSetTypingState), isTyping).ConfigureAwait(false);
    }

    public async Task UserSetTypingState(bool isTyping, UmbraSync.API.Data.Enum.TypingScope scope)
    {
        CheckConnection();
        try
        {
            await _mareHub!.SendAsync(nameof(UserSetTypingState), isTyping, scope).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // fallback for older servers without scope support
            Logger.LogDebug(ex, "UserSetTypingState(scope) not supported on server, falling back to legacy call");
            await _mareHub!.SendAsync(nameof(UserSetTypingState), isTyping).ConfigureAwait(false);
        }
    }

    public async Task UserSetTypingStateEx(TypingStateExDto dto)
    {
        CheckConnection();
        try
        {
            await _mareHub!.SendAsync(nameof(UserSetTypingStateEx), dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fallback to scoped/legacy APIs
            Logger.LogDebug(ex, "UserSetTypingStateEx not supported on server, falling back to scoped/legacy call");
            try
            {
                await UserSetTypingState(dto.IsTyping, dto.Scope).ConfigureAwait(false);
            }
            catch (Exception ex2)
            {
                Logger.LogDebug(ex2, "UserSetTypingStateEx fallback failed");
            }
        }
    }

    public async Task UserUpdateTypingChannels(TypingChannelsDto channels)
    {
        CheckConnection();
        try
        {
            await _mareHub!.SendAsync(nameof(UserUpdateTypingChannels), channels).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Older servers won't have this method; silently ignore
            Logger.LogTrace(ex, "UserUpdateTypingChannels not supported on server (ignored)");
        }
    }

    private async Task<bool> PushCharacterDataInternal(CharacterData character, List<UserData> visibleCharacters, CancellationToken ct)
    {
        Logger.LogInformation("Pushing character data for {hash} to {count} visible pair(s)", character.DataHash.Value, visibleCharacters.Count);
        StringBuilder sb = new();
        foreach (var kvp in character.FileReplacements.ToList())
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "FileReplacements for {0}: {1}", kvp.Key, kvp.Value.Count));
        }
        foreach (var item in character.GlamourerData)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "GlamourerData for {0}: {1}", item.Key, !string.IsNullOrEmpty(item.Value)));
        }
        Logger.LogDebug("Chara data contained: {nl} {data}", Environment.NewLine, sb.ToString());

        return await TryUserPushData(new(visibleCharacters, character), ct).ConfigureAwait(false);
    }

    private static readonly string[] PushAllowedExtensions = [".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".pbd", ".scd", ".skp", ".shpk", ".kdb"];
    private readonly HashSet<string> _reportedInvalidPushEntries = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^([a-z0-9_ '+&,\.\-\{\}]+\/)+([a-z0-9_ '+&,\.\-\{\}]+\.[a-z]{3,4})$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ECMAScript, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PushGamePathRegex();

    [GeneratedRegex(@"^[A-Z0-9]{40}$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ECMAScript, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PushHashRegex();
}