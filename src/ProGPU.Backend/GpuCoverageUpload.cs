using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>
/// Records copies from GPU-computed, tightly packed coverage bytes into a filterable R8 atlas.
/// Rasterization remains GPU-only while avoiding the four-channel storage-texture footprint.
/// </summary>
public static unsafe class GpuCoverageUpload
{
    public const uint CopyRowAlignment = 256;

    public static uint GetBytesPerRow(uint width)
    {
        if (width == 0)
        {
            return 0;
        }

        return checked((width + CopyRowAlignment - 1) & ~(CopyRowAlignment - 1));
    }

    public static uint GetRequiredBytes(uint width, uint height) =>
        checked(GetBytesPerRow(width) * height);

    public static void RecordCopy(
        WgpuContext context,
        CommandEncoder* encoder,
        GpuBuffer source,
        uint sourceOffset,
        uint bytesPerRow,
        GpuTexture destination,
        uint destinationX,
        uint destinationY,
        uint width,
        uint height)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (encoder == null)
        {
            throw new ArgumentNullException(nameof(encoder));
        }
        if (destination.Format != TextureFormat.R8Unorm)
        {
            throw new ArgumentException("Coverage copies require an R8Unorm destination.", nameof(destination));
        }
        if (!source.Usage.HasFlag(BufferUsage.CopySrc))
        {
            throw new ArgumentException("Coverage staging buffers require CopySrc usage.", nameof(source));
        }
        if (width == 0 || height == 0)
        {
            return;
        }
        if (bytesPerRow < width || bytesPerRow % CopyRowAlignment != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerRow));
        }
        if (destinationX > destination.Width ||
            width > destination.Width - destinationX)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Coverage copy exceeds the destination texture width.");
        }
        if (destinationY > destination.Height ||
            height > destination.Height - destinationY)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height),
                "Coverage copy exceeds the destination texture height.");
        }

        ulong requiredSourceBytes = checked(
            (ulong)sourceOffset +
            (ulong)bytesPerRow * (height - 1) +
            width);
        if (requiredSourceBytes > source.Size)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceOffset),
                "Coverage copy exceeds the source staging buffer.");
        }

        var copySource = new ImageCopyBuffer
        {
            Buffer = source.BufferPtr,
            Layout = new TextureDataLayout
            {
                Offset = sourceOffset,
                BytesPerRow = bytesPerRow,
                RowsPerImage = height
            }
        };
        var copyDestination = new ImageCopyTexture
        {
            Texture = destination.TexturePtr,
            MipLevel = 0,
            Origin = new Origin3D
            {
                X = destinationX,
                Y = destinationY,
                Z = 0
            },
            Aspect = TextureAspect.All
        };
        var copySize = new Extent3D
        {
            Width = width,
            Height = height,
            DepthOrArrayLayers = 1
        };
        context.Api.CommandEncoderCopyBufferToTexture(
            encoder,
            &copySource,
            &copyDestination,
            &copySize);
    }
}
