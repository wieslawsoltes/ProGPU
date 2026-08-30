using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using ProGPU.Backend.Dawn;
using ProGPU.Direct2D;
using Windows.Storage;
using Windows.UI;

namespace ProGPU.Direct2D.Win2D.Integration;

internal static partial class Program
{
    private const uint RoInitSingleThreaded = 0U;
    private const uint Width = 64U;
    private const uint Height = 64U;
    private const string ResultFileName = "direct2d-win2d-result.json";
    private static readonly Guid IUnknownInterfaceId =
        new("00000000-0000-0000-C000-000000000046");

    [STAThread]
    private static int Main()
    {
        int initializationHResult = RoInitialize(RoInitSingleThreaded);
        bool uninitialize = initializationHResult >= 0;
        try
        {
            if (initializationHResult < 0)
            {
                Marshal.ThrowExceptionForHR(initializationHResult);
            }

            IntegrationEvidence evidence = Run(initializationHResult);
            WriteEvidence(evidence);
            return 0;
        }
        catch (Exception exception)
        {
            WriteEvidence(
                new IntegrationEvidence(
                    Contract: "ProGPU genuine Win2D over Direct2D/Dawn",
                    Status: "failed",
                    InitializationHResult: initializationHResult,
                    NativeHResult: exception.HResult,
                    Adapter: null,
                    Width: Width,
                    Height: Height,
                    ContentVersionBefore: 0UL,
                    ContentVersionAfter: 0UL,
                    CanvasDeviceType: null,
                    CanvasRenderTargetType: null,
                    DrawingSessionType: null,
                    NativeDeviceIdentityMatches: null,
                    NativeBitmapIdentityMatches: null,
                    CornerPixel: null,
                    CenterPixel: null,
                    Error: exception.ToString()));
            return 1;
        }
        finally
        {
            if (uninitialize)
            {
                RoUninitialize();
            }
        }
    }

    private static IntegrationEvidence Run(int initializationHResult)
    {
        nint module = GetModuleHandleW(null);
        if (module == 0)
        {
            throw new InvalidOperationException(
                "Could not resolve the integration process module.");
        }

        nint hwnd = CreateWindowExW(
            0U,
            "STATIC",
            "ProGPU Direct2D Win2D Integration",
            0U,
            0,
            0,
            (int)Width,
            (int)Height,
            0,
            0,
            module,
            0);
        if (hwnd == 0)
        {
            throw new InvalidOperationException(
                $"Could not create the hidden Dawn compatibility window ({Marshal.GetLastWin32Error()}).");
        }

        try
        {
            using var windowSource = DawnNativeWindowSource.CreateWin32(hwnd);
            using DawnGpuContext dawn =
                DawnGpuContext.CreateNativePresentation(windowSource);
            using ProGpuDirect2DSurface surface =
                ProGpuDirect2DSurface.Create(
                    dawn,
                    new ProGpuDirect2DSurfaceOptions(
                        Width,
                        Height,
                        Flags:
                            ProGpuDirect2DSurfaceFlags.AllowWarpFallback));

            using ProGpuDirect2DComReference originalDevice =
                surface.AcquireInterface(
                    ProGpuDirect2DInterfaceKind.D2D1Device1);
            using ProGpuDirect2DComReference originalBitmap =
                surface.AcquireInterface(
                    ProGpuDirect2DInterfaceKind.D2D1Bitmap1);
            if (!surface.TryAcquireMicrosoftWin2DNativeDevice(
                    out ProGpuDirect2DComReference? wrappedDevice,
                    out int wrappedDeviceHResult) ||
                wrappedDevice is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasDevice native-resource interop failed (0x{wrappedDeviceHResult:X8}).");
            }
            using (wrappedDevice)
            {
                if (!HasSameComIdentity(originalDevice, wrappedDevice))
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasDevice did not return ProGPU's exact ID2D1Device1 identity.");
                }
            }
            if (!surface.TryAcquireMicrosoftWin2DNativeBitmap(
                    out ProGpuDirect2DComReference? wrappedBitmap,
                    out int wrappedBitmapHResult) ||
                wrappedBitmap is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasRenderTarget native-resource interop failed (0x{wrappedBitmapHResult:X8}).");
            }
            using (wrappedBitmap)
            {
                if (!HasSameComIdentity(originalBitmap, wrappedBitmap))
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasRenderTarget did not return ProGPU's exact ID2D1Bitmap1 identity.");
                }
            }

            ulong contentVersionBefore = surface.ContentVersion;
            if (!surface.TryBeginMicrosoftWin2DProducerAccess(
                    out ProGpuMicrosoftWin2DProducerAccess? access,
                    out int nativeHResult) ||
                access is null)
            {
                throw new InvalidOperationException(
                    $"The genuine Win2D producer could not be acquired (0x{nativeHResult:X8}).");
            }

            string canvasDeviceType;
            string canvasRenderTargetType;
            string drawingSessionType;
            PixelEvidence cornerPixel;
            PixelEvidence centerPixel;
            using (access)
            using (CanvasRenderTarget target =
                CanvasRenderTarget.FromAbi(
                    access.CanvasRenderTarget.DangerousGetHandle()))
            {
                canvasDeviceType = target.Device.GetType().FullName ??
                    target.Device.GetType().Name;
                canvasRenderTargetType = target.GetType().FullName ??
                    target.GetType().Name;
                Color fill = Color.FromArgb(255, 32, 96, 192);
                using (CanvasDrawingSession drawingSession =
                    target.CreateDrawingSession())
                {
                    drawingSessionType =
                        drawingSession.GetType().FullName ??
                        drawingSession.GetType().Name;
                    drawingSession.Clear(
                        Color.FromArgb(0, 0, 0, 0));
                    drawingSession.FillRectangle(
                        8.0F,
                        8.0F,
                        48.0F,
                        48.0F,
                        fill);
                }

                Color[] pixels = target.GetPixelColors();
                if (pixels.Length != checked((int)(Width * Height)))
                {
                    throw new InvalidOperationException(
                        "Win2D returned an unexpected pixel count.");
                }
                Color corner = pixels[0];
                Color center = pixels[
                    checked((int)((Height / 2U) * Width + Width / 2U))];
                cornerPixel = PixelEvidence.FromColor(corner);
                centerPixel = PixelEvidence.FromColor(center);
                if (corner.A != 0 ||
                    center.A != fill.A ||
                    center.R != fill.R ||
                    center.G != fill.G ||
                    center.B != fill.B)
                {
                    throw new InvalidOperationException(
                        $"Win2D pixel oracle failed: corner={cornerPixel}, center={centerPixel}.");
                }
            }

            ulong contentVersionAfter = surface.ContentVersion;
            if (contentVersionAfter <= contentVersionBefore)
            {
                throw new InvalidOperationException(
                    "The Win2D producer did not publish a newer shared-surface content version.");
            }

            return new IntegrationEvidence(
                Contract: "ProGPU genuine Win2D over Direct2D/Dawn",
                Status: "passed",
                InitializationHResult: initializationHResult,
                NativeHResult: 0,
                Adapter: dawn.Context.AdapterName,
                Width: Width,
                Height: Height,
                ContentVersionBefore: contentVersionBefore,
                ContentVersionAfter: contentVersionAfter,
                CanvasDeviceType: canvasDeviceType,
                CanvasRenderTargetType: canvasRenderTargetType,
                DrawingSessionType: drawingSessionType,
                NativeDeviceIdentityMatches: true,
                NativeBitmapIdentityMatches: true,
                CornerPixel: cornerPixel,
                CenterPixel: centerPixel,
                Error: null);
        }
        finally
        {
            _ = DestroyWindow(hwnd);
        }
    }

    private static bool HasSameComIdentity(
        ProGpuDirect2DComReference left,
        ProGpuDirect2DComReference right)
    {
        using ProGpuDirect2DComReference leftIdentity =
            left.QueryInterface(IUnknownInterfaceId);
        using ProGpuDirect2DComReference rightIdentity =
            right.QueryInterface(IUnknownInterfaceId);
        return leftIdentity.DangerousGetHandle() ==
            rightIdentity.DangerousGetHandle();
    }

    private static void WriteEvidence(IntegrationEvidence evidence)
    {
        string directory = ApplicationData.Current.LocalFolder.Path;
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, ResultFileName),
            JsonSerializer.Serialize(
                evidence,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(uint initializationType);

    [LibraryImport("combase.dll")]
    private static partial void RoUninitialize();

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetModuleHandleW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport(
        "user32.dll",
        EntryPoint = "CreateWindowExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    private sealed record IntegrationEvidence(
        string Contract,
        string Status,
        int InitializationHResult,
        int NativeHResult,
        string? Adapter,
        uint Width,
        uint Height,
        ulong ContentVersionBefore,
        ulong ContentVersionAfter,
        string? CanvasDeviceType,
        string? CanvasRenderTargetType,
        string? DrawingSessionType,
        bool? NativeDeviceIdentityMatches,
        bool? NativeBitmapIdentityMatches,
        PixelEvidence? CornerPixel,
        PixelEvidence? CenterPixel,
        string? Error);

    private sealed record PixelEvidence(byte A, byte R, byte G, byte B)
    {
        public static PixelEvidence FromColor(Color color) =>
            new(color.A, color.R, color.G, color.B);
    }
}
