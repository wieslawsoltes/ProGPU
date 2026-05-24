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

public static class DataVirtualizationPage
{
        public static FrameworkElement Create()
        {
            var grid = new ProGPU.WinUI.Grid();
            grid.RowDefinitions.Add(new GridLength(70, GridUnitType.Absolute));   // Header
            grid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));       // Recycled Grid
    
            var descStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };
            var listTitle = new RichTextBlock { Font = Program._font, FontSize = 14f };
            listTitle.Inlines.Add(new Bold(new Run("10,000 Record Virtualized DataGrid")));
            descStack.AddChild(listTitle);
    
            var listDesc = new RichTextBlock { Font = Program._font, FontSize = 11f, Margin = new Thickness(0, 2, 0, 0) };
            listDesc.Inlines.Add(new Run("Ultra-fast vertical scroll recycling displays massive datasets at locked 60 FPS. Click on any header column to "));
            listDesc.Inlines.Add(new Bold(new Run("sort alphanumerically")));
            listDesc.Inlines.Add(new Run(", and click rows to change selected indices. Double-click any cell (or press Enter on selection) to "));
            listDesc.Inlines.Add(new Bold(new Run("edit inline")) { Foreground = new SolidColorBrush(0x0078D4FF) });
            listDesc.Inlines.Add(new Run(". Press Enter to commit or Escape to cancel."));
            descStack.AddChild(listDesc);
    
            grid.AddChild(descStack);
            ProGPU.WinUI.Grid.SetRow(descStack, 0);
    
            // Virtualized DataGrid setup
            var dataGrid = new ProGPU.WinUI.DataGrid
            {
                Font = Program._font,
                RowHeight = 28f,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(4)
            };
    
            // Define columns
            dataGrid.Columns.Add(new DataGridColumn("ID", 70f, "Id"));
            dataGrid.Columns.Add(new DataGridColumn("Activity Name", 230f, "Name"));
            dataGrid.Columns.Add(new DataGridColumn("Status", 110f, "Status"));
            dataGrid.Columns.Add(new DataGridColumn("Latency (ms)", 120f, "Latency"));
    
            // Setup direct, reflection-free binding for maximum speed
            dataGrid.CellValueBinding = (item, prop) =>
            {
                if (item is Program.LogItem log)
                {
                    return prop switch
                    {
                        "Id" => log.Id.ToString(),
                        "Name" => log.Name,
                        "Status" => log.Status,
                        "Latency" => $"{log.Latency:F1}",
                        _ => string.Empty
                    };
                }
                return string.Empty;
            };
    
            // Populate logs
            foreach (var log in Program._logItems)
            {
                dataGrid.AddItem(log);
            }
    
            grid.AddChild(dataGrid);
            ProGPU.WinUI.Grid.SetRow(dataGrid, 1);
    
            return grid;
        }
}
