using Windows.Foundation.Metadata;
using Windows.UI.Core;
using WinRT;

namespace Microsoft.UI.Input;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public class InputCursor :
    IDisposable
{
    private bool _isDisposed;

    protected internal InputCursor(
        IObjectReference objRef)
    {
        ArgumentNullException.ThrowIfNull(objRef);
    }

    protected InputCursor(
        DerivedComposed _)
    {
        ArgumentNullException.ThrowIfNull(_);
    }

    internal bool IsDisposed => _isDisposed;

    public static InputCursor CreateFromCoreCursor(
        CoreCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (cursor.Type == CoreCursorType.Custom)
            return InputDesktopResourceCursor.Create(cursor.Id);

        return InputSystemCursor.Create(
            (InputSystemCursorShape)cursor.Type);
    }

    public void Dispose() =>
        _isDisposed = true;
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010001)]
public class InputCustomCursor :
    InputCursor
{
    protected internal InputCustomCursor(
        IObjectReference objRef)
        : base(objRef)
    {
    }

    protected InputCustomCursor(
        DerivedComposed _)
        : base(_)
    {
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010001)]
public sealed class InputDesktopNamedResourceCursor :
    InputCursor
{
    private InputDesktopNamedResourceCursor(
        string moduleName,
        string resourceName)
        : base(new DerivedComposed())
    {
        ModuleName = moduleName;
        ResourceName = resourceName;
    }

    public string ModuleName { get; }

    public string ResourceName { get; }

    public static InputDesktopNamedResourceCursor Create(
        string resourceName)
    {
        ArgumentNullException.ThrowIfNull(resourceName);
        return new InputDesktopNamedResourceCursor(
            string.Empty,
            resourceName);
    }

    public static InputDesktopNamedResourceCursor CreateFromModule(
        string moduleName,
        string resourceName)
    {
        ArgumentNullException.ThrowIfNull(moduleName);
        ArgumentNullException.ThrowIfNull(resourceName);
        return new InputDesktopNamedResourceCursor(
            moduleName,
            resourceName);
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class InputDesktopResourceCursor :
    InputCursor
{
    private InputDesktopResourceCursor(
        string moduleName,
        uint resourceId)
        : base(new DerivedComposed())
    {
        ModuleName = moduleName;
        ResourceId = resourceId;
    }

    public string ModuleName { get; }

    public uint ResourceId { get; }

    public static InputDesktopResourceCursor Create(
        uint resourceId) =>
        new(
            string.Empty,
            resourceId);

    public static InputDesktopResourceCursor CreateFromModule(
        string moduleName,
        uint resourceId)
    {
        ArgumentNullException.ThrowIfNull(moduleName);
        return new InputDesktopResourceCursor(
            moduleName,
            resourceId);
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class InputSystemCursor :
    InputCursor
{
    private InputSystemCursor(
        InputSystemCursorShape cursorShape)
        : base(new DerivedComposed())
    {
        CursorShape = cursorShape;
    }

    public InputSystemCursorShape CursorShape { get; }

    public static InputSystemCursor Create(
        InputSystemCursorShape type)
    {
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        return new InputSystemCursor(type);
    }
}
