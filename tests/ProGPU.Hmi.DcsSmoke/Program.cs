using System.Numerics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Silk.NET.Input;
using Button = Microsoft.UI.Xaml.Controls.Button;

if (args.Length != 3) throw new ArgumentException("Usage: ProGPU.Hmi.DcsSmoke <font-file> <output-directory> <Light|Dark|HighContrast>");
if (JsonSerializer.IsReflectionEnabledByDefault) throw new InvalidOperationException("JSON reflection must remain disabled.");
var scheme = Enum.Parse<HmiColorScheme>(args[2]);
string suffix = scheme.ToString().ToLowerInvariant();
Directory.CreateDirectory(args[1]);
var font = new TtfFont(args[0]); PopupService.DefaultFont = font;
using var window = new HeadlessWindow(1600, 1000);
using var studio = new HmiDcsStudio(font: font, automaticTicks: false) { ColorScheme = scheme };
window.Content = studio; InputSystem.Current = new WindowInputState { Root = studio };
Render();
Require(studio.Engineering == null && !studio.Operator.Session.Runtime.IsRunning, "Startup must be lazy and offline.");
Capture("dcs-overview");
ClickButton("Pump station");
Require(studio.Operator.Session.Location.ScreenId == "pumps", "Display tab did not navigate.");
var pump = studio.Operator.ProcessView!.Controls.Single(c => c.ElementId == "pump-run");
Capture("dcs-before-pick");
Click(pump, pump.Size / 2);
Require(studio.Operator.Session.Location.ElementId == "pump-run", "Actual equipment picking did not select its object aspect.");
Require(studio.Operator.Session.Runtime.Read("Pump.Running").Value.Boolean, "Picking equipment changed feedback.");
Capture("dcs-operator");
var oldScreen = studio.Operator.ProcessView;
ClickButton("Trends"); Capture("dcs-trends");
ClickButton("Process");
Require(ReferenceEquals(oldScreen, studio.Operator.ProcessView) && studio.Operator.ProcessView!.Controls.Contains(pump), "Aspect navigation rebuilt process objects.");
ClickButton("Simulate");
for (int i = 0; i < 60; i++) studio.Operator.Session.Advance(TimeSpan.FromMilliseconds(100));
Render();
var pressure = studio.Operator.ProcessView!.Controls.Single(c => c.TagName == "Line.Pressure");
Console.WriteLine($"RUNTIME pressure={studio.Operator.Session.Runtime.Read("Line.Pressure").Value} control={pressure.Value} text={pressure.DisplayText}");
ClickButton("Review OFF");
Require(studio.Operator.Session.PendingReview != null && studio.Operator.Session.Runtime.Read("Pump.Running").Value.Boolean, "Review must not perform an optimistic write.");
Capture("dcs-command-review");
ClickButton("Cancel");
Require(studio.Operator.Session.PendingReview == null, "Cancel did not retire review.");
ClickButton("Review OFF"); ClickButton("Confirm local");
Require(!studio.Operator.Session.Runtime.Read("Pump.Running").Value.Boolean, "Explicit local confirmation did not apply.");
Require(studio.Operator.Session.PendingReview == null, "Confirmation is not single use.");
Capture("dcs-pump-stopped");
ClickButton("Alarm list");
var table = Visible(studio).OfType<DataGrid>().First();
Require(table.Size.Y > 60, "Alarm table did not layout.");
int alarmIndex = studio.Operator.Session.GetAlarms().ToList().FindIndex(a => a.Id == "pump-stopped");
Require(alarmIndex >= 0, "Stopped pump did not raise an alarm.");
Click(table, new Vector2(100, 45 + alarmIndex * table.RowHeight));
Capture("dcs-alarms");
ClickButton("Acknowledge selected");
Require(studio.Operator.Session.Runtime.Alarms.Single(a => a.Definition.Id == "pump-stopped").IsAcknowledged, "Native selected acknowledgement failed.");
ClickButton("Export CSV");
Require(studio.Operator.ExportedText.Contains("pump-stopped"), "Alarm export lost its source.");
Capture("dcs-export"); ClickButton("Close");
ClickButton("Events"); Capture("dcs-events");
ClickButton("System"); Capture("dcs-system");
ClickButton("Process"); ClickButton("Trends"); Capture("dcs-live-trends");
ClickButton("Process");
ClickButton("Engineering Workplace");
Require(studio.IsEngineering && studio.Engineering != null && !studio.Operator.Session.Runtime.IsRunning, "Engineering transition must stop local runtime.");
studio.Engineering!.SetDataPanelsVisible(false); Render(); studio.Engineering.Fit(); Render();
Capture("dcs-engineering");
var editor = studio.Engineering;
editor.AddComponent(HmiSymbol.InstrumentBubble, 880, 320);
string edited = editor.Session.ExportJson();
ClickButton("Operator Workplace");
Require(HmiProjectSerializer.Serialize(studio.Operator.Session.GetProject()) == edited && studio.IsDirty,
    "Operator snapshot did not follow unsaved engineering edits.");
Require(!studio.Operator.Session.Runtime.IsRunning, "Engineering return unexpectedly ran simulation.");
ClickButton("Engineering Workplace"); Require(ReferenceEquals(editor, studio.Engineering), "Editor state was discarded.");
ClickButton("Operator Workplace");
// Repeated aspect changes, palette changes, and empty-object screens must preserve a usable display.
ClickButton("Plant overview");
window.Resize(1100, 760); Render(); Capture("dcs-compact");
Require(!studio.Operator.IsPlantExplorerVisible, "Compact workspace did not release canvas space.");
window.Content = null; window.Content = studio; Render();
Require(studio.Operator.ProcessView is { Controls.Count: > 0 }, "Reattached workplace lost its runtime projection.");
ClickButton("Pump station");
Require(studio.Operator.Session.Location.ScreenId == "pumps", "Reattached navigation is not functional.");
InputSystem.Current = new WindowInputState(); window.Content = null;
Console.WriteLine($"PASS {scheme}: standalone DCS native display/object/aspect navigation, retained controls, reviewed/canceled/confirmed local command, alarm acknowledgement, events/trends/system, lazy engineering and dirty snapshot round-trip; JSON reflection disabled.");

void Render() { window.Render(0); window.Render(0); }
void Capture(string name) { Render(); window.SaveScreenshot(Path.Combine(args[1], name + "-" + suffix + ".png")); }
void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void ClickButton(string text)
{
    var button = Visible(studio).OfType<Button>().FirstOrDefault(b => b.Content is TextBlock t && t.Text == text)
        ?? throw new InvalidOperationException("Visible button not found: " + text);
    Require(button.IsEnabled, "Button unexpectedly disabled: " + text);
    Click(button, button.Size / 2);
}
void Click(FrameworkElement element, Vector2 local)
{
    var at = element.TransformToVisual(studio).TransformPoint(local);
    Require(float.IsFinite(at.X) && float.IsFinite(at.Y) && at.X >= 0 && at.X < window.Width && at.Y >= 0 && at.Y < window.Height,
        $"Pointer target is outside the viewport: {at} / {element.GetType().Name}");
    InputSystem.InjectMouseMove(at); InputSystem.InjectMouseDown(MouseButton.Left); InputSystem.InjectMouseUp(MouseButton.Left); Render();
}
IEnumerable<Visual> Visible(Visual root)
{
    if (root is FrameworkElement { Visibility: Visibility.Collapsed }) yield break;
    yield return root;
    if (root is ContainerVisual container) foreach (var child in container.Children) foreach (var item in Visible(child)) yield return item;
}
