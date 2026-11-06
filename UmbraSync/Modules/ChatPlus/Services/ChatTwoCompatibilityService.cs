using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using UmbraSync.API.Data.Enum;
using UmbraSync.Interop.ChatTwo;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Modules.ChatPlus.Services;

public sealed class ChatTwoCompatibilityService : MediatorSubscriberBase, IHostedService
{
    private const string ChatTwoInternalName = "ChatTwo";
    private static readonly Version ChannelTypeSupportVersion = new(1, 31, 2);
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly TypingIndicatorStateService _typingIndicatorStateService;
    private readonly TypingRemoteNotificationService _typingRemoteNotifier;
    private readonly MareConfigService _configService;
    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private readonly IPartyList _partyList;
    private bool _warningShown;
    private bool _chatTwoSubscribed;
    private bool _chatTwoSupportsChannelType;
    private bool _chatTwoUsingLegacyPayload;
    private ICallGateSubscriber<(bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength, ChatType ChannelType)>? _chatTwoGetChatInputState;
    private ICallGateSubscriber<(bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength, ChatType ChannelType), object>? _chatTwoChatInputStateChanged;
    private ICallGateSubscriber<(bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength)>? _chatTwoLegacyGetChatInputState;
    private ICallGateSubscriber<(bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength), object>? _chatTwoLegacyChatInputStateChanged;
    private bool? _lastBubbleState;
    private bool _chatTwoLocalTyping;
    private bool _chatTwoRemoteAnnounced;
    private int _chatTwoLastTextLength;
    private bool _chatTwoServerSupportWarnLogged;
    private const int AllianceMemberSlots = 24;
    private readonly List<(uint EntityId, string Name)> _allianceMemberBuffer = new(AllianceMemberSlots);

    public ChatTwoCompatibilityService(ILogger<ChatTwoCompatibilityService> logger, IDalamudPluginInterface pluginInterface,
        MareMediator mediator, TypingIndicatorStateService typingIndicatorStateService, TypingRemoteNotificationService typingRemoteNotifier,
        MareConfigService configService, ApiController apiController, PairManager pairManager, IPartyList partyList)
        : base(logger, mediator)
    {
        _pluginInterface = pluginInterface;
        _typingIndicatorStateService = typingIndicatorStateService;
        _typingRemoteNotifier = typingRemoteNotifier;
        _configService = configService;
        _apiController = apiController;
        _pairManager = pairManager;
        _partyList = partyList;

        Mediator.SubscribeKeyed<PluginChangeMessage>(this, ChatTwoInternalName, OnChatTwoStateChanged);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var initialState = PluginWatcherService.GetInitialPluginState(_pluginInterface, ChatTwoInternalName);
            if (initialState?.IsLoaded == true)
            {
                UpdateChannelSupport(initialState.Version, true);
                if (!TryEnableChatTwoIntegration())
                {
                    ShowWarning();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to inspect ChatTwo initial state");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisableChatTwoIntegration();
        Mediator.UnsubscribeAll(this);
        return Task.CompletedTask;
    }

    private void OnChatTwoStateChanged(PluginChangeMessage message)
    {
        UpdateChannelSupport(message.Version, message.IsLoaded);
        if (message.IsLoaded)
        {
            if (!TryEnableChatTwoIntegration())
            {
                ShowWarning();
            }
        }
        else
        {
            DisableChatTwoIntegration();
            _warningShown = false;
        }
    }

    private void ShowWarning()
    {
        if (_warningShown) return;
        _warningShown = true;

        const string warningTitle = "ChatTwo détecté";
        const string warningBody = "Actuellement, le plugin ChatTwo n'est pas compatible avec la bulle d'écriture d'UmbraSync. Désactivez ChatTwo si vous souhaitez conserver l'indicateur de saisie.";

        Mediator.Publish(new NotificationMessage(warningTitle, warningBody, NotificationType.Warning, TimeSpan.FromSeconds(10)));
    }

    private bool TryEnableChatTwoIntegration()
    {
        if (_chatTwoSubscribed)
            return true;

        try
        {
            EnsureChatTwoSubscribers();
            if (_chatTwoSupportsChannelType)
            {
                if (_chatTwoChatInputStateChanged == null || _chatTwoGetChatInputState == null)
                    return false;

                _chatTwoChatInputStateChanged.Subscribe(OnChatTwoTypingStateChanged);
            }
            else
            {
                if (_chatTwoLegacyChatInputStateChanged == null || _chatTwoLegacyGetChatInputState == null)
                    return false;

                _chatTwoLegacyChatInputStateChanged.Subscribe(OnChatTwoLegacyTypingStateChanged);
            }

            _chatTwoUsingLegacyPayload = !_chatTwoSupportsChannelType;
            _chatTwoSubscribed = true;
            _lastBubbleState = null;
            _chatTwoLocalTyping = false;
            _chatTwoRemoteAnnounced = false;
            _chatTwoLastTextLength = 0;
            _chatTwoServerSupportWarnLogged = false;

            Logger.LogInformation("ChatTwo typing integration enabled (channelTypeSupport={support})", _chatTwoSupportsChannelType);
            InitializeChatTwoState();
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to enable ChatTwo integration");
            DisableChatTwoIntegration();
            return false;
        }
    }

    private void DisableChatTwoIntegration()
    {
        if (!_chatTwoSubscribed)
            return;

        try
        {
            if (_chatTwoUsingLegacyPayload)
            {
                _chatTwoLegacyChatInputStateChanged?.Unsubscribe(OnChatTwoLegacyTypingStateChanged);
            }
            else
            {
                _chatTwoChatInputStateChanged?.Unsubscribe(OnChatTwoTypingStateChanged);
            }
        }
        catch (Exception ex)
        {
            Logger.LogTrace(ex, "Failed to unsubscribe from ChatTwo state change IPC");
        }
        finally
        {
            _chatTwoSubscribed = false;
            _lastBubbleState = null;
            ResetChatTwoTypingState();
            Logger.LogInformation("ChatTwo typing integration disabled");
        }
    }

    private void EnsureChatTwoSubscribers()
    {
        if (_chatTwoSupportsChannelType)
        {
            _chatTwoGetChatInputState ??= _pluginInterface.GetIpcSubscriber<(bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength, ChatType ChannelType)>("ChatTwo.GetChatInputState");
            _chatTwoChatInputStateChanged ??= _pluginInterface.GetIpcSubscriber<(bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength, ChatType ChannelType), object>("ChatTwo.ChatInputStateChanged");
        }
        else
        {
            _chatTwoLegacyGetChatInputState ??= _pluginInterface.GetIpcSubscriber<(bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength)>("ChatTwo.GetChatInputState");
            _chatTwoLegacyChatInputStateChanged ??= _pluginInterface.GetIpcSubscriber<(bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength), object>("ChatTwo.ChatInputStateChanged");
        }
    }

    private void InitializeChatTwoState()
    {
        try
        {
            if (_chatTwoSupportsChannelType)
            {
                if (_chatTwoGetChatInputState == null)
                    return;

                var initialState = _chatTwoGetChatInputState.InvokeFunc();
                Logger.LogTrace("ChatTwo initial typing state: visible={Visible}, focused={Focused}, text={HasText}, typing={Typing}, length={Length}, channel={Channel}",
                    initialState.InputVisible, initialState.InputFocused, initialState.HasText, initialState.IsTyping, initialState.TextLength, initialState.ChannelType);
                HandleChatTwoTypingState(new ChatTwoTypingState(initialState.InputVisible, initialState.InputFocused, initialState.HasText,
                    initialState.IsTyping, initialState.TextLength, initialState.ChannelType));
            }
            else
            {
                if (_chatTwoLegacyGetChatInputState == null)
                    return;

                var initialState = _chatTwoLegacyGetChatInputState.InvokeFunc();
                Logger.LogTrace("ChatTwo initial typing state (legacy): visible={Visible}, focused={Focused}, text={HasText}, typing={Typing}, length={Length}",
                    initialState.InputVisible, initialState.InputFocused, initialState.HasText, initialState.IsTyping, initialState.TextLength);
                HandleChatTwoTypingState(new ChatTwoTypingState(initialState.InputVisible, initialState.InputFocused, initialState.HasText,
                    initialState.IsTyping, initialState.TextLength, ChatType.Echo));
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to fetch ChatTwo chat input state");
        }
    }

    private void OnChatTwoTypingStateChanged((bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength, ChatType ChannelType) state)
    {
        HandleChatTwoTypingState(new ChatTwoTypingState(state.InputVisible, state.InputFocused, state.HasText, state.IsTyping, state.TextLength, state.ChannelType));
    }

    private void OnChatTwoLegacyTypingStateChanged((bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength) state)
    {
        HandleChatTwoTypingState(new ChatTwoTypingState(state.InputVisible, state.InputFocused, state.HasText, state.IsTyping, state.TextLength, ChatType.Echo));
    }

    private void HandleChatTwoTypingState(ChatTwoTypingState state)
    {
        if (!_chatTwoSubscribed)
            return;

        if (!_configService.Current.TypingIndicatorEnabled)
        {
            ResetChatTwoTypingState();
            return;
        }

        if (!state.InputVisible)
        {
            Logger.LogTrace("ChatTwo typing state ignored (input hidden)");
            ResetChatTwoTypingState();
            return;
        }

        var shouldShow = state.IsTyping || (state.InputFocused && state.HasText);
        _typingIndicatorStateService.SetSelfTypingLocal(shouldShow);

        var scope = MapChannelToTypingScope(state.ChannelType);

        if (shouldShow)
        {
            var hasContent = state.HasText && state.TextLength > 0;
            var startedTyping = !_chatTwoLocalTyping;
            var textChanged = state.TextLength != _chatTwoLastTextLength;

            if (hasContent && (startedTyping || textChanged))
            {
                TryNotifyRemoteTyping(scope, state.ChannelType);
            }

            _chatTwoLocalTyping = true;
            _chatTwoLastTextLength = state.TextLength;
        }
        else
        {
            _chatTwoLocalTyping = false;
            _chatTwoLastTextLength = 0;
            ResetRemoteNotification();
        }

        if (_lastBubbleState != shouldShow)
        {
            _lastBubbleState = shouldShow;
            Logger.LogDebug("ChatTwo typing bubble {state} (typing={typing}, focused={focused}, hasText={hasText}, length={length}, channel={channel}, scope={scope})",
                shouldShow ? "enabled" : "disabled",
                state.IsTyping, state.InputFocused, state.HasText, state.TextLength, state.ChannelType, scope);
        }
    }

    private void TryNotifyRemoteTyping(TypingScope scope, ChatType channelType)
    {
        if (scope == TypingScope.Unknown && channelType != ChatType.Debug)
            return;

        if (!ShouldNotifyRemote(scope, channelType))
            return;

        _typingRemoteNotifier.NotifyTypingKeystroke(scope);
        _chatTwoRemoteAnnounced = true;
    }

    private void ResetChatTwoTypingState()
    {
        _chatTwoLocalTyping = false;
        _chatTwoLastTextLength = 0;
        ResetRemoteNotification();
        _typingIndicatorStateService.SetSelfTypingLocal(false);
    }

    private void ResetRemoteNotification()
    {
        if (!_chatTwoRemoteAnnounced)
            return;

        _typingRemoteNotifier.ClearTypingState();
        _chatTwoRemoteAnnounced = false;
    }

    private bool ShouldNotifyRemote(TypingScope scope, ChatType channelType)
    {
        if (!_configService.Current.TypingIndicatorEnabled)
            return false;

        var connected = _apiController.IsConnected;
        var supportsTyping = _apiController.SystemInfoDto.SupportsTypingState;
        if (!connected || !supportsTyping)
        {
            if (!_chatTwoServerSupportWarnLogged)
            {
                Logger.LogDebug("ChatTwo typing: remote notifications unavailable (connected={connected}, supports={supports})", connected, supportsTyping);
                _chatTwoServerSupportWarnLogged = true;
            }
            return false;
        }

        _chatTwoServerSupportWarnLogged = false;

        if (scope == TypingScope.Unknown)
            return channelType == ChatType.Debug;

        return scope switch
        {
            TypingScope.Proximity => true,
            TypingScope.Party or TypingScope.CrossParty => PartyContainsPairedMember(),
            TypingScope.FreeCompany => true,
            TypingScope.Alliance => AllianceContainsPairedMember(),
            _ => false,
        };
    }

    private bool PartyContainsPairedMember()
    {
        try
        {
            var pairedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _pairManager.GetOnlineUserPairs())
            {
                if (!string.IsNullOrEmpty(pair.PlayerName))
                    pairedNames.Add(pair.PlayerName);
            }

            if (pairedNames.Count == 0)
            {
                Logger.LogTrace("ChatTwo typing: no paired names online");
                return false;
            }

            for (var i = 0; i < _partyList.Count; ++i)
            {
                var member = _partyList[i];
                if (member == null)
                    continue;

                var name = member.Name.TextValue;
                if (string.IsNullOrEmpty(name))
                    continue;

                if (pairedNames.Contains(name))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogTrace(ex, "ChatTwo typing: failed to evaluate party composition");
        }

        Logger.LogTrace("ChatTwo typing: no paired party members");
        return false;
    }

    private bool AllianceContainsPairedMember()
    {
        try
        {
            if (!_partyList.IsAlliance)
            {
                Logger.LogTrace("ChatTwo typing: not in an alliance");
                return false;
            }

            var allianceMembers = GetAllianceMembersSnapshot();
            if (allianceMembers.Count == 0)
            {
                Logger.LogTrace("ChatTwo typing: alliance list empty");
                return false;
            }

            foreach (var pair in _pairManager.GetOnlineUserPairs())
            {
                var objectId = pair.PlayerCharacterId;
                if (objectId != 0 && objectId != uint.MaxValue && allianceMembers.Any(m => m.EntityId == objectId))
                    return true;

                var playerName = pair.PlayerName;
                if (string.IsNullOrEmpty(playerName))
                    continue;

                if (allianceMembers.Any(m =>
                        !string.IsNullOrEmpty(m.Name)
                        && m.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogTrace(ex, "ChatTwo typing: failed to check alliance composition");
        }

        Logger.LogTrace("ChatTwo typing: no paired alliance members");
        return false;
    }

    private IReadOnlyList<(uint EntityId, string Name)> GetAllianceMembersSnapshot()
    {
        _allianceMemberBuffer.Clear();
        try
        {
            for (var i = 0; i < AllianceMemberSlots; ++i)
            {
                var address = _partyList.GetAllianceMemberAddress(i);
                if (address == nint.Zero)
                    continue;

                var member = _partyList.CreateAllianceMemberReference(address);
                if (member == null)
                    continue;

                var name = member.Name.TextValue;
                var entityId = member.EntityId;
                if (entityId == 0 && string.IsNullOrEmpty(name))
                    continue;

                _allianceMemberBuffer.Add((entityId, name));
            }
        }
        catch (Exception ex)
        {
            Logger.LogTrace(ex, "ChatTwo typing: failed to enumerate alliance members");
        }

        return _allianceMemberBuffer;
    }

    private static TypingScope MapChannelToTypingScope(ChatType channelType) => channelType switch
    {
        ChatType.Say or ChatType.Shout or ChatType.Yell => TypingScope.Proximity,
        ChatType.Party => TypingScope.Party,
        ChatType.CrossParty => TypingScope.CrossParty,
        ChatType.Alliance => TypingScope.Alliance,
        ChatType.FreeCompany => TypingScope.FreeCompany,
        _ => TypingScope.Unknown,
    };

    private void UpdateChannelSupport(Version? chatTwoVersion, bool isLoaded)
    {
        var supportsChannel = chatTwoVersion != null && chatTwoVersion >= ChannelTypeSupportVersion;
        var capabilityChanged = supportsChannel != _chatTwoSupportsChannelType;
        _chatTwoSupportsChannelType = supportsChannel;

        if (capabilityChanged && _chatTwoSubscribed && isLoaded)
        {
            Logger.LogInformation("ChatTwo channel support changed (channelType={support}), reinitializing IPC", supportsChannel);
            DisableChatTwoIntegration();
            TryEnableChatTwoIntegration();
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ChatTwoTypingState(bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength, ChatType ChannelType);
}