using System.Numerics;
using ACadSharp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadPointAuthoringInteractionTests
{
    [Fact]
    public void TypedAbsolutePointCommitsImmediatelyAsOneEdit()
    {
        var document = new CadDocument();
        document.Header.PointDisplayMode = 98;
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginPointAuthoring());
            Assert.Equal(0UL, session.ContentGeneration);

            Assert.True(canvas.TryAcceptPointAuthoringInput(
                "1.5,-2,3",
                out string? error),
                error);

            Assert.False(canvas.IsPointAuthoring);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(1, canvas.UndoCount);
            ACadSharp.Entities.Point point = Assert.IsType<ACadSharp.Entities.Point>(
                Assert.Single(document.Entities));
            Assert.Equal(new CSMath.XYZ(1.5, -2, 3), point.Location);
            Assert.Single(canvas.CurrentSnapshot!.Points.ToArray());
            Assert.True(canvas.TryUndo());
            Assert.Empty(document.Entities);
            Assert.True(canvas.TryRedo());
            Assert.Same(point, Assert.Single(document.Entities));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void RelativeAndDirectDistanceInputAreRejectedWithoutACommandBase()
    {
        var session = new CadDocumentSession(new CadDocument());
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginPointAuthoring());

            Assert.False(canvas.CanAcceptPointAuthoringInput("@1,2"));
            Assert.False(canvas.TryAcceptPointAuthoringInput("@1,2", out _));
            Assert.False(canvas.TryAcceptPointAuthoringInput("5", out _));
            Assert.True(canvas.IsPointAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(session.Read(document => document.Entities.ToArray()));
            Assert.True(canvas.CancelPointAuthoring());
            Assert.Equal(0UL, session.ContentGeneration);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void PointerUsesNodeObjectSnapFromAnAuthoredPoint()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginPointAuthoring());
            Assert.True(canvas.TryAcceptPointAuthoringInput("4,3", out _));

            canvas.ObjectSnapModes = CadObjectSnapModes.Node;
            Assert.True(canvas.BeginPointAuthoring());
            Vector2 node = canvas.CurrentViewport.WorldToScreen(
                new CadPoint3D(4, 3, 0));
            Vector2 nearNode = node + new Vector2(2, -2);
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = nearNode,
            });

            CadObjectSnapResult snap =
                canvas.PendingPointTransformObjectSnap!.Value;
            Assert.Equal(CadObjectSnapKind.Node, snap.Kind);
            Assert.Equal(new CadPoint3D(4, 3, 0), snap.Point);
            Click(canvas, nearNode);

            Assert.False(canvas.IsPointAuthoring);
            Assert.Equal(2, document.Entities.OfType<ACadSharp.Entities.Point>().Count());
            Assert.All(
                document.Entities.OfType<ACadSharp.Entities.Point>(),
                point => Assert.Equal(new CSMath.XYZ(4, 3, 0), point.Location));
            Assert.Equal(2UL, session.ContentGeneration);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedPointButtonAndEscapeExposeSinglePointLifecycle()
    {
        var session = new CadDocumentSession(new CadDocument());
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(view.PointButton.IsEnabled);
            PressEnter(view.PointButton);
            Assert.True(view.Canvas.IsPointAuthoring);

            var escape = new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Escape,
            };
            view.OnKeyDown(escape);

            Assert.True(escape.Handled);
            Assert.False(view.Canvas.IsPointAuthoring);
            Assert.Empty(session.Read(document => document.Entities.ToArray()));
            Assert.Equal(0UL, session.ContentGeneration);

            PressEnter(view.PointButton);
            view.PointTransformInput.Text = "8,9";
            view.PointTransformInput.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Enter,
            });
            Assert.False(view.Canvas.IsPointAuthoring);
            Assert.Single(session.Read(document =>
                document.Entities.OfType<ACadSharp.Entities.Point>().ToArray()));
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

    private static void PressEnter(Microsoft.UI.Xaml.Controls.Button button) =>
        button.OnKeyDown(new KeyRoutedEventArgs
        {
            Key = Silk.NET.Input.Key.Enter,
        });
}
