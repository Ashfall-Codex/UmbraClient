using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Dto;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.API.Dto.Group;
using UmbraSync.API.Dto.User;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;

namespace UmbraSync.WebAPI.SignalR;

public partial class ApiController
{
    public Task Client_DownloadReady(Guid requestId)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Server sent {requestId} ready", requestId);
        Mediator.Publish(new DownloadReadyMessage(requestId));
        return Task.CompletedTask;
    }

    public Task Client_GroupChangePermissions(GroupPermissionDto groupPermission)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("Client_GroupChangePermissions: {gid}", groupPermission.Group.GID);
        ExecuteSafely(() => _pairManager.SetGroupPermissions(groupPermission));
        return Task.CompletedTask;
    }

    public Task Client_GroupPairChangePermissions(GroupPairUserPermissionDto permissionDto)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("Client_GroupPairChangePermissions: {gid} for {uid}", permissionDto.Group.GID, permissionDto.User.UID);
        ExecuteSafely(() =>
        {
            if (string.Equals(permissionDto.UID, UID, StringComparison.Ordinal))
                _pairManager.SetGroupUserPermissions(permissionDto);
            else
                _pairManager.SetGroupPairUserPermissions(permissionDto);
        });
        return Task.CompletedTask;
    }

    public Task Client_GroupDelete(GroupDto groupDto)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("Client_GroupDelete: {gid}", groupDto.Group.GID);
        ExecuteSafely(() => _pairManager.RemoveGroup(groupDto.Group));
        return Task.CompletedTask;
    }

    public Task Client_GroupPairChangeUserInfo(GroupPairUserInfoDto userInfo)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("Client_GroupPairChangeUserInfo: {gid} for {uid}", userInfo.Group.GID, userInfo.User.UID);
        ExecuteSafely(() =>
        {
            if (string.Equals(userInfo.UID, UID, StringComparison.Ordinal)) _pairManager.SetGroupStatusInfo(userInfo);
            else _pairManager.SetGroupPairStatusInfo(userInfo);
        });
        return Task.CompletedTask;
    }

    public Task Client_GroupPairJoined(GroupPairFullInfoDto groupPairInfoDto)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("Client_GroupPairJoined: {gid} for {uid}", groupPairInfoDto.Group.GID, groupPairInfoDto.User.UID);
        ExecuteSafely(() => _pairManager.AddGroupPair(groupPairInfoDto));
        return Task.CompletedTask;
    }

    public Task Client_GroupPairLeft(GroupPairDto groupPairDto)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("Client_GroupPairLeft: {gid} for {uid}", groupPairDto.Group.GID, groupPairDto.User.UID);
        ExecuteSafely(() => _pairManager.RemoveGroupPair(groupPairDto));
        if (string.Equals(groupPairDto.User.UID, _connectionDto?.User.UID, StringComparison.Ordinal))
        {
            Mediator.Publish(new GroupLeftMessage(groupPairDto.Group.GID));
        }
        return Task.CompletedTask;
    }

    public Task Client_GroupSendFullInfo(GroupFullInfoDto groupInfo)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("Client_GroupSendFullInfo: {gid}", groupInfo.Group.GID);
        ExecuteSafely(() => _pairManager.AddGroup(groupInfo));
        return Task.CompletedTask;
    }

    public Task Client_GroupSendInfo(GroupInfoDto groupInfo)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("Client_GroupSendInfo: {gid}", groupInfo.Group.GID);
        ExecuteSafely(() => _pairManager.SetGroupInfo(groupInfo));
        return Task.CompletedTask;
    }

    public Task Client_ReceiveServerMessage(MessageSeverity messageSeverity, string message)
    {
        if (ServerState is Utils.ServerState.Offline or Utils.ServerState.Disconnected or Utils.ServerState.Disconnecting)
            return Task.CompletedTask;

        switch (messageSeverity)
        {
            case MessageSeverity.Error:
                Mediator.Publish(new NotificationMessage("Warning from " + _serverManager.CurrentServer!.ServerName, message, NotificationType.Error, TimeSpan.FromSeconds(7.5)));
                _notificationTracker.Upsert(NotificationEntry.ServerMessageError(_serverManager.CurrentServer!.ServerName, message));
                break;

            case MessageSeverity.Warning:
                Mediator.Publish(new NotificationMessage("Warning from " + _serverManager.CurrentServer!.ServerName, message, NotificationType.Warning, TimeSpan.FromSeconds(7.5)));
                _notificationTracker.Upsert(NotificationEntry.ServerMessageWarning(_serverManager.CurrentServer!.ServerName, message));
                break;

            case MessageSeverity.Information:
                if (_doNotNotifyOnNextInfo)
                {
                    _doNotNotifyOnNextInfo = false;
                    break;
                }
                Mediator.Publish(new NotificationMessage("Info from " + _serverManager.CurrentServer!.ServerName, message, NotificationType.Info, TimeSpan.FromSeconds(5)));
                break;
        }

        return Task.CompletedTask;
    }

    public Task Client_UpdateSystemInfo(SystemInfoDto systemInfo)
    {
        SystemInfoDto = systemInfo;
        return Task.CompletedTask;
    }

    public Task Client_UserAddClientPair(UserPairDto dto)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Client_UserAddClientPair: {uid}", dto.User.UID);
        ExecuteSafely(() => _pairManager.AddUserPair(dto, addToLastAddedUser: true));
        return Task.CompletedTask;
    }

    public Task Client_UserTypingState(TypingStateDto dto)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("Client_UserTypingState: {uid} typing={typing}", dto.User.UID, dto.IsTyping);
        Mediator.Publish(new UserTypingStateMessage(dto));
        return Task.CompletedTask;
    }

    public Task Client_UserReceiveCharacterData(OnlineUserCharaDataDto dataDto)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("Client_UserReceiveCharacterData: {uid}", dataDto.User.UID);
        ExecuteSafely(() => _pairManager.ReceiveCharaData(dataDto));
        return Task.CompletedTask;
    }

    public Task Client_UserReceiveUploadStatus(UserDto dto)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("Client_UserReceiveUploadStatus: {uid}", dto.User.UID);
        ExecuteSafely(() => _pairManager.ReceiveUploadStatus(dto));
        return Task.CompletedTask;
    }

    public Task Client_UserRemoveClientPair(UserDto dto)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Client_UserRemoveClientPair: {uid}", dto.User.UID);
        ExecuteSafely(() => _pairManager.RemoveUserPair(dto));
        return Task.CompletedTask;
    }

    public Task Client_UserSendOffline(UserDto dto)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Client_UserSendOffline: {uid}", dto.User.UID);
        ExecuteSafely(() => _pairManager.MarkPairOffline(dto.User));
        return Task.CompletedTask;
    }

    public Task Client_UserSendOnline(OnlineUserIdentDto dto)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Client_UserSendOnline: {uid}", dto.User.UID);
        ExecuteSafely(() => _pairManager.MarkPairOnline(dto));
        return Task.CompletedTask;
    }

    public Task Client_UserUpdateOtherPairPermissions(UserPermissionsDto dto)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Client_UserUpdateOtherPairPermissions: {uid}", dto.User.UID);
        ExecuteSafely(() => _pairManager.UpdatePairPermissions(dto));
        return Task.CompletedTask;
    }

    public Task Client_UserUpdateProfile(UserDto dto)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Client_UserUpdateProfile: {uid}", dto.User.UID);
        ExecuteSafely(() => Mediator.Publish(new ClearProfileDataMessage(dto.User, null, null)));
        return Task.CompletedTask;
    }

    public Task Client_UserUpdateSelfPairPermissions(UserPermissionsDto dto)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Client_UserUpdateSelfPairPermissions: {uid}", dto.User.UID);
        ExecuteSafely(() => _pairManager.UpdateSelfPairPermissions(dto));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyJoin(UserData userData)
    {
        Logger.LogDebug("Client_GposeLobbyJoin: {dto}", userData);
        ExecuteSafely(() => Mediator.Publish(new GposeLobbyUserJoin(userData)));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyLeave(UserData userData)
    {
        Logger.LogDebug("Client_GposeLobbyLeave: {dto}", userData);
        ExecuteSafely(() => Mediator.Publish(new GPoseLobbyUserLeave(userData)));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyPushCharacterData(CharaDataDownloadDto charaDownloadDto)
    {
        Logger.LogDebug("Client_GposeLobbyPushCharacterData: {dto}", charaDownloadDto.Uploader);
        ExecuteSafely(() => Mediator.Publish(new GPoseLobbyReceiveCharaData(charaDownloadDto)));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyPushPoseData(UserData userData, PoseData poseData)
    {
        Logger.LogDebug("Client_GposeLobbyPushPoseData: {dto}", userData);
        ExecuteSafely(() => Mediator.Publish(new GPoseLobbyReceivePoseData(userData, poseData)));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyPushWorldData(UserData userData, WorldData worldData)
    {
        Logger.LogDebug("Client_GposeLobbyPushWorldData: {dto}", userData);
        ExecuteSafely(() => Mediator.Publish(new GPoseLobbyReceiveWorldData(userData, worldData)));
        return Task.CompletedTask;
    }

    public void OnDownloadReady(Action<Guid> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_DownloadReady), act);
    }

    public void OnGroupChangePermissions(Action<GroupPermissionDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GroupChangePermissions), act);
    }

    public void OnGroupPairChangePermissions(Action<GroupPairUserPermissionDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GroupPairChangePermissions), act);
    }

    public void OnGroupDelete(Action<GroupDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GroupDelete), act);
    }

    public void OnGroupPairChangeUserInfo(Action<GroupPairUserInfoDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GroupPairChangeUserInfo), act);
    }

    public void OnGroupPairJoined(Action<GroupPairFullInfoDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GroupPairJoined), act);
    }

    public void OnGroupPairLeft(Action<GroupPairDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GroupPairLeft), act);
    }

    public void OnGroupSendFullInfo(Action<GroupFullInfoDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GroupSendFullInfo), act);
    }

    public void OnGroupSendInfo(Action<GroupInfoDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GroupSendInfo), act);
    }

    public void OnReceiveServerMessage(Action<MessageSeverity, string> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_ReceiveServerMessage), act);
    }

    public void OnUpdateSystemInfo(Action<SystemInfoDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_UpdateSystemInfo), act);
    }

    public void OnUserAddClientPair(Action<UserPairDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_UserAddClientPair), act);
    }

    public void OnUserTypingState(Action<TypingStateDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_UserTypingState), act);
    }

    public void OnUserReceiveCharacterData(Action<OnlineUserCharaDataDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_UserReceiveCharacterData), act);
    }

    public void OnUserReceiveUploadStatus(Action<UserDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_UserReceiveUploadStatus), act);
    }

    public void OnUserRemoveClientPair(Action<UserDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_UserRemoveClientPair), act);
    }

    public void OnUserSendOffline(Action<UserDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_UserSendOffline), act);
    }

    public void OnUserSendOnline(Action<OnlineUserIdentDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_UserSendOnline), act);
    }

    public void OnUserUpdateOtherPairPermissions(Action<UserPermissionsDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_UserUpdateOtherPairPermissions), act);
    }

    public void OnUserUpdateProfile(Action<UserDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_UserUpdateProfile), act);
    }

    public void OnUserUpdateSelfPairPermissions(Action<UserPermissionsDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_UserUpdateSelfPairPermissions), act);
    }

    public void OnGposeLobbyJoin(Action<UserData> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GposeLobbyJoin), act);
    }

    public void OnGposeLobbyLeave(Action<UserData> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GposeLobbyLeave), act);
    }

    public void OnGposeLobbyPushCharacterData(Action<CharaDataDownloadDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GposeLobbyPushCharacterData), act);
    }

    public void OnGposeLobbyPushPoseData(Action<UserData, PoseData> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GposeLobbyPushPoseData), act);
    }

    public void OnGposeLobbyPushWorldData(Action<UserData, WorldData> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GposeLobbyPushWorldData), act);
    }

    public Task Client_GroupSendProfile(GroupProfileDto profile)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Client_GroupSendProfile: {gid}", profile.Group?.GID);
        Mediator.Publish(new GroupProfileUpdatedMessage(profile));
        return Task.CompletedTask;
    }

    public void OnGroupSendProfile(Action<GroupProfileDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_GroupSendProfile), act);
    }


    private void ExecuteSafely(Action act)
    {
        try
        {
            act();
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "Error on executing safely");
        }
    }
}