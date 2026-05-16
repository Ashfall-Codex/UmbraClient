using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using UmbraSync.API.Data;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

public sealed class TypingIndicatorStateService : IMediatorSubscriber, IDisposable
{
    private sealed record TypingEntry(UserData User, DateTime FirstSeen, DateTime LastUpdate);

    private readonly ConcurrentDictionary<string, TypingEntry> _typingUsers = new(StringComparer.Ordinal);
    private readonly ApiController _apiController;
    private readonly ILogger<TypingIndicatorStateService> _logger;
    private DateTime _selfTypingLast = DateTime.MinValue;
    private DateTime _selfTypingStart = DateTime.MinValue;
    private bool _selfTypingActive;

    public TypingIndicatorStateService(ILogger<TypingIndicatorStateService> logger, MareMediator mediator, ApiController apiController)
    {
        _logger = logger;
        _apiController = apiController;
        Mediator = mediator;

        mediator.Subscribe<UserTypingStateMessage>(this, OnTypingState);
    }

    public void Dispose()
    {
        Mediator.UnsubscribeAll(this);
    }

    public MareMediator Mediator { get; }

    public void SetSelfTypingLocal(bool isTyping)
    {
        var wasTyping = _selfTypingActive;
        if (isTyping)
        {
            if (!_selfTypingActive)
                _selfTypingStart = DateTime.UtcNow;
            _selfTypingLast = DateTime.UtcNow;
        }
        else
        {
            _selfTypingStart = DateTime.MinValue;
        }

        _selfTypingActive = isTyping;
        if (wasTyping != _selfTypingActive)
        {
            _logger.LogDebug("Typing state self -> {state}", _selfTypingActive);
        }
    }

    private void OnTypingState(UserTypingStateMessage msg)
    {
        var uid = msg.Typing.User.UID;
        var now = DateTime.UtcNow;

        if (string.Equals(uid, _apiController.UID, StringComparison.Ordinal))
        {
            var wasTyping = _selfTypingActive;
            _selfTypingActive = msg.Typing.IsTyping;
            if (_selfTypingActive)
            {
                if (_selfTypingStart == DateTime.MinValue)
                    _selfTypingStart = now;
                _selfTypingLast = now;
            }
            else
            {
                _selfTypingStart = DateTime.MinValue;
            }
            if (wasTyping != _selfTypingActive)
                _logger.LogDebug("Typing state self -> {state}", _selfTypingActive);
        }
        else if (msg.Typing.IsTyping)
        {
            _typingUsers.AddOrUpdate(uid,
                _ => new TypingEntry(msg.Typing.User, now, now),
                (_, existing) => new TypingEntry(msg.Typing.User, existing.FirstSeen, now));
        }
    }

    public bool TryGetSelfTyping(TimeSpan maxAge, out DateTime startTyping, out DateTime lastTyping)
    {
        startTyping = _selfTypingStart;
        lastTyping = _selfTypingLast;
        if (!_selfTypingActive)
            return false;

        var now = DateTime.UtcNow;
        if ((now - _selfTypingLast) >= maxAge)
        {
            _selfTypingActive = false;
            _selfTypingStart = DateTime.MinValue;
            return false;
        }

        return true;
    }

    public IReadOnlyDictionary<string, (UserData User, DateTime FirstSeen, DateTime LastUpdate)> GetActiveTypers(TimeSpan maxAge)
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _typingUsers.ToArray())
        {
            if ((now - kvp.Value.LastUpdate) >= maxAge)
            {
                _typingUsers.TryRemove(kvp.Key, out _);
            }
        }

        return _typingUsers.ToDictionary(k => k.Key, v => (v.Value.User, v.Value.FirstSeen, v.Value.LastUpdate), StringComparer.Ordinal);
    }

    public void ClearAll()
    {
        _typingUsers.Clear();
        _selfTypingActive = false;
        _selfTypingStart = DateTime.MinValue;
        _selfTypingLast = DateTime.MinValue;
        _logger.LogDebug("TypingIndicatorStateService: cleared all typing state");
    }
}