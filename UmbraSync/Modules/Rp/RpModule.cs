using Dalamud.Interface;
using Microsoft.Extensions.DependencyInjection;
using UmbraSync.Modules.Rp.Services;
using UmbraSync.Modules.Rp.UI;
using UmbraSync.Modules.Rp.WebAPI;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.UI;
using UmbraSync.WebAPI;

namespace UmbraSync.Modules.Rp;

public sealed class RpModule : IUmbraModule
{
    public ModuleId Id => ModuleId.Rp;
    public string TitleKey => "Modules.Rp.Title";
    public string DescriptionKey => "Modules.Rp.Desc";
    public FontAwesomeIcon Icon => FontAwesomeIcon.TheaterMasks;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<EstablishmentProximityService>();
        services.AddSingleton<EstablishmentReminderService>();
        services.AddSingleton<EstablishmentSyncSlotService>();
        services.AddSingleton<IRuntimeScopedService>(sp => sp.GetRequiredService<EstablishmentProximityService>());
        services.AddSingleton<IRuntimeScopedService>(sp => sp.GetRequiredService<EstablishmentReminderService>());
        services.AddSingleton<IRuntimeScopedService>(sp => sp.GetRequiredService<EstablishmentSyncSlotService>());
        services.AddSingleton<RpJournalService>();
        services.AddSingleton<IRuntimeScopedService>(sp => sp.GetRequiredService<RpJournalService>());
        services.AddSingleton<AshfallConnectAutoSyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<AshfallConnectAutoSyncService>());

        services.AddScoped<AshfallConnectService>();
        services.AddScoped<ChatNameReplacementService>();
        services.AddScoped<ChatEmoteHighlightService>();
        services.AddScoped<ChatProximityBlendService>();
        services.AddScoped<IRuntimeScopedService>(sp => sp.GetRequiredService<ChatNameReplacementService>());
        services.AddScoped<IRuntimeScopedService>(sp => sp.GetRequiredService<ChatEmoteHighlightService>());
        services.AddScoped<IRuntimeScopedService>(sp => sp.GetRequiredService<ChatProximityBlendService>());
        services.AddScoped<RpJournalContextMenuService>();
        services.AddScoped<IRuntimeScopedService>(sp => sp.GetRequiredService<RpJournalContextMenuService>());

        services.AddScoped<WindowMediatorSubscriberBase, TargetProfileTooltipUi>();
        services.AddScoped<RpJournalUi>();
        services.AddScoped<WindowMediatorSubscriberBase>(sp => sp.GetRequiredService<RpJournalUi>());
        services.AddScoped<AshfallLinkCodeUi>();
        services.AddScoped<WindowMediatorSubscriberBase>(sp => sp.GetRequiredService<AshfallLinkCodeUi>());
        services.AddScoped<EstablishmentDetailUi>();
        services.AddScoped<WindowMediatorSubscriberBase>(sp => sp.GetRequiredService<EstablishmentDetailUi>());
        services.AddScoped<EstablishmentRegistrationUi>();
        services.AddScoped<WindowMediatorSubscriberBase>(sp => sp.GetRequiredService<EstablishmentRegistrationUi>());
    }
}
