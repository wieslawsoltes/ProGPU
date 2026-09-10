using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadCircleAuthoringInteractionTests
{
    [Fact]
    public void TypedCenterRadiusCreatesOneTransactionalCircle()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginCircleAuthoring(
                CadCircleAuthoringMode.CenterRadius));

            Assert.True(canvas.TryAcceptCircleAuthoringInput("10,20,3", out _));
            Assert.True(canvas.IsCircleAuthoring);
            Assert.Equal(1, canvas.PendingCirclePointCount);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.True(canvas.TryAcceptCircleAuthoringInput("@5,0,0", out _));

            Assert.False(canvas.IsCircleAuthoring);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(1, canvas.UndoCount);
            Circle circle = Assert.Single(document.Entities.OfType<Circle>());
            Assert.Equal(new XYZ(10, 20, 3), circle.Center);
            Assert.Equal(5.0, circle.Radius);
            Assert.True(canvas.TryUndo());
            Assert.Empty(document.Entities);
            Assert.True(canvas.TryRedo());
            Assert.Same(circle, Assert.Single(document.Entities));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void CenterRadiusScalarDoesNotRequirePointerDirection()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginCircleAuthoring(
                CadCircleAuthoringMode.CenterRadius));
            Assert.True(canvas.TryAcceptCircleAuthoringInput("1,2,3", out _));

            Assert.True(canvas.CanAcceptCircleAuthoringInput("6"));
            Assert.True(canvas.TryAcceptCircleAuthoringInput("6", out _));

            Circle circle = Assert.Single(document.Entities.OfType<Circle>());
            Assert.Equal(new XYZ(1, 2, 3), circle.Center);
            Assert.Equal(6.0, circle.Radius);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void TwoPointDirectDistanceAndPointerStayOnFirstPointElevation()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginCircleAuthoring(
                CadCircleAuthoringMode.TwoPoint));
            Assert.True(canvas.TryAcceptCircleAuthoringInput("0,0,7", out _));
            Vector2 pointer = canvas.CurrentViewport.WorldToScreen(
                new CadPoint3D(8, 0, 7));
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = pointer,
            });

            Assert.True(canvas.TryAcceptCircleAuthoringInput("5", out _));

            Circle circle = Assert.Single(document.Entities.OfType<Circle>());
            Assert.Equal(new XYZ(2.5, 0, 7), circle.Center);
            Assert.Equal(2.5, circle.Radius, 12);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void CollinearThreePointFailureRetainsPromptForCorrectedFinalPoint()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginCircleAuthoring(
                CadCircleAuthoringMode.ThreePoint));
            Assert.True(canvas.TryAcceptCircleAuthoringInput("0,0,4", out _));
            Assert.True(canvas.TryAcceptCircleAuthoringInput("10,0,4", out _));

            Assert.False(canvas.TryAcceptCircleAuthoringInput(
                "20,0,4",
                out string? error));

            Assert.Contains("non-collinear", error, StringComparison.OrdinalIgnoreCase);
            Assert.True(canvas.IsCircleAuthoring);
            Assert.Equal(2, canvas.PendingCirclePointCount);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.True(canvas.TryAcceptCircleAuthoringInput("5,5,4", out _));
            Circle circle = Assert.Single(document.Entities.OfType<Circle>());
            Assert.Equal(new XYZ(5, 0, 4), circle.Center);
            Assert.Equal(5.0, circle.Radius, 12);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void NonzeroThicknessFailsWithoutPublishingAndKeepsPromptRecoverable()
    {
        var document = new CadDocument();
        document.Header.ThicknessDefault = 2.0;
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginCircleAuthoring(
                CadCircleAuthoringMode.CenterRadius));
            Assert.True(canvas.TryAcceptCircleAuthoringInput("0,0", out _));

            Assert.False(canvas.TryAcceptCircleAuthoringInput(
                "4,0",
                out string? error));

            Assert.Contains("THICKNESS", error, StringComparison.Ordinal);
            Assert.True(canvas.IsCircleAuthoring);
            Assert.Equal(1, canvas.PendingCirclePointCount);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(document.Entities);
            Assert.Equal(0, canvas.UndoCount);
            document.Header.ThicknessDefault = 0.0;
            Assert.True(canvas.TryAcceptCircleAuthoringInput("4,0", out _));
            Assert.Single(document.Entities.OfType<Circle>());
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedButtonsExposeAllModesAndEscapeCancels()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));

            PressEnter(view.CircleButton);
            Assert.Equal(
                CadCircleAuthoringMode.CenterRadius,
                view.Canvas.PendingCircleAuthoringMode);
            Accept(view.Canvas, "0,0", "4,0");

            PressEnter(view.CircleDiameterButton);
            Assert.Equal(
                CadCircleAuthoringMode.CenterDiameter,
                view.Canvas.PendingCircleAuthoringMode);
            Accept(view.Canvas, "10,0", "14,0");

            PressEnter(view.CircleTwoPointButton);
            Assert.Equal(
                CadCircleAuthoringMode.TwoPoint,
                view.Canvas.PendingCircleAuthoringMode);
            Accept(view.Canvas, "20,0", "24,0");

            PressEnter(view.CircleThreePointButton);
            Assert.Equal(
                CadCircleAuthoringMode.ThreePoint,
                view.Canvas.PendingCircleAuthoringMode);
            Accept(view.Canvas, "34,0", "32,2", "30,0");

            Assert.Equal(4, document.Entities.OfType<Circle>().Count());
            PressEnter(view.CircleButton);
            Assert.True(view.Canvas.TryAcceptCircleAuthoringInput("40,0", out _));
            var escape = new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.Escape };
            view.OnKeyDown(escape);
            Assert.True(escape.Handled);
            Assert.False(view.Canvas.IsCircleAuthoring);
            Assert.Equal(4, document.Entities.OfType<Circle>().Count());
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    private static void Accept(CadSampleCanvas canvas, params string[] points)
    {
        foreach (string point in points)
        {
            Assert.True(canvas.TryAcceptCircleAuthoringInput(point, out _));
        }
    }

    private static void PressEnter(Microsoft.UI.Xaml.Controls.Button button) =>
        button.OnKeyDown(new KeyRoutedEventArgs
        {
            Key = Silk.NET.Input.Key.Enter,
        });
}
