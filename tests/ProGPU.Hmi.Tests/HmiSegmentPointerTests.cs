using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.Scene;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiSegmentPointerTests
{
    private static HmiDesignerHost Host()
    {
        InputSystem.Current = new WindowInputState();
        var host = new HmiDesignerHost(HmiDiagramModelTests.Project());
        host.Measure(new Vector2(1600, 1000)); host.Arrange(new Rect(0, 0, 1600, 1000));
        host.WorkspaceCanvas.ZoomScale = 1; host.WorkspaceCanvas.PanOffset = Vector2.Zero; host.WorkspaceCanvas.ApplyTransforms();
        host.SelectDiagramLink("ab"); InputSystem.Current.Root = host;
        return host;
    }
    private static HmiPoint Grip(HmiDesignerHost host) => host.GetSelectedRouteSegments().Where(s => s.IsHorizontal)
        .OrderByDescending(s => Math.Abs(s.End.X - s.Start.X)).First().Midpoint;

    [Fact]
    public void ForeignPointerCannotCompleteTheOwningSegmentGesture()
    {
        using var host = Host(); string before = host.Session.ExportJson(); var grip = Grip(host);
        Send(host, PointerInputKind.Pressed, 71, grip, true);
        Assert.True(host.IsDraggingRouteSegment);
        Send(host, PointerInputKind.Moved, 71, grip with { Y = grip.Y - 60 }, true);
        Send(host, PointerInputKind.Released, 72, grip with { Y = grip.Y - 90 }, false);
        Assert.True(host.IsDraggingRouteSegment); Assert.Equal(before, host.Session.ExportJson());
        Send(host, PointerInputKind.Released, 71, grip with { Y = grip.Y - 60 }, false);
        Assert.False(host.IsDraggingRouteSegment); Assert.Single(host.Session.GetProject().Screens[0].Links[0].StraightSegments);
    }

    [Theory]
    [InlineData("placement")] [InlineData("segment")] [InlineData("waypoint")]
    public void CaptureReleaseObserversCannotCommitAnObsoleteGesture(string replacement)
    {
        using var host = Host(); string original = host.Session.ExportJson(); var grip = Grip(host);
        Send(host, PointerInputKind.Pressed, 71, grip, true);
        Send(host, PointerInputKind.Moved, 71, grip with { Y = grip.Y - 60 }, true);
        bool replaced = false;
        host.WorkspaceCanvas.PointerCaptureLost += (_, _) =>
        {
            if (replaced) return; replaced = true;
            if (replacement == "placement") { host.BeginComponentPlacement(HmiSymbol.Gauge); host.BeginPlacementDrag(new(750, 400)); }
            else if (replacement == "segment") host.BeginSegmentDrag(host.GetSelectedRouteSegments().First(s => s.IsHorizontal).Index);
            else host.BeginWaypointPlacement();
        };
        Send(host, PointerInputKind.Released, 71, grip with { Y = grip.Y - 60 }, false);
        Assert.True(replaced); Assert.Equal(original, host.Session.ExportJson()); Assert.False(host.Session.CanUndo);
        Assert.True(host.IsPlacingComponent || host.IsEditingRoute);
    }

    [Theory]
    [InlineData(PointerInputKind.Canceled)] [InlineData(PointerInputKind.Released)]
    public void NativeCancellationDoesNotPersistAnyPins(PointerInputKind kind)
    {
        using var host = Host(); string original = host.Session.ExportJson(); var grip = Grip(host);
        Send(host, PointerInputKind.Pressed, 71, grip, true);
        Send(host, PointerInputKind.Moved, 71, grip with { Y = grip.Y - 60 }, true);
        if (kind == PointerInputKind.Released) InputSystem.ReleasePointerCaptures(host.WorkspaceCanvas);
        else Send(host, kind, 71, grip, false);
        Assert.False(host.IsEditingRoute); Assert.Equal(original, host.Session.ExportJson());
    }

    private static void Send(HmiDesignerHost host, PointerInputKind kind, uint id, HmiPoint point, bool down)
    {
        var p = host.WorkspaceCanvas.DesignSurface.TransformToVisual(host).TransformPoint(new(point.X, point.Y));
        InputSystem.InjectPointer(new PointerInputEvent(kind, id, Windows.Devices.Input.PointerDeviceType.Touch, p,
            Timestamp: 1, IsInContact: down, IsLeftButtonPressed: down));
    }
}
