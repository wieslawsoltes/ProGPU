using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Avalonia.SilkNet
{
    public class SilkNetPlatform : IWindowingPlatform, IPlatformIconLoader
    {
        private static readonly SilkNetPlatform s_instance = new();
        public static SilkNetPlatform Instance => s_instance;

        private readonly List<WindowImpl> _windows = new();
        private WindowImpl[] _windowSnapshot = Array.Empty<WindowImpl>();
        private SilkNetDispatcherImpl? _dispatcher;
        private static Compositor? s_compositor;

        public static Compositor Compositor => s_compositor ?? throw new InvalidOperationException($"{nameof(SilkNetPlatform)} hasn't been initialized");

        public static void Initialize()
        {
            s_instance._dispatcher = new SilkNetDispatcherImpl();
#if !AVALONIA11
            Avalonia.Threading.Dispatcher.InitializeUIThreadDispatcher(s_instance._dispatcher);
#endif

            var clipboardImpl = new SilkNetClipboardImpl();
            var clipboard = new SilkNetClipboard(clipboardImpl);

            var renderTimer = new SilkNetRenderTimer(60);
#if AVALONIA11
            AvaloniaLocator.CurrentMutable
                .Bind<Avalonia.Threading.IDispatcherImpl>().ToConstant(s_instance._dispatcher)
                .Bind<IRenderTimer>().ToConstant(renderTimer);
#else
            var renderLoop = RenderLoop.FromTimer(renderTimer);
            AvaloniaLocator.CurrentMutable.Bind<IRenderLoop>().ToConstant(renderLoop);
#endif

            var platformGraphics = AvaloniaLocator.Current.GetService<IPlatformGraphics>();
            s_compositor = new Compositor(platformGraphics);

            AvaloniaLocator.CurrentMutable
                .Bind<Compositor>().ToConstant(s_compositor)
                .Bind<IWindowingPlatform>().ToConstant(s_instance)
                .Bind<IPlatformIconLoader>().ToConstant(s_instance)
                .Bind<ICursorFactory>().ToConstant(new SilkNetCursorFactory())
                .Bind<IKeyboardDevice>().ToConstant(SilkNetKeyboardDevice.Instance)
                .Bind<IPlatformSettings>().ToConstant(new SilkNetPlatformSettings())
                .Bind<IClipboardImpl>().ToConstant(clipboardImpl)
                .Bind<IClipboard>().ToConstant(clipboard)
                .Bind<IScreenImpl>().ToConstant(new SilkNetScreenImpl());
        }

        public void RegisterWindow(WindowImpl window)
        {
            lock (_windows)
            {
                _windows.Add(window);
                _windowSnapshot = _windows.ToArray();
            }
        }

        public void UnregisterWindow(WindowImpl window)
        {
            lock (_windows)
            {
                _windows.Remove(window);
                _windowSnapshot = _windows.ToArray();
            }
        }

        public void DoEvents()
        {
            // Registration is rare; event pumping is continuous. Publish an immutable
            // copy-on-write snapshot so the steady-state pump is O(W) with no allocation.
            foreach (var win in Volatile.Read(ref _windowSnapshot))
            {
                if (!win.IsDisposed && win.SilkWindow != null && win.SilkWindow.IsInitialized)
                {
                    try
                    {
                        win.SilkWindow.DoEvents();
                    }
                    catch {}
                }
            }
        }

        public ITrayIconImpl CreateTrayIcon() => null!;

        public IWindowImpl CreateWindow()
        {
            return new WindowImpl();
        }

        public ITopLevelImpl CreateEmbeddableTopLevel()
        {
            return new WindowImpl();
        }

        public IWindowImpl CreateEmbeddableWindow()
        {
            var embedded = new WindowImpl();
            embedded.Show(false, false);
            return embedded;
        }

        public void GetWindowsZOrder(ReadOnlySpan<IWindowImpl> windows, Span<long> zOrder)
        {
            for (int i = 0; i < windows.Length; i++)
            {
                zOrder[i] = i;
            }
        }

        public IWindowIconImpl LoadIcon(IBitmapImpl bitmap)
        {
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream);
                return LoadIcon(stream);
            }
        }

        public IWindowIconImpl LoadIcon(Stream stream)
        {
            return new SilkNetIconData(stream);
        }

        public IWindowIconImpl LoadIcon(string fileName)
        {
            using (var file = File.Open(fileName, FileMode.Open, FileAccess.Read))
                return LoadIcon(file);
        }
    }

    internal sealed class SilkNetIconData : IWindowIconImpl
    {
        private const int IconDirectoryEntrySize = 16;
        private const int MaxIconBytes = 64 * 1024 * 1024;
        private const int MaxIconFrames = 256;
        private static readonly byte[] s_pngSignature =
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        private readonly SilkNetIconFrame[] _frames;

        public SilkNetIconData(Stream stream)
        {
            using var copy = new MemoryStream();
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (copy.Length + bytesRead > MaxIconBytes)
                {
                    throw new InvalidDataException("Window icon exceeds the supported size.");
                }
                copy.Write(buffer, 0, bytesRead);
            }

            var bytes = copy.ToArray();
            var encodedFrames = IsIcon(bytes)
                ? ExtractIconFrames(bytes)
                : new[] { bytes };
            var frames = new List<SilkNetIconFrame>(encodedFrames.Count);
            foreach (var encodedFrame in encodedFrames)
            {
                try
                {
                    using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(encodedFrame);
                    var pixels = new byte[checked(image.Width * image.Height * 4)];
                    image.CopyPixelDataTo(pixels);
                    frames.Add(new SilkNetIconFrame(image.Width, image.Height, pixels));
                }
                catch (SixLabors.ImageSharp.UnknownImageFormatException)
                {
                    // Unsupported ICO entries are ignored when another frame can be decoded.
                }
            }

            if (frames.Count == 0)
            {
                throw new InvalidDataException("The window icon does not contain a supported image frame.");
            }

            _frames = frames.ToArray();
        }

        public IReadOnlyList<SilkNetIconFrame> Frames => _frames;

        public void Save(Stream outputStream)
        {
            var frame = _frames[^1];
            using var image = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(
                frame.Pixels,
                frame.Width,
                frame.Height);
            image.SaveAsPng(outputStream);
        }

        private static bool IsIcon(ReadOnlySpan<byte> bytes) =>
            bytes.Length >= 6 &&
            BinaryPrimitives.ReadUInt16LittleEndian(bytes) == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) == 1;

        private static IReadOnlyList<byte[]> ExtractIconFrames(byte[] icon)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4));
            var directorySize = checked(6 + count * IconDirectoryEntrySize);
            if (count == 0 || count > MaxIconFrames || directorySize > icon.Length)
            {
                throw new InvalidDataException("The ICO directory is invalid.");
            }

            var pngFrames = new List<(int Area, byte[] Data)>();
            var bitmapFrames = new List<(int Area, byte[] Data)>();
            long extractedBytes = 0;
            for (var index = 0; index < count; index++)
            {
                var entry = icon.AsSpan(6 + index * IconDirectoryEntrySize, IconDirectoryEntrySize);
                var width = entry[0] == 0 ? 256 : entry[0];
                var height = entry[1] == 0 ? 256 : entry[1];
                var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
                if (length == 0 || offset > icon.Length || length > icon.Length - offset)
                {
                    continue;
                }

                var data = icon.AsSpan((int)offset, (int)length);
                var area = width * height;
                extractedBytes += data.Length;
                if (extractedBytes > MaxIconBytes)
                {
                    throw new InvalidDataException("The ICO frame data exceeds the supported size.");
                }

                if (data.StartsWith(s_pngSignature))
                {
                    pngFrames.Add((area, data.ToArray()));
                }
                else if (TryWrapIconBitmap(data, height, out var bitmap))
                {
                    bitmapFrames.Add((area, bitmap));
                }
            }

            var frames = pngFrames.Count > 0 ? pngFrames : bitmapFrames;
            frames.Sort(static (left, right) => left.Area.CompareTo(right.Area));
            var result = new byte[frames.Count][];
            for (var index = 0; index < frames.Count; index++)
            {
                result[index] = frames[index].Data;
            }
            return result;
        }

        private static bool TryWrapIconBitmap(
            ReadOnlySpan<byte> dib,
            int iconHeight,
            out byte[] bitmap)
        {
            bitmap = Array.Empty<byte>();
            if (dib.Length < 40)
            {
                return false;
            }

            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
            if (headerSize < 40 || headerSize > dib.Length)
            {
                return false;
            }

            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
            var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
            var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
            var paletteEntries = colorsUsed != 0
                ? colorsUsed
                : bitCount <= 8
                    ? 1u << bitCount
                    : 0;
            var bitMasksSize = headerSize == 40 && compression is 3 or 6
                ? compression == 6 ? 16u : 12u
                : 0u;
            var pixelOffset = checked(14u + headerSize + bitMasksSize + paletteEntries * 4u);
            if (pixelOffset > 14u + dib.Length)
            {
                return false;
            }

            bitmap = new byte[checked(14 + dib.Length)];
            bitmap[0] = (byte)'B';
            bitmap[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(2), (uint)bitmap.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(10), pixelOffset);
            dib.CopyTo(bitmap.AsSpan(14));
            BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(22), iconHeight);
            return true;
        }

    }

    internal sealed record SilkNetIconFrame(int Width, int Height, byte[] Pixels);

    public class SilkNetPlatformSettings : IPlatformSettings
    {
        public Size GetTapSize(PointerType type) => new Size(4, 4);
        public Size GetDoubleTapSize(PointerType type) => new Size(4, 4);
        public TimeSpan GetDoubleTapTime(PointerType type) => TimeSpan.FromMilliseconds(500);
        public TimeSpan HoldWaitDuration => TimeSpan.FromMilliseconds(500);

        public PlatformHotkeyConfiguration HotkeyConfiguration { get; } = new(KeyModifiers.Control);

        public PlatformColorValues GetColorValues()
        {
            return new PlatformColorValues
            {
                ThemeVariant = PlatformThemeVariant.Light
            };
        }

        public event EventHandler<PlatformColorValues>? ColorValuesChanged
        {
            add { }
            remove { }
        }
    }
}

namespace Avalonia
{
    public static class SilkNetApplicationExtensions
    {
        public static AppBuilder UseSilkNet(this AppBuilder builder)
        {
            return builder
                .UseStandardRuntimePlatformSubsystem()
                .UseWindowingSubsystem(() => SilkNet.SilkNetPlatform.Initialize(), "SilkNet");
        }
    }
}
