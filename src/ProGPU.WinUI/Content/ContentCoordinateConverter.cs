using System.Numerics;
using Microsoft.UI.Windowing;
using ProGPU.WinUI.Platform;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics;

namespace Microsoft.UI.Content;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public class ContentCoordinateConverter
{
    private readonly WindowId _windowId;
    private readonly AppWindow? _appWindow;
    private readonly IContentCoordinateTransformSource? _source;

    protected internal ContentCoordinateConverter(
        WinRT.IObjectReference objRef)
    {
        ArgumentNullException.ThrowIfNull(objRef);
    }

    protected ContentCoordinateConverter(
        WinRT.DerivedComposed _)
    {
        ArgumentNullException.ThrowIfNull(_);
    }

    private ContentCoordinateConverter(
        WindowId windowId)
    {
        _windowId = windowId;
        _appWindow =
            AppWindow.GetFromWindowId(windowId);
    }

    internal ContentCoordinateConverter(
        IContentCoordinateTransformSource source)
    {
        _source = source ??
            throw new ArgumentNullException(
                nameof(source));
    }

    public Point ConvertScreenToLocal(
        PointInt32 screenPoint)
    {
        Matrix3x2 screenToLocal =
            GetScreenToLocalTransform();
        return Transform(
            screenPoint.X,
            screenPoint.Y,
            screenToLocal);
    }

    public Point[] ConvertScreenToLocal(
        PointInt32[] screenPoints)
    {
        ArgumentNullException.ThrowIfNull(
            screenPoints);

        var result = new Point[screenPoints.Length];
        Matrix3x2 screenToLocal =
            GetScreenToLocalTransform();
        for (int index = 0;
             index < screenPoints.Length;
             index++)
        {
            PointInt32 point = screenPoints[index];
            result[index] = Transform(
                point.X,
                point.Y,
                screenToLocal);
        }

        return result;
    }

    public Rect ConvertScreenToLocal(
        RectInt32 screenRect)
    {
        ValidateScreenRect(screenRect);
        return TransformBounds(
            screenRect.X,
            screenRect.Y,
            screenRect.Width,
            screenRect.Height,
            GetScreenToLocalTransform());
    }

    public PointInt32 ConvertLocalToScreen(
        Point localPoint)
    {
        Point transformed = Transform(
            localPoint.X,
            localPoint.Y,
            GetLocalToScreenTransform());
        return RoundPoint(
            transformed,
            ContentCoordinateRoundingMode.Auto);
    }

    public PointInt32[] ConvertLocalToScreen(
        Point[] localPoints) =>
        ConvertLocalToScreen(
            localPoints,
            ContentCoordinateRoundingMode.Auto);

    public PointInt32[] ConvertLocalToScreen(
        Point[] localPoints,
        ContentCoordinateRoundingMode roundingMode)
    {
        ArgumentNullException.ThrowIfNull(
            localPoints);
        ValidateRoundingMode(roundingMode);

        var result =
            new PointInt32[localPoints.Length];
        Matrix3x2 localToScreen =
            GetLocalToScreenTransform();
        for (int index = 0;
             index < localPoints.Length;
             index++)
        {
            Point point = localPoints[index];
            result[index] = RoundPoint(
                Transform(
                    point.X,
                    point.Y,
                    localToScreen),
                roundingMode);
        }

        return result;
    }

    public RectInt32 ConvertLocalToScreen(
        Rect localRect)
    {
        ValidateLocalRect(localRect);
        Rect bounds = TransformBounds(
            localRect.X,
            localRect.Y,
            localRect.Width,
            localRect.Height,
            GetLocalToScreenTransform());
        int left = RoundToInt32(
            bounds.X,
            ContentCoordinateRoundingMode.Auto);
        int top = RoundToInt32(
            bounds.Y,
            ContentCoordinateRoundingMode.Auto);
        int right = RoundToInt32(
            bounds.X + bounds.Width,
            ContentCoordinateRoundingMode.Auto);
        int bottom = RoundToInt32(
            bounds.Y + bounds.Height,
            ContentCoordinateRoundingMode.Auto);
        return new RectInt32(
            left,
            top,
            checked(right - left),
            checked(bottom - top));
    }

    public static ContentCoordinateConverter
        CreateForWindowId(
            WindowId windowId)
    {
        if (windowId.Value == 0)
        {
            throw new ArgumentException(
                "ContentCoordinateConverter requires a nonzero top-level WindowId.",
                nameof(windowId));
        }

        return new ContentCoordinateConverter(
            windowId);
    }

    private Matrix3x2 GetLocalToScreenTransform()
    {
        if (_source is not null)
        {
            Matrix3x2 sourceTransform =
                _source.GetLocalToScreenTransform();
            ValidateTransform(sourceTransform);
            return sourceTransform;
        }

        return GetWindowLocalToScreenTransform(
            _windowId,
            _appWindow);
    }

    internal static Matrix3x2
        GetWindowLocalToScreenTransform(
            WindowId windowId) =>
        GetWindowLocalToScreenTransform(
            windowId,
            AppWindow.GetFromWindowId(windowId));

    private static Matrix3x2
        GetWindowLocalToScreenTransform(
            WindowId windowId,
            AppWindow? appWindow)
    {
        IContentCoordinatePlatformProvider?
            provider =
                WindowingPlatformServices
                    .ContentCoordinates;
        Matrix3x2 transform;
        if (provider?.TryGetLocalToScreenTransform(
                windowId,
                out transform) != true)
        {
            PointInt32 position =
                appWindow?.Position ?? default;
            transform =
                Matrix3x2.CreateTranslation(
                    position.X,
                    position.Y);
        }

        ValidateTransform(transform);
        return transform;
    }

    private Matrix3x2 GetScreenToLocalTransform()
    {
        Matrix3x2 localToScreen =
            GetLocalToScreenTransform();
        if (!Matrix3x2.Invert(
                localToScreen,
                out Matrix3x2 screenToLocal))
        {
            throw new InvalidOperationException(
                "The local-to-screen coordinate transform is not invertible.");
        }

        ValidateTransform(screenToLocal);
        return screenToLocal;
    }

    private static Point Transform(
        double x,
        double y,
        in Matrix3x2 transform)
    {
        ValidateFinite(x, nameof(x));
        ValidateFinite(y, nameof(y));
        double transformedX =
            x * transform.M11 +
            y * transform.M21 +
            transform.M31;
        double transformedY =
            x * transform.M12 +
            y * transform.M22 +
            transform.M32;
        ValidateFinite(
            transformedX,
            "transformedX");
        ValidateFinite(
            transformedY,
            "transformedY");
        return new Point(
            transformedX,
            transformedY);
    }

    private static Rect TransformBounds(
        double x,
        double y,
        double width,
        double height,
        in Matrix3x2 transform)
    {
        Point topLeft =
            Transform(x, y, transform);
        Point topRight =
            Transform(
                x + width,
                y,
                transform);
        Point bottomLeft =
            Transform(
                x,
                y + height,
                transform);
        Point bottomRight =
            Transform(
                x + width,
                y + height,
                transform);

        double left = Math.Min(
            Math.Min(topLeft.X, topRight.X),
            Math.Min(
                bottomLeft.X,
                bottomRight.X));
        double top = Math.Min(
            Math.Min(topLeft.Y, topRight.Y),
            Math.Min(
                bottomLeft.Y,
                bottomRight.Y));
        double right = Math.Max(
            Math.Max(topLeft.X, topRight.X),
            Math.Max(
                bottomLeft.X,
                bottomRight.X));
        double bottom = Math.Max(
            Math.Max(topLeft.Y, topRight.Y),
            Math.Max(
                bottomLeft.Y,
                bottomRight.Y));

        return new Rect(
            left,
            top,
            right - left,
            bottom - top);
    }

    private static PointInt32 RoundPoint(
        Point point,
        ContentCoordinateRoundingMode roundingMode) =>
        new(
            RoundToInt32(
                point.X,
                roundingMode),
            RoundToInt32(
                point.Y,
                roundingMode));

    private static int RoundToInt32(
        double value,
        ContentCoordinateRoundingMode roundingMode)
    {
        ValidateFinite(value, nameof(value));
        double rounded = roundingMode switch
        {
            ContentCoordinateRoundingMode.Auto =>
                Math.Truncate(value),
            ContentCoordinateRoundingMode.Floor =>
                Math.Floor(value),
            ContentCoordinateRoundingMode.Round =>
                Math.Round(
                    value,
                    MidpointRounding.AwayFromZero),
            ContentCoordinateRoundingMode.Ceiling =>
                Math.Ceiling(value),
            _ => throw new ArgumentOutOfRangeException(
                nameof(roundingMode))
        };
        return checked((int)rounded);
    }

    private static void ValidateRoundingMode(
        ContentCoordinateRoundingMode roundingMode)
    {
        if (roundingMode <
                ContentCoordinateRoundingMode.Auto ||
            roundingMode >
                ContentCoordinateRoundingMode.Ceiling)
        {
            throw new ArgumentOutOfRangeException(
                nameof(roundingMode));
        }
    }

    private static void ValidateLocalRect(
        Rect rect)
    {
        ValidateFinite(rect.X, nameof(rect));
        ValidateFinite(rect.Y, nameof(rect));
        ValidateFinite(
            rect.Width,
            nameof(rect));
        ValidateFinite(
            rect.Height,
            nameof(rect));
        if (rect.Width < 0d ||
            rect.Height < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rect));
        }
    }

    private static void ValidateScreenRect(
        RectInt32 rect)
    {
        if (rect.Width < 0 ||
            rect.Height < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rect));
        }
    }

    private static void ValidateTransform(
        in Matrix3x2 transform)
    {
        if (!float.IsFinite(transform.M11) ||
            !float.IsFinite(transform.M12) ||
            !float.IsFinite(transform.M21) ||
            !float.IsFinite(transform.M22) ||
            !float.IsFinite(transform.M31) ||
            !float.IsFinite(transform.M32))
        {
            throw new InvalidOperationException(
                "The local-to-screen coordinate transform must contain only finite values.");
        }
    }

    private static void ValidateFinite(
        double value,
        string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName);
        }
    }
}

internal interface IContentCoordinateTransformSource
{
    Matrix3x2 GetLocalToScreenTransform();
}
