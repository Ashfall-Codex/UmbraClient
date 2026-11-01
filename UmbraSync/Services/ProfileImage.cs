using SixLabors.ImageSharp;
using System.Runtime.CompilerServices;

namespace UmbraSync.Services;

public sealed class ProfileImage
{
    private const int MaxEncodedBytes = 16 * 1024 * 1024;
    private const int MaxDimension = 4096;

    private static readonly ConditionalWeakTable<string, ProfileImage> DecodedImages = [];

    public static readonly ProfileImage Empty = new(null, null, null);

    private readonly Lazy<byte[]>? _decoded;
    private readonly ProfileImageStore? _store;

    private ProfileImage(Lazy<byte[]>? decoded, ProfileImageStore? store, string? storedHash)
    {
        _decoded = decoded;
        _store = store;
        StoredHash = storedHash;
    }

    internal string? StoredHash { get; }

    internal object TextureKey => (object?)StoredHash ?? this;

    public bool HasImage => _decoded != null || StoredHash != null;

    internal bool IsAwaitingStorage => _decoded != null;

    public byte[] ReadNow() => _store != null
        ? _store.ReadNow(StoredHash!)
        : _decoded?.Value ?? Array.Empty<byte>();

    public static ProfileImage FromBase64(string? base64) => string.IsNullOrEmpty(base64)
        ? Empty
        : DecodedImages.GetValue(base64, static value => new ProfileImage(new Lazy<byte[]>(() => Decode(value)), null, null));

    internal static ProfileImage FromStore(ProfileImageStore store, string hash) => new(null, store, hash);

    internal ProfileImage StoreIn(ProfileImageStore store)
    {
        if (StoredHash != null || !HasImage) return this;

        var bytes = ReadNow();
        if (bytes.Length == 0) return Empty;

        var hash = store.Put(bytes);
        return hash == null ? this : FromStore(store, hash);
    }

    internal static byte[] Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return Array.Empty<byte>();

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return IsAcceptable(bytes) ? bytes : Array.Empty<byte>();
        }
        catch (FormatException)
        {
            return Array.Empty<byte>();
        }
    }

    private static bool IsAcceptable(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaxEncodedBytes) return false;

        try
        {
            var info = Image.Identify(bytes);
            return info.Width is > 0 and <= MaxDimension && info.Height is > 0 and <= MaxDimension;
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            return false;
        }
    }
}
