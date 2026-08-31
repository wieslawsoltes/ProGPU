using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadEllipseAuthoringTests
{
    private const double Tolerance = 1e-10;

    public static TheoryData<
        CadEllipseAuthoringMode,
        CadEllipseArcInputMode> ConstructionMatrix => new()
    {
        { CadEllipseAuthoringMode.AxisEndpointsDistance, CadEllipseArcInputMode.Full },
        { CadEllipseAuthoringMode.AxisEndpointsRotation, CadEllipseArcInputMode.Full },
        { CadEllipseAuthoringMode.CenterDistance, CadEllipseArcInputMode.Full },
        { CadEllipseAuthoringMode.CenterRotation, CadEllipseArcInputMode.Full },
        { CadEllipseAuthoringMode.AxisEndpointsDistance, CadEllipseArcInputMode.Angle },
        { CadEllipseAuthoringMode.AxisEndpointsRotation, CadEllipseArcInputMode.Angle },
        { CadEllipseAuthoringMode.CenterDistance, CadEllipseArcInputMode.Angle },
        { CadEllipseAuthoringMode.CenterRotation, CadEllipseArcInputMode.Angle },
        { CadEllipseAuthoringMode.AxisEndpointsDistance, CadEllipseArcInputMode.Parameter },
        { CadEllipseAuthoringMode.AxisEndpointsRotation, CadEllipseArcInputMode.Parameter },
        { CadEllipseAuthoringMode.CenterDistance, CadEllipseArcInputMode.Parameter },
        { CadEllipseAuthoringMode.CenterRotation, CadEllipseArcInputMode.Parameter },
        { CadEllipseAuthoringMode.AxisEndpointsDistance, CadEllipseArcInputMode.IncludedAngle },
        { CadEllipseAuthoringMode.AxisEndpointsRotation, CadEllipseArcInputMode.IncludedAngle },
        { CadEllipseAuthoringMode.CenterDistance, CadEllipseArcInputMode.IncludedAngle },
        { CadEllipseAuthoringMode.CenterRotation, CadEllipseArcInputMode.IncludedAngle },
    };

    [Theory]
    [MemberData(nameof(ConstructionMatrix))]
    public void CompleteConstructionMatrixResolvesOneAnalyticSnapshot(
        CadEllipseAuthoringMode mode,
        CadEllipseArcInputMode arcInputMode)
    {
        var authoring = new CadEllipseAuthoringSession(mode, arcInputMode);

        CadEllipseAuthoringSnapshot snapshot = CompleteStandard(
            authoring,
            arcInputMode);

        AssertPoint(new CadPoint3D(0, 0, 5), snapshot.Center);
        AssertPoint(new CadPoint3D(4, 0, 0), snapshot.MajorAxisEndPoint);
        AssertPoint(new CadPoint3D(0, 2, 0), snapshot.MinorAxisEndPoint);
        AssertClose(4.0, snapshot.MajorRadius);
        AssertClose(2.0, snapshot.MinorRadius);
        AssertClose(0.5, snapshot.RadiusRatio);
        if (arcInputMode == CadEllipseArcInputMode.Full)
        {
            Assert.True(snapshot.IsFullEllipse);
            AssertClose(0.0, snapshot.StartParameter);
            AssertClose(Math.PI * 2.0, snapshot.EndParameter);
        }
        else
        {
            Assert.False(snapshot.IsFullEllipse);
            AssertClose(0.0, snapshot.StartParameter);
            AssertClose(Math.PI / 2.0, snapshot.SweepParameter);
            AssertClose(Math.PI / 2.0, snapshot.EndParameter);
        }
    }

    [Theory]
    [InlineData(CadPlanIsoplane.Left, 120.0)]
    [InlineData(CadPlanIsoplane.Top, 0.0)]
    [InlineData(CadPlanIsoplane.Right, 60.0)]
    public void IsocircleRadiusProjectsExactActivePlane(
        CadPlanIsoplane isoplane,
        double expectedMajorAngleDegrees)
    {
        CadPlanGridSnapSettings settings =
            CadPlanGridSnapSettings.CreateIsometric(
                false,
                CadPoint3D.Zero,
                1.0,
                isoplane);
        var authoring = new CadEllipseAuthoringSession(
            CadEllipseAuthoringMode.IsocircleRadius,
            isometricSnapSettings: settings);
        AcceptPoint(authoring, new CadPoint3D(10, 20, 7));

        CadEllipseAuthoringSnapshot snapshot = CompleteScalar(authoring, 4.0);

        double angle = expectedMajorAngleDegrees * Math.PI / 180.0;
        double majorRadius = 4.0 * Math.Sqrt(1.5);
        AssertPoint(new CadPoint3D(10, 20, 7), snapshot.Center);
        AssertPoint(
            new CadPoint3D(
                majorRadius * Math.Cos(angle),
                majorRadius * Math.Sin(angle),
                0.0),
            snapshot.MajorAxisEndPoint);
        AssertClose(4.0 / Math.Sqrt(2.0), snapshot.MinorRadius);
        AssertClose(1.0 / Math.Sqrt(3.0), snapshot.RadiusRatio);
        Assert.True(snapshot.IsFullEllipse);
    }

    [Fact]
    public void IsocircleDiameterUsesRotatedCapturedBasisAndPointerDistance()
    {
        const double rotation = Math.PI / 9.0;
        CadPlanGridSnapSettings settings =
            CadPlanGridSnapSettings.CreateIsometric(
                true,
                CadPoint3D.Zero,
                1.0,
                CadPlanIsoplane.Top,
                rotation);
        var authoring = new CadEllipseAuthoringSession(
            CadEllipseAuthoringMode.IsocircleDiameter,
            isometricSnapSettings: settings);
        AcceptPoint(authoring, new CadPoint3D(-3, 5, 2));

        CadEllipseAuthoringSnapshot preview = CompletePoint(
            authoring,
            new CadPoint3D(5, 5, 2));

        double expectedMajorRadius = 4.0 * Math.Sqrt(1.5);
        AssertClose(expectedMajorRadius, preview.MajorRadius);
        AssertPoint(
            new CadPoint3D(
                expectedMajorRadius * Math.Cos(rotation),
                expectedMajorRadius * Math.Sin(rotation),
                0.0),
            preview.MajorAxisEndPoint);

        var scalarAuthoring = new CadEllipseAuthoringSession(
            CadEllipseAuthoringMode.IsocircleDiameter,
            isometricSnapSettings: settings);
        AcceptPoint(scalarAuthoring, new CadPoint3D(-3, 5, 2));
        CadEllipseAuthoringSnapshot scalar = CompleteScalar(
            scalarAuthoring,
            8.0);
        AssertPoint(preview.MajorAxisEndPoint, scalar.MajorAxisEndPoint);
    }

    [Fact]
    public void IsocircleRequiresIsometricStyleAndRejectsArcMode()
    {
        Assert.Throws<ArgumentException>(() =>
            new CadEllipseAuthoringSession(
                CadEllipseAuthoringMode.IsocircleRadius));
        CadPlanGridSnapSettings settings =
            CadPlanGridSnapSettings.CreateIsometric(
                false,
                CadPoint3D.Zero,
                1.0,
                CadPlanIsoplane.Left);
        Assert.Throws<ArgumentException>(() =>
            new CadEllipseAuthoringSession(
                CadEllipseAuthoringMode.IsocircleRadius,
                CadEllipseArcInputMode.Angle,
                settings));
    }

    [Fact]
    public void LongerOtherAxisCanonicalizesMajorAxisWithoutChangingGeometry()
    {
        var authoring = new CadEllipseAuthoringSession(
            CadEllipseAuthoringMode.AxisEndpointsDistance);
        AcceptPoint(authoring, new CadPoint3D(-2, 0, 7));
        AcceptPoint(authoring, new CadPoint3D(2, 0, 7));

        CadEllipseAuthoringSnapshot snapshot = CompletePoint(
            authoring,
            new CadPoint3D(0, 5, 7));

        AssertPoint(new CadPoint3D(0, 5, 0), snapshot.MajorAxisEndPoint);
        AssertPoint(new CadPoint3D(-2, 0, 0), snapshot.MinorAxisEndPoint);
        AssertClose(0.4, snapshot.RadiusRatio);
        AssertPoint(new CadPoint3D(0, 5, 7), snapshot.PointAt(0));
        AssertPoint(new CadPoint3D(-2, 0, 7), snapshot.PointAt(Math.PI / 2.0));
    }

    [Fact]
    public void AxisEndpointMidpointRemainsStableAtLargeWcsOrigin()
    {
        var authoring = new CadEllipseAuthoringSession(
            CadEllipseAuthoringMode.AxisEndpointsDistance);
        AcceptPoint(authoring, new CadPoint3D(
            10_000_000_000_000_000,
            -10_000_000_000_000_000,
            9));
        AcceptPoint(authoring, new CadPoint3D(
            10_000_000_000_000_008,
            -10_000_000_000_000_000,
            9));

        CadEllipseAuthoringSnapshot snapshot = CompletePoint(
            authoring,
            new CadPoint3D(
                10_000_000_000_000_004,
                -9_999_999_999_999_998,
                9));

        Assert.Equal(
            new CadPoint3D(
                10_000_000_000_000_004,
                -10_000_000_000_000_000,
                9),
            snapshot.Center);
        AssertPoint(new CadPoint3D(4, 0, 0), snapshot.MajorAxisEndPoint);
        AssertClose(0.5, snapshot.RadiusRatio);
    }

    [Theory]
    [InlineData(89.5)]
    [InlineData(90.0)]
    [InlineData(90.5)]
    public void RotationRejectsDocumentedEdgeOnInterval(double degrees)
    {
        var authoring = new CadEllipseAuthoringSession(
            CadEllipseAuthoringMode.CenterRotation);
        AcceptPoint(authoring, new CadPoint3D(0, 0, 3));
        AcceptPoint(authoring, new CadPoint3D(4, 0, 3));

        Assert.False(authoring.TryAcceptScalar(
            degrees * Math.PI / 180.0,
            out _,
            out _,
            out string? error));

        Assert.Contains("edge-on", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, authoring.AcceptedInputCount);
    }

    [Fact]
    public void RotationUsesMirroredAbsoluteCosineRatio()
    {
        var authoring = new CadEllipseAuthoringSession(
            CadEllipseAuthoringMode.CenterRotation);
        AcceptPoint(authoring, new CadPoint3D(0, 0, 3));
        AcceptPoint(authoring, new CadPoint3D(4, 0, 3));

        CadEllipseAuthoringSnapshot snapshot = CompleteScalar(
            authoring,
            120.0 * Math.PI / 180.0);

        AssertClose(0.5, snapshot.RadiusRatio);
    }

    [Fact]
    public void DirectionAngleMapsToEllipseParameterWithoutSampling()
    {
        var authoring = new CadEllipseAuthoringSession(
            CadEllipseAuthoringMode.CenterDistance,
            CadEllipseArcInputMode.Angle);
        AcceptPoint(authoring, new CadPoint3D(0, 0, 4));
        AcceptPoint(authoring, new CadPoint3D(0, 4, 4));
        AcceptPoint(authoring, new CadPoint3D(-2, 0, 4));
        AcceptScalar(authoring, 0.0);

        CadEllipseAuthoringSnapshot snapshot = CompleteScalar(
            authoring,
            Math.PI / 2.0);

        AssertClose(Math.PI * 1.5, snapshot.StartParameter);
        AssertClose(Math.PI / 2.0, snapshot.SweepParameter);
        AssertPoint(new CadPoint3D(2, 0, 4), snapshot.StartPoint);
        AssertPoint(new CadPoint3D(0, 4, 4), snapshot.EndPoint);
    }

    [Fact]
    public void ExplicitDirectionDoesNotAddAUnitPointAtLargeWcsOrigin()
    {
        var authoring = new CadEllipseAuthoringSession(
            CadEllipseAuthoringMode.CenterDistance,
            CadEllipseArcInputMode.Angle);
        AcceptPoint(authoring, new CadPoint3D(
            10_000_000_000_000_000,
            -10_000_000_000_000_000,
            8));
        AcceptPoint(authoring, new CadPoint3D(
            10_000_000_000_000_008,
            -10_000_000_000_000_000,
            8));
        AcceptPoint(authoring, new CadPoint3D(
            10_000_000_000_000_000,
            -9_999_999_999_999_996,
            8));
        Assert.True(authoring.TryAcceptDirection(
            new CadPoint3D(1, 0, 0),
            out _,
            out bool startCompleted,
            out _));
        Assert.False(startCompleted);

        Assert.True(authoring.TryAcceptDirection(
            new CadPoint3D(0, 1, 0),
            out CadEllipseAuthoringSnapshot snapshot,
            out bool completed,
            out _));

        Assert.True(completed);
        AssertClose(Math.PI / 2.0, snapshot.SweepParameter);
    }

    [Fact]
    public void NegativeIncludedAngleCanonicalizesSameClockwiseLocus()
    {
        var authoring = new CadEllipseAuthoringSession(
            CadEllipseAuthoringMode.CenterDistance,
            CadEllipseArcInputMode.IncludedAngle);
        AcceptPoint(authoring, new CadPoint3D(0, 0, 6));
        AcceptPoint(authoring, new CadPoint3D(4, 0, 6));
        AcceptPoint(authoring, new CadPoint3D(0, 2, 6));
        AcceptScalar(authoring, 0.0);

        CadEllipseAuthoringSnapshot snapshot = CompleteScalar(
            authoring,
            -Math.PI / 2.0);

        AssertClose(Math.PI * 1.5, snapshot.StartParameter);
        AssertClose(Math.PI / 2.0, snapshot.SweepParameter);
        AssertPoint(new CadPoint3D(0, -2, 6), snapshot.StartPoint);
        AssertPoint(new CadPoint3D(4, 0, 6), snapshot.EndPoint);
    }

    [Fact]
    public void InvalidFinalInputDoesNotAdvanceRecoverableState()
    {
        var authoring = new CadEllipseAuthoringSession(
            CadEllipseAuthoringMode.CenterDistance,
            CadEllipseArcInputMode.Parameter);
        AcceptPoint(authoring, new CadPoint3D(0, 0, 2));
        AcceptPoint(authoring, new CadPoint3D(4, 0, 2));
        AcceptPoint(authoring, new CadPoint3D(0, 2, 2));
        AcceptScalar(authoring, 0.25);

        Assert.False(authoring.TryAcceptScalar(
            0.25,
            out _,
            out _,
            out string? error));

        Assert.Contains("distinct", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, authoring.AcceptedInputCount);
        Assert.True(authoring.TryAcceptScalar(
            1.25,
            out CadEllipseAuthoringSnapshot snapshot,
            out bool completed,
            out _));
        Assert.True(completed);
        AssertClose(1.0, snapshot.SweepParameter);
        Assert.Equal(4, authoring.AcceptedInputCount);
    }

    [Fact]
    public void SessionRejectsNonfiniteDuplicateAndOffPlanePoints()
    {
        var authoring = new CadEllipseAuthoringSession(
            CadEllipseAuthoringMode.AxisEndpointsDistance);
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(double.NaN, 0, 0),
            out _,
            out _,
            out string? nonfinite));
        Assert.Contains("finite", nonfinite, StringComparison.OrdinalIgnoreCase);
        AcceptPoint(authoring, CadPoint3D.Zero);
        Assert.False(authoring.TryAcceptPoint(
            CadPoint3D.Zero,
            out _,
            out _,
            out string? duplicate));
        Assert.Contains("nonzero", duplicate, StringComparison.OrdinalIgnoreCase);
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(2, 0, 1),
            out _,
            out _,
            out string? offPlane));
        Assert.Contains("plane", offPlane, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-12.5", -12.5)]
    [InlineData(" +4.25e2 ", 425.0)]
    public void ScalarParserIsBoundedInvariantAndSigned(
        string text,
        double expected)
    {
        Assert.True(CadEllipseScalarInput.TryParse(text, out var input));
        Assert.Equal(expected, input.Value);
        Assert.False(CadEllipseScalarInput.TryParse("NaN", out _));
        Assert.False(CadEllipseScalarInput.TryParse("1,5", out _));
        Assert.False(CadEllipseScalarInput.TryParse(
            new string('1', CadEllipseScalarInput.MaximumCodeUnits + 1),
            out _));
    }

    [Fact]
    public void CommandCapturesPropertiesAndRedoRetainsSameEllipse()
    {
        var document = new CadDocument();
        var layer = new Layer("ELLIPSES");
        document.Layers.Add(layer);
        document.Header.CurrentLayerName = layer.Name;
        document.Header.CurrentEntityColor = ACadSharp.Color.Cyan;
        document.Header.CurrentLineTypeName = LineType.ContinuousName;
        document.Header.CurrentEntityLinetypeScale = 2.5;
        document.Header.CurrentEntityLineWeight = LineWeightType.W35;
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddEllipseCommand(
            new CadEllipseAuthoringSnapshot(
                new CadPoint3D(4, 5, 6),
                new CadPoint3D(7, 0, 0),
                0.25,
                0.5,
                1.5));

        history.Execute(command);

        Ellipse ellipse = Assert.IsType<Ellipse>(Assert.Single(document.Entities));
        Assert.Same(ellipse, command.Ellipse);
        Assert.Same(layer, ellipse.Layer);
        Assert.Equal(ACadSharp.Color.Cyan, ellipse.Color);
        Assert.Equal(LineType.ContinuousName, ellipse.LineType.Name);
        Assert.Equal(2.5, ellipse.LineTypeScale);
        Assert.Equal(LineWeightType.W35, ellipse.LineWeight);
        Assert.Equal(new XYZ(4, 5, 6), ellipse.Center);
        Assert.Equal(new XYZ(7, 0, 0), ellipse.MajorAxisEndPoint);
        AssertClose(0.25, ellipse.RadiusRatio);
        AssertClose(0.5, ellipse.StartParameter);
        AssertClose(2.0, ellipse.EndParameter);
        Assert.Equal(XYZ.AxisZ, ellipse.Normal);
        Assert.Equal(0.0, ellipse.Thickness);
        Assert.NotEqual(0UL, command.CurrentHandle);

        Assert.True(history.TryUndo(out _));
        Assert.Empty(document.Entities);
        Assert.Equal(0UL, command.CurrentHandle);
        document.Header.CurrentEntityColor = ACadSharp.Color.Red;
        Assert.True(history.TryRedo(out _));
        Assert.Same(ellipse, Assert.Single(document.Entities));
        Assert.Equal(ACadSharp.Color.Cyan, ellipse.Color);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("celtscale")]
    [InlineData("thickness")]
    public void CommandRejectsUnsupportedCurrentStateBeforeMutation(string failure)
    {
        var document = new CadDocument();
        switch (failure)
        {
            case "locked":
                document.Header.CurrentLayer.Flags |= LayerFlags.Locked;
                break;
            case "celtscale":
                document.Header.CurrentEntityLinetypeScale = 0.0;
                break;
            case "thickness":
                document.Header.ThicknessDefault = 2.0;
                break;
        }
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddEllipseCommand(
            new CadEllipseAuthoringSnapshot(
                CadPoint3D.Zero,
                new CadPoint3D(4, 0, 0),
                0.5));

        Exception exception = Assert.ThrowsAny<Exception>(() =>
            history.Execute(command));

        if (failure == "thickness")
        {
            Assert.Equal("CadUnsupportedEntityException", exception.GetType().Name);
        }
        Assert.Empty(document.Entities);
        Assert.Equal(0, history.UndoCount);
        Assert.Null(command.Ellipse);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AuthoredEllipticalArcRoundTripsThroughCadStore(
        CadDocumentFormat format)
    {
        var session = new CadDocumentSession(new CadDocument());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddEllipseCommand(
            new CadEllipseAuthoringSnapshot(
                new CadPoint3D(-2, 3, 4),
                new CadPoint3D(9.5, 0, 0),
                0.4,
                0.25,
                4.5)));
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            session,
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"ellipse-authoring.{format.ToString().ToLowerInvariant()}");

        Ellipse ellipse = Assert.Single(loaded.Session.Read(document =>
            document.Entities.OfType<Ellipse>().ToArray()));
        Assert.Equal(new XYZ(-2, 3, 4), ellipse.Center);
        Assert.Equal(new XYZ(9.5, 0, 0), ellipse.MajorAxisEndPoint);
        AssertClose(0.4, ellipse.RadiusRatio);
        AssertClose(0.25, ellipse.StartParameter);
        AssertClose(4.75, ellipse.EndParameter);
        Assert.Equal(XYZ.AxisZ, ellipse.Normal);
        Assert.Equal(0.0, ellipse.Thickness);
    }

    [Fact]
    public void AuthoredEllipseRetainsManagedAndNativeAnalyticReplay()
    {
        var authoring = new CadEllipseAuthoringSession(
            CadEllipseAuthoringMode.IsocircleRadius,
            isometricSnapSettings:
                CadPlanGridSnapSettings.CreateIsometric(
                    false,
                    CadPoint3D.Zero,
                    1.0,
                    CadPlanIsoplane.Top));
        AcceptPoint(authoring, new CadPoint3D(10, 20, 0));
        CadEllipseAuthoringSnapshot isocircle =
            CompleteScalar(authoring, 8.0);
        var session = new CadDocumentSession(new CadDocument());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddEllipseCommand(isocircle));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadEllipsePrimitive primitive = Assert.Single(snapshot.Ellipses.ToArray());
        AssertPoint(
            new CadPoint3D(8.0 * Math.Sqrt(1.5), 0.0, 0.0),
            primitive.MajorAxis);
        AssertPoint(
            new CadPoint3D(0.0, 8.0 / Math.Sqrt(2.0), 0.0),
            primitive.MinorAxis);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawEllipse, command.Type);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            93U,
            scene.ContentGeneration,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.True(compiled.GeometryPrimitiveCount > 0);
    }

    private static CadEllipseAuthoringSnapshot CompleteStandard(
        CadEllipseAuthoringSession authoring,
        CadEllipseArcInputMode arcInputMode)
    {
        bool centerMode = authoring.Mode is
            CadEllipseAuthoringMode.CenterDistance or
            CadEllipseAuthoringMode.CenterRotation;
        AcceptPoint(authoring, centerMode
            ? new CadPoint3D(0, 0, 5)
            : new CadPoint3D(-4, 0, 5));
        AcceptPoint(authoring, new CadPoint3D(4, 0, 5));

        bool distanceMode = authoring.Mode is
            CadEllipseAuthoringMode.AxisEndpointsDistance or
            CadEllipseAuthoringMode.CenterDistance;
        if (distanceMode)
        {
            if (arcInputMode == CadEllipseArcInputMode.Full)
            {
                return CompletePoint(authoring, new CadPoint3D(0, 2, 5));
            }
            AcceptPoint(authoring, new CadPoint3D(0, 2, 5));
        }
        else
        {
            if (arcInputMode == CadEllipseArcInputMode.Full)
            {
                return CompleteScalar(authoring, Math.PI / 3.0);
            }
            AcceptScalar(authoring, Math.PI / 3.0);
        }

        AcceptScalar(authoring, 0.0);
        return CompleteScalar(authoring, Math.PI / 2.0);
    }

    private static void AcceptPoint(
        CadEllipseAuthoringSession authoring,
        CadPoint3D point)
    {
        Assert.True(authoring.TryAcceptPoint(
            point,
            out _,
            out bool completed,
            out string? error),
            error);
        Assert.False(completed);
    }

    private static CadEllipseAuthoringSnapshot CompletePoint(
        CadEllipseAuthoringSession authoring,
        CadPoint3D point)
    {
        Assert.True(authoring.TryAcceptPoint(
            point,
            out CadEllipseAuthoringSnapshot snapshot,
            out bool completed,
            out string? error),
            error);
        Assert.True(completed);
        return snapshot;
    }

    private static void AcceptScalar(
        CadEllipseAuthoringSession authoring,
        double value)
    {
        Assert.True(authoring.TryAcceptScalar(
            value,
            out _,
            out bool completed,
            out string? error),
            error);
        Assert.False(completed);
    }

    private static CadEllipseAuthoringSnapshot CompleteScalar(
        CadEllipseAuthoringSession authoring,
        double value)
    {
        Assert.True(authoring.TryAcceptScalar(
            value,
            out CadEllipseAuthoringSnapshot snapshot,
            out bool completed,
            out string? error),
            error);
        Assert.True(completed);
        return snapshot;
    }

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        AssertClose(expected.X, actual.X);
        AssertClose(expected.Y, actual.Y);
        AssertClose(expected.Z, actual.Z);
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);
}
