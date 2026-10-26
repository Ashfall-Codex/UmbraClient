using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Numerics;
using System.Runtime.InteropServices;

namespace UmbraSync.UI.Components;

public enum ProfileImageKind
{
    Portrait,
    Banner,
}

// Cadrage normalisé : centre en fraction de la source, et zoom (1 = plus grand cadre au bon ratio qui tient dans l'image).
[StructLayout(LayoutKind.Auto)]
public readonly record struct ProfileImageCrop(float CenterX, float CenterY, float Zoom)
{
    public const float MinZoom = 1f;
    public const float MaxZoom = 4f;

    public static ProfileImageCrop Centered => new(0.5f, 0.5f, MinZoom);

    public Vector2 VisibleSize(int sourceWidth, int sourceHeight, float targetAspect)
    {
        float sourceAspect = (float)sourceWidth / sourceHeight;
        Vector2 size = sourceAspect > targetAspect
            ? new Vector2(targetAspect / sourceAspect, 1f)
            : new Vector2(1f, sourceAspect / targetAspect);
        return size / Zoom;
    }

    public (Vector2 Uv0, Vector2 Uv1) GetUv(int sourceWidth, int sourceHeight, float targetAspect)
    {
        Vector2 size = VisibleSize(sourceWidth, sourceHeight, targetAspect);
        Vector2 min = new Vector2(CenterX, CenterY) - (size / 2f);
        return (min, min + size);
    }

    public ProfileImageCrop Clamped(int sourceWidth, int sourceHeight, float targetAspect)
    {
        ProfileImageCrop zoomed = this with { Zoom = Math.Clamp(Zoom, MinZoom, MaxZoom) };
        Vector2 half = zoomed.VisibleSize(sourceWidth, sourceHeight, targetAspect) / 2f;
        return zoomed with { CenterX = ClampCenter(CenterX, half.X), CenterY = ClampCenter(CenterY, half.Y) };
    }

    // Déplacement exprimé en fraction du cadre : glisser sur toute la largeur de l'aperçu décale d'un cadre entier.
    public ProfileImageCrop Pan(Vector2 deltaInFrame, int sourceWidth, int sourceHeight, float targetAspect)
    {
        Vector2 size = VisibleSize(sourceWidth, sourceHeight, targetAspect);
        return (this with { CenterX = CenterX - (deltaInFrame.X * size.X), CenterY = CenterY - (deltaInFrame.Y * size.Y) })
            .Clamped(sourceWidth, sourceHeight, targetAspect);
    }

    // Zoom qui garde immobile le point de l'image situé sous l'ancre (fraction du cadre, 0,5 = centre).
    public ProfileImageCrop ZoomAt(float zoom, Vector2 anchorInFrame, int sourceWidth, int sourceHeight, float targetAspect)
    {
        (Vector2 uv0, _) = GetUv(sourceWidth, sourceHeight, targetAspect);
        Vector2 point = uv0 + (anchorInFrame * VisibleSize(sourceWidth, sourceHeight, targetAspect));
        ProfileImageCrop zoomed = this with { Zoom = Math.Clamp(zoom, MinZoom, MaxZoom) };
        Vector2 size = zoomed.VisibleSize(sourceWidth, sourceHeight, targetAspect);
        Vector2 center = point - (anchorInFrame * size) + (size / 2f);
        return (zoomed with { CenterX = center.X, CenterY = center.Y }).Clamped(sourceWidth, sourceHeight, targetAspect);
    }

    private static float ClampCenter(float center, float half)
    {
        float bound = MathF.Min(half, 0.5f);
        return Math.Clamp(center, bound, 1f - bound);
    }
}

// Image prête à recadrer : déjà orientée, réduite et reconstruite à partir des seuls pixels.
public sealed class ProfileImageSource : IDisposable
{
    internal ProfileImageSource(Image<Rgba32> image, bool isPhoto)
    {
        Image = image;
        IsPhoto = isPhoto;
    }

    internal Image<Rgba32> Image { get; }
    internal bool IsPhoto { get; }
    public int Width => Image.Width;
    public int Height => Image.Height;

    public byte[] GetRgba()
    {
        byte[] rgba = new byte[Width * Height * 4];
        Image.CopyPixelDataTo(rgba);
        return rgba;
    }

    public void Dispose() => Image.Dispose();
}

public static class ProfileImageProcessor
{
    public const int MaxSourceBytes = 32 * 1024 * 1024;
    public const int BannerMaxBytes = 1536 * 1024;
    public const int PortraitMaxBytes = 1024 * 1024;

    private const long MaxSourcePixels = 50_000_000;
    private const int WorkingMaxDimension = 2048;
    private const int JpegQuality = 90;

    private static readonly DecoderOptions SourceDecoderOptions = new()
    {
        Configuration = new Configuration(new PngConfigurationModule(), new JpegConfigurationModule()),
        MaxFrames = 1,
    };

    private static readonly PngEncoder LosslessEncoder = new() { ColorType = PngColorType.RgbWithAlpha };
    private static readonly PngEncoder OpaqueLosslessEncoder = new() { ColorType = PngColorType.Rgb };
    private static readonly JpegEncoder PhotoEncoder = new() { Quality = JpegQuality };

    public static Size TargetSize(ProfileImageKind kind)
        => kind == ProfileImageKind.Banner ? new Size(1200, 250) : new Size(512, 512);

    public static float TargetAspect(ProfileImageKind kind)
    {
        Size size = TargetSize(kind);
        return (float)size.Width / size.Height;
    }

    public static int MaxOutputBytes(ProfileImageKind kind)
        => kind == ProfileImageKind.Banner ? BannerMaxBytes : PortraitMaxBytes;

    public static ProfileImageSource? TryLoad(byte[] file)
    {
        if (file.Length == 0 || file.Length > MaxSourceBytes) return null;

        try
        {
            ImageInfo info = Image.Identify(SourceDecoderOptions, file);
            if ((long)info.Width * info.Height > MaxSourcePixels) return null;

            using Image<Rgba32> decoded = Image.Load<Rgba32>(SourceDecoderOptions, file);
            bool isPhoto = decoded.Metadata.DecodedImageFormat == JpegFormat.Instance;
            decoded.Mutate(ctx => ctx.AutoOrient());
            if (decoded.Width > WorkingMaxDimension || decoded.Height > WorkingMaxDimension)
                decoded.Mutate(ctx => ctx.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(WorkingMaxDimension, WorkingMaxDimension) }));

            return new ProfileImageSource(PixelsOnly(decoded), isPhoto);
        }
        catch (ImageFormatException)
        {
            return null;
        }
        catch (InvalidMemoryOperationException)
        {
            return null;
        }
    }

    public static byte[]? TryRender(ProfileImageSource source, ProfileImageCrop crop, ProfileImageKind kind)
    {
        Size target = TargetSize(kind);
        float aspect = TargetAspect(kind);
        (Vector2 uv0, Vector2 uv1) = crop.Clamped(source.Width, source.Height, aspect).GetUv(source.Width, source.Height, aspect);
        Rectangle area = Rectangle.Intersect(
            Rectangle.FromLTRB(
                (int)MathF.Round(uv0.X * source.Width), (int)MathF.Round(uv0.Y * source.Height),
                (int)MathF.Round(uv1.X * source.Width), (int)MathF.Round(uv1.Y * source.Height)),
            new Rectangle(0, 0, source.Width, source.Height));
        if (area.Width <= 0 || area.Height <= 0) return null;

        Size output = area.Width >= target.Width
            ? target
            : new Size(area.Width, Math.Max(1, (int)MathF.Round(area.Width / aspect)));

        try
        {
            using Image<Rgba32> image = source.Image.Clone(ctx => ctx.Crop(area).Resize(output));
            byte[] encoded;
            if (source.IsPhoto)
            {
                encoded = Encode(image, PhotoEncoder);
            }
            else
            {
                bool opaque = IsOpaque(image);
                encoded = Encode(image, opaque ? OpaqueLosslessEncoder : LosslessEncoder);
                if (encoded.Length > MaxOutputBytes(kind) && opaque)
                    encoded = Encode(image, PhotoEncoder);
            }

            return encoded.Length <= MaxOutputBytes(kind) ? encoded : null;
        }
        catch (InvalidMemoryOperationException)
        {
            return null;
        }
    }
    
    private static Image<Rgba32> PixelsOnly(Image<Rgba32> image)
    {
        Rgba32[] pixels = new Rgba32[image.Width * image.Height];
        image.CopyPixelDataTo(pixels);
        return Image.LoadPixelData<Rgba32>(pixels, image.Width, image.Height);
    }

    private static byte[] Encode(Image image, IImageEncoder encoder)
    {
        using MemoryStream stream = new();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static bool IsOpaque(Image<Rgba32> image)
    {
        bool opaque = true;
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height && opaque; y++)
            {
                foreach (Rgba32 pixel in accessor.GetRowSpan(y))
                {
                    if (pixel.A == byte.MaxValue) continue;
                    opaque = false;
                    break;
                }
            }
        });
        return opaque;
    }
}
