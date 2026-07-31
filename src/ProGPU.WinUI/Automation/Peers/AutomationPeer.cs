using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Foundation;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation.Peers;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public class AutomationPeer : DependencyObject
{
    private static long s_nextRuntimeId;

    private AutomationPeer? _eventsSource;
    private AutomationPeer? _parent;
    private IRawElementProviderSimple? _provider;

    protected AutomationPeer()
    {
    }

    protected internal AutomationPeer(
        WinRT.IObjectReference objRef)
    {
        ArgumentNullException.ThrowIfNull(objRef);
    }

    protected AutomationPeer(
        WinRT.DerivedComposed _)
    {
        ArgumentNullException.ThrowIfNull(_);
    }

    public AutomationPeer EventsSource
    {
        get => _eventsSource!;
        set => _eventsSource = value;
    }

    public static RawElementProviderRuntimeId
        GenerateRawElementProviderRuntimeId()
    {
        var id = unchecked((ulong)Interlocked.Increment(
            ref s_nextRuntimeId));
        return new RawElementProviderRuntimeId(
            (uint)(id >> 32),
            (uint)id);
    }

    public static bool ListenerExists(
        AutomationEvents eventId) =>
        AutomationPeerEventRuntime.ListenerExists(eventId);

    public string GetAcceleratorKey() =>
        GetAcceleratorKeyCore();

    public string GetAccessKey() =>
        GetAccessKeyCore();

    public IList<AutomationPeerAnnotation> GetAnnotations() =>
        GetAnnotationsCore();

    public AutomationControlType GetAutomationControlType() =>
        GetAutomationControlTypeCore();

    public string GetAutomationId() =>
        GetAutomationIdCore();

    public Rect GetBoundingRectangle() =>
        GetBoundingRectangleCore();

    public IList<AutomationPeer> GetChildren() =>
        GetChildrenCore();

    public string GetClassName() =>
        GetClassNameCore();

    public Point GetClickablePoint() =>
        GetClickablePointCore();

    public IReadOnlyList<AutomationPeer>
        GetControlledPeers() =>
        GetControlledPeersCore();

    public int GetCulture() =>
        GetCultureCore();

    public object GetElementFromPoint(
        Point pointInWindowCoordinates) =>
        GetElementFromPointCore(
            pointInWindowCoordinates);

    public object GetFocusedElement() =>
        GetFocusedElementCore();

    public string GetFullDescription() =>
        GetFullDescriptionCore();

    public AutomationHeadingLevel GetHeadingLevel() =>
        GetHeadingLevelCore();

    public string GetHelpText() =>
        GetHelpTextCore();

    public string GetItemStatus() =>
        GetItemStatusCore();

    public string GetItemType() =>
        GetItemTypeCore();

    public AutomationPeer GetLabeledBy() =>
        GetLabeledByCore();

    public AutomationLandmarkType GetLandmarkType() =>
        GetLandmarkTypeCore();

    public int GetLevel() =>
        GetLevelCore();

    public AutomationLiveSetting GetLiveSetting() =>
        GetLiveSettingCore();

    public string GetLocalizedControlType() =>
        GetLocalizedControlTypeCore();

    public string GetLocalizedLandmarkType() =>
        GetLocalizedLandmarkTypeCore();

    public string GetName() =>
        GetNameCore();

    public AutomationOrientation GetOrientation() =>
        GetOrientationCore();

    public AutomationPeer GetParent() =>
        _parent!;

    public object GetPattern(
        PatternInterface patternInterface) =>
        GetPatternCore(patternInterface);

    public AutomationPeer GetPeerFromPoint(
        Point point) =>
        GetPeerFromPointCore(point);

    public int GetPositionInSet() =>
        GetPositionInSetCore();

    public int GetSizeOfSet() =>
        GetSizeOfSetCore();

    public bool HasKeyboardFocus() =>
        HasKeyboardFocusCore();

    public void InvalidatePeer()
        => AutomationPeerEventRuntime.InvalidatePeer(this);

    public bool IsContentElement() =>
        IsContentElementCore();

    public bool IsControlElement() =>
        IsControlElementCore();

    public bool IsDataValidForForm() =>
        IsDataValidForFormCore();

    public bool IsDialog() =>
        IsDialogCore();

    public bool IsEnabled() =>
        IsEnabledCore();

    public bool IsKeyboardFocusable() =>
        IsKeyboardFocusableCore();

    public bool IsOffscreen() =>
        IsOffscreenCore();

    public bool IsPassword() =>
        IsPasswordCore();

    public bool IsPeripheral() =>
        IsPeripheralCore();

    public bool IsRequiredForForm() =>
        IsRequiredForFormCore();

    public object Navigate(
        AutomationNavigationDirection direction) =>
        NavigateCore(direction);

    public void RaiseAutomationEvent(
        AutomationEvents eventId) =>
        AutomationPeerEventRuntime.RaiseAutomationEvent(
            this,
            eventId);

    public void RaiseNotificationEvent(
        AutomationNotificationKind notificationKind,
        AutomationNotificationProcessing notificationProcessing,
        string displayString,
        string activityId) =>
        AutomationPeerEventRuntime.RaiseNotificationEvent(
            this,
            notificationKind,
            notificationProcessing,
            displayString,
            activityId);

    public void RaisePropertyChangedEvent(
        AutomationProperty automationProperty,
        object oldValue,
        object newValue) =>
        AutomationPeerEventRuntime.RaisePropertyChangedEvent(
            this,
            automationProperty,
            oldValue,
            newValue);

    public void RaiseStructureChangedEvent(
        AutomationStructureChangeType structureChangeType,
        AutomationPeer child) =>
        AutomationPeerEventRuntime.RaiseStructureChangedEvent(
            this,
            structureChangeType,
            child);

    public void RaiseTextEditTextChangedEvent(
        AutomationTextEditChangeType automationTextEditChangeType,
        IReadOnlyList<string> changedData) =>
        AutomationPeerEventRuntime.RaiseTextEditTextChangedEvent(
            this,
            automationTextEditChangeType,
            changedData);

    public void SetFocus() =>
        SetFocusCore();

    public void SetParent(
        AutomationPeer peer) =>
        _parent = peer;

    public void ShowContextMenu() =>
        ShowContextMenuCore();

    protected IRawElementProviderSimple ProviderFromPeer(
        AutomationPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var provider = Volatile.Read(ref peer._provider);
        if (provider != null)
            return provider;

        var created = new IRawElementProviderSimple(peer);
        return Interlocked.CompareExchange(
            ref peer._provider,
            created,
            null) ?? created;
    }

    protected AutomationPeer PeerFromProvider(
        IRawElementProviderSimple provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.Peer;
    }

    protected virtual string GetAcceleratorKeyCore() =>
        string.Empty;

    protected virtual string GetAccessKeyCore() =>
        string.Empty;

    protected virtual IList<AutomationPeerAnnotation>
        GetAnnotationsCore() =>
        null!;

    protected virtual AutomationControlType
        GetAutomationControlTypeCore() =>
        this is RichEditBoxAutomationPeer richEditPeer
            ? richEditPeer.GetAutomationControlTypeValue()
            : AutomationControlType.Custom;

    protected virtual string GetAutomationIdCore() =>
        string.Empty;

    protected virtual Rect GetBoundingRectangleCore() =>
        default;

    protected virtual IList<AutomationPeer>
        GetChildrenCore() =>
        null!;

    protected virtual string GetClassNameCore() =>
        this is FrameworkElementAutomationPeer frameworkPeer
            ? frameworkPeer.GetClassNameValue()
            : string.Empty;

    protected virtual Point GetClickablePointCore() =>
        default;

    protected virtual IReadOnlyList<AutomationPeer>
        GetControlledPeersCore() =>
        null!;

    protected virtual int GetCultureCore() =>
        0;

    protected virtual IEnumerable<AutomationPeer>
        GetDescribedByCore() =>
        null!;

    protected virtual object GetElementFromPointCore(
        Point pointInWindowCoordinates) =>
        GetPeerFromPointCore(
            pointInWindowCoordinates)!;

    protected virtual IEnumerable<AutomationPeer>
        GetFlowsFromCore() =>
        null!;

    protected virtual IEnumerable<AutomationPeer>
        GetFlowsToCore() =>
        null!;

    protected virtual object GetFocusedElementCore() =>
        null!;

    protected virtual string GetFullDescriptionCore() =>
        string.Empty;

    protected virtual AutomationHeadingLevel
        GetHeadingLevelCore() =>
        AutomationHeadingLevel.None;

    protected virtual string GetHelpTextCore() =>
        string.Empty;

    protected virtual string GetItemStatusCore() =>
        string.Empty;

    protected virtual string GetItemTypeCore() =>
        string.Empty;

    protected virtual AutomationPeer GetLabeledByCore() =>
        null!;

    protected virtual AutomationLandmarkType
        GetLandmarkTypeCore() =>
        AutomationLandmarkType.None;

    protected virtual int GetLevelCore() =>
        0;

    protected virtual AutomationLiveSetting
        GetLiveSettingCore() =>
        AutomationLiveSetting.Off;

    protected virtual string GetLocalizedControlTypeCore() =>
        string.Empty;

    protected virtual string GetLocalizedLandmarkTypeCore() =>
        string.Empty;

    protected virtual string GetNameCore() =>
        this is RichEditBoxAutomationPeer richEditPeer
            ? richEditPeer.GetNameValue()
            : string.Empty;

    protected virtual AutomationOrientation
        GetOrientationCore() =>
        AutomationOrientation.None;

    protected virtual object GetPatternCore(
        PatternInterface patternInterface) =>
        this is RichEditBoxAutomationPeer richEditPeer
            ? richEditPeer.GetPatternValue(patternInterface)
            : null!;

    protected virtual AutomationPeer GetPeerFromPointCore(
        Point point) =>
        null!;

    protected virtual int GetPositionInSetCore() =>
        0;

    protected virtual int GetSizeOfSetCore() =>
        0;

    protected virtual bool HasKeyboardFocusCore() =>
        this is FrameworkElementAutomationPeer frameworkPeer &&
        frameworkPeer.HasKeyboardFocusValue();

    protected virtual bool IsContentElementCore() =>
        this is FrameworkElementAutomationPeer;

    protected virtual bool IsControlElementCore() =>
        this is FrameworkElementAutomationPeer;

    protected virtual bool IsDataValidForFormCore() =>
        true;

    protected virtual bool IsDialogCore() =>
        false;

    protected virtual bool IsEnabledCore() =>
        this is not FrameworkElementAutomationPeer frameworkPeer ||
        frameworkPeer.IsEnabledValue();

    protected virtual bool IsKeyboardFocusableCore() =>
        this is FrameworkElementAutomationPeer frameworkPeer &&
        frameworkPeer.IsKeyboardFocusableValue();

    protected virtual bool IsOffscreenCore() =>
        this is FrameworkElementAutomationPeer frameworkPeer &&
        frameworkPeer.IsOffscreenValue();

    protected virtual bool IsPasswordCore() =>
        false;

    protected virtual bool IsPeripheralCore() =>
        false;

    protected virtual bool IsRequiredForFormCore() =>
        false;

    protected virtual object NavigateCore(
        AutomationNavigationDirection direction)
    {
        if (direction ==
            AutomationNavigationDirection.Parent)
        {
            return _parent!;
        }

        if (direction ==
                AutomationNavigationDirection.FirstChild ||
            direction ==
                AutomationNavigationDirection.LastChild)
        {
            var children = GetChildrenCore();
            if (children == null || children.Count == 0)
                return null!;
            return direction ==
                AutomationNavigationDirection.FirstChild
                    ? children[0]
                    : children[children.Count - 1];
        }

        var siblings = _parent?.GetChildrenCore();
        if (siblings == null)
            return null!;
        for (var index = 0;
             index < siblings.Count;
             index++)
        {
            if (!ReferenceEquals(siblings[index], this))
                continue;
            var siblingIndex = direction ==
                AutomationNavigationDirection.NextSibling
                    ? index + 1
                    : index - 1;
            return siblingIndex >= 0 &&
                siblingIndex < siblings.Count
                    ? siblings[siblingIndex]
                    : null!;
        }

        return null!;
    }

    protected virtual void SetFocusCore()
    {
    }

    protected virtual void ShowContextMenuCore()
    {
    }
}

internal static class AutomationPeerEventRuntime
{
    internal static Action<AutomationPeer>?
        PeerInvalidated;

    internal static Func<AutomationEvents, bool>?
        ListenerProbe;

    internal static Action<AutomationPeer, AutomationEvents>?
        AutomationEventRaised;

    internal static Action<
        AutomationPeer,
        AutomationNotificationKind,
        AutomationNotificationProcessing,
        string,
        string>?
        NotificationEventRaised;

    internal static Action<
        AutomationPeer,
        AutomationProperty,
        object,
        object>?
        PropertyChangedEventRaised;

    internal static Action<
        AutomationPeer,
        AutomationStructureChangeType,
        AutomationPeer>?
        StructureChangedEventRaised;

    internal static Action<
        AutomationPeer,
        AutomationTextEditChangeType,
        IReadOnlyList<string>>?
        TextEditTextChangedEventRaised;

    internal static bool ListenerExists(
        AutomationEvents eventId)
    {
        var probe = Volatile.Read(
            ref ListenerProbe);
        if (probe?.Invoke(eventId) == true)
            return true;
        if (Volatile.Read(
                ref AutomationEventRaised) != null)
        {
            return true;
        }

        return eventId switch
        {
            AutomationEvents.PropertyChanged =>
                Volatile.Read(
                    ref PropertyChangedEventRaised) != null,
            AutomationEvents.StructureChanged =>
                Volatile.Read(
                    ref StructureChangedEventRaised) != null,
            AutomationEvents.TextEditTextChanged =>
                Volatile.Read(
                    ref TextEditTextChangedEventRaised) != null,
            _ => false
        };
    }

    internal static void InvalidatePeer(
        AutomationPeer peer) =>
        Volatile.Read(ref PeerInvalidated)?
            .Invoke(peer);

    internal static void RaiseAutomationEvent(
        AutomationPeer peer,
        AutomationEvents eventId) =>
        Volatile.Read(
            ref AutomationEventRaised)?
            .Invoke(peer, eventId);

    internal static void RaiseNotificationEvent(
        AutomationPeer peer,
        AutomationNotificationKind notificationKind,
        AutomationNotificationProcessing notificationProcessing,
        string displayString,
        string activityId) =>
        Volatile.Read(
            ref NotificationEventRaised)?
            .Invoke(
                peer,
                notificationKind,
                notificationProcessing,
                displayString,
                activityId);

    internal static void RaisePropertyChangedEvent(
        AutomationPeer peer,
        AutomationProperty automationProperty,
        object oldValue,
        object newValue)
    {
        Volatile.Read(
            ref PropertyChangedEventRaised)?
            .Invoke(
                peer,
                automationProperty,
                oldValue,
                newValue);
        RaiseAutomationEvent(
            peer,
            AutomationEvents.PropertyChanged);
    }

    internal static void RaiseStructureChangedEvent(
        AutomationPeer peer,
        AutomationStructureChangeType structureChangeType,
        AutomationPeer child)
    {
        Volatile.Read(
            ref StructureChangedEventRaised)?
            .Invoke(
                peer,
                structureChangeType,
                child);
        RaiseAutomationEvent(
            peer,
            AutomationEvents.StructureChanged);
    }

    internal static void RaiseTextEditTextChangedEvent(
        AutomationPeer peer,
        AutomationTextEditChangeType changeType,
        IReadOnlyList<string> changedData)
    {
        Volatile.Read(
            ref TextEditTextChangedEventRaised)?
            .Invoke(
                peer,
                changeType,
                changedData);
        RaiseAutomationEvent(
            peer,
            AutomationEvents.TextEditTextChanged);
    }

    internal static void Reset()
    {
        Volatile.Write(ref PeerInvalidated, null);
        Volatile.Write(ref ListenerProbe, null);
        Volatile.Write(ref AutomationEventRaised, null);
        Volatile.Write(ref NotificationEventRaised, null);
        Volatile.Write(ref PropertyChangedEventRaised, null);
        Volatile.Write(ref StructureChangedEventRaised, null);
        Volatile.Write(ref TextEditTextChangedEventRaised, null);
    }
}
