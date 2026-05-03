using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.McdfShare;

namespace UmbraSync.WebAPI.SignalR;

public sealed partial class ApiController
{
    public async Task<List<McdfShareEntryDto>> McdfShareGetOwn()
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<McdfShareEntryDto>>(nameof(McdfShareGetOwn)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(McdfShareGetOwn));
            return [];
        }
    }

    public async Task<List<McdfShareEntryDto>> McdfShareGetShared()
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<McdfShareEntryDto>>(nameof(McdfShareGetShared)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(McdfShareGetShared));
            return [];
        }
    }

    public async Task McdfShareUpload(McdfShareUploadRequestDto requestDto)
    {
        if (!IsConnected) return;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _connectionCancellationTokenSource.Token);
            await _mareHub!.InvokeAsync(nameof(McdfShareUpload), requestDto, linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(McdfShareUpload));
            throw new InvalidOperationException($"Error during {nameof(McdfShareUpload)}", ex);
        }
    }

    public async Task<McdfSharePayloadDto?> McdfShareDownload(Guid shareId)
    {
        if (!IsConnected) return null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _connectionCancellationTokenSource.Token);
            return await _mareHub!.InvokeAsync<McdfSharePayloadDto?>(nameof(McdfShareDownload), shareId, linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(McdfShareDownload));
            throw new InvalidOperationException($"Error during {nameof(McdfShareDownload)}", ex);
        }
    }

    public async Task<bool> McdfShareDelete(Guid shareId)
    {
        if (!IsConnected) return false;
        try
        {
            return await _mareHub!.InvokeAsync<bool>(nameof(McdfShareDelete), shareId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(McdfShareDelete));
            throw new InvalidOperationException($"Error during {nameof(McdfShareDelete)}", ex);
        }
    }

    public async Task<McdfShareEntryDto?> McdfShareUpdate(McdfShareUpdateRequestDto requestDto)
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<McdfShareEntryDto?>(nameof(McdfShareUpdate), requestDto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(McdfShareUpdate));
            throw new InvalidOperationException($"Error during {nameof(McdfShareUpdate)}", ex);
        }
    }
}