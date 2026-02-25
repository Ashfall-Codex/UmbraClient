using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.API.Dto.HousingShare;

namespace UmbraSync.WebAPI.SignalR;

public sealed partial class ApiController
{
    public async Task HousingShareUpload(HousingShareUploadRequestDto dto)
    {
        if (!IsConnected) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await _mareHub!.InvokeAsync(nameof(HousingShareUpload), dto, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(HousingShareUpload));
            throw new InvalidOperationException($"Error during {nameof(HousingShareUpload)}", ex);
        }
    }

    public async Task<HousingSharePayloadDto?> HousingShareDownload(Guid shareId)
    {
        if (!IsConnected) return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            return await _mareHub!.InvokeAsync<HousingSharePayloadDto?>(nameof(HousingShareDownload), shareId, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(HousingShareDownload));
            throw new InvalidOperationException($"Error during {nameof(HousingShareDownload)}", ex);
        }
    }

    public async Task<List<HousingShareEntryDto>> HousingShareGetOwn()
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<HousingShareEntryDto>>(nameof(HousingShareGetOwn)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(HousingShareGetOwn));
            return [];
        }
    }

    public async Task<List<HousingShareEntryDto>> HousingShareGetForLocation(LocationInfo location)
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<HousingShareEntryDto>>(nameof(HousingShareGetForLocation), location).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(HousingShareGetForLocation));
            return [];
        }
    }

    public async Task<HousingShareEntryDto?> HousingShareUpdate(HousingShareUpdateRequestDto dto)
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<HousingShareEntryDto?>(nameof(HousingShareUpdate), dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(HousingShareUpdate));
            throw new InvalidOperationException($"Error during {nameof(HousingShareUpdate)}", ex);
        }
    }

    public async Task<bool> HousingShareDelete(Guid shareId)
    {
        if (!IsConnected) return false;
        try
        {
            return await _mareHub!.InvokeAsync<bool>(nameof(HousingShareDelete), shareId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(HousingShareDelete));
            throw new InvalidOperationException($"Error during {nameof(HousingShareDelete)}", ex);
        }
    }
}
