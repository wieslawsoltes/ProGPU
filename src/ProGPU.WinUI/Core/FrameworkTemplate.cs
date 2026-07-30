using Microsoft.UI.Xaml.Markup;

namespace Microsoft.UI.Xaml;

/// <summary>
/// Base for parser-owned deferred element factories. The advertised Template content
/// member intentionally has no CLR property, matching the WinUI public contract.
/// </summary>
[ContentProperty(Name = "Template")]
public class FrameworkTemplate : DependencyObject
{
    internal global::System.Func<object?, FrameworkElement>? DeferredFactory { get; set; }
}
