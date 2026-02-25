using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.QuestSync;

namespace UmbraSync.WebAPI.SignalR;

public sealed partial class ApiController
{
    // --- Server calls (Client -> Server) ---

    public async Task<string> QuestSessionCreate(string questId, string questName)
    {
        if (!IsConnected) return string.Empty;
        try
        {
            return await _mareHub!.InvokeAsync<string>(nameof(QuestSessionCreate), questId, questName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(QuestSessionCreate));
            return string.Empty;
        }
    }

    public async Task<List<UserData>> QuestSessionJoin(string sessionId)
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<UserData>>(nameof(QuestSessionJoin), sessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(QuestSessionJoin));
            return [];
        }
    }

    public async Task<bool> QuestSessionLeave()
    {
        if (!IsConnected) return false;
        try
        {
            return await _mareHub!.InvokeAsync<bool>(nameof(QuestSessionLeave)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(QuestSessionLeave));
            return false;
        }
    }

    public async Task QuestSessionPushState(QuestSessionStateDto state)
    {
        if (!IsConnected) return;
        await _mareHub!.SendAsync(nameof(QuestSessionPushState), state).ConfigureAwait(false);
    }

    public async Task QuestSessionTriggerEvent(QuestEventTriggerDto trigger)
    {
        if (!IsConnected) return;
        await _mareHub!.SendAsync(nameof(QuestSessionTriggerEvent), trigger).ConfigureAwait(false);
    }

    public async Task QuestSessionBranchingChoice(QuestBranchingChoiceDto choice)
    {
        if (!IsConnected) return;
        await _mareHub!.SendAsync(nameof(QuestSessionBranchingChoice), choice).ConfigureAwait(false);
    }

    // --- Server callbacks (Server -> Client) ---

    public Task Client_QuestSessionJoin(UserData userData)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Client_QuestSessionJoin: {uid}", userData.UID);
        ExecuteSafely(() => Mediator.Publish(new Services.Mediator.QuestSessionJoinMessage(userData)));
        return Task.CompletedTask;
    }

    public Task Client_QuestSessionLeave(UserData userData)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Client_QuestSessionLeave: {uid}", userData.UID);
        ExecuteSafely(() => Mediator.Publish(new Services.Mediator.QuestSessionLeaveMessage(userData)));
        return Task.CompletedTask;
    }

    public Task Client_QuestSessionStateUpdate(UserData sender, QuestSessionStateDto state)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Client_QuestSessionStateUpdate from {uid}", sender.UID);
        ExecuteSafely(() => Mediator.Publish(new Services.Mediator.QuestSessionStateUpdateMessage(sender, state)));
        return Task.CompletedTask;
    }

    public Task Client_QuestSessionEventTriggered(UserData sender, QuestEventTriggerDto trigger)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Client_QuestSessionEventTriggered from {uid}", sender.UID);
        ExecuteSafely(() => Mediator.Publish(new Services.Mediator.QuestSessionEventTriggeredMessage(sender, trigger)));
        return Task.CompletedTask;
    }

    public Task Client_QuestSessionBranchingChoice(UserData sender, QuestBranchingChoiceDto choice)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Client_QuestSessionBranchingChoice from {uid}", sender.UID);
        ExecuteSafely(() => Mediator.Publish(new Services.Mediator.QuestSessionBranchingChoiceMessage(sender, choice)));
        return Task.CompletedTask;
    }

    // --- Callback registration (On* handlers) ---

    public void OnQuestSessionJoin(Action<UserData> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_QuestSessionJoin), act);
    }

    public void OnQuestSessionLeave(Action<UserData> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_QuestSessionLeave), act);
    }

    public void OnQuestSessionStateUpdate(Action<UserData, QuestSessionStateDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_QuestSessionStateUpdate), act);
    }

    public void OnQuestSessionEventTriggered(Action<UserData, QuestEventTriggerDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_QuestSessionEventTriggered), act);
    }

    public void OnQuestSessionBranchingChoice(Action<UserData, QuestBranchingChoiceDto> act)
    {
        if (_initialized) return;
        _mareHub!.On(nameof(Client_QuestSessionBranchingChoice), act);
    }
}
