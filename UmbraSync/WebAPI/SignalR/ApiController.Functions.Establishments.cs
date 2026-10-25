using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.API.Dto.User;

namespace UmbraSync.WebAPI.SignalR;

public partial class ApiController
{
    public async Task<EstablishmentDto?> EstablishmentCreate(EstablishmentCreateRequestDto request)
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<EstablishmentDto?>(nameof(EstablishmentCreate), request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error creating establishment");
            return null;
        }
    }

    public async Task<bool> EstablishmentUpdate(EstablishmentUpdateRequestDto request)
    {
        if (!IsConnected) return false;
        try
        {
            return await _mareHub!.InvokeAsync<bool>(nameof(EstablishmentUpdate), request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error updating establishment {id}", request.Id);
            return false;
        }
    }

    public async Task<bool> EstablishmentDelete(Guid id)
    {
        if (!IsConnected) return false;
        try
        {
            return await _mareHub!.InvokeAsync<bool>(nameof(EstablishmentDelete), id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error deleting establishment {id}", id);
            return false;
        }
    }

    public async Task<EstablishmentDto?> EstablishmentGetById(Guid id)
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<EstablishmentDto?>(nameof(EstablishmentGetById), id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error getting establishment {id}", id);
            return null;
        }
    }

    public async Task<EstablishmentListResponseDto?> EstablishmentList(EstablishmentListRequestDto request)
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<EstablishmentListResponseDto?>(nameof(EstablishmentList), request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error listing establishments");
            return null;
        }
    }

    public async Task<EstablishmentNearbyResponseDto?> EstablishmentGetNearby(EstablishmentNearbyRequestDto request)
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<EstablishmentNearbyResponseDto?>(nameof(EstablishmentGetNearby), request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error getting nearby establishments");
            return null;
        }
    }

    public async Task<List<EstablishmentDto>> EstablishmentGetByOwner()
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<EstablishmentDto>>(nameof(EstablishmentGetByOwner)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error getting owned establishments");
            return [];
        }
    }

    public async Task<List<RpProfileSummaryDto>> EstablishmentGetOwnRpProfiles()
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<RpProfileSummaryDto>>(nameof(EstablishmentGetOwnRpProfiles)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error getting own RP profiles");
            return [];
        }
    }

    public async Task<EstablishmentEventDto?> EstablishmentEventUpsert(EstablishmentEventUpsertRequestDto request)
    {
        if (!IsConnected) return null;
        try
        {
            return await _mareHub!.InvokeAsync<EstablishmentEventDto?>(nameof(EstablishmentEventUpsert), request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error upserting establishment event for {id}", request.EstablishmentId);
            return null;
        }
    }

    public async Task<bool> EstablishmentEventDelete(Guid eventId)
    {
        if (!IsConnected) return false;
        try
        {
            return await _mareHub!.InvokeAsync<bool>(nameof(EstablishmentEventDelete), eventId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error deleting establishment event {id}", eventId);
            return false;
        }
    }

    public async Task<EstablishmentAffiliationRequestResult> EstablishmentAffiliationRequest(EstablishmentAffiliationRequestDto request)
    {
        if (!IsConnected) return EstablishmentAffiliationRequestResult.NotFound;
        try
        {
            return await _mareHub!.InvokeAsync<EstablishmentAffiliationRequestResult>(nameof(EstablishmentAffiliationRequest), request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error requesting affiliation to establishment {id}", request.EstablishmentId);
            return EstablishmentAffiliationRequestResult.NotFound;
        }
    }

    public async Task<bool> EstablishmentAffiliationRespond(Guid affiliationId, bool accept)
    {
        if (!IsConnected) return false;
        try
        {
            return await _mareHub!.InvokeAsync<bool>(nameof(EstablishmentAffiliationRespond), affiliationId, accept).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error responding to affiliation {id}", affiliationId);
            return false;
        }
    }

    public async Task<bool> EstablishmentAffiliationRemove(Guid affiliationId)
    {
        if (!IsConnected) return false;
        try
        {
            return await _mareHub!.InvokeAsync<bool>(nameof(EstablishmentAffiliationRemove), affiliationId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error removing affiliation {id}", affiliationId);
            return false;
        }
    }

    public async Task<bool> EstablishmentAffiliationSetVisibility(Guid affiliationId, bool showOnProfile)
    {
        if (!IsConnected) return false;
        try
        {
            return await _mareHub!.InvokeAsync<bool>(nameof(EstablishmentAffiliationSetVisibility), affiliationId, showOnProfile).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error setting visibility of affiliation {id}", affiliationId);
            return false;
        }
    }

    public async Task<List<EstablishmentAffiliationDto>> EstablishmentAffiliationGetForEstablishment(Guid establishmentId)
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<EstablishmentAffiliationDto>>(nameof(EstablishmentAffiliationGetForEstablishment), establishmentId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error getting affiliations of establishment {id}", establishmentId);
            return [];
        }
    }

    public async Task<List<EstablishmentAffiliationDto>> EstablishmentAffiliationGetMine()
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<EstablishmentAffiliationDto>>(nameof(EstablishmentAffiliationGetMine)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error getting own affiliations");
            return [];
        }
    }

    public async Task<List<EstablishmentProfileEntryDto>> EstablishmentGetForCharacter(UserDto user)
    {
        if (!IsConnected) return [];
        try
        {
            return await _mareHub!.InvokeAsync<List<EstablishmentProfileEntryDto>>(nameof(EstablishmentGetForCharacter), user).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error getting establishments of a character of {uid}", user.User.UID);
            return [];
        }
    }
}
