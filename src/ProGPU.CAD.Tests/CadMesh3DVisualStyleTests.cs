using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using Microsoft.UI.Xaml.Media.Media3D;
using ProGPU.Backend.Native;
using ProGPU.CAD.Native;
using ProGPU.CAD.Sample;
using ProGPU.Scene.Extensions;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadMesh3DVisualStyleTests
{
    [Theory]
    [InlineData(CadMesh3DVisualStyle.Wireframe,
        RenderMode3D.Wireframe, ShadingMode3D.Flat)]
    [InlineData(CadMesh3DVisualStyle.Hidden,
        RenderMode3D.SolidWireframe, ShadingMode3D.HiddenLine)]
    [InlineData(CadMesh3DVisualStyle.Realistic,
        RenderMode3D.Solid, ShadingMode3D.Realistic)]
    [InlineData(CadMesh3DVisualStyle.Conceptual,
        RenderMode3D.SolidWireframe, ShadingMode3D.Conceptual)]
    [InlineData(CadMesh3DVisualStyle.Shaded,
        RenderMode3D.Solid, ShadingMode3D.Realistic)]
    [InlineData(CadMesh3DVisualStyle.ShadedWithEdges,
        RenderMode3D.SolidWireframe, ShadingMode3D.Realistic)]
    [InlineData(CadMesh3DVisualStyle.ShadesOfGray,
        RenderMode3D.Solid, ShadingMode3D.ShadesOfGray)]
    [InlineData(CadMesh3DVisualStyle.XRay,
        RenderMode3D.SolidWireframe, ShadingMode3D.XRay)]
    [InlineData(CadMesh3DVisualStyle.Normals,
        RenderMode3D.Solid, ShadingMode3D.Normals)]
    public void PolicySelectsOneExactManagedPipelineState(
        CadMesh3DVisualStyle visualStyle,
        RenderMode3D expectedRenderMode,
        ShadingMode3D expectedShadingMode)
    {
        CadMesh3DVisualStyleState state =
            CadMesh3DVisualStylePolicy.Resolve(visualStyle);

        Assert.Equal(expectedRenderMode, state.RenderMode);
        Assert.Equal(expectedShadingMode, state.ShadingMode);
    }

    [Fact]
    public void SharedShellSwitchesVisualStyleWithoutRebuildingCadGeometry()
    {
        var view = new CadSampleView();
        view.Canvas.Load(CreateFaceSession());
        object[] children = view.MeshViewport.Children.ToArray();
        CadRecordedMesh3DScene scene = Assert.IsType<CadRecordedMesh3DScene>(
            view.MeshScene);
        PerspectiveCamera camera = Assert.IsType<PerspectiveCamera>(
            view.MeshViewport.Camera);

        view.MeshVisualStyle = CadMesh3DVisualStyle.Hidden;

        Assert.Equal(RenderMode3D.SolidWireframe,
            view.MeshViewport.RenderMode);
        Assert.Equal(ShadingMode3D.HiddenLine,
            view.MeshViewport.ShadingMode);
        Assert.Same(scene, view.MeshScene);
        Assert.Same(camera, view.MeshViewport.Camera);
        Assert.Equal(children, view.MeshViewport.Children.ToArray());
    }

    [Theory]
    [InlineData(CadMesh3DVisualStyle.Wireframe,
        NativeMesh3DRenderMode.Wireframe,
        NativeMesh3DShadingMode.Flat)]
    [InlineData(CadMesh3DVisualStyle.Hidden,
        NativeMesh3DRenderMode.SolidWireframe,
        NativeMesh3DShadingMode.HiddenLine)]
    [InlineData(CadMesh3DVisualStyle.Conceptual,
        NativeMesh3DRenderMode.SolidWireframe,
        NativeMesh3DShadingMode.Conceptual)]
    [InlineData(CadMesh3DVisualStyle.XRay,
        NativeMesh3DRenderMode.SolidWireframe,
        NativeMesh3DShadingMode.XRay)]
    public void NativeAdapterEncodesTheSameAtomicVisualStyle(
        CadMesh3DVisualStyle visualStyle,
        NativeMesh3DRenderMode expectedRenderMode,
        NativeMesh3DShadingMode expectedShadingMode)
    {
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            CreateFaceSession());
        CadRecordedMesh3DScene scene =
            new CadMesh3DSceneCompiler().Compile(snapshot);
        var camera = new CadNativeMesh3DCamera(
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            new Vector3(0, 0, 5),
            new NativeImageRect(0, 0, 640, 480));

        CadNativeMesh3DScene native =
            new CadNativeMesh3DSceneCompiler().Compile(
                scene,
                camera,
                sceneId: 20260901U,
                new CadNativeMesh3DSceneOptions
                {
                    VisualStyle = visualStyle,
                });
        NativeSceneMesh3D encoded = ReadFirstMesh(native.Stream);

        Assert.Equal((uint)expectedRenderMode, encoded.RenderMode);
        Assert.Equal((uint)expectedShadingMode, encoded.ShadingMode);
        Assert.Equal(1.0f, encoded.LightDirection.W);
    }

    private static NativeSceneMesh3D ReadFirstMesh(ReadOnlySpan<byte> stream)
    {
        int resourceOffset = checked((int)
            BinaryPrimitives.ReadUInt32LittleEndian(stream[52..]));
        int payloadOffset = checked((int)
            BinaryPrimitives.ReadUInt32LittleEndian(
                stream[(resourceOffset + 32)..]));
        return MemoryMarshal.Read<NativeSceneMesh3D>(stream[payloadOffset..]);
    }

    private static CadDocumentSession CreateFaceSession()
    {
        var document = new CadDocument();
        document.Entities.Add(new Face3D
        {
            FirstCorner = XYZ.Zero,
            SecondCorner = XYZ.AxisX,
            ThirdCorner = new XYZ(1, 1, 0),
            FourthCorner = XYZ.AxisY,
        });
        return new CadDocumentSession(document);
    }
}
