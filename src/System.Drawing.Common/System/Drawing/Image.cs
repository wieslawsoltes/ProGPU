using System.Drawing.Imaging;
using System.Runtime.Serialization;

namespace System.Drawing;

public enum RotateFlipType
{
    RotateNoneFlipNone = 0,
    Rotate90FlipNone = 1,
    Rotate180FlipNone = 2,
    Rotate270FlipNone = 3,
    RotateNoneFlipX = 4,
    Rotate90FlipX = 5,
    Rotate180FlipX = 6,
    Rotate270FlipX = 7,
    RotateNoneFlipY = Rotate180FlipX,
    Rotate90FlipY = Rotate270FlipX,
    Rotate180FlipY = RotateNoneFlipX,
    Rotate270FlipY = Rotate90FlipX,
    RotateNoneFlipXY = Rotate180FlipNone,
    Rotate90FlipXY = Rotate270FlipNone,
    Rotate180FlipXY = RotateNoneFlipNone,
    Rotate270FlipXY = Rotate90FlipNone
}

[System.ComponentModel.TypeConverter(typeof(ImageConverter))]
[Serializable]
public abstract class Image : MarshalByRefObject, IDisposable, ICloneable, ISerializable
{
    public delegate bool GetThumbnailImageAbort();

    private readonly object _metadataLock = new();
    private float _horizontalResolution = 96f;
    private float _verticalResolution = 96f;
    private object? _tag;
    private ColorPalette _palette = new([]);
    private Dictionary<int, PropertyItem>? _propertyItems;

    public abstract int Width { get; }
    public abstract int Height { get; }
    public Size Size => new Size(Width, Height);
    public SizeF PhysicalDimension => new SizeF(Width, Height);
    public float HorizontalResolution => _horizontalResolution;
    public float VerticalResolution => _verticalResolution;
    public virtual PixelFormat PixelFormat => PixelFormat.Format32bppArgb;
    public ImageFormat RawFormat { get; protected set; } = ImageFormat.Png;
    public int Flags => (int)(ImageFlags.ColorSpaceRgb | ImageFlags.HasAlpha | ImageFlags.HasRealPixelSize);
    public object? Tag
    {
        get
        {
            lock (_metadataLock)
            {
                return _tag;
            }
        }
        set
        {
            lock (_metadataLock)
            {
                _tag = value;
            }
        }
    }

    public ColorPalette Palette
    {
        get
        {
            lock (_metadataLock)
            {
                return _palette.ClonePalette();
            }
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_metadataLock)
            {
                _palette = value.ClonePalette();
            }
        }
    }

    public int[] PropertyIdList
    {
        get
        {
            lock (_metadataLock)
            {
                if (_propertyItems is null || _propertyItems.Count == 0)
                {
                    return [];
                }

                int[] ids = _propertyItems.Keys.ToArray();
                Array.Sort(ids);
                return ids;
            }
        }
    }

    public PropertyItem[] PropertyItems
    {
        get
        {
            lock (_metadataLock)
            {
                if (_propertyItems is null || _propertyItems.Count == 0)
                {
                    return [];
                }

                int[] ids = _propertyItems.Keys.ToArray();
                Array.Sort(ids);
                var items = new PropertyItem[ids.Length];
                for (int index = 0; index < ids.Length; index++)
                {
                    items[index] = _propertyItems[ids[index]].CloneItem();
                }

                return items;
            }
        }
    }
    public Guid[] FrameDimensionsList => [FrameDimension.Page.Guid];

    public static Image FromFile(string filename)
    {
        return new Bitmap(filename);
    }

    public static Image FromFile(string filename, bool useEmbeddedColorManagement)
    {
        return new Bitmap(filename, useEmbeddedColorManagement);
    }

    public static Image FromStream(Stream stream)
    {
        return new Bitmap(stream);
    }

    public static Image FromStream(Stream stream, bool useEmbeddedColorManagement) => FromStream(stream);

    public static Image FromStream(Stream stream, bool useEmbeddedColorManagement, bool validateImageData) =>
        FromStream(stream);

    public static Bitmap FromHbitmap(IntPtr hbitmap) => FromHbitmap(hbitmap, IntPtr.Zero);

    public static Bitmap FromHbitmap(IntPtr hbitmap, IntPtr hpalette) =>
        throw new PlatformNotSupportedException(
            "HBITMAP import requires the explicit Windows GDI image adapter.");

    public virtual object Clone() =>
        throw new NotSupportedException($"Cloning is not implemented for {GetType().FullName}.");

    public Image GetThumbnailImage(
        int thumbWidth,
        int thumbHeight,
        GetThumbnailImageAbort? callback,
        IntPtr callbackData)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thumbWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thumbHeight);

        if (this is not Bitmap)
        {
            throw new NotSupportedException(
                "Thumbnail generation currently requires a bitmap-backed image.");
        }

        // The callback is retained only for compatibility. The corresponding
        // GDI+ callback was removed and the official managed implementation
        // does not invoke it.
        return new Bitmap(this, thumbWidth, thumbHeight);
    }

    public RectangleF GetBounds(ref GraphicsUnit pageUnit)
    {
        pageUnit = GraphicsUnit.Pixel;
        return new RectangleF(0f, 0f, Width, Height);
    }

    public int GetFrameCount(FrameDimension dimension)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        if (dimension.Guid != FrameDimension.Page.Guid)
        {
            throw new ArgumentException("The image does not contain the requested frame dimension.", nameof(dimension));
        }

        return 1;
    }

    public int SelectActiveFrame(FrameDimension dimension, int frameIndex)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        if (dimension.Guid != FrameDimension.Page.Guid)
        {
            throw new ArgumentException("The image does not contain the requested frame dimension.", nameof(dimension));
        }

        if (frameIndex != 0)
        {
            throw new ArgumentException("A single-frame image only contains frame zero.", nameof(frameIndex));
        }

        return 0;
    }

    public PropertyItem GetPropertyItem(int propid)
    {
        lock (_metadataLock)
        {
            if (_propertyItems is null || !_propertyItems.TryGetValue(propid, out PropertyItem? item))
            {
                throw new ArgumentException("The image does not contain the requested property item.", nameof(propid));
            }

            return item.CloneItem();
        }
    }

    public void SetPropertyItem(PropertyItem propitem)
    {
        ArgumentNullException.ThrowIfNull(propitem);
        ArgumentNullException.ThrowIfNull(propitem.Value);
        if (propitem.Len < 0 || propitem.Len != propitem.Value.Length)
        {
            throw new ArgumentException("Property item length must match its value buffer.", nameof(propitem));
        }

        lock (_metadataLock)
        {
            _propertyItems ??= [];
            _propertyItems[propitem.Id] = propitem.CloneItem();
        }
    }

    public void RemovePropertyItem(int propid)
    {
        lock (_metadataLock)
        {
            if (_propertyItems is null || !_propertyItems.Remove(propid))
            {
                throw new ArgumentException("The image does not contain the requested property item.", nameof(propid));
            }
        }
    }

    public static int GetPixelFormatSize(PixelFormat pixfmt)
    {
        int encodedBitsPerPixel = ((int)pixfmt >> 8) & 0xff;
        return encodedBitsPerPixel != 0
            ? encodedBitsPerPixel
            : pixfmt switch
            {
                PixelFormat.Format8bppIndexed => 8,
                PixelFormat.Format16bppRgb565 => 16,
                PixelFormat.Format24bppRgb => 24,
                _ => 32
            };
    }

    public static bool IsAlphaPixelFormat(PixelFormat pixfmt) =>
        ((int)pixfmt & 0x0004_0000) != 0;

    public static bool IsExtendedPixelFormat(PixelFormat pixfmt) =>
        ((int)pixfmt & 0x0010_0000) != 0;

    public static bool IsCanonicalPixelFormat(PixelFormat pixfmt) =>
        ((int)pixfmt & 0x0020_0000) != 0;

    public virtual void RotateFlip(RotateFlipType rotateFlipType) =>
        throw new NotSupportedException($"RotateFlip is not implemented for {GetType().FullName}.");

    public void Save(Stream stream) => Save(stream, RawFormat);

#pragma warning disable SYSLIB0050
    void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context)
    {
        ArgumentNullException.ThrowIfNull(info);
        info.AddValue("Data", GetSerializedData(), typeof(byte[]));
    }

    internal virtual byte[] GetSerializedData()
    {
        using var stream = new MemoryStream();
        Save(stream);
        return stream.ToArray();
    }
#pragma warning restore SYSLIB0050

    public void Save(string filename) => Save(filename, RawFormat);

    public void Save(string filename, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(filename);
        using Stream stream = File.Create(filename);
        Save(stream, format);
    }

    public void Save(string filename, ImageCodecInfo encoder, EncoderParameters? encoderParams)
    {
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(encoder);
        using Stream stream = File.Create(filename);
        Save(stream, encoder, encoderParams);
    }

    public void Save(Stream stream, ImageCodecInfo encoder, EncoderParameters? encoderParams)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(encoder);

        ImageCodecInfo? registeredEncoder = ImageCodecInfo.FindEncoder(encoder.Clsid);
        if (registeredEncoder is null || registeredEncoder.FormatID != encoder.FormatID)
        {
            throw new ArgumentException("The requested image encoder is not registered.", nameof(encoder));
        }

        if (this is Bitmap bitmap)
        {
            bitmap.SaveWithEncoder(stream, new ImageFormat(registeredEncoder.FormatID), encoderParams);
            return;
        }

        Save(stream, new ImageFormat(registeredEncoder.FormatID));
    }

    public void SaveAdd(EncoderParameters? encoderParams) =>
        throw new NotSupportedException("Multi-frame image encoding is not implemented by the managed ProGPU codec layer.");

    public void SaveAdd(Image image, EncoderParameters? encoderParams)
    {
        ArgumentNullException.ThrowIfNull(image);
        throw new NotSupportedException("Multi-frame image encoding is not implemented by the managed ProGPU codec layer.");
    }

    public EncoderParameters? GetEncoderParameterList(Guid encoder)
    {
        ImageCodecInfo? codec = ImageCodecInfo.FindEncoder(encoder);
        if (codec is null)
        {
            throw new ArgumentException("The requested image encoder is not registered.", nameof(encoder));
        }

        if (codec.FormatID != ImageFormat.Jpeg.Guid)
        {
            return new EncoderParameters(0);
        }

        return new EncoderParameters(1)
        {
            Param = [new EncoderParameter(Encoder.Quality, 0L, 100L)]
        };
    }

    public virtual void Save(Stream stream, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        throw new NotSupportedException($"Image format '{format.Guid}' is not supported for this image type.");
    }

    protected void SetResolutionCore(float xDpi, float yDpi)
    {
        if (!float.IsFinite(xDpi) || xDpi <= 0f)
        {
            throw new ArgumentException("Horizontal resolution must be finite and greater than zero.", nameof(xDpi));
        }

        if (!float.IsFinite(yDpi) || yDpi <= 0f)
        {
            throw new ArgumentException("Vertical resolution must be finite and greater than zero.", nameof(yDpi));
        }

        _horizontalResolution = xDpi;
        _verticalResolution = yDpi;
    }

    protected void CopyMetadataTo(Image destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_metadataLock)
        {
            destination._horizontalResolution = _horizontalResolution;
            destination._verticalResolution = _verticalResolution;
            destination.RawFormat = RawFormat;
            destination._tag = _tag;
            destination._palette = _palette.ClonePalette();
            if (_propertyItems is not null)
            {
                destination._propertyItems = new Dictionary<int, PropertyItem>(_propertyItems.Count);
                foreach ((int id, PropertyItem item) in _propertyItems)
                {
                    destination._propertyItems.Add(id, item.CloneItem());
                }
            }
        }
    }

    public abstract void Dispose();
}
