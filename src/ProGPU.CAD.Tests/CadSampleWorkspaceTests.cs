using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadSampleWorkspaceTests
{
    [Theory]
    [InlineData(1280, 800)]
    [InlineData(800, 600)]
    public void CompactWorkspaceGivesMostHeightToTheDrawing(float width, float height)
    {
        var view = new CadSampleView();
        try
        {
            Layout(view, width, height);
            Assert.False(view.AreAdvancedToolsVisible);
            Assert.True(view.Canvas.Size.Y >= height * 0.75f);
            Assert.Equal(width, view.Canvas.Size.X);
            Button toggle = FindButton(view, "More tools");
            Assert.True(toggle.Size.X > 0);
            Assert.Equal(Visibility.Visible, toggle.Visibility);
            foreach (string label in new[] { "Open DXF/DWG", "Save As", "Fit", "Line", "Circle" })
            {
                Assert.True(FindButton(view, label).Size.X > 0);
            }
        }
        finally
        {
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void AdvancedToolsTogglePreservesControlsDocumentAndDrawingSpace()
    {
        var view = new CadSampleView();
        try
        {
            Layout(view, 800, 600);
            CadDocumentSession session = view.Canvas.CurrentSession!;
            ulong generation = session.ContentGeneration;
            Button advancedButton = FindButton(view, "Load LIN");
            Visual? parent = advancedButton.Parent;
            Button toggle = FindButton(view, "More tools");
            long version = view.ChangeVersion;

            Press(toggle);
            Layout(view, 800, 600);
            Assert.True(view.AreAdvancedToolsVisible);
            Assert.True(view.Canvas.Size.Y >= 250);
            Assert.True(view.ChangeVersion > version);
            Assert.Same(advancedButton, FindButton(view, "Load LIN"));
            Assert.Same(parent, advancedButton.Parent);
            Assert.True(advancedButton.Size.X > 0);

            Press(toggle);
            Layout(view, 800, 600);
            Assert.False(view.AreAdvancedToolsVisible);
            Assert.Equal(466, view.Canvas.Size.Y);
            Assert.Same(session, view.Canvas.CurrentSession);
            Assert.Equal(generation, session.ContentGeneration);
            Assert.Same(parent, advancedButton.Parent);
        }
        finally
        {
            view.Canvas.FireUnloaded();
        }
    }

    private static void Layout(CadSampleView view, float width, float height)
    {
        view.Measure(new Vector2(width, height));
        view.Arrange(new Rect(0, 0, width, height));
    }

    private static void Press(Button button) => button.OnKeyDown(new KeyRoutedEventArgs
    {
        Key = Silk.NET.Input.Key.Enter,
    });

    private static Button FindButton(Visual root, string label) => Descendants(root)
        .OfType<Button>().Single(button => button.Content is TextBlock text && text.Text == label);

    private static IEnumerable<Visual> Descendants(Visual visual)
    {
        yield return visual;
        if (visual is not ContainerVisual container) yield break;
        foreach (Visual child in container.Children)
        foreach (Visual descendant in Descendants(child))
            yield return descendant;
    }
}
