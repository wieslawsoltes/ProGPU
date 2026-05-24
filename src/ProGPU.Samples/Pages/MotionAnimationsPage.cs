using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Windowing;
using Silk.NET.Input;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Compute;
using ProGPU.Virtualization;
using ProGPU.WinUI;
using Button = ProGPU.WinUI.Button;
using StackPanel = ProGPU.WinUI.StackPanel;

namespace ProGPU.Samples;

public static class MotionAnimationsPage
{
        public static FrameworkElement Create()
        {
            var grid = new ProGPU.WinUI.Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new GridLength(50, GridUnitType.Absolute));   // Header description
            grid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));       // Showcase cards
    
            var descText = new RichTextBlock { Font = Program._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 10) };
            descText.Inlines.Add(new Run("This page showcases modern high-performance GPU-accelerated motion and composition animations, including keyframe loops, spring wobbles, and dynamic expressions."));
            grid.AddChild(descText);
            ProGPU.WinUI.Grid.SetRow(descText, 0);
    
            var cardsGrid = new ProGPU.WinUI.Grid();
            cardsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            cardsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            cardsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
    
            var keyframeCard = new KeyframeShowcaseCard(Program._font!);
            cardsGrid.AddChild(keyframeCard);
            ProGPU.WinUI.Grid.SetColumn(keyframeCard, 0);
    
            var springCard = new SpringWobbleShowcaseCard(Program._font!);
            cardsGrid.AddChild(springCard);
            ProGPU.WinUI.Grid.SetColumn(springCard, 1);
    
            var expressionCard = new ExpressionTrackingShowcaseCard(Program._font!);
            cardsGrid.AddChild(expressionCard);
            ProGPU.WinUI.Grid.SetColumn(expressionCard, 2);
    
            grid.AddChild(cardsGrid);
            ProGPU.WinUI.Grid.SetRow(cardsGrid, 1);
    
            return grid;
        }
}
