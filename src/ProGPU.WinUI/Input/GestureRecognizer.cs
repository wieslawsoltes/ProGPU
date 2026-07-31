using System.Numerics;
using Windows.Foundation;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Input;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
[Flags]
public enum GestureSettings : uint
{
    None = 0,
    Tap = 1,
    DoubleTap = 2,
    Hold = 4,
    HoldWithMouse = 8,
    RightTap = 16,
    Drag = 32,
    ManipulationTranslateX = 64,
    ManipulationTranslateY = 128,
    ManipulationTranslateRailsX = 256,
    ManipulationTranslateRailsY = 512,
    ManipulationRotate = 1024,
    ManipulationScale = 2048,
    ManipulationTranslateInertia = 4096,
    ManipulationRotateInertia = 8192,
    ManipulationScaleInertia = 16384,
    CrossSlide = 32768,
    ManipulationMultipleFingerPanning = 65536
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum DraggingState
{
    Started,
    Continuing,
    Completed
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum CrossSlidingState
{
    Started,
    Dragging,
    Selecting,
    SelectSpeedBumping,
    SpeedBumping,
    Rearranging,
    Completed
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum HoldingState
{
    Started,
    Completed,
    Canceled
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public struct ManipulationDelta : IEquatable<ManipulationDelta>
{
    public ManipulationDelta(
        Point _Translation,
        float _Scale,
        float _Rotation,
        float _Expansion)
    {
        Translation = _Translation;
        Scale = _Scale;
        Rotation = _Rotation;
        Expansion = _Expansion;
    }

    public Point Translation;
    public float Scale;
    public float Rotation;
    public float Expansion;

    internal static ManipulationDelta Identity => new(new Point(0, 0), 1f, 0f, 0f);
    public readonly bool Equals(ManipulationDelta other) =>
        Translation.Equals(other.Translation) && Scale == other.Scale &&
        Rotation == other.Rotation && Expansion == other.Expansion;
    public override readonly bool Equals(object? obj) => obj is ManipulationDelta other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(Translation, Scale, Rotation, Expansion);
    public static bool operator ==(
        ManipulationDelta x,
        ManipulationDelta y) =>
        x.Equals(y);
    public static bool operator !=(
        ManipulationDelta x,
        ManipulationDelta y) =>
        !x.Equals(y);
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public struct ManipulationVelocities : IEquatable<ManipulationVelocities>
{
    public ManipulationVelocities(
        Point _Linear,
        float _Angular,
        float _Expansion)
    {
        Linear = _Linear;
        Angular = _Angular;
        Expansion = _Expansion;
    }

    public Point Linear;
    public float Angular;
    public float Expansion;

    public readonly bool Equals(ManipulationVelocities other) =>
        Linear.Equals(other.Linear) && Angular == other.Angular && Expansion == other.Expansion;
    public override readonly bool Equals(object? obj) => obj is ManipulationVelocities other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(Linear, Angular, Expansion);
    public static bool operator ==(
        ManipulationVelocities x,
        ManipulationVelocities y) =>
        x.Equals(y);
    public static bool operator !=(
        ManipulationVelocities x,
        ManipulationVelocities y) =>
        !x.Equals(y);
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public struct CrossSlideThresholds : IEquatable<CrossSlideThresholds>
{
    public CrossSlideThresholds(
        float _SelectionStart,
        float _SpeedBumpStart,
        float _SpeedBumpEnd,
        float _RearrangeStart)
    {
        SelectionStart = _SelectionStart;
        SpeedBumpStart = _SpeedBumpStart;
        SpeedBumpEnd = _SpeedBumpEnd;
        RearrangeStart = _RearrangeStart;
    }

    public float SelectionStart;
    public float SpeedBumpStart;
    public float SpeedBumpEnd;
    public float RearrangeStart;

    public readonly bool Equals(CrossSlideThresholds other) =>
        SelectionStart == other.SelectionStart && SpeedBumpStart == other.SpeedBumpStart &&
        SpeedBumpEnd == other.SpeedBumpEnd && RearrangeStart == other.RearrangeStart;
    public override readonly bool Equals(object? obj) => obj is CrossSlideThresholds other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(SelectionStart, SpeedBumpStart, SpeedBumpEnd, RearrangeStart);
    public static bool operator ==(
        CrossSlideThresholds x,
        CrossSlideThresholds y) =>
        x.Equals(y);
    public static bool operator !=(
        CrossSlideThresholds x,
        CrossSlideThresholds y) =>
        !x.Equals(y);
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class MouseWheelParameters
{
    internal MouseWheelParameters()
    {
    }

    public Point CharTranslation { get; set; } = new(8, 16);
    public float DeltaRotationAngle { get; set; } = 15f;
    public float DeltaScale { get; set; } = 1.1f;
    public Point PageTranslation { get; set; } = new(80, 240);
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class TappedEventArgs
{
    internal TappedEventArgs(PointerDeviceType pointerDeviceType, Point position, uint tapCount) =>
        (PointerDeviceType, Position, TapCount) = (pointerDeviceType, position, tapCount);
    public PointerDeviceType PointerDeviceType { get; }
    public Point Position { get; }
    public uint TapCount { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class RightTappedEventArgs
{
    internal RightTappedEventArgs(PointerDeviceType pointerDeviceType, Point position) =>
        (PointerDeviceType, Position) = (pointerDeviceType, position);
    public PointerDeviceType PointerDeviceType { get; }
    public Point Position { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class HoldingEventArgs
{
    internal HoldingEventArgs(PointerDeviceType pointerDeviceType, Point position, HoldingState state) =>
        (PointerDeviceType, Position, HoldingState) = (pointerDeviceType, position, state);
    public HoldingState HoldingState { get; }
    public PointerDeviceType PointerDeviceType { get; }
    public Point Position { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class DraggingEventArgs
{
    internal DraggingEventArgs(PointerDeviceType pointerDeviceType, Point position, DraggingState state) =>
        (PointerDeviceType, Position, DraggingState) = (pointerDeviceType, position, state);
    public DraggingState DraggingState { get; }
    public PointerDeviceType PointerDeviceType { get; }
    public Point Position { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class CrossSlidingEventArgs
{
    internal CrossSlidingEventArgs(PointerDeviceType pointerDeviceType, Point position, CrossSlidingState state) =>
        (PointerDeviceType, Position, CrossSlidingState) = (pointerDeviceType, position, state);
    public CrossSlidingState CrossSlidingState { get; }
    public PointerDeviceType PointerDeviceType { get; }
    public Point Position { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class ManipulationStartedEventArgs
{
    internal ManipulationStartedEventArgs(ManipulationDelta cumulative, PointerDeviceType type, Point position) =>
        (Cumulative, PointerDeviceType, Position) = (cumulative, type, position);
    public ManipulationDelta Cumulative { get; }
    public PointerDeviceType PointerDeviceType { get; }
    public Point Position { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class ManipulationUpdatedEventArgs
{
    internal ManipulationUpdatedEventArgs(ManipulationDelta cumulative, ManipulationDelta delta,
        PointerDeviceType type, Point position, ManipulationVelocities velocities) =>
        (Cumulative, Delta, PointerDeviceType, Position, Velocities) =
        (cumulative, delta, type, position, velocities);
    public ManipulationDelta Cumulative { get; }
    public ManipulationDelta Delta { get; }
    public PointerDeviceType PointerDeviceType { get; }
    public Point Position { get; }
    public ManipulationVelocities Velocities { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class ManipulationInertiaStartingEventArgs
{
    internal ManipulationInertiaStartingEventArgs(ManipulationDelta cumulative, ManipulationDelta delta,
        PointerDeviceType type, Point position, ManipulationVelocities velocities) =>
        (Cumulative, Delta, PointerDeviceType, Position, Velocities) =
        (cumulative, delta, type, position, velocities);
    public ManipulationDelta Cumulative { get; }
    public ManipulationDelta Delta { get; }
    public PointerDeviceType PointerDeviceType { get; }
    public Point Position { get; }
    public ManipulationVelocities Velocities { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class ManipulationCompletedEventArgs
{
    internal ManipulationCompletedEventArgs(ManipulationDelta cumulative, PointerDeviceType type,
        Point position, ManipulationVelocities velocities) =>
        (Cumulative, PointerDeviceType, Position, Velocities) = (cumulative, type, position, velocities);
    public ManipulationDelta Cumulative { get; }
    public PointerDeviceType PointerDeviceType { get; }
    public Point Position { get; }
    public ManipulationVelocities Velocities { get; }
}

/// <summary>
/// Clean-room implementation of the Windows App SDK gesture recognizer contract.
/// Pointer processing is O(P) per sample for P active contacts and stores O(P) state.
/// </summary>
[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class GestureRecognizer
{
    private const float StartThreshold = 4f;
    private const float TapRadius = 12f;
    private const ulong DoubleTapMicroseconds = 500_000;
    private const int HoldMilliseconds = 800;
    private readonly Dictionary<uint, Contact> _contacts = [];
    private readonly SynchronizationContext? _synchronizationContext = SynchronizationContext.Current;
    private CancellationTokenSource? _holdCancellation;
    private CancellationTokenSource? _inertiaCancellation;
    private bool _dragging;
    private bool _crossSliding;
    private CrossSlidingState _crossSlidingState;
    private bool _manipulating;
    private bool _holding;
    private bool _tapCandidate;
    private bool _isInertial;
    private Point _lastTapPosition;
    private PointerDeviceType _lastTapDevice;
    private ulong _lastTapTimestamp;
    private ManipulationDelta _cumulative = ManipulationDelta.Identity;
    private ManipulationDelta _lastDelta = ManipulationDelta.Identity;
    private ManipulationVelocities _velocities;
    private Point _position;
    private PointerDeviceType _deviceType;

    public bool AutoProcessInertia { get; set; } = true;
    public bool CrossSlideExact { get; set; }
    public bool CrossSlideHorizontally { get; set; }
    public CrossSlideThresholds CrossSlideThresholds { get; set; }
    public GestureSettings GestureSettings { get; set; }
    public float InertiaExpansion { get; set; }
    public float InertiaExpansionDeceleration { get; set; }
    public float InertiaRotationAngle { get; set; }
    public float InertiaRotationDeceleration { get; set; }
    public float InertiaTranslationDeceleration { get; set; }
    public float InertiaTranslationDisplacement { get; set; }
    public bool IsActive => _contacts.Count != 0 || _dragging || _crossSliding || _manipulating || IsInertial;
    public bool IsInertial => _isInertial;
    public bool ManipulationExact { get; set; }
    public MouseWheelParameters MouseWheelParameters { get; } = new();
    public Point PivotCenter { get; set; }
    public float PivotRadius { get; set; }
    public bool ShowGestureFeedback { get; set; } = true;

    public event TypedEventHandler<GestureRecognizer, CrossSlidingEventArgs>? CrossSliding;
    public event TypedEventHandler<GestureRecognizer, DraggingEventArgs>? Dragging;
    public event TypedEventHandler<GestureRecognizer, HoldingEventArgs>? Holding;
    public event TypedEventHandler<GestureRecognizer, ManipulationCompletedEventArgs>? ManipulationCompleted;
    public event TypedEventHandler<GestureRecognizer, ManipulationInertiaStartingEventArgs>? ManipulationInertiaStarting;
    public event TypedEventHandler<GestureRecognizer, ManipulationStartedEventArgs>? ManipulationStarted;
    public event TypedEventHandler<GestureRecognizer, ManipulationUpdatedEventArgs>? ManipulationUpdated;
    public event TypedEventHandler<GestureRecognizer, RightTappedEventArgs>? RightTapped;
    public event TypedEventHandler<GestureRecognizer, TappedEventArgs>? Tapped;

    public bool CanBeDoubleTap(PointerPoint value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_lastTapTimestamp == 0 || value.Timestamp < _lastTapTimestamp ||
            value.Timestamp - _lastTapTimestamp > DoubleTapMicroseconds) return false;
        return value.PointerDeviceType == _lastTapDevice &&
            Distance(value.Position, _lastTapPosition) <= TapRadius;
    }

    public void ProcessDownEvent(PointerPoint value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (IsInertial) StopInertia(raiseCompleted: true);
        var contact = new Contact(value);
        _contacts[value.PointerId] = contact;
        _position = value.Position;
        _deviceType = value.PointerDeviceType;
        _tapCandidate = _contacts.Count == 1;
        if (_contacts.Count == 1)
        {
            _cumulative = ManipulationDelta.Identity;
            _lastDelta = ManipulationDelta.Identity;
            _velocities = default;
            StartHolding(contact);
        }
        else
        {
            CancelHolding(raiseCanceled: true);
            _tapCandidate = false;
        }
    }

    public void ProcessMoveEvents(IList<PointerPoint> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        for (var index = 0; index < value.Count; index++) ProcessMove(value[index]);
    }

    public void ProcessUpEvent(PointerPoint value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_contacts.TryGetValue(value.PointerId, out var contact)) return;
        if (Distance(contact.Current.Position, value.Position) > 0f)
        {
            ProcessMove(value);
            if (!_contacts.TryGetValue(value.PointerId, out contact)) return;
        }
        else
        {
            UpdateContact(contact, value);
        }
        _position = value.Position;
        if (Distance(contact.Down.Position, value.Position) > StartThreshold) _tapCandidate = false;

        if (_holding)
        {
            if (HoldingEnabledFor(_deviceType))
                Holding?.Invoke(this, new HoldingEventArgs(_deviceType, _position, HoldingState.Completed));
            if (Has(GestureSettings.RightTap))
                RightTapped?.Invoke(this, new RightTappedEventArgs(_deviceType, _position));
            _holding = false;
        }
        CancelHolding(raiseCanceled: false);

        if (_dragging)
        {
            Dragging?.Invoke(this, new DraggingEventArgs(_deviceType, _position, DraggingState.Completed));
            _dragging = false;
        }
        if (_crossSliding)
        {
            CrossSliding?.Invoke(this, new CrossSlidingEventArgs(_deviceType, _position, CrossSlidingState.Completed));
            _crossSliding = false;
        }

        _contacts.Remove(value.PointerId);
        if (_manipulating && _contacts.Count == 0)
        {
            if (ShouldStartInertia()) StartInertia();
            else CompleteManipulation();
        }
        else if (_contacts.Count > 0)
        {
            ResetContactBaselines();
        }

        if (_contacts.Count == 0 && !_manipulating && !_holding && !_dragging && _tapCandidate)
        {
            CompleteTap(value, contact.Down.Properties.IsRightButtonPressed ||
                contact.Down.Properties.IsBarrelButtonPressed);
        }
        _tapCandidate = false;
    }

    public void ProcessMouseWheelEvent(PointerPoint value, bool isShiftKeyDown, bool isControlKeyDown)
    {
        ArgumentNullException.ThrowIfNull(value);
        int wheel = value.Properties.MouseWheelDelta;
        if (wheel == 0) return;
        float steps = wheel / 120f;
        var delta = ManipulationDelta.Identity;
        if (isControlKeyDown && Has(GestureSettings.ManipulationScale))
        {
            delta.Scale = MathF.Pow(Math.Max(0.001f, MouseWheelParameters.DeltaScale), steps);
        }
        else if (HasTranslation)
        {
            Point configured = MouseWheelParameters.CharTranslation;
            const float systemLinesPerDetent = 3f;
            bool horizontal = isShiftKeyDown || value.Properties.IsHorizontalMouseWheel;
            delta.Translation = horizontal
                ? new Point(configured.X * steps * systemLinesPerDetent, 0)
                : new Point(0, configured.Y * steps * systemLinesPerDetent);
            delta = FilterDelta(delta, 1);
        }
        else if (Has(GestureSettings.ManipulationRotate))
        {
            delta.Rotation = MouseWheelParameters.DeltaRotationAngle * steps;
        }
        else return;

        _deviceType = value.PointerDeviceType;
        _position = value.Position;
        _cumulative = ManipulationDelta.Identity;
        _manipulating = true;
        ManipulationStarted?.Invoke(this, new ManipulationStartedEventArgs(_cumulative, _deviceType, _position));
        Accumulate(delta);
        var velocities = default(ManipulationVelocities);
        ManipulationUpdated?.Invoke(this, new ManipulationUpdatedEventArgs(_cumulative, delta, _deviceType, _position, velocities));
        CompleteManipulation();
    }

    public void ProcessInertia()
    {
        if (!IsInertial) return;
        const float elapsedMilliseconds = 16f;
        Vector2 linear = ToVector(_velocities.Linear);
        float angular = _velocities.Angular;
        float expansion = _velocities.Expansion;

        linear = Decelerate(linear, ResolveTranslationDeceleration() * elapsedMilliseconds);
        angular = Decelerate(angular, ResolveRotationDeceleration() * elapsedMilliseconds);
        expansion = Decelerate(expansion, ResolveExpansionDeceleration() * elapsedMilliseconds);
        _velocities = new ManipulationVelocities(ToPoint(linear), angular, expansion);

        var delta = FilterDelta(new ManipulationDelta(
            ToPoint(linear * elapsedMilliseconds),
            ScaleFromExpansion(expansion * elapsedMilliseconds),
            angular * elapsedMilliseconds,
            expansion * elapsedMilliseconds), 0);
        Accumulate(delta);
        _lastDelta = delta;
        ManipulationUpdated?.Invoke(this,
            new ManipulationUpdatedEventArgs(_cumulative, delta, _deviceType, _position, _velocities));

        if (linear.LengthSquared() < 0.0001f && MathF.Abs(angular) < 0.001f && MathF.Abs(expansion) < 0.001f)
        {
            StopInertia(raiseCompleted: true);
        }
    }

    public void CompleteGesture()
    {
        CancelHolding(raiseCanceled: true);
        if (_dragging)
            Dragging?.Invoke(this, new DraggingEventArgs(_deviceType, _position, DraggingState.Completed));
        if (_crossSliding)
            CrossSliding?.Invoke(this, new CrossSlidingEventArgs(_deviceType, _position, CrossSlidingState.Completed));
        _dragging = false;
        _crossSliding = false;
        _contacts.Clear();
        StopInertia(raiseCompleted: _manipulating || IsInertial);
        if (_manipulating) CompleteManipulation();
        _tapCandidate = false;
    }

    private void ProcessMove(PointerPoint value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_contacts.TryGetValue(value.PointerId, out var contact)) return;
        var before = Snapshot();
        UpdateContact(contact, value);
        var after = Snapshot();
        _position = value.Position;
        _deviceType = value.PointerDeviceType;

        if (Distance(contact.Down.Position, value.Position) > StartThreshold)
        {
            _tapCandidate = false;
            CancelHolding(raiseCanceled: true);
        }

        ProcessDrag(contact, value);
        ProcessCrossSlide(contact, value);
        ProcessManipulation(before, after, value.Timestamp);
    }

    private void ProcessDrag(Contact contact, PointerPoint value)
    {
        if (!Has(GestureSettings.Drag) || _contacts.Count != 1 ||
            value.PointerDeviceType is not (PointerDeviceType.Mouse or PointerDeviceType.Pen)) return;
        if (!_dragging && Distance(contact.Down.Position, value.Position) > StartThreshold)
        {
            _dragging = true;
            Dragging?.Invoke(this, new DraggingEventArgs(_deviceType, _position, DraggingState.Started));
        }
        else if (_dragging)
        {
            Dragging?.Invoke(this, new DraggingEventArgs(_deviceType, _position, DraggingState.Continuing));
        }
    }

    private void ProcessCrossSlide(Contact contact, PointerPoint value)
    {
        if (!Has(GestureSettings.CrossSlide) || _contacts.Count != 1 ||
            value.PointerDeviceType != PointerDeviceType.Touch) return;
        Vector2 movement = ToVector(value.Position) - ToVector(contact.Down.Position);
        float cross = MathF.Abs(CrossSlideHorizontally ? movement.X : movement.Y);
        float along = MathF.Abs(CrossSlideHorizontally ? movement.Y : movement.X);
        if (cross <= along || cross < (CrossSlideExact ? 0f : StartThreshold)) return;

        CrossSlideThresholds thresholds = EffectiveCrossSlideThresholds();
        if (thresholds.SelectionStart <= 0f && thresholds.SpeedBumpStart <= 0f &&
            thresholds.SpeedBumpEnd <= 0f && thresholds.RearrangeStart <= 0f) return;
        var state = cross >= thresholds.RearrangeStart ? CrossSlidingState.Rearranging
            : cross >= thresholds.SpeedBumpEnd ? CrossSlidingState.SpeedBumping
            : cross >= thresholds.SpeedBumpStart ? CrossSlidingState.SelectSpeedBumping
            : cross >= thresholds.SelectionStart ? CrossSlidingState.Selecting
            : CrossSlidingState.Dragging;
        if (!_crossSliding)
        {
            _crossSliding = true;
            _crossSlidingState = CrossSlidingState.Started;
            CrossSliding?.Invoke(this, new CrossSlidingEventArgs(_deviceType, _position, CrossSlidingState.Started));
        }
        if (_crossSlidingState != state)
        {
            _crossSlidingState = state;
            CrossSliding?.Invoke(this, new CrossSlidingEventArgs(_deviceType, _position, state));
        }
    }

    private void ProcessManipulation(ContactSnapshot before, ContactSnapshot after, ulong timestamp)
    {
        if (!HasManipulation || after.Count == 0 || _dragging) return;
        Vector2 translation = after.Center - before.Center;
        float scale = after.Count >= 2 && before.Radius > 0.001f ? after.Radius / before.Radius : 1f;
        float rotation = NormalizeDegrees(after.Angle - before.Angle);
        float expansion = after.Count >= 2 ? (after.Radius - before.Radius) * 2f : 0f;
        var delta = FilterDelta(new ManipulationDelta(ToPoint(translation), scale, rotation, expansion), after.Count);
        Vector2 filteredTranslation = ToVector(delta.Translation);
        bool changed = filteredTranslation.LengthSquared() > 0f || MathF.Abs(delta.Scale - 1f) > 0.00001f ||
            MathF.Abs(delta.Rotation) > 0.00001f || MathF.Abs(delta.Expansion) > 0.00001f;
        if (!changed) return;

        if (!_manipulating)
        {
            float threshold = ManipulationExact ? 0f : StartThreshold;
            if (filteredTranslation.Length() < threshold && MathF.Abs(delta.Expansion) < threshold &&
                MathF.Abs(delta.Rotation) < 1f && MathF.Abs(delta.Scale - 1f) < 0.01f) return;
            _manipulating = true;
            CancelHolding(raiseCanceled: true);
            _tapCandidate = false;
            ManipulationStarted?.Invoke(this,
                new ManipulationStartedEventArgs(_cumulative, _deviceType, ToPoint(after.Center)));
        }

        float elapsedMs = Math.Max(0.001f, (timestamp - before.Timestamp) / 1000f);
        _velocities = new ManipulationVelocities(
            ToPoint(ToVector(delta.Translation) / elapsedMs),
            delta.Rotation / elapsedMs,
            delta.Expansion / elapsedMs);
        _lastDelta = delta;
        Accumulate(delta);
        _position = ToPoint(after.Center);
        ManipulationUpdated?.Invoke(this,
            new ManipulationUpdatedEventArgs(_cumulative, delta, _deviceType, _position, _velocities));
    }

    private ManipulationDelta FilterDelta(ManipulationDelta delta, int contactCount)
    {
        bool translateX = Has(GestureSettings.ManipulationTranslateX) || Has(GestureSettings.ManipulationTranslateRailsX);
        bool translateY = Has(GestureSettings.ManipulationTranslateY) || Has(GestureSettings.ManipulationTranslateRailsY);
        var translation = new Vector2(
            translateX ? (float)delta.Translation.X : 0f,
            translateY ? (float)delta.Translation.Y : 0f);
        if (Has(GestureSettings.ManipulationTranslateRailsX) && MathF.Abs(translation.X) >= MathF.Abs(translation.Y))
            translation.Y = 0f;
        if (Has(GestureSettings.ManipulationTranslateRailsY) && MathF.Abs(translation.Y) >= MathF.Abs(translation.X))
            translation.X = 0f;
        bool panOnly = contactCount >= 2 && Has(GestureSettings.ManipulationMultipleFingerPanning);
        return new ManipulationDelta(
            ToPoint(translation),
            !panOnly && Has(GestureSettings.ManipulationScale) ? delta.Scale : 1f,
            !panOnly && Has(GestureSettings.ManipulationRotate) ? delta.Rotation : 0f,
            !panOnly && Has(GestureSettings.ManipulationScale) ? delta.Expansion : 0f);
    }

    private void CompleteTap(PointerPoint value, bool startedAsRightTap)
    {
        bool rightButton = startedAsRightTap || value.Properties.IsRightButtonPressed ||
            value.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased;
        if (rightButton && Has(GestureSettings.RightTap))
        {
            RightTapped?.Invoke(this, new RightTappedEventArgs(value.PointerDeviceType, value.Position));
            return;
        }

        bool doubleTap = Has(GestureSettings.DoubleTap) && CanBeDoubleTap(value);
        if (doubleTap)
        {
            Tapped?.Invoke(this, new TappedEventArgs(value.PointerDeviceType, value.Position, 2));
            _lastTapTimestamp = 0;
        }
        else
        {
            if (Has(GestureSettings.Tap))
                Tapped?.Invoke(this, new TappedEventArgs(value.PointerDeviceType, value.Position, 1));
            if (Has(GestureSettings.DoubleTap))
            {
                _lastTapPosition = value.Position;
                _lastTapDevice = value.PointerDeviceType;
                _lastTapTimestamp = value.Timestamp;
            }
        }
    }

    private void StartHolding(Contact contact)
    {
        bool enabled = HoldingEnabledFor(contact.Down.PointerDeviceType) || Has(GestureSettings.RightTap);
        if (!enabled) return;
        CancelHolding(raiseCanceled: false);
        var cancellation = _holdCancellation = new CancellationTokenSource();
        _ = WaitForHoldAsync(contact.Down.PointerId, cancellation);
    }

    private async Task WaitForHoldAsync(uint pointerId, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(HoldMilliseconds, cancellation.Token).ConfigureAwait(false);
            Post(() =>
            {
                if (cancellation.IsCancellationRequested || !_contacts.TryGetValue(pointerId, out var contact)) return;
                _holding = true;
                _tapCandidate = false;
                _position = contact.Current.Position;
                if (HoldingEnabledFor(_deviceType))
                    Holding?.Invoke(this, new HoldingEventArgs(_deviceType, _position, HoldingState.Started));
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelHolding(bool raiseCanceled)
    {
        _holdCancellation?.Cancel();
        _holdCancellation?.Dispose();
        _holdCancellation = null;
        if (!_holding) return;
        if (raiseCanceled && HoldingEnabledFor(_deviceType))
            Holding?.Invoke(this, new HoldingEventArgs(_deviceType, _position, HoldingState.Canceled));
        _holding = false;
    }

    private bool ShouldStartInertia()
    {
        Vector2 linear = ToVector(_velocities.Linear);
        return (Has(GestureSettings.ManipulationTranslateInertia) && linear.LengthSquared() > 0.0025f) ||
            (Has(GestureSettings.ManipulationRotateInertia) && MathF.Abs(_velocities.Angular) > 0.01f) ||
            (Has(GestureSettings.ManipulationScaleInertia) && MathF.Abs(_velocities.Expansion) > 0.01f);
    }

    private void StartInertia()
    {
        _isInertial = true;
        ManipulationInertiaStarting?.Invoke(this,
            new ManipulationInertiaStartingEventArgs(_cumulative, _lastDelta, _deviceType, _position, _velocities));
        if (!AutoProcessInertia) return;
        var cancellation = _inertiaCancellation = new CancellationTokenSource();
        _ = RunInertiaAsync(cancellation);
    }

    private async Task RunInertiaAsync(CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested && IsInertial)
            {
                await Task.Delay(16, cancellation.Token).ConfigureAwait(false);
                Post(ProcessInertia);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StopInertia(bool raiseCompleted)
    {
        _inertiaCancellation?.Cancel();
        _inertiaCancellation?.Dispose();
        _inertiaCancellation = null;
        bool wasInertial = IsInertial;
        _isInertial = false;
        if (raiseCompleted && (wasInertial || _manipulating)) CompleteManipulation();
    }

    private void CompleteManipulation()
    {
        if (!_manipulating) return;
        _manipulating = false;
        ManipulationCompleted?.Invoke(this,
            new ManipulationCompletedEventArgs(_cumulative, _deviceType, _position, _velocities));
    }

    private void Accumulate(ManipulationDelta delta)
    {
        Vector2 translation = ToVector(_cumulative.Translation) + ToVector(delta.Translation);
        _cumulative = new ManipulationDelta(
            ToPoint(translation),
            _cumulative.Scale * delta.Scale,
            _cumulative.Rotation + delta.Rotation,
            _cumulative.Expansion + delta.Expansion);
    }

    private ContactSnapshot Snapshot()
    {
        if (_contacts.Count == 0) return default;
        Vector2 center = Vector2.Zero;
        ulong timestamp = 0;
        foreach (var contact in _contacts.Values)
        {
            center += ToVector(contact.Current.Position);
            timestamp = Math.Max(timestamp, contact.Current.Timestamp);
        }
        center /= _contacts.Count;
        float radius = 0f;
        float angle = 0f;
        if (_contacts.Count >= 2)
        {
            using var enumerator = _contacts.Values.GetEnumerator();
            enumerator.MoveNext();
            Vector2 first = ToVector(enumerator.Current.Current.Position);
            enumerator.MoveNext();
            Vector2 second = ToVector(enumerator.Current.Current.Position);
            radius = Vector2.Distance(first, second) * 0.5f;
            Vector2 axis = second - first;
            angle = MathF.Atan2(axis.Y, axis.X) * (180f / MathF.PI);
        }
        else if (_contacts.Count == 1 && PivotRadius > 0f && Has(GestureSettings.ManipulationRotate))
        {
            foreach (var contact in _contacts.Values)
            {
                Vector2 axis = ToVector(contact.Current.Position) - ToVector(PivotCenter);
                angle = MathF.Atan2(axis.Y, axis.X) * (180f / MathF.PI);
                break;
            }
            radius = PivotRadius;
        }
        return new ContactSnapshot(_contacts.Count, center, radius, angle, timestamp);
    }

    private void ResetContactBaselines()
    {
        foreach (var contact in _contacts.Values) contact.Previous = contact.Current;
    }

    private static void UpdateContact(Contact contact, PointerPoint value)
    {
        contact.Previous = contact.Current;
        contact.Current = value;
    }

    private CrossSlideThresholds EffectiveCrossSlideThresholds()
    {
        return CrossSlideThresholds;
    }

    private float ResolveTranslationDeceleration()
    {
        if (InertiaTranslationDeceleration > 0f) return InertiaTranslationDeceleration;
        float speed = ToVector(_velocities.Linear).Length();
        if (InertiaTranslationDisplacement > 0f) return speed * speed / (2f * InertiaTranslationDisplacement);
        return 0.0025f;
    }

    private float ResolveRotationDeceleration()
    {
        if (InertiaRotationDeceleration > 0f) return InertiaRotationDeceleration;
        if (InertiaRotationAngle > 0f) return _velocities.Angular * _velocities.Angular / (2f * InertiaRotationAngle);
        return 0.00015f;
    }

    private float ResolveExpansionDeceleration()
    {
        if (InertiaExpansionDeceleration > 0f) return InertiaExpansionDeceleration;
        if (InertiaExpansion > 0f) return _velocities.Expansion * _velocities.Expansion / (2f * InertiaExpansion);
        return 0.00015f;
    }

    private bool Has(GestureSettings setting) => (GestureSettings & setting) != 0;
    private bool HoldingEnabledFor(PointerDeviceType type) => type == PointerDeviceType.Mouse
        ? Has(GestureSettings.HoldWithMouse)
        : Has(GestureSettings.Hold);
    private bool HasTranslation => Has(GestureSettings.ManipulationTranslateX) ||
        Has(GestureSettings.ManipulationTranslateY) || Has(GestureSettings.ManipulationTranslateRailsX) ||
        Has(GestureSettings.ManipulationTranslateRailsY);
    private bool HasManipulation => HasTranslation || Has(GestureSettings.ManipulationRotate) ||
        Has(GestureSettings.ManipulationScale);
    private static Vector2 ToVector(Point point) => new((float)point.X, (float)point.Y);
    private static Point ToPoint(Vector2 vector) => new(vector.X, vector.Y);
    private static float Distance(Point left, Point right) => Vector2.Distance(ToVector(left), ToVector(right));
    private static float NormalizeDegrees(float value) => MathF.IEEERemainder(value, 360f);
    private static float ScaleFromExpansion(float expansion) => Math.Max(0.001f, 1f + expansion / 100f);
    private static Vector2 Decelerate(Vector2 value, float amount)
    {
        float length = value.Length();
        return length <= amount || length == 0f ? Vector2.Zero : value * ((length - amount) / length);
    }
    private static float Decelerate(float value, float amount) =>
        MathF.Abs(value) <= amount ? 0f : value - MathF.CopySign(amount, value);
    private void Post(Action action)
    {
        if (_synchronizationContext == null) action();
        else _synchronizationContext.Post(static state => ((Action)state!).Invoke(), action);
    }

    private sealed class Contact(PointerPoint down)
    {
        public PointerPoint Down { get; } = down;
        public PointerPoint Previous { get; set; } = down;
        public PointerPoint Current { get; set; } = down;
    }

    private readonly record struct ContactSnapshot(
        int Count,
        Vector2 Center,
        float Radius,
        float Angle,
        ulong Timestamp);
}
