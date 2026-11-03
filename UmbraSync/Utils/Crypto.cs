using Blake3;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace UmbraSync.Utils;

public readonly record struct FileHashes(string Sha1, string Blake3);

public static class Crypto
{
    private const int FileBufferSize = 64 * 1024;
    private const int Blake3SelfTestLength = 102400;
    private const string Blake3SelfTestHash = "BC3E3D41A1146B069ABFFAD3C0D44860CF664390AFCE4D9661F7902E7943E085";

    public static string GetFileHash(this string filePath)
    {
        using var stream = OpenForHashing(filePath);
        return Convert.ToHexString(SHA1.HashData(stream));
    }

    public static FileHashes GetFileHashes(this string filePath)
    {
        using var stream = OpenForHashing(filePath);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var blake3 = Hasher.New();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(FileBufferSize);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, FileBufferSize)) > 0)
            {
                sha1.AppendData(buffer, 0, read);
                blake3.Update(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        Span<byte> blake3Hash = stackalloc byte[32];
        blake3.Finalize(blake3Hash);
        return new FileHashes(Convert.ToHexString(sha1.GetHashAndReset()), Convert.ToHexString(blake3Hash));
    }

    public static bool Blake3SelfTest()
    {
        byte[] input = new byte[Blake3SelfTestLength];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 251);
        }

        Span<byte> hash = stackalloc byte[32];
        using var hasher = Hasher.New();
        hasher.Update(input);
        hasher.Finalize(hash);
        return string.Equals(Convert.ToHexString(hash), Blake3SelfTestHash, StringComparison.Ordinal);
    }

    public static string GetHash256(this string stringToHash)
    {
        return GetOrComputeHashSHA256(stringToHash);
    }

    private static string GetOrComputeHashSHA256(string stringToCompute)
    {
        ArgumentNullException.ThrowIfNull(stringToCompute);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stringToCompute));
        return BitConverter.ToString(hash).Replace("-", "", StringComparison.Ordinal);
    }

    private static FileStream OpenForHashing(string filePath)
        => new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize, FileOptions.SequentialScan);
}
