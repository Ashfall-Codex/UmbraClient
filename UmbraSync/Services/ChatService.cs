using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Data.Enum;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

public class ChatService : DisposableMediatorSubscriberBase
{
    private readonly IChatGui _chatGui;
    private readonly TypingRemoteNotificationService _typingNotifier;

    public ChatService(ILogger<ChatService> logger, MareMediator mediator, IChatGui chatGui,
        TypingRemoteNotificationService typingNotifier) : base(logger, mediator)
    {
        _chatGui = chatGui;
        _typingNotifier = typingNotifier;
    }

    public void NotifyTypingKeystroke(TypingScope scope)
    {
        _typingNotifier.NotifyTypingKeystroke(scope);
    }

    public void NotifyTypingKeystroke(TypingScope scope, string? channelId, string? targetUid)
    {
        _typingNotifier.NotifyTypingKeystroke(scope, channelId, targetUid);
    }

    public void ClearTypingState()
    {
        _typingNotifier.ClearTypingState();
    }

    public void Print(string message)
    {
        _chatGui.Print(new XivChatEntry
        {
            Message = "[UmbraSync] " + message,
            Type = XivChatType.Debug
        });
    }
}
