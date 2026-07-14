using System.Reflection;
using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortablePopupCreateRequestTests
{
    [Fact]
    public void LegacyConstructorPreservesCoordinatesAndDefaultsOwnerClientOrigin()
    {
        var placementTarget = new object();
        var ownerPresentationSource = new object();
        var ownerHandle = new IntPtr(17);

        var request = new PortablePopupCreateRequest(
            placementTarget,
            ownerPresentationSource,
            ownerHandle,
            x: -320,
            y: 480,
            isTransparent: true,
            isChildPopup: false);

        Assert.Same(placementTarget, request.PlacementTarget);
        Assert.Same(ownerPresentationSource, request.OwnerPresentationSource);
        Assert.Equal(ownerHandle, request.OwnerHandle);
        Assert.Equal(-320, request.PopupScreenDeviceX);
        Assert.Equal(480, request.PopupScreenDeviceY);
        Assert.Equal(-320, request.X);
        Assert.Equal(480, request.Y);
        Assert.Equal(0, request.OwnerClientScreenDeviceX);
        Assert.Equal(0, request.OwnerClientScreenDeviceY);
        Assert.True(request.IsTransparent);
        Assert.False(request.IsChildPopup);
    }

    [Fact]
    public void CoordinateAwareConstructorCarriesPopupAndOwnerClientScreenOrigins()
    {
        var request = new PortablePopupCreateRequest(
            placementTarget: null,
            ownerPresentationSource: null,
            ownerHandle: new IntPtr(23),
            popupScreenDeviceX: 1850,
            popupScreenDeviceY: 260,
            ownerClientScreenDeviceX: 1440,
            ownerClientScreenDeviceY: 120,
            isTransparent: false,
            isChildPopup: true);

        Assert.Equal(1850, request.PopupScreenDeviceX);
        Assert.Equal(260, request.PopupScreenDeviceY);
        Assert.Equal(1850, request.X);
        Assert.Equal(260, request.Y);
        Assert.Equal(1440, request.OwnerClientScreenDeviceX);
        Assert.Equal(120, request.OwnerClientScreenDeviceY);
        Assert.False(request.IsTransparent);
        Assert.True(request.IsChildPopup);
    }

    [Fact]
    public void PublicSurfacePreservesLegacyConstructorAndAddsCoordinateAwareOverload()
    {
        var type = typeof(PortablePopupCreateRequest);
        var legacyConstructor = type.GetConstructor(new[]
        {
            typeof(object),
            typeof(object),
            typeof(IntPtr),
            typeof(int),
            typeof(int),
            typeof(bool),
            typeof(bool),
        });
        var coordinateAwareConstructor = type.GetConstructor(new[]
        {
            typeof(object),
            typeof(object),
            typeof(IntPtr),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(bool),
            typeof(bool),
        });

        Assert.NotNull(legacyConstructor);
        Assert.Equal(
            new[]
            {
                "placementTarget",
                "ownerPresentationSource",
                "ownerHandle",
                "x",
                "y",
                "isTransparent",
                "isChildPopup",
            },
            legacyConstructor.GetParameters().Select(static parameter => parameter.Name));
        Assert.NotNull(coordinateAwareConstructor);
        Assert.Equal(
            new[]
            {
                "placementTarget",
                "ownerPresentationSource",
                "ownerHandle",
                "popupScreenDeviceX",
                "popupScreenDeviceY",
                "ownerClientScreenDeviceX",
                "ownerClientScreenDeviceY",
                "isTransparent",
                "isChildPopup",
            },
            coordinateAwareConstructor.GetParameters().Select(static parameter => parameter.Name));

        AssertPublicIntProperty(type, nameof(PortablePopupCreateRequest.X));
        AssertPublicIntProperty(type, nameof(PortablePopupCreateRequest.Y));
        AssertPublicIntProperty(type, nameof(PortablePopupCreateRequest.PopupScreenDeviceX));
        AssertPublicIntProperty(type, nameof(PortablePopupCreateRequest.PopupScreenDeviceY));
        AssertPublicIntProperty(type, nameof(PortablePopupCreateRequest.OwnerClientScreenDeviceX));
        AssertPublicIntProperty(type, nameof(PortablePopupCreateRequest.OwnerClientScreenDeviceY));
    }

    private static void AssertPublicIntProperty(Type type, string name)
    {
        var property = type.GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.NotNull(property);
        Assert.Equal(typeof(int), property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.True(property.GetMethod.IsPublic);
        Assert.Null(property.SetMethod);
    }
}
