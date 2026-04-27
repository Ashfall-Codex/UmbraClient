using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.User;

namespace UmbraSync.WebAPI.SignalR;

public partial class ApiController
{
    public bool IsProfileNsfw { get; set; }

    public async Task PushCharacterData(CharacterData data, List<UserData> visibleCharacters)
    {
        if (!IsConnected) return;

        try
        {
            Logger.LogDebug("Pushing Character data {hash} to {visible}", data.DataHash, string.Join(", ", visibleCharacters.Select(v => v.AliasOrUID)));
            await PushCharacterDataInternal(data, [.. visibleCharacters]).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("Upload operation was cancelled");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during upload of files");
        }
    }

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

    public async Task UserUpdateDefaultPermissions(DefaultPermissionsDto dto)
    {
        if (!IsConnected) return;
        await _mareHub!.SendAsync(nameof(UserUpdateDefaultPermissions), dto).ConfigureAwait(false);
    }

    public async Task SetBulkPermissions(BulkPermissionsDto dto)
    {
        if (!IsConnected) return;
        await _mareHub!.SendAsync(nameof(SetBulkPermissions), dto).ConfigureAwait(false);
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
        try
        {
            await _mareHub!.InvokeAsync(nameof(UserPushData), dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to Push character data");
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
            Logger.LogInformation("Sending UserSetAlias to server. Alias: {alias}", alias ?? "(null/clearing)");
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
            var json = System.Text.Json.JsonSerializer.Serialize(userDescription, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Logger.LogInformation("Sending UserSetProfile to server for {uid}. Data: {json}", userDescription.User.UID, json);
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

    private async Task PushCharacterDataInternal(CharacterData character, List<UserData> visibleCharacters)
    {
        Logger.LogInformation("Pushing character data for {hash} to {charas}", character.DataHash.Value, string.Join(", ", visibleCharacters.Select(c => c.AliasOrUID)));
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

        await UserPushData(new(visibleCharacters, character)).ConfigureAwait(false);
    }
}