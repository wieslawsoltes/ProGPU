using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProGPU.Backend.Dawn;
using ProGPU.Direct2D;
using ProGPU.Wpf.Interop;
using Windows.Storage;
using Windows.UI;

namespace ProGPU.Direct2D.Win2D.Integration;

internal static partial class Program
{
    private const uint RoInitSingleThreaded = 0U;
    private const uint Width = 64U;
    private const uint Height = 64U;
    private const string ResultFileName = "direct2d-win2d-result.json";
    private const string FallbackResultDirectoryName =
        "ProGPU.Direct2D.Win2D.Integration";
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
                    CanvasSolidColorBrushType: null,
                    CanvasLinearGradientBrushType: null,
                    CanvasRadialGradientBrushType: null,
                    CanvasGeometryType: null,
                    DrawingSessionType: null,
                    NativeDeviceIdentityMatches: null,
                    NativeBitmapIdentityMatches: null,
                    NativeSolidColorBrushIdentityMatches: null,
                    NativeLinearGradientBrushIdentityMatches: null,
                    NativeRadialGradientBrushIdentityMatches: null,
                    NativeGeometryIdentityMatches: null,
                    SolidColorBrushColor: null,
                    LinearGradientBrushColor: null,
                    RadialGradientBrushColor: null,
                    CornerPixel: null,
                    CenterPixel: null,
                    SolidPixel: null,
                    RadialPixel: null,
                    GeometryPixel: null,
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

            Color fill = Color.FromArgb(255, 224, 48, 96);
            using ProGpuDirect2DComReference nativeSolidColorBrush =
                surface.CreateSolidColorBrush(
                    ProGpuDirect2DColor.FromArgb(
                        fill.A,
                        fill.R,
                        fill.G,
                        fill.B));
            if (!surface.TryAcquireMicrosoftWin2DSolidColorBrush(
                    nativeSolidColorBrush,
                    out ProGpuDirect2DComReference? wrappedSolidColorBrush,
                    out int wrappedSolidColorBrushHResult) ||
                wrappedSolidColorBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasSolidColorBrush wrapping failed (0x{wrappedSolidColorBrushHResult:X8}).");
            }
            using ProGpuDirect2DComReference canvasSolidColorBrushReference =
                wrappedSolidColorBrush;
            if (!surface.TryAcquireMicrosoftWin2DNativeSolidColorBrush(
                    canvasSolidColorBrushReference,
                    out ProGpuDirect2DComReference? unwrappedSolidColorBrush,
                    out int unwrappedSolidColorBrushHResult) ||
                unwrappedSolidColorBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasSolidColorBrush native-resource interop failed (0x{unwrappedSolidColorBrushHResult:X8}).");
            }
            using (unwrappedSolidColorBrush)
            {
                if (!HasSameComIdentity(
                        nativeSolidColorBrush,
                        unwrappedSolidColorBrush))
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasSolidColorBrush did not preserve ProGPU's ID2D1SolidColorBrush identity.");
                }
            }

            Color linearColor = Color.FromArgb(255, 32, 160, 224);
            Span<ProGpuDirect2DGradientStop> linearStops =
                stackalloc ProGpuDirect2DGradientStop[2]
                {
                    new(
                        0.0F,
                        ProGpuDirect2DColor.FromArgb(
                            linearColor.A,
                            linearColor.R,
                            linearColor.G,
                            linearColor.B)),
                    new(
                        1.0F,
                        ProGpuDirect2DColor.FromArgb(
                            linearColor.A,
                            linearColor.R,
                            linearColor.G,
                            linearColor.B))
                };
            using ProGpuDirect2DComReference linearStopCollection =
                surface.CreateGradientStopCollection(linearStops);
            using ProGpuDirect2DComReference nativeLinearGradientBrush =
                surface.CreateLinearGradientBrush(
                    linearStopCollection,
                    new Vector2(24.0F, 0.0F),
                    new Vector2(40.0F, 0.0F));
            if (!surface.TryAcquireMicrosoftWin2DLinearGradientBrush(
                    nativeLinearGradientBrush,
                    out ProGpuDirect2DComReference? wrappedLinearGradientBrush,
                    out int wrappedLinearGradientBrushHResult) ||
                wrappedLinearGradientBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasLinearGradientBrush wrapping failed (0x{wrappedLinearGradientBrushHResult:X8}).");
            }
            using ProGpuDirect2DComReference
                canvasLinearGradientBrushReference =
                    wrappedLinearGradientBrush;
            if (!surface.TryAcquireMicrosoftWin2DNativeLinearGradientBrush(
                    canvasLinearGradientBrushReference,
                    out ProGpuDirect2DComReference? unwrappedLinearGradientBrush,
                    out int unwrappedLinearGradientBrushHResult) ||
                unwrappedLinearGradientBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasLinearGradientBrush native-resource interop failed (0x{unwrappedLinearGradientBrushHResult:X8}).");
            }
            using (unwrappedLinearGradientBrush)
            {
                if (!HasSameComIdentity(
                        nativeLinearGradientBrush,
                        unwrappedLinearGradientBrush))
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasLinearGradientBrush did not preserve ProGPU's ID2D1LinearGradientBrush identity.");
                }
            }

            Color radialColor = Color.FromArgb(255, 64, 192, 96);
            Span<ProGpuDirect2DGradientStop> radialStops =
                stackalloc ProGpuDirect2DGradientStop[2]
                {
                    new(
                        0.0F,
                        ProGpuDirect2DColor.FromArgb(
                            radialColor.A,
                            radialColor.R,
                            radialColor.G,
                            radialColor.B)),
                    new(
                        1.0F,
                        ProGpuDirect2DColor.FromArgb(
                            radialColor.A,
                            radialColor.R,
                            radialColor.G,
                            radialColor.B))
                };
            using ProGpuDirect2DComReference radialStopCollection =
                surface.CreateGradientStopCollection(radialStops);
            using ProGpuDirect2DComReference nativeRadialGradientBrush =
                surface.CreateRadialGradientBrush(
                    radialStopCollection,
                    new Vector2(52.0F, 32.0F),
                    Vector2.Zero,
                    8.0F,
                    28.0F);
            if (!surface.TryAcquireMicrosoftWin2DRadialGradientBrush(
                    nativeRadialGradientBrush,
                    out ProGpuDirect2DComReference? wrappedRadialGradientBrush,
                    out int wrappedRadialGradientBrushHResult) ||
                wrappedRadialGradientBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasRadialGradientBrush wrapping failed (0x{wrappedRadialGradientBrushHResult:X8}).");
            }
            using ProGpuDirect2DComReference
                canvasRadialGradientBrushReference =
                    wrappedRadialGradientBrush;
            if (!surface.TryAcquireMicrosoftWin2DNativeRadialGradientBrush(
                    canvasRadialGradientBrushReference,
                    out ProGpuDirect2DComReference? unwrappedRadialGradientBrush,
                    out int unwrappedRadialGradientBrushHResult) ||
                unwrappedRadialGradientBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasRadialGradientBrush native-resource interop failed (0x{unwrappedRadialGradientBrushHResult:X8}).");
            }
            using (unwrappedRadialGradientBrush)
            {
                if (!HasSameComIdentity(
                        nativeRadialGradientBrush,
                        unwrappedRadialGradientBrush))
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasRadialGradientBrush did not preserve ProGPU's ID2D1RadialGradientBrush identity.");
                }
            }

            var combinedGeometry = new PortableGeometryPath
            {
                Kind = PortableGeometryPathKind.Combined,
                PathA = CreateRectanglePath(4.0, 20.0, 12.0, 24.0),
                PathB = CreateRectanglePath(8.0, 26.0, 4.0, 12.0),
                CombineOperation = (int)ProGpuDirect2DCombineMode.Exclude,
                Transform = new PortableMatrix3x2(
                    1.0,
                    0.0,
                    0.0,
                    1.0,
                    2.0,
                    0.0)
            };
            using ProGpuDirect2DComReference nativeGeometry =
                surface.CreateGeometry(combinedGeometry);
            if (!surface.TryAcquireMicrosoftWin2DGeometry(
                    nativeGeometry,
                    out ProGpuDirect2DComReference? wrappedGeometry,
                    out int wrappedGeometryHResult) ||
                wrappedGeometry is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasGeometry wrapping failed (0x{wrappedGeometryHResult:X8}).");
            }
            using ProGpuDirect2DComReference canvasGeometryReference =
                wrappedGeometry;
            if (!surface.TryAcquireMicrosoftWin2DNativeGeometry(
                    canvasGeometryReference,
                    out ProGpuDirect2DComReference? unwrappedGeometry,
                    out int unwrappedGeometryHResult) ||
                unwrappedGeometry is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasGeometry native-resource interop failed (0x{unwrappedGeometryHResult:X8}).");
            }
            using (unwrappedGeometry)
            {
                if (!HasSameComIdentity(nativeGeometry, unwrappedGeometry))
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasGeometry did not preserve ProGPU's ID2D1Geometry identity.");
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
            string canvasSolidColorBrushType;
            string canvasLinearGradientBrushType;
            string canvasRadialGradientBrushType;
            string canvasGeometryType;
            string drawingSessionType;
            PixelEvidence solidColorBrushColor;
            PixelEvidence linearGradientBrushColor;
            PixelEvidence radialGradientBrushColor;
            PixelEvidence cornerPixel;
            PixelEvidence centerPixel;
            PixelEvidence solidPixel;
            PixelEvidence radialPixel;
            PixelEvidence geometryPixel;
            using (access)
            using (CanvasRenderTarget target =
                CanvasRenderTarget.FromAbi(
                    access.CanvasRenderTarget.DangerousGetHandle()))
            using (CanvasSolidColorBrush canvasSolidColorBrush =
                CanvasSolidColorBrush.FromAbi(
                    canvasSolidColorBrushReference.DangerousGetHandle()))
            using (CanvasLinearGradientBrush canvasLinearGradientBrush =
                CanvasLinearGradientBrush.FromAbi(
                    canvasLinearGradientBrushReference.DangerousGetHandle()))
            using (CanvasRadialGradientBrush canvasRadialGradientBrush =
                CanvasRadialGradientBrush.FromAbi(
                    canvasRadialGradientBrushReference.DangerousGetHandle()))
            using (CanvasGeometry canvasGeometry =
                CanvasGeometry.FromAbi(
                    canvasGeometryReference.DangerousGetHandle()))
            {
                canvasDeviceType = target.Device.GetType().FullName ??
                    target.Device.GetType().Name;
                canvasRenderTargetType = target.GetType().FullName ??
                    target.GetType().Name;
                canvasSolidColorBrushType =
                    canvasSolidColorBrush.GetType().FullName ??
                    canvasSolidColorBrush.GetType().Name;
                Color projectedBrushColor = canvasSolidColorBrush.Color;
                solidColorBrushColor =
                    PixelEvidence.FromColor(projectedBrushColor);
                if (projectedBrushColor.A != fill.A ||
                    projectedBrushColor.R != fill.R ||
                    projectedBrushColor.G != fill.G ||
                    projectedBrushColor.B != fill.B)
                {
                    throw new InvalidOperationException(
                        $"Win2D CanvasSolidColorBrush color changed: {solidColorBrushColor}.");
                }
                canvasLinearGradientBrushType =
                    canvasLinearGradientBrush.GetType().FullName ??
                    canvasLinearGradientBrush.GetType().Name;
                CanvasGradientStop[] projectedLinearStops =
                    canvasLinearGradientBrush.Stops;
                if (projectedLinearStops.Length != 2 ||
                    !MatchesColor(projectedLinearStops[0].Color, linearColor) ||
                    !MatchesColor(projectedLinearStops[1].Color, linearColor) ||
                    canvasLinearGradientBrush.StartPoint !=
                        new Vector2(24.0F, 0.0F) ||
                    canvasLinearGradientBrush.EndPoint !=
                        new Vector2(40.0F, 0.0F))
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasLinearGradientBrush metadata changed.");
                }
                linearGradientBrushColor =
                    PixelEvidence.FromColor(projectedLinearStops[0].Color);

                canvasRadialGradientBrushType =
                    canvasRadialGradientBrush.GetType().FullName ??
                    canvasRadialGradientBrush.GetType().Name;
                CanvasGradientStop[] projectedRadialStops =
                    canvasRadialGradientBrush.Stops;
                if (projectedRadialStops.Length != 2 ||
                    !MatchesColor(projectedRadialStops[0].Color, radialColor) ||
                    !MatchesColor(projectedRadialStops[1].Color, radialColor) ||
                    canvasRadialGradientBrush.Center !=
                        new Vector2(52.0F, 32.0F) ||
                    canvasRadialGradientBrush.OriginOffset != Vector2.Zero ||
                    canvasRadialGradientBrush.RadiusX != 8.0F ||
                    canvasRadialGradientBrush.RadiusY != 28.0F)
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasRadialGradientBrush metadata changed.");
                }
                radialGradientBrushColor =
                    PixelEvidence.FromColor(projectedRadialStops[0].Color);
                canvasGeometryType = canvasGeometry.GetType().FullName ??
                    canvasGeometry.GetType().Name;
                using (CanvasDrawingSession drawingSession =
                    target.CreateDrawingSession())
                {
                    drawingSessionType =
                        drawingSession.GetType().FullName ??
                        drawingSession.GetType().Name;
                    drawingSession.Clear(
                        Color.FromArgb(0, 0, 0, 0));
                    drawingSession.FillRectangle(
                        4.0F,
                        4.0F,
                        16.0F,
                        56.0F,
                        canvasSolidColorBrush);
                    drawingSession.FillRectangle(
                        24.0F,
                        4.0F,
                        16.0F,
                        56.0F,
                        canvasLinearGradientBrush);
                    drawingSession.FillRectangle(
                        44.0F,
                        4.0F,
                        16.0F,
                        56.0F,
                        canvasRadialGradientBrush);
                    drawingSession.FillGeometry(
                        canvasGeometry,
                        Color.FromArgb(255, 240, 208, 32));
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
                Color solid = pixels[checked((int)(32U * Width + 12U))];
                Color radial = pixels[checked((int)(32U * Width + 52U))];
                Color geometryColor =
                    pixels[checked((int)(32U * Width + 8U))];
                cornerPixel = PixelEvidence.FromColor(corner);
                centerPixel = PixelEvidence.FromColor(center);
                solidPixel = PixelEvidence.FromColor(solid);
                radialPixel = PixelEvidence.FromColor(radial);
                geometryPixel = PixelEvidence.FromColor(geometryColor);
                if (corner.A != 0 ||
                    !MatchesColor(solid, fill) ||
                    !MatchesColor(center, linearColor) ||
                    !MatchesColor(radial, radialColor) ||
                    !MatchesColor(
                        geometryColor,
                        Color.FromArgb(255, 240, 208, 32)))
                {
                    throw new InvalidOperationException(
                        $"Win2D pixel oracle failed: corner={cornerPixel}, solid={solidPixel}, linear={centerPixel}, radial={radialPixel}, geometry={geometryPixel}.");
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
                CanvasSolidColorBrushType: canvasSolidColorBrushType,
                CanvasLinearGradientBrushType: canvasLinearGradientBrushType,
                CanvasRadialGradientBrushType: canvasRadialGradientBrushType,
                CanvasGeometryType: canvasGeometryType,
                DrawingSessionType: drawingSessionType,
                NativeDeviceIdentityMatches: true,
                NativeBitmapIdentityMatches: true,
                NativeSolidColorBrushIdentityMatches: true,
                NativeLinearGradientBrushIdentityMatches: true,
                NativeRadialGradientBrushIdentityMatches: true,
                NativeGeometryIdentityMatches: true,
                SolidColorBrushColor: solidColorBrushColor,
                LinearGradientBrushColor: linearGradientBrushColor,
                RadialGradientBrushColor: radialGradientBrushColor,
                CornerPixel: cornerPixel,
                CenterPixel: centerPixel,
                SolidPixel: solidPixel,
                RadialPixel: radialPixel,
                GeometryPixel: geometryPixel,
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

    private static bool MatchesColor(Color actual, Color expected) =>
        actual.A == expected.A &&
        actual.R == expected.R &&
        actual.G == expected.G &&
        actual.B == expected.B;

    private static PortableGeometryPath CreateRectanglePath(
        double x,
        double y,
        double width,
        double height) =>
        new()
        {
            Kind = PortableGeometryPathKind.Path,
            FillRule = PortableFillRule.Nonzero,
            Figures =
            [
                new PortablePathFigure
                {
                    StartPoint = new PortablePoint(x, y),
                    IsClosed = true,
                    IsFilled = true,
                    Segments =
                    [
                        PortablePathSegment.Line(
                            new PortablePoint(x + width, y),
                            isSmoothJoin: false,
                            isStroked: true),
                        PortablePathSegment.Line(
                            new PortablePoint(x + width, y + height),
                            isSmoothJoin: false,
                            isStroked: true),
                        PortablePathSegment.Line(
                            new PortablePoint(x, y + height),
                            isSmoothJoin: false,
                            isStroked: true)
                    ]
                }
            ]
        };

    private static void WriteEvidence(IntegrationEvidence evidence)
    {
        string json = JsonSerializer.Serialize(
            evidence,
            new JsonSerializerOptions { WriteIndented = true });
        try
        {
            string packageDirectory = ApplicationData.Current.LocalFolder.Path;
            Directory.CreateDirectory(packageDirectory);
            File.WriteAllText(
                Path.Combine(packageDirectory, ResultFileName),
                json);
            return;
        }
        catch
        {
            // Full-trust package activation failures can make ApplicationData
            // unavailable while the diagnostic catch path is running. Never
            // let that secondary failure erase the original interop evidence.
        }

        string fallbackDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            FallbackResultDirectoryName);
        Directory.CreateDirectory(fallbackDirectory);
        File.WriteAllText(
            Path.Combine(fallbackDirectory, ResultFileName),
            json);
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
        string? CanvasSolidColorBrushType,
        string? CanvasLinearGradientBrushType,
        string? CanvasRadialGradientBrushType,
        string? CanvasGeometryType,
        string? DrawingSessionType,
        bool? NativeDeviceIdentityMatches,
        bool? NativeBitmapIdentityMatches,
        bool? NativeSolidColorBrushIdentityMatches,
        bool? NativeLinearGradientBrushIdentityMatches,
        bool? NativeRadialGradientBrushIdentityMatches,
        bool? NativeGeometryIdentityMatches,
        PixelEvidence? SolidColorBrushColor,
        PixelEvidence? LinearGradientBrushColor,
        PixelEvidence? RadialGradientBrushColor,
        PixelEvidence? CornerPixel,
        PixelEvidence? CenterPixel,
        PixelEvidence? SolidPixel,
        PixelEvidence? RadialPixel,
        PixelEvidence? GeometryPixel,
        string? Error);

    private sealed record PixelEvidence(byte A, byte R, byte G, byte B)
    {
        public static PixelEvidence FromColor(Color color) =>
            new(color.A, color.R, color.G, color.B);
    }
}
