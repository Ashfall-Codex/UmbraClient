using Microsoft.AspNetCore.SignalR.Client;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.McdfShare;

namespace UmbraSync.WebAPI.SignalR;

public sealed partial class ApiController
{
    // Jusqu'à cette taille on reste sur l'invocation classique (compatible avec un serveur sans streaming)
    private const int McdfInlineUploadLimit = 90 * 1024 * 1024;
    private const int McdfStreamChunkSize = 1024 * 1024;

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

    public async Task<bool> McdfShareUpload(McdfShareUploadRequestDto requestDto)
    {
        if (!IsConnected) return false;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _connectionCancellationTokenSource.Token);
            if (requestDto.CipherData.Length <= McdfInlineUploadLimit)
            {
                return await _mareHub!.InvokeAsync<bool>(nameof(McdfShareUpload), requestDto, linkedCts.Token).ConfigureAwait(false);
            }

            var header = new McdfShareUploadRequestDto
            {
                ShareId = requestDto.ShareId,
                Description = requestDto.Description,
                CipherData = [],
                Nonce = requestDto.Nonce,
                Salt = requestDto.Salt,
                Tag = requestDto.Tag,
                ExpiresAtUtc = requestDto.ExpiresAtUtc,
                AllowedIndividuals = requestDto.AllowedIndividuals,
                AllowedSyncshells = requestDto.AllowedSyncshells
            };
            return await _mareHub!.InvokeAsync<bool>("McdfShareUploadStream", header, McdfChunks(requestDto.CipherData, linkedCts.Token), linkedCts.Token).ConfigureAwait(false);
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

    private static async IAsyncEnumerable<byte[]> McdfChunks(byte[] data, [EnumeratorCancellation] CancellationToken token)
    {
        for (int offset = 0; offset < data.Length; offset += McdfStreamChunkSize)
        {
            token.ThrowIfCancellationRequested();
            yield return data.AsSpan(offset, Math.Min(McdfStreamChunkSize, data.Length - offset)).ToArray();
            await Task.Yield();
        }
    }

    /// <summary>Récupère le chiffré d'un partage volumineux (payload.CipherLength &gt; payload.CipherData.Length).</summary>
    public async Task<byte[]?> McdfShareDownloadCipher(McdfSharePayloadDto payload, CancellationToken token)
    {
        if (!IsConnected) return null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _connectionCancellationTokenSource.Token, token);
            var cipher = new byte[payload.CipherLength];
            long received = 0;
            await foreach (var chunk in _mareHub!.StreamAsync<byte[]>("McdfShareDownloadStream", payload.ShareId, linkedCts.Token).ConfigureAwait(false))
            {
                if (received + chunk.Length > cipher.Length) return null;
                chunk.CopyTo(cipher, received);
                received += chunk.Length;
            }
            return received == cipher.Length ? cipher : null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {method}", nameof(McdfShareDownloadCipher));
            throw new InvalidOperationException($"Error during {nameof(McdfShareDownloadCipher)}", ex);
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