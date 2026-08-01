using Microsoft.UI.Dispatching;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Content;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010007)]
public enum ContentAutomationOptions
{
    None = 0,
    FrameworkBased = 1,
    FragmentBased = 2
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public enum ContentCoordinateRoundingMode
{
    Auto = 0,
    Floor = 1,
    Round = 2,
    Ceiling = 3
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public enum ContentSizePolicy
{
    None = 0,
    ResizeContentToParentWindow = 1,
    ResizeParentWindowToContent = 2
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00020000)]
public enum PopupAnchor
{
    None = 0,
    TopLevelWindow = 1,
    ParentIsland = 2
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class ContentDeferral
{
    private readonly DispatcherQueue _dispatcherQueue;
    private Action? _completion;

    internal ContentDeferral(
        DispatcherQueue dispatcherQueue,
        Action completion)
    {
        _dispatcherQueue = dispatcherQueue ??
            throw new ArgumentNullException(
                nameof(dispatcherQueue));
        _completion = completion ??
            throw new ArgumentNullException(
                nameof(completion));
    }

    public void Complete()
    {
        if (!_dispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException(
                "The content deferral must be completed on its owner dispatcher thread.");
        }

        Interlocked.Exchange(
            ref _completion,
            null)?.Invoke();
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class
    ContentEnvironmentSettingChangedEventArgs
{
    internal ContentEnvironmentSettingChangedEventArgs(
        string settingName)
    {
        SettingName = settingName ??
            throw new ArgumentNullException(
                nameof(settingName));
    }

    public string SettingName { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class
    ContentEnvironmentStateChangedEventArgs
{
    private const byte AppWindowIdFlag = 1;
    private const byte DisplayIdFlag = 2;
    private const byte DisplayScaleFlag = 4;
    private readonly byte _changes;

    internal ContentEnvironmentStateChangedEventArgs(
        bool didAppWindowIdChange,
        bool didDisplayIdChange,
        bool didDisplayScaleChange)
    {
        _changes = (byte)(
            Pack(
                didAppWindowIdChange,
                AppWindowIdFlag) |
            Pack(
                didDisplayIdChange,
                DisplayIdFlag) |
            Pack(
                didDisplayScaleChange,
                DisplayScaleFlag));
    }

    public bool DidAppWindowIdChange =>
        Has(AppWindowIdFlag);

    public bool DidDisplayIdChange =>
        Has(DisplayIdFlag);

    public bool DidDisplayScaleChange =>
        Has(DisplayScaleFlag);

    private bool Has(byte flag) =>
        (_changes & flag) != 0;

    private static byte Pack(
        bool value,
        byte flag) =>
        value ? flag : (byte)0;
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class
    ContentIslandAutomationProviderRequestedEventArgs
{
    internal
        ContentIslandAutomationProviderRequestedEventArgs()
    {
    }

    public object? AutomationProvider
    {
        get;
        set;
    }

    public bool Handled
    {
        get;
        set;
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class ContentIslandStateChangedEventArgs
{
    private const byte ActualSizeFlag = 1;
    private const byte LayoutDirectionFlag = 2;
    private const byte LocalToClientTransformFlag = 4;
    private const byte LocalToParentTransformFlag = 8;
    private const byte RasterizationScaleFlag = 16;
    private const byte SiteEnabledFlag = 32;
    private const byte SiteVisibleFlag = 64;
    private readonly byte _changes;

    internal ContentIslandStateChangedEventArgs(
        bool didActualSizeChange,
        bool didLayoutDirectionChange,
        bool didLocalToClientTransformMatrixChange,
        bool didLocalToParentTransformMatrixChange,
        bool didRasterizationScaleChange,
        bool didSiteEnabledChange,
        bool didSiteVisibleChange)
    {
        _changes = (byte)(
            Pack(
                didActualSizeChange,
                ActualSizeFlag) |
            Pack(
                didLayoutDirectionChange,
                LayoutDirectionFlag) |
            Pack(
                didLocalToClientTransformMatrixChange,
                LocalToClientTransformFlag) |
            Pack(
                didLocalToParentTransformMatrixChange,
                LocalToParentTransformFlag) |
            Pack(
                didRasterizationScaleChange,
                RasterizationScaleFlag) |
            Pack(
                didSiteEnabledChange,
                SiteEnabledFlag) |
            Pack(
                didSiteVisibleChange,
                SiteVisibleFlag));
    }

    public bool DidActualSizeChange =>
        Has(ActualSizeFlag);

    public bool DidLayoutDirectionChange =>
        Has(LayoutDirectionFlag);

    public bool DidLocalToClientTransformMatrixChange =>
        Has(LocalToClientTransformFlag);

    public bool DidLocalToParentTransformMatrixChange =>
        Has(LocalToParentTransformFlag);

    public bool DidRasterizationScaleChange =>
        Has(RasterizationScaleFlag);

    public bool DidSiteEnabledChange =>
        Has(SiteEnabledFlag);

    public bool DidSiteVisibleChange =>
        Has(SiteVisibleFlag);

    private bool Has(byte flag) =>
        (_changes & flag) != 0;

    private static byte Pack(
        bool value,
        byte flag) =>
        value ? flag : (byte)0;
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010007)]
public sealed class
    ContentSiteAutomationProviderRequestedEventArgs
{
    internal
        ContentSiteAutomationProviderRequestedEventArgs()
    {
    }

    public object? AutomationProvider
    {
        get;
        set;
    }

    public bool Handled
    {
        get;
        set;
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class
    ContentSiteRequestedStateChangedEventArgs
{
    internal ContentSiteRequestedStateChangedEventArgs(
        bool didRequestedSizeChange)
    {
        DidRequestedSizeChange =
            didRequestedSizeChange;
    }

    public bool DidRequestedSizeChange { get; }
}
