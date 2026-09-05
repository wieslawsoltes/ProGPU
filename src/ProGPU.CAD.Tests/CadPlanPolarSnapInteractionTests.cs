using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Microsoft.UI.Xaml;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadPlanPolarSnapInteractionTests
{
    [Fact]
    public void PolarSnapReevaluatesPendingPointerAndCommitsQuantizedMove()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(-2.0, 0.0, 0.0), new XYZ(2.0, 0.0, 0.0));
        document.Entities.Add(line);
        document.VPorts[VPort.DefaultName].SnapSpacing = new XY(4.0, 4.0);
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport viewport = canvas.CurrentViewport;
            Click(canvas, viewport.WorldToScreen(CadPoint3D.Zero));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            canvas.PlanSnapType = CadPlanSnapType.Polar;
            canvas.PlanPolarSnapDistance = 4.0;
            Assert.True(canvas.BeginSelectionPointTransform(
                CadPointTransformOperation.Move));
            Assert.True(canvas.TryAcceptSelectionPointTransformInput(
                "0,0",
                out _));
            Vector2 pointer = viewport.WorldToScreen(
                new CadPoint3D(10.4, 0.0, 0.0));
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = pointer,
            });

            Assert.Null(canvas.PendingPointTransformPolarTracking);
            Assert.Null(canvas.PendingPointTransformGridSnap);

            canvas.IsPlanPolarTrackingEnabled = true;

            CadPlanPolarTrackingResult unsnapped =
                canvas.PendingPointTransformPolarTracking!.Value;
            Assert.False(unsnapped.IsDistanceSnapped);
            Assert.Equal(10.4, unsnapped.Distance, 5);

            canvas.SetPlanSnapMode(true);

            CadPlanPolarTrackingResult snapped =
                canvas.PendingPointTransformPolarTracking!.Value;
            Assert.True(snapped.IsDistanceSnapped);
            Assert.Equal(4.0, snapped.SnapIncrement);
            Assert.Equal(12.0, snapped.Distance, 10);
            AssertPoint(new CadPoint3D(12.0, 0.0, 0.0), snapped.Point);
            Assert.True(document.VPorts[VPort.DefaultName].SnapOn);
            Assert.Equal(1UL, session.ContentGeneration);
            Click(canvas, pointer);

            Assert.Null(canvas.PendingPointTransformOperation);
            Assert.Equal(2UL, session.ContentGeneration);
            AssertPoint(new XYZ(10.0, 0.0, 0.0), line.StartPoint);
            AssertPoint(new XYZ(14.0, 0.0, 0.0), line.EndPoint);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void DirectDistanceRemainsExactWhilePolarSnapIsEnabled()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(-2.0, 0.0, 0.0), new XYZ(2.0, 0.0, 0.0));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport viewport = canvas.CurrentViewport;
            Click(canvas, viewport.WorldToScreen(CadPoint3D.Zero));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            canvas.IsPlanPolarTrackingEnabled = true;
            canvas.PlanPolarSnapDistance = 4.0;
            canvas.IsPlanPolarSnapEnabled = true;
            Assert.True(canvas.BeginSelectionPointTransform(
                CadPointTransformOperation.Move));
            Assert.True(canvas.TryAcceptSelectionPointTransformInput(
                "0,0",
                out _));
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = viewport.WorldToScreen(
                    new CadPoint3D(10.4, 0.0, 0.0)),
            });
            Assert.Equal(
                12.0,
                canvas.PendingPointTransformPolarTracking!.Value.Distance,
                10);

            Assert.True(canvas.TryAcceptSelectionPointTransformInput(
                "10",
                out string? error));

            Assert.Null(error);
            Assert.Equal(1UL, session.ContentGeneration);
            AssertPoint(new XYZ(8.0, 0.0, 0.0), line.StartPoint);
            AssertPoint(new XYZ(12.0, 0.0, 0.0), line.EndPoint);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void ObjectSnapPrecedesPolarDistanceQuantization()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(-2.0, 0.0, 0.0), new XYZ(2.0, 0.0, 0.0));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport viewport = canvas.CurrentViewport;
            Click(canvas, viewport.WorldToScreen(CadPoint3D.Zero));
            canvas.ObjectSnapModes = CadObjectSnapModes.Endpoint;
            canvas.IsPlanPolarTrackingEnabled = true;
            canvas.PlanPolarSnapDistance = 4.0;
            canvas.IsPlanPolarSnapEnabled = true;
            Assert.True(canvas.BeginSelectionPointTransform(
                CadPointTransformOperation.Move));
            Assert.True(canvas.TryAcceptSelectionPointTransformInput(
                "0,0",
                out _));
            Vector2 endpoint = viewport.WorldToScreen(
                new CadPoint3D(2.0, 0.0, 0.0));
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = endpoint,
            });

            Assert.Equal(
                new CadPoint3D(2.0, 0.0, 0.0),
                canvas.PendingPointTransformObjectSnap!.Value.Point);
            Assert.Null(canvas.PendingPointTransformPolarTracking);
            Click(canvas, endpoint);

            Assert.Equal(1UL, session.ContentGeneration);
            AssertPoint(new XYZ(0.0, 0.0, 0.0), line.StartPoint);
            AssertPoint(new XYZ(4.0, 0.0, 0.0), line.EndPoint);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedControlsAndF9RetainSnapTypeWithoutDrawingEdits()
    {
        var document = new CadDocument();
        VPort active = document.VPorts[VPort.DefaultName];
        active.SnapOn = true;
        active.SnapSpacing = new XY(2.0, 3.0);
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);

            Assert.True(view.Canvas.IsPlanGridSnapEnabled);
            Assert.Equal(CadPlanSnapType.Grid, view.Canvas.PlanSnapType);
            view.PlanPolarSnapDistanceInput.Text = "4";
            view.PlanPolarSnapCheckBox.IsChecked = true;

            Assert.True(view.Canvas.IsPlanSnapEnabled);
            Assert.True(view.Canvas.IsPlanPolarSnapEnabled);
            Assert.False(view.Canvas.IsPlanGridSnapEnabled);
            Assert.False(view.PlanGridSnapCheckBox.IsChecked);
            Assert.Equal(CadPlanSnapType.Polar, view.Canvas.PlanSnapType);
            Assert.Equal(4.0, view.Canvas.PlanPolarSnapDistance);
            Assert.Equal(0UL, session.ContentGeneration);

            view.PlanGridMajorInput.Text = "19";
            var blockedStagedF9 = new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.F9,
            };
            view.OnKeyDown(blockedStagedF9);

            Assert.True(blockedStagedF9.Handled);
            Assert.True(view.Canvas.IsPlanPolarSnapEnabled);
            Assert.True(document.VPorts[VPort.DefaultName].SnapOn);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Equal("19", view.PlanGridMajorInput.Text);
            view.PlanGridMajorInput.Text = "5";

            var f9Off = new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.F9,
            };
            view.OnKeyDown(f9Off);

            Assert.True(f9Off.Handled);
            Assert.False(view.Canvas.IsPlanSnapEnabled);
            Assert.False(view.PlanPolarSnapCheckBox.IsChecked);
            Assert.Equal(CadPlanSnapType.Polar, view.Canvas.PlanSnapType);
            Assert.False(document.VPorts[VPort.DefaultName].SnapOn);
            Assert.Equal(1UL, session.ContentGeneration);

            view.PlanPolarSnapDistanceInput.Text = "invalid";
            var blockedF9 = new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.F9,
            };
            view.OnKeyDown(blockedF9);

            Assert.True(blockedF9.Handled);
            Assert.False(view.Canvas.IsPlanSnapEnabled);
            Assert.Equal(CadPlanSnapType.Polar, view.Canvas.PlanSnapType);
            Assert.Equal(1UL, session.ContentGeneration);

            view.PlanPolarSnapDistanceInput.Text = "0";
            var f9On = new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.F9,
            };
            view.OnKeyDown(f9On);

            Assert.True(f9On.Handled);
            Assert.True(view.Canvas.IsPlanPolarSnapEnabled);
            Assert.Equal(0.0, view.Canvas.PlanPolarSnapDistance);
            Assert.Equal(2.0, view.Canvas.PlanGridSnapSettings.SpacingX);
            Assert.True(document.VPorts[VPort.DefaultName].SnapOn);
            Assert.Equal(2UL, session.ContentGeneration);

            view.PlanGridSnapCheckBox.IsChecked = true;

            Assert.True(view.Canvas.IsPlanGridSnapEnabled);
            Assert.False(view.Canvas.IsPlanPolarSnapEnabled);
            Assert.False(view.PlanPolarSnapCheckBox.IsChecked);
            Assert.Equal(CadPlanSnapType.Grid, view.Canvas.PlanSnapType);
            Assert.Equal(2UL, session.ContentGeneration);

            Assert.True(view.Canvas.TryUndo());
            Assert.False(document.VPorts[VPort.DefaultName].SnapOn);
            Assert.False(view.Canvas.IsPlanSnapEnabled);
            Assert.False(view.PlanGridSnapCheckBox.IsChecked);
            Assert.Equal(CadPlanSnapType.Grid, view.Canvas.PlanSnapType);
            Assert.Equal(3UL, session.ContentGeneration);
            Assert.True(view.Canvas.TryRedo());
            Assert.True(document.VPorts[VPort.DefaultName].SnapOn);
            Assert.True(view.Canvas.IsPlanGridSnapEnabled);
            Assert.True(view.PlanGridSnapCheckBox.IsChecked);
            Assert.Equal(4UL, session.ContentGeneration);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    private static void Click(CadSampleCanvas canvas, Vector2 position)
    {
        canvas.OnPointerPressed(new PointerRoutedEventArgs
        {
            Position = position,
            IsLeftButtonPressed = true,
        });
        canvas.OnPointerReleased(new PointerRoutedEventArgs
        {
            Position = position,
        });
    }

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        Assert.Equal(expected.X, actual.X, 10);
        Assert.Equal(expected.Y, actual.Y, 10);
        Assert.Equal(expected.Z, actual.Z, 10);
    }

    private static void AssertPoint(XYZ expected, XYZ actual)
    {
        Assert.Equal(expected.X, actual.X, 10);
        Assert.Equal(expected.Y, actual.Y, 10);
        Assert.Equal(expected.Z, actual.Z, 10);
    }
}
