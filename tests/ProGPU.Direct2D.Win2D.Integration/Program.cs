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
    private const uint Width = 76U;
    private const uint Height = 64U;
    private const string ResultFileName = "direct2d-win2d-result.json";
    private const string ProgressFileName = "direct2d-win2d-progress.txt";
    private const string FallbackResultDirectoryName =
        "ProGPU.Direct2D.Win2D.Integration";
    private static readonly Guid IUnknownInterfaceId =
        new("00000000-0000-0000-C000-000000000046");

    [STAThread]
    private static int Main()
    {
        WriteProgress("main-entered");
        int initializationHResult = RoInitialize(RoInitSingleThreaded);
        bool uninitialize = initializationHResult >= 0;
        try
        {
            if (initializationHResult < 0)
            {
                WriteProgress(
                    $"ro-initialize-failed-0x{initializationHResult:X8}");
                Marshal.ThrowExceptionForHR(initializationHResult);
            }
            WriteProgress("ro-initialized");

            IntegrationEvidence evidence = Run(initializationHResult);
            WriteProgress("evidence-write-started");
            return WriteEvidence(evidence) ? 0 : 2;
        }
        catch (Exception exception)
        {
            _ = WriteEvidence(
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
                    CanvasBitmapType: null,
                    CanvasImageBrushType: null,
                    CanvasGeneralImageBrushType: null,
                    CanvasCommandListType: null,
                    CanvasEffectImageBrushType: null,
                    CanvasGeometryType: null,
                    CanvasStrokeStyleType: null,
                    DrawingSessionType: null,
                    NativeDeviceIdentityMatches: null,
                    NativeBitmapIdentityMatches: null,
                    NativeSolidColorBrushIdentityMatches: null,
                    NativeLinearGradientBrushIdentityMatches: null,
                    NativeRadialGradientBrushIdentityMatches: null,
                    NativeSourceBitmapIdentityMatches: null,
                    NativeImageBrushIdentityMatches: null,
                    NativeGeneralImageBrushIdentityMatches: null,
                    NativeCommandListIdentityMatches: null,
                    NativeCommandListImageBrushIdentityMatches: null,
                    NativeEffectImageBrushIdentityMatches: null,
                    NativeGeometryIdentityMatches: null,
                    NativeStrokeStyleIdentityMatches: null,
                    SolidColorBrushColor: null,
                    LinearGradientBrushColor: null,
                    RadialGradientBrushColor: null,
                    CornerPixel: null,
                    CenterPixel: null,
                    SolidPixel: null,
                    RadialPixel: null,
                    ImageBrushPixel: null,
                    GeneralImageBrushPixel: null,
                    CommandListPixel: null,
                    EffectPixel: null,
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
        WriteProgress("run-started");
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
            WriteProgress("surface-created");

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
            WriteProgress("brush-roundtrips-complete");

            Color imageColor = Color.FromArgb(255, 144, 64, 240);
            Span<byte> sourcePixels = stackalloc byte[16];
            for (int offset = 0; offset < sourcePixels.Length; offset += 4)
            {
                sourcePixels[offset] = imageColor.B;
                sourcePixels[offset + 1] = imageColor.G;
                sourcePixels[offset + 2] = imageColor.R;
                sourcePixels[offset + 3] = imageColor.A;
            }
            using ProGpuDirect2DComReference nativeSourceBitmap =
                surface.CreateBitmap(sourcePixels, 2U, 2U, 8U);
            WriteProgress("source-bitmap-created");
            using ProGpuDirect2DComReference nativeImageBrush =
                surface.CreateBitmapBrush(
                    nativeSourceBitmap,
                    new ProGpuDirect2DBitmapBrushProperties(
                        ProGpuDirect2DExtendMode.Wrap,
                        ProGpuDirect2DExtendMode.Mirror,
                        ProGpuDirect2DInterpolationMode.NearestNeighbor));
            WriteProgress("bitmap-brush-created");
            Color generalImageColor = Color.FromArgb(255, 48, 224, 176);
            Span<byte> generalSourcePixels = stackalloc byte[16];
            for (int row = 0; row < 2; row++)
            {
                int leftOffset = row * 8;
                generalSourcePixels[leftOffset] = imageColor.B;
                generalSourcePixels[leftOffset + 1] = imageColor.G;
                generalSourcePixels[leftOffset + 2] = imageColor.R;
                generalSourcePixels[leftOffset + 3] = imageColor.A;
                int rightOffset = leftOffset + 4;
                generalSourcePixels[rightOffset] = generalImageColor.B;
                generalSourcePixels[rightOffset + 1] = generalImageColor.G;
                generalSourcePixels[rightOffset + 2] = generalImageColor.R;
                generalSourcePixels[rightOffset + 3] = generalImageColor.A;
            }
            using ProGpuDirect2DComReference nativeGeneralSourceBitmap =
                surface.CreateBitmap(generalSourcePixels, 2U, 2U, 8U);
            using ProGpuDirect2DComReference nativeGeneralImageBrush =
                surface.CreateImageBrush(
                    nativeGeneralSourceBitmap,
                    new ProGpuDirect2DImageBrushProperties(
                        new ProGpuDirect2DRect(1.0F, 0.0F, 1.0F, 2.0F),
                        ProGpuDirect2DExtendMode.Clamp,
                        ProGpuDirect2DExtendMode.Clamp,
                        ProGpuDirect2DInterpolationMode.NearestNeighbor));
            WriteProgress("general-image-brush-created");
            WriteProgress("canvas-bitmap-wrap-started");
            if (!surface.TryAcquireMicrosoftWin2DBitmap(
                    nativeSourceBitmap,
                    out ProGpuDirect2DComReference? wrappedSourceBitmap,
                    out int wrappedSourceBitmapHResult) ||
                wrappedSourceBitmap is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasBitmap wrapping failed (0x{wrappedSourceBitmapHResult:X8}).");
            }
            WriteProgress("canvas-bitmap-wrapped");
            using ProGpuDirect2DComReference canvasBitmapReference =
                wrappedSourceBitmap;
            if (!surface.TryAcquireMicrosoftWin2DNativeBitmap(
                    canvasBitmapReference,
                    out ProGpuDirect2DComReference? unwrappedSourceBitmap,
                    out int unwrappedSourceBitmapHResult) ||
                unwrappedSourceBitmap is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasBitmap native-resource interop failed (0x{unwrappedSourceBitmapHResult:X8}).");
            }
            using (unwrappedSourceBitmap)
            {
                if (!HasSameComIdentity(
                        nativeSourceBitmap,
                        unwrappedSourceBitmap))
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasBitmap did not preserve ProGPU's uploaded ID2D1Bitmap1 identity.");
                }
            }
            WriteProgress("canvas-bitmap-roundtrip-complete");
            WriteProgress("canvas-image-brush-wrap-started");
            if (!surface.TryAcquireMicrosoftWin2DImageBrush(
                    nativeImageBrush,
                    out ProGpuDirect2DComReference? wrappedImageBrush,
                    out int wrappedImageBrushHResult) ||
                wrappedImageBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasImageBrush wrapping failed (0x{wrappedImageBrushHResult:X8}).");
            }
            WriteProgress("canvas-image-brush-wrapped");
            using ProGpuDirect2DComReference canvasImageBrushReference =
                wrappedImageBrush;
            if (!surface.TryAcquireMicrosoftWin2DNativeImageBrush(
                    canvasImageBrushReference,
                    out ProGpuDirect2DComReference? unwrappedImageBrush,
                    out int unwrappedImageBrushHResult) ||
                unwrappedImageBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasImageBrush native-resource interop failed (0x{unwrappedImageBrushHResult:X8}).");
            }
            using (unwrappedImageBrush)
            {
                if (!HasSameComIdentity(nativeImageBrush, unwrappedImageBrush))
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasImageBrush did not preserve ProGPU's ID2D1BitmapBrush1 identity.");
                }
            }
            WriteProgress("image-brush-roundtrips-complete");
            if (!surface.TryAcquireMicrosoftWin2DImageBrush(
                    nativeGeneralImageBrush,
                    out ProGpuDirect2DComReference? wrappedGeneralImageBrush,
                    out int wrappedGeneralImageBrushHResult) ||
                wrappedGeneralImageBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasImageBrush ID2D1ImageBrush wrapping failed (0x{wrappedGeneralImageBrushHResult:X8}).");
            }
            using ProGpuDirect2DComReference canvasGeneralImageBrushReference =
                wrappedGeneralImageBrush;
            if (!surface.TryAcquireMicrosoftWin2DNativeImageBrush(
                    canvasGeneralImageBrushReference,
                    ProGpuDirect2DInterfaceKind.D2D1ImageBrush,
                    out ProGpuDirect2DComReference? unwrappedGeneralImageBrush,
                    out int unwrappedGeneralImageBrushHResult) ||
                unwrappedGeneralImageBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasImageBrush ID2D1ImageBrush native-resource interop failed (0x{unwrappedGeneralImageBrushHResult:X8}).");
            }
            using (unwrappedGeneralImageBrush)
            {
                if (!HasSameComIdentity(
                        nativeGeneralImageBrush,
                        unwrappedGeneralImageBrush))
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasImageBrush did not preserve ProGPU's ID2D1ImageBrush identity.");
                }
            }
            WriteProgress("general-image-brush-roundtrip-complete");

            Color effectColor = Color.FromArgb(255, 112, 40, 248);
            Span<byte> effectSourcePixels = stackalloc byte[16];
            for (int offset = 0;
                 offset < effectSourcePixels.Length;
                 offset += 4)
            {
                effectSourcePixels[offset] = effectColor.B;
                effectSourcePixels[offset + 1] = effectColor.G;
                effectSourcePixels[offset + 2] = effectColor.R;
                effectSourcePixels[offset + 3] = effectColor.A;
            }
            using ProGpuDirect2DComReference nativeEffectSourceBitmap =
                surface.CreateBitmap(effectSourcePixels, 2U, 2U, 8U);
            using ProGpuDirect2DComReference nativeGaussianBlurEffect =
                surface.CreateEffect(
                    ProGpuDirect2DBuiltInEffects.GaussianBlur);
            surface.SetEffectInput(
                nativeGaussianBlurEffect,
                0U,
                nativeEffectSourceBitmap);
            surface.SetEffectFloat(
                nativeGaussianBlurEffect,
                (uint)ProGpuDirect2DGaussianBlurProperty
                    .StandardDeviation,
                0.0F);
            using ProGpuDirect2DComReference nativeEffectOutput =
                surface.GetEffectOutput(nativeGaussianBlurEffect);
            using ProGpuDirect2DComReference nativeEffectImageBrush =
                surface.CreateImageBrush(
                    nativeEffectOutput,
                    new ProGpuDirect2DImageBrushProperties(
                        new ProGpuDirect2DRect(0.0F, 0.0F, 2.0F, 2.0F),
                        ProGpuDirect2DExtendMode.Wrap,
                        ProGpuDirect2DExtendMode.Wrap,
                        ProGpuDirect2DInterpolationMode.NearestNeighbor));
            WriteProgress("effect-image-brush-created");
            if (!surface.TryAcquireMicrosoftWin2DImageBrush(
                    nativeEffectImageBrush,
                    out ProGpuDirect2DComReference? wrappedEffectImageBrush,
                    out int wrappedEffectImageBrushHResult) ||
                wrappedEffectImageBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D effect-output CanvasImageBrush wrapping failed (0x{wrappedEffectImageBrushHResult:X8}).");
            }
            using ProGpuDirect2DComReference canvasEffectImageBrushReference =
                wrappedEffectImageBrush;
            if (!surface.TryAcquireMicrosoftWin2DNativeImageBrush(
                    canvasEffectImageBrushReference,
                    ProGpuDirect2DInterfaceKind.D2D1ImageBrush,
                    out ProGpuDirect2DComReference?
                        unwrappedEffectImageBrush,
                    out int unwrappedEffectImageBrushHResult) ||
                unwrappedEffectImageBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D effect-output CanvasImageBrush native-resource interop failed (0x{unwrappedEffectImageBrushHResult:X8}).");
            }
            using (unwrappedEffectImageBrush)
            {
                if (!HasSameComIdentity(
                        nativeEffectImageBrush,
                        unwrappedEffectImageBrush))
                {
                    throw new InvalidOperationException(
                        "Win2D effect-output CanvasImageBrush did not preserve ProGPU's ID2D1ImageBrush identity.");
                }
            }
            WriteProgress("effect-image-brush-roundtrip-complete");

            Color commandListColor = Color.FromArgb(255, 248, 112, 40);
            using ProGpuDirect2DComReference nativeCommandList =
                surface.CreateCommandList();
            WriteProgress("command-list-created");
            if (!surface.TryAcquireMicrosoftWin2DCommandList(
                    nativeCommandList,
                    out ProGpuDirect2DComReference? wrappedCommandList,
                    out int wrappedCommandListHResult) ||
                wrappedCommandList is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasCommandList wrapping failed (0x{wrappedCommandListHResult:X8}).");
            }
            using ProGpuDirect2DComReference canvasCommandListReference =
                wrappedCommandList;
            string canvasCommandListType;
            using (CanvasCommandList canvasCommandList =
                CanvasCommandList.FromAbi(
                    canvasCommandListReference.DangerousGetHandle()))
            {
                canvasCommandListType =
                    canvasCommandList.GetType().FullName ??
                    canvasCommandList.GetType().Name;
                using (CanvasDrawingSession commandListDrawingSession =
                    canvasCommandList.CreateDrawingSession())
                {
                    commandListDrawingSession.FillRectangle(
                        0.0F,
                        0.0F,
                        4.0F,
                        56.0F,
                        commandListColor);
                }
                WriteProgress("command-list-recorded");
                _ = canvasCommandList.GetBounds(canvasCommandList.Device);
                WriteProgress("command-list-realized");
                if (!surface.TryAcquireMicrosoftWin2DNativeCommandList(
                        canvasCommandListReference,
                        out ProGpuDirect2DComReference? unwrappedCommandList,
                        out int unwrappedCommandListHResult) ||
                    unwrappedCommandList is null)
                {
                    throw new InvalidOperationException(
                        $"Win2D CanvasCommandList native-resource interop failed (0x{unwrappedCommandListHResult:X8}).");
                }
                using (unwrappedCommandList)
                {
                    if (!HasSameComIdentity(
                            nativeCommandList,
                            unwrappedCommandList))
                    {
                        throw new InvalidOperationException(
                            "Win2D CanvasCommandList did not preserve ProGPU's ID2D1CommandList identity.");
                    }
                }
            }
            using ProGpuDirect2DComReference nativeCommandListImageBrush =
                surface.CreateImageBrush(
                    nativeCommandList,
                    new ProGpuDirect2DImageBrushProperties(
                        new ProGpuDirect2DRect(0.0F, 0.0F, 4.0F, 56.0F),
                        ProGpuDirect2DExtendMode.Wrap,
                        ProGpuDirect2DExtendMode.Wrap,
                        ProGpuDirect2DInterpolationMode.NearestNeighbor));
            if (!surface.TryAcquireMicrosoftWin2DImageBrush(
                    nativeCommandListImageBrush,
                    out ProGpuDirect2DComReference?
                        wrappedCommandListImageBrush,
                    out int wrappedCommandListImageBrushHResult) ||
                wrappedCommandListImageBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D command-list CanvasImageBrush wrapping failed (0x{wrappedCommandListImageBrushHResult:X8}).");
            }
            using ProGpuDirect2DComReference
                canvasCommandListImageBrushReference =
                    wrappedCommandListImageBrush;
            if (!surface.TryAcquireMicrosoftWin2DNativeImageBrush(
                    canvasCommandListImageBrushReference,
                    ProGpuDirect2DInterfaceKind.D2D1ImageBrush,
                    out ProGpuDirect2DComReference?
                        unwrappedCommandListImageBrush,
                    out int unwrappedCommandListImageBrushHResult) ||
                unwrappedCommandListImageBrush is null)
            {
                throw new InvalidOperationException(
                    $"Win2D command-list CanvasImageBrush native-resource interop failed (0x{unwrappedCommandListImageBrushHResult:X8}).");
            }
            using (unwrappedCommandListImageBrush)
            {
                if (!HasSameComIdentity(
                        nativeCommandListImageBrush,
                        unwrappedCommandListImageBrush))
                {
                    throw new InvalidOperationException(
                        "Win2D command-list CanvasImageBrush did not preserve ProGPU's ID2D1ImageBrush identity.");
                }
            }
            WriteProgress("command-list-roundtrips-complete");

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
            WriteProgress("geometry-create-started");
            using ProGpuDirect2DComReference nativeGeometry =
                surface.CreateGeometry(combinedGeometry);
            WriteProgress("geometry-created");
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
            WriteProgress("geometry-wrapped");
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
            WriteProgress("geometry-roundtrip-complete");

            Span<float> customDashes = stackalloc float[4]
            {
                2.0F,
                1.0F,
                0.5F,
                1.0F
            };
            using ProGpuDirect2DComReference nativeStrokeStyle =
                surface.CreateStrokeStyle(
                    new ProGpuDirect2DStrokeStyleProperties(
                        StartCap: ProGpuDirect2DCapStyle.Round,
                        EndCap: ProGpuDirect2DCapStyle.Triangle,
                        DashCap: ProGpuDirect2DCapStyle.Square,
                        LineJoin: ProGpuDirect2DLineJoin.Bevel,
                        MiterLimit: 6.0F,
                        DashStyle: ProGpuDirect2DDashStyle.Custom,
                        DashOffset: 0.5F,
                        TransformType:
                            ProGpuDirect2DStrokeTransformType.Fixed),
                    customDashes);
            if (!surface.TryAcquireMicrosoftWin2DStrokeStyle(
                    nativeStrokeStyle,
                    out ProGpuDirect2DComReference? wrappedStrokeStyle,
                    out int wrappedStrokeStyleHResult) ||
                wrappedStrokeStyle is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasStrokeStyle wrapping failed (0x{wrappedStrokeStyleHResult:X8}).");
            }
            using ProGpuDirect2DComReference canvasStrokeStyleReference =
                wrappedStrokeStyle;
            if (!surface.TryAcquireMicrosoftWin2DNativeStrokeStyle(
                    canvasStrokeStyleReference,
                    out ProGpuDirect2DComReference? unwrappedStrokeStyle,
                    out int unwrappedStrokeStyleHResult) ||
                unwrappedStrokeStyle is null)
            {
                throw new InvalidOperationException(
                    $"Win2D CanvasStrokeStyle native-resource interop failed (0x{unwrappedStrokeStyleHResult:X8}).");
            }
            using (unwrappedStrokeStyle)
            {
                if (!HasSameComIdentity(
                        nativeStrokeStyle,
                        unwrappedStrokeStyle))
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasStrokeStyle did not preserve ProGPU's ID2D1StrokeStyle1 identity.");
                }
            }
            WriteProgress("stroke-style-roundtrip-complete");

            ulong contentVersionBefore = surface.ContentVersion;
            WriteProgress("producer-access-started");
            if (!surface.TryBeginMicrosoftWin2DProducerAccess(
                    out ProGpuMicrosoftWin2DProducerAccess? access,
                    out int nativeHResult) ||
                access is null)
            {
                throw new InvalidOperationException(
                    $"The genuine Win2D producer could not be acquired (0x{nativeHResult:X8}).");
            }
            WriteProgress("producer-access-acquired");

            string canvasDeviceType;
            string canvasRenderTargetType;
            string canvasSolidColorBrushType;
            string canvasLinearGradientBrushType;
            string canvasRadialGradientBrushType;
            string canvasBitmapType;
            string canvasImageBrushType;
            string canvasGeneralImageBrushType;
            string canvasEffectImageBrushType;
            string canvasGeometryType;
            string canvasStrokeStyleType;
            string drawingSessionType;
            PixelEvidence solidColorBrushColor;
            PixelEvidence linearGradientBrushColor;
            PixelEvidence radialGradientBrushColor;
            PixelEvidence cornerPixel;
            PixelEvidence centerPixel;
            PixelEvidence solidPixel;
            PixelEvidence radialPixel;
            PixelEvidence imageBrushPixel;
            PixelEvidence generalImageBrushPixel;
            PixelEvidence commandListPixel;
            PixelEvidence effectPixel;
            PixelEvidence geometryPixel;
            using (access)
            {
                WriteProgress("canvas-target-projection-started");
                using CanvasRenderTarget target =
                    CanvasRenderTarget.FromAbi(
                        access.CanvasRenderTarget.DangerousGetHandle());
                WriteProgress("canvas-target-projected");
                using CanvasSolidColorBrush canvasSolidColorBrush =
                    CanvasSolidColorBrush.FromAbi(
                        canvasSolidColorBrushReference.DangerousGetHandle());
                WriteProgress("canvas-solid-brush-projected");
                using CanvasLinearGradientBrush canvasLinearGradientBrush =
                    CanvasLinearGradientBrush.FromAbi(
                        canvasLinearGradientBrushReference.DangerousGetHandle());
                WriteProgress("canvas-linear-brush-projected");
                using CanvasRadialGradientBrush canvasRadialGradientBrush =
                    CanvasRadialGradientBrush.FromAbi(
                        canvasRadialGradientBrushReference.DangerousGetHandle());
                WriteProgress("canvas-radial-brush-projected");
                WriteProgress("canvas-bitmap-projection-started");
                using CanvasBitmap canvasBitmap =
                    CanvasBitmap.FromAbi(
                        canvasBitmapReference.DangerousGetHandle());
                WriteProgress("canvas-bitmap-projected");
                WriteProgress("canvas-image-brush-projection-started");
                using CanvasImageBrush canvasImageBrush =
                    CanvasImageBrush.FromAbi(
                        canvasImageBrushReference.DangerousGetHandle());
                WriteProgress("canvas-image-brush-projected");
                using CanvasImageBrush canvasGeneralImageBrush =
                    CanvasImageBrush.FromAbi(
                        canvasGeneralImageBrushReference.DangerousGetHandle());
                WriteProgress("canvas-general-image-brush-projected");
                using CanvasImageBrush canvasCommandListImageBrush =
                    CanvasImageBrush.FromAbi(
                        canvasCommandListImageBrushReference
                            .DangerousGetHandle());
                WriteProgress("canvas-command-list-image-brush-projected");
                using CanvasImageBrush canvasEffectImageBrush =
                    CanvasImageBrush.FromAbi(
                        canvasEffectImageBrushReference
                            .DangerousGetHandle());
                WriteProgress("canvas-effect-image-brush-projected");
                using CanvasGeometry canvasGeometry =
                    CanvasGeometry.FromAbi(
                        canvasGeometryReference.DangerousGetHandle());
                WriteProgress("canvas-geometry-projected");
                using CanvasStrokeStyle canvasStrokeStyle =
                    CanvasStrokeStyle.FromAbi(
                        canvasStrokeStyleReference.DangerousGetHandle());
                WriteProgress("canvas-projections-created");
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
                canvasBitmapType = canvasBitmap.GetType().FullName ??
                    canvasBitmap.GetType().Name;
                canvasImageBrushType =
                    canvasImageBrush.GetType().FullName ??
                    canvasImageBrush.GetType().Name;
                canvasGeneralImageBrushType =
                    canvasGeneralImageBrush.GetType().FullName ??
                    canvasGeneralImageBrush.GetType().Name;
                canvasEffectImageBrushType =
                    canvasEffectImageBrush.GetType().FullName ??
                    canvasEffectImageBrush.GetType().Name;
                Windows.Foundation.Rect? projectedSourceRectangle =
                    canvasGeneralImageBrush.SourceRectangle;
                if (projectedSourceRectangle is not Windows.Foundation.Rect sourceRectangle ||
                    sourceRectangle.X != 1.0 || sourceRectangle.Y != 0.0 ||
                    sourceRectangle.Width != 1.0 ||
                    sourceRectangle.Height != 2.0)
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasImageBrush source rectangle changed.");
                }
                Windows.Foundation.Rect? projectedCommandListRectangle =
                    canvasCommandListImageBrush.SourceRectangle;
                if (projectedCommandListRectangle is not Windows.Foundation.Rect commandListRectangle ||
                    commandListRectangle.X != 0.0 ||
                    commandListRectangle.Y != 0.0 ||
                    commandListRectangle.Width != 4.0 ||
                    commandListRectangle.Height != 56.0)
                {
                    throw new InvalidOperationException(
                        "Win2D command-list CanvasImageBrush source rectangle changed.");
                }
                Windows.Foundation.Rect? projectedEffectRectangle =
                    canvasEffectImageBrush.SourceRectangle;
                if (projectedEffectRectangle is not Windows.Foundation.Rect effectRectangle ||
                    effectRectangle.X != 0.0 ||
                    effectRectangle.Y != 0.0 ||
                    effectRectangle.Width != 2.0 ||
                    effectRectangle.Height != 2.0)
                {
                    throw new InvalidOperationException(
                        "Win2D effect-output CanvasImageBrush source rectangle changed.");
                }
                canvasGeometryType = canvasGeometry.GetType().FullName ??
                    canvasGeometry.GetType().Name;
                canvasStrokeStyleType =
                    canvasStrokeStyle.GetType().FullName ??
                    canvasStrokeStyle.GetType().Name;
                float[] projectedDashes = canvasStrokeStyle.CustomDashStyle;
                if (projectedDashes.Length != 4 ||
                    projectedDashes[0] != 2.0F ||
                    projectedDashes[1] != 1.0F ||
                    projectedDashes[2] != 0.5F ||
                    projectedDashes[3] != 1.0F)
                {
                    throw new InvalidOperationException(
                        "Win2D CanvasStrokeStyle custom dash metadata changed.");
                }
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
                    drawingSession.FillRectangle(
                        60.0F,
                        4.0F,
                        4.0F,
                        56.0F,
                        canvasImageBrush);
                    drawingSession.FillRectangle(
                        64.0F,
                        4.0F,
                        4.0F,
                        56.0F,
                        canvasGeneralImageBrush);
                    drawingSession.FillRectangle(
                        68.0F,
                        4.0F,
                        4.0F,
                        56.0F,
                        canvasCommandListImageBrush);
                    drawingSession.FillRectangle(
                        72.0F,
                        4.0F,
                        4.0F,
                        56.0F,
                        canvasEffectImageBrush);
                    drawingSession.FillGeometry(
                        canvasGeometry,
                        Color.FromArgb(255, 240, 208, 32));
                    drawingSession.DrawGeometry(
                        canvasGeometry,
                        Color.FromArgb(255, 32, 208, 240),
                        2.0F,
                        canvasStrokeStyle);
                }
                WriteProgress("canvas-draw-complete");

                Color[] pixels = target.GetPixelColors();
                WriteProgress("pixel-readback-complete");
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
                Color imageBrush =
                    pixels[checked((int)(32U * Width + 62U))];
                Color generalImageBrush =
                    pixels[checked((int)(32U * Width + 66U))];
                Color commandList =
                    pixels[checked((int)(32U * Width + 70U))];
                Color effect =
                    pixels[checked((int)(32U * Width + 74U))];
                Color geometryColor =
                    pixels[checked((int)(32U * Width + 8U))];
                cornerPixel = PixelEvidence.FromColor(corner);
                centerPixel = PixelEvidence.FromColor(center);
                solidPixel = PixelEvidence.FromColor(solid);
                radialPixel = PixelEvidence.FromColor(radial);
                imageBrushPixel = PixelEvidence.FromColor(imageBrush);
                generalImageBrushPixel =
                    PixelEvidence.FromColor(generalImageBrush);
                commandListPixel = PixelEvidence.FromColor(commandList);
                effectPixel = PixelEvidence.FromColor(effect);
                geometryPixel = PixelEvidence.FromColor(geometryColor);
                if (corner.A != 0 ||
                    !MatchesColor(solid, fill) ||
                    !MatchesColor(center, linearColor) ||
                    !MatchesColor(radial, radialColor) ||
                    !MatchesColor(imageBrush, imageColor) ||
                    !MatchesColor(generalImageBrush, generalImageColor) ||
                    !MatchesColor(commandList, commandListColor) ||
                    !MatchesColor(effect, effectColor) ||
                    !MatchesColor(
                        geometryColor,
                        Color.FromArgb(255, 240, 208, 32)))
                {
                    throw new InvalidOperationException(
                        $"Win2D pixel oracle failed: corner={cornerPixel}, solid={solidPixel}, linear={centerPixel}, radial={radialPixel}, bitmapBrush={imageBrushPixel}, imageBrush={generalImageBrushPixel}, commandList={commandListPixel}, effect={effectPixel}, geometry={geometryPixel}.");
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
                CanvasBitmapType: canvasBitmapType,
                CanvasImageBrushType: canvasImageBrushType,
                CanvasGeneralImageBrushType: canvasGeneralImageBrushType,
                CanvasCommandListType: canvasCommandListType,
                CanvasEffectImageBrushType: canvasEffectImageBrushType,
                CanvasGeometryType: canvasGeometryType,
                CanvasStrokeStyleType: canvasStrokeStyleType,
                DrawingSessionType: drawingSessionType,
                NativeDeviceIdentityMatches: true,
                NativeBitmapIdentityMatches: true,
                NativeSolidColorBrushIdentityMatches: true,
                NativeLinearGradientBrushIdentityMatches: true,
                NativeRadialGradientBrushIdentityMatches: true,
                NativeSourceBitmapIdentityMatches: true,
                NativeImageBrushIdentityMatches: true,
                NativeGeneralImageBrushIdentityMatches: true,
                NativeCommandListIdentityMatches: true,
                NativeCommandListImageBrushIdentityMatches: true,
                NativeEffectImageBrushIdentityMatches: true,
                NativeGeometryIdentityMatches: true,
                NativeStrokeStyleIdentityMatches: true,
                SolidColorBrushColor: solidColorBrushColor,
                LinearGradientBrushColor: linearGradientBrushColor,
                RadialGradientBrushColor: radialGradientBrushColor,
                CornerPixel: cornerPixel,
                CenterPixel: centerPixel,
                SolidPixel: solidPixel,
                RadialPixel: radialPixel,
                ImageBrushPixel: imageBrushPixel,
                GeneralImageBrushPixel: generalImageBrushPixel,
                CommandListPixel: commandListPixel,
                EffectPixel: effectPixel,
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

    private static bool WriteEvidence(IntegrationEvidence evidence)
    {
        string json = JsonSerializer.Serialize(
            evidence,
            new JsonSerializerOptions { WriteIndented = true });
        bool wroteEvidence = false;
        try
        {
            string packageDirectory = ApplicationData.Current.LocalFolder.Path;
            Directory.CreateDirectory(packageDirectory);
            File.WriteAllText(
                Path.Combine(packageDirectory, ResultFileName),
                json);
            wroteEvidence = true;
        }
        catch
        {
            // Full-trust package activation failures can make ApplicationData
            // unavailable while the diagnostic catch path is running. Never
            // let that secondary failure erase the original interop evidence.
        }

        try
        {
            string fallbackDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                FallbackResultDirectoryName);
            Directory.CreateDirectory(fallbackDirectory);
            File.WriteAllText(
                Path.Combine(fallbackDirectory, ResultFileName),
                json);
            wroteEvidence = true;
        }
        catch
        {
            // The process exit code and last durable progress stage remain
            // observable even when both package and fallback writes fail.
        }

        if (!wroteEvidence)
        {
            WriteProgress("evidence-write-failed");
        }
        return wroteEvidence;
    }

    private static void WriteProgress(string stage)
    {
        try
        {
            string packageDirectory = ApplicationData.Current.LocalFolder.Path;
            Directory.CreateDirectory(packageDirectory);
            File.WriteAllText(
                Path.Combine(packageDirectory, ProgressFileName),
                stage);
        }
        catch
        {
            // Progress reporting must never change the integration result.
        }

        try
        {
            string fallbackDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                FallbackResultDirectoryName);
            Directory.CreateDirectory(fallbackDirectory);
            File.WriteAllText(
                Path.Combine(fallbackDirectory, ProgressFileName),
                stage);
        }
        catch
        {
            // Package redirection can make the fallback path unavailable.
        }
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
        string? CanvasBitmapType,
        string? CanvasImageBrushType,
        string? CanvasGeneralImageBrushType,
        string? CanvasCommandListType,
        string? CanvasEffectImageBrushType,
        string? CanvasGeometryType,
        string? CanvasStrokeStyleType,
        string? DrawingSessionType,
        bool? NativeDeviceIdentityMatches,
        bool? NativeBitmapIdentityMatches,
        bool? NativeSolidColorBrushIdentityMatches,
        bool? NativeLinearGradientBrushIdentityMatches,
        bool? NativeRadialGradientBrushIdentityMatches,
        bool? NativeSourceBitmapIdentityMatches,
        bool? NativeImageBrushIdentityMatches,
        bool? NativeGeneralImageBrushIdentityMatches,
        bool? NativeCommandListIdentityMatches,
        bool? NativeCommandListImageBrushIdentityMatches,
        bool? NativeEffectImageBrushIdentityMatches,
        bool? NativeGeometryIdentityMatches,
        bool? NativeStrokeStyleIdentityMatches,
        PixelEvidence? SolidColorBrushColor,
        PixelEvidence? LinearGradientBrushColor,
        PixelEvidence? RadialGradientBrushColor,
        PixelEvidence? CornerPixel,
        PixelEvidence? CenterPixel,
        PixelEvidence? SolidPixel,
        PixelEvidence? RadialPixel,
        PixelEvidence? ImageBrushPixel,
        PixelEvidence? GeneralImageBrushPixel,
        PixelEvidence? CommandListPixel,
        PixelEvidence? EffectPixel,
        PixelEvidence? GeometryPixel,
        string? Error);

    private sealed record PixelEvidence(byte A, byte R, byte G, byte B)
    {
        public static PixelEvidence FromColor(Color color) =>
            new(color.A, color.R, color.G, color.B);
    }
}
