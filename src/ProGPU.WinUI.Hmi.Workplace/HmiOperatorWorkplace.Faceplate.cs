using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Workplace;

public sealed partial class HmiOperatorWorkplace
{
    private HmiElement? _selectedDefinition;
    private HmiControl? _faceplateControl;
    private TextBlock? _faceValue, _faceQuality, _faceDetails, _reviewDescription;
    private Grid? _reviewPanel;
    private TextBox? _setpoint;
    private Button? _reviewOff, _reviewOn, _reviewValue, _confirmButton;
    private HmiLocalCommandReview? _displayedReview;

    private void BuildFaceplate()
    {
        var location = Session.Location;
        string identity = location.ScreenId + "/" + location.ElementId;
        if (_faceplateIdentity == identity) { RefreshFaceplate(); RefreshReview(); return; }
        _faceplateIdentity = identity; _faceplate.Children.Clear(); _faceplateControl = null;
        _faceValue = null; _faceQuality = null; _reviewPanel = null; _setpoint = null;
        _reviewOff = null; _reviewOn = null; _reviewValue = null; _confirmButton = null; _displayedReview = null;
        _selectedDefinition = Session.GetSelectedObject();
        _faceplate.AddChild(Text("OBJECT ASPECTS", 10, "Muted"));
        if (_selectedDefinition is not { } model)
        {
            var hint = Text("Select equipment in a graphic or the Plant Explorer to inspect its faceplate, trends, alarms and details.", 12, "Muted");
            hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 20, 0, 10); _faceplate.AddChild(hint);
            _faceplate.AddChild(Text("No object selected", 15)); return;
        }
        var title = Text(model.Label.Length > 0 ? model.Label : model.Name, 15); title.Margin = new Thickness(0, 10, 0, 10); title.TextWrapping = TextWrapping.Wrap; _faceplate.AddChild(title);
        var aspects = Row();
        aspects.AddChild(Button("Operate", () => Session.Show(HmiWorkplaceView.Process)));
        aspects.AddChild(Button("Trend", () => Session.Show(HmiWorkplaceView.Trends)));
        aspects.AddChild(Button("Alarms", () => { _objectAlarms = true; Session.Show(HmiWorkplaceView.Alarms); RefreshAlarms(); }));
        aspects.AddChild(Button("Details", () => { if (_faceDetails != null) { _faceDetails.Visibility = _faceDetails.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; _faceplate.InvalidateMeasure(); } }));
        _faceplate.AddChild(HorizontalScroll(aspects));
        var visual = model.Copy(); visual.X = 0; visual.Y = 0; visual.Width = 266; visual.Height = 194;
        // Faceplate graphics are a noninteractive projection of the real component definition.
        visual.Action = new(); visual.Label = ""; visual.Appearance.ShowTagName = false;
        _faceplateControl = HmiControlCatalog.Create(model.Symbol); _faceplateControl.Font = _font; _faceplateControl.ColorScheme = ColorScheme;
        _faceplateControl.ApplyDefinition(visual); _faceplateControl.IsHitTestVisible = false; _faceplateControl.Margin = new Thickness(0, 14, 0, 4);
        _faceplate.AddChild(_faceplateControl);
        _faceValue = Text("", 23); _faceValue.Margin = new Thickness(0, 10, 0, 4); _faceplate.AddChild(_faceValue);
        _faceQuality = Text("", 10, "Muted"); _faceplate.AddChild(_faceQuality);
        _faceDetails = Text($"OBJECT  {model.Name}\nTAG       {(model.Tag.Length > 0 ? model.Tag : "Not bound")}\nRANGE   {model.Minimum.ToString("0.###", CultureInfo.InvariantCulture)} … {model.Maximum.ToString("0.###", CultureInfo.InvariantCulture)} {model.Unit}\nTYPE      {model.Symbol}", 11);
        _faceDetails.TextWrapping = TextWrapping.Wrap; _faceDetails.Margin = new Thickness(0, 20, 0, 15); _faceplate.AddChild(_faceDetails);
        var tag = _project.Tags.FirstOrDefault(t => t.Name == model.Tag && t.Writable);
        var address = new HmiObjectAddress(location.ScreenId, model.Id);
        if (tag?.Type == HmiTagType.Boolean)
        {
            var actions = Row();
            _reviewOff = Button("Review OFF", () => Session.ReviewValue(address, HmiValue.From(false)));
            _reviewOn = Button("Review ON", () => Session.ReviewValue(address, HmiValue.From(true)));
            actions.AddChild(_reviewOff); actions.AddChild(_reviewOn); _faceplate.AddChild(actions);
        }
        else if (tag?.Type == HmiTagType.Number)
        {
            _setpoint = Input("New value (invariant decimal)");
            _setpoint.Text = Session.Runtime.Read(tag.Name).Value.ToString(); _faceplate.AddChild(_setpoint);
            _reviewValue = Button("Review setpoint", () => Session.ReviewValue(address, HmiValue.Parse(_setpoint.Text, HmiTagType.Number)));
            _faceplate.AddChild(_reviewValue);
        }
        _reviewPanel = new Grid { Background = R("Header"), Padding = new Thickness(10), Margin = new Thickness(0, 10), Visibility = Visibility.Collapsed };
        _reviewPanel.RowDefinitions.Add(GridLength.Auto); _reviewPanel.RowDefinitions.Add(GridLength.Auto);
        _reviewDescription = Text("", 12); _reviewDescription.TextWrapping = TextWrapping.Wrap; _reviewPanel.AddChild(_reviewDescription);
        var confirmation = Row(); confirmation.Margin = new Thickness(0, 10, 0, 0);
        _confirmButton = Button("Confirm local", () =>
        {
            if (_displayedReview is not { } review) throw new InvalidOperationException("Review a command first.");
            Session.Confirm(review); SetMessage("Local simulation value applied. No equipment command was sent.");
        });
        confirmation.AddChild(_confirmButton); confirmation.AddChild(Button("Cancel", Session.CancelReview));
        _reviewPanel.AddChild(confirmation); SetRow(confirmation, 1); _faceplate.AddChild(_reviewPanel);
        var pin = Button("Pin / unpin object", () => Session.TogglePin(address)); pin.Margin = new Thickness(0, 13, 0, 10); _faceplate.AddChild(pin);
        var boundary = Text("Commands require explicit local simulation and a separate confirmation. Operator identity text is audit context, not authentication. Acknowledgement does not reset a process condition.", 10, "Muted");
        boundary.TextWrapping = TextWrapping.Wrap; _faceplate.AddChild(boundary);
        RefreshFaceplate(); RefreshReview();
    }
    private void RefreshFaceplate()
    {
        if (_selectedDefinition is not { } e || _faceValue == null || _faceQuality == null || _faceplateControl == null) return;
        _faceplateControl.UpdateState(HmiStateEvaluator.Evaluate(e.States, tag => Session.Runtime.TryRead(tag, out var sample) ? sample : null));
        bool good = Session.Runtime.TryRead(e.Tag, out var value) && value.Quality == HmiQuality.Good;
        if (Session.Runtime.TryRead(e.Tag, out value))
        {
            _faceplateControl.UpdateSample(value, Session.Runtime.GetHistory(e.Tag), now: Session.Runtime.Now);
            _faceValue.Text = good ? value.Value.Type == HmiTagType.Boolean ? value.Value.Boolean ? "ON / RUNNING" : "OFF / STOPPED" : value.Value + " " + e.Unit : "UNKNOWN";
            _faceQuality.Text = $"{value.Quality.ToString().ToUpperInvariant()}   ·   {value.Timestamp:HH:mm:ss.fff} UTC";
        }
        else { _faceValue.Text = "NOT BOUND"; _faceQuality.Text = "No process tag is configured."; }
        _faceQuality.Foreground = R(good ? "Muted" : "Warning");
        bool writable = Session.Mode == HmiWorkplaceMode.Simulation && good && _faceplateControl.VisualTone != HmiVisualTone.Unknown;
        if (_reviewOff != null) _reviewOff.IsEnabled = writable;
        if (_reviewOn != null) _reviewOn.IsEnabled = writable;
        if (_reviewValue != null) _reviewValue.IsEnabled = writable;
    }
    private void RefreshReview()
    {
        if (_reviewPanel == null || _reviewDescription == null || _confirmButton == null) return;
        _displayedReview = Session.PendingReview;
        var visibility = _displayedReview == null ? Visibility.Collapsed : Visibility.Visible;
        if (_reviewPanel.Visibility != visibility)
        {
            _reviewPanel.Visibility = visibility;
            // A never-measured collapsed StackPanel child cannot invalidate its already-measured parent.
            _faceplate.InvalidateMeasure(); _faceplate.InvalidateArrange();
        }
        if (_displayedReview is not { } review) return;
        _reviewDescription.Text = $"REVIEW / LOCAL SIMULATION\n{review.Tag}\nCurrent: {review.ExpectedValue}  →  Requested: {review.Value}\nExpires: {review.ExpiresAt:HH:mm:ss} UTC";
        _confirmButton.IsEnabled = Session.IsReviewCurrent;
    }
    /// <summary>Host clock refresh. No process write, simulation step, or new GPU submission is performed here.</summary>
    public void RefreshClock()
    {
        if (_disposed) return; RefreshReview(); RefreshStatus();
    }
}
