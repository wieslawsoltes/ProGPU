using System.IO;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public class DataGridValueProviderTests
{
    [Fact]
    public void DataGrid_WrappedCellMeasuresVariableRowHeight()
    {
        InterFontFamily.RegisterFonts();
        var dataGrid = new DataGrid
        {
            Width = 220f,
            Height = 140f,
            Font = InterFontFamily.Regular,
            RowHeight = float.NaN,
            MinRowHeight = 28f,
            EstimatedRowHeight = 28f,
            CellTextWrapping = TextWrapping.WrapWholeWords
        };
        dataGrid.Columns.Add(new DataGridColumn("Name", 120f, "Name"));
        dataGrid.AddItem(new ProviderRow("A long activity name that must wrap across several visual lines"));

        using var window = new HeadlessWindow(220, 140) { Content = dataGrid };
        window.Render();
        window.Render();

        Assert.True(dataGrid.TotalBodyHeight > dataGrid.MinRowHeight);
    }

    [Fact]
    public void DataGridScrollingRecordsOnlyDirectVisibleRowCommands()
    {
        InterFontFamily.RegisterFonts();
        var dataGrid = new DataGrid
        {
            Width = 220f,
            Height = 140f,
            Font = InterFontFamily.Regular,
            RowHeight = 28f
        };
        dataGrid.Columns.Add(new DataGridColumn("Name", 180f, "Name"));
        for (var index = 0; index < 100; index++)
            dataGrid.AddItem(new ProviderRow($"Row {index}"));

        using var window = new HeadlessWindow(220, 140)
        {
            Content = dataGrid
        };
        dataGrid.ScrollOffset = 1f;
        window.Render();
        var context = new DrawingContext();

        dataGrid.OnRender(context);

        Assert.DoesNotContain(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawVisual);
    }

    [Fact]
    public void ScrollViewerStableScrollbarHoverDoesNotInvalidate()
    {
        var viewer = new ScrollViewer
        {
            WidthConstraint = 200f,
            HeightConstraint = 100f,
            Content = new Border
            {
                WidthConstraint = 200f,
                HeightConstraint = 1000f
            }
        };
        using var window = new HeadlessWindow(200, 100)
        {
            Content = viewer
        };
        window.Render();
        var move = new PointerRoutedEventArgs
        {
            Position = new Vector2(195f, 20f),
            ScreenPosition = new Vector2(195f, 20f)
        };

        viewer.OnPointerMoved(move);
        long hoverVersion = viewer.ChangeVersion;
        viewer.OnPointerMoved(move);

        Assert.Equal(hoverVersion, viewer.ChangeVersion);
    }

    [Fact]
    public void DataGrid_UsesValueProviderForSortingWithoutPocoProperty()
    {
        var column = new DataGridColumn("Name", 120f, "Name");
        var beta = new ProviderRow("Beta");
        var alpha = new ProviderRow("Alpha");
        var dataGrid = new DataGrid();

        dataGrid.Columns.Add(column);
        dataGrid.AddItem(beta);
        dataGrid.AddItem(alpha);

        dataGrid.SortItems(column);

        Assert.Same(alpha, dataGrid.ItemsSource[0]);
        Assert.Same(beta, dataGrid.ItemsSource[1]);
        Assert.True(alpha.GetCount > 0);
        Assert.True(beta.GetCount > 0);
    }

    [Fact]
    public void DataGrid_CommitsEditsThroughValueProvider()
    {
        var row = new ProviderRow("Alpha");
        var dataGrid = new DataGrid();

        dataGrid.Columns.Add(new DataGridColumn("Name", 120f, "Name"));
        dataGrid.AddItem(row);

        dataGrid.BeginEdit(0, 0);
        dataGrid.CommitValue("Gamma");

        Assert.Equal("Gamma", row.Value);
        Assert.Equal(-1, dataGrid.EditingRow);
        Assert.True(row.SetCount > 0);
    }

    [Fact]
    public void DataGrid_UsesRegisteredAccessorForPocoSortingAndEditing()
    {
        DataGrid.RegisterValueAccessor<RegisteredRow, string>(
            "Name",
            row => row.Name,
            (row, value) => row.Name = value);

        try
        {
            var column = new DataGridColumn("Name", 120f, "Name");
            var beta = new RegisteredRow("Beta");
            var alpha = new RegisteredRow("Alpha");
            var dataGrid = new DataGrid();

            dataGrid.Columns.Add(column);
            dataGrid.AddItem(beta);
            dataGrid.AddItem(alpha);

            dataGrid.SortItems(column);

            Assert.Same(alpha, dataGrid.ItemsSource[0]);
            Assert.Same(beta, dataGrid.ItemsSource[1]);

            dataGrid.BeginEdit(0, 0);
            dataGrid.CommitValue("Gamma");

            Assert.Equal("Gamma", alpha.Name);
            Assert.Equal(-1, dataGrid.EditingRow);
        }
        finally
        {
            DataGrid.UnregisterValueAccessor<RegisteredRow>("Name");
        }
    }

    [Fact]
    public void DataGrid_DoesNotDiscoverUnregisteredPocoProperties()
    {
        var row = new UnregisteredRow("Alpha");
        var dataGrid = new DataGrid();

        dataGrid.Columns.Add(new DataGridColumn("Name", 120f, "Name"));
        dataGrid.AddItem(row);

        dataGrid.BeginEdit(0, 0);
        dataGrid.CommitValue("Gamma");

        Assert.Equal("Alpha", row.Name);
        Assert.Equal(-1, dataGrid.EditingRow);
    }

    [Fact]
    public void DataGridControlSourceDoesNotUseRuntimePropertyReflection()
    {
        string source = File.ReadAllText(FindRepoFile("src/ProGPU.WinUI/Controls/DataGrid.cs"));

        Assert.DoesNotContain("GetProperty(", source);
        Assert.DoesNotContain("BindingFlags", source);
        Assert.DoesNotContain("System.Reflection", source);
    }

    private sealed class ProviderRow : IDataGridValueProvider
    {
        public ProviderRow(string value)
        {
            Value = value;
        }

        public string Value { get; private set; }
        public int GetCount { get; private set; }
        public int SetCount { get; private set; }

        public bool TryGetDataGridValue(string propertyName, out object? value)
        {
            if (propertyName == "Name")
            {
                GetCount++;
                value = Value;
                return true;
            }

            value = null;
            return false;
        }

        public bool TrySetDataGridValue(string propertyName, object? value)
        {
            if (propertyName == "Name" && value is string text)
            {
                SetCount++;
                Value = text;
                return true;
            }

            return false;
        }

        public Type? GetDataGridValueType(string propertyName)
        {
            return propertyName == "Name" ? typeof(string) : null;
        }
    }

    private sealed class RegisteredRow
    {
        public RegisteredRow(string name)
        {
            Name = name;
        }

        public string Name { get; set; }
    }

    private sealed class UnregisteredRow
    {
        public UnregisteredRow(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }

    private static string FindRepoFile(string relativePath)
    {
        DirectoryInfo? current = new(Directory.GetCurrentDirectory());
        while (current != null)
        {
            string candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {Directory.GetCurrentDirectory()}.");
    }
}
