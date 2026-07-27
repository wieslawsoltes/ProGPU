using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Silk.NET.Core;
using Silk.NET.Input;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SilkCursor = Silk.NET.Input.ICursor;
using SilkStandardCursor = Silk.NET.Input.StandardCursor;

namespace Avalonia.SilkNet;

public sealed class SilkNetCursorImpl : ICursorImpl
{
    internal SilkNetCursorImpl(
        SilkStandardCursor standardCursor,
        bool hidden = false)
    {
        StandardCursor = standardCursor;
        Hidden = hidden;
    }

    internal SilkNetCursorImpl(
        RawImage image,
        PixelPoint hotSpot)
    {
        Image = image;
        HotSpot = hotSpot;
    }

    internal SilkStandardCursor StandardCursor { get; }
    internal RawImage? Image { get; }
    internal PixelPoint HotSpot { get; }
    internal bool Hidden { get; }

    public void Apply(SilkCursor target)
    {
        if (Hidden)
        {
            target.CursorMode = CursorMode.Hidden;
            return;
        }

        target.CursorMode = CursorMode.Normal;
        if (Image is { } image)
        {
            target.Type = CursorType.Custom;
            target.Image = image;
            target.HotspotX = HotSpot.X;
            target.HotspotY = HotSpot.Y;
            return;
        }

        target.Type = CursorType.Standard;
        target.StandardCursor = target.IsSupported(StandardCursor)
            ? StandardCursor
            : SilkStandardCursor.Arrow;
    }

    public static SilkStandardCursor MapStandardCursor(
        StandardCursorType source) =>
        source switch
        {
            StandardCursorType.Ibeam => SilkStandardCursor.IBeam,
            StandardCursorType.Wait => SilkStandardCursor.Wait,
            StandardCursorType.Cross => SilkStandardCursor.Crosshair,
            StandardCursorType.SizeWestEast => SilkStandardCursor.HResize,
            StandardCursorType.SizeNorthSouth => SilkStandardCursor.VResize,
            StandardCursorType.SizeAll or
            StandardCursorType.DragMove => SilkStandardCursor.ResizeAll,
            StandardCursorType.No => SilkStandardCursor.NotAllowed,
            StandardCursorType.Hand or
            StandardCursorType.DragCopy or
            StandardCursorType.DragLink => SilkStandardCursor.Hand,
            StandardCursorType.AppStarting => SilkStandardCursor.WaitArrow,
            StandardCursorType.TopLeftCorner or
            StandardCursorType.BottomRightCorner =>
                SilkStandardCursor.NwseResize,
            StandardCursorType.TopRightCorner or
            StandardCursorType.BottomLeftCorner =>
                SilkStandardCursor.NeswResize,
            _ => SilkStandardCursor.Arrow
        };

    public void Dispose()
    {
    }
}

internal sealed class SilkNetCursorFactory : ICursorFactory
{
    public ICursorImpl GetCursor(StandardCursorType cursorType) =>
        cursorType == StandardCursorType.None
            ? new SilkNetCursorImpl(
                SilkStandardCursor.Arrow,
                hidden: true)
            : new SilkNetCursorImpl(
                SilkNetCursorImpl.MapStandardCursor(cursorType));

#if AVALONIA11
    public ICursorImpl CreateCursor(
        IBitmapImpl cursor,
        PixelPoint hotSpot)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        using var encoded = new MemoryStream();
        cursor.Save(encoded);
        return CreateDecodedCursor(encoded, hotSpot);
    }
#else
    public ICursorImpl CreateCursor(
        Bitmap cursor,
        PixelPoint hotSpot)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        using var encoded = new MemoryStream();
        cursor.Save(encoded);
        return CreateDecodedCursor(encoded, hotSpot);
    }
#endif

    private static ICursorImpl CreateDecodedCursor(
        MemoryStream encoded,
        PixelPoint hotSpot)
    {
        encoded.Position = 0;
        using Image<Rgba32> image =
            Image.Load<Rgba32>(encoded);
        byte[] pixels =
            GC.AllocateUninitializedArray<byte>(
                checked(image.Width * image.Height * 4));
        image.CopyPixelDataTo(pixels);
        return new SilkNetCursorImpl(
            new RawImage(
                image.Width,
                image.Height,
                pixels),
            hotSpot);
    }
}

internal sealed class SilkNetWindowIcon : IWindowIconImpl
{
    private readonly byte[] _encoded;

    internal SilkNetWindowIcon(byte[] encoded)
    {
        _encoded = encoded;
    }

    internal RawImage? TryDecode()
    {
        try
        {
            using Image<Rgba32> image =
                Image.Load<Rgba32>(_encoded);
            byte[] pixels =
                GC.AllocateUninitializedArray<byte>(
                    checked(image.Width * image.Height * 4));
            image.CopyPixelDataTo(pixels);
            return new RawImage(
                image.Width,
                image.Height,
                pixels);
        }
        catch (UnknownImageFormatException)
        {
            return null;
        }
        catch (InvalidImageContentException)
        {
            return null;
        }
    }

    public void Save(Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        outputStream.Write(_encoded);
    }
}

internal sealed class SilkNetIconLoader : IPlatformIconLoader
{
    public IWindowIconImpl LoadIcon(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        return new SilkNetWindowIcon(File.ReadAllBytes(fileName));
    }

    public IWindowIconImpl LoadIcon(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return new SilkNetWindowIcon(copy.ToArray());
    }

    public IWindowIconImpl LoadIcon(IBitmapImpl bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return new SilkNetWindowIcon(stream.ToArray());
    }
}

internal sealed class SilkNetClipboard : IClipboard
{
    private readonly object _gate = new();
    private IKeyboard? _keyboard;
    private IAsyncDataTransfer? _ownedData;
    private string? _text;
#if AVALONIA11
    private IDataObject? _legacyData;
#endif

    internal void AttachKeyboard(IKeyboard keyboard)
    {
        lock (_gate)
        {
            _keyboard = keyboard;
            if (_text is not null)
                keyboard.ClipboardText = _text;
        }
    }

    internal void DetachKeyboard(IKeyboard keyboard)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_keyboard, keyboard))
                _keyboard = null;
        }
    }

    public Task ClearAsync()
    {
        IAsyncDataTransfer? previous;
        lock (_gate)
        {
            previous = _ownedData;
            _ownedData = null;
            _text = null;
            if (_keyboard is not null)
                _keyboard.ClipboardText = string.Empty;
        }

        previous?.Dispose();
        return Task.CompletedTask;
    }

    public async Task SetDataAsync(
        IAsyncDataTransfer? dataTransfer)
    {
        if (dataTransfer is null)
        {
            await ClearAsync().ConfigureAwait(false);
            return;
        }

        string? text = null;
        foreach (IAsyncDataTransferItem item in dataTransfer.Items)
        {
            text = await item.TryGetTextAsync().ConfigureAwait(false);
            if (text is not null)
                break;
        }

        IAsyncDataTransfer? previous;
        lock (_gate)
        {
            previous = _ownedData;
            _ownedData = dataTransfer;
            _text = text;
            if (_keyboard is not null && text is not null)
                _keyboard.ClipboardText = text;
        }

        previous?.Dispose();
    }

    public Task FlushAsync() => Task.CompletedTask;

    public Task<IAsyncDataTransfer?> TryGetDataAsync()
    {
        string? text;
        lock (_gate)
        {
            text = _keyboard?.ClipboardText;
            if (string.IsNullOrEmpty(text))
                text = _text;
        }

        if (string.IsNullOrEmpty(text))
            return Task.FromResult<IAsyncDataTransfer?>(null);

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        return Task.FromResult<IAsyncDataTransfer?>(transfer);
    }

    public Task<IAsyncDataTransfer?> TryGetInProcessDataAsync()
    {
        lock (_gate)
            return Task.FromResult(_ownedData);
    }

#if AVALONIA11
#pragma warning disable CS0618
    public Task<string?> GetTextAsync()
    {
        lock (_gate)
            return Task.FromResult(_keyboard?.ClipboardText ?? _text);
    }

    public Task SetTextAsync(string? text)
    {
        lock (_gate)
        {
            _text = text;
            _legacyData = null;
            if (_keyboard is not null)
                _keyboard.ClipboardText = text ?? string.Empty;
        }

        return Task.CompletedTask;
    }

    public Task SetDataObjectAsync(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            _legacyData = data;
            _text = data.Get(DataFormats.Text) as string;
            if (_keyboard is not null && _text is not null)
                _keyboard.ClipboardText = _text;
        }

        return Task.CompletedTask;
    }

    public Task<string[]> GetFormatsAsync()
    {
        lock (_gate)
        {
            if (_legacyData is null)
            {
                return Task.FromResult(
                    _text is null
                        ? Array.Empty<string>()
                        : new[] { DataFormats.Text });
            }

            return Task.FromResult(
                System.Linq.Enumerable.ToArray(
                    _legacyData.GetDataFormats()));
        }
    }

    public Task<object?> GetDataAsync(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        lock (_gate)
        {
            object? value = _legacyData?.Get(format);
            if (value is null && format == DataFormats.Text)
                value = _keyboard?.ClipboardText ?? _text;
            return Task.FromResult(value);
        }
    }

    public Task<IDataObject?> TryGetInProcessDataObjectAsync()
    {
        lock (_gate)
            return Task.FromResult(_legacyData);
    }
#pragma warning restore CS0618
#endif
}
