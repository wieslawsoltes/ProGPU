"""Guarded HMI integration edits; removed after validation."""
from pathlib import Path


def replace(path, before, after):
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    if text.count(before) != 1:
        raise RuntimeError(f"{path}: expected exactly one source match for {before!r}")
    file.write_text(text.replace(before, after), encoding="utf-8", newline="\n")


replace("src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.Tables.cs",
    'private TextBox _monitor = null!;', 'private RichEditBox _monitor = null!;')
replace("src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.Tables.cs",
    '_monitor = new TextBox { Font = _font, FontSize = 11, AcceptsReturn = true, IsReadOnly = true };',
    '_monitor = new RichEditBox { Font = _font, FontSize = 11, AcceptsReturn = true, IsReadOnly = true };')
replace("src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.cs",
    'files.AddChild(Command("Data panels", () => _dataArea.Visibility = _dataArea.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible));',
    'files.AddChild(Command("Data panels", ToggleDataPanels));')
replace("src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.cs",
    'focused is TextBox or PasswordBox or VirtualizedCodeEditor',
    'focused is TextBox or RichEditBox or PasswordBox or VirtualizedCodeEditor')
print("Applied read-only diagnostics and focus-safe workbench integration.")
