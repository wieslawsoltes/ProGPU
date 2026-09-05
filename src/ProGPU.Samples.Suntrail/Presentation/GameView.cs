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
    private readonly StackPanel _menu, _map, _hud, _settings, _directions, _abilities;
    private readonly TouchStick _stick;
    private readonly Button[] _layoutButtons = new Button[3];
    private readonly Button _sprintSetting, _sizeSetting;
    private bool _settingsOpen, _autoSprint = true, _largeTouch = true;
    public TouchLayout ControlLayout { get; private set; }
    public event Action<int>? TouchOptionsChanged;
    private readonly Grid _touch;
    private readonly Button _primary, _levels, _pause;
    private readonly Style _actionStyle;
    private LevelWorkshop? _workshop;
    private bool _workshopOpen;
    private readonly Button[] _mapButtons = new Button[8];
    private readonly Grid _mapGrid = new();
    private bool _left, _right, _jump, _run, _mapOpen;
    private bool _touchLeft, _touchRight, _touchJump, _touchRun, _interact;
    private GameMode _lastMode = (GameMode)(-1);
    private int _lastHearts = -1, _lastSecond = -1, _lastLevel = -1;
    private (int Coins, int Relics, bool Custom) _lastScore = (-1, -1, false);
    private readonly List<HoldButton> _holdButtons = new(4);
    private Thickness _safeArea;
    public void SetSafeArea(Thickness insets) { _safeArea = insets; InvalidateMeasure(); }
    public event Action<int>? ProgressChanged;

    public GameView(int unlockedLevel = 0, int touchOptions = 12)
    {
        // Apply the loaded Fluent template explicitly: its visual states resolve
        // the per-button resources below, including hover after a menu reappears.
        _actionStyle = XamlResourceResolver.Resolve<Style>(Application.Current.Resources, typeof(Button));
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
        _levels = ActionButton("The worlds", ToggleMap, false); actions.AddChild(_levels); actions.AddChild(ActionButton("Settings", ToggleSettings, false)); _menu.AddChild(actions);
        _controls = Label("← → or A D  move     SPACE  jump     SHIFT  sprint\nHold jump to leap higher · ↓ / S enters pipes", 13); _menu.AddChild(_controls);
        var workshopButton = ActionButton("Level workshop   ↗", OpenWorkshop, false);
        workshopButton.HorizontalAlignment = HorizontalAlignment.Left; _menu.AddChild(workshopButton);
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
        _settings = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 640, Visibility = Visibility.Collapsed };
        _settings.AddChild(Label("MAKE YOURSELF AT HOME", 13, "SuntrailGold", true));
        _settings.AddChild(Label("Touch controls", 32, "SuntrailCream", true));
        _settings.AddChild(Label("Move with your left thumb. Hold JUMP to leap higher.", 14));
        var layouts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        string[] layoutNames = ["Floating stick", "Fixed stick", "Arrow buttons"];
        for (int i = 0; i < 3; i++)
        {
            int selected = i;
            _layoutButtons[i] = ActionButton(layoutNames[i], () => SetOptions((TouchLayout)selected, _autoSprint, _largeTouch), false);
            _layoutButtons[i].Padding = new Thickness(14, 12, 14, 12);
            layouts.AddChild(_layoutButtons[i]);
        }
        _settings.AddChild(layouts);
        _sprintSetting = ActionButton("", () => SetOptions(ControlLayout, !_autoSprint, _largeTouch), false);
        _sizeSetting = ActionButton("", () => SetOptions(ControlLayout, _autoSprint, !_largeTouch), false);
        _settings.AddChild(_sprintSetting); _settings.AddChild(_sizeSetting);
        _settings.AddChild(ActionButton("Save & back", ToggleSettings, true));
        AddChild(_settings);
        _touch = new Grid { Height = 160, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(25, 0, 25, 22) };
        var directions = _directions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0,0,0,10) };
        directions.AddChild(Hold("←", active => _touchLeft = active));
        directions.AddChild(Hold("→", active => _touchRight = active));
        _touch.AddChild(directions);
        var abilities = _abilities = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0,0,0,10) };
        abilities.AddChild(Hold("RUN", active => _touchRun = active));
        abilities.AddChild(Hold("JUMP", active =>
        {
            if (active && !_touchJump) Surface.Input = Surface.Input with { JumpPressed = true };
            _touchJump = active;
        }));
        abilities.AddChild(Hold("↓", active =>
        {
            if (active) Surface.Input = Surface.Input with { InteractPressed = true };
        }));
        _touch.AddChild(abilities);
        _stick = new TouchStick { Width = 300, Height = 160, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetName(_stick, "Movement thumbstick");
        _stick.InputChanged += Refresh;
        _touch.AddChild(_stick);
        AddChild(_touch);
        ApplyTouchOptions(touchOptions);
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
        var b = new Button { Style = _actionStyle, Content = Label(text, 15, primary ? "SuntrailInk" : "SuntrailCream", true), Font = InterFontFamily.Bold, FontSize = 15,
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
        var b = new HoldButton(active => { action(active); Refresh(); }) { Content = text, Font = InterFontFamily.Bold, FontSize = 17, MinWidth = text.Length > 1 ? 90 : 60,
            Height = 54, CornerRadius = new CornerRadius(12), Background = new ThemeResourceBrush("SuntrailButton"), Foreground = new ThemeResourceBrush("SuntrailCream") };
        AutomationProperties.SetName(b, text switch { "←" => "Move left", "→" => "Move right", _ => text });
        _holdButtons.Add(b);
        return b;
    }
    public int TouchOptions => (int)ControlLayout | (_autoSprint ? 4 : 0) | (_largeTouch ? 8 : 0);
    public void ApplyTouchOptions(int value)
    {
        if (value < 0 || value > 14 || (value & 3) == 3) value = 12;
        SetOptions((TouchLayout)(value & 3), (value & 4) != 0, (value & 8) != 0, false);
    }
    private void SetOptions(TouchLayout layout, bool autoSprint, bool large, bool save = true)
    {
        ClearInput(); ControlLayout = layout; _autoSprint = autoSprint; _largeTouch = large;
        _stick.Floating = layout == TouchLayout.FloatingStick; _stick.AutoSprint = autoSprint;
        _stick.Visibility = layout == TouchLayout.Buttons ? Visibility.Collapsed : Visibility.Visible;
        _directions.Visibility = layout == TouchLayout.Buttons ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)_abilities.Children[0]).Visibility = autoSprint ? Visibility.Collapsed : Visibility.Visible;
        foreach (var b in _holdButtons)
        {
            bool jump = Equals(b.Content, "JUMP");
            b.Width = b.MinWidth = jump ? (large ? 94 : 78) : (large ? 74 : 62);
            b.Height = jump ? (large ? 94 : 78) : (large ? 74 : 62);
            b.CornerRadius = new(jump ? b.Height / 2 : 22); b.Opacity = .72f;
            b.VerticalAlignment = VerticalAlignment.Bottom;
        }
        for (int i = 0; i < _layoutButtons.Length; i++)
        {
            var button = _layoutButtons[i]; bool selected = i == (int)layout;
            foreach (string state in new[] { "", "PointerOver", "Pressed" })
                button.Resources["ButtonBackground" + state] = new ThemeResourceBrush(selected ? "SuntrailGold" : "SuntrailButton");
            button.Background = new ThemeResourceBrush(selected ? "SuntrailGold" : "SuntrailButton");
            if (button.Content is TextBlock label) label.Foreground = new ThemeResourceBrush(selected ? "SuntrailInk" : "SuntrailCream");
        }
        ((TextBlock)_sprintSetting.Content!).Text = autoSprint ? (layout == TouchLayout.Buttons ? "Sprint: Automatic" : "Sprint: Push to the outer edge") : "Sprint: Separate RUN button";
        ((TextBlock)_sizeSetting.Content!).Text = large ? "Button size: Large" : "Button size: Standard";
        if (save) TouchOptionsChanged?.Invoke(TouchOptions);
        InvalidateMeasure(); Refresh();
    }
    private void ToggleSettings() { ClearInput(); _settingsOpen = !_settingsOpen; _mapOpen = false; RefreshMap(); }
    private void OpenWorkshop()
    {
        ClearInput();
        if (Surface.Session.Mode == GameMode.Playing) Surface.Session.TogglePause();
        if (_workshop is null)
        {
            _workshop = new LevelWorkshop(ActionButton);
            _workshop.PlayRequested += document =>
            {
                _workshopOpen = false; _workshop.Visibility = Visibility.Collapsed; Surface.Visibility = Visibility.Visible;
                _lastLevel = -1; _lastMode = (GameMode)(-1); Surface.Session.StartDocument(document); ClearInput(); Refresh();
            };
            _workshop.CloseRequested += () =>
            {
                _workshopOpen = false; _workshop.Visibility = Visibility.Collapsed; Surface.Visibility = Visibility.Visible;
                _lastMode = (GameMode)(-1); ClearInput(); Refresh(); RefreshMap();
            };
            AddChild(_workshop);
        }
        _workshopOpen = true; _workshop.Visibility = Visibility.Visible; Surface.Visibility = Visibility.Collapsed;
        _menu.Visibility = _hud.Visibility = _pause.Visibility = _touch.Visibility = _veil.Visibility = Visibility.Collapsed;
        _mapOpen = _settingsOpen = false; RefreshMap();
    }
    private void ToggleMap() { _mapOpen = !_mapOpen; _settingsOpen = false; RefreshMap(); }
    private void RefreshMap()
    {
        _map.Visibility = _mapOpen ? Visibility.Visible : Visibility.Collapsed;
        _settings.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
        _menu.Visibility = _workshopOpen || _settingsOpen || _mapOpen || Surface.Session.Mode == GameMode.Playing ? Visibility.Collapsed : Visibility.Visible;
        for (int i = 0; i < Level.Names.Length; i++)
            _mapButtons[i].IsEnabled = i <= Surface.Session.UnlockedLevel;
    }
    private void Refresh()
    {
        if (_workshopOpen) return;
        var s = Surface.Session;
        if (_lastMode != s.Mode)
        {
            _lastMode = s.Mode; bool playing = s.Mode == GameMode.Playing;
            _veil.Visibility = _menu.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            _touch.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            _hud.Visibility = s.Mode == GameMode.Title ? Visibility.Collapsed : Visibility.Visible;
            _pause.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            _mapOpen = _settingsOpen = false; RefreshMap();
            _heading.Text = s.Mode switch { GameMode.Title => "SUNTRAIL", GameMode.Paused => "Take a breath.", GameMode.Fallen => "One more leap.", GameMode.LevelComplete => "Island restored.", GameMode.Complete => "A new sunrise.", _ => "" };
            _heading.FontSize = s.Mode == GameMode.Title ? (Size.X < 700 ? 53 : 88) : (Size.X < 700 ? 38 : 62);
            _eyebrow.Text = s.Mode switch { GameMode.Title => "A LITTLE COURIER. A GREAT BIG WORLD.", GameMode.Paused => "THE TRAIL WILL WAIT", GameMode.Fallen => "EVERY ADVENTURE TAKES PRACTICE", GameMode.LevelComplete => "A LITTLE LIGHT GOES A LONG WAY", _ => "YOU BROUGHT THE LIGHT HOME" };
            _description.Text = s.Mode switch
            {
                GameMode.Title => "Chase the light beyond the horizon.\nEight worlds. Follow the light from forest to sky.",
                GameMode.Paused => s.Level.Document is null ? Level.Regions[s.Level.Index] + "\n" + Level.Descriptions[s.Level.Index] : s.Level.Name + "\nReturn to the workshop to keep editing.",
                GameMode.Fallen => "Return to your last lantern and try again.\nYour collected sunsparks stay with you.",
                GameMode.LevelComplete => $"{s.Coins} sunsparks collected · {s.Relics} hidden relics\n{Level.Names[Math.Min(s.Level.Index + 1, 7)]} awaits.",
                _ => s.Level.Document is null ? "All eight worlds are shining again.\nThank you for walking the Suntrail." : "Trail complete.\nReturn to the workshop to keep creating."
            };
            if (_primary.Content is TextBlock primaryText)
            {
                primaryText.Text = s.Mode switch { GameMode.Title => s.UnlockedLevel == 0 ? "Begin adventure   →" : "Continue adventure   →", GameMode.Paused => "Back to the trail   →", GameMode.Fallen => "Try again   →", GameMode.LevelComplete => "Next island   →", _ => "Play again   →" };
                AutomationProperties.SetName(_primary, primaryText.Text);
            }
            if (s.Level.Document is null && s.Mode is GameMode.LevelComplete or GameMode.Complete) ProgressChanged?.Invoke(s.UnlockedLevel);
        }
        if (_lastLevel != s.Level.Index + (s.Level.IsDungeon ? 8 : 0)) { _lastLevel = s.Level.Index + (s.Level.IsDungeon ? 8 : 0); _stage.Text = s.Level.Document is not null ? s.Level.Name : s.Level.IsDungeon ? "SECRET VAULT · ↓ on a pipe to return" : $"{s.Level.Index + 1:00} / {Level.Names[s.Level.Index]}"; }
        var score = (s.Coins, s.Relics, s.Level.Document is not null);
        if (_lastScore != score)
        {
            _lastScore = score;
            _score.Text = s.Level.Document is null
                ? $"{s.Coins:00}  SUNSPARKS   ·   {s.Relics}/3 RELICS"
                : $"{s.Coins:00}  SUNSPARKS   ·   {s.Relics} RELICS";
        }
        if (_lastHearts != s.Hearts) { _lastHearts = s.Hearts; _health.Text = s.Hearts switch { 3 => "● ● ●", 2 => "● ● ○", 1 => "● ○ ○", _ => "○ ○ ○" }; }
        if (_lastSecond != (int)s.Time) { _lastSecond = (int)s.Time; _timer.Text = $"{_lastSecond / 60}:{_lastSecond % 60:00}"; }
        if (!Surface.AutoPlay)
            Surface.Input = new(Math.Clamp((_right || _touchRight ? 1 : 0) - (_left || _touchLeft ? 1 : 0) + _stick.Axis, -1, 1),
                _jump || _touchJump, Surface.Input.JumpPressed,
                _run || _touchRun || _stick.Sprint || (_autoSprint && (_touchLeft || _touchRight)), Surface.Input.InteractPressed);
    }
    protected override void ArrangeOverride(ProGPU.Scene.Rect arrangeRect)
    {
        bool compact = arrangeRect.Width < 850 || arrangeRect.Height < 500;
        if (_workshop is not null) _workshop.Margin = _safeArea;
        _settings.Margin = new Thickness(_safeArea.Left + 20, _safeArea.Top + 8, _safeArea.Right + 20, _safeArea.Bottom + 8);
        _settings.Spacing = arrangeRect.Height < 500 ? 7 : 12;
        _stick.Width = Math.Min(300, arrangeRect.Width * .43f);
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
        _left = _right = _jump = _run = _touchLeft = _touchRight = _touchJump = _touchRun = _interact = false;
        foreach (var button in _holdButtons) button.Reset();
        _stick?.Reset();
        Surface.Input = default;
    }
    public void Deactivate()
    {
        ClearInput(); if (Surface.Session.Mode == GameMode.Playing) Surface.Session.TogglePause();
    }
    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (_workshopOpen) { _workshop?.HandleKey(e.Key); e.Handled = true; return; }
        // A settings/map overlay owns keyboard focus until it is dismissed.
        // Space/Enter must not secretly resume the simulation behind the panel.
        if ((_settingsOpen || _mapOpen) && e.Key is not (Key.Escape or Key.P))
        {
            base.OnKeyDown(e); return;
        }
        switch(e.Key)
        {
            case Key.Left: case Key.A: _left = true; break;
            case Key.Right: case Key.D: _right = true; break;
            case Key.Space: case Key.Up: case Key.W:
                if (Surface.Session.Mode != GameMode.Playing) { Surface.Session.Continue(); break; }
                if (!_jump) Surface.Input = Surface.Input with { JumpPressed = true }; _jump = true; break;
            case Key.Down: case Key.S:
                if (!_interact) Surface.Input = Surface.Input with { InteractPressed = true }; _interact = true; break;
            case Key.ShiftLeft: case Key.ShiftRight: _run = true; break;
            case Key.Escape: case Key.P: if (_settingsOpen) ToggleSettings(); else if (_mapOpen) ToggleMap(); else Surface.Session.TogglePause(); ClearInput(); break;
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
            case Key.Down: case Key.S: _interact = false; break;
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
            _pointer = e.Pointer.PointerId; CapturePointer(e.Pointer); Opacity = 1; changed(true); App.TouchFeedback?.Invoke(); e.Handled = true;
        }
        public override void OnPointerReleased(PointerRoutedEventArgs e) => Release(e);
        public override void OnPointerCanceled(PointerRoutedEventArgs e) => Release(e);
        public override void OnPointerCaptureLost(PointerRoutedEventArgs e)
        {
            if (_pointer != e.Pointer.PointerId) return;
            _pointer = null; Opacity = .72f; changed(false); e.Handled = true;
        }
        public void Reset()
        {
            _pointer = null; ReleasePointerCaptures(); Opacity = .72f; changed(false);
        }
        private void Release(PointerRoutedEventArgs e)
        {
            if (_pointer != e.Pointer.PointerId) return;
            _pointer = null; ReleasePointerCapture(e.Pointer); Opacity = .72f; changed(false); e.Handled = true;
        }
    }
}
