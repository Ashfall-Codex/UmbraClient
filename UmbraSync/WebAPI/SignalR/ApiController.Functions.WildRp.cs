using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.WildRp;

namespace UmbraSync.WebAPI.SignalR;

public partial class ApiController
{
    public async Task<WildRpAnnouncementDto?> WildRpAnnounce(WildRpAnnounceRequestDto request)
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<WildRpAnnouncementDto?>(nameof(WildRpAnnounce), request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error announcing wild RP");
            return null;
        }
    }

    public async Task<bool> WildRpWithdraw()
    {
        if (!IsConnected) return false;
        try
        {
            return await _mareHub!.InvokeAsync<bool>(nameof(WildRpWithdraw)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error withdrawing wild RP announcement");
            return false;
        }
    }

    public async Task<WildRpListResponseDto> WildRpList(WildRpListRequestDto request)
    {
        if (!IsConnected) return new WildRpListResponseDto();
        try
        {
            return await _mareHub!.InvokeAsync<WildRpListResponseDto>(nameof(WildRpList), request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error listing wild RP announcements");
            return new WildRpListResponseDto();
        }
    }

    public async Task<WildRpAnnouncementDto?> WildRpGetOwn()
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<WildRpAnnouncementDto?>(nameof(WildRpGetOwn)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error getting own wild RP announcement");
            return null;
        }
    }
}
