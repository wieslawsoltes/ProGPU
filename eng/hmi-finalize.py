"""Guarded integration edits for the HMI feature branch. Removed after validation."""
from pathlib import Path


def replace(path, before, after):
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    if text.count(before) != 1:
        raise RuntimeError(f"{path}: expected exactly one source match")
    file.write_text(text.replace(before, after), encoding="utf-8", newline="\n")


replace("src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.cs",
    '            foreach (var locked in original.Values.Where(e => e.IsLocked && !ids.Contains(e.Id))) elements.Add(locked.Copy());',
    '''            for (int index = 0; index < Session.ActiveScreen.Elements.Count; index++)
            {
                var locked = Session.ActiveScreen.Elements[index];
                if (locked.IsLocked && !ids.Contains(locked.Id))
                    elements.Insert(Math.Min(index, elements.Count), locked.Copy());
            }''')
replace("src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.cs",
    'private readonly ScrollViewer _previewScroll = new() { Visibility = Visibility.Collapsed };',
    'private readonly ScrollViewer _previewScroll = new() { Visibility = Visibility.Collapsed, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };')
replace("src/ProGPU.WinUI.Hmi/HmiControl.cs",
    '!double.IsFinite(definition.Maximum) || definition.Maximum <= definition.Minimum',
    '!double.IsFinite(definition.Maximum) || !double.IsFinite(definition.Maximum - definition.Minimum) || definition.Maximum <= definition.Minimum')
replace("src/ProGPU.WinUI.Hmi/HmiControl.cs",
    '        _value.FontSize = Symbol == HmiSymbol.AlarmList ? 12 : Symbol == HmiSymbol.NumericDisplay ? 27 : 16;',
    '''        if (Quality != HmiQuality.Good && Symbol is (HmiSymbol.Pump or HmiSymbol.Motor or HmiSymbol.Indicator or HmiSymbol.Conveyor or HmiSymbol.Valve or HmiSymbol.ToggleSwitch))
            _value.Text = "UNKNOWN";
        _value.FontSize = Symbol == HmiSymbol.AlarmList ? 12 : Symbol == HmiSymbol.NumericDisplay ? 27 : 16;''')
print("Applied HMI pointer, lock ordering, preview scrolling and quality presentation fixes.")
