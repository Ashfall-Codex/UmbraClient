using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.DecorCatalog;

namespace UmbraSync.WebAPI.SignalR;

public sealed partial class ApiController
{
    public async Task<DecorCatalogListResponseDto> DecorCatalogList(DecorCatalogListRequestDto dto)
    {
        var failed = new DecorCatalogListResponseDto { TotalCount = -1, Page = dto.Page, PageSize = dto.PageSize };
        if (!IsConnected) return failed;
        try
        {
            return await _mareHub!.InvokeAsync<DecorCatalogListResponseDto>(nameof(DecorCatalogList), dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(DecorCatalogList));
            return failed;
        }
    }

    public async Task<string?> DecorCatalogGetPreview(Guid id)
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<string?>(nameof(DecorCatalogGetPreview), id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(DecorCatalogGetPreview));
            return null;
        }
    }

    public async Task<DecorCatalogPayloadDto?> DecorCatalogDownload(Guid id)
    {
        if (!IsConnected) return null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _connectionCancellationTokenSource.Token);
            return await _mareHub!.InvokeAsync<DecorCatalogPayloadDto?>(nameof(DecorCatalogDownload), id, linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(DecorCatalogDownload));
            throw new InvalidOperationException($"Error during {nameof(DecorCatalogDownload)}", ex);
        }
    }

    public async Task<DecorCatalogEntryDto?> DecorCatalogPublish(DecorCatalogPublishRequestDto dto)
    {
        if (!IsConnected) return null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _connectionCancellationTokenSource.Token);
            return await _mareHub!.InvokeAsync<DecorCatalogEntryDto?>(nameof(DecorCatalogPublish), dto, linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(DecorCatalogPublish));
            throw new InvalidOperationException($"Error during {nameof(DecorCatalogPublish)}", ex);
        }
    }

    public async Task<DecorCatalogEntryDto?> DecorCatalogUpdate(DecorCatalogUpdateRequestDto dto)
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<DecorCatalogEntryDto?>(nameof(DecorCatalogUpdate), dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(DecorCatalogUpdate));
            throw new InvalidOperationException($"Error during {nameof(DecorCatalogUpdate)}", ex);
        }
    }

    public async Task<bool> DecorCatalogDelete(Guid id)
    {
        if (!IsConnected) return false;
        try
        {
            return await _mareHub!.InvokeAsync<bool>(nameof(DecorCatalogDelete), id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(DecorCatalogDelete));
            throw new InvalidOperationException($"Error during {nameof(DecorCatalogDelete)}", ex);
        }
    }

    public async Task<int> DecorCatalogSetFavorite(Guid id, bool favorite)
    {
        if (!IsConnected) return -1;
        try
        {
            return await _mareHub!.InvokeAsync<int>(nameof(DecorCatalogSetFavorite), id, favorite).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(DecorCatalogSetFavorite));
            throw new InvalidOperationException($"Error during {nameof(DecorCatalogSetFavorite)}", ex);
        }
    }

    public async Task DecorCatalogReport(DecorCatalogReportDto dto)
    {
        if (!IsConnected) return;
        try
        {
            await _mareHub!.InvokeAsync(nameof(DecorCatalogReport), dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(DecorCatalogReport));
            throw new InvalidOperationException($"Error during {nameof(DecorCatalogReport)}", ex);
        }
    }
}
