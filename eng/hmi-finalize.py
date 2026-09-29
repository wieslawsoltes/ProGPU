"""One-time, guarded source fixes for the HMI feature branch. No network access."""
from pathlib import Path


def replace(path: str, before: str, after: str, expected: int = 1) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(before)
    if count != expected:
        raise RuntimeError(f"{path}: expected {expected} exact matches, found {count}")
    file.write_text(text.replace(before, after), encoding="utf-8", newline="\n")


replace("src/ProGPU.WinUI.Hmi/HmiControl.cs", "using Microsoft.UI.Xaml.Controls;", "using Microsoft.UI.Xaml.Controls;\nusing Microsoft.UI.Xaml.Input;")
replace("src/ProGPU.WinUI.Hmi/HmiControl.cs", "public TtfFont? Font", "public new TtfFont? Font")
replace("src/ProGPU.Samples/Pages/VisualDesignerPage.cs", "using Microsoft.UI.Xaml;", "using ProGPU.Backend;\nusing Microsoft.UI.Xaml;")
replace("src/ProGPU.Hmi/HmiProjectSerializer.cs", "using System.Text;", "using System.Diagnostics.CodeAnalysis;\nusing System.Text;")
replace("src/ProGPU.Hmi/HmiProjectSerializer.cs", "private static void Require(bool condition, string message)", "private static void Require([DoesNotReturnIf(false)] bool condition, string message)")
replace("src/ProGPU.Hmi/HmiProjectSerializer.cs", "double.IsFinite(tag.Maximum) && tag.Minimum < tag.Maximum", "double.IsFinite(tag.Maximum) && double.IsFinite(tag.Maximum - tag.Minimum) && tag.Minimum < tag.Maximum")
replace("src/ProGPU.Hmi/HmiProjectSerializer.cs", "double.IsFinite(element.Maximum) && element.Minimum < element.Maximum", "double.IsFinite(element.Maximum) && double.IsFinite(element.Maximum - element.Minimum) && element.Minimum < element.Maximum")
replace("src/ProGPU.Hmi/HmiAlarm.cs", "public bool NeedsAttention => IsActive || !IsAcknowledged;", "public bool NeedsAttention => IsActive || !IsAcknowledged || IsQualityUnknown;")
replace("src/ProGPU.Hmi/HmiAlarm.cs", "IsActive ? value > Definition.Limit - Definition.Deadband", "IsActive ? value >= Definition.Limit - Definition.Deadband")
replace("src/ProGPU.Hmi/HmiAlarm.cs", "IsActive ? value < Definition.Limit + Definition.Deadband", "IsActive ? value <= Definition.Limit + Definition.Deadband")
replace("src/ProGPU.WinUI.Designer/DesignerHost.cs", "            if (original is Button origButton && clone is Button cloneButton)", "            if (DesignerElementRegistry.IsAtomic(original))\n            {\n                // The registered factory/state copier owns this component's private visual tree.\n            }\n            else if (original is Button origButton && clone is Button cloneButton)", expected=2)
replace("src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.cs", "_outline.ModeChanged += _ => { _canvas.IsLogicalMode = true;", "_outline.ModeChanged += _ => { _outline.IsLogicalMode = true; _canvas.IsLogicalMode = true;")
replace("src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.cs", "                var element = control.CaptureDefinition();\n                if (original.TryGetValue", "                var element = control.CaptureDefinition();\n                element.IsHidden = control.Visibility != Visibility.Visible;\n                if (original.TryGetValue")
replace("src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.cs", "            UpdateInspector();\n            _multiAdorner.Invalidate();\n        });", "            // Reconcile even no-op/rejected mutations (for example deleting a locked item in the shared outline).\n            RebuildDocumentViews();\n            UpdateInspector();\n            _multiAdorner.Invalidate();\n        });")
replace("src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.Inspector.cs", "        _properties.ClearItems();", "        _outline.IsHitTestVisible = !IsPreviewing;\n        _palette.IsHitTestVisible = !IsPreviewing;\n        _properties.ClearItems();")
replace("src/ProGPU.WinUI.Hmi.Designer/HmiDesignerSession.cs", "element.Action.Kind is HmiActionKind.ToggleTag or HmiActionKind.WriteTag &&", "element.Action.Kind is (HmiActionKind.ToggleTag or HmiActionKind.WriteTag) &&")
replace("src/ProGPU.WinUI.Designer/DesignerMultiSelectionAdorner.cs", "        Width = 32768; Height = 32768;\n", "")
replace("src/ProGPU.WinUI.Designer/DesignerMultiSelectionAdorner.cs", "    public override void OnRender(DrawingContext context)", "    protected override Vector2 MeasureOverride(Vector2 availableSize)\n    {\n        float width = _canvas.DesignSurface.Width;\n        float height = _canvas.DesignSurface.Height;\n        return new Vector2(float.IsFinite(width) ? width : 1280, float.IsFinite(height) ? height : 720);\n    }\n    public override void OnRender(DrawingContext context)")
print("Applied guarded HMI integration fixes.")
