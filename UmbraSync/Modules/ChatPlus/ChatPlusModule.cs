using Dalamud.Interface;
using Microsoft.Extensions.DependencyInjection;
using UmbraSync.Modules.ChatPlus.Services;
using UmbraSync.Modules.ChatPlus.UI;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.UI;

namespace UmbraSync.Modules.ChatPlus;

public sealed class ChatPlusModule : IUmbraModule
{
    public ModuleId Id => ModuleId.ChatPlus;
    public string TitleKey => "Modules.ChatPlus.Title";
    public string DescriptionKey => "Modules.ChatPlus.Desc";
    public FontAwesomeIcon Icon => FontAwesomeIcon.Comments;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<PartyListTypingService>();
        services.AddSingleton<TypingIndicatorStateService>();
        services.AddSingleton<TypingRemoteNotificationService>();
        services.AddSingleton<ChatTwoCompatibilityService>();

        services.AddHostedService(sp => sp.GetRequiredService<PartyListTypingService>());
        services.AddHostedService(sp => sp.GetRequiredService<ChatTwoCompatibilityService>());

        services.AddScoped<ChatTargetSoundService>();
        services.AddScoped<ChatTypingDetectionService>();
        services.AddScoped<IRuntimeScopedService>(sp => sp.GetRequiredService<ChatTargetSoundService>());
        services.AddScoped<IRuntimeScopedService>(sp => sp.GetRequiredService<ChatTypingDetectionService>());

        services.AddScoped<WindowMediatorSubscriberBase, TypingIndicatorOverlay>();
    }
}
