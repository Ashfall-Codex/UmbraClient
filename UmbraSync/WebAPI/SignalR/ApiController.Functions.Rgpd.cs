using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.Rgpd;

namespace UmbraSync.WebAPI.SignalR;

public sealed partial class ApiController
{
    public async Task<RgpdDataExportDto?> UserRgpdExportData()
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<RgpdDataExportDto>(nameof(UserRgpdExportData)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(UserRgpdExportData));
            return null;
        }
    }

    public async Task UserRgpdDeleteAllData()
    {
        if (!IsConnected) return;
        try
        {
            await _mareHub!.SendAsync(nameof(UserRgpdDeleteAllData)).ConfigureAwait(false);
            await CreateConnections().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(UserRgpdDeleteAllData));
        }
    }
}
