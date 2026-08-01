using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Markup;

[AttributeUsage(
    AttributeTargets.Class,
    AllowMultiple = false)]
[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class ContentPropertyAttribute : Attribute
{
    public string Name = string.Empty;

    public ContentPropertyAttribute()
    {
    }
}
