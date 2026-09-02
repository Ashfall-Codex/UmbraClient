using Microsoft.AspNetCore.SignalR.Client;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;

namespace UmbraSync.WebAPI.SignalR.Utils;

public class ForeverRetryPolicy : IRetryPolicy
{
    private readonly MareMediator _mediator;
    private readonly NotificationTracker _notificationTracker;
    private bool _sentDisconnected = false;

    public ForeverRetryPolicy(MareMediator mediator, NotificationTracker notificationTracker)
    {
        _mediator = mediator;
        _notificationTracker = notificationTracker;
    }

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        TimeSpan timeToWait = TimeSpan.FromSeconds(Random.Shared.Next(10, 20));
        if (retryContext.PreviousRetryCount == 0)
        {
            _sentDisconnected = false;
            timeToWait = TimeSpan.FromSeconds(3);
        }
        else if (retryContext.PreviousRetryCount == 1) timeToWait = TimeSpan.FromSeconds(5);
        else if (retryContext.PreviousRetryCount == 2) timeToWait = TimeSpan.FromSeconds(10);
        else
        {
            if (!_sentDisconnected)
            {
                _mediator.Publish(new NotificationMessage("Connection lost", "Connection lost to server", NotificationType.Warning, TimeSpan.FromSeconds(10)));
                _notificationTracker.Upsert(NotificationEntry.ConnectionLost());
                _mediator.Publish(new DisconnectedMessage());
            }
            _sentDisconnected = true;
        }

        return timeToWait;
    }
}