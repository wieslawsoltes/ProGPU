using System.IO;
using System.Resources;

namespace System.Drawing;

/// <summary>
/// Associates a small and optional large toolbox image with a component type.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class ToolboxBitmapAttribute : Attribute
{
    private static readonly Size s_smallSize = new(16, 16);
    private static readonly Size s_largeSize = new(32, 32);

    private Image? _smallImage;
    private Image? _largeImage;

    public ToolboxBitmapAttribute(string? imageFile)
    {
        ImageFile = imageFile;
    }

    public ToolboxBitmapAttribute(Type? t)
    {
        ToolboxType = t;
    }

    public ToolboxBitmapAttribute(Type? t, string? name)
    {
        ToolboxType = t;
        Name = name;
    }

    public string? ImageFile { get; }

    public string? Name { get; }

    public Type? ToolboxType { get; }

    /// <summary>
    /// Gets an attribute with no configured image. Unresolved lookups use a
    /// visible, ProGPU-drawn generic component glyph.
    /// </summary>
    public static readonly ToolboxBitmapAttribute Default = new((string?)null);

    public Image? GetImage(object? component) => GetImage(component, large: true);

    public Image? GetImage(object? component, bool large) =>
        component is null ? null : GetImage(component.GetType(), large);

    public Image? GetImage(Type? type) => GetImage(type, large: false);

    public Image? GetImage(Type? type, bool large) => GetImage(type, imageName: null, large);

    public Image? GetImage(Type? type, string? imageName, bool large)
    {
        Image? cached = large ? _largeImage : _smallImage;
        if (cached is not null)
        {
            return cached;
        }

        Image? image = LoadConfiguredImage(large);
        if (image is null && type is not null)
        {
            image = GetImageFromResource(type, imageName, large);
        }

        // Bitmap files conventionally carry only the 16x16 toolbox image.
        // Match System.Drawing by synthesizing the optional large image from
        // that real source rather than returning an empty placeholder.
        if (image is null && large)
        {
            Image? smallImage = _smallImage ?? LoadConfiguredImage(large: false);
            if (smallImage is null && type is not null)
            {
                smallImage = GetImageFromResource(type, imageName, large: false);
            }

            if (smallImage is Bitmap smallBitmap)
            {
                image = new Bitmap(smallBitmap, s_largeSize.Width, s_largeSize.Height);
            }

            if (!ReferenceEquals(this, Default))
            {
                _smallImage ??= smallImage;
            }
        }

        // SharpDevelop and other classic designers construct a Bitmap directly
        // from this result without a null check. Supply a truthful generic
        // component glyph, not a transparent placeholder, when an attribute's
        // file or resource cannot be resolved.
        image ??= CreateDefaultComponentBitmap(large);

        // Default is shared process-wide. Do not let one component type's
        // conventional resource become the cached default for every type.
        if (!ReferenceEquals(this, Default))
        {
            if (large)
            {
                _largeImage = image;
            }
            else
            {
                _smallImage = image;
            }
        }

        return image;
    }

    public static Image? GetImageFromResource(Type? type, string? imageName, bool large)
    {
        if (type is null)
        {
            return null;
        }

        try
        {
            foreach (string candidate in GetResourceCandidates(type, imageName))
            {
                using Stream? stream = OpenResourceStream(type, candidate);
                if (stream is null)
                {
                    continue;
                }

                Image? image = LoadToolboxBitmap(stream, candidate, large);
                if (image is not null)
                {
                    return image;
                }
            }
        }
        catch (Exception exception) when (IsRecoverableImageException(exception))
        {
            return null;
        }

        return null;
    }

    private Image? LoadConfiguredImage(bool large)
    {
        if (!string.IsNullOrWhiteSpace(ImageFile))
        {
            return LoadImageFromFile(ImageFile, large);
        }

        return ToolboxType is null
            ? null
            : GetImageFromResource(ToolboxType, Name, large);
    }

    private static Image? LoadImageFromFile(string imageFile, bool large)
    {
        // Non-icon files provide the small image only. The caller creates the
        // conventional 32x32 image from that source when requested.
        bool isIconName = HasExtension(imageFile, ".ico");
        if (large && !isIconName)
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(imageFile);
            return LoadToolboxBitmap(stream, imageFile, large);
        }
        catch (Exception exception) when (IsRecoverableImageException(exception))
        {
            return null;
        }
    }

    private static Image? LoadToolboxBitmap(Stream stream, string sourceName, bool large)
    {
        Bitmap? bitmap = null;
        try
        {
            bool isIcon = HasExtension(sourceName, ".ico") || HasIconHeader(stream);
            bitmap = new Bitmap(stream);

            // Remove the conventional bottom-left color key before resizing so
            // interpolation does not blend an opaque background into icon edges.
            if (!isIcon)
            {
                MakeBackgroundAlphaZero(bitmap);
            }

            int expectedSize = large ? s_largeSize.Width : s_smallSize.Width;
            if (isIcon && (bitmap.Width != expectedSize || bitmap.Height != expectedSize))
            {
                var result = new Bitmap(bitmap, expectedSize, expectedSize);
                bitmap.Dispose();
                bitmap = null;
                return result;
            }

            if (large && (bitmap.Width != s_largeSize.Width || bitmap.Height != s_largeSize.Height))
            {
                var result = new Bitmap(bitmap, s_largeSize.Width, s_largeSize.Height);
                bitmap.Dispose();
                bitmap = null;
                return result;
            }

            Bitmap resultBitmap = bitmap;
            bitmap = null;
            return resultBitmap;
        }
        catch (Exception exception) when (IsRecoverableImageException(exception))
        {
            bitmap?.Dispose();
            return null;
        }
    }

    private static IEnumerable<string> GetResourceCandidates(Type type, string? imageName)
    {
        string name = string.IsNullOrEmpty(imageName) ? type.Name : imageName;
        string extension = Path.GetExtension(name);

        if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
        {
            yield return name;
            yield break;
        }

        // Preserve the desktop lookup order for old assemblies that embed an
        // extensionless bitmap/icon, followed by conventional named resources.
        yield return name;
        yield return $"{name}.bmp";
        yield return $"{name}.ico";
    }

    private static Stream? OpenResourceStream(Type type, string resourceName)
    {
        var assembly = type.Assembly;

        Stream? stream = assembly.GetManifestResourceStream(type, resourceName);
        if (stream is not null)
        {
            return stream;
        }

        stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is not null)
        {
            return stream;
        }

        string suffix = $".{resourceName}";
        string? uniqueMatch = null;
        foreach (string manifestName in assembly.GetManifestResourceNames())
        {
            if (!manifestName.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            if (uniqueMatch is not null)
            {
                // A suffix-only lookup is unsafe when more than one namespace
                // publishes the same icon name. Fail closed instead of choosing
                // a nondeterministic resource.
                return null;
            }

            uniqueMatch = manifestName;
        }

        if (uniqueMatch is not null)
        {
            return assembly.GetManifestResourceStream(uniqueMatch);
        }

        // Compiled .resx files publish values inside a .resources manifest
        // instead of exposing each image as a raw manifest stream. Portable
        // designers use streams, byte arrays, and data-URI strings here.
        try
        {
            object? value = new ResourceManager(type).GetObject(resourceName);
            return value switch
            {
                byte[] bytes => new MemoryStream(bytes, writable: false),
                Stream resourceStream => resourceStream,
                string encodedImage => OpenEncodedResource(encodedImage),
                _ => null
            };
        }
        catch (MissingManifestResourceException)
        {
            return null;
        }
    }

    private static MemoryStream? OpenEncodedResource(string value)
    {
        const string Marker = ";base64,";
        if (!value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int markerIndex = value.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        try
        {
            return new MemoryStream(
                Convert.FromBase64String(value[(markerIndex + Marker.Length)..]),
                writable: false);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool HasIconHeader(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return false;
        }

        long position = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[4];
            int read = stream.Read(header);
            return read == header.Length
                && header[0] == 0
                && header[1] == 0
                && header[2] == 1
                && header[3] == 0;
        }
        finally
        {
            stream.Position = position;
        }
    }

    private static void MakeBackgroundAlphaZero(Bitmap bitmap)
    {
        bitmap.MakeTransparent();
    }

    private static Bitmap CreateDefaultComponentBitmap(bool large)
    {
        int size = large ? s_largeSize.Width : s_smallSize.Width;
        float scale = size / 16f;
        var bitmap = new Bitmap(size, size);

        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (var shadow = new SolidBrush(Color.FromArgb(110, 32, 45, 61)))
        using (var body = new SolidBrush(Color.FromArgb(255, 236, 241, 247)))
        using (var accent = new SolidBrush(Color.FromArgb(255, 47, 111, 191)))
        using (var border = new Pen(Color.FromArgb(255, 45, 62, 80), scale))
        {
            graphics.Clear(Color.Transparent);
            graphics.FillRectangle(shadow, 3f * scale, 3f * scale, 10f * scale, 10f * scale);
            graphics.FillRectangle(body, 2f * scale, 2f * scale, 10f * scale, 10f * scale);
            graphics.DrawRectangle(border, 2f * scale, 2f * scale, 10f * scale, 10f * scale);
            graphics.FillRectangle(accent, 4f * scale, 5f * scale, 6f * scale, 2f * scale);
            graphics.FillRectangle(accent, 4f * scale, 9f * scale, 4f * scale, 1f * scale);
            graphics.FillEllipse(accent, 9f * scale, 8f * scale, 3f * scale, 3f * scale);
        }

        return bitmap;
    }

    private static bool HasExtension(string path, string extension) =>
        Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsRecoverableImageException(Exception exception) =>
        exception is ArgumentException
            or IOException
            or NotSupportedException
            or InvalidOperationException
            or UnauthorizedAccessException;
}
