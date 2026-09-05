using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Scene;
using ProGPU.Samples.Suntrail.Game;
using ProGPU.Samples.Suntrail.Presentation;
using Windows.Devices.Input;
using Xunit;

namespace ProGPU.Samples.Suntrail.Tests;

public sealed class WorkshopInputTests
{
    [Theory]
    [InlineData(PointerDeviceType.Mouse)]
    [InlineData(PointerDeviceType.Touch)]
    public void PaletteDropMoveUndoCancelAndPlaytestPreserveTheDraft(PointerDeviceType device)
    {
        var oldApp = Application.Current; var oldTheme = ThemeManager.CurrentTheme; var oldInput = InputSystem.Current;
        try
        {
            Application.Current = new App();
            var view = new GameView(); InputSystem.Current = InputSystem.CreateExternalState(view);
            void Layout() { view.Measure(new(1440, 900)); view.Arrange(new Rect(0, 0, 1440, 900)); }
            void Send(PointerInputKind kind, Vector2 position) => InputSystem.InjectPointer(new(kind, 91, device, position, 1_000_000,
                IsInContact: kind is PointerInputKind.Pressed or PointerInputKind.Moved,
                IsLeftButtonPressed: device == PointerDeviceType.Mouse && kind is PointerInputKind.Pressed or PointerInputKind.Moved));
            Vector2 Center(FrameworkElement element) => Vector2.Transform(element.Size / 2, element.GetGlobalCoordinateTransformMatrix());
            Button Button(string text) => Descendants(view).OfType<Button>().First(b => Visible(b) && (b.Content is TextBlock label ? label.Text : b.Content?.ToString())?.StartsWith(text, StringComparison.Ordinal) == true);
            void Click(string text) { Layout(); var point = Center(Button(text)); Send(PointerInputKind.Pressed, point); Send(PointerInputKind.Released, point); Layout(); }
            Layout(); Click("Level workshop");
            var workshop = Assert.Single(Descendants(view).OfType<LevelWorkshop>());
            Assert.Equal(Visibility.Visible, workshop.Visibility); Assert.Equal(Visibility.Collapsed, view.Surface.Visibility);
            var board = Descendants(workshop).Single(e => e.Name == "WorkshopMap");
            Vector2 Map(float x, float y) => Vector2.Transform(new(x * .65f, y * .65f), board.GetGlobalCoordinateTransformMatrix());
            int count = workshop.Editor.Objects.Count;
            Assert.True(Button("coin").Size.X > 0 && Button("coin").Size.Y > 0,
                string.Join("\n", Descendants(workshop).Select(e => $"{e.GetType().Name} {e.Name}: size={e.Size} desired={e.DesiredSize} visibility={e.Visibility}")));
            Send(PointerInputKind.Pressed, Center(Button("coin")));
            Assert.True(InputSystem.Current.CapturedElements.TryGetValue(91, out var captured) && ReferenceEquals(captured, board),
                $"Coin {Center(Button("coin"))}; hit={InputSystem.HitTest(Center(Button("coin")))?.GetType().Name}; captured={captured?.GetType().Name}; board={Center(board)} size={board.Size}; workshop={Center(workshop)} size={workshop.Size}");
            Send(PointerInputKind.Moved, Map(320, 400)); Send(PointerInputKind.Released, Map(320, 400)); Layout();
            Assert.Equal(count + 1, workshop.Editor.Objects.Count);
            Assert.Equal(new Box(320, 400, 0, 0), workshop.Editor.Objects[^1].Bounds);
            Click("Select / drag");
            Send(PointerInputKind.Pressed, Map(320, 400)); Send(PointerInputKind.Moved, Map(400, 336)); Send(PointerInputKind.Released, Map(400, 336)); Layout();
            Assert.Equal(new Box(400, 336, 0, 0), workshop.Editor.Objects[^1].Bounds);
            Click("Undo"); Assert.Equal(new Box(320, 400, 0, 0), workshop.Editor.Objects[^1].Bounds);
            Send(PointerInputKind.Pressed, Map(320, 400)); Send(PointerInputKind.Moved, Map(480, 300)); Send(PointerInputKind.Canceled, Map(480, 300)); Layout();
            Assert.Equal(new Box(320, 400, 0, 0), workshop.Editor.Objects[^1].Bounds);
            byte[] draft = LevelFiles.Write(workshop.Editor.Snapshot());
            Click("Play test"); Assert.Equal(GameMode.Playing, view.Surface.Session.Mode);
            Assert.NotNull(view.Surface.Session.Level.Document); Assert.Equal(Visibility.Collapsed, workshop.Visibility);
            view.OnKeyDown(new() { Key = Silk.NET.Input.Key.Escape }); Layout();
            Click("Level workshop");
            Assert.Equal(draft, LevelFiles.Write(workshop.Editor.Snapshot()));
            Click("← Back"); Assert.Equal(Visibility.Collapsed, workshop.Visibility); Assert.Equal(Visibility.Visible, view.Surface.Visibility);
        }
        finally { InputSystem.Current = oldInput; Application.Current = oldApp; ThemeManager.CurrentTheme = oldTheme; }
    }
    private static IEnumerable<FrameworkElement> Descendants(FrameworkElement root)
    {
        yield return root;
        foreach (var child in root.Children.OfType<FrameworkElement>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private static bool Visible(FrameworkElement element)
    {
        for (Visual? current = element; current is not null; current = current.Parent)
            if (current is FrameworkElement framework && framework.Visibility != Visibility.Visible) return false;
        return true;
    }
}
