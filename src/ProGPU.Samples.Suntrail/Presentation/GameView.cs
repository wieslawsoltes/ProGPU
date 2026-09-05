using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using ProGPU.Fonts.Inter;
using ProGPU.Samples.Suntrail.Game;
using ProGPU.Vector;
using Key = Silk.NET.Input.Key;

namespace ProGPU.Samples.Suntrail.Presentation;

public sealed class GameView : Grid
{
    public GameSurface Surface { get; } = new();
    private readonly TextBlock _stage, _score, _health, _timer, _heading, _eyebrow, _description, _controls;
    private readonly Border _veil;
    private readonly StackPanel _menu, _map, _hud;
    private readonly Grid _touch;
    private readonly Button _primary, _levels, _pause;
    private readonly Button[] _mapButtons = new Button[8];
    private readonly Grid _mapGrid = new();
    private bool _left, _right, _jump, _run, _mapOpen;
    private bool _touchLeft, _touchRight, _touchJump, _touchRun;
    private GameMode _lastMode = (GameMode)(-1);
    private int _lastCoins = -1, _lastHearts = -1, _lastSecond = -1, _lastLevel = -1;
    private readonly List<HoldButton> _holdButtons = new(4);
    private Thickness _safeArea;
    public void SetSafeArea(Thickness insets) { _safeArea = insets; InvalidateMeasure(); }
    public event Action<int>? ProgressChanged;

    public GameView(int unlockedLevel = 0)
    {
        Surface.Session.SetUnlockedLevel(unlockedLevel);
        AddChild(Surface);
        _hud = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 30, Margin = new Thickness(36, 26, 130, 0), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        var chapter = new StackPanel { Spacing = 4 };
        chapter.AddChild(Label("SUNTRAIL", 15, "SuntrailGold", true));
        _stage = Label("01 / The waking orchard", 14); chapter.AddChild(_stage); _hud.AddChild(chapter);
        _health = Label("● ● ●", 23, "SuntrailGold"); _hud.AddChild(_health);
        _score = Label("00  /  SUNSPARKS", 16); _hud.AddChild(_score);
        _timer = Label("0:00", 15); _hud.AddChild(_timer); AddChild(_hud);
        _pause = ActionButton("Pause  II", () => { Surface.Session.TogglePause(); ClearInput(); }, false);
        _pause.HorizontalAlignment = HorizontalAlignment.Right; _pause.VerticalAlignment = VerticalAlignment.Top;
        _pause.Margin = new Thickness(0, 22, 28, 0); AddChild(_pause);
        _veil = new Border { Background = new ThemeResourceBrush("SuntrailVeil"), IsHitTestVisible = false };
        AddChild(_veil);
        _menu = new StackPanel { Spacing = 18, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(72, 0, 28, 60), MaxWidth = 670 };
        _eyebrow = Label("A LITTLE COURIER. A GREAT BIG WORLD.", 13, "SuntrailGold", true); _menu.AddChild(_eyebrow);
        _heading = Label("SUNTRAIL", 88, "SuntrailCream", true); _menu.AddChild(_heading);
        _description = Label("Chase the light beyond the horizon.\nEight worlds. Follow the light from forest to sky.", 21); _menu.AddChild(_description);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 12, 0, 0) };
        _primary = ActionButton("Begin adventure   →", () => { Surface.Session.Continue(); _mapOpen = false; ClearInput(); }, true); actions.AddChild(_primary);
        _levels = ActionButton("The worlds", ToggleMap, false); actions.AddChild(_levels); _menu.AddChild(actions);
        _controls = Label("← → or A D  move     SPACE  jump     SHIFT  sprint\nHold jump to leap higher · Land on beetles to bounce", 13); _menu.AddChild(_controls);
        AddChild(_menu);
        _map = new StackPanel { Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(25), Visibility = Visibility.Collapsed };
        _map.AddChild(Label("THE EIGHT WORLDS", 17, "SuntrailGold", true));
        _mapGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        _mapGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        for (int i = 0; i < 8; i++) _mapGrid.RowDefinitions.Add(GridLength.Auto);
        for (int i = 0; i < Level.Names.Length; i++)
        {
            int index = i;
            var button = ActionButton($"{i + 1:00}   {Level.Names[i]}", () =>
            {
                if (index <= Surface.Session.UnlockedLevel) { Surface.Session.StartLevel(index); _mapOpen = false; ClearInput(); }
            }, false);
            button.Name = $"Island{i + 1}"; button.Margin = new Thickness(4);
            _mapButtons[i] = button; Grid.SetRow(button, i / 2); Grid.SetColumn(button, i % 2); _mapGrid.AddChild(button);
        }
        _map.AddChild(_mapGrid);
        _map.AddChild(ActionButton("← Back", ToggleMap, false));
        AddChild(_map);
        _touch = new Grid { Height = 54, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(25, 0, 25, 22) };
        var directions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Left };
        directions.AddChild(Hold("←", active => _touchLeft = active));
        directions.AddChild(Hold("→", active => _touchRight = active));
        _touch.AddChild(directions);
        var abilities = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        abilities.AddChild(Hold("RUN", active => _touchRun = active));
        abilities.AddChild(Hold("JUMP", active =>
        {
            if (active && !_touchJump) Surface.Input = Surface.Input with { JumpPressed = true };
            _touchJump = active;
        }));
        _touch.AddChild(abilities);
        AddChild(_touch);
        Surface.Updated += Refresh;
        Refresh();
    }

    private static TextBlock Label(string text, float size, string brush = "SuntrailCream", bool bold = false) => new()
    {
        Text = text, Font = bold ? InterFontFamily.Bold : InterFontFamily.Regular, FontSize = size,
        Foreground = new ThemeResourceBrush(brush), IsHitTestVisible = false
    };
    private Button ActionButton(string text, Action action, bool primary)
    {
        var b = new Button { Content = Label(text, 15, primary ? "SuntrailInk" : "SuntrailCream", true), Font = InterFontFamily.Bold, FontSize = 15,
            Padding = new Thickness(23, 14, 23, 14), MinHeight = 48, CornerRadius = new CornerRadius(8),
            Background = new ThemeResourceBrush(primary ? "SuntrailGold" : "SuntrailButton"),
            Foreground = new ThemeResourceBrush(primary ? "SuntrailInk" : "SuntrailCream") };
        foreach (string state in new[] { "", "PointerOver", "Pressed" })
        {
            b.Resources["ButtonBackground" + state] = new ThemeResourceBrush(primary ? "SuntrailGold" : "SuntrailButton");
            b.Resources["ButtonForeground" + state] = new ThemeResourceBrush(primary ? "SuntrailInk" : "SuntrailCream");
        }
        AutomationProperties.SetName(b, text);
        b.Click += (_, _) => { action(); InputSystem.SetFocus(this); Refresh(); };
        return b;
    }
    private HoldButton Hold(string text, Action<bool> action)
    {
        var b = new HoldButton(action) { Content = text, Font = InterFontFamily.Bold, FontSize = 17, MinWidth = text.Length > 1 ? 90 : 60,
            Height = 54, CornerRadius = new CornerRadius(12), Background = new ThemeResourceBrush("SuntrailButton"), Foreground = new ThemeResourceBrush("SuntrailCream") };
        AutomationProperties.SetName(b, text switch { "←" => "Move left", "→" => "Move right", _ => text });
        _holdButtons.Add(b);
        return b;
    }
    private void ToggleMap() { _mapOpen = !_mapOpen; RefreshMap(); }
    private void RefreshMap()
    {
        _map.Visibility = _mapOpen ? Visibility.Visible : Visibility.Collapsed;
        _menu.Visibility = _mapOpen || Surface.Session.Mode == GameMode.Playing ? Visibility.Collapsed : Visibility.Visible;
        for (int i = 0; i < Level.Names.Length; i++)
            _mapButtons[i].IsEnabled = i <= Surface.Session.UnlockedLevel;
    }
    private void Refresh()
    {
        var s = Surface.Session;
        if (_lastMode != s.Mode)
        {
            _lastMode = s.Mode; bool playing = s.Mode == GameMode.Playing;
            _veil.Visibility = _menu.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            _touch.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            _hud.Visibility = s.Mode == GameMode.Title ? Visibility.Collapsed : Visibility.Visible;
            _pause.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            _mapOpen = false; RefreshMap();
            _heading.Text = s.Mode switch { GameMode.Title => "SUNTRAIL", GameMode.Paused => "Take a breath.", GameMode.Fallen => "One more leap.", GameMode.LevelComplete => "Island restored.", GameMode.Complete => "A new sunrise.", _ => "" };
            _heading.FontSize = s.Mode == GameMode.Title ? (Size.X < 700 ? 53 : 88) : (Size.X < 700 ? 38 : 62);
            _eyebrow.Text = s.Mode switch { GameMode.Title => "A LITTLE COURIER. A GREAT BIG WORLD.", GameMode.Paused => "THE TRAIL WILL WAIT", GameMode.Fallen => "EVERY ADVENTURE TAKES PRACTICE", GameMode.LevelComplete => "A LITTLE LIGHT GOES A LONG WAY", _ => "YOU BROUGHT THE LIGHT HOME" };
            _description.Text = s.Mode switch
            {
                GameMode.Title => "Chase the light beyond the horizon.\nEight worlds. Follow the light from forest to sky.",
                GameMode.Paused => Level.Regions[s.Level.Index] + "\n" + Level.Descriptions[s.Level.Index],
                GameMode.Fallen => "Return to your last lantern and try again.\nYour collected sunsparks stay with you.",
                GameMode.LevelComplete => $"{s.Coins} sunsparks collected · {s.Relics} hidden relics\n{Level.Names[Math.Min(s.Level.Index + 1, 7)]} awaits.",
                _ => $"All eight worlds are shining again.\nThank you for walking the Suntrail."
            };
            if (_primary.Content is TextBlock primaryText) primaryText.Text = s.Mode switch { GameMode.Title => s.UnlockedLevel == 0 ? "Begin adventure   →" : "Continue adventure   →", GameMode.Paused => "Back to the trail   →", GameMode.Fallen => "Try again   →", GameMode.LevelComplete => "Next island   →", _ => "Play again   →" };
            if (s.Mode is GameMode.LevelComplete or GameMode.Complete) ProgressChanged?.Invoke(s.UnlockedLevel);
        }
        if (_lastLevel != s.Level.Index) { _lastLevel = s.Level.Index; _stage.Text = $"{s.Level.Index + 1:00} / {Level.Names[s.Level.Index]}"; }
        if (_lastCoins != s.Coins * 10 + s.Relics) { _lastCoins = s.Coins * 10 + s.Relics; _score.Text = $"{s.Coins:00}  SUNSPARKS   ·   {s.Relics}/3 RELICS"; }
        if (_lastHearts != s.Hearts) { _lastHearts = s.Hearts; _health.Text = s.Hearts switch { 3 => "● ● ●", 2 => "● ● ○", 1 => "● ○ ○", _ => "○ ○ ○" }; }
        if (_lastSecond != (int)s.Time) { _lastSecond = (int)s.Time; _timer.Text = $"{_lastSecond / 60}:{_lastSecond % 60:00}"; }
        if (!Surface.AutoPlay)
            Surface.Input = new((_right || _touchRight ? 1 : 0) - (_left || _touchLeft ? 1 : 0), _jump || _touchJump, Surface.Input.JumpPressed, _run || _touchRun);
    }
    protected override void ArrangeOverride(ProGPU.Scene.Rect arrangeRect)
    {
        bool compact = arrangeRect.Width < 850 || arrangeRect.Height < 500;
        _menu.Spacing = arrangeRect.Height < 500 ? 10 : 18;
        _controls.Visibility = arrangeRect.Height < 500 ? Visibility.Collapsed : Visibility.Visible;
        _map.Width = Math.Min(740, arrangeRect.Width - 50);
        int columns = arrangeRect.Width < 650 ? 1 : 2;
        for (int i = 0; i < 8; i++)
        {
            Grid.SetRow(_mapButtons[i], i / columns); Grid.SetColumn(_mapButtons[i], i % columns);
            _mapButtons[i].Padding = new Thickness(12, 8, 12, 8);
            _mapButtons[i].MinHeight = 40;
        }
        _mapGrid.ColumnDefinitions[1] = new GridLength(columns == 1 ? 0 : 1, GridUnitType.Star);
        _menu.Margin = new Thickness(Math.Max(compact ? 28 : 72, _safeArea.Left + 20), _safeArea.Top, _safeArea.Right + 24, compact ? _safeArea.Bottom : 60);
        _heading.FontSize = _lastMode == GameMode.Title ? (compact ? 53 : 88) : (compact ? 38 : 62);
        _description.FontSize = compact ? 16 : 21;
        _controls.FontSize = compact ? 11 : 13;
        _hud.Spacing = compact ? 12 : 30; _hud.Margin = new Thickness(Math.Max(compact ? 18 : 36, _safeArea.Left + 12), _safeArea.Top + 22, _safeArea.Right + 110, 0);
        _pause.Margin = new Thickness(0, _safeArea.Top + 22, _safeArea.Right + 28, 0);
        _touch.Margin = new Thickness(_safeArea.Left + 25, 0, _safeArea.Right + 25, _safeArea.Bottom + 16);
        _timer.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _score.FontSize = compact ? 12 : 16;
        _stage.FontSize = compact ? 11 : 14;
        base.ArrangeOverride(arrangeRect);
    }
    public void ClearInput()
    {
        _left = _right = _jump = _run = _touchLeft = _touchRight = _touchJump = _touchRun = false;
        foreach (var button in _holdButtons) button.Reset();
        Surface.Input = default;
    }
    public void Deactivate()
    {
        ClearInput(); if (Surface.Session.Mode == GameMode.Playing) Surface.Session.TogglePause();
    }
    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        switch(e.Key)
        {
            case Key.Left: case Key.A: _left = true; break;
            case Key.Right: case Key.D: _right = true; break;
            case Key.Space: case Key.Up: case Key.W:
                if (Surface.Session.Mode != GameMode.Playing) { Surface.Session.Continue(); break; }
                if (!_jump) Surface.Input = Surface.Input with { JumpPressed = true }; _jump = true; break;
            case Key.ShiftLeft: case Key.ShiftRight: _run = true; break;
            case Key.Escape: case Key.P: if (_mapOpen) ToggleMap(); else Surface.Session.TogglePause(); ClearInput(); break;
            case Key.Enter: Surface.Session.Continue(); ClearInput(); break;
            case Key.R: Surface.Session.Respawn(); ClearInput(); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true; Refresh();
    }
    public override void OnKeyUp(KeyRoutedEventArgs e)
    {
        switch(e.Key)
        {
            case Key.Left: case Key.A: _left = false; break;
            case Key.Right: case Key.D: _right = false; break;
            case Key.Space: case Key.Up: case Key.W: _jump = false; break;
            case Key.ShiftLeft: case Key.ShiftRight: _run = false; break;
            default: base.OnKeyUp(e); return;
        }
        e.Handled = true; Refresh();
    }
    public override void OnPointerPressed(PointerRoutedEventArgs e) { InputSystem.SetFocus(this); base.OnPointerPressed(e); }

    private sealed class HoldButton(Action<bool> changed) : Button
    {
        private uint? _pointer;
        public override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (_pointer.HasValue) return;
            _pointer = e.Pointer.PointerId; CapturePointer(e.Pointer); changed(true); e.Handled = true;
        }
        public override void OnPointerReleased(PointerRoutedEventArgs e) => Release(e);
        public override void OnPointerCanceled(PointerRoutedEventArgs e) => Release(e);
        public override void OnPointerCaptureLost(PointerRoutedEventArgs e)
        {
            if (_pointer != e.Pointer.PointerId) return;
            _pointer = null; changed(false); e.Handled = true;
        }
        public void Reset()
        {
            _pointer = null; ReleasePointerCaptures(); changed(false);
        }
        private void Release(PointerRoutedEventArgs e)
        {
            if (_pointer != e.Pointer.PointerId) return;
            _pointer = null; ReleasePointerCapture(e.Pointer); changed(false); e.Handled = true;
        }
    }
}
