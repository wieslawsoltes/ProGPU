using Windows.Foundation.Metadata;

namespace Microsoft.UI.Composition;

internal static class CompositionContract
{
    internal const string Name =
        "Microsoft.Foundation.WindowsAppSDKContract";
    internal const uint Version1 = 0x00010000;
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public enum AnimationPropertyAccessMode
{
    None = 0,
    ReadOnly = 1,
    WriteOnly = 2,
    ReadWrite = 3
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public enum CompositionBackfaceVisibility
{
    Inherit = 0,
    Visible = 1,
    Hidden = 2
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public enum CompositionBorderMode
{
    Inherit = 0,
    Soft = 1,
    Hard = 2
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public enum CompositionCompositeMode
{
    Inherit = 0,
    SourceOver = 1,
    DestinationInvert = 2,
    MinBlend = 3
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public enum CompositionGetValueStatus
{
    Succeeded = 0,
    TypeMismatch = 1,
    NotFound = 2
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public interface IAnimationObject
{
    void PopulatePropertyInfo(
        string propertyName,
        AnimationPropertyInfo propertyInfo);
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public interface ICompositionAnimationBase
{
}
