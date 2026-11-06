using Dalamud.Interface;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Modules.ChatPlus.Services;

public class PartyListTypingService(ILogger<PartyListTypingService> logger,
    MareMediator mediator,
    IPartyList partyList,
    PairManager pairManager,
    MareConfigService configService,
    TypingIndicatorStateService typingStateService,
    IUiBuilder uiBuilder)
    : DisposableMediatorSubscriberBase(logger, mediator), IHostedService
{
    private readonly ILogger<PartyListTypingService> _logger = logger;
    private readonly IUiBuilder _uiBuilder = uiBuilder;
    private readonly IPartyList _partyList = partyList;
    private readonly MareConfigService _configService = configService;
    private readonly PairManager _pairManager = pairManager;
    private readonly TypingIndicatorStateService _typingStateService = typingStateService;
    private static readonly TimeSpan TypingDisplayTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TypingDisplayFade = TypingDisplayTime;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _uiBuilder.Draw += Draw;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _uiBuilder.Draw -= Draw;
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        _uiBuilder.Draw -= Draw;
        base.Dispose(disposing);
    }

    public void Draw()
    {
        if (!_configService.Current.TypingIndicatorEnabled) return;
        if (!_configService.Current.TypingIndicatorShowOnPartyList) return;
        var visibleByAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var visibleUsers = _pairManager.GetVisibleUsers();
            foreach (var u in visibleUsers)
            {
                var alias = string.IsNullOrEmpty(u.AliasOrUID) ? u.UID : u.AliasOrUID;
                if (!visibleByAlias.ContainsKey(alias)) visibleByAlias[alias] = u.UID;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PartyListTypingService: failed to get visible users");
        }

        var activeTypers = _typingStateService.GetActiveTypers(TypingDisplayTime);
        var now = DateTime.UtcNow;

        for (var i = 0; i < _partyList.Length; ++i)
        {
            var member = _partyList[i];
            if (member == null || member.Address == nint.Zero)
                continue;

            var displayName = member.Name.TextValue;
            if (string.IsNullOrEmpty(displayName))
                continue;

            if (visibleByAlias.TryGetValue(displayName, out var uid)
                && activeTypers.TryGetValue(uid, out var entry)
                && (now - entry.LastUpdate) <= TypingDisplayFade)
            {
                _logger.LogDebug("PartyListTypingService: bubble would be shown for {name}", displayName);
            }
        }
    }
}