using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.StageShare;

namespace UmbraSync.WebAPI.SignalR;

public sealed partial class ApiController
{
    public async Task StageShareUpload(StageShareUploadRequestDto dto)
    {
        if (!IsConnected) return;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _connectionCancellationTokenSource.Token);
            await _mareHub!.InvokeAsync(nameof(StageShareUpload), dto, linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(StageShareUpload));
            throw new InvalidOperationException($"Error during {nameof(StageShareUpload)}", ex);
        }
    }

    public async Task<StageSharePayloadDto?> StageShareDownload(Guid shareId)
    {
        if (!IsConnected) return null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _connectionCancellationTokenSource.Token);
            return await _mareHub!.InvokeAsync<StageSharePayloadDto?>(nameof(StageShareDownload), shareId, linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(StageShareDownload));
            throw new InvalidOperationException($"Error during {nameof(StageShareDownload)}", ex);
        }
    }

    public async Task<List<StageShareEntryDto>> StageShareGetOwn()
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<StageShareEntryDto>>(nameof(StageShareGetOwn)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(StageShareGetOwn));
            return [];
        }
    }

    public async Task<List<StageShareEntryDto>> StageShareGetForLocation(StageLocationDto location)
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<StageShareEntryDto>>(nameof(StageShareGetForLocation), location).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(StageShareGetForLocation));
            return [];
        }
    }

    public async Task<StageShareEntryDto?> StageShareUpdate(StageShareUpdateRequestDto dto)
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<StageShareEntryDto?>(nameof(StageShareUpdate), dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(StageShareUpdate));
            throw new InvalidOperationException($"Error during {nameof(StageShareUpdate)}", ex);
        }
    }

    public async Task<bool> StageShareDelete(Guid shareId)
    {
        if (!IsConnected) return false;
        try
        {
            return await _mareHub!.InvokeAsync<bool>(nameof(StageShareDelete), shareId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(StageShareDelete));
            throw new InvalidOperationException($"Error during {nameof(StageShareDelete)}", ex);
        }
    }
}
