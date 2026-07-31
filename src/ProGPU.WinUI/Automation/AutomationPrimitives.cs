using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class AutomationProperty
{
    internal AutomationProperty(int id) => Id = id;

    internal int Id { get; }
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum AutomationTextEditChangeType
{
    None = 0,
    AutoCorrect = 1,
    Composition = 2,
    CompositionFinalized = 3
}
