using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using ProGPU.Fonts.Inter;
using ProGPU.Samples.Suntrail.Game;
using ProGPU.Vector;

namespace ProGPU.Samples.Suntrail.Presentation;

/// <summary>Shared WinUI authoring UI; native and browser file operations use host storage services.</summary>
public sealed class LevelWorkshop : Grid
{
    public LevelEditor Editor { get; } = new(LevelDocument.CreateStarter());
    private readonly WorkshopBoard _board;
    private readonly TextBlock _status, _world;
    private readonly Button _undo, _redo;
    private readonly List<Button> _fileButtons = [];
    private bool _busy;
    public event Action<LevelDocument>? PlayRequested;
    public event Action? CloseRequested;

    public LevelWorkshop(Func<string, Action, bool, Button> actionButton)
    {
        Background = new ThemeResourceBrush("SuntrailInk");
        RowDefinitions.Add(GridLength.Auto); RowDefinitions.Add(new GridLength(1, GridUnitType.Star)); RowDefinitions.Add(GridLength.Auto);
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(16, 12, 16, 8) };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        title.AddChild(Label("LEVEL WORKSHOP", 21));
        _world = Label("", 15); title.AddChild(_world); header.AddChild(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button Action(string text, Action callback, bool primary = false)
        {
            var button = actionButton(text, callback, primary); button.Padding = new Thickness(14, 9, 14, 9); button.MinHeight = 42;
            actions.AddChild(button); return button;
        }
        Action("← Back", () => { _board?.Cancel(); CloseRequested?.Invoke(); });
        Action("Play test →", Play, true);
        _undo = Action("Undo", Editor.Undo); _redo = Action("Redo", Editor.Redo);
        Action("Delete", Editor.DeleteSelected);
        Action("− Width", () => Editor.ResizeSelected(-32)); Action("+ Width", () => Editor.ResizeSelected(32));
        Action("World →", () => Editor.SetBiome((Editor.Biome + 1) % 8));
        _fileButtons.Add(Action("Open…", () => _ = OpenAsync()));
        _fileButtons.Add(Action("Save…", () => _ = SaveAsync()));
        header.AddChild(new ScrollViewer { Content = actions, Height = 56, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        AddChild(header);

        var workspace = new Grid(); workspace.ColumnDefinitions.Add(new GridLength(128)); workspace.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star)); Grid.SetRow(workspace, 1);
        var palette = new StackPanel { Spacing = 5, Margin = new Thickness(12, 0, 8, 0) };
        var select = actionButton("Select / drag", () => _board!.Tool = null, false); select.Padding = new Thickness(8); select.MinHeight = 40; palette.AddChild(select);
        _board = new(Editor);
        for (int i = 0; i <= (int)LevelObjectKind.Crusher; i++)
        {
            var kind = (LevelObjectKind)i;
            var button = new PaletteButton(_board, kind) { Content = LevelFiles.KindName(kind), Font = InterFontFamily.Regular, FontSize = 14,
                MinHeight = 38, Padding = new Thickness(8), Background = new ThemeResourceBrush("SuntrailButton"), Foreground = new ThemeResourceBrush("SuntrailCream") };
            AutomationProperties.SetName(button, "Place " + LevelFiles.KindName(kind));
            palette.AddChild(button);
        }
        workspace.AddChild(new ScrollViewer { Content = palette, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var map = new ScrollViewer { Content = _board, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(map, 1); workspace.AddChild(map); AddChild(workspace);
        _status = Label("Drag a tool onto the map, or select a tool and tap to place. Drag objects to move them. Grid: 16 units.", 13);
        _status.Margin = new Thickness(16, 10, 16, 12); Grid.SetRow(_status, 2); AddChild(_status);
        _board.Notice += message => _status.Text = message;
        Editor.Changed += Refresh;
        Refresh();
    }

    private static TextBlock Label(string text, float size) => new() { Text = text, Font = InterFontFamily.Regular, FontSize = size,
        Foreground = new ThemeResourceBrush("SuntrailCream"), IsHitTestVisible = false };
    private void Refresh()
    {
        _undo.IsEnabled = Editor.CanUndo; _redo.IsEnabled = Editor.CanRedo;
        _world.Text = Level.Regions[Editor.Biome];
    }
    private void Play()
    {
        _board.Cancel();
        try { PlayRequested?.Invoke(Editor.Snapshot()); }
        catch (FormatException e) { _status.Text = e.Message; }
    }
    private void Busy(bool value) { _busy = value; foreach (var button in _fileButtons) button.IsEnabled = !value; }
    private static void Post(DispatcherQueue? dispatcher, Action action)
    {
        if (dispatcher is null || dispatcher.HasThreadAccess) action();
        else dispatcher.TryEnqueue(() => action());
    }
    private async Task OpenAsync()
    {
        if (_busy) return;
        _board.Cancel(); Busy(true);
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".suntrail"); picker.FileTypeFilter.Add(".json"); picker.FileTypeFilter.Add(".tmx");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            // All current hosts expose picked data as a local/virtual path. Check
            // the length before allocating the parse buffer, then cap a racing read.
            using var input = File.OpenRead(file.Path);
            if (input.Length > LevelDocument.MaximumBytes) throw new FormatException("Level files must be at most 1 MiB.");
            var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes);
            var document = LevelFiles.Read(bytes, file.Name);
            Post(dispatcher, () => { Editor.Load(document); _status.Text = $"Opened {file.Name}. Save creates a Suntrail copy; the imported file is unchanged."; });
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        { Post(dispatcher, () => _status.Text = e.Message); }
        finally { Post(dispatcher, () => Busy(false)); }
    }
    private async Task SaveAsync()
    {
        if (_busy) return;
        _board.Cancel(); Busy(true);
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        try
        {
            byte[] bytes = LevelFiles.Write(Editor.Snapshot());
            var picker = new FileSavePicker { SuggestedFileName = "my-trail.suntrail" };
            picker.FileTypeChoices.Add("Suntrail level", new[] { ".suntrail" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await file.WriteBytesAsync(bytes);
            Post(dispatcher, () => _status.Text = $"Saved {file.Name}.");
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        { Post(dispatcher, () => _status.Text = e.Message); }
        finally { Post(dispatcher, () => Busy(false)); }
    }
    public void HandleKey(Silk.NET.Input.Key key)
    {
        switch (key)
        {
            case Silk.NET.Input.Key.Delete: case Silk.NET.Input.Key.Backspace: Editor.DeleteSelected(); break;
            case Silk.NET.Input.Key.Escape: _board.Cancel(); _board.Tool = null; break;
        }
    }

    private sealed class PaletteButton(WorkshopBoard board, LevelObjectKind kind) : Button
    {
        public override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            board.BeginPlacement(kind, e); e.Handled = true;
        }
    }

    private sealed class WorkshopBoard : Canvas
    {
        private const float MapScale = .65f;
        private readonly LevelEditor _editor;
        private readonly List<Border> _shapes = [];
        private readonly List<LevelObject> _displayed = [];
        private uint? _pointer;
        private Vector2 _origin;
        private LevelObjectKind? _placement;
        private int _lastSelected = -1;
        private LevelObjectKind? _tool;
        public event Action<string>? Notice;
        public LevelObjectKind? Tool
        {
            get => _tool;
            set { _tool = value; Notice?.Invoke(value is { } kind ? $"Place {LevelFiles.KindName(kind)}: drag from the palette or tap the map. Select / drag moves existing objects." : "Select an object and drag to move it. Undo restores the complete drag."); }
        }
        public WorkshopBoard(LevelEditor editor)
        {
            _editor = editor; Name = "WorkshopMap"; Width = 4000 * MapScale; Height = 1200 * MapScale;
            AutomationProperties.SetName(this, "Level map");
            Background = new ThemeResourceBrush("SuntrailButton");
            editor.Changed += Update; Update();
        }
        private void Update()
        {
            while (_shapes.Count > _editor.Objects.Count) { RemoveChild(_shapes[^1]); _shapes.RemoveAt(_shapes.Count - 1); _displayed.RemoveAt(_displayed.Count - 1); }
            float right = 4000;
            for (int i = 0; i < _editor.Objects.Count; i++)
            {
                var item = _editor.Objects[i]; right = Math.Max(right, item.Bounds.Right + 400);
                bool added = i == _shapes.Count;
                if (added)
                {
                    var shape = new Border { IsHitTestVisible = false, BorderThickness = new Thickness(1), CornerRadius = new(2) };
                    _shapes.Add(shape); _displayed.Add(default); AddChild(shape);
                }
                if (added || _displayed[i] != item || i == _editor.Selected || i == _lastSelected)
                {
                    var shape = _shapes[i]; var b = LevelEditor.SelectionBounds(item);
                    Canvas.SetLeft(shape, b.X * MapScale); Canvas.SetTop(shape, b.Y * MapScale);
                    shape.Width = b.Width * MapScale; shape.Height = b.Height * MapScale;
                    shape.Background = new ThemeResourceBrush(item.Kind is LevelObjectKind.Coin or LevelObjectKind.Relic or LevelObjectKind.Spawn or LevelObjectKind.Exit or LevelObjectKind.Checkpoint ? "SuntrailGold" : "SuntrailCream");
                    shape.BorderBrush = new ThemeResourceBrush(i == _editor.Selected ? "SuntrailGold" : "SuntrailInk");
                    shape.BorderThickness = new Thickness(i == _editor.Selected ? 3 : 1);
                    shape.Opacity = i == _editor.Selected ? 1 : item.Kind == LevelObjectKind.Ground ? .30f : .65f;
                    if (b.Width >= 60 && b.Height >= 32)
                        shape.Child = new TextBlock { Text = LevelFiles.KindName(item.Kind), Font = InterFontFamily.Regular, FontSize = 11, Margin = new Thickness(4),
                            Foreground = new ThemeResourceBrush("SuntrailInk"), IsHitTestVisible = false };
                    else shape.Child = null;
                    _displayed[i] = item;
                }
            }
            _lastSelected = _editor.Selected; Width = right * MapScale;
        }
        public void BeginPlacement(LevelObjectKind kind, PointerRoutedEventArgs e)
        {
            if (_pointer.HasValue) return;
            Tool = _placement = kind; _pointer = e.Pointer.PointerId; CapturePointer(e.Pointer);
        }
        public override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (_pointer.HasValue) return;
            _pointer = e.Pointer.PointerId; CapturePointer(e.Pointer);
            _origin = (Vector2)e.GetCurrentPoint(this).Position / MapScale;
            int hit = _editor.HitTest(_origin);
            if (Tool.HasValue) _placement = Tool;
            else if (hit >= 0) _editor.BeginDrag(hit);
            else _editor.Select(-1);
            e.Handled = true;
        }
        public override void OnPointerMoved(PointerRoutedEventArgs e)
        {
            if (_pointer != e.Pointer.PointerId) return;
            _editor.MoveSelected((Vector2)e.GetCurrentPoint(this).Position / MapScale - _origin); e.Handled = true;
        }
        public override void OnPointerReleased(PointerRoutedEventArgs e)
        {
            if (_pointer != e.Pointer.PointerId) return;
            Vector2 p = e.GetCurrentPoint(this).Position;
            var placement = _placement; _placement = null; _pointer = null; ReleasePointerCapture(e.Pointer);
            _editor.CommitDrag();
            if (placement is { } kind && p.X >= 0 && p.X < Size.X && p.Y >= 0 && p.Y <= 944 * MapScale)
            {
                try { _editor.Add(kind, p / MapScale); }
                catch (FormatException error) { Notice?.Invoke(error.Message); }
            }
            e.Handled = true;
        }
        public override void OnPointerCanceled(PointerRoutedEventArgs e) { if (_pointer == e.Pointer.PointerId) { Cancel(); e.Handled = true; } }
        public override void OnPointerCaptureLost(PointerRoutedEventArgs e) { if (_pointer == e.Pointer.PointerId) { Cancel(); e.Handled = true; } }
        public void Cancel() { _pointer = null; _placement = null; _editor.CancelDrag(); ReleasePointerCaptures(); }
    }
}
