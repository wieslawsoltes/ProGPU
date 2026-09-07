using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class PanelInputHitTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void PanelBackgroundEnablesHitsWhileNullRemainsPassThrough(int kind)
    {
        var previous = InputSystem.Current;
        Panel panel = kind switch { 0 => new Panel(), 1 => new Grid(), _ => new StackPanel() };
        panel.Width = 200; panel.Height = 120;
        panel.Measure(new(200, 120)); panel.Arrange(new Rect(20, 30, 200, 120));
        InputSystem.Current = InputSystem.CreateExternalState(panel);
        try
        {
            var point = new Vector2(70, 80);
            Assert.Null(InputSystem.HitTest(point));
            // Hit eligibility depends on a brush being assigned, including transparent/unresolved resources.
            panel.Background = new ThemeResourceBrush("Transparent");
            Assert.Same(panel, InputSystem.HitTest(point));
            Assert.Null(InputSystem.HitTest(new(300, 300)));
            panel.IsHitTestVisible = false;
            Assert.Null(InputSystem.HitTest(point));
            panel.IsHitTestVisible = true;
            panel.Background = null;
            Assert.Null(InputSystem.HitTest(point));
        }
        finally { InputSystem.Current = previous; }
    }
}
