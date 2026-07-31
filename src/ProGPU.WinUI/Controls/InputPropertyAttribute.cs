using System;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// Identifies the primary editable property of an input control.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class InputPropertyAttribute : Attribute
{
    public string Name = string.Empty;
}
