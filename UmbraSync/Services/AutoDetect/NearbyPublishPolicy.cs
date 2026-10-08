namespace UmbraSync.Services.AutoDetect;

public enum NearbyVisibility
{
    Visible,
    HiddenAfk,
    HiddenNotRoleplaying,
}

public static class NearbyPublishPolicy
{
    public const uint OnlineStatusAfk = 17;
    public const uint OnlineStatusRoleplaying = 22;

    public static NearbyVisibility Evaluate(uint onlineStatusId, bool publishWhenAfk, bool publishWhenNotRoleplaying)
    {
        return onlineStatusId switch
        {
            OnlineStatusAfk => publishWhenAfk ? NearbyVisibility.Visible : NearbyVisibility.HiddenAfk,
            OnlineStatusRoleplaying => NearbyVisibility.Visible,
            _ => publishWhenNotRoleplaying ? NearbyVisibility.Visible : NearbyVisibility.HiddenNotRoleplaying,
        };
    }
}
