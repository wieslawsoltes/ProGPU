using Windows.Foundation.Metadata;

namespace Microsoft.UI.Text;

[ContractVersion(0x00020000)]
public enum TextApiContract
{
}

internal static class TextApiContractInfo
{
    public const string Name =
        "Microsoft.UI.Text.TextApiContract";
    public const uint Version1 = 0x00010000;
}
