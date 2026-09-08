using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadFitSplineTests
{
    [Fact]
    public void EndpointDerivativesProduceExactCubicWithoutMutatingFitData()
    {
        Spline source = CreateFitSpline();
        XYZ[] fit = source.FitPoints.ToArray();
        CadDocumentSnapshot snapshot = Compile(source);
        Assert.Single(snapshot.Splines.ToArray());
        Assert.Equal(new[] { new CadPoint3D(0, 0, 0), new CadPoint3D(3, 6, 0),
            new CadPoint3D(9, -3, 0), new CadPoint3D(12, 3, 0) },
            snapshot.SplineControlPoints.ToArray());
        Assert.Equal(new double[] { 0, 0, 0, 0, 1, 1, 1, 1 }, snapshot.SplineKnots.ToArray());
        Assert.Empty(source.ControlPoints);
        Assert.Empty(source.Knots);
        Assert.Equal(fit, source.FitPoints);
        Assert.Equal(new XYZ(9, 18, 0), source.StartTangent);
        Assert.Equal(new XYZ(9, 18, 0), source.EndTangent);

        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        var candidate = new CadSelectionCandidate(snapshot.ContentGeneration, 0,
            entity.Handle, entity.Kind, entity.Bounds);
        // Independent cubic Bernstein evaluation at t=1/2: (P0+3P1+3P2+P3)/8.
        Assert.Equal(CadPointHitStatus.Hit, CadSelectionHitTester.HitTestPoint(
            snapshot, candidate, new CadPoint3D(6, 1.5, 0), 1e-8).Status);
        Assert.Equal(CadPointHitStatus.Miss, CadSelectionHitTester.HitTestPoint(
            snapshot, candidate, new CadPoint3D(6, 4, 0), 1e-3).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FitAndControlRepresentationsMatchManagedNativeAndPrint(bool inBlock)
    {
        CadDocumentSnapshot fit = Compile(CreateFitSpline(), inBlock);
        CadDocumentSnapshot controls = Compile(CreateControlSpline(), inBlock);
        Assert.Equal(controls.SplineControlPoints.ToArray(), fit.SplineControlPoints.ToArray());
        Assert.Equal(controls.SplineKnots.ToArray(), fit.SplineKnots.ToArray());
        using var fitScene = new CadPlanSceneCompiler().Compile(fit);
        using var controlScene = new CadPlanSceneCompiler().Compile(controls);
        using GpuPicture fitPicture = fitScene.CreatePicture();
        using GpuPicture controlPicture = controlScene.CreatePicture();
        Assert.Equal(controlPicture.PointBuffer.ToArray(), fitPicture.PointBuffer.ToArray());
        Assert.Equal(controlPicture.DoubleBuffer.ToArray(), fitPicture.DoubleBuffer.ToArray());
        Assert.Equal(1, fitPicture.CommandCount);
        Assert.Equal(NativeStream(controlPicture), NativeStream(fitPicture));
        using var fitPrint = new CadPrintPlanCompiler().Compile(fit);
        using var controlPrint = new CadPrintPlanCompiler().Compile(controls);
        using GpuPicture fitPage = fitPrint.CreatePagePicture();
        using GpuPicture controlPage = controlPrint.CreatePagePicture();
        Assert.Equal(NativeStream(controlPage), NativeStream(fitPage));
    }

    [Theory]
    [InlineData("missing-start")]
    [InlineData("missing-end")]
    [InlineData("chord")]
    [InlineData("centripetal")]
    [InlineData("custom")]
    [InlineData("multi-point")]
    [InlineData("closed")]
    [InlineData("periodic")]
    [InlineData("quadratic")]
    public void OtherFitSystemsAreExplicitlyUnsupported(string variant)
    {
        Spline source = CreateFitSpline();
        switch (variant)
        {
            case "missing-start": source.StartTangent = XYZ.Zero; break;
            case "missing-end": source.EndTangent = XYZ.Zero; break;
            case "chord": source.KnotParametrization = KnotParametrization.Chord; break;
            case "centripetal": source.KnotParametrization = KnotParametrization.SquareRoot; break;
            case "custom": source.KnotParametrization = KnotParametrization.Custom; break;
            case "multi-point": source.FitPoints.Add(new XYZ(20, 0, 0)); break;
            case "closed": source.IsClosed = true; break;
            case "periodic": source.Flags1 = SplineFlags1.None; source.IsPeriodic = true; break;
            case "quadratic": source.Degree = 2; break;
        }
        CadDocumentSnapshot snapshot = Compile(source);
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
        Assert.Empty(snapshot.Splines.ToArray());
        Assert.Empty(snapshot.SplineControlPoints.ToArray());
        Assert.Empty(snapshot.SplineKnots.ToArray());
    }

    [Theory]
    [InlineData("point")]
    [InlineData("tangent")]
    [InlineData("tolerance")]
    [InlineData("weights")]
    [InlineData("degree")]
    [InlineData("overflow")]
    public void InvalidFitDataIsRejectedWithoutPartialGeometry(string variant)
    {
        Spline source = CreateFitSpline();
        switch (variant)
        {
            case "point": source.FitPoints[0] = new XYZ(double.NaN, 0, 0); break;
            case "tangent": source.EndTangent = new XYZ(0, double.PositiveInfinity, 0); break;
            case "tolerance": source.FitTolerance = -1; break;
            case "weights": source.Weights.Add(1); break;
            case "degree": source.Degree = 0; break;
            case "overflow":
                source.FitPoints[0] = new XYZ(double.MaxValue, 0, 0);
                source.StartTangent = new XYZ(double.MaxValue, 0, 0);
                break;
        }
        CadDocumentSnapshot snapshot = Compile(source);
        Assert.Equal(1, snapshot.Statistics.InvalidEntityCount);
        Assert.Empty(snapshot.Splines.ToArray());
        Assert.Empty(snapshot.SplineControlPoints.ToArray());
    }

    [Fact]
    public void ExplicitControlsRemainAuthoritativeWhenFitMetadataExists()
    {
        Spline source = CreateControlSpline();
        source.FitPoints.AddRange([new XYZ(100, 100, 0), new XYZ(200, 200, 0)]);
        source.KnotParametrization = KnotParametrization.Custom;
        Assert.Equal(Compile(CreateControlSpline()).SplineControlPoints.ToArray(),
            Compile(source).SplineControlPoints.ToArray());
    }

    [Fact]
    public void MoveAndPivotedScalePreserveDerivativeSemanticsThroughUndoRedo()
    {
        var document = new CadDocument();
        Spline spline = CreateFitSpline();
        document.Entities.Add(spline);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var compiler = new CadSnapshotCompiler();
        CadPoint3D[] original = compiler.Compile(session).SplineControlPoints.ToArray();
        history.Execute(new CadTranslateEntitiesCommand([spline.Handle], new CadPoint3D(100, -30, 4)));
        CadPoint3D[] moved = compiler.Compile(session).SplineControlPoints.ToArray();
        for (int i = 0; i < original.Length; i++)
            Assert.Equal(original[i] + new CadPoint3D(100, -30, 4), moved[i]);
        Assert.Equal(new XYZ(9, 18, 0), spline.StartTangent);
        Assert.True(history.TryUndo(out _));
        Assert.Equal(original, compiler.Compile(session).SplineControlPoints.ToArray());
        Assert.True(history.TryRedo(out _));
        Assert.Equal(moved, compiler.Compile(session).SplineControlPoints.ToArray());
        history.Execute(new CadScaleEntitiesCommand([spline.Handle], 2, new CadPoint3D(100, -30, 4)));
        CadPoint3D[] scaled = compiler.Compile(session).SplineControlPoints.ToArray();
        for (int i = 0; i < original.Length; i++)
            Assert.Equal(original[i] * 2 + new CadPoint3D(100, -30, 4), scaled[i]);
        Assert.Equal(new XYZ(18, 36, 0), spline.EndTangent);
        Assert.True(history.TryUndo(out _));
        Assert.Equal(moved, compiler.Compile(session).SplineControlPoints.ToArray());
    }

    [Fact]
    public async Task DwgRoundTripPreservesFitGeometryAndMetadata()
    {
        var document = new CadDocument(ACadVersion.AC1032);
        Spline source = CreateFitSpline();
        document.Entities.Add(source);
        var session = new CadDocumentSession(document);
        CadDocumentSnapshot before = new CadSnapshotCompiler().Compile(session);
        using var output = new MemoryStream();
        var store = new CadDocumentStore();
        await store.SaveAsync(session, output, CadDocumentFormat.Dwg,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        output.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(output, CadDocumentFormat.Dwg);
        CadDocumentSnapshot after = new CadSnapshotCompiler().Compile(loaded.Session);
        Assert.Equal(before.SplineControlPoints.ToArray(), after.SplineControlPoints.ToArray());
        Assert.Equal(before.SplineKnots.ToArray(), after.SplineKnots.ToArray());
        loaded.Session.Read(value =>
        {
            Spline spline = Assert.IsType<Spline>(Assert.Single(value.Entities));
            Assert.Empty(spline.ControlPoints);
            Assert.Equal(source.FitPoints, spline.FitPoints);
            Assert.Equal(source.StartTangent, spline.StartTangent);
            Assert.Equal(source.EndTangent, spline.EndTangent);
            Assert.Equal(KnotParametrization.Uniform, spline.KnotParametrization);
            return true;
        });
    }

    [Fact]
    public async Task FitOnlyDxfWriteFailsBeforeTouchingDestinationOrSavedGeneration()
    {
        var session = CadDocumentSession.CreateNew();
        session.Edit("Add fit spline", document => document.Entities.Add(CreateFitSpline()));
        using var output = new MemoryStream();
        output.Write([1, 2, 3]);
        output.Position = 1;
        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await new CadDocumentStore().SaveAsync(session, output, CadDocumentFormat.Dxf,
                new CadSaveOptions { AllowUncertifiedWrite = true }));
        Assert.Contains("CADSAVE002", error.Message, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
        Assert.Equal(1, output.Position);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void DependencyDxfWriterCurrentlyLosesUniformFitParameterization()
    {
        var document = new CadDocument(ACadVersion.AC1032);
        document.Entities.Add(CreateFitSpline());
        using var output = new MemoryStream();
        DxfWriter.Write(output, document, false, new DxfWriterConfiguration { CloseStream = false });
        using var input = new MemoryStream(output.ToArray());
        CadDocument reopened = DxfReader.Read(input);
        Spline spline = Assert.IsType<Spline>(Assert.Single(reopened.Entities));
        Assert.Equal(2, spline.FitPoints.Count);
        Assert.Empty(spline.ControlPoints);
        Assert.Equal(KnotParametrization.Chord, spline.KnotParametrization);
        // Replace this characterization and CADSAVE002 together when the writer
        // can emit an exact control representation without mutating the source.
    }

    [Fact]
    public void PairedRepresentativeDwgFitSplineMatchesDxfControlGeometry()
    {
        string samples = Path.Combine(FindRepositoryRoot(), "external", "ACadSharp", "samples");
        CadDocument dxf = DxfReader.Read(Path.Combine(samples, "sample_AC1032_ascii.dxf"));
        CadDocument dwg = DwgReader.Read(Path.Combine(samples, "sample_AC1032.dwg"));
        Spline dxfSpline = Assert.Single(dxf.Entities.OfType<Spline>(), spline => spline.Handle == 0x434);
        Spline dwgSpline = Assert.Single(dwg.Entities.OfType<Spline>(), spline => spline.Handle == 0x434);
        Assert.Empty(dwgSpline.ControlPoints);
        Assert.Equal(2, dwgSpline.FitPoints.Count);
        CadPoint3D[] expected = Compile((Spline)dxfSpline.Clone()).SplineControlPoints.ToArray();
        CadPoint3D[] actual = Compile((Spline)dwgSpline.Clone()).SplineControlPoints.ToArray();
        Assert.Equal(4, actual.Length);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(expected[i].X, actual[i].X, 10);
            Assert.Equal(expected[i].Y, actual[i].Y, 10);
            Assert.Equal(expected[i].Z, actual[i].Z, 10);
        }
    }

    private static byte[] NativeStream(GpuPicture picture)
    {
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 97U, 1U,
            out NativeCompiledPicture? native, out NativePictureCompileFailure failure), failure.ToString());
        return native!.Stream.ToArray();
    }

    private static Spline CreateFitSpline()
    {
        var spline = new Spline
        {
            Degree = 3,
            KnotParametrization = KnotParametrization.Uniform,
            Flags1 = SplineFlags1.MethodFitPoints | SplineFlags1.UseKnotParameter,
            StartTangent = new XYZ(9, 18, 0), EndTangent = new XYZ(9, 18, 0),
        };
        spline.FitPoints.AddRange([XYZ.Zero, new XYZ(12, 3, 0)]);
        return spline;
    }

    private static Spline CreateControlSpline()
    {
        var spline = new Spline { Degree = 3 };
        spline.ControlPoints.AddRange([XYZ.Zero, new XYZ(3, 6, 0),
            new XYZ(9, -3, 0), new XYZ(12, 3, 0)]);
        spline.Knots.AddRange([0, 0, 0, 0, 1, 1, 1, 1]);
        return spline;
    }

    private static CadDocumentSnapshot Compile(Spline spline, bool inBlock = false)
    {
        var document = new CadDocument();
        if (inBlock)
        {
            var block = new BlockRecord("FitCurve");
            block.Entities.Add(spline);
            document.BlockRecords.Add(block);
            document.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(100, -30, 4), XScale = 2, YScale = 3, ZScale = 4,
                Rotation = 0.37,
            });
        }
        else
            document.Entities.Add(spline);
        return new CadSnapshotCompiler().Compile(new CadDocumentSession(document));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "ProGPU.CAD")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("ProGPU repository root was not found.");
    }
}
