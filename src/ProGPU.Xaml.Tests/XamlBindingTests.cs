using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Lowering;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Roslyn;
using ProGPU.Xaml.Resources;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Serialization;
using ProGPU.Xaml.Syntax;
using Xunit;

namespace ProGPU.Xaml.Tests;

public sealed class XamlBindingTests
{
    private const string Framework = """
namespace Microsoft.UI.Xaml.Markup {
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=true)]
  public sealed class ContentPropertyAttribute : System.Attribute { public string? Name { get; set; } }
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=true)]
  public sealed class MarkupExtensionReturnTypeAttribute : System.Attribute { public System.Type? ReturnType; }
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=false, Inherited=true)]
  public sealed class UsableDuringInitializationAttribute : System.Attribute {
    public UsableDuringInitializationAttribute(bool usable) { Usable = usable; }
    public bool Usable { get; }
  }
  public class MarkupExtension { protected virtual object? ProvideValue() => null; protected virtual object? ProvideValue(Microsoft.UI.Xaml.IXamlServiceProvider serviceProvider) => ProvideValue(); internal object? Evaluate(Microsoft.UI.Xaml.IXamlServiceProvider? serviceProvider) => serviceProvider == null ? ProvideValue() : ProvideValue(serviceProvider); }
  public interface IAddChild { void AddChild(object value); }
  public static class XamlTemplateFactory {
    public static void SetFactory(Microsoft.UI.Xaml.FrameworkTemplate template, System.Func<object?, Microsoft.UI.Xaml.FrameworkElement> factory) { }
    public static void AttachBindings(Microsoft.UI.Xaml.FrameworkElement root, object bindings) { }
  }
}
namespace Microsoft.Maui.Controls.Xaml {
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=false)]
  public sealed class AcceptEmptyServiceProviderAttribute : System.Attribute { }
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=false, Inherited=false)]
  public sealed class RequireServiceAttribute : System.Attribute {
    public RequireServiceAttribute(params System.Type[] serviceTypes) { ServiceTypes = serviceTypes; }
    public System.Type[] ServiceTypes { get; }
  }
  public interface IMarkupExtension {
    object ProvideValue(System.IServiceProvider serviceProvider);
  }
  public interface IMarkupExtension<out T> : IMarkupExtension {
    new T ProvideValue(System.IServiceProvider serviceProvider);
  }
}
namespace System.Windows.Markup {
  public abstract class MarkupExtension {
    public virtual object? ProvideValue(System.IServiceProvider serviceProvider) => null;
  }
  [System.Obsolete]
  public interface IReceiveMarkupExtension {
    void ReceiveMarkupExtension(string property, MarkupExtension markupExtension, System.IServiceProvider serviceProvider);
  }
  [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method | System.AttributeTargets.Property, AllowMultiple=false, Inherited=true)]
  public sealed class AmbientAttribute : System.Attribute { }
  [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Property, AllowMultiple=false, Inherited=true)]
  public sealed class XamlDeferLoadAttribute : System.Attribute {
    public XamlDeferLoadAttribute(System.Type loaderType, System.Type contentType) { LoaderType = loaderType; ContentType = contentType; }
    public XamlDeferLoadAttribute(string loaderTypeName, string contentTypeName) { LoaderTypeName = loaderTypeName; ContentTypeName = contentTypeName; }
    public System.Type? LoaderType { get; }
    public System.Type? ContentType { get; }
    public string? LoaderTypeName { get; }
    public string? ContentTypeName { get; }
  }
  [System.AttributeUsage(System.AttributeTargets.Property, AllowMultiple=true, Inherited=false)]
  public sealed class MarkupExtensionBracketCharactersAttribute : System.Attribute {
    public MarkupExtensionBracketCharactersAttribute(char openingBracket, char closingBracket) { OpeningBracket = openingBracket; ClosingBracket = closingBracket; }
    public char OpeningBracket { get; }
    public char ClosingBracket { get; }
  }
  [System.AttributeUsage(System.AttributeTargets.Property, AllowMultiple=false, Inherited=false)]
  public sealed class ConstructorArgumentAttribute : System.Attribute {
    public ConstructorArgumentAttribute(string argumentName) { ArgumentName = argumentName; }
    public string ArgumentName { get; }
  }
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=true, Inherited=true)]
  public sealed class ContentWrapperAttribute : System.Attribute {
    public ContentWrapperAttribute(System.Type contentWrapper) { ContentWrapper = contentWrapper; }
    public System.Type ContentWrapper { get; }
  }
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=false, Inherited=true)]
  public sealed class TrimSurroundingWhitespaceAttribute : System.Attribute { }
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=false, Inherited=true)]
  public sealed class WhitespaceSignificantCollectionAttribute : System.Attribute { }
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=false, Inherited=true)]
  public sealed class UsableDuringInitializationAttribute : System.Attribute {
    public UsableDuringInitializationAttribute(bool usable) { Usable = usable; }
    public bool Usable { get; }
  }
  [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct | System.AttributeTargets.Property, AllowMultiple=false, Inherited=true)]
  public sealed class ValueSerializerAttribute : System.Attribute {
    public ValueSerializerAttribute(System.Type? valueSerializerType) { ValueSerializerType = valueSerializerType; }
    public System.Type? ValueSerializerType { get; }
  }
  public interface IValueSerializerContext { }
  public abstract class ValueSerializer {
    public virtual bool CanConvertToString(object value, IValueSerializerContext? context) => false;
    public virtual string ConvertToString(object value, IValueSerializerContext? context) => throw new System.NotSupportedException();
  }
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=false, Inherited=true)]
  public sealed class XamlSetMarkupExtensionAttribute : System.Attribute {
    public XamlSetMarkupExtensionAttribute(string handler) { XamlSetMarkupExtensionHandler = handler; }
    public string XamlSetMarkupExtensionHandler { get; }
  }
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=false, Inherited=true)]
  public sealed class XamlSetTypeConverterAttribute : System.Attribute {
    public XamlSetTypeConverterAttribute(string handler) { XamlSetTypeConverterHandler = handler; }
    public string XamlSetTypeConverterHandler { get; }
  }
  public class XamlSetMarkupExtensionEventArgs : System.EventArgs { }
  public class XamlSetTypeConverterEventArgs : System.EventArgs { }
}
namespace System.Xaml {
  public abstract class XamlReader { }
  public abstract class XamlDeferringLoader {
    public abstract object Load(XamlReader xamlReader, System.IServiceProvider serviceProvider);
    public abstract XamlReader Save(object value, System.IServiceProvider serviceProvider);
  }
}
namespace ProGPU.Xaml.Runtime {
  public static class WinUiMarkupExtensionRuntime {
    public static T Evaluate<T>(Microsoft.UI.Xaml.Markup.MarkupExtension extension, object? targetObject, System.Type? targetType, string? targetMemberName, object? rootObject, string? resourceUri) => default!;
  }
}
namespace Microsoft.UI.Xaml.HotReload { public interface IHotReloadable { void Reload(HotReloadContext context); } public sealed class HotReloadContext { } }
namespace Windows.Foundation.Metadata {
  [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct, AllowMultiple=false, Inherited=false)]
  public sealed class CreateFromStringAttribute : System.Attribute {
    public string? MethodName;
  }
}
namespace Windows.UI.Text { public struct FontWeight { public ushort Weight; } }
namespace Microsoft.UI.Text {
  public static class FontWeights {
    public static Windows.UI.Text.FontWeight Normal => new() { Weight = 400 };
    public static Windows.UI.Text.FontWeight SemiBold => new() { Weight = 600 };
    public static Windows.UI.Text.FontWeight Bold => new() { Weight = 700 };
  }
}
namespace Microsoft.UI.Xaml {
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=true, Inherited=true)]
  public sealed class TemplatePartAttribute : System.Attribute {
    public string? Name;
    public System.Type? Type;
  }
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=true, Inherited=true)]
  public sealed class TemplateVisualStateAttribute : System.Attribute {
    public string? Name;
    public string? GroupName;
  }
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=true, Inherited=true)]
  public sealed class StyleTypedPropertyAttribute : System.Attribute {
    public string? Property;
    public System.Type? StyleTargetType;
  }
  public interface IXamlServiceProvider { object? GetService(System.Type serviceType); }
  public sealed class DependencyProperty { }
  public class DependencyObject {
    public void SetValue(DependencyProperty property, object? value) { }
  }
  public struct Thickness {
    public Thickness(float uniform) { }
    public Thickness(float horizontal, float vertical) { }
    public Thickness(float left, float top, float right, float bottom) { }
  }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Template")]
  public class FrameworkTemplate { }
  public class FrameworkElement : DependencyObject { public string? Name { get; set; } public Microsoft.UI.Xaml.Controls.ResourceDictionary Resources { get; } = new(); }
  public class Style { public System.Type? TargetType { get; set; } }
  public sealed class VisualStateGroup { }
  public static class VisualStateManager {
    private static readonly System.Collections.Generic.List<VisualStateGroup> Groups = new();
    public static System.Collections.Generic.IList<VisualStateGroup> GetVisualStateGroups(FrameworkElement element) => Groups;
  }
  public sealed class ThemeResource { public ThemeResource(object? root, object key) { } }
  public static class ThemeResourceOperations {
    public static void SetResource(DependencyObject target, string propertyName, ThemeResource resource) { }
  }
  public static class XamlResourceResolver { public static T Resolve<T>(object root, object key) => default!; }
  public static class XamlResourceProviderRegistry { public static void Register(string uri, System.Func<Microsoft.UI.Xaml.Controls.ResourceDictionary> factory) { } }
}
namespace Microsoft.UI.Xaml.Data {
  public enum BindingMode { OneWay, OneTime, TwoWay }
  public enum UpdateSourceTrigger { Default, PropertyChanged, Explicit, LostFocus }
  public enum RelativeSourceMode { None, TemplatedParent, Self }
  public sealed class RelativeSource {
    public RelativeSourceMode Mode { get; set; }
  }
  public sealed class Binding {
    public string? Path { get; set; }
    public BindingMode Mode { get; set; }
    public string? ElementName { get; set; }
    public RelativeSource? RelativeSource { get; set; }
    public object? Source { get; set; }
    public object? Converter { get; set; }
    public object? ConverterParameter { get; set; }
    public string? ConverterLanguage { get; set; }
    public UpdateSourceTrigger UpdateSourceTrigger { get; set; }
    public object? FallbackValue { get; set; }
    public object? TargetNullValue { get; set; }
  }
  public static class BindingOperations {
    public static object SetBinding(
      Microsoft.UI.Xaml.DependencyObject target,
      string targetPropertyName,
      Binding binding,
      object? context = null,
      object? lookupRoot = null) => new object();
  }
}
namespace Microsoft.UI.Xaml.Media {
  public class FontFamily { public FontFamily(string source) { Source = source; } public string Source { get; } }
}
namespace Avalonia.Metadata {
  [System.AttributeUsage(System.AttributeTargets.Property)]
  public sealed class DataTypeAttribute : System.Attribute { }
  public enum InheritDataTypeFromScopeKind { Style = 1, ControlTemplate = 2 }
  [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Parameter, Inherited=true)]
  public sealed class InheritDataTypeFromAttribute : System.Attribute {
    public InheritDataTypeFromAttribute(InheritDataTypeFromScopeKind scopeKind) { ScopeKind = scopeKind; }
    public InheritDataTypeFromScopeKind ScopeKind { get; }
  }
  [System.AttributeUsage(System.AttributeTargets.Property, Inherited=true)]
  public sealed class InheritDataTypeFromItemsAttribute : System.Attribute {
    public InheritDataTypeFromItemsAttribute(string ancestorItemsProperty) { AncestorItemsProperty = ancestorItemsProperty; }
    public string AncestorItemsProperty { get; }
    public System.Type? AncestorType { get; set; }
  }
  [System.AttributeUsage(System.AttributeTargets.Property)]
  public sealed class MarkupExtensionDefaultOptionAttribute : System.Attribute { }
  [System.AttributeUsage(System.AttributeTargets.Property)]
  public sealed class MarkupExtensionOptionAttribute : System.Attribute {
    public MarkupExtensionOptionAttribute(object value) { Value = value; }
    public object Value { get; }
    public int Priority { get; set; }
  }
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=true)]
  public sealed class AvaloniaListAttribute : System.Attribute {
    public string[]? Separators { get; set; }
    public System.StringSplitOptions SplitOptions { get; set; } =
      System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries;
  }
}
namespace Avalonia.Data {
  [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method)]
  public sealed class AssignBindingAttribute : System.Attribute { }
}
namespace Avalonia.Controls.Metadata {
  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple=true, Inherited=true)]
  public sealed class TemplatePartAttribute : System.Attribute {
    public TemplatePartAttribute() { }
    public TemplatePartAttribute(string name, System.Type type) { Name = name; Type = type; }
    public string? Name { get; set; }
    public System.Type? Type { get; set; }
    public bool IsRequired { get; set; }
  }
}
namespace System.Windows {
  [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple=true, Inherited=true)]
  public sealed class AttachedPropertyBrowsableForTypeAttribute : System.Attribute {
    public AttachedPropertyBrowsableForTypeAttribute(System.Type targetType) { TargetType = targetType; }
    public System.Type TargetType { get; }
  }
  [System.AttributeUsage(System.AttributeTargets.Method, Inherited=true)]
  public sealed class AttachedPropertyBrowsableForChildrenAttribute : System.Attribute {
    public bool IncludeDescendants { get; set; }
  }
  [System.AttributeUsage(System.AttributeTargets.Method, Inherited=true)]
  public sealed class AttachedPropertyBrowsableWhenAttributePresentAttribute : System.Attribute {
    public AttachedPropertyBrowsableWhenAttributePresentAttribute(System.Type attributeType) { AttributeType = attributeType; }
    public System.Type AttributeType { get; }
  }
}
namespace Microsoft.UI.Xaml.Controls {
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")]
  public class Page : Microsoft.UI.Xaml.FrameworkElement { public object? Content { get; set; } }
  public class ControlTemplate : Microsoft.UI.Xaml.FrameworkTemplate { public System.Type? TargetType { get; set; } }
  [Microsoft.UI.Xaml.Markup.UsableDuringInitialization(true)]
  public class ResourceDictionary : System.Collections.Generic.Dictionary<object, object> {
    public System.Uri? Source { get; set; }
    public System.Collections.Generic.List<ResourceDictionary> MergedDictionaries { get; } = new();
    public System.Collections.Generic.Dictionary<object, object> ThemeDictionaries { get; } = new();
  }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Children")]
  public class StackPanel : Microsoft.UI.Xaml.FrameworkElement { public System.Collections.Generic.List<object> Children { get; } = new(); }
  public class Grid : Microsoft.UI.Xaml.FrameworkElement {
    private static readonly System.Collections.Generic.Dictionary<Microsoft.UI.Xaml.FrameworkElement, int> Rows = new();
    public static int GetRow(Microsoft.UI.Xaml.FrameworkElement element) => Rows.TryGetValue(element, out var value) ? value : 0;
    public static void SetRow(Microsoft.UI.Xaml.FrameworkElement element, int value) => Rows[element] = value;
  }
  public class TextBlock : Microsoft.UI.Xaml.FrameworkElement { public string? Text { get; set; } }
  public class Button : Microsoft.UI.Xaml.FrameworkElement { public event System.EventHandler? Click; }
}
namespace Demo {
  [System.AttributeUsage(System.AttributeTargets.Property, AllowMultiple=true, Inherited=true)]
  public sealed class DependsOnAttribute : System.Attribute { public DependsOnAttribute(string name) { Name = name; } public string Name { get; } }
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=true)]
  public sealed class NameScopePropertyAttribute : System.Attribute {
    public NameScopePropertyAttribute(string name) { Name = name; }
    public NameScopePropertyAttribute(string name, System.Type type) { Name = name; Type = type; }
    public string Name { get; }
    public System.Type? Type { get; }
  }
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=true)]
  public sealed class XmlLanguagePropertyAttribute : System.Attribute { public XmlLanguagePropertyAttribute(string name) { Name = name; } public string Name { get; } }
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=true)]
  public sealed class UidPropertyAttribute : System.Attribute { public UidPropertyAttribute(string name) { Name = name; } public string Name { get; } }
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=true)]
  public sealed class ContentOneAttribute : System.Attribute { public ContentOneAttribute(string name) { Name = name; } public string Name { get; } }
  [System.AttributeUsage(System.AttributeTargets.Class, Inherited=true)]
  public sealed class ContentTwoAttribute : System.Attribute { public ContentTwoAttribute(string name) { Name = name; } public string Name { get; } }
  public static class Palette {
    public const string Greeting = "Hello";
    public const string GreetingAlias = "Hello";
    public const int One = 1;
    public const long LongOne = 1L;
    public static readonly string RuntimeGreeting = "Hello";
    public const string NullKey = null;
  }
  public sealed class StringDictionary : System.Collections.Generic.Dictionary<string, object> { }
  public sealed class IntDictionary : System.Collections.Generic.Dictionary<int, object> { }
  public sealed class Widget { public Widget(int count) { Count = count; } public int Count { get; } public static Widget Create(int count) => new Widget(count); }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Value")] public sealed class Box<T> { public T? Value { get; set; } }
  public sealed class ReferenceBox<T> where T : class { }
  public sealed class OrderedControl {
    public string? First { get; set; }
    [DependsOn(nameof(First))] public string? Second { get; set; }
    [DependsOn(nameof(First)), DependsOn(nameof(Second))] public string? Third { get; set; }
  }
  public sealed class BrokenOrderControl { [DependsOn("Missing")] public string? Value { get; set; } }
  public sealed class CyclicOrderControl {
    [DependsOn(nameof(Second))] public string? First { get; set; }
    [DependsOn(nameof(First))] public string? Second { get; set; }
  }
  public static class OverloadedAttachedOwner {
    public static int GetLevel(object value) => 0;
    public static void SetLevel(object value, int level) { }
    public static int GetLevel(Microsoft.UI.Xaml.FrameworkElement value) => 0;
    public static void SetLevel(Microsoft.UI.Xaml.FrameworkElement value, int level) { }
  }
  [System.Obsolete("Legacy type")] public sealed class LegacyControl { }
  [System.Diagnostics.CodeAnalysis.Experimental("DEMO001")] public sealed class PreviewControl { }
  public sealed class AnnotationControl { [System.Obsolete("Old member", true)] public string? Old { get; set; } }
  [NameScopeProperty(nameof(Scope)), XmlLanguageProperty(nameof(Language)), UidProperty(nameof(AutomationId))]
  public sealed class DirectiveAliasControl { public object? Scope { get; set; } public string? Language { get; set; } public string? AutomationId { get; set; } }
  public static class AttachedScopeOwner {
    public static object GetScope(AttachedDirectiveAliasControl value) => new object();
    public static void SetScope(AttachedDirectiveAliasControl value, object scope) { }
  }
  [NameScopeProperty("Scope", typeof(AttachedScopeOwner))]
  public sealed class AttachedDirectiveAliasControl { }
  [UidProperty("Missing")]
  public sealed class InvalidDirectiveAliasControl { }
  public interface ITestNameScope {
    void RegisterName(string name, object value);
    void UnregisterName(string name);
    object? FindName(string name);
  }
  public interface IAlternateNameScope {
    void RegisterName(string name, object value);
    void UnregisterName(string name);
    object? FindName(string name);
  }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")]
  public sealed class InterfaceNameScopeControl : ITestNameScope {
    public object? Content { get; set; }
    public void RegisterName(string name, object value) { }
    public void UnregisterName(string name) { }
    public object? FindName(string name) => null;
  }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name="Content")]
  public sealed class DuckNameScopeControl {
    public object? Content { get; set; }
    public void RegisterName(string name, object value) { }
    public void UnregisterName(string name) { }
    public object? FindName(string name) => null;
  }
  public sealed class InvalidDuckNameScopeControl {
    public void RegisterName(string name, object value) { }
  }
  public sealed class CustomDuckNameScopeControl {
    public void AddName(string name, object value) { }
    public void RemoveName(string name) { }
    public object? LookupName(string name) => null;
  }
  public sealed class ConflictingNameScopeControl : ITestNameScope, IAlternateNameScope {
    public void RegisterName(string name, object value) { }
    public void UnregisterName(string name) { }
    public object? FindName(string name) => null;
  }
  public sealed class OptionExtension : Microsoft.UI.Xaml.Markup.MarkupExtension {
    [Avalonia.Metadata.MarkupExtensionDefaultOption]
    public object? Default { get; set; }
    [Avalonia.Metadata.MarkupExtensionOption("X64", Priority=20)]
    public object? X64 { get; set; }
    [Avalonia.Metadata.MarkupExtensionDefaultOption]
    [Avalonia.Metadata.MarkupExtensionOption("Conflict")]
    public object? Conflict { get; set; }
    public static bool ShouldProvideOption(object option) => false;
    public static bool ShouldProvideOption(string option) => option == "X64";
    protected override object? ProvideValue() => X64 ?? Default;
  }
  public enum OptionKind { Desktop, Mobile }
  public sealed class ServiceOptionExtension : Microsoft.UI.Xaml.Markup.MarkupExtension {
    [Avalonia.Metadata.MarkupExtensionDefaultOption]
    public object? Default { get; set; }
    [Avalonia.Metadata.MarkupExtensionOption(OptionKind.Desktop, Priority=10)]
    public object? Desktop { get; set; }
    public static bool ShouldProvideOption(OptionKind option) =>
      option == OptionKind.Desktop;
    public static bool ShouldProvideOption(System.IServiceProvider provider, OptionKind option) =>
      option == OptionKind.Desktop;
    protected override object? ProvideValue() => Desktop ?? Default;
  }
  public sealed class InvalidSelectorExtension : Microsoft.UI.Xaml.Markup.MarkupExtension {
    [Avalonia.Metadata.MarkupExtensionOption("Desktop")]
    public object? Desktop { get; set; }
    public static int ShouldProvideOption(string option) => 1;
    protected override object? ProvideValue() => Desktop;
  }
  [Avalonia.Metadata.AvaloniaList(
    Separators=new[] { "::", ";", "|" },
    SplitOptions=System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)]
  public sealed class TokenList : System.Collections.Generic.List<string> { }
  [Avalonia.Metadata.AvaloniaList(Separators=new[] { "" })]
  public sealed class InvalidTokenList : System.Collections.Generic.List<string> { }
  public sealed class ListHost {
    public TokenList Items { get; } = new TokenList();
  }
  public sealed class BindingHost {
    [Avalonia.Metadata.DataType]
    public object? DataType { get; set; }
    public object? Items { get; set; }
    [Avalonia.Metadata.InheritDataTypeFromItems(
      nameof(Items), AncestorType=typeof(BindingHost))]
    public object? ItemTemplate { get; set; }
    [Avalonia.Data.AssignBinding]
    public object? AssignedBinding { get; set; }
  }
  public sealed class BindingExtension : Microsoft.UI.Xaml.Markup.MarkupExtension {
    public string? Path { get; set; }
    protected override object? ProvideValue() => this;
  }
  public static class BindingAttachedOwner {
    public static object? GetAssigned(BindingHost target) => null;
    [Avalonia.Data.AssignBinding]
    public static void SetAssigned(BindingHost target, object? binding) { }
  }
  public sealed class ScopeExtension : Microsoft.UI.Xaml.Markup.MarkupExtension {
    [Avalonia.Metadata.InheritDataTypeFrom(
      Avalonia.Metadata.InheritDataTypeFromScopeKind.ControlTemplate)]
    public object? Property { get; set; }
  }
  public sealed class ParameterScopeExtension : Microsoft.UI.Xaml.Markup.MarkupExtension {
    public ParameterScopeExtension(
      [Avalonia.Metadata.InheritDataTypeFrom(
        Avalonia.Metadata.InheritDataTypeFromScopeKind.Style)] object property) { }
  }
  public sealed class InvalidDataTypeHost {
    [Avalonia.Metadata.DataType]
    public static object? DataType { get; set; }
    [Avalonia.Metadata.InheritDataTypeFrom(
      (Avalonia.Metadata.InheritDataTypeFromScopeKind)99)]
    public object? Scope { get; set; }
    [Avalonia.Metadata.InheritDataTypeFromItems("Missing")]
    public object? ItemTemplate { get; set; }
  }
  [Microsoft.UI.Xaml.TemplatePart(
    Name="PART_Content", Type=typeof(Microsoft.UI.Xaml.Controls.TextBlock))]
  [Microsoft.UI.Xaml.TemplateVisualState(Name="Normal", GroupName="CommonStates")]
  [Microsoft.UI.Xaml.StyleTypedProperty(
    Property=nameof(ItemContainerStyle),
    StyleTargetType=typeof(Microsoft.UI.Xaml.Controls.Button))]
  public class ToolingControl : Microsoft.UI.Xaml.FrameworkElement {
    public object? ItemContainerStyle { get; set; }
  }
  [Avalonia.Controls.Metadata.TemplatePart(
    "PART_Avalonia", typeof(Microsoft.UI.Xaml.Controls.Button), IsRequired=true)]
  public sealed class DerivedToolingControl : ToolingControl { }
  [Microsoft.UI.Xaml.TemplatePart(Name="PART_Duplicate", Type=typeof(Microsoft.UI.Xaml.Controls.Button))]
  [Microsoft.UI.Xaml.TemplatePart(Name="PART_Duplicate", Type=typeof(Microsoft.UI.Xaml.Controls.TextBlock))]
  [Microsoft.UI.Xaml.TemplateVisualState(Name="Broken")]
  [Microsoft.UI.Xaml.StyleTypedProperty(
    Property="Missing", StyleTargetType=typeof(Microsoft.UI.Xaml.Controls.Button))]
  public sealed class InvalidToolingControl : Microsoft.UI.Xaml.FrameworkElement { }
  public static class BrowseOwner {
    [System.Windows.AttachedPropertyBrowsableForType(
      typeof(Microsoft.UI.Xaml.Controls.Button))]
    [System.Windows.AttachedPropertyBrowsableForType(
      typeof(Microsoft.UI.Xaml.FrameworkElement))]
    [System.Windows.AttachedPropertyBrowsableForChildren(IncludeDescendants=true)]
    [System.Windows.AttachedPropertyBrowsableWhenAttributePresent(
      typeof(System.ObsoleteAttribute))]
    public static object? GetHint(Microsoft.UI.Xaml.FrameworkElement target) => null;
    public static void SetHint(
      Microsoft.UI.Xaml.FrameworkElement target, object? value) { }

    public static object? GetInvalidHint(
      Microsoft.UI.Xaml.FrameworkElement target) => null;
    [System.Windows.AttachedPropertyBrowsableForChildren]
    public static void SetInvalidHint(
      Microsoft.UI.Xaml.FrameworkElement target, object? value) { }
  }
  [ContentOne(nameof(First)), ContentTwo(nameof(Second))]
  public sealed class ConflictingContentControl { public object? First { get; set; } public object? Second { get; set; } }
  public sealed class ReferenceHolder { public object? Target { get; set; } }
  public sealed class ExplicitChildHost : Microsoft.UI.Xaml.FrameworkElement, Microsoft.UI.Xaml.Markup.IAddChild {
    void Microsoft.UI.Xaml.Markup.IAddChild.AddChild(object value) { }
  }
  [System.ComponentModel.TypeConverter(typeof(LengthConverter))]
  public readonly struct Length { public Length(int value) { Value = value; } public int Value { get; } }
  public sealed class LengthConverter : System.ComponentModel.TypeConverter {
    public override bool CanConvertFrom(System.ComponentModel.ITypeDescriptorContext? context, System.Type sourceType) => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    public override object? ConvertFrom(System.ComponentModel.ITypeDescriptorContext? context, System.Globalization.CultureInfo? culture, object value) => value is string text ? new Length(int.Parse(text, System.Globalization.CultureInfo.InvariantCulture)) : base.ConvertFrom(context, culture, value);
  }
  public sealed class ConversionControl {
    public Length Length { get; set; }
    [System.ComponentModel.TypeConverter(typeof(LengthConverter))] public object? Payload { get; set; }
  }
  [Windows.Foundation.Metadata.CreateFromString(MethodName=nameof(Parse))]
  public readonly struct FactoryLength {
    public FactoryLength(int value) { Value = value; }
    public int Value { get; }
    public static FactoryLength Parse(string value) => new(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
  }
  [Windows.Foundation.Metadata.CreateFromString(MethodName="Demo.FactoryLengthFactory.ParseQualified")]
  public sealed class QualifiedFactoryLength {
    public QualifiedFactoryLength(int value) { Value = value; }
    public int Value { get; }
  }
  public static class FactoryLengthFactory {
    public static object ParseQualified(string value) => new QualifiedFactoryLength(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
  }
  [Windows.Foundation.Metadata.CreateFromString(MethodName="Parser.Parse")]
  public sealed class NestedFactoryLength {
    public NestedFactoryLength(int value) { Value = value; }
    public int Value { get; }
    public static class Parser {
      public static NestedFactoryLength Parse(string value) => new(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
    }
  }
  public sealed class FactoryLengthConverter : System.ComponentModel.TypeConverter {
    public override object? ConvertFrom(System.ComponentModel.ITypeDescriptorContext? context, System.Globalization.CultureInfo? culture, object value) =>
      value is string text ? FactoryLength.Parse(text) : base.ConvertFrom(context, culture, value);
  }
  public sealed class FactoryConversionControl {
    public FactoryLength Direct { get; set; }
    public QualifiedFactoryLength? Qualified { get; set; }
    public NestedFactoryLength? Nested { get; set; }
    [System.ComponentModel.TypeConverter(typeof(FactoryLengthConverter))]
    public FactoryLength MemberOverride { get; set; }
  }
  [Windows.Foundation.Metadata.CreateFromString(MethodName="Missing")]
  public sealed class InvalidFactoryLength { }
  [Windows.Foundation.Metadata.CreateFromString(MethodName=nameof(Parse))]
  public sealed class InstanceFactoryLength { public InstanceFactoryLength Parse(string value) => new(); }
  [Windows.Foundation.Metadata.CreateFromString(MethodName=nameof(Parse))]
  public sealed class NonPublicFactoryLength { internal static NonPublicFactoryLength Parse(string value) => new(); }
  [Windows.Foundation.Metadata.CreateFromString(MethodName=nameof(Parse))]
  public sealed class GenericFactoryLength { public static GenericFactoryLength Parse<T>(string value) => new(); }
  [Windows.Foundation.Metadata.CreateFromString(MethodName=nameof(Parse))]
  public sealed class VoidFactoryLength { public static void Parse(string value) { } }
  [Windows.Foundation.Metadata.CreateFromString(MethodName=nameof(Parse))]
  public sealed class WrongParameterFactoryLength { public static WrongParameterFactoryLength Parse(int value) => new(); }
  [Windows.Foundation.Metadata.CreateFromString(MethodName=nameof(Parse))]
  public sealed class InconvertibleFactoryLength { public static string Parse(string value) => value; }
  [Windows.Foundation.Metadata.CreateFromString(MethodName="Demo.NoSuchFactory.Parse")]
  public sealed class UnresolvedFactoryLength { }
  [Windows.Foundation.Metadata.CreateFromString]
  public sealed class EmptyFactoryLength { }
  public sealed class InvalidFactoryConversionControl {
    public InvalidFactoryLength? Value { get; set; }
  }
  [System.ComponentModel.TypeConverter(typeof(string))] public sealed class InvalidConvertedValue { }
  public sealed class InvalidConversionControl { public InvalidConvertedValue? Value { get; set; } }
  [System.Windows.Markup.ValueSerializer(typeof(SerializedLengthSerializer))]
  public readonly struct SerializedLength { public SerializedLength(int value) { Value = value; } public int Value { get; } }
  public sealed class SerializedLengthSerializer : System.Windows.Markup.ValueSerializer {
    public override bool CanConvertToString(object value, System.Windows.Markup.IValueSerializerContext? context) => value is SerializedLength;
    public override string ConvertToString(object value, System.Windows.Markup.IValueSerializerContext? context) => ((SerializedLength)value).Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
  }
  public sealed class HexLengthSerializer : System.Windows.Markup.ValueSerializer {
    public override bool CanConvertToString(object value, System.Windows.Markup.IValueSerializerContext? context) => value is SerializedLength;
    public override string ConvertToString(object value, System.Windows.Markup.IValueSerializerContext? context) => ((SerializedLength)value).Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
  }
  [System.Windows.Markup.ValueSerializer(typeof(string))]
  public sealed class InvalidSerializedValue { }
  [System.Windows.Markup.ValueSerializer(null)]
  public sealed class SuppressedSerializedValue { }
  public sealed class SerializationControl {
    public SerializedLength TypeSerializerValue { get; set; }
    [System.Windows.Markup.ValueSerializer(typeof(HexLengthSerializer))]
    public object? MemberSerializerValue { get; set; }
    [System.Windows.Markup.ValueSerializer(typeof(string))]
    public object? InvalidMemberSerializerValue { get; set; }
  }
  [System.Windows.Markup.WhitespaceSignificantCollection]
  public class SignificantInlineCollection : System.Collections.Generic.List<object> { }
  public class DerivedSignificantInlineCollection : SignificantInlineCollection { }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name=nameof(Items))]
  public sealed class TextFlow {
    public SignificantInlineCollection Items { get; } = new();
  }
  [System.Windows.Markup.TrimSurroundingWhitespace]
  public class InlineBreak { }
  public sealed class DerivedInlineBreak : InlineBreak { }
  public sealed class InlineMarker { }
  [System.Windows.Markup.UsableDuringInitialization(true)]
  public class EarlyAttachNode {
    public string? Value { get; set; }
    public EarlyAttachNode? Child { get; set; }
  }
  [System.Windows.Markup.UsableDuringInitialization(false)]
  public sealed class DeferredAttachNode : EarlyAttachNode { }
  public sealed class InitializationHost : Microsoft.UI.Xaml.FrameworkElement {
    public EarlyAttachNode? Child { get; set; }
    public ConstructorInitializationHost? ConstructorChild { get; set; }
    public System.Collections.Generic.List<EarlyAttachNode> Children { get; } = new();
    public System.Collections.Generic.Dictionary<string, EarlyAttachNode> Entries { get; } = new();
  }
  public sealed class ConstructorInitializationHost {
    public ConstructorInitializationHost(EarlyAttachNode child) { Child = child; }
    public EarlyAttachNode Child { get; }
  }
  public static class InitializationAttachOwner {
    public static EarlyAttachNode? GetNode(Microsoft.UI.Xaml.FrameworkElement target) => null;
    public static void SetNode(Microsoft.UI.Xaml.FrameworkElement target, EarlyAttachNode? value) { }
  }
  public abstract class WrapperInlineBase { }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name=nameof(Text))]
  public sealed class TextInline : WrapperInlineBase { public string? Text { get; set; } }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name=nameof(Child))]
  public sealed class ObjectInline : WrapperInlineBase { public object? Child { get; set; } }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name=nameof(Child))]
  public sealed class AlternateObjectInline : WrapperInlineBase { public object? Child { get; set; } }
  public sealed class DirectInline : WrapperInlineBase { }
  [System.Windows.Markup.ContentWrapper(typeof(TextInline))]
  [System.Windows.Markup.ContentWrapper(typeof(ObjectInline))]
  public class WrappedInlineCollection : System.Collections.Generic.List<WrapperInlineBase> { }
  public sealed class DerivedWrappedInlineCollection : WrappedInlineCollection { }
  [System.Windows.Markup.ContentWrapper(typeof(ObjectInline))]
  [System.Windows.Markup.ContentWrapper(typeof(AlternateObjectInline))]
  public sealed class AmbiguousInlineCollection : System.Collections.Generic.List<WrapperInlineBase> { }
  [System.Windows.Markup.ContentWrapper(typeof(TextInline))]
  public sealed class StringOnlyInlineCollection : System.Collections.Generic.List<WrapperInlineBase> { }
  [System.Windows.Markup.ContentWrapper(typeof(InlineMarker))]
  public sealed class InvalidInlineCollection : System.Collections.Generic.List<WrapperInlineBase> { }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name=nameof(Items))]
  public sealed class WrappedFlow { public WrappedInlineCollection Items { get; } = new(); }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name=nameof(Items))]
  public sealed class AmbiguousWrappedFlow { public AmbiguousInlineCollection Items { get; } = new(); }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name=nameof(Items))]
  public sealed class StringOnlyWrappedFlow { public StringOnlyInlineCollection Items { get; } = new(); }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name=nameof(Items))]
  public sealed class InvalidWrappedFlow { public InvalidInlineCollection Items { get; } = new(); }
  public sealed class ConstructorProjectedValue {
    public ConstructorProjectedValue() { Value = string.Empty; }
    public ConstructorProjectedValue(string value) { Value = value; }
    [System.Windows.Markup.ConstructorArgument("value")]
    public string Value { get; set; }
  }
  public sealed class InvalidConstructorProjectedValue {
    public InvalidConstructorProjectedValue() { Value = string.Empty; }
    public InvalidConstructorProjectedValue(int count) { Value = count.ToString(); }
    [System.Windows.Markup.ConstructorArgument("value")]
    public string Value { get; set; }
  }
  public sealed class AmbiguousConstructorProjectedValue {
    public AmbiguousConstructorProjectedValue() { First = string.Empty; }
    public AmbiguousConstructorProjectedValue(string first) { First = first; }
    public AmbiguousConstructorProjectedValue(int second) {
      First = string.Empty;
      Second = second;
    }
    [System.Windows.Markup.ConstructorArgument("first")]
    public string First { get; set; }
    [System.Windows.Markup.ConstructorArgument("second")]
    public int Second { get; set; }
  }
  [System.Windows.Markup.Ambient]
  public class AmbientDictionary : System.Collections.Generic.Dictionary<object, object> { }
  public sealed class DerivedAmbientDictionary : AmbientDictionary { }
  [Microsoft.UI.Xaml.Markup.ContentProperty(Name=nameof(Content))]
  public sealed class AmbientHost {
    [System.Windows.Markup.Ambient]
    public System.Collections.Generic.Dictionary<object, object> DeclaredContext { get; } = new();
    public AmbientDictionary TypedContext { get; } = new();
    public object? Content { get; set; }
  }
  public static class AmbientAttachedOwner {
    private static readonly System.Collections.Generic.Dictionary<object, object> Context = new();
    [System.Windows.Markup.Ambient]
    public static System.Collections.Generic.Dictionary<object, object> GetContext(Microsoft.UI.Xaml.FrameworkElement value) => Context;
    public static void SetContext(Microsoft.UI.Xaml.FrameworkElement value, System.Collections.Generic.Dictionary<object, object> context) { }
  }
  public sealed class DemoDeferredReader : System.Xaml.XamlReader { }
  public sealed class DemoDeferringLoader : System.Xaml.XamlDeferringLoader {
    public override object Load(System.Xaml.XamlReader xamlReader, System.IServiceProvider serviceProvider) => new object();
    public override System.Xaml.XamlReader Save(object value, System.IServiceProvider serviceProvider) => new DemoDeferredReader();
  }
  public abstract class AbstractDeferringLoader : System.Xaml.XamlDeferringLoader { }
  internal sealed class InternalDeferringLoader : System.Xaml.XamlDeferringLoader {
    public override object Load(System.Xaml.XamlReader xamlReader, System.IServiceProvider serviceProvider) => new object();
    public override System.Xaml.XamlReader Save(object value, System.IServiceProvider serviceProvider) => new DemoDeferredReader();
  }
  public sealed class WrongDeferringLoader {
    public object Load(int value, int serviceProvider) => new object();
    public int Save(object value, int serviceProvider) => 0;
  }
  public sealed class AmbiguousDeferringLoader {
    public object Load(System.Xaml.XamlReader xamlReader, System.IServiceProvider serviceProvider) => new object();
    public object Load(DemoDeferredReader xamlReader, System.IServiceProvider serviceProvider) => string.Empty;
    public System.Xaml.XamlReader Save(object value, System.IServiceProvider serviceProvider) => new DemoDeferredReader();
    public DemoDeferredReader Save(string value, System.IServiceProvider serviceProvider) => new DemoDeferredReader();
  }
  [System.Windows.Markup.XamlDeferLoad(typeof(DemoDeferringLoader), typeof(object))]
  public sealed class DeferredTemplateValue { }
  [System.Windows.Markup.XamlDeferLoad(typeof(AbstractDeferringLoader), typeof(object))]
  public sealed class AbstractDeferredValue { }
  [System.Windows.Markup.XamlDeferLoad(typeof(InternalDeferringLoader), typeof(object))]
  public sealed class InternalDeferredValue { }
  [System.Windows.Markup.XamlDeferLoad(typeof(WrongDeferringLoader), typeof(object))]
  public sealed class WrongDeferredValue { }
  [System.Windows.Markup.XamlDeferLoad(typeof(AmbiguousDeferringLoader), typeof(string))]
  public sealed class AmbiguousDeferredValue { }
  public sealed class TypedDeferredHost {
    public DeferredTemplateValue? Template { get; set; }
  }
  public sealed class OverrideDeferredHost {
    [System.Windows.Markup.XamlDeferLoad(typeof(DemoDeferringLoader), typeof(string))]
    public DeferredTemplateValue? Template { get; set; }
  }
  public sealed class MemberDeferredHost {
    [System.Windows.Markup.XamlDeferLoad("Demo.DemoDeferringLoader", "System.Object")]
    public object? Template { get; set; }
  }
  public sealed class InvalidDeferredHost {
    [System.Windows.Markup.XamlDeferLoad("Demo.MissingLoader", "System.Object")]
    public object? Template { get; set; }
  }
  public sealed class BracketExtension : Microsoft.UI.Xaml.Markup.MarkupExtension {
    [System.Windows.Markup.MarkupExtensionBracketCharacters('[', ']')]
    [System.Windows.Markup.MarkupExtensionBracketCharacters('(', ')')]
    public string? Path { get; set; }
    public string? Mode { get; set; }
    [System.Windows.Markup.MarkupExtensionBracketCharacters('[', ']')]
    [System.Windows.Markup.MarkupExtensionBracketCharacters('[', ')')]
    public string? Conflict { get; set; }
    protected override object ProvideValue() => new object();
  }
  public sealed class WrapperExtension : Microsoft.UI.Xaml.Markup.MarkupExtension {
    public object? Value { get; set; }
    protected override object ProvideValue() => Value ?? new object();
  }
  [Microsoft.UI.Xaml.Markup.MarkupExtensionReturnType(ReturnType=typeof(string))]
  public sealed class StringExtension : Microsoft.UI.Xaml.Markup.MarkupExtension { protected override object ProvideValue() => string.Empty; }
  [Microsoft.UI.Xaml.Markup.MarkupExtensionReturnType(ReturnType=typeof(int))]
  public sealed class IntegerExtension : Microsoft.UI.Xaml.Markup.MarkupExtension { protected override object ProvideValue() => 0; }
  public sealed class FakeExtension { }
  public sealed class AmbiguousExtension {
    public object ProvideValue() => string.Empty;
    public object CreateValue() => string.Empty;
  }
  public sealed class WrongServiceExtension { public object ProvideValue(int serviceProvider) => string.Empty; }
  public interface IMarkupExtension {
    object ProvideValue(Microsoft.UI.Xaml.IXamlServiceProvider serviceProvider);
  }
  public interface IMarkupExtension<out T> : IMarkupExtension {
    new T ProvideValue(Microsoft.UI.Xaml.IXamlServiceProvider serviceProvider);
  }
  public sealed class InterfaceValueExtension : IMarkupExtension<string> {
    public string ProvideValue(Microsoft.UI.Xaml.IXamlServiceProvider serviceProvider) => string.Empty;
    object IMarkupExtension.ProvideValue(Microsoft.UI.Xaml.IXamlServiceProvider serviceProvider) => ProvideValue(serviceProvider);
  }
  public interface IAvailableService { }
  public interface IMissingService { }
  [Microsoft.Maui.Controls.Xaml.RequireService(typeof(IAvailableService))]
  public sealed class RequiredMauiExtension : Microsoft.Maui.Controls.Xaml.IMarkupExtension<string> {
    public string ProvideValue(System.IServiceProvider serviceProvider) => string.Empty;
    object Microsoft.Maui.Controls.Xaml.IMarkupExtension.ProvideValue(System.IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
  }
  [Microsoft.Maui.Controls.Xaml.RequireService(typeof(IMissingService))]
  public sealed class MissingServiceMauiExtension : Microsoft.Maui.Controls.Xaml.IMarkupExtension<string> {
    public string ProvideValue(System.IServiceProvider serviceProvider) => string.Empty;
    object Microsoft.Maui.Controls.Xaml.IMarkupExtension.ProvideValue(System.IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
  }
  [Microsoft.Maui.Controls.Xaml.AcceptEmptyServiceProvider]
  public sealed class EmptyProviderMauiExtension : Microsoft.Maui.Controls.Xaml.IMarkupExtension<string> {
    public string ProvideValue(System.IServiceProvider serviceProvider) => string.Empty;
    object Microsoft.Maui.Controls.Xaml.IMarkupExtension.ProvideValue(System.IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
  }
  public sealed class UndeclaredServiceMauiExtension : Microsoft.Maui.Controls.Xaml.IMarkupExtension<string> {
    public string ProvideValue(System.IServiceProvider serviceProvider) => string.Empty;
    object Microsoft.Maui.Controls.Xaml.IMarkupExtension.ProvideValue(System.IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
  }
  [Microsoft.Maui.Controls.Xaml.AcceptEmptyServiceProvider]
  [Microsoft.Maui.Controls.Xaml.RequireService(typeof(IAvailableService))]
  public sealed class ConflictingServiceMauiExtension : Microsoft.Maui.Controls.Xaml.IMarkupExtension<string> {
    public string ProvideValue(System.IServiceProvider serviceProvider) => string.Empty;
    object Microsoft.Maui.Controls.Xaml.IMarkupExtension.ProvideValue(System.IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
  }
  [System.Windows.Markup.XamlSetMarkupExtension(nameof(ReceiveMarkupExtension))]
  [System.Windows.Markup.XamlSetTypeConverter(nameof(ReceiveTypeConverter))]
  public class InterceptingControl {
    public object? Value { get; set; }
    public Length Length { get; set; }
    public static void ReceiveMarkupExtension(object sender, System.Windows.Markup.XamlSetMarkupExtensionEventArgs eventArgs) { }
    internal static void ReceiveTypeConverter(object sender, System.Windows.Markup.XamlSetTypeConverterEventArgs eventArgs) { }
  }
  public sealed class HandlerValueExtension : System.Windows.Markup.MarkupExtension {
    public object ProvideValue() => "handled";
    public override object ProvideValue(System.IServiceProvider serviceProvider) => "handled";
  }
  public class LegacyReceiverControl : System.Windows.Markup.IReceiveMarkupExtension {
    public object? Value { get; set; }
    public void ReceiveMarkupExtension(string property, System.Windows.Markup.MarkupExtension markupExtension, System.IServiceProvider serviceProvider) { }
  }
  public sealed class DerivedLegacyReceiverControl : LegacyReceiverControl { }
  public sealed class DuckReceiverControl {
    public object? Value { get; set; }
    public void AcceptMarkup(string property, System.Windows.Markup.MarkupExtension markupExtension, System.IServiceProvider serviceProvider) { }
  }
  public sealed class InvalidDuckReceiverControl {
    public object? Value { get; set; }
    public static void AcceptMarkup(string property, System.Windows.Markup.MarkupExtension markupExtension, System.IServiceProvider serviceProvider) { }
  }
  [System.Windows.Markup.XamlSetMarkupExtension(nameof(HandleMarkup))]
  public sealed class AttributedReceiverControl : System.Windows.Markup.IReceiveMarkupExtension {
    public object? Value { get; set; }
    public void ReceiveMarkupExtension(string property, System.Windows.Markup.MarkupExtension markupExtension, System.IServiceProvider serviceProvider) { }
    public static void HandleMarkup(object sender, System.Windows.Markup.XamlSetMarkupExtensionEventArgs eventArgs) { }
  }
  public static class ObjectWriterRuntime {
    public static void ApplyMarkupExtension(
      object receiver,
      object extension,
      System.Action<object, System.Windows.Markup.XamlSetMarkupExtensionEventArgs> handler,
      string memberName,
      string? resourceUri) { }
    public static void ApplyTypeConverter(
      object receiver,
      System.Type converterType,
      string text,
      System.Action<object, System.Windows.Markup.XamlSetTypeConverterEventArgs> handler,
      string memberName,
      string? resourceUri) { }
  }
  public sealed class DerivedInterceptingControl : InterceptingControl { }
  [System.Windows.Markup.XamlSetMarkupExtension(nameof(ReceiveMarkupExtension))]
  public sealed class InvalidInterceptingControl {
    public object? Value { get; set; }
    public void ReceiveMarkupExtension(object sender, System.Windows.Markup.XamlSetMarkupExtensionEventArgs eventArgs) { }
  }
  [System.Windows.Markup.XamlSetTypeConverter("MissingHandler")]
  public sealed class MissingInterceptingControl { public object? Value { get; set; } }
  [System.Windows.Markup.XamlSetMarkupExtension(nameof(ReceiveMarkupExtension))]
  public sealed class PrivateInterceptingControl {
    public object? Value { get; set; }
    private static void ReceiveMarkupExtension(object sender, System.Windows.Markup.XamlSetMarkupExtensionEventArgs eventArgs) { }
  }
  public enum DisplayMode { Compact, Expanded }
  public sealed class ScalarControl { public byte Count { get; set; } public bool Enabled { get; set; } public DisplayMode Mode { get; set; } }
  public partial class MainPage : Microsoft.UI.Xaml.Controls.Page { }
}
""";

    [Fact]
    public void BindsInfosetToCanonicalSymbolsAndLowersExplicitConstructionOperations()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <StackPanel><TextBlock x:Name="Message" Text="Hello" /></StackPanel>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var bound = new XamlSemanticBinder().Bind(
            infoset,
            new RoslynXamlTypeSystem(compilation, profile));

        Assert.False(bound.HasErrors);
        var root = Assert.IsType<XamlBoundObject>(bound.Root);
        Assert.Equal("Microsoft.UI.Xaml.Controls.Page", root.Type.Symbol?.MetadataName);
        var content = Assert.Single(root.Members.Where(member => member.Origin == XamlMemberOrigin.ImplicitContent));
        Assert.Equal("Content", content.Member.Symbol?.Name);
        var panel = Assert.IsType<XamlBoundObject>(Assert.Single(content.Values));
        var children = Assert.Single(panel.Members);
        Assert.Equal("Children", children.Member.Symbol?.Name);

        var program = new XamlConstructionLowerer().Lower(bound);
        Assert.Equal(XamlIrObjectKind.ExistingRoot, program.Root?.Kind);
        var setContent = Assert.Single(program.Root!.Operations.Where(operation => operation.Kind == XamlIrOperationKind.SetMember));
        var panelIr = Assert.IsType<XamlIrObject>(Assert.Single(setContent.Values));
        Assert.Equal(XamlIrObjectKind.Create, panelIr.Kind);
        Assert.Contains(panelIr.Operations, operation => operation.Kind == XamlIrOperationKind.AddCollectionItem);
        Assert.Contains(panelIr.Operations.SelectMany(operation => operation.Values).OfType<XamlIrObject>()
            .SelectMany(child => child.Operations), operation => operation.Kind == XamlIrOperationKind.ApplyDirective);
    }

    [Fact]
    public void UnresolvedTypeAndMemberUseExplicitErrorReferences()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Missing="value">
  <UnknownControl />
</Page>
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile),
            new XamlSemanticBindingOptions { Strict = false });

        var root = Assert.IsType<XamlBoundObject>(bound.Root);
        var missing = Assert.Single(root.Members.Where(member => member.Member.RequestedName.LocalName == "Missing"));
        Assert.True(missing.Member.IsError);
        Assert.Equal("PGXAML2002", missing.Member.Diagnostic?.Id);
        var content = Assert.Single(root.Members.Where(member => member.Origin == XamlMemberOrigin.ImplicitContent));
        var unknown = Assert.IsType<XamlBoundObject>(Assert.Single(content.Values));
        Assert.True(unknown.Type.IsError);
        Assert.Equal("PGXAML2001", unknown.Type.Diagnostic?.Id);
    }

    [Fact]
    public void ValidatesRuntimeNameAliasAgainstXName()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Name="First" Name="Second" />
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile),
            new XamlSemanticBindingOptions { Strict = false });

        Assert.Contains(bound.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML2012" &&
            diagnostic.Properties["MSXamlSection"] == "6.2.2.1");
    }

    [Fact]
    public void XNameAloneDoesNotAliasItselfAsRuntimeName()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Name="OnlyName" />
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile),
            new XamlSemanticBindingOptions { Strict = false });

        Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2012");
    }

    [Fact]
    public void ValidatesNamesEventsDictionaryKeysAndFieldModifiers()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <StackPanel>
    <TextBlock x:Name="Duplicate" />
    <TextBlock x:Name="Duplicate" />
    <TextBlock x:FieldModifier="private" />
    <Button Click="OnClick" x:Key="NotInDictionary" />
  </StackPanel>
</Page>
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile),
            new XamlSemanticBindingOptions { Strict = false });

        Assert.Contains(bound.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2013");
        Assert.Contains(bound.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2014");
        Assert.Contains(bound.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2015");
        Assert.Contains(bound.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2016");
    }

    [Fact]
    public void DeferredTemplateContentCreatesIndependentNameScopes()
    {
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ControlTemplate x:Key="First">
    <StackPanel x:Name="Root"><TextBlock x:Name="Label" /></StackPanel>
  </ControlTemplate>
  <ControlTemplate x:Key="Second">
    <StackPanel x:Name="Root"><TextBlock x:Name="Label" /></StackPanel>
  </ControlTemplate>
</ResourceDictionary>
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile),
            new XamlSemanticBindingOptions { Strict = false });

        Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2013");
    }

    [Fact]
    public void DuplicateNameInsideOneDeferredTemplateScopeIsRejected()
    {
        const string xaml = """
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <StackPanel>
    <TextBlock x:Name="Label" />
    <TextBlock x:Name="Label" />
  </StackPanel>
</ControlTemplate>
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile),
            new XamlSemanticBindingOptions { Strict = false });

        Assert.Contains(bound.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2013");
    }

    [Fact]
    public void WinUiDialectDirectivesAreProfileOwnedAndUnknownXNamesRemainErrors()
    {
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(CreateCompilation(), profile);
        var bound = new XamlSemanticBinder().Bind(Convert("""
<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
           xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
           x:Load="False"
           x:DeferLoadStrategy="Lazy" />
"""), typeSystem);

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        Assert.Equal(2, bound.Root!.Members.Count(member =>
            member.Member.Kind == XamlBoundReferenceKind.Directive));

        var misspelled = new XamlSemanticBinder().Bind(Convert("""
<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
           xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
           x:Laod="False" />
"""), typeSystem, new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(misspelled.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2002");
    }

    [Fact]
    public void IntrinsicNullIsSchemaDataAndLowersAsMarkupInvocation()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Content="{x:Null}" />
""";
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(CreateCompilation(), profile);
        var bound = new XamlSemanticBinder().Bind(
            infoset,
            typeSystem);

        Assert.False(bound.HasErrors);
        var content = Assert.Single(Assert.IsType<XamlBoundObject>(bound.Root).Members);
        var nullValue = Assert.IsType<XamlBoundObject>(Assert.Single(content.Values));
        Assert.Equal("x:Null", nullValue.Type.Symbol?.MetadataName);
        var ir = new XamlConstructionLowerer().Lower(bound);
        var operation = Assert.Single(ir.Root!.Operations);
        Assert.Equal(XamlIrObjectKind.InvokeMarkupExtension, Assert.IsType<XamlIrObject>(Assert.Single(operation.Values)).Kind);
    }

    [Fact]
    public void IntrinsicTypeArgumentBindsToCanonicalTypeAndEmitsTypeOfSyntax()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage"
      Content="{x:Type TextBlock}" />
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);

        Assert.False(bound.HasErrors);
        var content = Assert.Single(Assert.IsType<XamlBoundObject>(bound.Root).Members,
            member => member.Member.Symbol?.Name == "Content");
        var extension = Assert.IsType<XamlBoundObject>(Assert.Single(content.Values));
        var positional = Assert.Single(extension.Members);
        var typeValue = Assert.IsType<XamlBoundTypeValue>(Assert.Single(positional.Values));
        Assert.Equal("Microsoft.UI.Xaml.Controls.TextBlock", typeValue.Type.Symbol?.MetadataName);

        var emitted = new CSharpXamlEmitter().Emit(infoset, typeSystem, profile, new ProGPU.Xaml.Schema.XamlCompilerOptions());
        Assert.Contains("typeof(global::Microsoft.UI.Xaml.Controls.TextBlock)", Assert.Single(emitted.Sources).Source, StringComparison.Ordinal);
    }

    [Fact]
    public void IntrinsicStaticArgumentBindsPublicStaticSymbolAndEmitsMemberAccess()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <TextBlock Text="{x:Static local:Palette.Greeting}" />
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);

        Assert.False(bound.HasErrors);
        var staticValue = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundStaticMemberValue>());
        Assert.Equal("Greeting", staticValue.Member?.Name);
        var emitted = new CSharpXamlEmitter().Emit(infoset, typeSystem, profile, new ProGPU.Xaml.Schema.XamlCompilerOptions());
        Assert.Contains("global::Demo.Palette.Greeting", Assert.Single(emitted.Sources).Source, StringComparison.Ordinal);
    }

    [Fact]
    public void IntrinsicArrayUsesOrdinaryTypeMemberAndTypedArrayConstructionIr()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <x:Array Type="{x:Type TextBlock}">
    <TextBlock Text="First" />
    <TextBlock Text="Second" />
  </x:Array>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var arrayInfoset = Assert.Single(
            Assert.Single(
                infoset.Root!.Members,
                member => member.Origin == XamlMemberOrigin.ImplicitContent)
                .Values.OfType<XamlInfosetObject>());
        Assert.False(Assert.Single(arrayInfoset.Members, member => member.Name.LocalName == "Type").Name.IsDirective);

        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var program = new XamlConstructionLowerer().Lower(bound);
        var arrayIr = Assert.Single(DescendantIr(program.Root), value => value.Kind == XamlIrObjectKind.CreateArray);
        Assert.Contains(arrayIr.Operations, operation => operation.Kind == XamlIrOperationKind.SetIntrinsicMember);
        Assert.Contains(arrayIr.Operations, operation => operation.Kind == XamlIrOperationKind.AddCollectionItem);

        var emitted = new CSharpXamlEmitter().EmitProgram(program, profile, new ProGPU.Xaml.Schema.XamlCompilerOptions());
        Assert.Contains("new global::Microsoft.UI.Xaml.Controls.TextBlock[]", Assert.Single(emitted.Sources).Source, StringComparison.Ordinal);
    }

    [Fact]
    public void IntrinsicArrayValidityRejectsMissingTypeAndIncompatibleItems()
    {
        const string missingType = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><x:Array /></Page>
""";
        const string incompatibleItem = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:Array Type="{x:Type TextBlock}"><Button /></x:Array>
</Page>
""";
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(CreateCompilation(), profile);

        var missing = new XamlSemanticBinder().Bind(Convert(missingType), typeSystem, new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2021" && diagnostic.Properties["MSXamlSection"] == "6.2.2.10");
        var incompatible = new XamlSemanticBinder().Bind(Convert(incompatibleItem), typeSystem, new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(incompatible.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2022" && diagnostic.Properties["MSXamlSection"] == "6.2.2.10");
    }

    [Fact]
    public void OrdinarySingleValueMemberRejectsMultipleValues()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Page.Content><TextBlock /><Button /></Page.Content>
</Page>
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile),
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(bound.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML2023" && diagnostic.Properties["MSXamlSection"] == "6.3.1.2");
    }

    [Fact]
    public void XArgumentsBindConstructorMetadataAndEmitTypedArguments()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:Widget>
    <x:Arguments><x:Int32>42</x:Int32></x:Arguments>
  </local:Widget>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var widgetInfo = Assert.Single(
            Assert.Single(
                infoset.Root!.Members,
                member => member.Origin == XamlMemberOrigin.ImplicitContent)
                .Values.OfType<XamlInfosetObject>());
        var argumentsInfo = Assert.Single(widgetInfo.Members, member => member.Name.LocalName == "Arguments");
        Assert.True(argumentsInfo.Name.IsDirective);
        Assert.Equal(XamlMemberOrigin.MemberElement, argumentsInfo.Origin);

        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        Assert.False(typeSystem.ResolveType("using:Demo", "Widget")!.IsDefaultConstructible);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var emitted = new CSharpXamlEmitter().Emit(infoset, typeSystem, profile, new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var creation = Assert.Single(Assert.Single(emitted.Sources).GeneratedSyntaxTree!.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>(),
            expression => expression.Type.ToString() == "global::Demo.Widget");
        Assert.Equal("42", Assert.Single(creation.ArgumentList!.Arguments).Expression.ToString());
        var generatedCompilation = compilation.AddSyntaxTrees(Assert.Single(emitted.Sources).GeneratedSyntaxTree!);
        Assert.DoesNotContain(generatedCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var missingArguments = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><local:Widget /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(missingArguments.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2024");
    }

    [Fact]
    public void XFactoryMethodBindsStaticFactoryAndEmitsInvocation()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:Widget x:FactoryMethod="Create">
    <x:Arguments><x:Int32>7</x:Int32></x:Arguments>
  </local:Widget>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var factory = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundFactoryMethodValue>());
        Assert.Equal("Create", factory.Method?.Name);

        var emitted = new CSharpXamlEmitter().Emit(infoset, typeSystem, profile, new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var invocation = Assert.Single(Assert.Single(emitted.Sources).GeneratedSyntaxTree!.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(),
            expression => expression.Expression.ToString().Contains("Demo.Widget.Create", StringComparison.Ordinal));
        Assert.Equal("7", Assert.Single(invocation.ArgumentList.Arguments).Expression.ToString());
        Assert.DoesNotContain(compilation.AddSyntaxTrees(Assert.Single(emitted.Sources).GeneratedSyntaxTree!).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void DictionaryValidityRejectsMissingAndDuplicateKeys()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Page.Resources>
    <ResourceDictionary>
      <TextBlock x:Key="Repeated" />
      <Button x:Key="Repeated" />
      <TextBlock />
    </ResourceDictionary>
  </Page.Resources>
</Page>
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile),
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(bound.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML2028" && diagnostic.Properties["MSXamlSection"] == "6.3.1.4");
        Assert.Contains(bound.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML2029" && diagnostic.Properties["MSXamlSection"] == "6.3.1.4");
    }

    [Fact]
    public void XTypeArgumentsConstructCanonicalGenericRoslynType()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:Box x:TypeArguments="x:String"><x:String>Hello</x:String></local:Box>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var box = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundObject>(),
            value => value.Type.Symbol?.Name == "Box");
        Assert.True(box.Type.Symbol!.IsGeneric);
        Assert.Equal(SpecialType.System_String, Assert.IsAssignableFrom<INamedTypeSymbol>(box.Type.Symbol.Symbol).TypeArguments[0].SpecialType);
        Assert.Contains("System.String", box.Type.Symbol.MetadataName, StringComparison.Ordinal);

        var emitted = new CSharpXamlEmitter().Emit(infoset, typeSystem, profile, new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var generatedTree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        Assert.Contains(generatedTree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>(),
            creation => creation.Type.ToString().Contains("Box<string>", StringComparison.Ordinal));
        Assert.DoesNotContain(compilation.AddSyntaxTrees(generatedTree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var invalid = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"using:Demo\"><local:ReferenceBox x:TypeArguments=\"x:Int32\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2032");
    }

    [Fact]
    public void RepeatableDependsOnMetadataIsPreservedValidatedAndOrdersConstruction()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
        x:Class="Demo.MainPage">
  <local:OrderedControl Third="3" Second="2" First="1" />
</Page>
""";
        var profile = new WinUiXamlProfile();
        var compilation = CreateCompilation();
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile, new DependencyMetadataProvider());
        var bound = new XamlSemanticBinder().Bind(Convert(xaml), typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var ordered = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundObject>(),
            value => value.Type.Symbol?.Name == "OrderedControl");
        var third = Assert.Single(ordered.Members, member => member.Member.Symbol?.Name == "Third");
        Assert.Equal(2, third.Member.Symbol!.Annotations.Count(annotation =>
            annotation.Semantic == ProGPU.Xaml.Schema.XamlSchemaSemantics.DependsOn));
        Assert.All(third.Member.Symbol.Annotations.Where(annotation =>
            annotation.Semantic == ProGPU.Xaml.Schema.XamlSchemaSemantics.DependsOn), annotation =>
        {
            Assert.True(annotation.ValueConstant.HasValue);
            Assert.Equal(TypedConstantKind.Primitive, annotation.ValueConstant.Value.Kind);
            Assert.Equal(1000, annotation.ProviderPriority);
        });
        Assert.Equal(2, third.Member.Symbol.Dependencies.Count);
        Assert.All(
            third.Member.Symbol.Dependencies,
            dependency =>
            {
                Assert.True(dependency.IsValid, dependency.Error);
                Assert.Equal("tests.dependencies", dependency.ProviderId);
                Assert.Equal(
                    "Demo.OrderedControl",
                    dependency.Dependency!.ContainingType.ToDisplayString());
            });
        Assert.Equal(
            new[] { "First", "Second" },
            third.Member.Symbol.Dependencies
                .Select(dependency => dependency.Dependency!.Name)
                .OrderBy(name => name, StringComparer.Ordinal));

        var ir = Assert.Single(DescendantIr(new XamlConstructionLowerer().Lower(bound).Root),
            value => value.Type.Symbol?.Name == "OrderedControl");
        Assert.Equal(new[] { "First", "Second", "Third" },
            ir.Operations.Where(operation => operation.Member.Symbol != null)
                .Select(operation => operation.Member.Symbol!.Name));
        Assert.Equal(
            new[] { "First", "Second", "Third" },
            Assert.Single(
                    bound.SerializationPlans.Objects.Values,
                    plan => plan.Type?.Name == "OrderedControl")
                .Members
                .Where(item => item.Member != null)
                .Select(item => item.Member!.Name));

        var broken = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><local:BrokenOrderControl Value=\"x\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(broken.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2033");
        var cyclic = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><local:CyclicOrderControl First=\"1\" Second=\"2\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(cyclic.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2034");

        using var image = new System.IO.MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "DependencyMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            profile,
            new DependencyMetadataProvider());
        var metadataType = metadataTypeSystem.ResolveType(
            "using:Demo",
            "OrderedControl")!;
        var metadataDependencies = metadataTypeSystem.ResolveMember(
            metadataType,
            "using:Demo",
            ownerTypeName: null,
            memberName: "Third")!.Dependencies;
        Assert.Equal(2, metadataDependencies.Count);
        Assert.All(
            metadataDependencies,
            dependency => Assert.All(
                dependency.Dependency!.Locations,
                location => Assert.True(location.IsInMetadata)));
    }

    [Fact]
    public void CompilerUsageAnnotationsProduceXamlLocatedDiagnostics()
    {
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(CreateCompilation(), profile);
        XamlBoundDocument Bind(string body) => new XamlSemanticBinder().Bind(Convert(
            "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\">" + body + "</Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });

        var obsoleteType = Bind("<local:LegacyControl />");
        Assert.Contains(obsoleteType.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML2035" && diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Location.GetLineSpan().Path == "Binding.xaml");
        var obsoleteMember = Bind("<local:AnnotationControl Old=\"value\" />");
        Assert.Contains(obsoleteMember.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML2035" && diagnostic.Severity == DiagnosticSeverity.Error);
        var experimental = Bind("<local:PreviewControl />");
        Assert.Contains(experimental.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML2036" && diagnostic.GetMessage().Contains("DEMO001", StringComparison.Ordinal));
    }

    [Fact]
    public void AttributeBasedDirectiveAliasesResolveToCanonicalMembers()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:DirectiveAliasControl x:Uid="control-1" xml:lang="en-US" />
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile, new DependencyMetadataProvider());
        var infoset = Convert(xaml);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var control = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundObject>(),
            value => value.Type.Symbol?.Name == "DirectiveAliasControl");
        Assert.Equal("Scope", control.Type.Symbol!.NameScopeMemberName);
        Assert.True(control.Type.Symbol.NameScopeProperty!.IsValid);
        Assert.Equal(
            "Scope",
            control.Type.Symbol.NameScopeProperty.Property!.Name);
        Assert.Equal(
            "tests.dependencies",
            control.Type.Symbol.NameScopeProperty.ProviderId);
        Assert.Equal(
            "Language",
            control.Type.Symbol.XmlLanguageProperty!.Property!.Name);
        Assert.Equal(
            "AutomationId",
            control.Type.Symbol.UidProperty!.Property!.Name);
        Assert.Contains(control.Members, member =>
            member.Member.RequestedName.LocalName == "Uid" && member.Member.Symbol?.Name == "AutomationId");
        Assert.Contains(control.Members, member =>
            member.Member.RequestedName.LocalName == "lang" && member.Member.Symbol?.Name == "Language");

        var attachedType = typeSystem.ResolveType(
            "using:Demo",
            "AttachedDirectiveAliasControl")!;
        var attachedShape = attachedType.NameScopeProperty!;
        Assert.True(attachedShape.IsValid, attachedShape.Error);
        Assert.Equal(
            "Demo.AttachedScopeOwner",
            attachedShape.OwnerType!.ToDisplayString());
        Assert.Equal("GetScope", attachedShape.AttachableGetter!.Name);
        Assert.Equal("SetScope", attachedShape.AttachableSetter!.Name);
        Assert.Null(attachedShape.Property);

        var invalid = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo"><local:InvalidDirectiveAliasControl /></Page>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(
            invalid.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2058");

        var emitted = new CSharpXamlEmitter().Emit(infoset, typeSystem, profile, new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var source = Assert.Single(emitted.Sources).Source;
        Assert.Contains(".AutomationId = \"control-1\"", source, StringComparison.Ordinal);
        Assert.Contains(".Language = \"en-US\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(Assert.Single(emitted.Sources).GeneratedSyntaxTree!).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        using var image = new System.IO.MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "DirectiveAliasMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            profile,
            new DependencyMetadataProvider());
        var metadataShape = metadataTypeSystem.ResolveType(
            "using:Demo",
            "AttachedDirectiveAliasControl")!.NameScopeProperty!;
        Assert.All(
            metadataShape.OwnerType!.Locations,
            location => Assert.True(location.IsInMetadata));
        Assert.All(
            metadataShape.AttachableGetter!.Locations,
            location => Assert.True(location.IsInMetadata));
        Assert.All(
            metadataShape.AttachableSetter!.Locations,
            location => Assert.True(location.IsInMetadata));
        var metadataNameScope = metadataTypeSystem.ResolveType(
            "using:Demo",
            "InterfaceNameScopeControl")!.NameScopeShape!;
        Assert.All(
            metadataNameScope.IdentityType!.Locations,
            location => Assert.True(location.IsInMetadata));
        Assert.All(
            metadataNameScope.RegisterNameMethod!.Locations,
            location => Assert.True(location.IsInMetadata));
    }

    [Fact]
    public void ProfileNameScopeInterfaceAndDuckShapesCreateExactSemanticBoundaries()
    {
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new DependencyMetadataProvider());
        var interfaceType = typeSystem.ResolveType(
            "using:Demo",
            "InterfaceNameScopeControl")!;
        var interfaceShape = interfaceType.NameScopeShape!;
        Assert.True(interfaceShape.IsValid, interfaceShape.Error);
        Assert.True(interfaceType.IsNameScope);
        Assert.Equal(
            XamlNameScopeIdentityKind.Interface,
            interfaceShape.IdentityKind);
        Assert.Equal(
            "Demo.ITestNameScope",
            interfaceShape.IdentityType!.ToDisplayString());
        Assert.Equal(
            "RegisterName",
            interfaceShape.RegisterNameMethod!.Name);
        Assert.Equal(
            "UnregisterName",
            interfaceShape.UnregisterNameMethod!.Name);
        Assert.Equal(
            "FindName",
            interfaceShape.FindNameMethod!.Name);
        Assert.Equal("tests.dependencies", interfaceShape.ProviderId);

        var duckType = typeSystem.ResolveType(
            "using:Demo",
            "DuckNameScopeControl")!;
        Assert.True(duckType.IsNameScope);
        Assert.Equal(
            XamlNameScopeIdentityKind.DuckMethods,
            duckType.NameScopeShape!.IdentityKind);
        Assert.Equal(
            "Demo.DuckNameScopeControl",
            duckType.NameScopeShape.IdentityType!.ToDisplayString());

        var storageOnly = typeSystem.ResolveType(
            "using:Demo",
            "DirectiveAliasControl")!;
        Assert.NotNull(storageOnly.NameScopeProperty);
        Assert.Null(storageOnly.NameScopeShape);
        Assert.False(storageOnly.IsNameScope);

        var isolated = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo">
  <StackPanel>
    <local:InterfaceNameScopeControl>
      <StackPanel>
        <TextBlock x:Name="Same" />
        <local:ReferenceHolder Target="{x:Reference Same}" />
      </StackPanel>
    </local:InterfaceNameScopeControl>
    <local:InterfaceNameScopeControl><TextBlock x:Name="Same" /></local:InterfaceNameScopeControl>
    <local:DuckNameScopeControl><TextBlock x:Name="DuckSame" /></local:DuckNameScopeControl>
    <local:DuckNameScopeControl><TextBlock x:Name="DuckSame" /></local:DuckNameScopeControl>
  </StackPanel>
</Page>
"""),
            typeSystem);
        Assert.DoesNotContain(
            isolated.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2013" ||
                          diagnostic.Id == "PGXAML2039");

        var duplicate = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo">
  <local:InterfaceNameScopeControl>
    <StackPanel>
      <TextBlock x:Name="Same" />
      <TextBlock x:Name="Same" />
    </StackPanel>
  </local:InterfaceNameScopeControl>
</Page>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(
            duplicate.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2013");

        var invalidDuck = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo"><local:InvalidDuckNameScopeControl /></Page>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(
            invalidDuck.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2060");

        var customTypeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new NameScopeShapeMetadataProvider(
                "tests.custom-namescope",
                2000,
                registerName: "AddName",
                unregisterName: "RemoveName",
                findName: "LookupName",
                inferDuck: true));
        var customShape = customTypeSystem.ResolveType(
            "using:Demo",
            "CustomDuckNameScopeControl")!.NameScopeShape!;
        Assert.True(customShape.IsValid, customShape.Error);
        Assert.Equal("AddName", customShape.RegisterNameMethod!.Name);
        Assert.Equal("RemoveName", customShape.UnregisterNameMethod!.Name);
        Assert.Equal("LookupName", customShape.FindNameMethod!.Name);

        var conflictingTypeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new NameScopeShapeMetadataProvider(
                "tests.namescope-a",
                3000,
                interfaceName: "Demo.ITestNameScope"),
            new NameScopeShapeMetadataProvider(
                "tests.namescope-b",
                3000,
                interfaceName: "Demo.IAlternateNameScope"));
        var conflictingShape = conflictingTypeSystem.ResolveType(
            "using:Demo",
            "ConflictingNameScopeControl")!.NameScopeShape!;
        Assert.False(conflictingShape.IsValid);
        Assert.Contains(
            "incompatible equal-priority",
            conflictingShape.Error,
            StringComparison.Ordinal);
        var conflicting = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo"><local:ConflictingNameScopeControl /></Page>
"""),
            conflictingTypeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(
            conflicting.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2060");
    }

    [Fact]
    public void AvaloniaMarkupExtensionOptionsAreCanonicalTypedMemberMetadata()
    {
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new MarkupOptionMetadataProvider());
        var extensionType = typeSystem.ResolveType(
            "using:Demo",
            "Option")!;
        var defaultMember = typeSystem.ResolveMember(
            extensionType,
            "using:Demo",
            ownerTypeName: null,
            memberName: "Default")!;
        var defaultOption = defaultMember.MarkupExtensionOption!;
        Assert.True(defaultOption.IsValid, defaultOption.Error);
        Assert.True(defaultOption.IsDefault);
        Assert.False(defaultOption.OptionValue.HasValue);
        Assert.Equal("tests.avalonia-options", defaultOption.ProviderId);
        Assert.Equal(defaultMember.Symbol, defaultOption.Annotation.DeclaredOn);
        Assert.Equal(defaultMember.Symbol, defaultOption.Property);

        var x64Member = typeSystem.ResolveMember(
            extensionType,
            "using:Demo",
            ownerTypeName: null,
            memberName: "X64")!;
        var x64Option = x64Member.MarkupExtensionOption!;
        Assert.True(x64Option.IsValid, x64Option.Error);
        Assert.False(x64Option.IsDefault);
        Assert.Equal(TypedConstantKind.Primitive, x64Option.OptionValue!.Value.Kind);
        Assert.Equal("X64", x64Option.OptionValue.Value.Value);
        Assert.Equal(20, x64Option.Priority);
        var selector = extensionType.MarkupExtensionOptionSelector!;
        Assert.True(selector.IsValid, selector.Error);
        Assert.Equal("ShouldProvideOption", selector.Method!.Name);
        Assert.Equal(SpecialType.System_String, selector.OptionType!.SpecialType);
        Assert.False(selector.RequiresServiceProvider);
        Assert.Equal("tests.avalonia-options", selector.ProviderId);
        Assert.Contains(
            selector.Options,
            option => SymbolEqualityComparer.Default.Equals(option.Property, x64Member.Symbol));
        Assert.Equal(2, selector.Candidates.Count);

        var serviceExtensionType = typeSystem.ResolveType(
            "using:Demo",
            "ServiceOption")!;
        var serviceSelector = serviceExtensionType.MarkupExtensionOptionSelector!;
        Assert.True(serviceSelector.IsValid, serviceSelector.Error);
        Assert.True(serviceSelector.RequiresServiceProvider);
        Assert.Equal(
            "System.IServiceProvider",
            serviceSelector.ServiceProviderType!.ToDisplayString());
        Assert.Equal(
            "Demo.OptionKind",
            serviceSelector.OptionType!.ToDisplayString());
        Assert.Equal(2, serviceSelector.Method!.Parameters.Length);

        var valid = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo"
      Content="{local:Option Default=white, X64=green}" />
"""),
            typeSystem);
        Assert.False(valid.HasErrors, string.Join(Environment.NewLine, valid.Diagnostics));

        var invalid = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo"
      Content="{local:Option Conflict=value}" />
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(
            invalid.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2061");
        var invalidSelector = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo"
      Content="{local:InvalidSelector Desktop=value}" />
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(
            invalidSelector.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2062");

        var emitted = new CSharpXamlEmitter().Emit(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage"
      Content="{local:Option Default=white, X64=green}" />
"""),
            typeSystem,
            new OptionEmissionProfile(),
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        Assert.DoesNotContain(
            emitted.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(emitted.Sources);
        Assert.Contains(
            "global::Demo.OptionExtension.ShouldProvideOption(\"X64\")",
            generated.Source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(generated.GeneratedSyntaxTree!).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        using var image = new System.IO.MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "MarkupOptionMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            profile,
            new MarkupOptionMetadataProvider());
        var metadataType = metadataTypeSystem.ResolveType(
            "using:Demo",
            "Option")!;
        var metadataOption = metadataTypeSystem.ResolveMember(
            metadataType,
            "using:Demo",
            ownerTypeName: null,
            memberName: "X64")!.MarkupExtensionOption!;
        Assert.All(
            metadataOption.Annotation.DeclaredOn.Locations,
            location => Assert.True(location.IsInMetadata));
        var metadataSelector = metadataType.MarkupExtensionOptionSelector!;
        Assert.True(metadataSelector.IsValid, metadataSelector.Error);
        Assert.All(
            metadataSelector.Method!.Locations,
            location => Assert.True(location.IsInMetadata));

        var defaultRule = Assert.Single(
            XamlSchemaAttributeCatalog.Avalonia,
            rule => rule.Semantic == XamlSchemaSemantics.MarkupExtensionDefaultOption);
        Assert.Equal(XamlSchemaAttributeTargets.Member, defaultRule.Targets);
    }

    [Fact]
    public void AvaloniaListMetadataProducesReusableSpanPreservingSplitter()
    {
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new AvaloniaListMetadataProvider());
        var listType = typeSystem.ResolveType("using:Demo", "TokenList")!;
        var split = listType.ListSplit!;
        Assert.True(split.IsValid, split.Error);
        Assert.Equal("tests.avalonia-list", split.ProviderId);
        Assert.Equal(listType.Symbol, split.DeclaringType);
        Assert.Equal(new[] { "::", ";", "|" }, split.Separators);
        Assert.True(split.RemovesEmptyEntries);
        Assert.True(split.TrimsEntries);

        const string source = " one :: ::two| three ;four";
        var items = split.Split(source);
        Assert.Equal(new[] { "one", "two", "three", "four" },
            items.Select(static item => item.Text));
        Assert.Equal("one", source.Substring(
            items[0].SourceSpan.Start,
            items[0].SourceSpan.Length));
        Assert.Equal("two", source.Substring(
            items[1].SourceSpan.Start,
            items[1].SourceSpan.Length));
        Assert.Equal(" three ", items[2].RawText);

        var invalid = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo"><local:InvalidTokenList /></Page>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(
            invalid.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2063");

        var infoset = Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:ListHost Items=" one :: ::two| three ;four" />
</Page>
""");
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var itemsMember = Assert.Single(
            DescendantValues(bound.Root)
                .OfType<XamlBoundObject>()
                .Single(value => value.Type.Symbol?.MetadataName == "Demo.ListHost")
                .Members,
            member => member.Member.Symbol?.Name == "Items");
        Assert.Equal(
            new[] { "one", "two", "three", "four" },
            itemsMember.Values.OfType<XamlBoundText>().Select(static item => item.Text));
        var program = new XamlConstructionLowerer().Lower(bound);
        var emittedProgram = new CSharpXamlEmitter().EmitProgram(
            program,
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        Assert.DoesNotContain(
            emittedProgram.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generatedProgram = Assert.Single(emittedProgram.Sources);
        Assert.Contains(".Items.Add(\"one\")", generatedProgram.Source, StringComparison.Ordinal);
        Assert.Contains(".Items.Add(\"four\")", generatedProgram.Source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(generatedProgram.GeneratedSyntaxTree!).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        using var image = new System.IO.MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "AvaloniaListMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var metadataList = new RoslynXamlTypeSystem(
            metadataCompilation,
            profile,
            new AvaloniaListMetadataProvider())
            .ResolveType("using:Demo", "TokenList")!.ListSplit!;
        Assert.True(metadataList.IsValid, metadataList.Error);
        Assert.All(
            metadataList.Annotation.DeclaredOn.Locations,
            location => Assert.True(location.IsInMetadata));

        var rule = Assert.Single(
            XamlSchemaAttributeCatalog.Avalonia,
            candidate => candidate.Semantic == XamlSchemaSemantics.ListSeparator);
        Assert.Equal(XamlSchemaAttributeTargets.Type, rule.Targets);
        Assert.True(rule.Inherited);
    }

    [Fact]
    public void AvaloniaBindingAnnotationsDriveDataTypeContextsAndStructuredAssignment()
    {
        var compilation = CreateCompilation();
        var framework = new BindingAnnotationEmissionProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            framework,
            new WinUiXamlProfile(),
            new BindingAnnotationMetadataProvider());
        var hostType = typeSystem.ResolveType("using:Demo", "BindingHost")!;
        var dataType = typeSystem.ResolveMember(
            hostType, "using:Demo", null, "DataType")!;
        Assert.True(dataType.DataTypeSource!.IsValid, dataType.DataTypeSource.Error);
        Assert.Equal("DataType", dataType.DataTypeSource.Property!.Name);

        var itemTemplate = typeSystem.ResolveMember(
            hostType, "using:Demo", null, "ItemTemplate")!;
        var itemsInheritance = itemTemplate.ItemsDataTypeInheritance!;
        Assert.True(itemsInheritance.IsValid, itemsInheritance.Error);
        Assert.Equal("Items", itemsInheritance.AncestorItemsPropertyName);
        Assert.Equal("Demo.BindingHost", itemsInheritance.LookupType!.ToDisplayString());
        Assert.Equal("Items", itemsInheritance.AncestorItemsProperty!.Name);

        var assigned = typeSystem.ResolveMember(
            hostType, "using:Demo", null, "AssignedBinding")!;
        Assert.True(assigned.AssignsBindingObject);
        Assert.IsAssignableFrom<IPropertySymbol>(assigned.BindingAssignment!.Target);
        var attachedAssigned = typeSystem.ResolveMember(
            hostType, "using:Demo", "BindingAttachedOwner", "Assigned")!;
        Assert.True(attachedAssigned.AssignsBindingObject);
        Assert.Equal(
            "SetAssigned",
            Assert.IsAssignableFrom<IMethodSymbol>(
                attachedAssigned.BindingAssignment!.Target).Name);

        var scopeType = typeSystem.ResolveType("using:Demo", "ScopeExtension")!;
        var scopeMember = typeSystem.ResolveMember(
            scopeType, "using:Demo", null, "Property")!;
        Assert.Equal(
            XamlDataTypeScopeKind.ControlTemplate,
            scopeMember.DataTypeInheritance!.ScopeKind);
        var parameterType = typeSystem.ResolveType(
            "using:Demo", "ParameterScopeExtension")!;
        var parameter = Assert.Single(Assert.Single(parameterType.Constructors).Parameters);
        Assert.Equal(
            XamlDataTypeScopeKind.Style,
            parameter.DataTypeInheritance!.ScopeKind);
        Assert.Equal("property", parameter.DataTypeInheritance.Target!.Name);

        var bound = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:BindingHost DataType="{x:Type local:Widget}"
                     Items="{x:Type local:Widget}"
                     ItemTemplate="{x:Type local:Widget}"
                     AssignedBinding="{local:Binding Path=Name}" />
</Page>
"""),
            typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var boundHost = DescendantValues(bound.Root)
            .OfType<XamlBoundObject>()
            .Single(value => value.Type.Symbol?.MetadataName == "Demo.BindingHost");
        var boundTemplate = Assert.Single(
            boundHost.Members,
            member => member.Member.Symbol?.Name == "ItemTemplate");
        var context = bound.DataTypeContexts.GetContext(boundTemplate.StableId);
        Assert.Equal(XamlDataTypeScopeKind.ControlTemplate,
            scopeMember.DataTypeInheritance.ScopeKind);
        Assert.Equal(
            itemsInheritance.AncestorItemsProperty,
            context.ItemsInheritance!.AncestorItemsProperty,
            SymbolEqualityComparer.Default);
        Assert.Contains(
            context.Sources,
            source => source.Origin == XamlDataTypeSourceOrigin.AttributedMember &&
                      source.Member.Member.Symbol?.Name == "DataType");
        Assert.Contains(
            context.Sources,
            source => source.Origin == XamlDataTypeSourceOrigin.AncestorItems &&
                      source.Member.Member.Symbol?.Name == "Items");

        var program = new XamlConstructionLowerer().Lower(bound);
        var emitted = new CSharpXamlEmitter().EmitProgram(
            program,
            framework,
            new XamlCompilerOptions());
        Assert.DoesNotContain(
            emitted.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var source = Assert.Single(emitted.Sources);
        var assignment = source.GeneratedSyntaxTree!.GetRoot()
            .DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(node => node.Left.ToString().EndsWith(
                ".AssignedBinding",
                StringComparison.Ordinal));
        Assert.IsType<IdentifierNameSyntax>(assignment.Right);
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(source.GeneratedSyntaxTree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var rejected = new CSharpXamlEmitter().EmitProgram(
            program,
            new WinUiXamlProfile(),
            new XamlCompilerOptions());
        Assert.Contains(
            rejected.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML3042");

        var invalid = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo">
  <local:InvalidDataTypeHost DataType="x" Scope="x" ItemTemplate="x" />
</Page>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2064");
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2065");
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2066");

        using var image = new MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "BindingAnnotationMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            framework,
            new WinUiXamlProfile(),
            new BindingAnnotationMetadataProvider());
        var metadataHost = metadataTypeSystem.ResolveType(
            "using:Demo", "BindingHost")!;
        var metadataDataType = metadataTypeSystem.ResolveMember(
            metadataHost, "using:Demo", null, "DataType")!
            .DataTypeSource!;
        var metadataItems = metadataTypeSystem.ResolveMember(
            metadataHost, "using:Demo", null, "ItemTemplate")!
            .ItemsDataTypeInheritance!;
        var metadataAssignment = metadataTypeSystem.ResolveMember(
            metadataHost, "using:Demo", null, "AssignedBinding")!
            .BindingAssignment!;
        var metadataParameter = Assert.Single(Assert.Single(
            metadataTypeSystem.ResolveType(
                "using:Demo", "ParameterScopeExtension")!.Constructors).Parameters)
            .DataTypeInheritance!;
        Assert.True(metadataDataType.IsValid, metadataDataType.Error);
        Assert.True(metadataItems.IsValid, metadataItems.Error);
        Assert.True(metadataAssignment.IsValid, metadataAssignment.Error);
        Assert.True(metadataParameter.IsValid, metadataParameter.Error);
        Assert.All(
            new ISymbol[]
            {
                metadataDataType.Property!,
                metadataItems.Annotation.DeclaredOn,
                metadataItems.AncestorItemsProperty!,
                metadataAssignment.Target!,
                metadataParameter.Target!
            },
            symbol => Assert.All(
                symbol.Locations,
                location => Assert.True(location.IsInMetadata)));

        var rules = XamlSchemaAttributeCatalog.Avalonia.Where(rule =>
            rule.Semantic == XamlSchemaSemantics.DataType ||
            rule.Semantic == XamlSchemaSemantics.InheritDataType ||
            rule.Semantic == XamlSchemaSemantics.InheritDataTypeFromItems ||
            rule.Semantic == XamlSchemaSemantics.AssignBinding).ToArray();
        Assert.Equal(4, rules.Length);
        Assert.Contains(
            rules,
            rule => rule.Semantic == XamlSchemaSemantics.InheritDataType &&
                    (rule.Targets & XamlSchemaAttributeTargets.Parameter) != 0 &&
                    rule.ConstructorArgumentIndex == 0);
    }

    [Fact]
    public void TemplateAndAttachedBrowseMetadataRemainCanonicalToolingSymbols()
    {
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new ToolingAnnotationMetadataProvider());
        var derived = typeSystem.ResolveType(
            "using:Demo", "DerivedToolingControl")!;
        Assert.Equal(2, derived.TemplateParts.Count);
        Assert.Equal(2, derived.EffectiveTemplateParts.Count);
        var avaloniaPart = Assert.Single(
            derived.TemplateParts,
            part => part.Name == "PART_Avalonia");
        Assert.True(avaloniaPart.IsValid, avaloniaPart.Error);
        Assert.True(avaloniaPart.IsRequired);
        Assert.Equal(
            "Microsoft.UI.Xaml.Controls.Button",
            avaloniaPart.PartType!.ToDisplayString());
        var inheritedPart = Assert.Single(
            derived.TemplateParts,
            part => part.Name == "PART_Content");
        Assert.True(inheritedPart.IsValid, inheritedPart.Error);
        Assert.Equal(1, inheritedPart.Annotation.InheritanceDepth);

        var state = Assert.Single(derived.TemplateVisualStates);
        Assert.True(state.IsValid, state.Error);
        Assert.Equal("CommonStates", state.GroupName);
        Assert.Equal("Normal", state.Name);
        Assert.Equal(
            state,
            derived.EffectiveTemplateVisualStates[
                new XamlTemplateVisualStateKey("CommonStates", "Normal")]);
        var style = Assert.Single(derived.StyleTypedProperties);
        Assert.True(style.IsValid, style.Error);
        Assert.Equal("ItemContainerStyle", style.Property!.Name);
        Assert.Equal(
            "Microsoft.UI.Xaml.Controls.Button",
            style.StyleTargetType!.ToDisplayString());
        Assert.Equal(
            style,
            derived.EffectiveStyleTypedProperties["ItemContainerStyle"]);

        var textBlock = typeSystem.ResolveType(
            "using:Microsoft.UI.Xaml.Controls", "TextBlock")!;
        var browseMember = typeSystem.ResolveMember(
            textBlock, "using:Demo", "BrowseOwner", "Hint")!;
        Assert.Equal(4, browseMember.AttachedPropertyBrowseRules.Count);
        var typeRules = browseMember.AttachedPropertyBrowseRules
            .Where(rule => rule.Kind ==
                XamlAttachedPropertyBrowseRuleKind.TargetType)
            .ToArray();
        Assert.Equal(2, typeRules.Length);
        Assert.All(typeRules, rule =>
        {
            Assert.True(rule.IsValid, rule.Error);
            Assert.Equal("GetHint", rule.Getter!.Name);
        });
        Assert.Contains(
            typeRules,
            rule => rule.ConstraintType!.ToDisplayString() ==
                "Microsoft.UI.Xaml.Controls.Button");
        Assert.Contains(
            typeRules,
            rule => rule.ConstraintType!.ToDisplayString() ==
                "Microsoft.UI.Xaml.FrameworkElement");
        var childRule = Assert.Single(
            browseMember.AttachedPropertyBrowseRules,
            rule => rule.Kind == XamlAttachedPropertyBrowseRuleKind.Children);
        Assert.True(childRule.IncludeDescendants);
        var attributeRule = Assert.Single(
            browseMember.AttachedPropertyBrowseRules,
            rule => rule.Kind == XamlAttachedPropertyBrowseRuleKind.AttributePresent);
        Assert.Equal(
            "System.ObsoleteAttribute",
            attributeRule.ConstraintType!.ToDisplayString());
        var toolingOnlyBound = new XamlSemanticBinder().Bind(
            Convert("""
<local:DerivedToolingControl xmlns:local="using:Demo" />
"""),
            typeSystem);
        Assert.False(
            toolingOnlyBound.HasErrors,
            string.Join(Environment.NewLine, toolingOnlyBound.Diagnostics));
        var toolingOnlyProgram =
            new XamlConstructionLowerer().Lower(toolingOnlyBound);
        Assert.Equal(
            "Demo.DerivedToolingControl",
            toolingOnlyProgram.Root!.Type.Symbol!.MetadataName);
        Assert.Empty(toolingOnlyProgram.Root.Operations);

        var invalid = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo">
  <StackPanel>
    <local:InvalidToolingControl />
    <TextBlock local:BrowseOwner.InvalidHint="x" />
  </StackPanel>
</Page>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2068");
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2069");
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2070");
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2071");

        using var image = new MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "ToolingAnnotationMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            profile,
            new ToolingAnnotationMetadataProvider());
        var metadataDerived = metadataTypeSystem.ResolveType(
            "using:Demo", "DerivedToolingControl")!;
        Assert.All(
            metadataDerived.TemplateParts
                .SelectMany(part => new ISymbol[]
                {
                    part.DeclaringType!,
                    part.PartType!
                })
                .Concat(metadataDerived.TemplateVisualStates.Select(state =>
                    (ISymbol)state.DeclaringType!))
                .Concat(metadataDerived.StyleTypedProperties.SelectMany(item =>
                    new ISymbol[] { item.Property!, item.StyleTargetType! })),
            symbol => Assert.All(
                symbol.Locations,
                location => Assert.True(location.IsInMetadata)));
        var metadataTextBlock = metadataTypeSystem.ResolveType(
            "using:Microsoft.UI.Xaml.Controls", "TextBlock")!;
        var metadataBrowse = metadataTypeSystem.ResolveMember(
            metadataTextBlock, "using:Demo", "BrowseOwner", "Hint")!;
        Assert.All(
            metadataBrowse.AttachedPropertyBrowseRules,
            rule => Assert.All(
                rule.Getter!.Locations.Concat(rule.ConstraintType?.Locations ??
                    ImmutableArray<Location>.Empty),
                location => Assert.True(location.IsInMetadata)));

        Assert.Equal(
            3,
            XamlSchemaAttributeCatalog.WinUi.Count(rule =>
                rule.Semantic == XamlSchemaSemantics.TemplatePart ||
                rule.Semantic == XamlSchemaSemantics.TemplateVisualState ||
                rule.Semantic == XamlSchemaSemantics.StyleTypedProperty));
        var childrenRule = Assert.Single(
            XamlSchemaAttributeCatalog.Wpf,
            rule => rule.AttributeMetadataName ==
                "System.Windows.AttachedPropertyBrowsableForChildrenAttribute");
        Assert.False(childrenRule.AllowMultiple);
        var attributePresentRule = Assert.Single(
            XamlSchemaAttributeCatalog.Wpf,
            rule => rule.AttributeMetadataName ==
                "System.Windows.AttachedPropertyBrowsableWhenAttributePresentAttribute");
        Assert.False(attributePresentRule.AllowMultiple);
        var avaloniaPartRule = Assert.Single(
            XamlSchemaAttributeCatalog.Avalonia,
            rule => rule.AttributeMetadataName ==
                "Avalonia.Controls.Metadata.TemplatePartAttribute");
        Assert.True(avaloniaPartRule.AllowMultiple);
    }

    [Fact]
    public void EqualPriorityIncompatibleAttributeProvidersAreDiagnosed()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo"><local:ConflictingContentControl /></Page>
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile, new DependencyMetadataProvider()),
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(bound.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML2038" && diagnostic.GetMessage().Contains("xaml.content-property", StringComparison.Ordinal));
    }

    [Fact]
    public void SerializationPlanBuildsOneStableEntryPerLargeBoundObject()
    {
        var children = string.Concat(
            Enumerable.Range(0, 512).Select(index =>
                "<TextBlock Text=\"" +
                index.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) +
                "\" />"));
        var bound = new XamlSemanticBinder().Bind(
            Convert(
                "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "<StackPanel>" + children + "</StackPanel></Page>"),
            new RoslynXamlTypeSystem(
                CreateCompilation(),
                new WinUiXamlProfile()));
        Assert.False(
            bound.HasErrors,
            string.Join(Environment.NewLine, bound.Diagnostics));
        Assert.True(bound.SerializationPlans.IsValid);
        Assert.Equal(514, bound.SerializationPlans.Objects.Count);
        Assert.Equal(
            bound.SerializationPlans.Objects.Count,
            bound.SerializationPlans.Objects.Keys.Distinct().Count());
        Assert.All(
            bound.SerializationPlans.Objects,
            pair => Assert.Equal(pair.Key, pair.Value.Source.StableId));
    }

    [Fact]
    public void DesignerSerializationAndLocalizationAnnotationsProjectFromSourceAndMetadata()
    {
        const string annotations = """
namespace System.Windows.Markup {
  [System.Flags]
  public enum DesignerSerializationOptions { SerializeAsAttribute = 1 }
  [System.AttributeUsage(
    System.AttributeTargets.Method | System.AttributeTargets.Property,
    AllowMultiple=false, Inherited=true)]
  public sealed class DesignerSerializationOptionsAttribute : System.Attribute {
    public DesignerSerializationOptionsAttribute(
      DesignerSerializationOptions options) { DesignerSerializationOptions = options; }
    public DesignerSerializationOptions DesignerSerializationOptions { get; }
  }
}
namespace System.Windows {
  public enum LocalizationCategory {
    None=0, Text=1, Title=2, Label=3, Button=4, CheckBox=5,
    ComboBox=6, ListBox=7, Menu=8, RadioButton=9, ToolTip=10,
    Hyperlink=11, TextFlow=12, XmlData=13, Font=14, Inherit=15,
    Ignore=16, NeverLocalize=17
  }
  public enum Readability { Unreadable=0, Readable=1, Inherit=2 }
  public enum Modifiability { Unmodifiable=0, Modifiable=1, Inherit=2 }
  [System.AttributeUsage(
    System.AttributeTargets.Class | System.AttributeTargets.Enum |
    System.AttributeTargets.Field | System.AttributeTargets.Property |
    System.AttributeTargets.Struct, AllowMultiple=false, Inherited=true)]
  public sealed class LocalizabilityAttribute : System.Attribute {
    public LocalizabilityAttribute(LocalizationCategory category) {
      Category = category;
      Readability = Readability.Readable;
      Modifiability = Modifiability.Modifiable;
    }
    public LocalizationCategory Category { get; }
    public Readability Readability { get; set; }
    public Modifiability Modifiability { get; set; }
  }
}
namespace DesignerDemo {
  [System.ComponentModel.Browsable(false)]
  [System.ComponentModel.EditorBrowsable(
    System.ComponentModel.EditorBrowsableState.Advanced)]
  [System.ComponentModel.DesignTimeVisible]
  [System.Windows.Localizability(
    System.Windows.LocalizationCategory.Button,
    Readability=System.Windows.Readability.Readable,
    Modifiability=System.Windows.Modifiability.Unmodifiable)]
  public class Control {
    [System.ComponentModel.DefaultValue(typeof(int), "7")]
    public int Count { get; set; }

    [System.ComponentModel.DefaultValue(42)]
    public int Size { get; set; }

    [System.ComponentModel.DefaultValue(null)]
    public string? Tag { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(
      System.ComponentModel.DesignerSerializationVisibility.Content)]
    [System.Windows.Markup.DesignerSerializationOptions(
      System.Windows.Markup.DesignerSerializationOptions.SerializeAsAttribute)]
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.EditorBrowsable(
      System.ComponentModel.EditorBrowsableState.Never)]
    [System.Windows.Localizability(
      System.Windows.LocalizationCategory.Text)]
    public virtual string Content { get; set; } = string.Empty;
    public bool ShouldSerializeContent() => Content.Length != 0;

    [System.ComponentModel.DesignerSerializationVisibility(
      System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Hidden { get; set; } = string.Empty;

    [System.Windows.Markup.DesignerSerializationOptions(
      System.Windows.Markup.DesignerSerializationOptions.SerializeAsAttribute)]
    public string Title { get; set; } = string.Empty;

    [System.ComponentModel.DesignerSerializationVisibility(
      System.ComponentModel.DesignerSerializationVisibility.Visible)]
    public string Visible { get; set; } = string.Empty;

    public string Complex { get; set; } = string.Empty;
    public bool ShouldSerializeComplex() => Complex.Length != 0;
    public void ResetComplex() => Complex = string.Empty;

    public string PrivateComplex { get; set; } = string.Empty;
    private bool ShouldSerializePrivateComplex() => PrivateComplex.Length != 0;
    private void ResetPrivateComplex() => PrivateComplex = string.Empty;

    public string ShouldOnly { get; set; } = string.Empty;
    private bool ShouldSerializeShouldOnly() => ShouldOnly.Length != 0;

    public string ResetOnly { get; set; } = string.Empty;
    private void ResetResetOnly() => ResetOnly = string.Empty;
  }

  public sealed class DerivedControl : Control {
    public override string Content { get; set; } = string.Empty;
    public new bool ShouldSerializeContent() => false;
  }

  public static class AttachedOwner {
    [System.Windows.Markup.DesignerSerializationOptions(
      System.Windows.Markup.DesignerSerializationOptions.SerializeAsAttribute)]
    public static string GetHint(Control target) => string.Empty;
    public static void SetHint(Control target, string value) { }
  }
}
""";
        var compilation = CreateCompilation().AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(annotations));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new DesignerMetadataProvider());

        var derived = typeSystem.ResolveType(
            "using:DesignerDemo",
            "DerivedControl")!;
        Assert.False(derived.IsBrowsableForDesigner);
        Assert.Equal(
            XamlEditorBrowsableState.Advanced,
            derived.EditorBrowsable?.State);
        Assert.False(derived.DesignTimeVisible?.Value);
        Assert.Equal(
            XamlLocalizationCategory.Button,
            derived.Localizability?.Category);
        Assert.Equal(
            XamlLocalizationReadability.Readable,
            derived.Localizability?.Readability);
        Assert.Equal(
            XamlLocalizationModifiability.Unmodifiable,
            derived.Localizability?.Modifiability);
        Assert.Equal(1, derived.Browsable?.Annotation.InheritanceDepth);

        var content = typeSystem.ResolveMember(
            derived,
            "using:DesignerDemo",
            null,
            "Content")!;
        Assert.Equal(
            "DesignerDemo.Control.Content",
            content.DesignerSerializationVisibility?.Member?.ToDisplayString());
        Assert.False(content.IsBrowsableForDesigner);
        Assert.Equal(
            XamlDesignerSerializationVisibility.Content,
            content.DesignerSerializationVisibility?.Visibility);
        Assert.True(content.DesignerSerializationOptions?.SerializeAsAttribute);
        Assert.Equal(
            XamlLocalizationCategory.Text,
            content.Localizability?.Category);
        Assert.Equal(
            XamlLocalizationReadability.Readable,
            content.Localizability?.Readability);
        Assert.Equal(
            XamlLocalizationModifiability.Modifiable,
            content.Localizability?.Modifiability);
        var contentPolicy = content.SerializationPolicy;
        Assert.True(contentPolicy.IsValid);
        Assert.Equal(
            XamlMemberSerializationForm.Content,
            contentPolicy.Form);
        Assert.Equal(
            "DesignerDemo.DerivedControl",
            contentPolicy.ShouldSerializeMethod?.ContainingType.ToDisplayString());

        var count = typeSystem.ResolveMember(
            derived,
            "using:DesignerDemo",
            null,
            "Count")!;
        Assert.True(count.DefaultValue?.IsValid);
        Assert.True(count.DefaultValue?.UsesTextConversion);
        Assert.Equal(
            "int",
            count.DefaultValue?.ConversionType?.ToDisplayString());
        Assert.Equal("7", count.DefaultValue?.Text);
        Assert.Null(count.DefaultValue?.ValueConstant);
        var size = typeSystem.ResolveMember(
            derived,
            "using:DesignerDemo",
            null,
            "Size")!;
        Assert.Equal(42, size.DefaultValue?.ValueConstant?.Value);
        var tag = typeSystem.ResolveMember(
            derived,
            "using:DesignerDemo",
            null,
            "Tag")!;
        Assert.True(tag.DefaultValue?.ValueConstant?.IsNull);

        var hidden = typeSystem.ResolveMember(
            derived,
            "using:DesignerDemo",
            null,
            "Hidden")!;
        Assert.Equal(
            XamlMemberSerializationForm.Excluded,
            hidden.SerializationPolicy.Form);
        var title = typeSystem.ResolveMember(
            derived,
            "using:DesignerDemo",
            null,
            "Title")!;
        Assert.Equal(
            XamlMemberSerializationForm.Attribute,
            title.SerializationPolicy.Form);
        var visible = typeSystem.ResolveMember(
            derived,
            "using:DesignerDemo",
            null,
            "Visible")!;
        Assert.Equal(
            XamlMemberSerializationForm.Element,
            visible.SerializationPolicy.Form);
        var complex = typeSystem.ResolveMember(
            derived,
            "using:DesignerDemo",
            null,
            "Complex")!;
        Assert.True(complex.SerializationMethods?.IsValid);
        Assert.Equal(
            "DesignerDemo.Control.ShouldSerializeComplex()",
            complex.SerializationPolicy.ShouldSerializeMethod?.ToDisplayString());
        Assert.Equal(
            "DesignerDemo.Control.ResetComplex()",
            complex.SerializationPolicy.ResetMethod?.ToDisplayString());
        Assert.True(complex.SerializationPolicy.IsConditionallyIncluded);
        Assert.True(complex.SerializationPolicy.CanReset);
        var privateComplex = typeSystem.ResolveMember(
            derived,
            "using:DesignerDemo",
            null,
            "PrivateComplex")!;
        Assert.Equal(
            Accessibility.Private,
            privateComplex.SerializationPolicy.ShouldSerializeMethod?
                .DeclaredAccessibility);
        Assert.Equal(
            Accessibility.Private,
            privateComplex.SerializationPolicy.ResetMethod?
                .DeclaredAccessibility);
        var shouldOnly = typeSystem.ResolveMember(
            derived,
            "using:DesignerDemo",
            null,
            "ShouldOnly")!;
        Assert.True(shouldOnly.SerializationPolicy.IsConditionallyIncluded);
        Assert.False(shouldOnly.SerializationPolicy.CanReset);
        var resetOnly = typeSystem.ResolveMember(
            derived,
            "using:DesignerDemo",
            null,
            "ResetOnly")!;
        Assert.False(resetOnly.SerializationPolicy.IsConditionallyIncluded);
        Assert.True(resetOnly.SerializationPolicy.CanReset);
        var attached = typeSystem.ResolveMember(
            derived,
            "using:DesignerDemo",
            "AttachedOwner",
            "Hint")!;
        Assert.True(attached.DesignerSerializationOptions?.SerializeAsAttribute);
        Assert.Equal(
            "DesignerDemo.AttachedOwner.GetHint(DesignerDemo.Control)",
            attached.DesignerSerializationOptions?.Member?.ToDisplayString());

        var loadBound = new XamlSemanticBinder().Bind(
            Convert("""
<local:DerivedControl
    xmlns:local="using:DesignerDemo"
    Content="content"
    Complex="complex"
    Hidden="hidden"
    PrivateComplex="private"
    Title="title"
    Visible="visible" />
"""),
            typeSystem);
        Assert.False(
            loadBound.HasErrors,
            string.Join(Environment.NewLine, loadBound.Diagnostics));
        Assert.Equal(
            new[]
            {
                "Content",
                "Complex",
                "Hidden",
                "PrivateComplex",
                "Title",
                "Visible"
            },
            new XamlConstructionLowerer().Lower(loadBound).Root!.Operations
                .Where(operation => operation.Member.Symbol != null)
                .Select(operation => operation.Member.Symbol!.Name));
        var savePlan = loadBound.SerializationPlans.Root!;
        Assert.True(savePlan.IsValid);
        Assert.Equal(
            XamlSerializationDisposition.Content,
            Assert.Single(
                savePlan.Members,
                item => item.Member?.Name == "Content").Disposition);
        Assert.Equal(
            XamlSerializationDisposition.Omit,
            Assert.Single(
                savePlan.Members,
                item => item.Member?.Name == "Hidden").Disposition);
        Assert.Equal(
            XamlSerializationDisposition.Attribute,
            Assert.Single(
                savePlan.Members,
                item => item.Member?.Name == "Title").Disposition);
        Assert.Equal(
            "DesignerDemo.DerivedControl",
            Assert.Single(
                    savePlan.Members,
                    item => item.Member?.Name == "Content")
                .ShouldSerializeMethod?.ContainingType.ToDisplayString());
        var complexSavePlan = Assert.Single(
            savePlan.Members,
            item => item.Member?.Name == "Complex");
        Assert.Contains(
            "ShouldSerializeComplex",
            RoslynXamlSerializationPlanSyntaxFactory
                .CreateShouldSerializeInvocation(
                    complexSavePlan,
                    SyntaxFactory.IdentifierName("value"))
                .NormalizeWhitespace()
                .ToFullString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "ResetComplex",
            RoslynXamlSerializationPlanSyntaxFactory
                .CreateResetInvocation(
                    complexSavePlan,
                    SyntaxFactory.IdentifierName("value"))
                .NormalizeWhitespace()
                .ToFullString(),
            StringComparison.Ordinal);
        var privateSavePlan = Assert.Single(
            savePlan.Members,
            item => item.Member?.Name == "PrivateComplex");
        Assert.Throws<ArgumentException>(() =>
            RoslynXamlSerializationPlanSyntaxFactory
                .CreateShouldSerializeInvocation(
                    privateSavePlan,
                    SyntaxFactory.IdentifierName("value")));

        using var image = new MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(
            emit.Success,
            string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "DesignerMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            profile,
            new DesignerMetadataProvider());
        var metadataDerived = metadataTypeSystem.ResolveType(
            "using:DesignerDemo",
            "DerivedControl")!;
        var metadataContent = metadataTypeSystem.ResolveMember(
            metadataDerived,
            "using:DesignerDemo",
            null,
            "Content")!;
        Assert.Equal(
            XamlMemberSerializationForm.Content,
            metadataContent.SerializationPolicy.Form);
        var metadataBound = new XamlSemanticBinder().Bind(
            Convert("""
<local:DerivedControl
    xmlns:local="using:DesignerDemo"
    Content="metadata"
    Complex="metadata-complex" />
"""),
            metadataTypeSystem);
        var metadataSavePlan = metadataBound.SerializationPlans.Root!;
        Assert.Equal(
            XamlSerializationDisposition.Content,
            Assert.Single(
                metadataSavePlan.Members,
                item => item.Member?.Name == "Content").Disposition);
        Assert.All(
            Assert.Single(
                    metadataSavePlan.Members,
                    item => item.Member?.Name == "Complex")
                .ShouldSerializeMethod!.Locations,
            location => Assert.True(location.IsInMetadata));
        Assert.All(
            new[]
            {
                metadataDerived.Localizability!.Target!,
                metadataContent.DesignerSerializationVisibility!.Member!,
                metadataContent.Localizability!.Target!,
                metadataTypeSystem.ResolveMember(
                    metadataDerived,
                    "using:DesignerDemo",
                    null,
                    "Complex")!.SerializationMethods!.ShouldSerializeMethod!
            },
            symbol => Assert.All(
                symbol.Locations,
                location => Assert.True(location.IsInMetadata)));
    }

    [Fact]
    public void MalformedDesignerSerializationMetadataProducesTypedDiagnostics()
    {
        const string annotations = """
namespace Meta {
  [System.AttributeUsage(System.AttributeTargets.Property)]
  public sealed class InvalidDefaultAttribute : System.Attribute { }
}
namespace System.Windows.Markup {
  [System.Flags]
  public enum DesignerSerializationOptions { SerializeAsAttribute=1 }
  [System.AttributeUsage(System.AttributeTargets.Property)]
  public sealed class DesignerSerializationOptionsAttribute : System.Attribute {
    public DesignerSerializationOptionsAttribute(
      DesignerSerializationOptions options) { }
  }
}
namespace System.Windows {
  public enum LocalizationCategory { None=0, NeverLocalize=17 }
  public enum Readability { Unreadable=0, Readable=1, Inherit=2 }
  public enum Modifiability { Unmodifiable=0, Modifiable=1, Inherit=2 }
  [System.AttributeUsage(System.AttributeTargets.Property)]
  public sealed class LocalizabilityAttribute : System.Attribute {
    public LocalizabilityAttribute(LocalizationCategory category) { }
    public Readability Readability { get; set; }
    public Modifiability Modifiability { get; set; }
  }
}
namespace DesignerDemo {
  public sealed class BrokenControl {
    [Meta.InvalidDefault]
    public string BrokenDefault { get; set; } = string.Empty;
    [System.Windows.Markup.DesignerSerializationOptions(
      (System.Windows.Markup.DesignerSerializationOptions)4)]
    public string BrokenOptions { get; set; } = string.Empty;
    [System.Windows.Localizability(
      (System.Windows.LocalizationCategory)99)]
    public string BrokenLocalization { get; set; } = string.Empty;
    [System.ComponentModel.EditorBrowsable(
      (System.ComponentModel.EditorBrowsableState)9)]
    public string BrokenEditor { get; set; } = string.Empty;
    public string BrokenShape { get; set; } = string.Empty;
    private bool ShouldSerializeBrokenShape(int value) => value != 0;
    [System.ComponentModel.DefaultValue("default")]
    public string MixedDefault { get; set; } = "default";
    private bool ShouldSerializeMixedDefault() => true;
  }
}
""";
        var compilation = CreateCompilation().AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(annotations));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var bound = new XamlSemanticBinder().Bind(
            Convert("""
<local:BrokenControl
    xmlns:local="using:DesignerDemo"
    BrokenDefault="a"
    BrokenOptions="b"
    BrokenLocalization="c"
    BrokenEditor="d"
    BrokenShape="e"
    MixedDefault="f" />
"""),
            new RoslynXamlTypeSystem(
                compilation,
                new WinUiXamlProfile(),
                new DesignerMetadataProvider()),
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(
            bound.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2078");
        Assert.Contains(
            bound.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2079");
        Assert.Contains(
            bound.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2080");
        Assert.Contains(
            bound.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2081");
        Assert.Contains(
            bound.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2082");
    }

    [Fact]
    public void XReferenceBindsNamescopeAndDefersForwardStructuredAssignment()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <StackPanel>
    <local:ReferenceHolder Target="{x:Reference Later}" />
    <TextBlock x:Name="Later" Text="target" />
  </StackPanel>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var reference = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundNameReferenceValue>());
        Assert.Equal("Later", reference.Name);

        var program = new XamlConstructionLowerer().Lower(bound);
        Assert.Single(DescendantIr(program.Root).SelectMany(value => value.Operations)
            .SelectMany(operation => operation.Values).OfType<XamlIrNameReference>());
        var emitted = new CSharpXamlEmitter().EmitProgram(program, profile, new ProGPU.Xaml.Schema.XamlCompilerOptions());
        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var missing = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"using:Demo\"><local:ReferenceHolder Target=\"{x:Reference Missing}\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2039");
    }

    [Fact]
    public void TypeConverterMetadataFlowsThroughBoundTreeIrAndStructuredEmission()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:ConversionControl Length="42" Payload="7" />
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var convertedText = DescendantValues(bound.Root).OfType<XamlBoundText>()
            .Where(value => value.TextSyntax.Kind == ProGPU.Xaml.Schema.XamlTextSyntaxKind.TypeConverter).ToArray();
        Assert.Equal(2, convertedText.Length);
        Assert.All(convertedText, value =>
        {
            Assert.Equal("Demo.LengthConverter", value.TextSyntax.ConverterType!.ToDisplayString());
            var shape = Assert.IsType<ProGPU.Xaml.Schema.XamlCreateFromStringShapeInfo>(
                value.TextSyntax.CreateFromStringShape);
            Assert.Equal("Demo.LengthConverter.LengthConverter()", shape.Constructor!.ToDisplayString());
            Assert.Equal("ConvertFromInvariantString", shape.Method!.Name);
            Assert.Equal("ProGPU.WinUI", shape.ProviderId);
        });

        var program = new XamlConstructionLowerer().Lower(bound);
        Assert.Equal(2, DescendantIr(program.Root).SelectMany(value => value.Operations)
            .SelectMany(operation => operation.Values).OfType<XamlIrText>()
            .Count(value => value.TextSyntax.Kind == ProGPU.Xaml.Schema.XamlTextSyntaxKind.TypeConverter));
        var emitted = new CSharpXamlEmitter().EmitProgram(program, profile, new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var source = Assert.Single(emitted.Sources).Source;
        Assert.Equal(2, source.Split("new global::Demo.LengthConverter()", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(Assert.Single(emitted.Sources).GeneratedSyntaxTree!).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var invalid = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><local:InvalidConversionControl Value=\"bad\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(invalid.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML2041" && diagnostic.Properties["MSXamlSection"] == "6.3.2.4");
    }

    [Fact]
    public void CreateFromStringAnnotationsBindExactStaticFactoriesAndEmitStructuredCalls()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:FactoryConversionControl Direct="12"
                                  Qualified="34"
                                  Nested="78"
                                  MemberOverride="56" />
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile);
        var directType = typeSystem.ResolveType(
            "using:Demo",
            "FactoryLength")!;
        var directShape = Assert.IsType<XamlCreateFromStringShapeInfo>(
            directType.TextSyntax.CreateFromStringShape);
        Assert.True(directShape.IsValid, directShape.Error);
        Assert.Equal(
            XamlTextSyntaxKind.CreateFromString,
            directType.TextSyntax.Kind);
        Assert.Equal(
            XamlCreateFromStringInvocationKind.StaticMethod,
            directShape.InvocationKind);
        Assert.True(SymbolEqualityComparer.Default.Equals(
            directType.Symbol,
            directShape.TargetType));
        Assert.True(SymbolEqualityComparer.Default.Equals(
            directType.Symbol,
            directShape.FactoryType));
        Assert.Equal("Parse", directShape.RequestedMethodName);
        Assert.Equal("Parse", directShape.Method!.Name);
        Assert.Equal(
            SpecialType.System_String,
            directShape.Method.Parameters[0].Type.SpecialType);
        Assert.Equal(
            "Windows.Foundation.Metadata.CreateFromStringAttribute",
            directShape.Annotation.Attribute.AttributeClass!
                .ToDisplayString());
        Assert.Equal("ProGPU.WinUI", directShape.ProviderId);

        var qualifiedType = typeSystem.ResolveType(
            "using:Demo",
            "QualifiedFactoryLength")!;
        var qualifiedShape =
            Assert.IsType<XamlCreateFromStringShapeInfo>(
                qualifiedType.TextSyntax.CreateFromStringShape);
        Assert.True(qualifiedShape.IsValid, qualifiedShape.Error);
        Assert.Equal(
            "Demo.FactoryLengthFactory",
            qualifiedShape.FactoryType!.ToDisplayString());
        Assert.Equal(
            "Demo.FactoryLengthFactory.ParseQualified",
            qualifiedShape.RequestedMethodName);
        Assert.Equal(
            SpecialType.System_Object,
            qualifiedShape.Method!.ReturnType.SpecialType);
        var nestedShape = typeSystem.ResolveType(
                "using:Demo",
                "NestedFactoryLength")!
            .TextSyntax.CreateFromStringShape!;
        Assert.True(nestedShape.IsValid, nestedShape.Error);
        Assert.Equal(
            "Demo.NestedFactoryLength.Parser",
            nestedShape.FactoryType!.ToDisplayString());

        var control = typeSystem.ResolveType(
            "using:Demo",
            "FactoryConversionControl")!;
        var overridden = typeSystem.ResolveMember(
            control,
            "using:Demo",
            ownerTypeName: null,
            memberName: "MemberOverride")!;
        Assert.Equal(
            XamlTextSyntaxKind.TypeConverter,
            overridden.TextSyntax.Kind);
        Assert.Equal(
            XamlTextSyntaxKind.CreateFromString,
            overridden.ValueType.TextSyntax.Kind);

        var bound = new XamlSemanticBinder().Bind(
            infoset,
            typeSystem);
        Assert.False(
            bound.HasErrors,
            string.Join(Environment.NewLine, bound.Diagnostics));
        var boundControl = Assert.Single(
            DescendantValues(bound.Root)
                .OfType<XamlBoundObject>(),
            value => value.Type.Symbol?.MetadataName ==
                "Demo.FactoryConversionControl");
        Assert.Equal(
            XamlTextSyntaxKind.CreateFromString,
            Assert.IsType<XamlBoundText>(
                Assert.Single(
                    boundControl.Members,
                    member => member.Member.Symbol?.Name == "Direct")
                    .Values[0]).TextSyntax.Kind);
        Assert.Equal(
            XamlTextSyntaxKind.TypeConverter,
            Assert.IsType<XamlBoundText>(
                Assert.Single(
                    boundControl.Members,
                    member => member.Member.Symbol?.Name ==
                        "MemberOverride").Values[0]).TextSyntax.Kind);

        var program = new XamlConstructionLowerer().Lower(bound);
        Assert.Equal(
            3,
            DescendantIr(program.Root)
                .SelectMany(value => value.Operations)
                .SelectMany(operation => operation.Values)
                .OfType<XamlIrText>()
                .Count(value => value.TextSyntax.Kind ==
                    XamlTextSyntaxKind.CreateFromString));
        var emitted = new CSharpXamlEmitter().EmitProgram(
            program,
            profile,
            new XamlCompilerOptions());
        Assert.DoesNotContain(
            emitted.Diagnostics,
            diagnostic => diagnostic.Severity ==
                DiagnosticSeverity.Error);
        var generatedTree =
            Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var invocations = generatedTree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .ToArray();
        Assert.Contains(
            invocations,
            invocation => invocation.Expression.ToString().EndsWith(
                "FactoryLength.Parse",
                StringComparison.Ordinal));
        Assert.Contains(
            invocations,
            invocation => invocation.Expression.ToString().EndsWith(
                "FactoryLengthFactory.ParseQualified",
                StringComparison.Ordinal));
        Assert.Contains(
            invocations,
            invocation => invocation.Expression.ToString().EndsWith(
                "NestedFactoryLength.Parser.Parse",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(generatedTree).GetDiagnostics(),
            diagnostic => diagnostic.Severity ==
                DiagnosticSeverity.Error);

        using var image = new MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(
            emit.Success,
            string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "CreateFromStringMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            profile);
        var metadataShape =
            metadataTypeSystem.ResolveType(
                "using:Demo",
                "QualifiedFactoryLength")!
                .TextSyntax.CreateFromStringShape!;
        Assert.True(metadataShape.IsValid, metadataShape.Error);
        Assert.All(
            metadataShape.Method!.Locations,
            location => Assert.True(location.IsInMetadata));
        Assert.All(
            metadataShape.FactoryType!.Locations,
            location => Assert.True(location.IsInMetadata));

        foreach (var invalidTypeName in new[]
                 {
                     "InstanceFactoryLength",
                     "NonPublicFactoryLength",
                     "GenericFactoryLength",
                     "VoidFactoryLength",
                     "WrongParameterFactoryLength",
                     "InconvertibleFactoryLength",
                     "UnresolvedFactoryLength",
                     "EmptyFactoryLength"
                 })
        {
            var shape = typeSystem.ResolveType(
                    "using:Demo",
                    invalidTypeName)!
                .TextSyntax.CreateFromStringShape!;
            Assert.False(shape.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(shape.Error));
        }
        Assert.Single(
            typeSystem.ResolveType(
                    "using:Demo",
                    "InstanceFactoryLength")!
                .TextSyntax.CreateFromStringShape!.Candidates);
        Assert.Empty(
            typeSystem.ResolveType(
                    "using:Demo",
                    "UnresolvedFactoryLength")!
                .TextSyntax.CreateFromStringShape!.Candidates);

        var invalid = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo">
  <local:InvalidFactoryConversionControl Value="bad" />
</Page>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(
            invalid.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2041");
        var invalidShape = typeSystem.ResolveType(
            "using:Demo",
            "InvalidFactoryLength")!
            .TextSyntax.CreateFromStringShape!;
        Assert.False(invalidShape.IsValid);
        Assert.Empty(invalidShape.Candidates);
    }

    [Fact]
    public void ValueSerializerMetadataIsValidatedAsSavePathEvidence()
    {
        var compilation = CreateCompilation();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            new WinUiXamlProfile(),
            new ValueSerializerMetadataProvider());

        var serialized = typeSystem.ResolveType("using:Demo", "SerializedLength")!;
        Assert.Equal(ProGPU.Xaml.Schema.XamlTextSyntaxKind.None, serialized.TextSyntax.Kind);
        var typeShape = Assert.IsType<ProGPU.Xaml.Schema.XamlValueSerializerShapeInfo>(
            serialized.ValueSerializer);
        Assert.True(typeShape.IsValid, typeShape.Error);
        Assert.False(typeShape.IsSuppressed);
        Assert.Equal("tests.value-serializer", typeShape.ProviderId);
        Assert.Equal("Demo.SerializedLengthSerializer", typeShape.SerializerType!.ToDisplayString());
        Assert.Equal("System.Windows.Markup.IValueSerializerContext",
            Assert.IsAssignableFrom<INamedTypeSymbol>(typeShape.ContextType)
                .OriginalDefinition.ToDisplayString());
        Assert.Equal("CanConvertToString", typeShape.CanConvertToStringMethod!.Name);
        Assert.Equal("ConvertToString", typeShape.ConvertToStringMethod!.Name);
        Assert.Equal(2, typeShape.Candidates.Count);

        var control = typeSystem.ResolveType("using:Demo", "SerializationControl")!;
        var member = typeSystem.ResolveMember(
            control,
            "using:Demo",
            ownerTypeName: null,
            memberName: "MemberSerializerValue")!;
        var memberShape = Assert.IsType<ProGPU.Xaml.Schema.XamlValueSerializerShapeInfo>(
            member.ValueSerializer);
        Assert.True(memberShape.IsValid, memberShape.Error);
        Assert.Equal("Demo.HexLengthSerializer", memberShape.SerializerType!.ToDisplayString());
        Assert.Equal(ProGPU.Xaml.Schema.XamlTextSyntaxKind.Intrinsic, member.TextSyntax.Kind);
        Assert.Null(member.TextSyntax.CreateFromStringShape);
        var typeBackedMember = typeSystem.ResolveMember(
            control,
            "using:Demo",
            ownerTypeName: null,
            memberName: "TypeSerializerValue")!;
        Assert.Null(typeBackedMember.ValueSerializer);
        var effectiveTypeShape = Assert.IsType<ProGPU.Xaml.Schema.XamlValueSerializerShapeInfo>(
            typeBackedMember.ValueType.ValueSerializer);
        Assert.True(SymbolEqualityComparer.Default.Equals(
            typeShape.SerializerType,
            effectiveTypeShape.SerializerType));

        var suppressed = typeSystem.ResolveType("using:Demo", "SuppressedSerializedValue")!;
        Assert.True(suppressed.ValueSerializer!.IsValid);
        Assert.True(suppressed.ValueSerializer.IsSuppressed);
        Assert.Null(suppressed.ValueSerializer.SerializerType);

        var validBound = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><local:SerializationControl MemberSerializerValue=\"value\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(validBound.HasErrors, string.Join(Environment.NewLine, validBound.Diagnostics));
        var validProgram = new XamlConstructionLowerer().Lower(validBound);
        var serializedOperation = Assert.Single(
            DescendantIr(validProgram.Root)
                .SelectMany(value => value.Operations),
            operation => operation.Member.Symbol?.Name == "MemberSerializerValue");
        Assert.Equal(
            "Demo.HexLengthSerializer",
            serializedOperation.Member.Symbol!.ValueSerializer!.SerializerType!.ToDisplayString());

        var invalid = typeSystem.ResolveType("using:Demo", "InvalidSerializedValue")!;
        Assert.False(invalid.ValueSerializer!.IsValid);
        var invalidBound = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><local:InvalidSerializedValue /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        var diagnostic = Assert.Single(
            invalidBound.Diagnostics,
            candidate => candidate.Id == "PGXAML2050");
        Assert.Equal("5.4.1.1", diagnostic.Properties["MSXamlSection"]);
        Assert.Contains("registered serializer base", diagnostic.GetMessage(), StringComparison.Ordinal);
        var invalidMemberBound = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><local:SerializationControl InvalidMemberSerializerValue=\"bad\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        var memberDiagnostic = Assert.Single(
            invalidMemberBound.Diagnostics,
            candidate => candidate.Id == "PGXAML2050");
        Assert.Contains("InvalidMemberSerializerValue", memberDiagnostic.GetMessage(), StringComparison.Ordinal);

        var canConvert = RoslynXamlValueSerializerSyntaxFactory.CreateCanConvertToStringExpression(
            typeShape,
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression),
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
        var convert = RoslynXamlValueSerializerSyntaxFactory.CreateConvertToStringExpression(
            typeShape,
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression),
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
        Assert.IsType<InvocationExpressionSyntax>(canConvert);
        Assert.IsType<InvocationExpressionSyntax>(convert);
        var convertText = convert.NormalizeWhitespace().ToFullString();
        Assert.Contains("new global::Demo.SerializedLengthSerializer()",
            convertText, StringComparison.Ordinal);
        var casts = convert.DescendantNodes().OfType<CastExpressionSyntax>().ToArray();
        Assert.Equal(2, casts.Length);
        Assert.Equal("object", casts[0].Type.ToString());
        Assert.Equal(
            "global::System.Windows.Markup.IValueSerializerContext?",
            casts[1].Type.ToString());

        var probe = SyntaxFactory.CompilationUnit()
            .AddMembers(SyntaxFactory.NamespaceDeclaration(
                    SyntaxFactory.IdentifierName("Demo"))
                .AddMembers(SyntaxFactory.ClassDeclaration("__SerializerProbe")
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.InternalKeyword))
                    .AddMembers(SyntaxFactory.MethodDeclaration(
                            SyntaxFactory.PredefinedType(
                                SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                            "Serialize")
                        .AddModifiers(
                            SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                            SyntaxFactory.Token(SyntaxKind.StaticKeyword))
                        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(convert))
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))));
        var probeTree = CSharpSyntaxTree.Create(probe.NormalizeWhitespace());
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(probeTree).GetDiagnostics(),
            candidate => candidate.Severity == DiagnosticSeverity.Error);

        using var image = new System.IO.MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "MetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            new WinUiXamlProfile(),
            new ValueSerializerMetadataProvider());
        var metadataShape = metadataTypeSystem.ResolveType(
            "using:Demo",
            "SerializedLength")!.ValueSerializer!;
        Assert.True(metadataShape.IsValid, metadataShape.Error);
        Assert.All(
            metadataShape.SerializerType!.Locations,
            location => Assert.True(location.IsInMetadata));
        Assert.Equal("Demo.SerializedLengthSerializer",
            metadataShape.SerializerType.ToDisplayString());
    }

    [Fact]
    public void WhitespaceAndInitializationAnnotationsAreCanonicalAndDriveElementContent()
    {
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new WhitespaceMetadataProvider());

        var collection = typeSystem.ResolveType("using:Demo", "SignificantInlineCollection")!;
        var significant = Assert.IsType<ProGPU.Xaml.Schema.XamlSchemaBooleanInfo>(
            collection.WhitespaceSignificantCollection);
        Assert.True(significant.IsValid);
        Assert.True(significant.Value);
        Assert.Equal("tests.whitespace", significant.Annotation.ProviderId);
        Assert.Equal(
            "System.Windows.Markup.WhitespaceSignificantCollectionAttribute",
            significant.Annotation.Attribute.AttributeClass!.ToDisplayString());
        Assert.False(significant.Annotation.IsInherited);

        var derivedCollection = typeSystem.ResolveType(
            "using:Demo",
            "DerivedSignificantInlineCollection")!;
        Assert.True(derivedCollection.IsWhitespaceSignificantCollection);
        Assert.True(derivedCollection.WhitespaceSignificantCollection!.Annotation.IsInherited);
        Assert.Equal(1,
            derivedCollection.WhitespaceSignificantCollection.Annotation.InheritanceDepth);

        var derivedBreak = typeSystem.ResolveType("using:Demo", "DerivedInlineBreak")!;
        Assert.True(derivedBreak.ShouldTrimSurroundingWhitespace);
        Assert.True(derivedBreak.TrimSurroundingWhitespace!.Annotation.IsInherited);

        var early = typeSystem.ResolveType("using:Demo", "EarlyAttachNode")!;
        Assert.True(early.IsUsableDuringInitialization);
        Assert.True(early.UsableDuringInitialization!.Value);
        var deferred = typeSystem.ResolveType("using:Demo", "DeferredAttachNode")!;
        Assert.False(deferred.IsUsableDuringInitialization);
        Assert.False(deferred.UsableDuringInitialization!.Value);
        Assert.False(deferred.UsableDuringInitialization.Annotation.IsInherited);
        Assert.Equal("Demo.DeferredAttachNode",
            deferred.UsableDuringInitialization.Annotation.DeclaredOn.ToDisplayString());

        var normalized = new XamlSemanticBinder().Bind(
            Convert("""
<local:TextFlow xmlns:local="using:Demo">alpha
  <local:InlineMarker />

  <local:InlineMarker /> beta</local:TextFlow>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(normalized.HasErrors, string.Join(Environment.NewLine, normalized.Diagnostics));
        var normalizedValues = Assert.Single(normalized.Root!.Members).Values;
        Assert.Collection(
            normalizedValues,
            value =>
            {
                var text = Assert.IsType<XamlBoundText>(value);
                Assert.Equal("alpha ", text.Text);
                Assert.Contains('\n', text.OriginalText);
                Assert.True(text.IsNormalized);
            },
            value => Assert.Equal("Demo.InlineMarker",
                Assert.IsType<XamlBoundObject>(value).Type.Symbol!.MetadataName),
            value => Assert.Equal(" ", Assert.IsType<XamlBoundText>(value).Text),
            value => Assert.Equal("Demo.InlineMarker",
                Assert.IsType<XamlBoundObject>(value).Type.Symbol!.MetadataName),
            value => Assert.Equal(" beta", Assert.IsType<XamlBoundText>(value).Text));

        var trimmed = new XamlSemanticBinder().Bind(
            Convert("""
<local:TextFlow xmlns:local="using:Demo">alpha
  <local:DerivedInlineBreak />
  beta</local:TextFlow>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(trimmed.HasErrors, string.Join(Environment.NewLine, trimmed.Diagnostics));
        Assert.Collection(
            Assert.Single(trimmed.Root!.Members).Values,
            value => Assert.Equal("alpha", Assert.IsType<XamlBoundText>(value).Text),
            value => Assert.IsType<XamlBoundObject>(value),
            value => Assert.Equal("beta", Assert.IsType<XamlBoundText>(value).Text));

        var preserved = new XamlSemanticBinder().Bind(
            Convert("""
<local:TextFlow xmlns:local="using:Demo" xml:space="preserve">  alpha
 <local:InlineMarker /> 	 beta  </local:TextFlow>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(preserved.HasErrors, string.Join(Environment.NewLine, preserved.Diagnostics));
        var preservedValues = Assert.Single(
            preserved.Root!.Members,
            member => member.Member.Symbol?.Name == "Items").Values;
        Assert.Equal("  alpha\n ", Assert.IsType<XamlBoundText>(preservedValues[0]).Text);
        Assert.Equal(" \t beta  ", Assert.IsType<XamlBoundText>(preservedValues[2]).Text);
        Assert.False(Assert.IsType<XamlBoundText>(preservedValues[0]).IsNormalized);

        var reset = new XamlSemanticBinder().Bind(
            Convert("""
<local:TextFlow xmlns:local="using:Demo" xml:space="preserve"><local:TextFlow xml:space="default">  alpha
 <local:InlineMarker />  beta  </local:TextFlow></local:TextFlow>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(reset.HasErrors, string.Join(Environment.NewLine, reset.Diagnostics));
        var nestedFlow = Assert.Single(
            DescendantValues(reset.Root).OfType<XamlBoundObject>(),
            value => value.Type.Symbol?.MetadataName == "Demo.TextFlow");
        Assert.Collection(
            Assert.Single(
                nestedFlow.Members,
                member => member.Member.Symbol?.Name == "Items").Values,
            value => Assert.Equal("alpha ", Assert.IsType<XamlBoundText>(value).Text),
            value => Assert.IsType<XamlBoundObject>(value),
            value => Assert.Equal(" beta", Assert.IsType<XamlBoundText>(value).Text));

        var ordinaryInfoset = Convert("""
<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <TextBlock />
  <TextBlock />
</StackPanel>
""");
        Assert.Contains(
            Assert.Single(ordinaryInfoset.Root!.Members).Values,
            value => value is XamlInfosetText);
        var ordinary = new XamlSemanticBinder().Bind(
            ordinaryInfoset,
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Equal(
            2,
            Assert.Single(ordinary.Root!.Members).Values.OfType<XamlBoundObject>().Count());
        Assert.DoesNotContain(
            Assert.Single(ordinary.Root.Members).Values,
            value => value is XamlBoundText);

        using var image = new System.IO.MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "WhitespaceMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            profile,
            new WhitespaceMetadataProvider());
        var metadataBreak = metadataTypeSystem.ResolveType(
            "using:Demo",
            "DerivedInlineBreak")!;
        Assert.True(metadataBreak.ShouldTrimSurroundingWhitespace);
        Assert.All(
            metadataBreak.TrimSurroundingWhitespace!.Annotation.DeclaredOn.Locations,
            location => Assert.True(location.IsInMetadata));
        Assert.False(metadataTypeSystem.ResolveType(
            "using:Demo",
            "DeferredAttachNode")!.IsUsableDuringInitialization);

        var metadataBound = new XamlSemanticBinder().Bind(
            Convert("""
<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:local="using:Demo">
  <local:EarlyAttachNode Value="metadata" />
</StackPanel>
"""),
            metadataTypeSystem);
        Assert.False(
            metadataBound.HasErrors,
            string.Join(Environment.NewLine, metadataBound.Diagnostics));
        var metadataProgram =
            new XamlConstructionLowerer().Lower(metadataBound);
        var metadataNode = Assert.Single(
            DescendantIr(metadataProgram.Root),
            value => value.Type.Symbol?.MetadataName ==
                "Demo.EarlyAttachNode");
        Assert.Equal(
            XamlIrInitializationMode.TopDown,
            metadataNode.InitializationMode);
        Assert.All(
            metadataNode.Type.Symbol!.Symbol.Locations,
            location => Assert.True(location.IsInMetadata));
    }

    [Fact]
    public void UsableDuringInitializationDrivesExplicitTopDownStructuredEmission()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <Page.Content>
    <local:InitializationHost>
      <local:InitializationHost.Child>
        <local:EarlyAttachNode Value="property" />
      </local:InitializationHost.Child>
      <local:InitializationAttachOwner.Node>
        <local:EarlyAttachNode Value="attached" />
      </local:InitializationAttachOwner.Node>
      <local:InitializationHost.Children>
        <local:EarlyAttachNode Value="collection">
          <local:EarlyAttachNode.Child>
            <local:EarlyAttachNode Value="nested-child" />
          </local:EarlyAttachNode.Child>
        </local:EarlyAttachNode>
        <local:DeferredAttachNode Value="bottom-up" />
      </local:InitializationHost.Children>
      <local:InitializationHost.Entries>
        <local:EarlyAttachNode x:Key="Entry" Value="dictionary" />
      </local:InitializationHost.Entries>
      <local:InitializationHost.ConstructorChild>
        <local:ConstructorInitializationHost>
          <x:Arguments>
            <local:EarlyAttachNode Value="constructor" />
          </x:Arguments>
        </local:ConstructorInitializationHost>
      </local:InitializationHost.ConstructorChild>
    </local:InitializationHost>
  </Page.Content>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new WhitespaceMetadataProvider());
        var infoset = Convert(xaml);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(
            bound.HasErrors,
            string.Join(Environment.NewLine, bound.Diagnostics));
        var program = new XamlConstructionLowerer().Lower(bound);
        var objects = DescendantIr(program.Root).ToArray();
        Assert.Equal(
            5,
            objects.Count(value =>
                value.Type.Symbol?.MetadataName == "Demo.EarlyAttachNode" &&
                value.InitializationMode ==
                    XamlIrInitializationMode.TopDown));
        var deferred = Assert.Single(
            objects,
            value => value.Type.Symbol?.MetadataName ==
                "Demo.DeferredAttachNode");
        Assert.Equal(
            XamlIrInitializationMode.BottomUp,
            deferred.InitializationMode);
        var constructorArgument = Assert.Single(
            objects,
            value =>
                value.Type.Symbol?.MetadataName ==
                    "Demo.EarlyAttachNode" &&
                value.Operations.SelectMany(
                        operation => operation.Values)
                    .OfType<XamlIrText>()
                    .Any(text => text.Text == "constructor"));
        Assert.Equal(
            XamlIrInitializationMode.BottomUp,
            constructorArgument.InitializationMode);
        Assert.Equal(
            XamlIrInitializationMode.BottomUp,
            Assert.Single(
                objects,
                value => value.Type.Symbol?.MetadataName ==
                    "Demo.InitializationHost").InitializationMode);

        var emitted = new CSharpXamlEmitter().Emit(
            infoset,
            typeSystem,
            profile,
            new XamlCompilerOptions());
        Assert.DoesNotContain(
            emitted.Diagnostics,
            diagnostic => diagnostic.Severity ==
                DiagnosticSeverity.Error);
        var generatedTree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var initialize = Assert.Single(
            generatedTree.GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText ==
                "InitializeComponent");
        var statements = initialize.Body!.Statements;

        AssertSchedule("property", topDown: true);
        AssertSchedule("attached", topDown: true);
        AssertSchedule("collection", topDown: true);
        AssertSchedule("nested-child", topDown: true);
        AssertSchedule("dictionary", topDown: true);
        AssertSchedule("bottom-up", topDown: false);
        AssertConstructorArgumentIsBottomUp();
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(generatedTree).GetDiagnostics(),
            diagnostic => diagnostic.Severity ==
                DiagnosticSeverity.Error);

        void AssertSchedule(string value, bool topDown)
        {
            var population = Assert.Single(
                statements.OfType<ExpressionStatementSyntax>(),
                statement =>
                    statement.Expression is AssignmentExpressionSyntax
                    {
                        Left: MemberAccessExpressionSyntax
                        {
                            Name.Identifier.ValueText: "Value"
                        },
                        Right: LiteralExpressionSyntax literal
                    } &&
                    literal.Token.ValueText == value);
            var assignment = (AssignmentExpressionSyntax)
                population.Expression;
            var childName = ((MemberAccessExpressionSyntax)
                assignment.Left).Expression.ToString();
            var declaration = Assert.Single(
                statements.OfType<LocalDeclarationStatementSyntax>(),
                statement => statement.Declaration.Variables.Any(
                    variable => variable.Identifier.ValueText ==
                        childName));
            var publication = Assert.Single(
                statements.OfType<ExpressionStatementSyntax>(),
                statement =>
                    !ReferenceEquals(statement, population) &&
                    IsPublication(statement.Expression, childName));
            var declarationIndex = statements.IndexOf(declaration);
            var publicationIndex = statements.IndexOf(publication);
            var populationIndex = statements.IndexOf(population);
            Assert.True(declarationIndex < publicationIndex);
            if (topDown)
                Assert.True(publicationIndex < populationIndex);
            else
                Assert.True(populationIndex < publicationIndex);
            Assert.NotEmpty(
                publication.GetAnnotations(
                    XamlProjectionMap.AnnotationKind));
            Assert.NotEmpty(
                population.GetAnnotations(
                    XamlProjectionMap.AnnotationKind));
        }

        void AssertConstructorArgumentIsBottomUp()
        {
            var population = Assert.Single(
                statements.OfType<ExpressionStatementSyntax>(),
                statement =>
                    statement.Expression is AssignmentExpressionSyntax
                    {
                        Left: MemberAccessExpressionSyntax
                        {
                            Name.Identifier.ValueText: "Value"
                        },
                        Right: LiteralExpressionSyntax literal
                    } &&
                    literal.Token.ValueText == "constructor");
            var childName = ((MemberAccessExpressionSyntax)
                ((AssignmentExpressionSyntax)population.Expression).Left)
                .Expression.ToString();
            var childDeclaration = Assert.Single(
                statements.OfType<LocalDeclarationStatementSyntax>(),
                statement => statement.Declaration.Variables.Any(
                    variable => variable.Identifier.ValueText ==
                        childName));
            var ownerDeclaration = Assert.Single(
                statements.OfType<LocalDeclarationStatementSyntax>(),
                statement => statement.Declaration.Variables.Any(
                    variable =>
                        variable.Initializer?.Value is
                            ObjectCreationExpressionSyntax creation &&
                        creation.Type.ToString().EndsWith(
                            "ConstructorInitializationHost",
                            StringComparison.Ordinal) &&
                        creation.ArgumentList!.Arguments.Any(argument =>
                            argument.Expression is IdentifierNameSyntax
                                identifier &&
                            identifier.Identifier.ValueText ==
                                childName)));
            Assert.True(
                statements.IndexOf(childDeclaration) <
                statements.IndexOf(population));
            Assert.True(
                statements.IndexOf(population) <
                statements.IndexOf(ownerDeclaration));
        }

        static bool IsPublication(
            ExpressionSyntax expression,
            string childName)
        {
            if (expression is AssignmentExpressionSyntax assignment &&
                assignment.Right is IdentifierNameSyntax right &&
                right.Identifier.ValueText == childName)
                return true;
            return expression is InvocationExpressionSyntax invocation &&
                invocation.ArgumentList.Arguments.Any(argument =>
                    argument.Expression is IdentifierNameSyntax identifier &&
                    identifier.Identifier.ValueText == childName);
        }
    }

    [Fact]
    public void ContentWrapperMetadataSelectsTypedWrappersAndEmitsStructuredConstruction()
    {
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new ContentWrapperMetadataProvider());

        var collection = typeSystem.ResolveType("using:Demo", "WrappedInlineCollection")!;
        Assert.Equal(2, collection.ContentWrappers.Count);
        var textShape = Assert.Single(
            collection.ContentWrappers,
            shape => shape.WrapperType?.MetadataName == "Demo.TextInline");
        Assert.True(textShape.IsValid, textShape.Error);
        Assert.Equal("tests.content-wrapper", textShape.ProviderId);
        Assert.Equal("Text", textShape.ContentMember!.Name);
        Assert.Equal(SpecialType.System_String, textShape.ContentValueType!.SpecialType);
        Assert.Equal("Demo.TextInline.TextInline()", textShape.Constructor!.ToDisplayString());
        Assert.Equal(
            "Demo.TextInline",
            Assert.IsAssignableFrom<INamedTypeSymbol>(
                textShape.Annotation.ValueConstant!.Value.Value).ToDisplayString());
        Assert.False(textShape.Annotation.IsInherited);

        var derived = typeSystem.ResolveType(
            "using:Demo",
            "DerivedWrappedInlineCollection")!;
        Assert.Equal(2, derived.ContentWrappers.Count);
        Assert.All(derived.ContentWrappers, shape =>
        {
            Assert.True(shape.IsValid, shape.Error);
            Assert.True(shape.Annotation.IsInherited);
            Assert.Equal(1, shape.Annotation.InheritanceDepth);
        });

        var bound = new XamlSemanticBinder().Bind(
            Convert("""
<local:WrappedFlow xmlns:local="using:Demo">hello<local:DirectInline /><local:InlineMarker /></local:WrappedFlow>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var items = Assert.Single(bound.Root!.Members).Values;
        Assert.Collection(
            items,
            value =>
            {
                var wrapper = Assert.IsType<XamlBoundObject>(value);
                Assert.Equal("Demo.TextInline", wrapper.Type.Symbol!.MetadataName);
                var text = Assert.IsType<XamlBoundText>(
                    Assert.Single(Assert.Single(wrapper.Members).Values));
                Assert.Equal("hello", text.Text);
                Assert.Equal(ProGPU.Xaml.Schema.XamlTextSyntaxKind.Intrinsic,
                    text.TextSyntax.Kind);
            },
            value => Assert.Equal(
                "Demo.DirectInline",
                Assert.IsType<XamlBoundObject>(value).Type.Symbol!.MetadataName),
            value =>
            {
                var wrapper = Assert.IsType<XamlBoundObject>(value);
                Assert.Equal("Demo.ObjectInline", wrapper.Type.Symbol!.MetadataName);
                Assert.Equal(
                    "Demo.InlineMarker",
                    Assert.IsType<XamlBoundObject>(
                        Assert.Single(Assert.Single(wrapper.Members).Values))
                        .Type.Symbol!.MetadataName);
            });
        Assert.NotEqual(items[0].StableId,
            Assert.Single(Assert.Single(Assert.IsType<XamlBoundObject>(items[0]).Members).Values).StableId);
        var rebound = new XamlSemanticBinder().Bind(
            Convert("""
<local:WrappedFlow xmlns:local="using:Demo">hello<local:DirectInline /><local:InlineMarker /></local:WrappedFlow>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Equal(
            items.Select(value => value.StableId),
            Assert.Single(rebound.Root!.Members).Values.Select(value => value.StableId));

        var program = new XamlConstructionLowerer().Lower(bound);
        Assert.Contains(
            DescendantIr(program.Root),
            value => value.Type.Symbol?.MetadataName == "Demo.TextInline");
        Assert.Contains(
            DescendantIr(program.Root),
            value => value.Type.Symbol?.MetadataName == "Demo.ObjectInline");
        var emissionBound = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage"><local:WrappedFlow>hello<local:InlineMarker /></local:WrappedFlow></Page>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(
            emissionBound.HasErrors,
            string.Join(Environment.NewLine, emissionBound.Diagnostics));
        var emitted = new CSharpXamlEmitter().EmitProgram(
            new XamlConstructionLowerer().Lower(emissionBound),
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var generated = Assert.Single(emitted.Sources);
        Assert.Contains("new global::Demo.TextInline()", generated.Source, StringComparison.Ordinal);
        Assert.Contains("new global::Demo.ObjectInline()", generated.Source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(generated.GeneratedSyntaxTree!).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var ambiguous = new XamlSemanticBinder().Bind(
            Convert("""
<local:AmbiguousWrappedFlow xmlns:local="using:Demo"><local:InlineMarker /></local:AmbiguousWrappedFlow>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(ambiguous.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2054");

        var noApplicable = new XamlSemanticBinder().Bind(
            Convert("""
<local:StringOnlyWrappedFlow xmlns:local="using:Demo"><local:InlineMarker /></local:StringOnlyWrappedFlow>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(noApplicable.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2053");

        var invalidCollection = typeSystem.ResolveType(
            "using:Demo",
            "InvalidInlineCollection")!;
        var invalidShape = Assert.Single(invalidCollection.ContentWrappers);
        Assert.False(invalidShape.IsValid);
        Assert.Contains("not assignable", invalidShape.Error, StringComparison.Ordinal);
        var invalid = new XamlSemanticBinder().Bind(
            Convert("""
<local:InvalidWrappedFlow xmlns:local="using:Demo"><local:InlineMarker /></local:InvalidWrappedFlow>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        var invalidDiagnostic = Assert.Single(
            invalid.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2052");
        Assert.Equal("5.2.2.3", invalidDiagnostic.Properties["MSXamlSection"]);

        using var image = new System.IO.MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "ContentWrapperMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            profile,
            new ContentWrapperMetadataProvider());
        var metadataShape = Assert.Single(
            metadataTypeSystem.ResolveType(
                "using:Demo",
                "WrappedInlineCollection")!.ContentWrappers,
            shape => shape.WrapperType?.MetadataName == "Demo.TextInline");
        Assert.True(metadataShape.IsValid, metadataShape.Error);
        Assert.All(
            metadataShape.WrapperType!.Symbol.Locations,
            location => Assert.True(location.IsInMetadata));
        Assert.All(
            metadataShape.ContentMember!.Symbol!.Locations,
            location => Assert.True(location.IsInMetadata));
    }

    [Fact]
    public void ConstructorArgumentMetadataIsExactSavePathEvidence()
    {
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new ConstructorArgumentMetadataProvider());
        var type = typeSystem.ResolveType(
            "using:Demo",
            "ConstructorProjectedValue")!;
        var member = typeSystem.ResolveMember(
            type,
            "using:Demo",
            ownerTypeName: null,
            memberName: "Value")!;
        var shape = Assert.IsType<ProGPU.Xaml.Schema.XamlConstructorArgumentShapeInfo>(
            member.ConstructorArgument);
        Assert.True(shape.IsValid, shape.Error);
        Assert.Equal("value", shape.ArgumentName);
        Assert.Equal("tests.constructor-argument", shape.ProviderId);
        Assert.Equal(
            "Demo.ConstructorProjectedValue.ConstructorProjectedValue(string)",
            shape.Constructor!.ToDisplayString());
        Assert.Equal("string value", shape.Parameter!.ToDisplayString());
        Assert.Equal(SpecialType.System_String, shape.Parameter.Type.SpecialType);
        Assert.Single(shape.Candidates);
        Assert.Equal(
            "System.Windows.Markup.ConstructorArgumentAttribute",
            shape.Annotation.Attribute.AttributeClass!.ToDisplayString());
        Assert.Equal(member.Symbol, shape.Annotation.DeclaredOn);

        var bound = new XamlSemanticBinder().Bind(
            Convert("""
<local:ConstructorProjectedValue xmlns:local="using:Demo" Value="hello" />
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var operation = Assert.Single(
            new XamlConstructionLowerer().Lower(bound).Root!.Operations);
        Assert.Equal(XamlIrOperationKind.SetMember, operation.Kind);
        Assert.Equal("Value", operation.Member.Symbol!.Name);
        Assert.False(bound.SerializationPlans.Root!.UsesConstructorArgument);
        var constructorPlan = new XamlSerializationPlanner().Build(
            bound,
            new XamlSerializationPlanOptions
            {
                ConstructorRepresentation =
                    XamlConstructorRepresentationMode.PreferSingleMappedMember
            });
        Assert.True(
            constructorPlan.IsValid,
            string.Join(
                Environment.NewLine,
                constructorPlan.Issues.Select(issue => issue.Message)));
        Assert.True(constructorPlan.Root!.UsesConstructorArgument);
        var constructorMember = Assert.Single(
            constructorPlan.Root.Members,
            item => item.Disposition ==
                XamlSerializationDisposition.ConstructorArgument);
        Assert.Equal("Value", constructorMember.Member!.Name);

        var expression = RoslynXamlConstructorArgumentSyntaxFactory
            .CreateObjectCreationExpression(
                shape,
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal("round-trip")));
        var plannedExpression =
            RoslynXamlSerializationPlanSyntaxFactory.CreateConstructorExpression(
                constructorPlan.Root,
                _ => SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal("planned-round-trip")));
        Assert.Contains(
            "new global::Demo.ConstructorProjectedValue",
            plannedExpression.NormalizeWhitespace().ToFullString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "new global::Demo.ConstructorProjectedValue",
            expression.NormalizeWhitespace().ToFullString(),
            StringComparison.Ordinal);
        var ambiguousBound = new XamlSemanticBinder().Bind(
            Convert("""
<local:AmbiguousConstructorProjectedValue
    xmlns:local="using:Demo"
    First="one"
    Second="2" />
"""),
            typeSystem);
        var ambiguousPlan = new XamlSerializationPlanner().Build(
            ambiguousBound,
            new XamlSerializationPlanOptions
            {
                ConstructorRepresentation =
                    XamlConstructorRepresentationMode.PreferSingleMappedMember
            });
        Assert.False(ambiguousPlan.IsValid);
        Assert.Contains(
            ambiguousPlan.Issues,
            issue => issue.Kind ==
                XamlSerializationPlanIssueKind
                    .AmbiguousConstructorRepresentation);
        var probe = SyntaxFactory.CompilationUnit()
            .AddMembers(
                SyntaxFactory.NamespaceDeclaration(
                        SyntaxFactory.IdentifierName("Demo"))
                    .AddMembers(
                        SyntaxFactory.ClassDeclaration("__ConstructorArgumentProbe")
                            .AddModifiers(SyntaxFactory.Token(SyntaxKind.InternalKeyword))
                            .AddMembers(
                                SyntaxFactory.MethodDeclaration(
                                        SyntaxFactory.IdentifierName(
                                            "ConstructorProjectedValue"),
                                        "Create")
                                    .AddModifiers(
                                        SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                                        SyntaxFactory.Token(SyntaxKind.StaticKeyword))
                                    .WithExpressionBody(
                                        SyntaxFactory.ArrowExpressionClause(expression))
                                    .WithSemicolonToken(
                                        SyntaxFactory.Token(SyntaxKind.SemicolonToken)))));
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(
                CSharpSyntaxTree.Create(probe.NormalizeWhitespace())).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var invalidType = typeSystem.ResolveType(
            "using:Demo",
            "InvalidConstructorProjectedValue")!;
        var invalidMember = typeSystem.ResolveMember(
            invalidType,
            "using:Demo",
            ownerTypeName: null,
            memberName: "Value")!;
        Assert.False(invalidMember.ConstructorArgument!.IsValid);
        Assert.Contains(
            "no public one-argument constructor parameter",
            invalidMember.ConstructorArgument.Error,
            StringComparison.Ordinal);
        var invalid = new XamlSemanticBinder().Bind(
            Convert("""
<local:InvalidConstructorProjectedValue xmlns:local="using:Demo" Value="hello" />
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        var diagnostic = Assert.Single(
            invalid.Diagnostics,
            candidate => candidate.Id == "PGXAML2055");
        Assert.Equal("5.4.2", diagnostic.Properties["MSXamlSection"]);

        using var image = new System.IO.MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "ConstructorArgumentMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            profile,
            new ConstructorArgumentMetadataProvider());
        var metadataType = metadataTypeSystem.ResolveType(
            "using:Demo",
            "ConstructorProjectedValue")!;
        var metadataShape = metadataTypeSystem.ResolveMember(
            metadataType,
            "using:Demo",
            ownerTypeName: null,
            memberName: "Value")!.ConstructorArgument!;
        Assert.True(metadataShape.IsValid, metadataShape.Error);
        Assert.All(
            metadataShape.Constructor!.Locations,
            location => Assert.True(location.IsInMetadata));
        Assert.All(
            metadataShape.Parameter!.Locations,
            location => Assert.True(location.IsInMetadata));
    }

    [Fact]
    public void AmbientMetadataFlowsThroughTypesMembersAttachedGettersAndResourceScopes()
    {
        const string ambientXaml = """
<local:AmbientHost xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   xmlns:local="using:Demo">
  <local:AmbientHost.TypedContext>
    <local:AmbientDictionary>
      <TextBlock x:Key="Accent" Text="green" />
    </local:AmbientDictionary>
  </local:AmbientHost.TypedContext>
  <ControlTemplate>
    <TextBlock Text="{StaticResource Accent}" />
  </ControlTemplate>
</local:AmbientHost>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new AmbientMetadataProvider());

        var ambientDictionary = typeSystem.ResolveType(
            "using:Demo",
            "AmbientDictionary")!;
        Assert.True(ambientDictionary.IsAmbient);
        Assert.Equal("tests.ambient", ambientDictionary.Ambient!.Annotation.ProviderId);
        Assert.Equal(
            "System.Windows.Markup.AmbientAttribute",
            ambientDictionary.Ambient.Annotation.Attribute.AttributeClass!.ToDisplayString());
        Assert.False(ambientDictionary.Ambient.Annotation.IsInherited);
        Assert.True(typeSystem.ResolveType(
            "using:Demo",
            "DerivedAmbientDictionary")!.IsAmbient);

        var host = typeSystem.ResolveType("using:Demo", "AmbientHost")!;
        var declared = typeSystem.ResolveMember(
            host,
            "using:Demo",
            ownerTypeName: null,
            memberName: "DeclaredContext")!;
        Assert.True(declared.IsDeclaredAmbient);
        Assert.True(declared.IsAmbient);
        Assert.Equal(declared.Symbol, declared.Ambient!.Annotation.DeclaredOn);

        var typed = typeSystem.ResolveMember(
            host,
            "using:Demo",
            ownerTypeName: null,
            memberName: "TypedContext")!;
        Assert.Null(typed.Ambient);
        Assert.False(typed.IsDeclaredAmbient);
        Assert.True(typed.IsAmbient);

        var stackPanel = typeSystem.ResolveType(
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
            "StackPanel")!;
        var attached = typeSystem.ResolveMember(
            stackPanel,
            "using:Demo",
            ownerTypeName: "AmbientAttachedOwner",
            memberName: "Context")!;
        Assert.True(attached.IsDeclaredAmbient);
        Assert.Equal("GetContext", attached.Ambient!.Annotation.DeclaredOn.Name);
        Assert.Equal("tests.ambient", attached.Ambient.Annotation.ProviderId);

        var bound = new XamlSemanticBinder().Bind(
            Convert(ambientXaml),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var graph = new XamlResourceGraphBuilder().Build(bound);
        var definition = Assert.Single(graph.Definitions);
        Assert.Equal("Accent", definition.Key);
        var reference = Assert.Single(graph.References);
        Assert.Equal(XamlResourceResolutionKind.Resolved, reference.Resolution);
        Assert.Equal(definition.ValueStableId, reference.DefinitionStableId);
        var ambientGraph = new XamlAmbientContextGraphBuilder().Build(bound);
        var definitionContext = ambientGraph.GetContext(definition.ValueStableId);
        Assert.Contains(
            definitionContext.Values,
            value => value.IsTypeAmbient &&
                     value.AmbientType.MetadataName == "Demo.AmbientDictionary");
        var referenceContext = ambientGraph.GetContext(reference.StableId);
        Assert.Contains(
            referenceContext.Values,
            value => value.AmbientMember?.Name == "TypedContext" &&
                     value.OwnerStableId == bound.Root!.StableId);
        Assert.Single(referenceContext.DeferredBoundaryStableIds);

        var explicitRoleTypeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new AmbientExplicitResourceRoleMetadataProvider());
        var explicitRoleHost = explicitRoleTypeSystem.ResolveType(
            "using:Demo",
            "AmbientHost")!;
        var explicitRoleMember = explicitRoleTypeSystem.ResolveMember(
            explicitRoleHost,
            "using:Demo",
            ownerTypeName: null,
            memberName: "TypedContext")!;
        Assert.True(explicitRoleMember.IsAmbient);
        Assert.Equal(
            ProGPU.Xaml.Schema.XamlResourceMemberRole.Source,
            explicitRoleMember.ResourceRole);
        var explicitRoleBound = new XamlSemanticBinder().Bind(
            Convert(ambientXaml),
            explicitRoleTypeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Empty(new XamlResourceGraphBuilder().Build(explicitRoleBound).Definitions);

        using var image = new System.IO.MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "AmbientMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            profile,
            new AmbientMetadataProvider());
        var metadataAmbient = metadataTypeSystem.ResolveType(
            "using:Demo",
            "AmbientDictionary")!;
        Assert.True(metadataAmbient.IsAmbient);
        Assert.All(
            metadataAmbient.Ambient!.Annotation.DeclaredOn.Locations,
            location => Assert.True(location.IsInMetadata));
        var metadataHost = metadataTypeSystem.ResolveType("using:Demo", "AmbientHost")!;
        Assert.True(metadataTypeSystem.ResolveMember(
            metadataHost,
            "using:Demo",
            ownerTypeName: null,
            memberName: "TypedContext")!.IsAmbient);
    }

    [Fact]
    public void DeferredLoadMetadataProducesExactLoaderSymbolsAndDeferredIr()
    {
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new DeferredLoadMetadataProvider());

        var deferredType = typeSystem.ResolveType(
            "using:Demo",
            "DeferredTemplateValue")!;
        var typeShape = Assert.IsType<
            ProGPU.Xaml.Schema.XamlDeferringLoaderShapeInfo>(
            deferredType.DeferringLoader);
        Assert.True(typeShape.IsValid, typeShape.Error);
        Assert.Equal("tests.deferred-load", typeShape.ProviderId);
        Assert.Equal("Demo.DemoDeferringLoader",
            typeShape.LoaderType!.ToDisplayString());
        Assert.Equal(SpecialType.System_Object, typeShape.ContentType!.SpecialType);
        Assert.Equal("Load", typeShape.LoadMethod!.Name);
        Assert.Equal("Save", typeShape.SaveMethod!.Name);
        Assert.Equal("System.Xaml.XamlReader",
            typeShape.LoadMethod.Parameters[0].Type.ToDisplayString());
        Assert.Equal(typeShape.LoadMethod.Parameters[0].Type, typeShape.SaveMethod.ReturnType);
        Assert.Equal(
            "System.Windows.Markup.XamlDeferLoadAttribute",
            typeShape.Annotation.Attribute.AttributeClass!.ToDisplayString());

        var typedHost = typeSystem.ResolveType(
            "using:Demo",
            "TypedDeferredHost")!;
        var typedMember = typeSystem.ResolveMember(
            typedHost,
            "using:Demo",
            ownerTypeName: null,
            memberName: "Template")!;
        Assert.Equal(ProGPU.Xaml.Schema.XamlMemberKind.DeferredContent, typedMember.Kind);
        Assert.Null(typedMember.DeclaredDeferringLoader);
        Assert.Equal(
            typeShape.LoaderType,
            typedMember.DeferringLoader!.LoaderType,
            SymbolEqualityComparer.Default);
        Assert.Equal(
            typeShape.LoadMethod,
            typedMember.DeferringLoader.LoadMethod,
            SymbolEqualityComparer.Default);

        var overrideHost = typeSystem.ResolveType(
            "using:Demo",
            "OverrideDeferredHost")!;
        var overrideMember = typeSystem.ResolveMember(
            overrideHost,
            "using:Demo",
            ownerTypeName: null,
            memberName: "Template")!;
        Assert.Equal(
            SpecialType.System_String,
            overrideMember.DeferringLoader!.ContentType!.SpecialType);
        Assert.NotNull(overrideMember.DeclaredDeferringLoader);
        Assert.Equal(
            overrideMember.DeclaredDeferringLoader,
            overrideMember.DeferringLoader);

        var memberHost = typeSystem.ResolveType(
            "using:Demo",
            "MemberDeferredHost")!;
        var member = typeSystem.ResolveMember(
            memberHost,
            "using:Demo",
            ownerTypeName: null,
            memberName: "Template")!;
        var memberShape = Assert.IsType<
            ProGPU.Xaml.Schema.XamlDeferringLoaderShapeInfo>(
            member.DeclaredDeferringLoader);
        Assert.True(memberShape.IsValid, memberShape.Error);
        Assert.Equal("Demo.DemoDeferringLoader", memberShape.LoaderTypeName);
        Assert.Equal("System.Object", memberShape.ContentTypeName);
        Assert.Equal(ProGPU.Xaml.Schema.XamlMemberKind.DeferredContent, member.Kind);

        var bound = new XamlSemanticBinder().Bind(
            Convert("""
<local:MemberDeferredHost xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:local="using:Demo">
  <local:MemberDeferredHost.Template>
    <TextBlock Text="first" />
    <TextBlock Text="second" />
  </local:MemberDeferredHost.Template>
</local:MemberDeferredHost>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var operation = Assert.Single(
            new XamlConstructionLowerer().Lower(bound).Root!.Operations);
        Assert.Equal(XamlIrOperationKind.SetDeferredContent, operation.Kind);
        Assert.Equal(2, operation.Values.Length);
        var firstDeferredValue = Assert.IsType<XamlBoundObject>(
            Assert.Single(bound.Root!.Members).Values[0]);
        var ambientContext = new XamlAmbientContextGraphBuilder()
            .Build(bound)
            .GetContext(firstDeferredValue.StableId);
        Assert.Single(ambientContext.DeferredBoundaryStableIds);
        Assert.Equal(
            Assert.Single(bound.Root.Members).StableId,
            ambientContext.DeferredBoundaryStableIds[0]);

        var load = RoslynXamlDeferringLoaderSyntaxFactory.CreateLoadExpression(
            memberShape,
            SyntaxFactory.IdentifierName("value"),
            SyntaxFactory.IdentifierName("services"));
        var save = RoslynXamlDeferringLoaderSyntaxFactory.CreateSaveExpression(
            memberShape,
            SyntaxFactory.IdentifierName("value"),
            SyntaxFactory.IdentifierName("services"));
        var objectType = SyntaxFactory.PredefinedType(
            SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        MethodDeclarationSyntax ProbeMethod(string name, ExpressionSyntax body) =>
            SyntaxFactory.MethodDeclaration(objectType, name)
                .AddModifiers(
                    SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                    SyntaxFactory.Token(SyntaxKind.StaticKeyword))
                .AddParameterListParameters(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"))
                        .WithType(objectType),
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("services"))
                        .WithType(objectType))
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(body))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        var probe = SyntaxFactory.CompilationUnit().AddMembers(
            SyntaxFactory.ClassDeclaration("__DeferredLoaderProbe")
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.InternalKeyword))
                .AddMembers(ProbeMethod("Load", load), ProbeMethod("Save", save)));
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(
                CSharpSyntaxTree.Create(probe.NormalizeWhitespace())).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var malformed = new[]
        {
            ("AbstractDeferredValue", "non-abstract"),
            ("InternalDeferredValue", "public"),
            ("WrongDeferredValue", "compatible Load"),
            ("AmbiguousDeferredValue", "multiple compatible")
        };
        foreach (var (typeName, expectedError) in malformed)
        {
            var shape = typeSystem.ResolveType(
                "using:Demo",
                typeName)!.DeferringLoader!;
            Assert.False(shape.IsValid);
            Assert.Contains(expectedError, shape.Error, StringComparison.Ordinal);
            Assert.NotEmpty(shape.LoadCandidates);
            Assert.NotEmpty(shape.SaveCandidates);
        }
        Assert.Equal(
            2,
            typeSystem.ResolveType(
                "using:Demo",
                "AmbiguousDeferredValue")!.DeferringLoader!.LoadCandidates.Count);
        Assert.Equal(
            2,
            typeSystem.ResolveType(
                "using:Demo",
                "AmbiguousDeferredValue")!.DeferringLoader!.SaveCandidates.Count);

        var invalidHost = typeSystem.ResolveType(
            "using:Demo",
            "InvalidDeferredHost")!;
        var invalidMember = typeSystem.ResolveMember(
            invalidHost,
            "using:Demo",
            ownerTypeName: null,
            memberName: "Template")!;
        Assert.False(invalidMember.DeferringLoader!.IsValid);
        var invalid = new XamlSemanticBinder().Bind(
            Convert("""
<local:InvalidDeferredHost xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                           xmlns:local="using:Demo">
  <local:InvalidDeferredHost.Template><TextBlock /></local:InvalidDeferredHost.Template>
</local:InvalidDeferredHost>
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        var diagnostic = Assert.Single(
            invalid.Diagnostics,
            candidate => candidate.Id == "PGXAML2056");
        Assert.Equal("5.5", diagnostic.Properties["MSXamlSection"]);

        using var image = new System.IO.MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "DeferredLoadMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            profile,
            new DeferredLoadMetadataProvider());
        var metadataShape = metadataTypeSystem.ResolveType(
            "using:Demo",
            "DeferredTemplateValue")!.DeferringLoader!;
        Assert.True(metadataShape.IsValid, metadataShape.Error);
        Assert.All(
            metadataShape.LoaderType!.Locations,
            location => Assert.True(location.IsInMetadata));
        Assert.All(
            metadataShape.LoadMethod!.Locations,
            location => Assert.True(location.IsInMetadata));
    }

    [Fact]
    public void MarkupBracketAnnotationsConfigureSharedParserWithoutLosingRoslynEvidence()
    {
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new MarkupBracketMetadataProvider());
        var extensionType = typeSystem.ResolveType(
            "using:Demo",
            "Bracket")!;
        var pathMember = typeSystem.ResolveMember(
            extensionType,
            "using:Demo",
            ownerTypeName: null,
            memberName: "Path")!;
        Assert.Equal(2, pathMember.MarkupExtensionBracketCharacters.Count);
        Assert.All(
            pathMember.MarkupExtensionBracketCharacters,
            pair => Assert.True(pair.IsValid, pair.Error));
        Assert.Equal(
            new[] { '(', '[' },
            pathMember.MarkupExtensionBracketCharacters
                .Select(pair => pair.OpeningBracket)
                .OrderBy(value => value));
        Assert.All(
            pathMember.MarkupExtensionBracketCharacters,
            pair =>
            {
                Assert.Equal("tests.markup-brackets", pair.Annotation.ProviderId);
                Assert.Equal(pathMember.Symbol, pair.Annotation.DeclaredOn);
            });

        var markupOptions = XamlMarkupBracketPolicy.CreateOptions(pathMember);
        const string markup =
            "{local:Bracket Path=Items[(key,value)=selected], Mode=OneWay}";
        var markupText = SourceText.From(markup);
        var parsed = new XamlMarkupExtensionParser().Parse(
            markupText,
            new TextSpan(0, markupText.Length),
            options: markupOptions);
        Assert.False(parsed.HasErrors, string.Join(
            Environment.NewLine,
            parsed.Diagnostics));
        Assert.Equal(2, parsed.Root!.NamedArguments.Count);
        Assert.Equal(
            "Items[(key,value)=selected]",
            Assert.IsType<XamlMarkupTextValue>(
                parsed.Root.NamedArguments[0].Value).Text);

        var xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo"
      Content="{local:Bracket Path=Items[(key,value)=selected], Mode=OneWay}" />
""";
        var syntax = XamlParser.Parse(
            SourceText.From(xaml),
            "Bracket.xaml",
            new XamlParseOptions { Mode = XamlParseMode.Recovering }).Document;
        var infoset = new XamlInfosetConverter().Convert(
            syntax,
            new XamlInfosetConversionOptions
            {
                Mode = XamlParseMode.Recovering
            });
        Assert.NotNull(infoset.Root);
        var schemaNeutralExtension = Assert.IsType<XamlInfosetObject>(
            Assert.Single(
                Assert.Single(
                    infoset.Root.Members,
                    member => member.Name.LocalName == "Content").Values));
        Assert.Equal(markup, schemaNeutralExtension.MarkupText);
        var bound = new XamlSemanticBinder().Bind(
            infoset,
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var extension = Assert.Single(
            DescendantValues(bound.Root).OfType<XamlBoundObject>(),
            value => value.Type.Symbol?.MetadataName == "Demo.BracketExtension");
        Assert.Equal(
            "Items[(key,value)=selected]",
            Assert.IsType<XamlBoundText>(
                Assert.Single(
                    Assert.Single(
                        extension.Members,
                        member => member.Member.Symbol?.Name == "Path").Values)).Text);

        var nested = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo"
      Content="{local:Wrapper Value={local:Bracket Path=Items[(key,value)=selected], Mode=OneWay}}" />
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(nested.HasErrors, string.Join(Environment.NewLine, nested.Diagnostics));
        var nestedExtension = Assert.Single(
            DescendantValues(nested.Root).OfType<XamlBoundObject>(),
            value => value.Type.Symbol?.MetadataName == "Demo.BracketExtension");
        Assert.Equal(
            "Items[(key,value)=selected]",
            Assert.IsType<XamlBoundText>(
                Assert.Single(
                    Assert.Single(
                        nestedExtension.Members,
                        member => member.Member.Symbol?.Name == "Path").Values)).Text);

        var conflictMember = typeSystem.ResolveMember(
            extensionType,
            "using:Demo",
            ownerTypeName: null,
            memberName: "Conflict")!;
        Assert.Equal(2, conflictMember.MarkupExtensionBracketCharacters.Count);
        Assert.All(
            conflictMember.MarkupExtensionBracketCharacters,
            pair => Assert.False(pair.IsValid));
        var conflict = new XamlSemanticBinder().Bind(
            Convert("""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:local="using:Demo"
      Content="{local:Bracket Conflict=value}" />
"""),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Equal(
            2,
            conflict.Diagnostics.Count(diagnostic => diagnostic.Id == "PGXAML2057"));

        using var image = new System.IO.MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var metadataCompilation = CSharpCompilation.Create(
            "MarkupBracketMetadataConsumer",
            references: PlatformReferences().Append(
                MetadataReference.CreateFromImage(image.ToArray())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var metadataTypeSystem = new RoslynXamlTypeSystem(
            metadataCompilation,
            profile,
            new MarkupBracketMetadataProvider());
        var metadataType = metadataTypeSystem.ResolveType(
            "using:Demo",
            "Bracket")!;
        var metadataPairs = metadataTypeSystem.ResolveMember(
            metadataType,
            "using:Demo",
            ownerTypeName: null,
            memberName: "Path")!.MarkupExtensionBracketCharacters;
        Assert.Equal(2, metadataPairs.Count);
        Assert.All(
            metadataPairs,
            pair => Assert.All(
                pair.Annotation.DeclaredOn.Locations,
                location => Assert.True(location.IsInMetadata)));
    }

    [Fact]
    public void IntrinsicAndEnumerationTextSyntaxIsSchemaDataAndValidatedDuringBinding()
    {
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(CreateCompilation(), profile);
        var valid = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><local:ScalarControl Count=\"255\" Enabled=\"true\" Mode=\"compact\" /></Page>"),
            typeSystem);
        Assert.False(valid.HasErrors, string.Join(Environment.NewLine, valid.Diagnostics));
        var scalar = Assert.Single(DescendantValues(valid.Root).OfType<XamlBoundObject>(),
            value => value.Type.Symbol?.Name == "ScalarControl");
        var enabled = Assert.Single(scalar.Members, member => member.Member.Symbol?.Name == "Enabled");
        var booleanSyntax = Assert.IsType<XamlBoundText>(Assert.Single(enabled.Values)).TextSyntax;
        Assert.Equal(ProGPU.Xaml.Schema.XamlTextSyntaxKind.Intrinsic, booleanSyntax.Kind);
        Assert.Equal(new[] { "True", "False" }, booleanSyntax.Values.Select(value => value.Text));

        var invalid = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><local:ScalarControl Count=\"-1\" Enabled=\"perhaps\" Mode=\"Unknown\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Equal(3, invalid.Diagnostics.Count(diagnostic => diagnostic.Id == "PGXAML2042"));
        Assert.All(invalid.Diagnostics.Where(diagnostic => diagnostic.Id == "PGXAML2042"), diagnostic =>
            Assert.Equal("6.3.2.4", diagnostic.Properties["MSXamlSection"]));
    }

    [Fact]
    public void FrameworkProfilePublishesSyntheticMarkupExtensionSchemaTypes()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Content="{StaticResource Accent}" />
""";
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(CreateCompilation(), profile);
        var bound = new XamlSemanticBinder().Bind(Convert(xaml), typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var extension = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundObject>(),
            value => value.Type.RequestedName.LocalName == "StaticResource");
        Assert.True(extension.Type.Symbol!.IsMarkupExtension);
        Assert.Equal(SpecialType.System_Object, extension.Type.Symbol.ReturnValueType!.SpecialType);
        Assert.StartsWith("synthetic:WinUI:", extension.Type.Symbol.MetadataName, StringComparison.Ordinal);

        var compiledBinding = typeSystem.ResolveType(ProGPU.Xaml.Syntax.XamlNamespaces.Language2006, "Bind");
        Assert.True(compiledBinding?.IsMarkupExtension);

        var bindingDocument = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding Path=Customer.Name, Mode=OneWay, FallbackValue='missing'}\" /></Page>"),
            typeSystem);
        Assert.False(bindingDocument.HasErrors, string.Join(Environment.NewLine, bindingDocument.Diagnostics));
        var binding = Assert.Single(DescendantValues(bindingDocument.Root).OfType<XamlBoundObject>(),
            value => value.Type.RequestedName.LocalName == "Binding");
        Assert.Equal(new[] { "Path", "Mode", "FallbackValue" },
            binding.Members.Select(member => member.Member.Symbol!.Name));
        Assert.All(binding.Members, member =>
        {
            Assert.Null(member.Member.Symbol!.Symbol);
            Assert.StartsWith("synthetic:WinUI:Binding.", member.Member.Symbol.Identity, StringComparison.Ordinal);
        });

        var unknownArgument = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding UnknownOption=value}\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(unknownArgument.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2002");
    }

    [Fact]
    public void WinUiBindingEmitsStructuredRuntimeActivationWithLookupRoot()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <TextBlock Text="{Binding Path=Customer.Name, Mode=TwoWay,
                            ElementName=SourceText, FallbackValue=missing}" />
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var emitted = new CSharpXamlEmitter().Emit(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new XamlCompilerOptions { ResourceUri = "Pages/Binding.xaml" });

        Assert.DoesNotContain(
            emitted.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var invocation = Assert.Single(
            tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            candidate => candidate.Expression.ToString().EndsWith(
                "BindingOperations.SetBinding",
                StringComparison.Ordinal));
        Assert.Equal(5, invocation.ArgumentList.Arguments.Count);
        Assert.Equal("\"Text\"", invocation.ArgumentList.Arguments[1].Expression.ToString());
        Assert.Equal("this", invocation.ArgumentList.Arguments[4].Expression.ToString());
        var creation = Assert.IsType<ObjectCreationExpressionSyntax>(
            invocation.ArgumentList.Arguments[2].Expression);
        Assert.Equal(
            new[] { "Path", "ElementName", "Mode", "FallbackValue" },
            creation.Initializer!.Expressions
                .OfType<AssignmentExpressionSyntax>()
                .Select(static assignment => assignment.Left.ToString())
                .ToArray());
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    }

    [Fact]
    public void WinUiXBindBindsCanonicalPathSymbolsAndLowersDistinctIr()
    {
        const string additions = """
namespace Demo {
  public partial class MainPage {
    public ViewModel ViewModel { get; } = new();
  }
  public sealed class ViewModel {
    public string Name { get; set; } = "Ada";
  }
  public sealed class BindingTarget : Microsoft.UI.Xaml.DependencyObject {
    public static readonly Microsoft.UI.Xaml.DependencyProperty ValueProperty = new();
    public string? Value { get; set; }
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:BindingTarget Value="{x:Bind ViewModel.Name, Mode=TwoWay}" />
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText(additions));
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        Assert.Equal("Demo.MainPage", bound.RootClassType?.MetadataName);
        var compiled = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundCompiledBinding>());
        Assert.Equal(XamlCompiledBindingMode.TwoWay, compiled.Mode);
        Assert.Equal(new[] { "ViewModel", "Name" },
            compiled.PathSegments.Select(segment => segment.Member.Name));
        Assert.All(compiled.PathSegments, segment => Assert.NotNull(segment.Member));
        Assert.True(compiled.CanWrite);

        var program = new XamlConstructionLowerer().Lower(bound);
        var ir = Assert.Single(DescendantIrValues(program.Root).OfType<XamlIrCompiledBinding>());
        Assert.Same(compiled, ir.Binding);
        Assert.Equal(XamlExpressionRole.CompiledBinding, ir.Extension.Type.Symbol?.ExpressionRole);
    }

    [Fact]
    public void WinUiXDefaultBindModeFlowsThroughNestedObjectScope()
    {
        const string additions = """
namespace Demo {
  public partial class MainPage { public string Title { get; set; } = "Hello"; }
  public sealed class BindingTarget : Microsoft.UI.Xaml.DependencyObject {
    public static readonly Microsoft.UI.Xaml.DependencyProperty ValueProperty = new();
    public string? Value { get; set; }
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage"
      x:DefaultBindMode="OneWay">
  <local:BindingTarget Value="{x:Bind Title}" />
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText(additions));
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, new WinUiXamlProfile()));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        Assert.Equal(
            XamlCompiledBindingMode.OneWay,
            Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundCompiledBinding>()).Mode);
    }

    [Fact]
    public void WinUiXBindEmitsTypedRoslynSegmentsAndCompiles()
    {
        const string additions = """
namespace Demo {
  public partial class MainPage {
    public ViewModel ViewModel { get; } = new();
  }
  public sealed class ViewModel {
    public string Name { get; set; } = "Ada";
  }
  public sealed class BindingTarget : Microsoft.UI.Xaml.DependencyObject {
    public static readonly Microsoft.UI.Xaml.DependencyProperty ValueProperty = new();
    public string? Value { get; set; }
  }
}
namespace Microsoft.UI.Xaml.Data {
  public interface ICompiledBindingPathSegment { }
  public interface ICompiledBindings { void Initialize(); void Update(); void StopTracking(); }
  public sealed class TestCompiledBindings : ICompiledBindings {
    public void Initialize() { }
    public void Update() { }
    public void StopTracking() { }
  }
  public sealed class CompiledBindingPathSegment<TSource, TValue> : ICompiledBindingPathSegment {
    public CompiledBindingPathSegment(
      string name,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter,
      Microsoft.UI.Xaml.DependencyProperty? property) { }
  }
  public sealed class CompiledBindingOptions {
    public BindingMode Mode { get; set; }
    public UpdateSourceTrigger UpdateSourceTrigger { get; set; }
    public object? Converter { get; set; }
    public object? ConverterParameter { get; set; }
    public string? ConverterLanguage { get; set; }
    public object? FallbackValue { get; set; }
    public object? TargetNullValue { get; set; }
    public System.Action<object, object?>? BindBack { get; set; }
  }
  public static class CompiledBindingOperations {
    public static ICompiledBindings BeginBindings() => new TestCompiledBindings();
    public static ICompiledBindings BeginBindings(object source) => new TestCompiledBindings();
    public static void ClearBindingsForSource(object source) { }
    public static object SetBinding(
      Microsoft.UI.Xaml.DependencyObject target,
      Microsoft.UI.Xaml.DependencyProperty property,
      object source,
      System.Collections.Generic.IReadOnlyList<ICompiledBindingPathSegment> path,
      CompiledBindingOptions options,
      ICompiledBindings? bindings = null) => new object();
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:BindingTarget Value="{x:Bind ViewModel.Name, Mode=TwoWay,
                                      FallbackValue=missing}" />
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText(additions));
        var profile = new WinUiXamlProfile();
        var emitted = new CSharpXamlEmitter().Emit(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new XamlCompilerOptions { ResourceUri = "Pages/XBind.xaml" });

        Assert.DoesNotContain(
            emitted.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var invocation = Assert.Single(
            tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            candidate => candidate.Expression.ToString().EndsWith(
                "CompiledBindingOperations.SetBinding",
                StringComparison.Ordinal));
        Assert.Equal("this", invocation.ArgumentList.Arguments[2].Expression.ToString());
        Assert.Equal(2, invocation.ArgumentList.Arguments[3].Expression
            .DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>()
            .Count(creation => creation.Type.ToString().Contains(
                "CompiledBindingPathSegment",
                StringComparison.Ordinal)));
        Assert.Contains(
            invocation.DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>(),
            lambda => lambda.ToString().Contains("source.ViewModel", StringComparison.Ordinal));
        Assert.Contains(
            invocation.DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>(),
            lambda => lambda.ToString().Contains("source.Name", StringComparison.Ordinal));
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void WinUiXBindBindsAndEmitsIntegerAndStringIndexers()
    {
        const string additions = """
namespace Demo {
  public partial class MainPage {
    public System.Collections.Generic.List<Team> Teams { get; } = new();
  }
  public sealed class Team {
    public System.Collections.Generic.Dictionary<string, Player> Players { get; } = new();
  }
  public sealed class Player {
    public string Name { get; set; } = "Ada";
  }
  public sealed class BindingTarget : Microsoft.UI.Xaml.DependencyObject {
    public static readonly Microsoft.UI.Xaml.DependencyProperty ValueProperty = new();
    public string? Value { get; set; }
  }
}
namespace Microsoft.UI.Xaml.Data {
  public interface ICompiledBindingPathSegment { }
  public interface ICompiledBindings { void Initialize(); void Update(); void StopTracking(); }
  public sealed class TestCompiledBindings : ICompiledBindings {
    public void Initialize() { }
    public void Update() { }
    public void StopTracking() { }
  }
  public sealed class CompiledBindingPathSegment<TSource, TValue> : ICompiledBindingPathSegment {
    public CompiledBindingPathSegment(
      string name,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter,
      Microsoft.UI.Xaml.DependencyProperty? property) { }
  }
  public sealed class CompiledBindingIndexerPathSegment<TSource, TValue> : ICompiledBindingPathSegment {
    public CompiledBindingIndexerPathSegment(
      int index,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter) { }
    public CompiledBindingIndexerPathSegment(
      string key,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter) { }
  }
  public sealed class CompiledBindingOptions {
    public BindingMode Mode { get; set; }
    public UpdateSourceTrigger UpdateSourceTrigger { get; set; }
    public object? Converter { get; set; }
    public object? ConverterParameter { get; set; }
    public string? ConverterLanguage { get; set; }
    public object? FallbackValue { get; set; }
    public object? TargetNullValue { get; set; }
    public System.Action<object, object?>? BindBack { get; set; }
  }
  public static class CompiledBindingOperations {
    public static ICompiledBindings BeginBindings(object source) => new TestCompiledBindings();
    public static void ClearBindingsForSource(object source) { }
    public static object SetBinding(
      Microsoft.UI.Xaml.DependencyObject target,
      Microsoft.UI.Xaml.DependencyProperty property,
      object source,
      System.Collections.Generic.IReadOnlyList<ICompiledBindingPathSegment> path,
      CompiledBindingOptions options) => new object();
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:BindingTarget Value="{x:Bind Teams[0].Players['John Smith'].Name, Mode=TwoWay}" />
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText(additions));
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var binding = Assert.Single(
            DescendantValues(bound.Root).OfType<XamlBoundCompiledBinding>());
        Assert.Equal(
            new[]
            {
                XamlCompiledBindingPathSegmentKind.Member,
                XamlCompiledBindingPathSegmentKind.IntegerIndexer,
                XamlCompiledBindingPathSegmentKind.Member,
                XamlCompiledBindingPathSegmentKind.StringIndexer,
                XamlCompiledBindingPathSegmentKind.Member
            },
            binding.PathSegments.Select(static segment => segment.Kind));
        Assert.Equal(0, binding.PathSegments[1].IntegerIndex);
        Assert.Equal("John Smith", binding.PathSegments[3].StringIndex);
        Assert.True(binding.CanWrite);
        Assert.All(
            binding.PathSegments,
            static segment => Assert.NotNull(segment.Member));

        var emitted = new CSharpXamlEmitter().Emit(
            bound.Infoset,
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new XamlCompilerOptions());
        Assert.DoesNotContain(
            emitted.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var generated = tree.GetRoot().ToFullString();
        Assert.Contains(
            "CompiledBindingIndexerPathSegment<global::System.Collections.Generic.List<global::Demo.Team>, global::Demo.Team>",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "((global::System.Collections.Generic.IList<global::Demo.Team>)source)[0]",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "((global::System.Collections.Generic.IDictionary<string, global::Demo.Player>)source)[\"John Smith\"]",
            generated,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void WinUiXBindRejectsReadOnlyCollectionIndexerContracts()
    {
        const string additions = """
namespace Demo {
  public partial class MainPage {
    public System.Collections.Generic.IReadOnlyList<string> Names { get; } =
      System.Array.Empty<string>();
  }
  public sealed class BindingTarget : Microsoft.UI.Xaml.DependencyObject {
    public static readonly Microsoft.UI.Xaml.DependencyProperty ValueProperty = new();
    public string? Value { get; set; }
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:BindingTarget Value="{x:Bind Names[0]}" />
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText(additions));
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, new WinUiXamlProfile()),
            new XamlSemanticBindingOptions { Strict = false });

        Assert.Contains(bound.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2121");
    }

    [Fact]
    public void WinUiXBindBindsAndEmitsCastsAndAttachedMembers()
    {
        const string additions = """
namespace Demo {
  public partial class MainPage {
    public object Current { get; } = new Derived();
    public Microsoft.UI.Xaml.FrameworkElement Element { get; } = new();
  }
  public sealed class Derived {
    public string Name { get; set; } = "cast";
  }
  public sealed class BindingTarget : Microsoft.UI.Xaml.DependencyObject {
    public static readonly Microsoft.UI.Xaml.DependencyProperty ValueProperty = new();
    public object? Value { get; set; }
  }
}
namespace Microsoft.UI.Xaml.Data {
  public interface ICompiledBindingPathSegment { }
  public interface ICompiledBindings { void Initialize(); void Update(); void StopTracking(); }
  public sealed class TestCompiledBindings : ICompiledBindings {
    public void Initialize() { }
    public void Update() { }
    public void StopTracking() { }
  }
  public sealed class CompiledBindingPathSegment<TSource, TValue> : ICompiledBindingPathSegment {
    public CompiledBindingPathSegment(
      string name,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter,
      Microsoft.UI.Xaml.DependencyProperty? property) { }
  }
  public sealed class CompiledBindingCastPathSegment<TSource, TValue> : ICompiledBindingPathSegment {
    public CompiledBindingCastPathSegment(
      string typeName,
      System.Func<TSource, TValue> getter) { }
  }
  public sealed class CompiledBindingOptions {
    public BindingMode Mode { get; set; }
    public UpdateSourceTrigger UpdateSourceTrigger { get; set; }
    public object? Converter { get; set; }
    public object? ConverterParameter { get; set; }
    public string? ConverterLanguage { get; set; }
    public object? FallbackValue { get; set; }
    public object? TargetNullValue { get; set; }
    public System.Action<object, object?>? BindBack { get; set; }
  }
  public static class CompiledBindingOperations {
    public static ICompiledBindings BeginBindings(object source) => new TestCompiledBindings();
    public static void ClearBindingsForSource(object source) { }
    public static object SetBinding(
      Microsoft.UI.Xaml.DependencyObject target,
      Microsoft.UI.Xaml.DependencyProperty property,
      object source,
      System.Collections.Generic.IReadOnlyList<ICompiledBindingPathSegment> path,
      CompiledBindingOptions options) => new object();
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <StackPanel>
    <local:BindingTarget Value="{x:Bind ((local:Derived)Current).Name}" />
    <local:BindingTarget Value="{x:Bind Element.(Grid.Row), Mode=TwoWay}" />
    <local:BindingTarget Value="{x:Bind Current.(local:Derived.Name)}" />
  </StackPanel>
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText(additions));
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var bindings = DescendantValues(bound.Root)
            .OfType<XamlBoundCompiledBinding>()
            .OrderBy(static binding => binding.Path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3, bindings.Length);
        var csharpCast = Assert.Single(
            bindings,
            binding => binding.Path.StartsWith("((", StringComparison.Ordinal));
        Assert.Equal(
            new[]
            {
                XamlCompiledBindingPathSegmentKind.Member,
                XamlCompiledBindingPathSegmentKind.Cast,
                XamlCompiledBindingPathSegmentKind.Member
            },
            csharpCast.PathSegments.Select(static segment => segment.Kind));
        Assert.Equal(
            "Demo.Derived",
            csharpCast.PathSegments[1].ValueType.ToDisplayString());

        var attached = Assert.Single(
            bindings,
            binding => binding.Path.Contains("Grid.Row", StringComparison.Ordinal));
        Assert.Equal(
            XamlCompiledBindingPathSegmentKind.AttachedMember,
            attached.PathSegments[1].Kind);
        Assert.Equal("GetRow", attached.PathSegments[1].Member.Name);
        Assert.Equal("SetRow", attached.PathSegments[1].SetterMethod?.Name);
        Assert.True(attached.CanWrite);

        var emitted = new CSharpXamlEmitter().Emit(
            bound.Infoset,
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new XamlCompilerOptions());
        Assert.DoesNotContain(
            emitted.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var generated = tree.GetRoot().ToFullString();
        Assert.Contains(
            "CompiledBindingCastPathSegment<object, global::Demo.Derived>",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            tree.GetRoot().DescendantNodes().OfType<CastExpressionSyntax>(),
            cast => string.Equals(
                cast.Type.ToString(),
                "global::Demo.Derived",
                StringComparison.Ordinal));
        Assert.Contains(
            tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "Grid.GetRow",
                StringComparison.Ordinal));
        Assert.Contains(
            tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "Grid.SetRow",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void WinUiXBindRejectsInvalidExplicitCast()
    {
        const string additions = """
namespace Demo {
  public partial class MainPage { public int Number { get; } }
  public sealed class Unrelated { public string Name { get; } = ""; }
  public sealed class BindingTarget : Microsoft.UI.Xaml.DependencyObject {
    public static readonly Microsoft.UI.Xaml.DependencyProperty ValueProperty = new();
    public object? Value { get; set; }
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:BindingTarget Value="{x:Bind ((local:Unrelated)Number).Name}" />
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText(additions));
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, new WinUiXamlProfile()),
            new XamlSemanticBindingOptions { Strict = false });

        Assert.Contains(bound.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2122");
    }

    [Fact]
    public void WinUiXBindBindsAndEmitsTrackedInstanceAndStaticFunctions()
    {
        const string additions = """
namespace Demo {
  public partial class MainPage {
    public System.Collections.Generic.List<Item> Items { get; } = new() { new Item() };
    public string Prefix { get; set; } = "value";
    public string Format(string value, string prefix, int count, bool upper) =>
      value + prefix + count.ToString() + upper.ToString();
  }
  public sealed class Item { public string Title { get; set; } = "item"; }
  public static class Formatter {
    public static string Format(string value, string prefix) => value + prefix;
  }
  public sealed class BindingTarget : Microsoft.UI.Xaml.DependencyObject {
    public static readonly Microsoft.UI.Xaml.DependencyProperty ValueProperty = new();
    public string? Value { get; set; }
  }
}
namespace Microsoft.UI.Xaml.Data {
  public interface ICompiledBindingPathSegment { }
  public interface ICompiledBindings { void Initialize(); void Update(); void StopTracking(); }
  public sealed class TestCompiledBindings : ICompiledBindings {
    public void Initialize() { }
    public void Update() { }
    public void StopTracking() { }
  }
  public sealed class CompiledBindingPathSegment<TSource, TValue> : ICompiledBindingPathSegment {
    public CompiledBindingPathSegment(
      string name,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter,
      Microsoft.UI.Xaml.DependencyProperty? property) { }
  }
  public sealed class CompiledBindingIndexerPathSegment<TSource, TValue> : ICompiledBindingPathSegment {
    public CompiledBindingIndexerPathSegment(
      int index,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter) { }
    public CompiledBindingIndexerPathSegment(
      string key,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter) { }
  }
  public sealed class CompiledBindingFunctionPathSegment<TSource, TValue> : ICompiledBindingPathSegment {
    public CompiledBindingFunctionPathSegment(
      string methodName,
      System.Func<TSource, TValue> getter,
      ICompiledBindingPathSegment[] ownerPath,
      ICompiledBindingPathSegment[][] dependencies) { }
  }
  public sealed class CompiledBindingOptions {
    public BindingMode Mode { get; set; }
    public UpdateSourceTrigger UpdateSourceTrigger { get; set; }
    public object? Converter { get; set; }
    public object? ConverterParameter { get; set; }
    public string? ConverterLanguage { get; set; }
    public object? FallbackValue { get; set; }
    public object? TargetNullValue { get; set; }
    public System.Action<object, object?>? BindBack { get; set; }
  }
  public static class CompiledBindingOperations {
    public static ICompiledBindings BeginBindings(object source) => new TestCompiledBindings();
    public static void ClearBindingsForSource(object source) { }
    public static object SetBinding(
      Microsoft.UI.Xaml.DependencyObject target,
      Microsoft.UI.Xaml.DependencyProperty property,
      object source,
      System.Collections.Generic.IReadOnlyList<ICompiledBindingPathSegment> path,
      CompiledBindingOptions options) => new object();
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <StackPanel>
    <local:BindingTarget Value="{x:Bind Format(Items[0].Title, Prefix, 2, x:True), Mode=OneWay}" />
    <local:BindingTarget Value="{x:Bind local:Formatter.Format(Items[0].Title, ' static'), Mode=OneWay}" />
  </StackPanel>
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(additions));
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var bindings = DescendantValues(bound.Root)
            .OfType<XamlBoundCompiledBinding>()
            .OrderBy(static binding => binding.Path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, bindings.Length);
        var instance = Assert.Single(
            bindings,
            binding => binding.Function?.IsStatic == false);
        Assert.Equal("Format", instance.Function!.Method.Name);
        Assert.Equal(4, instance.Function.Arguments.Length);
        Assert.Equal(
            new[]
            {
                XamlCompiledBindingFunctionArgumentKind.Path,
                XamlCompiledBindingFunctionArgumentKind.Path,
                XamlCompiledBindingFunctionArgumentKind.Number,
                XamlCompiledBindingFunctionArgumentKind.Boolean
            },
            instance.Function.Arguments.Select(static argument => argument.Kind));
        Assert.Equal(3, instance.Function.Arguments[0].PathSegments.Length);
        var @static = Assert.Single(
            bindings,
            binding => binding.Function?.IsStatic == true);
        Assert.Equal("Demo.Formatter", @static.Function!.Method.ContainingType.ToDisplayString());

        var emitted = new CSharpXamlEmitter().Emit(
            bound.Infoset,
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new XamlCompilerOptions());
        Assert.DoesNotContain(
            emitted.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var generated = tree.GetRoot().ToFullString();
        Assert.Contains(
            "CompiledBindingFunctionPathSegment<global::Demo.MainPage, string>",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "Formatter.Format",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void WinUiXBindRejectsMissingOrIncompatibleFunctions()
    {
        const string additions = """
namespace Demo {
  public partial class MainPage {
    public string Name { get; } = "value";
    public string Format(int value) => value.ToString();
  }
  public sealed class BindingTarget : Microsoft.UI.Xaml.DependencyObject {
    public static readonly Microsoft.UI.Xaml.DependencyProperty ValueProperty = new();
    public string? Value { get; set; }
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <StackPanel>
    <local:BindingTarget Value="{x:Bind Missing(Name)}" />
    <local:BindingTarget Value="{x:Bind Format(Name)}" />
  </StackPanel>
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(additions));
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, new WinUiXamlProfile()),
            new XamlSemanticBindingOptions { Strict = false });

        Assert.Equal(
            2,
            bound.Diagnostics.Count(diagnostic =>
                diagnostic.Id == "PGXAML2123"));
    }

    [Fact]
    public void WinUiXBindEventResolvesMethodAndEmitsEventTimePathLambda()
    {
        const string additions = """
namespace Demo {
  public partial class MainPage {
    public System.Collections.Generic.List<EventViewModel> ViewModels { get; } = new();
  }
  public sealed class EventViewModel {
    public void HandleClick(object? sender, System.EventArgs args) { }
  }
}
namespace Microsoft.UI.Xaml.Data {
  public interface ICompiledBindings { void Initialize(); void Update(); void StopTracking(); }
  public sealed class TestCompiledBindings : ICompiledBindings {
    public void Initialize() { }
    public void Update() { }
    public void StopTracking() { }
  }
  public static class CompiledBindingOperations {
    public static ICompiledBindings BeginBindings(object source) => new TestCompiledBindings();
    public static void ClearBindingsForSource(object source) { }
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <Button Click="{x:Bind ViewModels[0].HandleClick}" />
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText(additions));
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var compiled = Assert.Single(
            DescendantValues(bound.Root).OfType<XamlBoundCompiledBinding>());
        Assert.Equal(XamlCompiledBindingKind.Event, compiled.Kind);
        Assert.Equal("HandleClick", compiled.EventHandlerMethod?.Name);
        Assert.Equal(
            new[]
            {
                XamlCompiledBindingPathSegmentKind.Member,
                XamlCompiledBindingPathSegmentKind.IntegerIndexer
            },
            compiled.PathSegments.Select(static segment => segment.Kind));

        var emitted = new CSharpXamlEmitter().Emit(
            bound.Infoset,
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new XamlCompilerOptions());
        Assert.DoesNotContain(
            emitted.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var add = Assert.Single(tree.GetRoot().DescendantNodes()
            .OfType<AssignmentExpressionSyntax>(),
            assignment => assignment.IsKind(SyntaxKind.AddAssignmentExpression));
        var lambda = Assert.IsType<ParenthesizedLambdaExpressionSyntax>(add.Right);
        Assert.Contains(
            "((global::System.Collections.Generic.IList<global::Demo.EventViewModel>)this.ViewModels)[0].HandleClick(__eventArg0, __eventArg1)",
            lambda.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>(),
            property => property.Identifier.ValueText == "Bindings");
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void WinUiXBindEventRejectsOverloadedHandler()
    {
        const string additions = """
namespace Demo {
  public partial class MainPage {
    public EventViewModel ViewModel { get; } = new();
  }
  public sealed class EventViewModel {
    public void HandleClick() { }
    public void HandleClick(object? sender, System.EventArgs args) { }
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <Button Click="{x:Bind ViewModel.HandleClick}" />
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText(additions));
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, new WinUiXamlProfile()),
            new XamlSemanticBindingOptions { Strict = false });

        Assert.Contains(bound.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2118");
    }

    [Fact]
    public void WinUiXBindDiagnosesUserDeclaredBindingsLifecycleMember()
    {
        const string additions = """
namespace Demo {
  public partial class MainPage {
    public object Bindings { get; } = new();
    public void HandleClick() { }
  }
}
namespace Microsoft.UI.Xaml.Data {
  public static class CompiledBindingOperations {
    public static void ClearBindingsForSource(object source) { }
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <Button Click="{x:Bind HandleClick}" />
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(additions));
        var profile = new WinUiXamlProfile();
        var emitted = new CSharpXamlEmitter().Emit(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new XamlCompilerOptions());

        Assert.Contains(
            emitted.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML3045");
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        Assert.DoesNotContain(
            tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>(),
            property => property.Identifier.ValueText == "Bindings");
    }

    [Fact]
    public void WinUiDataTemplateXDataTypeSuppliesTypedCompiledBindingContext()
    {
        const string additions = """
namespace Microsoft.UI.Xaml {
  public class DataTemplate : FrameworkTemplate { }
}
namespace Demo {
  public sealed class Item { public string Title { get; set; } = "Item"; }
  public sealed class BindingTarget : Microsoft.UI.Xaml.FrameworkElement {
    public static readonly Microsoft.UI.Xaml.DependencyProperty ValueProperty = new();
    public string? Value { get; set; }
  }
}
namespace Microsoft.UI.Xaml.Data {
  public interface ICompiledBindingPathSegment { }
  public interface ICompiledBindings { void Initialize(); void Update(); void StopTracking(); }
  public sealed class TestCompiledBindings : ICompiledBindings {
    public void Initialize() { }
    public void Update() { }
    public void StopTracking() { }
  }
  public sealed class CompiledBindingPathSegment<TSource, TValue> : ICompiledBindingPathSegment {
    public CompiledBindingPathSegment(
      string name,
      System.Func<TSource, TValue> getter,
      System.Action<TSource, TValue>? setter,
      Microsoft.UI.Xaml.DependencyProperty? property) { }
  }
  public sealed class CompiledBindingOptions {
    public BindingMode Mode { get; set; }
    public UpdateSourceTrigger UpdateSourceTrigger { get; set; }
    public object? Converter { get; set; }
    public object? ConverterParameter { get; set; }
    public string? ConverterLanguage { get; set; }
    public object? FallbackValue { get; set; }
    public object? TargetNullValue { get; set; }
    public System.Action<object, object?>? BindBack { get; set; }
  }
  public static class CompiledBindingOperations {
    public static ICompiledBindings BeginBindings() => new TestCompiledBindings();
    public static ICompiledBindings BeginBindings(object source) => new TestCompiledBindings();
    public static void ClearBindingsForSource(object source) { }
    public static object SetBinding(
      Microsoft.UI.Xaml.DependencyObject target,
      Microsoft.UI.Xaml.DependencyProperty property,
      object source,
      System.Collections.Generic.IReadOnlyList<ICompiledBindingPathSegment> path,
      CompiledBindingOptions options,
      ICompiledBindings? bindings = null) => new object();
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <DataTemplate x:DataType="local:Item">
    <local:BindingTarget Value="{x:Bind Title, Mode=OneWay}" />
  </DataTemplate>
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText(additions));
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var compiled = Assert.Single(
            DescendantValues(bound.Root).OfType<XamlBoundCompiledBinding>());
        Assert.Equal("Demo.Item", compiled.SourceType.MetadataName);
        Assert.Equal(XamlCompiledBindingSourceKind.Context, compiled.SourceKind);
        Assert.Equal("Title", Assert.Single(compiled.PathSegments).Member.Name);

        var emitted = new CSharpXamlEmitter().Emit(
            bound.Infoset,
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new XamlCompilerOptions());
        Assert.DoesNotContain(
            emitted.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var setBinding = Assert.Single(
            tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "CompiledBindingOperations.SetBinding",
                StringComparison.Ordinal));
        Assert.Equal(
            "__templateContext!",
            setBinding.ArgumentList.Arguments[2].Expression.ToString());
        Assert.Equal(6, setBinding.ArgumentList.Arguments.Count);
        Assert.Equal(
            "__templateBindings",
            setBinding.ArgumentList.Arguments[5].Expression.ToString());
        Assert.Contains(
            "CompiledBindingPathSegment<global::Demo.Item, string>",
            setBinding.ToString(),
            StringComparison.Ordinal);
        var begin = Assert.Single(
            tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "CompiledBindingOperations.BeginBindings",
                StringComparison.Ordinal));
        Assert.Empty(begin.ArgumentList.Arguments);
        var attach = Assert.Single(
            tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "XamlTemplateFactory.AttachBindings",
                StringComparison.Ordinal));
        Assert.Equal(
            "__templateBindings",
            attach.ArgumentList.Arguments[1].Expression.ToString());
        Assert.True(begin.SpanStart < setBinding.SpanStart);
        Assert.True(setBinding.SpanStart < attach.SpanStart);
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var unsupported = new CSharpXamlEmitter().Emit(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile),
            new MissingDeferredBindingLifecycleProfile(),
            new XamlCompilerOptions());
        Assert.Contains(
            unsupported.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML3046");
    }

    [Fact]
    public void WinUiEventOnlyDataTemplateStillOwnsOneMaterializationLifecycle()
    {
        const string additions = """
namespace Microsoft.UI.Xaml {
  public class DataTemplate : FrameworkTemplate { }
}
namespace Demo {
  public sealed class EventItem {
    public void Activate(object? sender, System.EventArgs args) { }
  }
}
namespace Microsoft.UI.Xaml.Data {
  public interface ICompiledBindings {
    void Initialize();
    void Update();
    void StopTracking();
  }
  public sealed class TestCompiledBindings : ICompiledBindings {
    public void Initialize() { }
    public void Update() { }
    public void StopTracking() { }
  }
  public static class CompiledBindingOperations {
    public static ICompiledBindings BeginBindings() =>
      new TestCompiledBindings();
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <DataTemplate x:DataType="local:EventItem">
    <Button Click="{x:Bind Activate}" />
  </DataTemplate>
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(additions));
        var profile = new WinUiXamlProfile();
        var emitted = new CSharpXamlEmitter().Emit(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new XamlCompilerOptions());

        Assert.DoesNotContain(
            emitted.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var factory = Assert.Single(
            tree.GetRoot().DescendantNodes()
                .OfType<ParenthesizedLambdaExpressionSyntax>(),
            lambda => lambda.ParameterList.Parameters.Count == 1 &&
                lambda.ParameterList.Parameters[0].Identifier.ValueText ==
                    "__templateContext");
        Assert.Contains(
            factory.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "CompiledBindingOperations.BeginBindings",
                StringComparison.Ordinal));
        Assert.Contains(
            factory.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "XamlTemplateFactory.AttachBindings",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            factory.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith(
                "CompiledBindingOperations.SetBinding",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ClrMarkupExtensionShapeAndDeclaredReturnTypeAreSymbolValidated()
    {
        const string compatible = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <TextBlock Text="{local:String}" />
</Page>
""";
        var typeSystem = new RoslynXamlTypeSystem(CreateCompilation(), new WinUiXamlProfile());
        var valid = new XamlSemanticBinder().Bind(Convert(compatible), typeSystem);
        Assert.False(valid.HasErrors, string.Join(Environment.NewLine, valid.Diagnostics));
        var extension = Assert.Single(DescendantValues(valid.Root).OfType<XamlBoundObject>(),
            value => value.Type.RequestedName.LocalName == "String");
        Assert.True(extension.Type.Symbol!.IsMarkupExtension);
        Assert.Equal(SpecialType.System_String, extension.Type.Symbol.ReturnValueType!.SpecialType);
        var shape = Assert.IsType<ProGPU.Xaml.Schema.XamlMarkupExtensionShapeInfo>(
            extension.Type.Symbol.MarkupExtensionShape);
        Assert.True(shape.IsValid);
        Assert.Equal(ProGPU.Xaml.Schema.XamlMarkupExtensionIdentityKind.BaseType, shape.IdentityKind);
        Assert.Equal("Microsoft.UI.Xaml.Markup.MarkupExtension", shape.Identity);
        Assert.Equal("ProGPU.WinUI", shape.ProviderId);
        Assert.Equal("ProvideValue", shape.ProvideValueMethod!.Name);
        Assert.Equal("Microsoft.UI.Xaml.IXamlServiceProvider", shape.ServiceProviderType!.ToDisplayString());
        Assert.Contains(extension.Type.Symbol.Annotations, annotation =>
            annotation.Semantic == ProGPU.Xaml.Schema.XamlSchemaSemantics.MarkupExtensionReturnType &&
            annotation.ValueConstant?.Value is ITypeSymbol type && type.SpecialType == SpecialType.System_String);

        var emitted = new CSharpXamlEmitter().Emit(
            Convert(compatible),
            typeSystem,
            new WinUiXamlProfile(),
            new ProGPU.Xaml.Schema.XamlCompilerOptions { ResourceUri = "Pages/Binding.xaml" });
        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generatedTree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var generatedText = generatedTree.GetText().ToString();
        Assert.Contains("new global::Demo.StringExtension()", generatedText, StringComparison.Ordinal);
        var evaluation = Assert.Single(generatedTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(), invocation =>
            invocation.Expression.ToString().Contains("WinUiMarkupExtensionRuntime.Evaluate", StringComparison.Ordinal));
        var genericEvaluation = Assert.IsType<MemberAccessExpressionSyntax>(evaluation.Expression).Name;
        var genericName = Assert.IsType<GenericNameSyntax>(genericEvaluation);
        Assert.Equal("Evaluate", genericName.Identifier.ValueText);
        var resultType = Assert.IsType<NullableTypeSyntax>(Assert.Single(genericName.TypeArgumentList.Arguments));
        Assert.Equal(SyntaxKind.StringKeyword, Assert.IsType<PredefinedTypeSyntax>(resultType.ElementType).Keyword.Kind());
        Assert.Equal(
            "\"Pages/Binding.xaml\"",
            evaluation.ArgumentList.Arguments[^1].Expression.ToString());
        Assert.DoesNotContain(CreateCompilation().AddSyntaxTrees(generatedTree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var incompatible = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><TextBlock Text=\"{local:Integer}\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(incompatible.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2047");

        var invalidShape = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><TextBlock Text=\"{local:Fake}\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(invalidShape.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2046");

        var ambiguousTypeSystem = new RoslynXamlTypeSystem(
            CreateCompilation(),
            new WinUiXamlProfile(),
            new AmbiguousMarkupShapeProvider());
        var ambiguous = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><TextBlock Text=\"{local:Ambiguous}\" /></Page>"),
            ambiguousTypeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        var ambiguity = Assert.Single(ambiguous.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2046");
        Assert.Contains("ambiguous", ambiguity.GetMessage(), StringComparison.OrdinalIgnoreCase);
        var ambiguousType = Assert.IsType<ProGPU.Xaml.Schema.XamlTypeInfo>(
            ambiguousTypeSystem.ResolveType("using:Demo", "Ambiguous"));
        var ambiguousShape = Assert.IsType<ProGPU.Xaml.Schema.XamlMarkupExtensionShapeInfo>(
            ambiguousType.MarkupExtensionShape);
        Assert.False(ambiguousShape.IsValid);
        Assert.Equal(ProGPU.Xaml.Schema.XamlMarkupExtensionIdentityKind.Suffix, ambiguousShape.IdentityKind);
        Assert.Equal(2, ambiguousShape.Candidates.Count);
        Assert.Equal("tests.markup-ambiguity", ambiguousShape.ProviderId);

        var wrongService = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><TextBlock Text=\"{local:WrongService}\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        var wrongServiceDiagnostic = Assert.Single(
            wrongService.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2046");
        Assert.Contains("No accessible instance method", wrongServiceDiagnostic.GetMessage(), StringComparison.Ordinal);
        var wrongServiceType = Assert.IsType<ProGPU.Xaml.Schema.XamlTypeInfo>(
            typeSystem.ResolveType("using:Demo", "WrongService"));
        Assert.Empty(wrongServiceType.MarkupExtensionShape!.Candidates);

        var interfaceTypeSystem = new RoslynXamlTypeSystem(
            CreateCompilation(),
            new WinUiXamlProfile(),
            new InterfaceMarkupShapeProvider());
        var interfaceDocument = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><TextBlock Text=\"{local:InterfaceValue}\" /></Page>"),
            interfaceTypeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(interfaceDocument.HasErrors, string.Join(Environment.NewLine, interfaceDocument.Diagnostics));
        var interfaceType = Assert.IsType<ProGPU.Xaml.Schema.XamlTypeInfo>(
            interfaceTypeSystem.ResolveType("using:Demo", "InterfaceValue"));
        var interfaceShape = Assert.IsType<ProGPU.Xaml.Schema.XamlMarkupExtensionShapeInfo>(
            interfaceType.MarkupExtensionShape);
        Assert.True(interfaceShape.IsValid);
        Assert.Equal(ProGPU.Xaml.Schema.XamlMarkupExtensionIdentityKind.Interface, interfaceShape.IdentityKind);
        Assert.Equal("Demo.IMarkupExtension`1", interfaceShape.Identity);
        Assert.Equal(SpecialType.System_String, interfaceShape.ProvideValueMethod!.ReturnType.SpecialType);
        Assert.Equal(SpecialType.System_String, interfaceType.ReturnValueType!.SpecialType);
    }

    [Fact]
    public void MauiMarkupExtensionServiceAnnotationsAreCanonicalShapeEvidence()
    {
        var typeSystem = new RoslynXamlTypeSystem(
            CreateCompilation(),
            new WinUiXamlProfile(),
            new MauiMarkupShapeProvider());

        var required = Assert.IsType<ProGPU.Xaml.Schema.XamlTypeInfo>(
            typeSystem.ResolveType("using:Demo", "RequiredMaui"));
        var requiredShape = Assert.IsType<ProGPU.Xaml.Schema.XamlMarkupExtensionShapeInfo>(
            required.MarkupExtensionShape);
        Assert.True(requiredShape.IsValid, requiredShape.Error);
        Assert.Equal(ProGPU.Xaml.Schema.XamlMarkupExtensionIdentityKind.Interface, requiredShape.IdentityKind);
        Assert.Equal("Microsoft.Maui.Controls.Xaml.IMarkupExtension`1", requiredShape.Identity);
        Assert.Equal("System.IServiceProvider", requiredShape.ServiceProviderType!.ToDisplayString());
        Assert.Equal("Demo.IAvailableService",
            Assert.Single(requiredShape.RequiredServices).ToDisplayString());
        Assert.False(requiredShape.AcceptsEmptyServiceProvider);
        var requiredAnnotation = Assert.Single(required.Annotations, annotation =>
            annotation.Semantic == ProGPU.Xaml.Schema.XamlSchemaSemantics.RequireService);
        Assert.False(requiredAnnotation.AllowMultiple);
        Assert.Equal(TypedConstantKind.Array, requiredAnnotation.ValueConstant!.Value.Kind);

        var missing = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><TextBlock Text=\"{local:MissingServiceMaui}\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        var missingDiagnostic = Assert.Single(
            missing.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2046");
        Assert.Contains("Demo.IMissingService", missingDiagnostic.GetMessage(), StringComparison.Ordinal);
        var missingShape = typeSystem.ResolveType("using:Demo", "MissingServiceMaui")!.MarkupExtensionShape!;
        Assert.False(missingShape.IsValid);
        Assert.Null(missingShape.ProvideValueMethod);
        Assert.Equal("Demo.IMissingService",
            Assert.Single(missingShape.RequiredServices).ToDisplayString());

        var empty = typeSystem.ResolveType("using:Demo", "EmptyProviderMaui")!.MarkupExtensionShape!;
        Assert.True(empty.IsValid, empty.Error);
        Assert.True(empty.AcceptsEmptyServiceProvider);
        Assert.Empty(empty.RequiredServices);

        var conflicting = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><TextBlock Text=\"{local:ConflictingServiceMaui}\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        var conflictingDiagnostic = Assert.Single(
            conflicting.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2046");
        Assert.Contains("both require services and accept an empty service provider",
            conflictingDiagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);

        var undeclared = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><TextBlock Text=\"{local:UndeclaredServiceMaui}\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        var undeclaredDiagnostic = Assert.Single(
            undeclared.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2046");
        Assert.Contains("must declare either required services or acceptance",
            undeclaredDiagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndependentSymbolShapeProvidersComposeWithoutReplacingFrameworkSemantics()
    {
        var typeSystem = new RoslynXamlTypeSystem(
            CreateCompilation(),
            new WinUiXamlProfile(),
            new MauiMarkupShapeProvider(),
            new ObjectWriterHandlerMetadataProvider());

        var winUiExtension = Assert.IsType<ProGPU.Xaml.Schema.XamlTypeInfo>(
            typeSystem.ResolveType("using:Demo", "String"));
        var winUiShape = Assert.IsType<ProGPU.Xaml.Schema.XamlMarkupExtensionShapeInfo>(
            winUiExtension.MarkupExtensionShape);
        Assert.True(winUiShape.IsValid, winUiShape.Error);
        Assert.Equal(ProGPU.Xaml.Schema.XamlMarkupExtensionIdentityKind.BaseType, winUiShape.IdentityKind);
        Assert.Equal("Microsoft.UI.Xaml.Markup.MarkupExtension", winUiShape.Identity);
        Assert.Equal("ProGPU.WinUI", winUiShape.ProviderId);
        Assert.Equal("Microsoft.UI.Xaml.IXamlServiceProvider",
            winUiShape.ServiceProviderType!.ToDisplayString());

        var mauiExtension = Assert.IsType<ProGPU.Xaml.Schema.XamlTypeInfo>(
            typeSystem.ResolveType("using:Demo", "RequiredMaui"));
        var mauiShape = Assert.IsType<ProGPU.Xaml.Schema.XamlMarkupExtensionShapeInfo>(
            mauiExtension.MarkupExtensionShape);
        Assert.True(mauiShape.IsValid, mauiShape.Error);
        Assert.Equal(ProGPU.Xaml.Schema.XamlMarkupExtensionIdentityKind.Interface, mauiShape.IdentityKind);
        Assert.Equal("tests.markup-maui", mauiShape.ProviderId);
        Assert.Equal("System.IServiceProvider", mauiShape.ServiceProviderType!.ToDisplayString());
        Assert.Equal("Demo.IAvailableService",
            Assert.Single(mauiShape.RequiredServices).ToDisplayString());

        var intercepted = Assert.IsType<ProGPU.Xaml.Schema.XamlTypeInfo>(
            typeSystem.ResolveType("using:Demo", "InterceptingControl"));
        var handler = Assert.IsType<ProGPU.Xaml.Schema.XamlSetValueHandlerShapeInfo>(
            intercepted.MarkupExtensionSetHandler);
        Assert.True(handler.IsValid, handler.Error);
        Assert.Equal("tests.object-writer-handlers", handler.ProviderId);

        var page = Assert.IsType<ProGPU.Xaml.Schema.XamlTypeInfo>(
            typeSystem.ResolveType(
                "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
                "Page"));
        Assert.Equal("Content", page.ContentMemberName);
        Assert.Equal("Name", page.RuntimeNameMemberName);

        const string attachedCollection = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <StackPanel>
    <VisualStateManager.VisualStateGroups>
      <VisualStateGroup />
    </VisualStateManager.VisualStateGroups>
  </StackPanel>
</Page>
""";
        var bound = new XamlSemanticBinder().Bind(
            Convert(attachedCollection),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var attached = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundObject>()
            .SelectMany(value => value.Members),
            member => member.Member.Symbol?.Name == "VisualStateGroups");
        Assert.Equal("ProGPU.WinUI", attached.Member.Symbol!.AttachableShape!.ProviderId);
    }

    [Fact]
    public void EqualPriorityMarkupExtensionIdentityProvidersAreDiagnosed()
    {
        var typeSystem = new RoslynXamlTypeSystem(
            CreateCompilation(),
            new WinUiXamlProfile(),
            new ConflictingWinUiMarkupShapeProvider());
        var bound = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><TextBlock Text=\"{local:String}\" /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });

        var diagnostic = Assert.Single(
            bound.Diagnostics,
            candidate => candidate.Id == "PGXAML2046");
        Assert.Contains("equal-priority providers", diagnostic.GetMessage(), StringComparison.Ordinal);
        var extension = Assert.IsType<ProGPU.Xaml.Schema.XamlTypeInfo>(
            typeSystem.ResolveType("using:Demo", "String"));
        var shape = Assert.IsType<ProGPU.Xaml.Schema.XamlMarkupExtensionShapeInfo>(
            extension.MarkupExtensionShape);
        Assert.False(shape.IsValid);
        Assert.Contains("ProGPU.WinUI", shape.ProviderId, StringComparison.Ordinal);
        Assert.Contains("tests.winui-markup-conflict", shape.ProviderId, StringComparison.Ordinal);
    }

    [Fact]
    public void EquivalentEqualPriorityMarkupExtensionProvidersCoalesceWithProvenance()
    {
        var typeSystem = new RoslynXamlTypeSystem(
            CreateCompilation(),
            new WinUiXamlProfile(),
            new EquivalentMarkupShapeProvider("tests.equivalent-a"),
            new EquivalentMarkupShapeProvider("tests.equivalent-b"));

        var extension = Assert.IsType<ProGPU.Xaml.Schema.XamlTypeInfo>(
            typeSystem.ResolveType("using:Demo", "WrongService"));
        var shape = Assert.IsType<ProGPU.Xaml.Schema.XamlMarkupExtensionShapeInfo>(
            extension.MarkupExtensionShape);
        Assert.True(shape.IsValid, shape.Error);
        Assert.Equal(ProGPU.Xaml.Schema.XamlMarkupExtensionIdentityKind.Suffix, shape.IdentityKind);
        Assert.Equal(SpecialType.System_Int32, shape.ServiceProviderType!.SpecialType);
        Assert.Equal("tests.equivalent-a, tests.equivalent-b", shape.ProviderId);
    }

    [Fact]
    public void ScalarAndKeyedShapePolicyConflictsAreCanonicalDiagnostics()
    {
        var typeSystem = new RoslynXamlTypeSystem(
            CreateCompilation(),
            new WinUiXamlProfile(),
            new ConflictingCompositionProvider(
                "tests.composition-a",
                runtimeNameFallback: "FirstName",
                implicitStyleKeyMember: "TargetType"),
            new ConflictingCompositionProvider(
                "tests.composition-b",
                runtimeNameFallback: "SecondName",
                implicitStyleKeyMember: "OtherKey"));

        var conflictProvider = Assert.IsAssignableFrom<
            ProGPU.Xaml.Schema.IXamlSymbolShapeConflictProvider>(typeSystem);
        var conflicts = conflictProvider.SymbolShapeConflicts;
        var runtimeName = Assert.Single(conflicts, conflict =>
            conflict.Feature == ProGPU.Xaml.Schema.XamlSymbolShapeFeatures.RuntimeNameFallback);
        Assert.Null(runtimeName.Key);
        Assert.Equal(
            new[] { "tests.composition-a", "tests.composition-b" },
            runtimeName.Candidates.Select(candidate => candidate.ProviderId));
        var implicitKey = Assert.Single(conflicts, conflict =>
            conflict.Feature == ProGPU.Xaml.Schema.XamlSymbolShapeFeatures.ImplicitDictionaryKeys);
        Assert.Equal("Microsoft.UI.Xaml.Style", implicitKey.Key);
        Assert.Equal(
            new[] { "TargetType", "OtherKey" },
            implicitKey.Candidates.Select(candidate => candidate.Value));

        var bound = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        var diagnostics = bound.Diagnostics.Where(diagnostic =>
            diagnostic.Id == "PGXAML2049").ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
            Assert.Equal(
                Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />")
                    .Root!.SourceSpan,
                diagnostic.Location.SourceSpan));
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.GetMessage().Contains("RuntimeNameFallback", StringComparison.Ordinal) &&
            diagnostic.GetMessage().Contains("tests.composition-a", StringComparison.Ordinal) &&
            diagnostic.GetMessage().Contains("tests.composition-b", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.GetMessage().Contains(
                "ImplicitDictionaryKeys key 'Microsoft.UI.Xaml.Style'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void EveryGlobalScalarAndKeyedShapeFamilyDetectsWinningConflicts()
    {
        var typeSystem = new RoslynXamlTypeSystem(
            CreateCompilation(),
            new WinUiXamlProfile(),
            new AllConflictingShapeFamiliesProvider("tests.all-shapes-a", first: true),
            new AllConflictingShapeFamiliesProvider("tests.all-shapes-b", first: false));

        var conflicts = Assert.IsAssignableFrom<
            ProGPU.Xaml.Schema.IXamlSymbolShapeConflictProvider>(typeSystem).SymbolShapeConflicts;
        Assert.Equal(8, conflicts.Count);
        Assert.Equal(
            new[]
            {
                ProGPU.Xaml.Schema.XamlSymbolShapeFeatures.AttachedAccessorPrefixes,
                ProGPU.Xaml.Schema.XamlSymbolShapeFeatures.RuntimeNameFallback,
                ProGPU.Xaml.Schema.XamlSymbolShapeFeatures.CollectionInference,
                ProGPU.Xaml.Schema.XamlSymbolShapeFeatures.PropertySystem,
                ProGPU.Xaml.Schema.XamlSymbolShapeFeatures.GetterOnlyAttachedCollections,
                ProGPU.Xaml.Schema.XamlSymbolShapeFeatures.ImplicitDictionaryKeys,
                ProGPU.Xaml.Schema.XamlSymbolShapeFeatures.ResourceMemberRoles,
                ProGPU.Xaml.Schema.XamlSymbolShapeFeatures.PseudoContentMembers
            }.OrderBy(feature => feature),
            conflicts.Select(conflict => conflict.Feature).OrderBy(feature => feature));
        Assert.All(conflicts, conflict =>
        {
            Assert.Equal(2, conflict.Candidates.Count);
            Assert.All(conflict.Candidates, candidate => Assert.Equal(2000, candidate.ProviderPriority));
        });
    }

    [Fact]
    public void EquivalentOrLowerPriorityShapePoliciesDoNotConflict()
    {
        var typeSystem = new RoslynXamlTypeSystem(
            CreateCompilation(),
            new WinUiXamlProfile(),
            new ConflictingCompositionProvider(
                "tests.composition-a",
                runtimeNameFallback: "SharedName",
                implicitStyleKeyMember: "TargetType",
                priority: 2000),
            new ConflictingCompositionProvider(
                "tests.composition-b",
                runtimeNameFallback: "SharedName",
                implicitStyleKeyMember: "TargetType",
                priority: 2000),
            new ConflictingCompositionProvider(
                "tests.composition-lower",
                runtimeNameFallback: "IgnoredName",
                implicitStyleKeyMember: "OtherKey",
                priority: 1000));

        var conflicts = Assert.IsAssignableFrom<
            ProGPU.Xaml.Schema.IXamlSymbolShapeConflictProvider>(typeSystem).SymbolShapeConflicts;
        Assert.Empty(conflicts);
        var page = typeSystem.ResolveType(
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
            "Page")!;
        Assert.Equal("SharedName", page.RuntimeNameMemberName);
        var style = typeSystem.ResolveType(
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
            "Style")!;
        Assert.Equal("TargetType", style.DictionaryKeyMemberName);
    }

    [Fact]
    public void ObjectWriterSetHandlerAnnotationsResolveExactRoslynCallbacks()
    {
        var typeSystem = new RoslynXamlTypeSystem(
            CreateCompilation(),
            new WinUiXamlProfile(),
            new UnrelatedSetHandlerShapeProvider(),
            new ObjectWriterHandlerMetadataProvider());

        var type = typeSystem.ResolveType("using:Demo", "InterceptingControl")!;
        var markup = Assert.IsType<ProGPU.Xaml.Schema.XamlSetValueHandlerShapeInfo>(
            type.MarkupExtensionSetHandler);
        Assert.True(markup.IsValid, markup.Error);
        Assert.Equal(ProGPU.Xaml.Schema.XamlSchemaSemantics.SetMarkupExtensionHandler, markup.Semantic);
        Assert.Equal("ReceiveMarkupExtension", markup.HandlerName);
        Assert.Equal("System.Windows.Markup.XamlSetMarkupExtensionEventArgs",
            markup.EventArgsType!.ToDisplayString());
        Assert.Equal("Demo.InterceptingControl.ReceiveMarkupExtension(object, System.Windows.Markup.XamlSetMarkupExtensionEventArgs)",
            markup.Handler!.ToDisplayString());
        Assert.True(markup.Handler.IsStatic);
        Assert.True(markup.Handler.ReturnsVoid);
        Assert.True(markup.IsDirectlyAccessible);
        Assert.False(markup.RequiresAccessBridge);
        Assert.Equal("tests.object-writer-handlers", markup.ProviderId);
        Assert.Equal("tests.object-writer-handlers", markup.Annotation.ProviderId);
        Assert.Single(markup.Candidates);

        var converter = Assert.IsType<ProGPU.Xaml.Schema.XamlSetValueHandlerShapeInfo>(
            type.TypeConverterSetHandler);
        Assert.True(converter.IsValid, converter.Error);
        Assert.Equal(Accessibility.Internal, converter.Handler!.DeclaredAccessibility);
        Assert.Equal("System.Windows.Markup.XamlSetTypeConverterEventArgs",
            converter.EventArgsType!.ToDisplayString());

        var inherited = typeSystem.ResolveType("using:Demo", "DerivedInterceptingControl")!;
        var inheritedMarkup = Assert.IsType<ProGPU.Xaml.Schema.XamlSetValueHandlerShapeInfo>(
            inherited.MarkupExtensionSetHandler);
        Assert.True(inheritedMarkup.IsValid, inheritedMarkup.Error);
        Assert.Equal("Demo.InterceptingControl", inheritedMarkup.Handler!.ContainingType.ToDisplayString());
        Assert.True(inheritedMarkup.Annotation.IsInherited);

        var privateType = typeSystem.ResolveType("using:Demo", "PrivateInterceptingControl")!;
        var privateMarkup = Assert.IsType<ProGPU.Xaml.Schema.XamlSetValueHandlerShapeInfo>(
            privateType.MarkupExtensionSetHandler);
        Assert.True(privateMarkup.IsValid, privateMarkup.Error);
        Assert.Equal(Accessibility.Private, privateMarkup.Handler!.DeclaredAccessibility);
        Assert.False(privateMarkup.IsDirectlyAccessible);
        Assert.True(privateMarkup.RequiresAccessBridge);

        var invalid = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><local:InvalidInterceptingControl /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        var invalidDiagnostic = Assert.Single(invalid.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2048");
        Assert.Contains("static void callback", invalidDiagnostic.GetMessage(), StringComparison.Ordinal);
        var invalidShape = typeSystem.ResolveType("using:Demo", "InvalidInterceptingControl")!.MarkupExtensionSetHandler!;
        Assert.False(invalidShape.IsValid);
        Assert.Single(invalidShape.Candidates);
        Assert.False(invalidShape.Candidates[0].IsStatic);

        var missing = new XamlSemanticBinder().Bind(
            Convert("<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"using:Demo\"><local:MissingInterceptingControl /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(missing.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML2048" &&
            diagnostic.GetMessage().Contains("MissingHandler", StringComparison.Ordinal));
    }

    [Fact]
    public void ObjectWriterSetHandlersLowerThroughProfileOwnedStructuredRoslynCalls()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:InterceptingControl Value="{local:HandlerValue}" Length="42" />
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new ObjectWriterEmissionProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new ObjectWriterHandlerMetadataProvider(),
            new WinUiXamlProfile());
        var emitted = new CSharpXamlEmitter().Emit(
            Convert(xaml),
            typeSystem,
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions
            {
                ResourceUri = "Pages/ObjectWriter.xaml"
            });

        Assert.DoesNotContain(emitted.Diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var invocations = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        var markup = Assert.Single(invocations, invocation =>
            invocation.Expression.ToString().EndsWith(
                "ObjectWriterRuntime.ApplyMarkupExtension",
                StringComparison.Ordinal));
        Assert.Equal(5, markup.ArgumentList.Arguments.Count);
        Assert.Contains("InterceptingControl.ReceiveMarkupExtension",
            markup.ArgumentList.Arguments[2].Expression.ToString(), StringComparison.Ordinal);
        Assert.Equal("\"Value\"", markup.ArgumentList.Arguments[3].Expression.ToString());
        Assert.Equal("\"Pages/ObjectWriter.xaml\"", markup.ArgumentList.Arguments[4].Expression.ToString());
        var converter = Assert.Single(invocations, invocation =>
            invocation.Expression.ToString().EndsWith(
                "ObjectWriterRuntime.ApplyTypeConverter",
                StringComparison.Ordinal));
        Assert.Contains("typeof(global::Demo.LengthConverter)",
            converter.ArgumentList.Arguments[1].Expression.ToString(), StringComparison.Ordinal);
        Assert.Equal("\"42\"", converter.ArgumentList.Arguments[2].Expression.ToString());
        Assert.Contains("InterceptingControl.ReceiveTypeConverter",
            converter.ArgumentList.Arguments[3].Expression.ToString(), StringComparison.Ordinal);
        Assert.Equal("\"Length\"", converter.ArgumentList.Arguments[4].Expression.ToString());
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var unsupportedProfile = new WinUiXamlProfile();
        var unsupported = new CSharpXamlEmitter().Emit(
            Convert(xaml),
            new RoslynXamlTypeSystem(
                compilation,
                unsupportedProfile,
                new ObjectWriterHandlerMetadataProvider()),
            unsupportedProfile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        Assert.Contains(unsupported.Diagnostics, diagnostic => diagnostic.Id == "PGXAML3040");
        Assert.Contains(unsupported.Diagnostics, diagnostic => diagnostic.Id == "PGXAML3041");
    }

    [Fact]
    public void MarkupExtensionReceiversResolveExactInterfaceAndDuckSymbols()
    {
        var compilation = CreateCompilation();
        var profile = new ObjectWriterEmissionProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new MarkupExtensionReceiverMetadataProvider(),
            new ObjectWriterHandlerMetadataProvider(),
            new WinUiXamlProfile());

        var legacy = typeSystem.ResolveType("using:Demo", "LegacyReceiverControl")!;
        var receiver = Assert.IsType<ProGPU.Xaml.Schema.XamlMarkupExtensionReceiverShapeInfo>(
            legacy.MarkupExtensionReceiver);
        Assert.True(receiver.IsValid, receiver.Error);
        Assert.Equal(
            ProGPU.Xaml.Schema.XamlMarkupExtensionReceiverIdentityKind.Interface,
            receiver.IdentityKind);
        Assert.Equal(
            "System.Windows.Markup.IReceiveMarkupExtension",
            receiver.IdentityType!.ToDisplayString());
        Assert.Equal("ReceiveMarkupExtension", receiver.ReceiveMethod!.Name);
        Assert.Equal(
            "System.Windows.Markup.MarkupExtension",
            receiver.MarkupExtensionType!.ToDisplayString());
        Assert.Equal("System.IServiceProvider", receiver.ServiceProviderType!.ToDisplayString());
        Assert.Equal("tests.markup-extension-receivers", receiver.ProviderId);
        Assert.Single(receiver.Candidates);

        var inherited = typeSystem.ResolveType("using:Demo", "DerivedLegacyReceiverControl")!;
        var inheritedReceiver =
            Assert.IsType<ProGPU.Xaml.Schema.XamlMarkupExtensionReceiverShapeInfo>(
                inherited.MarkupExtensionReceiver);
        Assert.True(inheritedReceiver.IsValid, inheritedReceiver.Error);
        Assert.Equal(
            ProGPU.Xaml.Schema.XamlMarkupExtensionReceiverIdentityKind.Interface,
            inheritedReceiver.IdentityKind);

        var duck = typeSystem.ResolveType("using:Demo", "DuckReceiverControl")!;
        var duckReceiver =
            Assert.IsType<ProGPU.Xaml.Schema.XamlMarkupExtensionReceiverShapeInfo>(
                duck.MarkupExtensionReceiver);
        Assert.True(duckReceiver.IsValid, duckReceiver.Error);
        Assert.Equal(
            ProGPU.Xaml.Schema.XamlMarkupExtensionReceiverIdentityKind.DuckMethod,
            duckReceiver.IdentityKind);
        Assert.Equal("AcceptMarkup", duckReceiver.ReceiveMethod!.Name);
        Assert.Equal("Demo.DuckReceiverControl", duckReceiver.IdentityType!.ToDisplayString());

        var attributed = typeSystem.ResolveType("using:Demo", "AttributedReceiverControl")!;
        Assert.True(attributed.MarkupExtensionSetHandler!.IsValid);
        Assert.True(attributed.MarkupExtensionReceiver!.IsValid);

        var invalid = new XamlSemanticBinder().Bind(
            Convert(
                "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:local=\"using:Demo\"><local:InvalidDuckReceiverControl /></Page>"),
            typeSystem,
            new XamlSemanticBindingOptions { Strict = false });
        var invalidDiagnostic = Assert.Single(
            invalid.Diagnostics,
            diagnostic => diagnostic.Id == "PGXAML2083");
        Assert.Contains("exactly one accessible instance void", invalidDiagnostic.GetMessage());
        var invalidShape = typeSystem.ResolveType(
            "using:Demo",
            "InvalidDuckReceiverControl")!.MarkupExtensionReceiver!;
        Assert.False(invalidShape.IsValid);
        Assert.Single(invalidShape.Candidates);
        Assert.True(invalidShape.Candidates[0].IsStatic);

        var noOptIn = new RoslynXamlTypeSystem(compilation, profile, new WinUiXamlProfile())
            .ResolveType("using:Demo", "DuckReceiverControl")!;
        Assert.Null(noOptIn.MarkupExtensionReceiver);
    }

    [Fact]
    public void MarkupExtensionReceiversLowerThroughStructuredProfileAndRespectHandlerPrecedence()
    {
        const string receiverXaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:LegacyReceiverControl Value="{local:HandlerValue}" />
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new ObjectWriterEmissionProfile();
        var receiverTypeSystem = new RoslynXamlTypeSystem(
            compilation,
            profile,
            new MarkupExtensionReceiverMetadataProvider(),
            new ObjectWriterHandlerMetadataProvider(),
            new WinUiXamlProfile());
        var emitted = new CSharpXamlEmitter().Emit(
            Convert(receiverXaml),
            receiverTypeSystem,
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions
            {
                ResourceUri = "Pages/Receiver.xaml"
            });
        Assert.DoesNotContain(
            emitted.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var invocations = tree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .ToArray();
        var receiverCall = Assert.Single(invocations, invocation =>
            invocation.Expression.ToString().EndsWith(
                ".ReceiveMarkupExtension",
                StringComparison.Ordinal));
        Assert.Equal("\"Value\"", receiverCall.ArgumentList.Arguments[0].Expression.ToString());
        Assert.Contains(
            "System.Windows.Markup.MarkupExtension",
            receiverCall.ArgumentList.Arguments[1].Expression.ToString());
        Assert.Contains(
            "System.IServiceProvider",
            receiverCall.ArgumentList.Arguments[2].Expression.ToString());
        Assert.Single(
            tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
            creation => creation.Type.ToString().Contains(
                "HandlerValueExtension",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        const string attributedXaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:AttributedReceiverControl Value="{local:HandlerValue}" />
</Page>
""";
        var attributed = new CSharpXamlEmitter().Emit(
            Convert(attributedXaml),
            receiverTypeSystem,
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var attributedTree = Assert.Single(attributed.Sources).GeneratedSyntaxTree!;
        var attributedInvocations = attributedTree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .ToArray();
        Assert.Contains(attributedInvocations, invocation =>
            invocation.Expression.ToString().EndsWith(
                "ObjectWriterRuntime.ApplyMarkupExtension",
                StringComparison.Ordinal));
        Assert.DoesNotContain(attributedInvocations, invocation =>
            invocation.Expression.ToString().EndsWith(
                ".ReceiveMarkupExtension",
                StringComparison.Ordinal));

        var unsupportedProfile = new WinUiXamlProfile();
        var unsupported = new CSharpXamlEmitter().Emit(
            Convert(receiverXaml),
            new RoslynXamlTypeSystem(
                compilation,
                unsupportedProfile,
                new MarkupExtensionReceiverMetadataProvider()),
            unsupportedProfile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        Assert.Contains(unsupported.Diagnostics, diagnostic => diagnostic.Id == "PGXAML3043");
    }

    [Fact]
    public void MarkupExtensionReceiverSymbolsSurviveMetadataReferences()
    {
        var sourceCompilation = CreateCompilation();
        using var image = new MemoryStream();
        var emit = sourceCompilation.Emit(image);
        Assert.True(
            emit.Success,
            string.Join(Environment.NewLine, emit.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        var consumer = CSharpCompilation.Create(
            "ReceiverMetadataConsumer",
            syntaxTrees: null,
            PlatformReferences().Append(MetadataReference.CreateFromImage(image.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var profile = new ObjectWriterEmissionProfile();
        var typeSystem = new RoslynXamlTypeSystem(
            consumer,
            profile,
            new MarkupExtensionReceiverMetadataProvider());

        var type = typeSystem.ResolveType("using:Demo", "LegacyReceiverControl")!;
        var receiver =
            Assert.IsType<ProGPU.Xaml.Schema.XamlMarkupExtensionReceiverShapeInfo>(
                type.MarkupExtensionReceiver);
        Assert.True(receiver.IsValid, receiver.Error);
        Assert.All(
            new ISymbol[]
            {
                receiver.IdentityType!,
                receiver.ReceiveMethod!,
                receiver.MarkupExtensionType!,
                receiver.ServiceProviderType!
            },
            symbol => Assert.All(symbol.Locations, location => Assert.True(location.IsInMetadata)));
    }

    [Fact]
    public void ResourceGraphTracksLexicalScopesAndStaticForwardReferenceRules()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Page.Resources>
    <ResourceDictionary>
      <TextBlock x:Key="First" Text="{StaticResource Later}" />
      <TextBlock x:Key="Later" Text="value" />
      <TextBlock x:Key="Third" Text="{StaticResource First}" />
      <TextBlock x:Key="ThemeForward" Text="{ThemeResource ThemeLater}" />
      <TextBlock x:Key="ThemeLater" Text="theme" />
      <TextBlock x:Key="Self" Text="{StaticResource Self}" />
    </ResourceDictionary>
  </Page.Resources>
</Page>
""";
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(CreateCompilation(), profile);
        var bound = new XamlSemanticBinder().Bind(
            infoset,
            typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var resources = Assert.Single(bound.Root!.Members, member => member.Member.Symbol?.Name == "Resources");
        Assert.True(Assert.IsType<XamlBoundObject>(Assert.Single(resources.Values)).IsRetrieved);
        Assert.Equal(XamlIrOperationKind.RetrieveMember,
            Assert.Single(new XamlConstructionLowerer().Lower(bound).Root!.Operations).Kind);
        var graph = new XamlResourceGraphBuilder().Build(bound);
        Assert.Equal(6, graph.Definitions.Length);
        Assert.Equal(4, graph.References.Length);
        Assert.Equal(XamlResourceResolutionKind.ForwardReferenceDisallowed,
            Assert.Single(graph.References, reference => reference.Key == "Later").Resolution);
        Assert.Equal(XamlResourceResolutionKind.Resolved,
            Assert.Single(graph.References, reference => reference.Key == "First").Resolution);
        Assert.Equal(XamlResourceResolutionKind.Resolved,
            Assert.Single(graph.References, reference => reference.Key == "ThemeLater").Resolution);
        Assert.Equal(XamlResourceResolutionKind.Unresolved,
            Assert.Single(graph.References, reference => reference.Key == "Self").Resolution);
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Id == "PGXAML4002");
        Assert.All(graph.Scopes, scope => Assert.NotEqual(0UL, scope.Id));
        var emitted = new CSharpXamlEmitter().Emit(infoset, typeSystem, profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions { Strict = false });
        Assert.Contains(emitted.Diagnostics, diagnostic => diagnostic.Id == "PGXAML4002");

        var reorderedGraph = new XamlResourceGraphBuilder().Build(
            bound,
            resourceDependencies: null,
            allowStaticResourceForwardReferences: true);
        Assert.DoesNotContain(reorderedGraph.Diagnostics, diagnostic => diagnostic.Id == "PGXAML4002");
        Assert.Equal(
            XamlResourceResolutionKind.Resolved,
            Assert.Single(reorderedGraph.References, reference => reference.Key == "Later").Resolution);
    }

    [Fact]
    public void WinUiDictionaryNameAliasPrecedesImplicitStyleKey()
    {
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style x:Name="NamedStyle" TargetType="TextBlock" />
  <Style x:Name="SecondNamedStyle" TargetType="TextBlock" />
</ResourceDictionary>
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        Assert.DoesNotContain(bound.Diagnostics, diagnostic =>
            diagnostic.Id is "PGXAML2028" or "PGXAML2029");
        Assert.Equal(
            new[] { "NamedStyle", "SecondNamedStyle" },
            new XamlResourceGraphBuilder().Build(bound).Definitions
                .Select(static definition => definition.Key)
                .ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0}")]
    public void WinUiLegacyDoubleTextPolicyIsNarrowAndEmitsNumericRoslynLiteral(string text)
    {
        var compilation = CreateCompilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText("""
namespace Demo {
  public sealed class DoubleHolder { public double Value { get; set; } }
  public partial class DoublePage : Microsoft.UI.Xaml.Controls.Page { }
}
"""));
        var xaml = $"""
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.DoublePage">
  <local:DoubleHolder Value="{text}" />
</Page>
""";
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);

        Assert.False(XamlIntrinsicTextSyntax.IsValid(
            typeSystem.ResolveType(XamlNamespaces.Language2006, "Double")!,
            text));
        Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2042");
        var emitted = new CSharpXamlEmitter().Emit(
            infoset,
            typeSystem,
            profile,
            new XamlCompilerOptions { Strict = false });
        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2102");
        Assert.Contains(
            Assert.Single(emitted.Sources).GeneratedSyntaxTree!.GetRoot()
                .DescendantNodes().OfType<LiteralExpressionSyntax>(),
            literal => literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
                       System.Convert.ToDouble(
                           literal.Token.Value,
                           System.Globalization.CultureInfo.InvariantCulture) == 0d);
    }

    [Fact]
    public void ThemeDictionariesAreConditionalSiblingPartitions()
    {
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light">
      <TextBlock x:Key="Accent" Text="light" />
      <TextBlock x:Key="LocalUse" Text="{StaticResource Accent}" />
      <Page x:Key="CrossVariant" Content="{ThemeResource DarkOnly}" />
    </ResourceDictionary>
    <ResourceDictionary x:Key="Dark">
      <TextBlock x:Key="Accent" Text="dark" />
      <TextBlock x:Key="DarkOnly" Text="dark-only" />
    </ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>
  <Page x:Key="Consumer" Content="{ThemeResource Accent}" />
  <Page x:Key="StaticConsumer" Content="{StaticResource Accent}" />
</ResourceDictionary>
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var graph = new XamlResourceGraphBuilder().Build(bound);
        var partitions = graph.Scopes
            .Where(scope => scope.Kind == XamlResourceScopeKind.ThemePartition)
            .OrderBy(scope => scope.PartitionKey!.Text, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "Dark", "Light" }, partitions.Select(scope => scope.PartitionKey!.Text));
        Assert.Equal(2, graph.Definitions.Count(definition => definition.Key == "Accent"));
        Assert.Single(graph.Definitions, definition => definition.Key == "Consumer");
        Assert.Single(graph.Definitions, definition => definition.Key == "StaticConsumer");

        var dynamicReferences = graph.References.Where(reference => reference.Kind == XamlResourceReferenceKind.Theme).ToArray();
        Assert.Equal(2, dynamicReferences.Length);
        var dynamicReference = Assert.Single(dynamicReferences, reference => reference.Key == "Accent");
        Assert.Equal(XamlResourceResolutionKind.ResolvedConditional, dynamicReference.Resolution);
        Assert.Equal(2, dynamicReference.CandidateDefinitionStableIds.Length);
        Assert.Equal(XamlResourceResolutionKind.Unresolved,
            Assert.Single(dynamicReferences, reference => reference.Key == "DarkOnly").Resolution);

        var staticReferences = graph.References.Where(reference => reference.Kind == XamlResourceReferenceKind.Static).ToArray();
        Assert.Equal(2, staticReferences.Length);
        var localReference = Assert.Single(staticReferences,
            reference => graph.Scopes.Single(scope => scope.Id == reference.ScopeId).Kind == XamlResourceScopeKind.ThemePartition);
        Assert.Equal(XamlResourceResolutionKind.Resolved, localReference.Resolution);
        var externalStaticReference = Assert.Single(staticReferences, reference => reference != localReference);
        Assert.Equal(XamlResourceResolutionKind.ResolvedConditional, externalStaticReference.Resolution);
        Assert.Equal(2, externalStaticReference.CandidateDefinitionStableIds.Length);
        var lightScope = Assert.Single(partitions, scope => scope.PartitionKey!.Text == "Light");
        Assert.Equal(lightScope.Id, Assert.Single(graph.Definitions,
            definition => definition.ValueStableId == localReference.DefinitionStableId).ScopeId);

        var rawManifest = new XamlResourceDocumentManifestBuilder().Build(bound.Infoset);
        var manifest = new XamlResourceSemanticManifestBuilder().Build(rawManifest, bound);
        Assert.DoesNotContain(manifest.Definitions,
            definition => definition.Key == "Light" || definition.Key == "Dark");
        var accents = manifest.Definitions.Where(definition => definition.Key == "Accent").ToArray();
        Assert.Equal(2, accents.Length);
        Assert.All(accents, definition =>
        {
            Assert.True(definition.IsProviderVisible);
            Assert.Equal(XamlResourcePartitionKind.Theme, definition.Partition!.Kind);
        });
        Assert.Equal(new[] { "Dark", "Light" }, accents
            .Select(definition => definition.Partition!.Key.Text)
            .OrderBy(static key => key, StringComparer.Ordinal));

        var emitted = new CSharpXamlEmitter().Emit(
            bound.Infoset,
            new RoslynXamlTypeSystem(CreateCompilation(), profile),
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions { ResourceUri = "/Themes/ThemeResources.xaml" });
        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(emitted.Sources);
        Assert.Contains("ThemeDictionaries.Add(\"Light\"", generated.Source, StringComparison.Ordinal);
        Assert.Contains("ThemeDictionaries.Add(\"Dark\"", generated.Source, StringComparison.Ordinal);
        Assert.True(
            generated.Source.IndexOf("ThemeDictionaries.Add(\"Light\"", StringComparison.Ordinal) <
            generated.Source.IndexOf(".Add(\"Accent\",", StringComparison.Ordinal));
        Assert.True(
            generated.Source.IndexOf("ThemeDictionaries.Add(\"Dark\"", StringComparison.Ordinal) <
            generated.Source.LastIndexOf(".Add(\"Consumer\",", StringComparison.Ordinal));
        Assert.Contains(
            "XamlResourceResolver.Resolve<object?>",
            generated.Source,
            StringComparison.Ordinal);
        var generatedRoot = generated.GeneratedSyntaxTree!.GetRoot();
        Assert.Contains(
            generatedRoot.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
            creation =>
                creation.Type.ToString().EndsWith("ThemeResource", StringComparison.Ordinal) &&
                creation.ArgumentList?.Arguments.Count == 2 &&
                creation.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax lookupRoot &&
                lookupRoot.Identifier.ValueText != "target" &&
                creation.ArgumentList.Arguments[1].Expression is LiteralExpressionSyntax key &&
                string.Equals(key.Token.ValueText, "DarkOnly", StringComparison.Ordinal));
        Assert.Contains(
            generatedRoot.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation =>
                invocation.Expression.ToString().Contains("XamlResourceResolver.Resolve", StringComparison.Ordinal) &&
                invocation.ArgumentList.Arguments.Count == 2 &&
                invocation.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax lookupRoot &&
                lookupRoot.Identifier.ValueText != "target" &&
                invocation.ArgumentList.Arguments[1].Expression is LiteralExpressionSyntax key &&
                string.Equals(key.Token.ValueText, "Accent", StringComparison.Ordinal));
        Assert.DoesNotContain(CreateCompilation().AddSyntaxTrees(generated.GeneratedSyntaxTree!).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void KeyedStaticResourceAliasIsEmittedAsDictionaryValue()
    {
        var profile = new WinUiXamlProfile();
        var compilation = CreateCompilation();
        var infoset = Convert("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <StaticResource x:Key="AliasValue" ResourceKey="BaseValue" />
  <TextBlock x:Key="BaseValue" Text="base" />
</ResourceDictionary>
""");

        var emitted = new CSharpXamlEmitter().Emit(
            infoset,
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new XamlCompilerOptions
            {
                ResourceUri = "/Themes/Aliases.xaml",
                StaticResourceForwardReferenceMode = XamlStaticResourceForwardReferenceMode.Reorder
            });

        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(emitted.Sources);
        Assert.Contains(".Add(\"AliasValue\",", generated.Source, StringComparison.Ordinal);
        Assert.True(
            generated.Source.IndexOf(".Add(\"BaseValue\",", StringComparison.Ordinal) <
            generated.Source.IndexOf(".Add(\"AliasValue\",", StringComparison.Ordinal));
        Assert.Contains(
            "XamlResourceResolver.Resolve<",
            generated.Source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            compilation.AddSyntaxTrees(generated.GeneratedSyntaxTree!).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void SingleKeyedThemeDictionaryIsAnItemNotRetrievedPropertyObject()
    {
        var bound = new XamlSemanticBinder().Bind(
            Convert("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light"><TextBlock x:Key="Accent" Text="light" /></ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
"""),
            new RoslynXamlTypeSystem(CreateCompilation(), new WinUiXamlProfile()));
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var themeMember = Assert.Single(bound.Root!.Members,
            member => member.Member.Symbol?.ResourceRole == ProGPU.Xaml.Schema.XamlResourceMemberRole.ThemeDictionaries);
        Assert.False(Assert.IsType<XamlBoundObject>(Assert.Single(themeMember.Values)).IsRetrieved);
        var partition = Assert.Single(new XamlResourceGraphBuilder().Build(bound).Scopes,
            scope => scope.Kind == XamlResourceScopeKind.ThemePartition);
        Assert.Equal("Light", partition.PartitionKey!.Text);
    }

    [Fact]
    public void ThemeResourceResolvesAllExternalProviderVariants()
    {
        const string providerPath = "/project/Themes/Controls.xaml";
        const string pagePath = "/project/Pages/MainPage.xaml";
        var providerInfoset = ConvertAt(providerPath, """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light"><TextBlock x:Key="Accent" Text="light" /></ResourceDictionary>
    <ResourceDictionary x:Key="Dark"><TextBlock x:Key="Accent" Text="dark" /></ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
""");
        var pageInfoset = ConvertAt(pagePath, """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="../Themes/Controls.xaml" />
  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
  <TextBlock Text="{ThemeResource Accent}" />
</Page>
""");
        var profile = new WinUiXamlProfile();
        var compilation = CreateCompilation();
        var providerBound = new XamlSemanticBinder().Bind(
            providerInfoset,
            new RoslynXamlTypeSystem(compilation, profile));
        var pageBound = new XamlSemanticBinder().Bind(
            pageInfoset,
            new RoslynXamlTypeSystem(compilation, profile));
        Assert.False(providerBound.HasErrors, string.Join(Environment.NewLine, providerBound.Diagnostics));
        Assert.False(pageBound.HasErrors, string.Join(Environment.NewLine, pageBound.Diagnostics));

        var rawBuilder = new XamlResourceDocumentManifestBuilder();
        var semanticBuilder = new XamlResourceSemanticManifestBuilder();
        var providerManifest = semanticBuilder.Build(rawBuilder.Build(providerInfoset), providerBound);
        var pageManifest = semanticBuilder.Build(rawBuilder.Build(pageInfoset), pageBound);
        var slice = new XamlResourceProjectIndex(new[] { providerManifest, pageManifest })
            .GetDependencySlice(pagePath);
        var graph = new XamlResourceGraphBuilder().Build(pageBound, slice);

        var reference = Assert.Single(graph.References);
        Assert.Equal(XamlResourceResolutionKind.ResolvedExternal, reference.Resolution);
        Assert.Equal(2, reference.ExternalCandidates.Length);
        Assert.Equal(new[] { "Dark", "Light" }, reference.ExternalCandidates
            .Select(candidate => candidate.Partition!.Key.Text)
            .OrderBy(static key => key, StringComparer.Ordinal));
        Assert.All(reference.ExternalCandidates, candidate => Assert.Equal(providerPath, candidate.ProviderPath));
    }

    [Fact]
    public void ThemePartitionSourceImportsPreserveVariantIdentityTransitively()
    {
        const string lightPath = "/project/Themes/Light.xaml";
        const string darkPath = "/project/Themes/Dark.xaml";
        const string themePath = "/project/Themes/ThemeResources.xaml";
        const string pagePath = "/project/MainPage.xaml";
        var light = ConvertAt(lightPath, """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <TextBlock x:Key="Accent" Text="light" />
</ResourceDictionary>
""");
        var dark = ConvertAt(darkPath, """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <TextBlock x:Key="Accent" Text="dark" />
</ResourceDictionary>
""");
        var themes = ConvertAt(themePath, """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light" Source="Light.xaml" />
    <ResourceDictionary x:Key="Dark" Source="Dark.xaml" />
  </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
""");
        var page = ConvertAt(pagePath, """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Themes/ThemeResources.xaml" />
  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
  <Page Content="{ThemeResource Accent}" />
</Page>
""");
        var profile = new WinUiXamlProfile();
        var compilation = CreateCompilation();
        var infosets = new[] { light, dark, themes, page };
        var manifests = new List<XamlResourceDocumentManifest>();
        XamlBoundDocument? pageBound = null;
        foreach (var infoset in infosets)
        {
            var bound = new XamlSemanticBinder().Bind(
                infoset,
                new RoslynXamlTypeSystem(compilation, profile));
            Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
            var raw = new XamlResourceDocumentManifestBuilder().Build(infoset);
            manifests.Add(new XamlResourceSemanticManifestBuilder().Build(raw, bound));
            if (infoset.Path == pagePath) pageBound = bound;
        }

        var themeManifest = Assert.Single(manifests, manifest => manifest.DocumentPath == themePath);
        Assert.Equal(2, themeManifest.ImportEntries.Length);
        Assert.Equal(new[] { "Dark", "Light" }, themeManifest.ImportEntries
            .Select(import => import.Partition!.Key.Text)
            .OrderBy(static key => key, StringComparer.Ordinal));

        var slice = new XamlResourceProjectIndex(manifests).GetDependencySlice(pagePath);
        var graph = new XamlResourceGraphBuilder().Build(pageBound!, slice);
        var reference = Assert.Single(graph.References);
        Assert.Equal(XamlResourceResolutionKind.ResolvedExternal, reference.Resolution);
        Assert.Equal(2, reference.ExternalCandidates.Length);
        Assert.Equal(new[] { darkPath, lightPath }, reference.ExternalCandidates
            .Select(candidate => candidate.ProviderPath)
            .OrderBy(static path => path, StringComparer.Ordinal));
        Assert.Equal(new[] { "Dark", "Light" }, reference.ExternalCandidates
            .Select(candidate => candidate.Partition!.Key.Text)
            .OrderBy(static key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void ClasslessRootResourceDictionaryPublishesDefinitions()
    {
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:String x:Key="Greeting">Hello</x:String>
</ResourceDictionary>
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile));
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var graph = new XamlResourceGraphBuilder().Build(bound);
        Assert.Equal("Greeting", Assert.Single(graph.Definitions).Key);

        var emitted = new CSharpXamlEmitter().Emit(
            bound.Infoset,
            new RoslynXamlTypeSystem(CreateCompilation(), profile),
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions { ResourceUri = @"Themes\Colors.xaml" });
        Assert.DoesNotContain(emitted.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var source = Assert.Single(emitted.Sources);
        Assert.NotNull(source.CompiledResource);
        Assert.Equal("/Themes/Colors.xaml", source.CompiledResource!.ResourceUri);
        Assert.Contains("Build()", source.Source, StringComparison.Ordinal);
        Assert.Contains("Populate(", source.Source, StringComparison.Ordinal);
        Assert.Contains("ModuleInitializer", source.Source, StringComparison.Ordinal);
        Assert.Contains("XamlResourceProviderRegistry.Register", source.Source, StringComparison.Ordinal);
        Assert.Contains("target.Add(\"Greeting\"", source.Source, StringComparison.Ordinal);
        Assert.DoesNotContain(CreateCompilation().AddSyntaxTrees(source.GeneratedSyntaxTree!).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ResourceGraphResolvesMergedDictionaryProvidersFromProjectSlice()
    {
        const string pageText = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="../Themes/Colors.xaml" />
  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
  <TextBlock Text="{StaticResource Accent}" />
</Page>
""";
        const string colorsText = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:String x:Key="Accent">red</x:String>
</ResourceDictionary>
""";
        var page = new XamlInfosetConverter().Convert(XamlParser.Parse(
            SourceText.From(pageText), "/project/Pages/MainPage.xaml").Document);
        var colors = new XamlInfosetConverter().Convert(XamlParser.Parse(
            SourceText.From(colorsText), "/project/Themes/Colors.xaml").Document);
        var manifests = new XamlResourceDocumentManifestBuilder();
        var dependencies = new XamlResourceProjectIndex(new[] { manifests.Build(page), manifests.Build(colors) })
            .GetDependencySlice(page.Path);
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(page, new RoslynXamlTypeSystem(CreateCompilation(), profile));

        var graph = new XamlResourceGraphBuilder().Build(bound, dependencies);

        var reference = Assert.Single(graph.References);
        Assert.Equal(XamlResourceResolutionKind.ResolvedExternal, reference.Resolution);
        Assert.Equal(colors.Path, reference.ProviderPath);
    }

    [Fact]
    public void ExternalMergedDictionariesRespectLexicalResourceScopeAncestry()
    {
        const string pagePath = "/project/MainPage.xaml";
        var page = ConvertAt(pagePath, """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <StackPanel>
    <TextBlock Text="{StaticResource RootOnly}" />
    <TextBlock Text="{StaticResource NestedOnly}" />
    <TextBlock Text="{StaticResource Shared}" />
    <StackPanel>
      <StackPanel.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="Themes/Nested.xaml" />
      </ResourceDictionary.MergedDictionaries></ResourceDictionary></StackPanel.Resources>
      <TextBlock Text="{StaticResource RootOnly}" />
      <TextBlock Text="{StaticResource NestedOnly}" />
      <TextBlock Text="{StaticResource Shared}" />
    </StackPanel>
  </StackPanel>
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Themes/Root.xaml" />
  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
</Page>
""");
        var rootProvider = ConvertAt("/project/Themes/Root.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:String x:Key="RootOnly">root</x:String>
  <x:String x:Key="Shared">root shared</x:String>
</ResourceDictionary>
""");
        var nestedProvider = ConvertAt("/project/Themes/Nested.xaml", """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:String x:Key="NestedOnly">nested</x:String>
  <x:String x:Key="Shared">nested shared</x:String>
</ResourceDictionary>
""");
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(CreateCompilation(), profile);
        var binder = new XamlSemanticBinder();
        var pageBound = binder.Bind(page, typeSystem);
        var rootBound = binder.Bind(rootProvider, typeSystem);
        var nestedBound = binder.Bind(nestedProvider, typeSystem);
        Assert.False(pageBound.HasErrors, string.Join(Environment.NewLine, pageBound.Diagnostics));

        var raw = new XamlResourceDocumentManifestBuilder();
        var semantic = new XamlResourceSemanticManifestBuilder();
        var manifests = new[]
        {
            semantic.Build(raw.Build(page), pageBound),
            semantic.Build(raw.Build(rootProvider), rootBound),
            semantic.Build(raw.Build(nestedProvider), nestedBound)
        };
        var dependencies = new XamlResourceProjectIndex(manifests).GetDependencySlice(pagePath);
        var graph = new XamlResourceGraphBuilder().Build(pageBound, dependencies);

        var rootReferences = graph.References.Where(reference => reference.Key == "RootOnly").ToArray();
        var nestedReferences = graph.References.Where(reference => reference.Key == "NestedOnly").ToArray();
        Assert.Equal(2, rootReferences.Length);
        Assert.All(rootReferences, reference =>
            Assert.Equal(XamlResourceResolutionKind.ResolvedExternal, reference.Resolution));
        Assert.Equal(2, nestedReferences.Length);
        Assert.Equal(XamlResourceResolutionKind.Unresolved, nestedReferences[0].Resolution);
        Assert.Equal(XamlResourceResolutionKind.ResolvedExternal, nestedReferences[1].Resolution);
        var sharedReferences = graph.References.Where(reference => reference.Key == "Shared").ToArray();
        Assert.Equal(2, sharedReferences.Length);
        Assert.Equal(manifests[1].DocumentPath, sharedReferences[0].ProviderPath);
        Assert.Equal(manifests[2].DocumentPath, sharedReferences[1].ProviderPath);
        Assert.Equal(4, dependencies.ExternalDefinitions.Length);
        Assert.Equal(2, dependencies.ExternalDefinitions.Select(definition => definition.ConsumerScopeOwnerStableId).Distinct().Count());
    }

    [Fact]
    public void RetrievedReadOnlyDictionaryEmitsPopulationAgainstGetter()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <Page.Resources>
    <ResourceDictionary><TextBlock x:Key="Greeting" Text="Hello" /></ResourceDictionary>
  </Page.Resources>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var emitted = new CSharpXamlEmitter().Emit(infoset, typeSystem, profile, new ProGPU.Xaml.Schema.XamlCompilerOptions());
        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        Assert.Contains("this.Resources.Add", tree.GetText().ToString(), StringComparison.Ordinal);
        Assert.Contains("\"Greeting\"", tree.GetText().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void TypeValuedExplicitResourceKeyRemainsRoslynTypeOfExpression()
    {
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:Demo">
  <x:String x:Key="{x:Type local:Widget}">typed</x:String>
</ResourceDictionary>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var graph = new XamlResourceGraphBuilder().Build(bound);
        var definition = Assert.Single(graph.Definitions);
        Assert.Equal(XamlResourceKeyKind.Type, definition.ResourceKey.Kind);
        Assert.Equal("Widget", definition.ResourceKey.TypeSymbol?.Name);

        var emitted = new CSharpXamlEmitter().EmitProgram(
            new XamlConstructionLowerer().Lower(bound, graph),
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var typeOf = Assert.Single(tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeOfExpressionSyntax>());
        Assert.Equal("global::Demo.Widget", typeOf.Type.ToString());
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void WinUiStyleTargetTypeIsValidatedImplicitTypedResourceKey()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <Page.Resources>
    <ResourceDictionary>
      <Style TargetType="Button" />
    </ResourceDictionary>
  </Page.Resources>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var styleType = typeSystem.ResolveType(XamlNamespaces.Presentation2006, "Style");
        Assert.Equal("TargetType", styleType?.DictionaryKeyMemberName);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var graph = new XamlResourceGraphBuilder().Build(bound);
        var definition = Assert.Single(graph.Definitions);
        Assert.Equal(XamlResourceKeyKind.Type, definition.ResourceKey.Kind);
        Assert.Equal("Button", definition.ResourceKey.TypeSymbol?.Name);

        var emitted = new CSharpXamlEmitter().EmitProgram(
            new XamlConstructionLowerer().Lower(bound, graph),
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        Assert.True(tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeOfExpressionSyntax>()
            .Count(expression => expression.Type.ToString() == "global::Microsoft.UI.Xaml.Controls.Button") >= 2);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var duplicate = new XamlSemanticBinder().Bind(Convert("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Style TargetType="Button" />
  <Style TargetType="Button" />
</ResourceDictionary>
"""), typeSystem, new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(duplicate.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2029");

        var textAndConstant = new XamlSemanticBinder().Bind(Convert("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:Demo">
  <x:String x:Key="Hello">text</x:String>
  <x:String x:Key="{x:Static local:Palette.Greeting}">constant</x:String>
</ResourceDictionary>
"""), typeSystem, new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(textAndConstant.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2029");

        var convertedInteger = new XamlSemanticBinder().Bind(Convert("""
<local:IntDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:local="using:Demo">
  <x:String x:Key="1">text</x:String>
  <x:String x:Key="{x:Static local:Palette.One}">constant</x:String>
</local:IntDictionary>
"""), typeSystem, new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(convertedInteger.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2029");

        var unresolvedTarget = new XamlSemanticBinder().Bind(Convert("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Style TargetType="MissingControl" />
</ResourceDictionary>
"""), typeSystem, new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(unresolvedTarget.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2017");
        Assert.DoesNotContain(unresolvedTarget.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2028");
    }

    [Fact]
    public void NestedTypeResourceReferenceMatchesImplicitStyleKeyAndEmitsTypeOfLookup()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage"
      Content="{StaticResource {x:Type Button}}">
  <Page.Resources>
    <ResourceDictionary><Style TargetType="Button" /></ResourceDictionary>
  </Page.Resources>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var graph = new XamlResourceGraphBuilder().Build(bound);
        var reference = Assert.Single(graph.References);
        Assert.Equal(XamlResourceKeyKind.Type, reference.ResourceKey.Kind);
        Assert.Equal(XamlResourceResolutionKind.Resolved, reference.Resolution);

        var emitted = new CSharpXamlEmitter().EmitProgram(
            new XamlConstructionLowerer().Lower(bound, graph),
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var resolver = Assert.Single(tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(), invocation =>
                invocation.Expression.ToString().Contains("XamlResourceResolver.Resolve", StringComparison.Ordinal));
        Assert.IsType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeOfExpressionSyntax>(
            resolver.ArgumentList.Arguments[1].Expression);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void CrossFileTypedReferenceResolvesByPortableManifestIdentity()
    {
        const string pagePath = "/project/MainPage.xaml";
        const string providerPath = "/project/Themes/Typed.xaml";
        var pageInfoset = ConvertAt(pagePath, """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage"
      Content="{StaticResource {x:Type local:Widget}}">
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Themes/Typed.xaml" />
  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
</Page>
""");
        var providerInfoset = ConvertAt(providerPath, """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:Demo">
  <x:String x:Key="{x:Type local:Widget}">typed</x:String>
</ResourceDictionary>
""");
        var pageManifest = new XamlResourceDocumentManifestBuilder().Build(pageInfoset);
        var providerManifest = new XamlResourceDocumentManifestBuilder().Build(providerInfoset);
        var slice = new XamlResourceProjectIndex(new[] { pageManifest, providerManifest })
            .GetDependencySlice(pagePath);
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            pageInfoset,
            new RoslynXamlTypeSystem(CreateCompilation(), profile));
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));

        var graph = new XamlResourceGraphBuilder().Build(bound, slice);
        var reference = Assert.Single(graph.References);
        Assert.Equal(XamlResourceKeyKind.Type, reference.ResourceKey.Kind);
        Assert.Equal(XamlResourceResolutionKind.ResolvedExternal, reference.Resolution);
        Assert.Equal(providerManifest.DocumentPath, reference.ProviderPath);
    }

    [Fact]
    public void StaticMemberResourceKeyAndReferenceRetainCanonicalSymbolExpression()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage"
      Content="{StaticResource {x:Static local:Palette.Greeting}}">
  <Page.Resources><ResourceDictionary>
    <x:String x:Key="{x:Static local:Palette.Greeting}">value</x:String>
  </ResourceDictionary></Page.Resources>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var graph = new XamlResourceGraphBuilder().Build(bound);
        Assert.Equal(XamlResourceKeyKind.StaticMember, Assert.Single(graph.Definitions).ResourceKey.Kind);
        Assert.Equal(XamlResourceResolutionKind.Resolved, Assert.Single(graph.References).Resolution);

        var emitted = new CSharpXamlEmitter().EmitProgram(
            new XamlConstructionLowerer().Lower(bound, graph),
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        Assert.True(tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>()
            .Count(access => access.ToString() == "global::Demo.Palette.Greeting") >= 2);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ConstantStaticAliasesShareRuntimeKeyIdentityButRetainTheirExpressions()
    {
        const string pagePath = "/project/MainPage.xaml";
        const string providerPath = "/project/Themes/Constants.xaml";
        var pageInfoset = ConvertAt(pagePath, """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage"
      Content="{StaticResource {x:Static local:Palette.GreetingAlias}}">
  <Page.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Themes/Constants.xaml" />
  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Page.Resources>
</Page>
""");
        var providerInfoset = ConvertAt(providerPath, """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:Demo">
  <x:String x:Key="{x:Static local:Palette.Greeting}">value</x:String>
</ResourceDictionary>
""");
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var binder = new XamlSemanticBinder();
        var pageBound = binder.Bind(pageInfoset, typeSystem);
        var providerBound = binder.Bind(providerInfoset, typeSystem);
        var pageKey = Assert.Single(new XamlResourceGraphBuilder().Build(pageBound).References).ResourceKey;
        var providerKey = Assert.Single(new XamlResourceGraphBuilder().Build(providerBound).Definitions).ResourceKey;
        Assert.Equal(providerKey.Identity, pageKey.Identity);
        Assert.NotEqual(providerKey.ExpressionIdentity, pageKey.ExpressionIdentity);
        Assert.StartsWith("constant:string:", pageKey.Identity, StringComparison.Ordinal);

        var raw = new XamlResourceDocumentManifestBuilder();
        var semantic = new XamlResourceSemanticManifestBuilder();
        var pageManifest = semantic.Build(raw.Build(pageInfoset), pageBound);
        var providerManifest = semantic.Build(raw.Build(providerInfoset), providerBound);
        var dependencies = new XamlResourceProjectIndex(new[] { pageManifest, providerManifest })
            .GetDependencySlice(pagePath);
        var graph = new XamlResourceGraphBuilder().Build(pageBound, dependencies);
        Assert.Equal(XamlResourceResolutionKind.ResolvedExternal, Assert.Single(graph.References).Resolution);

        var emitted = new CSharpXamlEmitter().EmitProgram(
            new XamlConstructionLowerer().Lower(pageBound, graph),
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        Assert.Contains(tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>(),
            access => access.ToString() == "global::Demo.Palette.GreetingAlias");
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void DuplicateConstantStaticAliasesAreDiagnosedByRuntimeEquality()
    {
        var profile = new WinUiXamlProfile();
        var typeSystem = new RoslynXamlTypeSystem(CreateCompilation(), profile);
        var duplicate = new XamlSemanticBinder().Bind(Convert("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:Demo">
  <x:String x:Key="{x:Static local:Palette.Greeting}">first</x:String>
  <x:String x:Key="{x:Static local:Palette.GreetingAlias}">second</x:String>
</ResourceDictionary>
"""), typeSystem, new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(duplicate.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2029");

        var distinctTypes = new XamlSemanticBinder().Bind(Convert("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:Demo">
  <x:String x:Key="{x:Static local:Palette.One}">int</x:String>
  <x:String x:Key="{x:Static local:Palette.LongOne}">long</x:String>
  <x:String x:Key="{x:Static local:Palette.RuntimeGreeting}">runtime</x:String>
  <x:String x:Key="Hello">text</x:String>
</ResourceDictionary>
"""), typeSystem, new XamlSemanticBindingOptions { Strict = false });
        Assert.DoesNotContain(distinctTypes.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2029");

        var nullKey = new XamlSemanticBinder().Bind(Convert("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:Demo">
  <x:String x:Key="{x:Static local:Palette.NullKey}">invalid</x:String>
</ResourceDictionary>
"""), typeSystem, new XamlSemanticBindingOptions { Strict = false });
        Assert.Contains(nullKey.Diagnostics, diagnostic => diagnostic.Id == "PGXAML2044");
    }

    [Fact]
    public void SymbolValuedKeyMustBeAssignableToCanonicalDictionaryKeyType()
    {
        const string xaml = """
<local:StringDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:local="using:Demo">
  <x:String x:Key="{x:Type local:Widget}">value</x:String>
</local:StringDictionary>
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile),
            new XamlSemanticBindingOptions { Strict = false });

        var diagnostic = Assert.Single(bound.Diagnostics, candidate => candidate.Id == "PGXAML2043");
        Assert.Equal("6.3.1.4", diagnostic.Properties["MSXamlSection"]);
    }

    [Fact]
    public void StaticResourceLowersThroughTypedResourceIrAndStructuredResolverCall()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <Page.Resources>
    <ResourceDictionary><x:String x:Key="Greeting">Hello</x:String></ResourceDictionary>
  </Page.Resources>
  <TextBlock Text="{StaticResource Greeting}" />
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var graph = new XamlResourceGraphBuilder().Build(bound);
        Assert.Equal(XamlResourceResolutionKind.Resolved, Assert.Single(graph.References).Resolution);
        var program = new XamlConstructionLowerer().Lower(bound, graph);
        Assert.Single(DescendantIr(program.Root).SelectMany(value => value.Operations)
            .SelectMany(operation => operation.Values).OfType<XamlIrResourceReference>());

        var emitted = new CSharpXamlEmitter().EmitProgram(program, profile, new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var resolverCall = Assert.Single(
            tree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().Contains(
                "XamlResourceResolver.Resolve", StringComparison.Ordinal));
        var resolverMember = Assert.IsType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>(
            resolverCall.Expression);
        var resolverName = Assert.IsType<Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax>(resolverMember.Name);
        Assert.Equal("string?", Assert.Single(resolverName.TypeArgumentList.Arguments).ToString());
        Assert.Contains("\"Greeting\"", tree.GetText().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void StaticResourceObjectElementIsSyntheticVocabularyAndLowersAsAReference()
    {
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:String x:Key="Base">value</x:String>
  <StaticResource x:Key="Alias" ResourceKey="Base" />
</ResourceDictionary>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var bound = new XamlSemanticBinder().Bind(infoset, typeSystem);

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var graph = new XamlResourceGraphBuilder().Build(bound);
        Assert.Contains(graph.Definitions, definition => definition.Key == "Alias");
        var reference = Assert.Single(graph.References);
        Assert.Equal("Base", reference.Key);
        Assert.Equal(XamlResourceResolutionKind.Resolved, reference.Resolution);

        var program = new XamlConstructionLowerer().Lower(bound, graph);
        var emitted = new CSharpXamlEmitter().EmitProgram(program, profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions { ResourceUri = "/Themes/Alias.xaml" });
        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ProfileTextSyntaxInitializesNonIntrinsicObjectElementsThroughStructuredLiteralSyntax()
    {
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Thickness x:Key="Inset">1,2,3,4</Thickness>
</ResourceDictionary>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var bound = new XamlSemanticBinder().Bind(
            infoset,
            new RoslynXamlTypeSystem(compilation, profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var thickness = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundObject>(),
            value => value.Type.Symbol?.MetadataName == "Microsoft.UI.Xaml.Thickness");
        var initialization = Assert.Single(thickness.Members,
            member => member.Member.RequestedName.LocalName == "Initialization");
        Assert.Equal("Initialization", initialization.Member.RequestedName.LocalName);
        Assert.Equal(ProGPU.Xaml.Schema.XamlTextSyntaxKind.Profile,
            Assert.IsType<XamlBoundText>(Assert.Single(initialization.Values)).TextSyntax.Kind);

        var program = new XamlConstructionLowerer().Lower(bound, new XamlResourceGraphBuilder().Build(bound));
        var emitted = new CSharpXamlEmitter().EmitProgram(program, profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions { ResourceUri = "/Themes/Values.xaml" });
        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var creation = Assert.Single(tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>(),
            candidate => candidate.Type.ToString().Contains("Microsoft.UI.Xaml.Thickness", StringComparison.Ordinal));
        Assert.Equal(4, creation.ArgumentList!.Arguments.Count);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void WinUiTypographyResourcesUseProfileTextSyntaxAndTypedRoslynExpressions()
    {
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <FontWeight x:Key="BodyWeight">SemiBold</FontWeight>
  <FontFamily x:Key="BodyFont">Segoe UI, Arial</FontFamily>
</ResourceDictionary>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var bound = new XamlSemanticBinder().Bind(
            infoset,
            new RoslynXamlTypeSystem(compilation, profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var initializedTypes = DescendantValues(bound.Root).OfType<XamlBoundObject>()
            .Where(value => value.Members.Any(member =>
                member.Member.RequestedName.LocalName == "Initialization"))
            .Select(value => value.Type.Symbol?.MetadataName)
            .ToArray();
        Assert.Contains("Windows.UI.Text.FontWeight", initializedTypes);
        Assert.Contains("Microsoft.UI.Xaml.Media.FontFamily", initializedTypes);

        var program = new XamlConstructionLowerer().Lower(bound, new XamlResourceGraphBuilder().Build(bound));
        var emitted = new CSharpXamlEmitter().EmitProgram(program, profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions { ResourceUri = "/Themes/Typography.xaml" });
        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var generated = tree.GetText().ToString();
        Assert.Contains("global::Microsoft.UI.Text.FontWeights.SemiBold", generated, StringComparison.Ordinal);
        Assert.Contains("new global::Microsoft.UI.Xaml.Media.FontFamily(\"Segoe UI, Arial\")", generated, StringComparison.Ordinal);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ProfilePseudoContentMemberPreservesParserOwnedDeferredTemplateSemantics()
    {
        const string xaml = """
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 TargetType="Page">
  <TextBlock Text="deferred" />
</ControlTemplate>
""";
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(CreateCompilation(), profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        Assert.NotNull(bound.Root);
        var template = bound.Root!;
        var content = Assert.Single(template.Members,
            member => member.Member.Symbol?.Kind == ProGPU.Xaml.Schema.XamlMemberKind.DeferredContent);
        Assert.Null(content.Member.Symbol!.Symbol);
        Assert.Equal(ProGPU.Xaml.Schema.XamlSchemaSemantics.TemplateContent,
            content.Member.Symbol.SyntheticSemantic);
        var program = new XamlConstructionLowerer().Lower(bound);
        Assert.Contains(program.Root!.Operations,
            operation => operation.Kind == XamlIrOperationKind.SetDeferredContent);
    }

    [Fact]
    public void DeferredTemplateFactoryIsEmittedAsStructuredRoslynLambda()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <ControlTemplate TargetType="Page">
    <StackPanel>
      <TextBlock x:Name="Caption" Text="deferred" />
    </StackPanel>
  </ControlTemplate>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var emitted = new CSharpXamlEmitter().Emit(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());

        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var root = tree.GetRoot();
        Assert.Contains(root.DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>(),
            lambda => lambda.ParameterList.Parameters.Single().Identifier.ValueText == "__templateContext");
        Assert.Contains("global::Microsoft.UI.Xaml.Markup.XamlTemplateFactory.SetFactory", tree.GetText().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void TemplateBindingEmitsSymbolResolvedRoslynOperationInsideDeferredFactory()
    {
        const string propertySystem = """
namespace Microsoft.UI.Xaml.Controls {
  public static class TemplateBinding {
    public static object Bind(object target, Microsoft.UI.Xaml.DependencyProperty targetProperty, object source, Microsoft.UI.Xaml.DependencyProperty sourceProperty) => new();
  }
}
namespace Demo {
  public sealed class TemplateOwner : Microsoft.UI.Xaml.FrameworkElement {
    public static readonly Microsoft.UI.Xaml.DependencyProperty SourceProperty = new();
    public string? Source { get; set; }
    public void SetValue(Microsoft.UI.Xaml.DependencyProperty property, object? value) { }
  }
  public sealed class TemplateChild : Microsoft.UI.Xaml.FrameworkElement {
    public static readonly Microsoft.UI.Xaml.DependencyProperty TargetProperty = new();
    public string? Target { get; set; }
    public void SetValue(Microsoft.UI.Xaml.DependencyProperty property, object? value) { }
  }
}
""";
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <ControlTemplate TargetType="local:TemplateOwner">
    <local:TemplateChild Target="{TemplateBinding Property=Source}" />
  </ControlTemplate>
</Page>
""";
        var compilation = CreateCompilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText(propertySystem));
        var profile = new WinUiXamlProfile();
        var emitted = new CSharpXamlEmitter().Emit(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());

        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        var invocation = Assert.Single(tree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>(), candidate =>
                candidate.Expression.ToString().EndsWith("TemplateBinding.Bind", StringComparison.Ordinal));
        var arguments = invocation.ArgumentList.Arguments.Select(argument => argument.Expression.ToString()).ToArray();
        Assert.Equal("global::Demo.TemplateChild.TargetProperty", arguments[1]);
        Assert.Contains("global::Demo.TemplateOwner", arguments[2], StringComparison.Ordinal);
        Assert.Contains("__templateContext", arguments[2], StringComparison.Ordinal);
        Assert.Equal("global::Demo.TemplateOwner.SourceProperty", arguments[3]);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void GetterOnlyAttachedCollectionShapeBindsAndEmitsThroughTypedGetter()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <StackPanel>
    <VisualStateManager.VisualStateGroups>
      <VisualStateGroup />
    </VisualStateManager.VisualStateGroups>
  </StackPanel>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var bound = new XamlSemanticBinder().Bind(
            infoset,
            new RoslynXamlTypeSystem(compilation, profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var attached = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundObject>()
            .SelectMany(value => value.Members),
            member => member.Member.Symbol?.Name == "VisualStateGroups");
        Assert.NotNull(attached.Member.Symbol!.AttachableShape);
        Assert.Null(attached.Member.Symbol.AttachableShape!.Setter);

        var emitted = new CSharpXamlEmitter().Emit(
            infoset,
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        Assert.Contains("VisualStateManager.GetVisualStateGroups", tree.GetText().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AttachedMemberOnAnInstanceOfItsOwnerTypeStillUsesAccessorShape()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <Grid Grid.Row="2" />
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var bound = new XamlSemanticBinder().Bind(
            infoset,
            new RoslynXamlTypeSystem(compilation, profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var row = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundObject>()
            .SelectMany(value => value.Members),
            member => member.Member.Symbol?.Name == "Row");
        Assert.Equal(ProGPU.Xaml.Schema.XamlMemberKind.AttachableProperty, row.Member.Symbol!.Kind);
        Assert.Equal("SetRow", row.Member.Symbol.AttachableShape!.Setter!.Name);

        var emitted = new CSharpXamlEmitter().Emit(
            infoset,
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        Assert.Contains("Grid.SetRow", tree.GetText().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void SelfOwnedAttachedMemberMayOmitItsOwnerQualifier()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="Demo.MainPage">
  <Grid Row="2" />
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var bound = new XamlSemanticBinder().Bind(
            infoset,
            new RoslynXamlTypeSystem(compilation, profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var row = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundObject>()
            .SelectMany(value => value.Members), member => member.Member.Symbol?.Name == "Row");
        Assert.Equal(ProGPU.Xaml.Schema.XamlMemberKind.AttachableProperty, row.Member.Symbol!.Kind);

        var emitted = new CSharpXamlEmitter().Emit(
            infoset,
            new RoslynXamlTypeSystem(compilation, profile),
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        Assert.Contains("Grid.SetRow", tree.GetText().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AttachedAccessorOverloadsSelectMostSpecificRoslynReceiver()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <TextBlock local:OverloadedAttachedOwner.Level="3" />
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var bound = new XamlSemanticBinder().Bind(
            Convert(xaml),
            new RoslynXamlTypeSystem(compilation, profile));

        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        var level = Assert.Single(DescendantValues(bound.Root).OfType<XamlBoundObject>()
            .SelectMany(value => value.Members), member => member.Member.Symbol?.Name == "Level");
        var shape = Assert.IsType<ProGPU.Xaml.Schema.XamlAttachedMemberShapeInfo>(level.Member.Symbol!.AttachableShape);
        Assert.Equal("Microsoft.UI.Xaml.FrameworkElement",
            shape.Setter!.Parameters[0].Type.ToDisplayString());
    }

    [Fact]
    public void ExplicitAddChildInterfaceShapeEmitsTypedInterfaceReceiver()
    {
        const string xaml = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Demo"
      x:Class="Demo.MainPage">
  <local:ExplicitChildHost><TextBlock Text="Child" /></local:ExplicitChildHost>
</Page>
""";
        var compilation = CreateCompilation();
        var profile = new WinUiXamlProfile();
        var infoset = Convert(xaml);
        var typeSystem = new RoslynXamlTypeSystem(compilation, profile);
        var hostType = typeSystem.ResolveType("using:Demo", "ExplicitChildHost");
        Assert.NotNull(hostType);
        Assert.Equal("AddChild", hostType.CollectionShape?.AddMethod.Name);
        Assert.Equal(TypeKind.Interface, hostType.CollectionShape?.AddMethod.ContainingType.TypeKind);

        var emitted = new CSharpXamlEmitter().Emit(
            infoset,
            typeSystem,
            profile,
            new ProGPU.Xaml.Schema.XamlCompilerOptions());
        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var tree = Assert.Single(emitted.Sources).GeneratedSyntaxTree!;
        Assert.Contains("((global::Microsoft.UI.Xaml.Markup.IAddChild)__xamlObject1).AddChild",
            tree.GetText().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(compilation.AddSyntaxTrees(tree).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private static XamlInfosetDocument Convert(string source)
        => ConvertAt("Binding.xaml", source);

    private static XamlInfosetDocument ConvertAt(string path, string source)
    {
        var syntax = XamlParser.Parse(
            SourceText.From(source),
            path,
            new XamlParseOptions { Mode = XamlParseMode.Recovering }).Document;
        return new XamlInfosetConverter().Convert(
            syntax,
            new XamlInfosetConversionOptions { Mode = XamlParseMode.Recovering });
    }

    private static IEnumerable<XamlBoundValue> DescendantValues(XamlBoundObject? root)
    {
        if (root == null) yield break;
        foreach (var member in root.Members)
        foreach (var value in member.Values)
        {
            yield return value;
            if (value is XamlBoundObject child)
                foreach (var descendant in DescendantValues(child)) yield return descendant;
        }
    }

    private static IEnumerable<XamlIrObject> DescendantIr(XamlIrObject? root)
    {
        if (root == null) yield break;
        yield return root;
        foreach (var operation in root.Operations)
        foreach (var value in operation.Values.OfType<XamlIrObject>())
        foreach (var descendant in DescendantIr(value)) yield return descendant;
    }

    private static IEnumerable<XamlIrValue> DescendantIrValues(XamlIrObject? root)
    {
        if (root == null) yield break;
        foreach (var operation in root.Operations)
        foreach (var value in operation.Values)
        {
            yield return value;
            if (value is XamlIrObject child)
                foreach (var descendant in DescendantIrValues(child)) yield return descendant;
        }
    }

    private sealed class DependencyMetadataProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.dependencies";
        public int MetadataPriority => 1000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } = new[]
        {
            new ProGPU.Xaml.Schema.XamlSchemaAttributeRule(
                "Demo.DependsOnAttribute",
                ProGPU.Xaml.Schema.XamlSchemaSemantics.DependsOn,
                ProGPU.Xaml.Schema.XamlSchemaAttributeTargets.Member,
                inherited: true,
                valueSource: ProGPU.Xaml.Schema.XamlSchemaAttributeValueSource.ConstructorArgument,
                constructorArgumentIndex: 0,
                allowMultiple: true),
            Alias("Demo.NameScopePropertyAttribute", ProGPU.Xaml.Schema.XamlSchemaSemantics.NameScopeProperty),
            Alias("Demo.XmlLanguagePropertyAttribute", ProGPU.Xaml.Schema.XamlSchemaSemantics.XmlLanguageProperty),
            Alias("Demo.UidPropertyAttribute", ProGPU.Xaml.Schema.XamlSchemaSemantics.UidProperty),
            Alias("Demo.ContentOneAttribute", ProGPU.Xaml.Schema.XamlSchemaSemantics.ContentProperty),
            Alias("Demo.ContentTwoAttribute", ProGPU.Xaml.Schema.XamlSchemaSemantics.ContentProperty)
        };
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; } = new(
            nameScopeInterfaceMetadataNames: new[] { "Demo.ITestNameScope" },
            inferNameScopeFromMethods: true);

        private static ProGPU.Xaml.Schema.XamlSchemaAttributeRule Alias(string name, string semantic) => new(
            name,
            semantic,
            ProGPU.Xaml.Schema.XamlSchemaAttributeTargets.Type,
            inherited: true,
            valueSource: ProGPU.Xaml.Schema.XamlSchemaAttributeValueSource.ConstructorArgument,
            constructorArgumentIndex: 0);
    }

    private sealed class NameScopeShapeMetadataProvider :
        ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public NameScopeShapeMetadataProvider(
            string providerId,
            int priority,
            string? interfaceName = null,
            string? registerName = null,
            string? unregisterName = null,
            string? findName = null,
            bool inferDuck = false)
        {
            MetadataProviderId = providerId;
            MetadataPriority = priority;
            SymbolShapePolicy = new ProGPU.Xaml.Schema.XamlSymbolShapePolicy(
                nameScopeInterfaceMetadataNames:
                    interfaceName == null ? null : new[] { interfaceName },
                nameScopeRegisterMethodNames:
                    registerName == null ? null : new[] { registerName },
                nameScopeUnregisterMethodNames:
                    unregisterName == null ? null : new[] { unregisterName },
                nameScopeFindMethodNames:
                    findName == null ? null : new[] { findName },
                inferNameScopeFromMethods: inferDuck ? true : null);
        }

        public string MetadataProviderId { get; }
        public int MetadataPriority { get; }
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules =>
            Array.Empty<ProGPU.Xaml.Schema.XamlSchemaAttributeRule>();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; }
    }

    private sealed class MarkupOptionMetadataProvider :
        ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.avalonia-options";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Avalonia.Where(rule =>
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.MarkupExtensionOption,
                    StringComparison.Ordinal) ||
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.MarkupExtensionDefaultOption,
                    StringComparison.Ordinal)).ToArray();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy =>
            new ProGPU.Xaml.Schema.XamlSymbolShapePolicy(
                markupExtensionOptionSelectorMethodNames:
                    new[] { "ShouldProvideOption" },
                markupExtensionOptionSelectorServiceProviderTypeMetadataNames:
                    new[] { "System.IServiceProvider" });
    }

    private sealed class AvaloniaListMetadataProvider :
        ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.avalonia-list";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Avalonia.Where(rule =>
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.ListSeparator,
                    StringComparison.Ordinal)).ToArray();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy =>
            ProGPU.Xaml.Schema.XamlSymbolShapePolicy.Default;
    }

    private sealed class BindingAnnotationMetadataProvider :
        ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.avalonia-binding-annotations";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Avalonia.Where(rule =>
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.DataType,
                    StringComparison.Ordinal) ||
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.InheritDataType,
                    StringComparison.Ordinal) ||
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.InheritDataTypeFromItems,
                    StringComparison.Ordinal) ||
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.AssignBinding,
                    StringComparison.Ordinal)).ToArray();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy =>
            ProGPU.Xaml.Schema.XamlSymbolShapePolicy.Default;
    }

    private sealed class ToolingAnnotationMetadataProvider :
        ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.template-tooling-annotations";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Wpf.Where(rule =>
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.AttachedPropertyBrowseRule,
                    StringComparison.Ordinal))
            .Concat(ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Avalonia.Where(rule =>
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.TemplatePart,
                    StringComparison.Ordinal)))
            .ToArray();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy =>
            ProGPU.Xaml.Schema.XamlSymbolShapePolicy.Default;
    }

    private sealed class DesignerMetadataProvider :
        ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.designer-annotations";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule>
            AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Wpf.Where(rule =>
                    string.Equals(
                        rule.Semantic,
                        ProGPU.Xaml.Schema.XamlSchemaSemantics.DesignerSerializationOptions,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        rule.Semantic,
                        ProGPU.Xaml.Schema.XamlSchemaSemantics.Localizability,
                        StringComparison.Ordinal))
                .Concat(new[]
                {
                    new ProGPU.Xaml.Schema.XamlSchemaAttributeRule(
                        "Meta.InvalidDefaultAttribute",
                        ProGPU.Xaml.Schema.XamlSchemaSemantics.DefaultValue,
                        ProGPU.Xaml.Schema.XamlSchemaAttributeTargets.Member,
                        inherited: true)
                })
                .ToArray();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy =>
            ProGPU.Xaml.Schema.XamlSymbolShapePolicy.Default;
    }

    private sealed class BindingAnnotationEmissionProfile :
        ProGPU.Xaml.Roslyn.IRoslynXamlFrameworkProfile,
        ProGPU.Xaml.Roslyn.IRoslynXamlBindingAssignmentProfile
    {
        private readonly WinUiXamlProfile _inner = new();

        public string Id => "AvaloniaBindingAnnotationTest";
        public int ContractVersion => _inner.ContractVersion;
        public ProGPU.Xaml.Schema.XamlFrameworkCapabilities Capabilities =>
            _inner.Capabilities;
        public IReadOnlyList<string> FileExtensions => _inner.FileExtensions;
        public IReadOnlyList<string> GetClrNamespaceCandidates(string xamlNamespaceUri) =>
            _inner.GetClrNamespaceCandidates(xamlNamespaceUri);
        public bool TryCreateLiteralExpression(
            ProGPU.Xaml.Schema.XamlTypeInfo targetType,
            string text,
            out ExpressionSyntax expression) =>
            _inner.TryCreateLiteralExpression(targetType, text, out expression);
        public bool TryCreateMarkupExtensionExpression(
            ProGPU.Xaml.Parsing.XamlMarkupExtension extension,
            ProGPU.Xaml.Schema.XamlTypeInfo targetType,
            out ExpressionSyntax expression) =>
            _inner.TryCreateMarkupExtensionExpression(extension, targetType, out expression);
        public bool TryCreateMarkupExtensionExpression(
            XamlIrObject extension,
            ProGPU.Xaml.Schema.XamlTypeInfo targetType,
            out ExpressionSyntax expression) =>
            _inner.TryCreateMarkupExtensionExpression(extension, targetType, out expression);
        public bool TryCreateResourceReferenceExpression(
            XamlIrResourceReference reference,
            ProGPU.Xaml.Schema.XamlTypeInfo targetType,
            ExpressionSyntax lookupRoot,
            ExpressionSyntax resourceKey,
            out ExpressionSyntax expression) =>
            _inner.TryCreateResourceReferenceExpression(
                reference,
                targetType,
                lookupRoot,
                resourceKey,
                out expression);

        public bool TryCreateBindingObjectAssignment(
            XamlIrObject binding,
            ProGPU.Xaml.Schema.XamlBindingAssignmentInfo assignment,
            ProGPU.Xaml.Schema.XamlMemberInfo member,
            ExpressionSyntax bindingInstance,
            ExpressionSyntax receiver,
            out StatementSyntax statement)
        {
            statement = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        receiver,
                        SyntaxFactory.IdentifierName(member.CSharpName)),
                    bindingInstance));
            return true;
        }
    }

    private sealed class OptionEmissionProfile :
        ProGPU.Xaml.Roslyn.IRoslynXamlFrameworkProfile,
        ProGPU.Xaml.Roslyn.IRoslynXamlMarkupExtensionOptionProfile
    {
        private readonly WinUiXamlProfile _inner = new();

        public string Id => "AvaloniaOptionTest";
        public int ContractVersion => _inner.ContractVersion;
        public ProGPU.Xaml.Schema.XamlFrameworkCapabilities Capabilities =>
            _inner.Capabilities;
        public IReadOnlyList<string> FileExtensions => _inner.FileExtensions;
        public IReadOnlyList<string> GetClrNamespaceCandidates(string xamlNamespaceUri) =>
            _inner.GetClrNamespaceCandidates(xamlNamespaceUri);
        public bool TryCreateLiteralExpression(
            ProGPU.Xaml.Schema.XamlTypeInfo targetType,
            string text,
            out ExpressionSyntax expression) =>
            _inner.TryCreateLiteralExpression(targetType, text, out expression);
        public bool TryCreateMarkupExtensionExpression(
            ProGPU.Xaml.Parsing.XamlMarkupExtension extension,
            ProGPU.Xaml.Schema.XamlTypeInfo targetType,
            out ExpressionSyntax expression) =>
            _inner.TryCreateMarkupExtensionExpression(extension, targetType, out expression);
        public bool TryCreateMarkupExtensionExpression(
            XamlIrObject extension,
            ProGPU.Xaml.Schema.XamlTypeInfo targetType,
            out ExpressionSyntax expression) =>
            _inner.TryCreateMarkupExtensionExpression(extension, targetType, out expression);
        public bool TryCreateResourceReferenceExpression(
            XamlIrResourceReference reference,
            ProGPU.Xaml.Schema.XamlTypeInfo targetType,
            ExpressionSyntax lookupRoot,
            ExpressionSyntax resourceKey,
            out ExpressionSyntax expression) =>
            _inner.TryCreateResourceReferenceExpression(
                reference,
                targetType,
                lookupRoot,
                resourceKey,
                out expression);

        public bool TryCreateMarkupExtensionOptionExpression(
            XamlIrObject extension,
            ProGPU.Xaml.Schema.XamlMarkupExtensionOptionSelectorShapeInfo selector,
            ProGPU.Xaml.Schema.XamlTypeInfo targetType,
            Func<XamlIrValue, ProGPU.Xaml.Schema.XamlTypeInfo, ExpressionSyntax?> emitValue,
            ExpressionSyntax? targetObject,
            ProGPU.Xaml.Schema.XamlMemberInfo? targetMember,
            ExpressionSyntax rootObject,
            string? resourceUri,
            out ExpressionSyntax expression)
        {
            ExpressionSyntax? fallback = null;
            var keyed = new List<(ProGPU.Xaml.Schema.XamlMarkupExtensionOptionInfo Option, ExpressionSyntax Value)>();
            foreach (var operation in extension.Operations)
            {
                var option = operation.Member.Symbol?.MarkupExtensionOption;
                if (option == null || !option.IsValid || operation.Values.Length != 1)
                    continue;
                var value = emitValue(operation.Values[0], targetType);
                if (value == null) continue;
                if (option.IsDefault)
                    fallback = value;
                else
                    keyed.Add((option, value));
            }
            if (fallback == null ||
                keyed.Any(item => item.Option.OptionValue?.Value is not string))
            {
                expression = null!;
                return false;
            }

            expression = fallback;
            for (var index = keyed.Count - 1; index >= 0; index--)
            {
                var item = keyed[index];
                var condition = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            CreateGlobalTypeName(selector.Method!.ContainingType),
                            SyntaxFactory.IdentifierName(selector.Method.Name)))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(
                                SyntaxFactory.LiteralExpression(
                                    SyntaxKind.StringLiteralExpression,
                                    SyntaxFactory.Literal(
                                        (string)item.Option.OptionValue!.Value.Value!))))));
                expression = SyntaxFactory.ConditionalExpression(
                    condition,
                    item.Value,
                    expression);
            }
            return true;
        }

        private static NameSyntax CreateGlobalTypeName(INamedTypeSymbol type)
        {
            var names = new List<string>();
            for (var current = type.ContainingNamespace;
                 current is { IsGlobalNamespace: false };
                 current = current.ContainingNamespace)
                names.Add(current.Name);
            names.Reverse();
            names.Add(type.Name);
            NameSyntax result = SyntaxFactory.AliasQualifiedName(
                SyntaxFactory.IdentifierName(
                    SyntaxFactory.Token(SyntaxKind.GlobalKeyword)),
                SyntaxFactory.IdentifierName(names[0]));
            for (var index = 1; index < names.Count; index++)
                result = SyntaxFactory.QualifiedName(
                    result,
                    SyntaxFactory.IdentifierName(names[index]));
            return result;
        }
    }

    private sealed class AmbiguousMarkupShapeProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.markup-ambiguity";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            Array.Empty<ProGPU.Xaml.Schema.XamlSchemaAttributeRule>();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; } = new(
            markupExtensionSuffixes: new[] { "Extension" },
            markupExtensionProvideValueMethodNames: new[] { "ProvideValue", "CreateValue" });
    }

    private sealed class InterfaceMarkupShapeProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.markup-interface";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            Array.Empty<ProGPU.Xaml.Schema.XamlSchemaAttributeRule>();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; } = new(
            markupExtensionInterfaceMetadataNames: new[]
            {
                "Demo.IMarkupExtension",
                "Demo.IMarkupExtension`1"
            },
            markupExtensionProvideValueMethodNames: new[] { "ProvideValue" },
            markupExtensionServiceProviderTypeMetadataNames: new[]
            {
                "Microsoft.UI.Xaml.IXamlServiceProvider"
            });
    }

    private sealed class MauiMarkupShapeProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.markup-maui";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Maui;
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; } = new(
            markupExtensionInterfaceMetadataNames: new[]
            {
                "Microsoft.Maui.Controls.Xaml.IMarkupExtension",
                "Microsoft.Maui.Controls.Xaml.IMarkupExtension`1"
            },
            markupExtensionProvideValueMethodNames: new[] { "ProvideValue" },
            markupExtensionServiceProviderTypeMetadataNames: new[]
            {
                "System.IServiceProvider"
            },
            markupExtensionAvailableServiceTypeMetadataNames: new[]
            {
                "Demo.IAvailableService"
            },
            requireMarkupExtensionServiceDeclaration: true);
    }

    private sealed class ConflictingWinUiMarkupShapeProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.winui-markup-conflict";
        public int MetadataPriority => 100;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            Array.Empty<ProGPU.Xaml.Schema.XamlSchemaAttributeRule>();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; } = new(
            markupExtensionSuffixes: new[] { "Extension" },
            markupExtensionProvideValueMethodNames: new[] { "ProvideValue" },
            markupExtensionServiceProviderTypeMetadataNames: new[]
            {
                "Microsoft.UI.Xaml.IXamlServiceProvider"
            });
    }

    private sealed class EquivalentMarkupShapeProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public EquivalentMarkupShapeProvider(string id)
        {
            MetadataProviderId = id;
        }

        public string MetadataProviderId { get; }
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            Array.Empty<ProGPU.Xaml.Schema.XamlSchemaAttributeRule>();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; } = new(
            markupExtensionSuffixes: new[] { "Extension" },
            markupExtensionProvideValueMethodNames: new[] { "ProvideValue" },
            markupExtensionServiceProviderTypeMetadataNames: new[] { "System.Int32" });
    }

    private sealed class ConflictingCompositionProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public ConflictingCompositionProvider(
            string id,
            string runtimeNameFallback,
            string implicitStyleKeyMember,
            int priority = 2000)
        {
            MetadataProviderId = id;
            MetadataPriority = priority;
            SymbolShapePolicy = new ProGPU.Xaml.Schema.XamlSymbolShapePolicy(
                runtimeNameFallback: runtimeNameFallback,
                implicitDictionaryKeyMembers: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Microsoft.UI.Xaml.Style"] = implicitStyleKeyMember
                });
        }

        public string MetadataProviderId { get; }
        public int MetadataPriority { get; }
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            Array.Empty<ProGPU.Xaml.Schema.XamlSchemaAttributeRule>();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; }
    }

    private sealed class AllConflictingShapeFamiliesProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public AllConflictingShapeFamiliesProvider(string id, bool first)
        {
            MetadataProviderId = id;
            SymbolShapePolicy = new ProGPU.Xaml.Schema.XamlSymbolShapePolicy(
                attachedGetterPrefix: first ? "GetFirst" : "GetSecond",
                attachedSetterPrefix: first ? "SetFirst" : "SetSecond",
                runtimeNameFallback: first ? "FirstName" : "SecondName",
                inferCollectionsFromAddMethods: first,
                propertyIdentifierSuffix: first ? "FirstProperty" : "SecondProperty",
                propertyIdentifierTypeMetadataName: first
                    ? "Demo.FirstPropertyIdentifier"
                    : "Demo.SecondPropertyIdentifier",
                propertySetterMethodName: first ? "SetFirstValue" : "SetSecondValue",
                implicitDictionaryKeyMembers: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Microsoft.UI.Xaml.Style"] = first ? "TargetType" : "OtherKey"
                },
                resourceMemberRoles:
                    new Dictionary<string, ProGPU.Xaml.Schema.XamlResourceMemberRole>(StringComparer.Ordinal)
                    {
                        ["Resources"] = first
                            ? ProGPU.Xaml.Schema.XamlResourceMemberRole.LexicalResources
                            : ProGPU.Xaml.Schema.XamlResourceMemberRole.Source
                    },
                pseudoContentMembers:
                    new Dictionary<string, ProGPU.Xaml.Schema.XamlPseudoMemberDefinition>(StringComparer.Ordinal)
                    {
                        ["Microsoft.UI.Xaml.FrameworkTemplate"] =
                            new ProGPU.Xaml.Schema.XamlPseudoMemberDefinition(
                                first ? "FirstTemplate" : "SecondTemplate",
                                "System.Object",
                                ProGPU.Xaml.Schema.XamlMemberKind.DeferredContent,
                                first ? "tests.first-template" : "tests.second-template")
                    },
                inferGetterOnlyAttachedCollections: first);
        }

        public string MetadataProviderId { get; }
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            Array.Empty<ProGPU.Xaml.Schema.XamlSchemaAttributeRule>();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; }
    }

    private sealed class UnrelatedSetHandlerShapeProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.unrelated-set-handler";
        public int MetadataPriority => 3000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            Array.Empty<ProGPU.Xaml.Schema.XamlSchemaAttributeRule>();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; } = new(
            setValueHandlerEventArgsTypeMetadataNames:
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ProGPU.Xaml.Schema.XamlSchemaSemantics.SetMarkupExtensionHandler] =
                        "System.EventArgs",
                    [ProGPU.Xaml.Schema.XamlSchemaSemantics.SetTypeConverterHandler] =
                        "System.EventArgs"
                });
    }

    private sealed class ValueSerializerMetadataProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.value-serializer";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Wpf.Where(rule =>
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.ValueSerializer,
                    StringComparison.Ordinal)).ToArray();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; } = new(
            valueSerializerBaseTypeMetadataNames: new[]
            {
                "System.Windows.Markup.ValueSerializer"
            },
            valueSerializerContextTypeMetadataNames: new[]
            {
                "System.Windows.Markup.IValueSerializerContext"
            },
            valueSerializerCanConvertToStringMethodNames: new[]
            {
                "CanConvertToString"
            },
            valueSerializerConvertToStringMethodNames: new[]
            {
                "ConvertToString"
            });
    }

    private sealed class WhitespaceMetadataProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.whitespace";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Wpf.Where(rule =>
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.TrimSurroundingWhitespace,
                    StringComparison.Ordinal) ||
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.WhitespaceSignificantCollection,
                    StringComparison.Ordinal) ||
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.UsableDuringInitialization,
                    StringComparison.Ordinal)).ToArray();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy =>
            ProGPU.Xaml.Schema.XamlSymbolShapePolicy.Default;
    }

    private sealed class ContentWrapperMetadataProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.content-wrapper";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Wpf.Where(rule =>
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.ContentWrapper,
                    StringComparison.Ordinal)).ToArray();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy =>
            ProGPU.Xaml.Schema.XamlSymbolShapePolicy.Default;
    }

    private sealed class ConstructorArgumentMetadataProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.constructor-argument";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Wpf.Where(rule =>
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.ConstructorArgument,
                    StringComparison.Ordinal)).ToArray();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy =>
            ProGPU.Xaml.Schema.XamlSymbolShapePolicy.Default;
    }

    private sealed class AmbientMetadataProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.ambient";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Wpf.Where(rule =>
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.Ambient,
                    StringComparison.Ordinal)).ToArray();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy =>
            ProGPU.Xaml.Schema.XamlSymbolShapePolicy.Default;
    }

    private sealed class AmbientExplicitResourceRoleMetadataProvider :
        ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.ambient-explicit-resource-role";
        public int MetadataPriority => 3000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Wpf.Where(rule =>
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.Ambient,
                    StringComparison.Ordinal)).ToArray();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; } = new(
            resourceMemberRoles:
                new Dictionary<string, ProGPU.Xaml.Schema.XamlResourceMemberRole>(
                    StringComparer.Ordinal)
                {
                    ["TypedContext"] = ProGPU.Xaml.Schema.XamlResourceMemberRole.Source
                });
    }

    private sealed class DeferredLoadMetadataProvider :
        ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.deferred-load";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Wpf.Where(rule =>
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.DeferredLoad,
                    StringComparison.Ordinal)).ToArray();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy =>
            ProGPU.Xaml.Schema.XamlSymbolShapePolicy.Default;
    }

    private sealed class MarkupBracketMetadataProvider :
        ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.markup-brackets";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Wpf.Where(rule =>
                string.Equals(
                    rule.Semantic,
                    ProGPU.Xaml.Schema.XamlSchemaSemantics.MarkupExtensionBracketCharacters,
                    StringComparison.Ordinal)).ToArray();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy =>
            ProGPU.Xaml.Schema.XamlSymbolShapePolicy.Default;
    }

    private sealed class ObjectWriterHandlerMetadataProvider : ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.object-writer-handlers";
        public int MetadataPriority => 2000;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules { get; } =
            ProGPU.Xaml.Roslyn.XamlSchemaAttributeCatalog.Wpf;
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; } = new(
            setValueHandlerEventArgsTypeMetadataNames:
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ProGPU.Xaml.Schema.XamlSchemaSemantics.SetMarkupExtensionHandler] =
                        "System.Windows.Markup.XamlSetMarkupExtensionEventArgs",
                    [ProGPU.Xaml.Schema.XamlSchemaSemantics.SetTypeConverterHandler] =
                        "System.Windows.Markup.XamlSetTypeConverterEventArgs"
                });
    }

    private sealed class MarkupExtensionReceiverMetadataProvider :
        ProGPU.Xaml.Schema.IXamlSchemaMetadataProvider
    {
        public string MetadataProviderId => "tests.markup-extension-receivers";
        public int MetadataPriority => 2100;
        public IReadOnlyList<ProGPU.Xaml.Schema.XamlSchemaAttributeRule> AttributeRules =>
            Array.Empty<ProGPU.Xaml.Schema.XamlSchemaAttributeRule>();
        public ProGPU.Xaml.Schema.XamlSymbolShapePolicy SymbolShapePolicy { get; } = new(
            markupExtensionReceiverInterfaceMetadataNames:
                new[] { "System.Windows.Markup.IReceiveMarkupExtension" },
            markupExtensionReceiverMethodNames:
                new[] { "ReceiveMarkupExtension", "AcceptMarkup" },
            markupExtensionReceiverMarkupExtensionTypeMetadataNames:
                new[] { "System.Windows.Markup.MarkupExtension" },
            markupExtensionReceiverServiceProviderTypeMetadataNames:
                new[] { "System.IServiceProvider" },
            inferMarkupExtensionReceiversFromMethods: true);
    }

    private sealed class ObjectWriterEmissionProfile :
        ProGPU.Xaml.Roslyn.IRoslynXamlFrameworkProfile,
        ProGPU.Xaml.Roslyn.IRoslynXamlSetValueHandlerProfile,
        ProGPU.Xaml.Roslyn.IRoslynXamlMarkupExtensionReceiverProfile
    {
        private readonly WinUiXamlProfile _inner = new();

        public string Id => "ObjectWriterTest";
        public int ContractVersion => _inner.ContractVersion;
        public ProGPU.Xaml.Schema.XamlFrameworkCapabilities Capabilities =>
            _inner.Capabilities;
        public IReadOnlyList<string> FileExtensions => _inner.FileExtensions;
        public IReadOnlyList<string> GetClrNamespaceCandidates(string xamlNamespaceUri) =>
            _inner.GetClrNamespaceCandidates(xamlNamespaceUri);
        public bool TryCreateLiteralExpression(
            ProGPU.Xaml.Schema.XamlTypeInfo targetType,
            string text,
            out ExpressionSyntax expression) =>
            _inner.TryCreateLiteralExpression(targetType, text, out expression);
        public bool TryCreateMarkupExtensionExpression(
            ProGPU.Xaml.Parsing.XamlMarkupExtension extension,
            ProGPU.Xaml.Schema.XamlTypeInfo targetType,
            out ExpressionSyntax expression) =>
            _inner.TryCreateMarkupExtensionExpression(extension, targetType, out expression);
        public bool TryCreateMarkupExtensionExpression(
            XamlIrObject extension,
            ProGPU.Xaml.Schema.XamlTypeInfo targetType,
            out ExpressionSyntax expression) =>
            _inner.TryCreateMarkupExtensionExpression(extension, targetType, out expression);
        public bool TryCreateResourceReferenceExpression(
            XamlIrResourceReference reference,
            ProGPU.Xaml.Schema.XamlTypeInfo targetType,
            ExpressionSyntax lookupRoot,
            ExpressionSyntax resourceKey,
            out ExpressionSyntax expression) =>
            _inner.TryCreateResourceReferenceExpression(
                reference,
                targetType,
                lookupRoot,
                resourceKey,
                out expression);

        public bool TryCreateMarkupExtensionSetHandlerAssignment(
            ProGPU.Xaml.Schema.XamlSetValueHandlerShapeInfo handler,
            XamlIrObject extension,
            ExpressionSyntax extensionInstance,
            ProGPU.Xaml.Schema.XamlMemberInfo member,
            ExpressionSyntax receiver,
            ExpressionSyntax rootObject,
            string? resourceUri,
            out StatementSyntax statement)
        {
            statement = InvocationStatement(
                RuntimeMember("ApplyMarkupExtension"),
                receiver,
                extensionInstance,
                HandlerMember(handler),
                StringLiteral(member.Name),
                OptionalStringLiteral(resourceUri));
            return true;
        }

        public bool TryCreateTypeConverterSetHandlerAssignment(
            ProGPU.Xaml.Schema.XamlSetValueHandlerShapeInfo handler,
            XamlIrText value,
            INamedTypeSymbol converterType,
            ProGPU.Xaml.Schema.XamlMemberInfo member,
            ExpressionSyntax receiver,
            ExpressionSyntax rootObject,
            string? resourceUri,
            out StatementSyntax statement)
        {
            statement = InvocationStatement(
                RuntimeMember("ApplyTypeConverter"),
                receiver,
                SyntaxFactory.TypeOfExpression(CreateGlobalTypeName(converterType)),
                StringLiteral(value.Text),
                HandlerMember(handler),
                StringLiteral(member.Name),
                OptionalStringLiteral(resourceUri));
            return true;
        }

        public bool TryCreateMarkupExtensionReceiverAssignment(
            ProGPU.Xaml.Schema.XamlMarkupExtensionReceiverShapeInfo receiverShape,
            XamlIrObject extension,
            ExpressionSyntax extensionInstance,
            ProGPU.Xaml.Schema.XamlMemberInfo member,
            ExpressionSyntax receiver,
            ExpressionSyntax rootObject,
            string? resourceUri,
            out StatementSyntax statement)
        {
            var target = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiverShape.IdentityKind ==
                    ProGPU.Xaml.Schema.XamlMarkupExtensionReceiverIdentityKind.Interface
                    ? SyntaxFactory.ParenthesizedExpression(
                        SyntaxFactory.CastExpression(
                            CreateGlobalTypeName((INamedTypeSymbol)receiverShape.IdentityType!),
                            receiver))
                    : receiver,
                SyntaxFactory.IdentifierName(receiverShape.ReceiveMethod!.Name));
            statement = InvocationStatement(
                target,
                StringLiteral(member.Name),
                SyntaxFactory.CastExpression(
                    CreateGlobalTypeName(
                        (INamedTypeSymbol)receiverShape.MarkupExtensionType!),
                    extensionInstance),
                SyntaxFactory.CastExpression(
                    CreateGlobalTypeName(
                        (INamedTypeSymbol)receiverShape.ServiceProviderType!),
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
            return true;
        }

        private static StatementSyntax InvocationStatement(
            ExpressionSyntax target,
            params ExpressionSyntax[] arguments) =>
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(target).WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(arguments.Select(SyntaxFactory.Argument)))));

        private static ExpressionSyntax RuntimeMember(string name) =>
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                CreateGlobalName("Demo", "ObjectWriterRuntime"),
                SyntaxFactory.IdentifierName(name));

        private static ExpressionSyntax HandlerMember(
            ProGPU.Xaml.Schema.XamlSetValueHandlerShapeInfo handler) =>
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                CreateGlobalTypeName(handler.Handler!.ContainingType),
                SyntaxFactory.IdentifierName(handler.Handler.Name));

        private static NameSyntax CreateGlobalTypeName(INamedTypeSymbol type)
        {
            if (type.ContainingType != null)
                return SyntaxFactory.QualifiedName(
                    CreateGlobalTypeName(type.ContainingType),
                    SyntaxFactory.IdentifierName(type.Name));
            var segments = new List<string>();
            for (var current = type.ContainingNamespace;
                 current is { IsGlobalNamespace: false };
                 current = current.ContainingNamespace)
                segments.Add(current.Name);
            segments.Reverse();
            segments.Add(type.Name);
            return CreateGlobalName(segments.ToArray());
        }

        private static NameSyntax CreateGlobalName(params string[] segments)
        {
            NameSyntax result = SyntaxFactory.AliasQualifiedName(
                SyntaxFactory.IdentifierName(
                    SyntaxFactory.Token(SyntaxKind.GlobalKeyword)),
                SyntaxFactory.IdentifierName(segments[0]));
            for (var index = 1; index < segments.Length; index++)
                result = SyntaxFactory.QualifiedName(result, SyntaxFactory.IdentifierName(segments[index]));
            return result;
        }

        private static LiteralExpressionSyntax StringLiteral(string value) =>
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(value));

        private static ExpressionSyntax OptionalStringLiteral(string? value) =>
            value == null
                ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                : StringLiteral(value);
    }

    private sealed class MissingDeferredBindingLifecycleProfile :
        IRoslynXamlFrameworkProfile,
        IRoslynXamlDeferredContentProfile,
        IRoslynXamlCompiledBindingAssignmentProfile
    {
        private readonly WinUiXamlProfile _inner = new();

        public string Id => "MissingDeferredBindingLifecycle";
        public int ContractVersion => _inner.ContractVersion;
        public XamlFrameworkCapabilities Capabilities => _inner.Capabilities;
        public IReadOnlyList<string> FileExtensions => _inner.FileExtensions;

        public IReadOnlyList<string> GetClrNamespaceCandidates(
            string xamlNamespaceUri) =>
            _inner.GetClrNamespaceCandidates(xamlNamespaceUri);

        public bool TryCreateLiteralExpression(
            XamlTypeInfo targetType,
            string text,
            out ExpressionSyntax expression) =>
            _inner.TryCreateLiteralExpression(targetType, text, out expression);

        public bool TryCreateMarkupExtensionExpression(
            XamlMarkupExtension extension,
            XamlTypeInfo targetType,
            out ExpressionSyntax expression) =>
            _inner.TryCreateMarkupExtensionExpression(
                extension,
                targetType,
                out expression);

        public bool TryCreateMarkupExtensionExpression(
            XamlIrObject extension,
            XamlTypeInfo targetType,
            out ExpressionSyntax expression) =>
            _inner.TryCreateMarkupExtensionExpression(
                extension,
                targetType,
                out expression);

        public bool TryCreateResourceReferenceExpression(
            XamlIrResourceReference reference,
            XamlTypeInfo targetType,
            ExpressionSyntax lookupRoot,
            ExpressionSyntax resourceKey,
            out ExpressionSyntax expression) =>
            _inner.TryCreateResourceReferenceExpression(
                reference,
                targetType,
                lookupRoot,
                resourceKey,
                out expression);

        public bool TryCreateDeferredContentAssignment(
            XamlMemberInfo member,
            ExpressionSyntax receiver,
            ParenthesizedLambdaExpressionSyntax factory,
            out StatementSyntax statement) =>
            _inner.TryCreateDeferredContentAssignment(
                member,
                receiver,
                factory,
                out statement);

        public bool TryCreateCompiledBindingAssignment(
            XamlIrCompiledBinding binding,
            XamlMemberInfo member,
            ExpressionSyntax receiver,
            ExpressionSyntax source,
            ExpressionSyntax lookupRoot,
            ExpressionSyntax? bindingOwner,
            out StatementSyntax statement) =>
            _inner.TryCreateCompiledBindingAssignment(
                binding,
                member,
                receiver,
                source,
                lookupRoot,
                bindingOwner,
                out statement);
    }

    private static CSharpCompilation CreateCompilation() => CSharpCompilation.Create(
        "BindingHarness",
        new[] { CSharpSyntaxTree.ParseText(Framework) },
        PlatformReferences(),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static IEnumerable<MetadataReference> PlatformReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        return trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
    }
}
