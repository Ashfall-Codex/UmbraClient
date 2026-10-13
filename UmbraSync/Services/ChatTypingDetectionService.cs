using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Runtime.CompilerServices;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Dto.User;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;

namespace UmbraSync.Services;

public sealed class ChatTypingDetectionService : IDisposable
{
    private readonly ILogger<ChatTypingDetectionService> _logger;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly IGameGui _gameGui;
    private readonly ChatService _chatService;
    private readonly TypingIndicatorStateService _typingStateService;
    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private readonly IPartyList _partyList;
    private readonly MareConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;

    private string _lastChatText = string.Empty;
    private bool _isTyping;
    private bool _notifyingRemote;
    private bool _serverSupportWarnLogged;
    private bool _remoteNotificationsEnabled;
    private bool _subscribed;
    private const int AllianceMemberSlots = 24;
    private readonly List<(uint EntityId, string Name)> _allianceMemberBuffer = new(AllianceMemberSlots);
    private string _lastSkipReason = string.Empty;
    private DateTime _lastSkipLog = DateTime.MinValue;
    private static readonly TimeSpan SkipLogThrottle = TimeSpan.FromSeconds(5);

    // Track current typing channels and last published snapshot
    private TypingChannelsDto _currentChannels = new();
    private TypingChannelsDto _lastPublishedChannels = new();

    public ChatTypingDetectionService(ILogger<ChatTypingDetectionService> logger, IFramework framework,
        IClientState clientState, IGameGui gameGui, ChatService chatService, PairManager pairManager, IPartyList partyList,
        TypingIndicatorStateService typingStateService, ApiController apiController, MareConfigService configService,
        DalamudUtilService dalamudUtil)
    {
        _logger = logger;
        _framework = framework;
        _clientState = clientState;
        _gameGui = gameGui;
        _chatService = chatService;
        _pairManager = pairManager;
        _partyList = partyList;
        _typingStateService = typingStateService;
        _apiController = apiController;
        _configService = configService;
        _dalamudUtil = dalamudUtil;

        Subscribe();
        _logger.LogInformation("ChatTypingDetectionService initialized");
    }

    public void Dispose()
    {
        Unsubscribe();
        ResetTypingState();
    }

    public void SoftRestart()
    {
        try
        {
            _logger.LogInformation("TypingDetection: soft restarting");
            Unsubscribe();
            ResetTypingState();
            _chatService.ClearTypingState();
            _typingStateService.ClearAll();
            Subscribe();
            _logger.LogInformation("TypingDetection: soft restart completed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TypingDetection: soft restart failed");
        }
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _framework.Update += OnFrameworkUpdate;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _framework.Update -= OnFrameworkUpdate;
        _subscribed = false;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            if (!_clientState.IsLoggedIn)
            {
                LogSkip("not logged in");
                ResetTypingState();
                return;
            }

            if (!_configService.Current.TypingIndicatorEnabled || _dalamudUtil.IsInGpose)
            {
                LogSkip("typing indicator disabled or in gpose");
                ResetTypingState();
                _chatService.ClearTypingState();
                return;
            }

            if (!TryGetChatInput(out var chatText) || string.IsNullOrEmpty(chatText))
            {
                LogSkip("chat input unavailable/empty");
                ResetTypingState();
                return;
            }

            if (IsIgnoredCommand(chatText))
            {
                LogSkip("ignored command");
                ResetTypingState();
                return;
            }

            var notifyRemote = ShouldNotifyRemote();
            UpdateRemoteNotificationLogState(notifyRemote);
            if (!notifyRemote && _notifyingRemote)
            {
                LogSkip("notifyRemote=false");
                _chatService.ClearTypingState();
                _notifyingRemote = false;
            }

            if (_isTyping && string.Equals(chatText, _lastChatText, StringComparison.Ordinal))
            {
                _lastChatText = chatText;
                return;
            }

            // Keep server channel memberships up to date (party/alliance/etc.)
            RefreshTypingChannelsIfChanged();

            if (notifyRemote)
            {
                var scope = GetCurrentTypingScope();
                if (scope == TypingScope.Unknown)
                {
                    scope = TypingScope.Proximity; // fallback when chat type cannot be resolved
                }
                // Resolve channelId for scoped routing when available
                string? channelId = ResolveChannelIdForScope(scope);
                _logger.LogDebug("TypingDetection: notify remote scope={scope} channelId={channel} textLength={length}", scope, channelId, chatText.Length);
                if (!string.IsNullOrEmpty(channelId))
                {
                    _chatService.NotifyTypingKeystroke(scope, channelId, targetUid: null);
                }
                else
                {
                    _chatService.NotifyTypingKeystroke(scope);
                }
                _notifyingRemote = true;
            }

            _typingStateService.SetSelfTypingLocal(true);
            _isTyping = true;

            _lastChatText = chatText;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "ChatTypingDetectionService tick failed");
        }
    }

    private void ResetTypingState()
    {
        if (!_isTyping)
        {
            _lastChatText = string.Empty;
            return;
        }

        _isTyping = false;
        _lastChatText = string.Empty;
        _chatService.ClearTypingState();
        _notifyingRemote = false;
        _typingStateService.SetSelfTypingLocal(false);
    }

    private static unsafe TypingScope GetCurrentTypingScope()
    {
        try
        {
            var shellModule = RaptureShellModule.Instance();
            if (shellModule == null)
                return TypingScope.Unknown;

            var chatType = (XivChatType)shellModule->ChatType;
            switch (chatType)
            {
                case XivChatType.Say:
                case XivChatType.Shout:
                case XivChatType.Yell:
                    return TypingScope.Proximity;
                case XivChatType.Party:
                    return TypingScope.Party;
                case XivChatType.CrossParty:
                    return TypingScope.CrossParty;
                case XivChatType.Alliance:
                    return TypingScope.Alliance;
                case XivChatType.FreeCompany:
                    return TypingScope.FreeCompany;
                // Note: whisper (tell) target resolution requires mapping to Umbra UID; not handled here
                default:
                    return TypingScope.Unknown;
            }
        }
        catch
        {
            return TypingScope.Unknown;
        }
    }

    private string? ResolveChannelIdForScope(TypingScope scope)
    {
        return scope switch
        {
            TypingScope.Party => _currentChannels.PartyId,
            TypingScope.Alliance => _currentChannels.AllianceId,
            TypingScope.FreeCompany => _currentChannels.FreeCompanyId,
            TypingScope.CrossParty => _currentChannels.CrossPartyIds != null && _currentChannels.CrossPartyIds.Length > 0 ? _currentChannels.CrossPartyIds[0] : null,
            // For Proximity/Unknown, server fallback uses paired users; no channel id
            _ => null,
        };
    }

    private void RefreshTypingChannelsIfChanged()
    {
        // Build current channels from available Dalamud services. For now, we reliably support Party via IPartyList.
        var channels = new TypingChannelsDto();

        try
        {
            var party = _partyList;
            if (party.Length > 0)
            {
                // Build a stable party ID based on the smallest non-zero ContentId among party members
                // This avoids relying on a Leader property that may not exist across platforms/APIs
                ulong leaderCid = 0UL;
                try
                {
                    for (int i = 0; i < party.Length; i++)
                    {
                        var member = party[i];
                        if (member == null) continue;

                        // Some platforms expose ContentId as long? and others as ulong.
                        // Use Convert.ToUInt64 via dynamic to normalize without compile-time type conflicts.
                        ulong cid = 0UL;
                        try
                        {
                            dynamic dm = member;
                            var rawCid = dm.ContentId; // could be long?, ulong, or null
                            if (rawCid != null)
                            {
                                cid = Convert.ToUInt64(rawCid);
                            }
                        }
                        catch
                        {
                            cid = 0UL;
                        }

                        if (cid != 0UL && (leaderCid == 0UL || cid < leaderCid))
                            leaderCid = cid;
                    }
                }
                catch
                {
                    leaderCid = 0UL;
                }

                if (leaderCid != 0UL)
                {
                    // Haché : l'identifiant de jeu d'un membre du groupe, peut-être un tiers, ne part pas tel quel.
                    // Tous les membres calculent la même valeur, le routage reste commun.
                    channels.PartyId = "party:" + UmbraSync.Utils.Crypto.GetHash256(
                        "umbra-typing-party:" + leaderCid.ToString(CultureInfo.InvariantCulture))[..32];
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "TypingDetection: failed to build party channel");
        }

        // Compare with last snapshot
        if (!ChannelsEqual(_currentChannels, channels))
        {
            _currentChannels = channels;
        }

        if (!ChannelsEqual(_lastPublishedChannels, _currentChannels))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogDebug("TypingDetection: publishing typing channels (party={party}, alliance={alliance}, fc={fc})",
                        _currentChannels.PartyId, _currentChannels.AllianceId, _currentChannels.FreeCompanyId);
                    await _apiController.UserUpdateTypingChannels(_currentChannels).ConfigureAwait(false);
                    _lastPublishedChannels = CloneChannels(_currentChannels);
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "TypingDetection: failed to publish typing channels");
                }
            });
        }
    }

    private static bool ChannelsEqual(TypingChannelsDto a, TypingChannelsDto b)
    {
        if (!string.Equals(a.PartyId, b.PartyId, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.AllianceId, b.AllianceId, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.FreeCompanyId, b.FreeCompanyId, StringComparison.Ordinal)) return false;
        if (a.ProximityEnabled != b.ProximityEnabled) return false;
        if ((a.CrossPartyIds?.Length ?? 0) != (b.CrossPartyIds?.Length ?? 0)) return false;
        if ((a.CustomGroupIds?.Length ?? 0) != (b.CustomGroupIds?.Length ?? 0)) return false;
        if (a.CrossPartyIds != null && b.CrossPartyIds != null)
        {
            for (int i = 0; i < a.CrossPartyIds.Length; i++)
            {
                if (!string.Equals(a.CrossPartyIds[i], b.CrossPartyIds[i], StringComparison.Ordinal)) return false;
            }
        }
        if (a.CustomGroupIds != null && b.CustomGroupIds != null)
        {
            for (int i = 0; i < a.CustomGroupIds.Length; i++)
            {
                if (!string.Equals(a.CustomGroupIds[i], b.CustomGroupIds[i], StringComparison.Ordinal)) return false;
            }
        }
        return true;
    }

    private static TypingChannelsDto CloneChannels(TypingChannelsDto src)
    {
        return new TypingChannelsDto
        {
            PartyId = src.PartyId,
            AllianceId = src.AllianceId,
            FreeCompanyId = src.FreeCompanyId,
            CrossPartyIds = src.CrossPartyIds != null ? (string[])src.CrossPartyIds.Clone() : null,
            CustomGroupIds = src.CustomGroupIds != null ? (string[])src.CustomGroupIds.Clone() : null,
            ProximityEnabled = src.ProximityEnabled,
        };
    }

    private static bool IsIgnoredCommand(string chatText)
    {
        if (string.IsNullOrWhiteSpace(chatText))
            return false;

        var trimmed = chatText.TrimStart();
        if (!trimmed.StartsWith('/'))
            return false;

        var firstTokenEnd = trimmed.IndexOf(' ');
        var command = firstTokenEnd >= 0 ? trimmed[..firstTokenEnd] : trimmed;
        command = command.TrimEnd();

        var comparison = StringComparison.OrdinalIgnoreCase;
        return command.StartsWith("/tell", comparison)
            || command.StartsWith("/t", comparison)
            || command.StartsWith("/xllog", comparison)
            || command.StartsWith("/umbra", comparison);
    }

    private unsafe bool ShouldNotifyRemote()
    {
        try
        {
            if (!_configService.Current.TypingIndicatorEnabled)
            {
                return false;
            }

            var supportsTypingState = _apiController.SystemInfoDto.SupportsTypingState;
            var connected = _apiController.IsConnected;
            if (!connected || !supportsTypingState)
            {
                if (!_serverSupportWarnLogged)
                {
                    _logger.LogDebug("TypingDetection: server support unavailable (connected={connected}, supports={supports})", connected, supportsTypingState);
                    _serverSupportWarnLogged = true;
                }
                return false;
            }

            _serverSupportWarnLogged = false;

            var shellModule = RaptureShellModule.Instance();
            if (shellModule == null)
            {
                _logger.LogDebug("TypingDetection: shell module null");
                return true;
            }

            var chatType = (XivChatType)shellModule->ChatType;
            switch (chatType)
            {
                case XivChatType.Say:
                case XivChatType.Shout:
                case XivChatType.Yell:
                    return true;
                case XivChatType.Party:
                case XivChatType.CrossParty:
                    var eligible = PartyContainsPairedMember();
                    return eligible;
                case XivChatType.Alliance:
                    return AllianceContainsPairedMember();
                case XivChatType.FreeCompany:
                    return true;
                case XivChatType.Debug:
                    return true;
                default:
                    _logger.LogTrace("TypingDetection: channel {type} rejected", chatType);
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "ChatTypingDetectionService: failed to evaluate chat channel");
        }

        return true;
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
                _logger.LogDebug("TypingDetection: no paired names online");
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
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ChatTypingDetectionService: failed to check party composition");
        }

        _logger.LogDebug("TypingDetection: no paired members in party");
        return false;
    }

    private bool AllianceContainsPairedMember()
    {
        try
        {
            if (!_partyList.IsAlliance)
            {
                _logger.LogDebug("TypingDetection: not in an alliance");
                return false;
            }

            var allianceMembers = GetAllianceMembersSnapshot();
            if (allianceMembers.Count == 0)
            {
                _logger.LogDebug("TypingDetection: alliance list empty");
                return false;
            }

            foreach (var pair in _pairManager.GetOnlineUserPairs())
            {
                var objectId = pair.PlayerCharacterId;
                if (objectId != 0 && objectId != uint.MaxValue && allianceMembers.Any(m => m.EntityId == objectId))
                {
                    return true;
                }

                var playerName = pair.PlayerName;
                if (string.IsNullOrEmpty(playerName))
                    continue;

                if (allianceMembers.Any(m =>
                        !string.IsNullOrEmpty(m.Name)
                        && m.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ChatTypingDetectionService: failed to check alliance composition");
        }

        _logger.LogDebug("TypingDetection: no paired members in alliance");
        return false;
    }

    private IReadOnlyList<(uint EntityId, string Name)> GetAllianceMembersSnapshot()
    {
        _allianceMemberBuffer.Clear();
        try
        {
            for (var i = 0; i < AllianceMemberSlots; ++i)
            {
                var memberAddress = _partyList.GetAllianceMemberAddress(i);
                if (memberAddress == nint.Zero)
                    continue;

                var member = _partyList.CreateAllianceMemberReference(memberAddress);
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
            _logger.LogTrace(ex, "ChatTypingDetectionService: failed to enumerate alliance members");
        }

        return _allianceMemberBuffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe bool TryGetChatInput(out string chatText)
    {
        chatText = string.Empty;

        var addon = _gameGui.GetAddonByName("ChatLog", 1);
        if (addon.Address == nint.Zero)
            return false;

        var chatLog = (AtkUnitBase*)addon.Address;
        if (chatLog == null || !chatLog->IsVisible)
            return false;

        var textInputNode = chatLog->UldManager.NodeList[16];
        if (textInputNode == null)
            return false;

        var componentNode = textInputNode->GetAsAtkComponentNode();
        if (componentNode == null || componentNode->Component == null)
            return false;

        var cursorNode = componentNode->Component->UldManager.NodeList[14];
        if (cursorNode == null)
            return false;

        var cursorVisible = cursorNode->IsVisible();
        if (!cursorVisible)
        {
            return false;
        }

        var chatInputNode = componentNode->Component->UldManager.NodeList[1];
        if (chatInputNode == null)
            return false;

        var textNode = chatInputNode->GetAsAtkTextNode();
        if (textNode == null)
            return false;

        var rawText = textNode->GetText();
        if (rawText == (byte*)0)
            return false;

        chatText = rawText.AsDalamudSeString().ToString();
        return true;
    }

    private void UpdateRemoteNotificationLogState(bool notifyRemote)
    {
        if (notifyRemote && !_remoteNotificationsEnabled)
        {
            _remoteNotificationsEnabled = true;
            _logger.LogInformation("TypingDetection: remote notifications enabled");
        }
        else if (!notifyRemote && _remoteNotificationsEnabled)
        {
            _remoteNotificationsEnabled = false;
            _logger.LogInformation("TypingDetection: remote notifications disabled");
        }
    }

    private void LogSkip(string reason)
    {
        var now = DateTime.UtcNow;
        if (!string.Equals(reason, _lastSkipReason, StringComparison.OrdinalIgnoreCase)
            || (now - _lastSkipLog) >= SkipLogThrottle)
        {
            _logger.LogDebug("TypingDetection: skipped ({reason})", reason);
            _lastSkipReason = reason;
            _lastSkipLog = now;
        }
    }
}