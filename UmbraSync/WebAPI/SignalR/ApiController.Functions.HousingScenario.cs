using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.API.Dto.HousingScenario;

namespace UmbraSync.WebAPI.SignalR;

public sealed partial class ApiController
{
    public async Task<HousingScenarioUploadResultDto?> HousingScenarioUpload(HousingScenarioUploadRequestDto dto)
    {
        if (!IsConnected) return null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _connectionCancellationTokenSource.Token);
            return await _mareHub!.InvokeAsync<HousingScenarioUploadResultDto>(nameof(HousingScenarioUpload), dto, linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(HousingScenarioUpload));
            throw new InvalidOperationException($"Error during {nameof(HousingScenarioUpload)}", ex);
        }
    }

    public async Task<HousingScenarioPayloadDto?> HousingScenarioDownload(Guid shareId)
    {
        if (!IsConnected) return null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _connectionCancellationTokenSource.Token);
            return await _mareHub!.InvokeAsync<HousingScenarioPayloadDto?>(nameof(HousingScenarioDownload), shareId, linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(HousingScenarioDownload));
            throw new InvalidOperationException($"Error during {nameof(HousingScenarioDownload)}", ex);
        }
    }

    public async Task<List<HousingScenarioEntryDto>> HousingScenarioGetOwn()
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<HousingScenarioEntryDto>>(nameof(HousingScenarioGetOwn)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(HousingScenarioGetOwn));
            return [];
        }
    }

    public async Task<List<HousingScenarioEntryDto>?> HousingScenarioGetDelegatedToMe()
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<List<HousingScenarioEntryDto>>(nameof(HousingScenarioGetDelegatedToMe)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "{method} indisponible sur ce serveur", nameof(HousingScenarioGetDelegatedToMe));
            return null;
        }
    }

    public async Task<List<HousingScenarioEntryDto>> HousingScenarioGetForLocation(LocationInfo location)
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<HousingScenarioEntryDto>>(nameof(HousingScenarioGetForLocation), location).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(HousingScenarioGetForLocation));
            return [];
        }
    }

    public async Task<HousingScenarioEntryDto?> HousingScenarioUpdate(HousingScenarioUpdateRequestDto dto)
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<HousingScenarioEntryDto?>(nameof(HousingScenarioUpdate), dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(HousingScenarioUpdate));
            throw new InvalidOperationException($"Error during {nameof(HousingScenarioUpdate)}", ex);
        }
    }

    public async Task<bool> HousingScenarioDelete(Guid shareId)
    {
        if (!IsConnected) return false;
        try
        {
            return await _mareHub!.InvokeAsync<bool>(nameof(HousingScenarioDelete), shareId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(HousingScenarioDelete));
            throw new InvalidOperationException($"Error during {nameof(HousingScenarioDelete)}", ex);
        }
    }
}
