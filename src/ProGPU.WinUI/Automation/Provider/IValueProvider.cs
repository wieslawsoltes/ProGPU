using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation.Provider;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IValueProvider
{
    bool IsReadOnly { get; }

    string Value { get; }

    void SetValue(string value);
}
