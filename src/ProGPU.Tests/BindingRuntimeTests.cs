using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Xunit;

namespace ProGPU.Tests;

public sealed class BindingRuntimeTests
{
    [Fact]
    public void DependencyPropertyPathTracksSourceAndDataContextReplacement()
    {
        var first = new BindingSource { Value = "first" };
        var second = new BindingSource { Value = "second" };
        var target = new TextBlock { DataContext = first };

        var expression = BindingOperations.SetBinding(
            target,
            TextBlock.TextProperty,
            new Binding { Path = nameof(BindingSource.Value) });

        Assert.Equal("first", target.Text);
        Assert.Equal(BindingExpressionStatus.Active, expression.Status);

        first.Value = "updated";
        Assert.Equal("updated", target.Text);

        target.DataContext = second;
        Assert.Equal("second", target.Text);
        first.Value = "stale";
        Assert.Equal("second", target.Text);

        second.Value = "current";
        Assert.Equal("current", target.Text);
    }

    [Fact]
    public void TypedClrAccessorSupportsNestedOneWayAndTwoWayUpdates()
    {
        BindingMemberAccessorRegistry.Register<BindingViewModel, BindingChild>(
            nameof(BindingViewModel.Child),
            static source => source.Child,
            static (source, value) => source.Child = value);
        BindingMemberAccessorRegistry.Register<BindingChild, string?>(
            nameof(BindingChild.Name),
            static source => source.Name,
            static (source, value) => source.Name = value);

        var child = new BindingChild { Name = "alpha" };
        var viewModel = new BindingViewModel { Child = child };
        var target = new TextBlock();
        var expression = BindingOperations.SetBinding(
            target,
            TextBlock.TextProperty,
            new Binding
            {
                Source = viewModel,
                Path = "Child.Name",
                Mode = BindingMode.TwoWay
            });

        Assert.Equal("alpha", target.Text);

        child.Name = "beta";
        Assert.Equal("beta", target.Text);

        target.Text = "gamma";
        Assert.Equal("gamma", child.Name);
        Assert.Equal(BindingExpressionStatus.Active, expression.Status);

        var replacement = new BindingChild { Name = "replacement" };
        viewModel.Child = replacement;
        Assert.Equal("replacement", target.Text);
        child.Name = "detached";
        Assert.Equal("replacement", target.Text);
    }

    [Fact]
    public void OrdinaryBindingTypedIndexersTrackAndWriteWithoutRuntimeParsing()
    {
        BindingMemberAccessorRegistry.RegisterIndexer<
            ObservableCollection<BindingChild>,
            BindingChild>(
            0,
            static values => values[0],
            static (values, value) => values[0] = value);
        BindingMemberAccessorRegistry.Register<BindingChild, string?>(
            nameof(BindingChild.Name),
            static source => source.Name,
            static (source, value) => source.Name = value);

        var first = new BindingChild { Name = "first" };
        var values = new ObservableCollection<BindingChild> { first };
        var target = new TextBlock();
        var expression = BindingOperations.SetBindingWithPath(
            target,
            nameof(TextBlock.Text),
            new Binding
            {
                Source = values,
                Path = "[0].Name",
                Mode = BindingMode.TwoWay
            },
            new[]
            {
                BindingPathSegment.Indexer(0),
                BindingPathSegment.Member(nameof(BindingChild.Name))
            });

        Assert.Equal("first", target.Text);
        first.Name = "item";
        Assert.Equal("item", target.Text);

        var replacement = new BindingChild { Name = "replacement" };
        values[0] = replacement;
        Assert.Equal("replacement", target.Text);
        first.Name = "detached";
        Assert.Equal("replacement", target.Text);

        target.Text = "written";
        Assert.Equal("written", replacement.Name);
        Assert.Equal(BindingExpressionStatus.Active, expression.Status);

        BindingMemberAccessorRegistry.RegisterIndexer<
            Dictionary<string, BindingChild>,
            BindingChild>(
            "primary",
            static entries => entries["primary"],
            static (entries, value) => entries["primary"] = value);
        var entries = new Dictionary<string, BindingChild>
        {
            ["primary"] = new BindingChild { Name = "dictionary" }
        };
        var dictionaryTarget = new TextBlock();
        BindingOperations.SetBindingWithPath(
            dictionaryTarget,
            nameof(TextBlock.Text),
            new Binding
            {
                Source = entries,
                Path = "['primary'].Name",
                Mode = BindingMode.TwoWay
            },
            new[]
            {
                BindingPathSegment.Indexer("primary"),
                BindingPathSegment.Member(nameof(BindingChild.Name))
            });

        Assert.Equal("dictionary", dictionaryTarget.Text);
        dictionaryTarget.Text = "dictionary-written";
        Assert.Equal("dictionary-written", entries["primary"].Name);
    }

    [Fact]
    public void ExplicitTwoWayConverterFallbackAndTargetNullAreApplied()
    {
        BindingMemberAccessorRegistry.Register<NumericViewModel, int>(
            nameof(NumericViewModel.Value),
            static source => source.Value,
            static (source, value) => source.Value = value);
        var viewModel = new NumericViewModel { Value = 12 };
        var target = new TextBlock();
        var binding = new Binding
        {
            Source = viewModel,
            Path = nameof(NumericViewModel.Value),
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.Explicit,
            Converter = new IntegerTextConverter()
        };
        var expression = BindingOperations.SetBinding(
            target,
            TextBlock.TextProperty,
            binding);

        Assert.Equal("#12", target.Text);
        target.Text = "#34";
        Assert.Equal(12, viewModel.Value);
        expression.UpdateSource();
        Assert.Equal(34, viewModel.Value);

        var missing = new TextBlock();
        var missingExpression = BindingOperations.SetBinding(
            missing,
            TextBlock.TextProperty,
            new Binding
            {
                Source = viewModel,
                Path = "Missing",
                FallbackValue = "fallback"
            });
        Assert.Equal("fallback", missing.Text);
        Assert.Equal(BindingExpressionStatus.PathError, missingExpression.Status);

        BindingMemberAccessorRegistry.Register<NullableViewModel, string?>(
            nameof(NullableViewModel.Value),
            static source => source.Value);
        var nullable = new TextBlock();
        BindingOperations.SetBinding(
            nullable,
            TextBlock.TextProperty,
            new Binding
            {
                Source = new NullableViewModel(),
                Path = nameof(NullableViewModel.Value),
                TargetNullValue = "null-value"
            });
        Assert.Equal("null-value", nullable.Text);
    }

    [Fact]
    public void ElementNameBindingUsesExplicitLookupRootAndClearDetaches()
    {
        var root = new Grid();
        var source = new TextBlock { Name = "SourceText", Text = "one" };
        var target = new TextBlock();
        root.Children.Add(source);
        root.Children.Add(target);

        BindingOperations.SetBinding(
            target,
            TextBlock.TextProperty,
            new Binding
            {
                ElementName = "SourceText",
                Path = nameof(TextBlock.Text)
            },
            lookupRoot: root);
        Assert.Equal("one", target.Text);

        source.Text = "two";
        Assert.Equal("two", target.Text);

        BindingOperations.ClearBinding(target, TextBlock.TextProperty);
        Assert.Null(BindingOperations.GetBinding(target, TextBlock.TextProperty));
        source.Text = "three";
        Assert.Equal(string.Empty, target.Text);
    }

    [Fact]
    public void LostFocusTwoWayBindingDefersSourceUpdate()
    {
        BindingMemberAccessorRegistry.Register<BindingChild, string?>(
            nameof(BindingChild.Name),
            static source => source.Name,
            static (source, value) => source.Name = value);
        var source = new BindingChild { Name = "initial" };
        var target = new TextBox();
        BindingOperations.SetBinding(
            target,
            TextBox.TextProperty,
            new Binding
            {
                Source = source,
                Path = nameof(BindingChild.Name),
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
            });

        try
        {
            InputSystem.SetFocus(target);
            target.Text = "edited";
            Assert.Equal("initial", source.Name);

            InputSystem.SetFocus(null);
            Assert.Equal("edited", source.Name);
        }
        finally
        {
            InputSystem.SetFocus(null);
        }
    }

    [Fact]
    public void CompiledBindingTracksNestedTypedPathAndSupportsLifecycle()
    {
        var original = new BindingChild { Name = "alpha" };
        var source = new BindingViewModel { Child = original };
        var target = new TextBlock();
        var path = new ICompiledBindingPathSegment[]
        {
            new CompiledBindingPathSegment<BindingViewModel, BindingChild>(
                nameof(BindingViewModel.Child),
                static value => value.Child,
                static (value, child) => value.Child = child),
            new CompiledBindingPathSegment<BindingChild, string?>(
                nameof(BindingChild.Name),
                static value => value.Name,
                static (value, name) => value.Name = name)
        };

        var expression = CompiledBindingOperations.SetBinding(
            target,
            TextBlock.TextProperty,
            source,
            path,
            new CompiledBindingOptions { Mode = BindingMode.TwoWay });

        Assert.Equal("alpha", target.Text);
        original.Name = "beta";
        Assert.Equal("beta", target.Text);

        var replacement = new BindingChild { Name = "replacement" };
        source.Child = replacement;
        Assert.Equal("replacement", target.Text);
        original.Name = "detached";
        Assert.Equal("replacement", target.Text);

        target.Text = "gamma";
        Assert.Equal("gamma", replacement.Name);
        Assert.Equal(BindingExpressionStatus.Active, expression.Status);

        expression.StopTracking();
        replacement.Name = "stopped";
        Assert.Equal("gamma", target.Text);
        expression.Initialize();
        Assert.Equal("stopped", target.Text);

        CompiledBindingOperations.ClearBindingsForSource(source);
        replacement.Name = "cleared";
        Assert.Equal("stopped", target.Text);
        Assert.Null(CompiledBindingOperations.GetBindingExpression(
            target,
            TextBlock.TextProperty));
    }

    [Fact]
    public void GeneratedCompiledBindingsGroupDefersInitialActivationAndControlsTracking()
    {
        var child = new BindingChild { Name = "initial" };
        var source = new BindingViewModel { Child = child };
        var target = new TextBlock();

        var bindings = CompiledBindingOperations.BeginBindings(source);
        var expression = CompiledBindingOperations.SetBinding(
            target,
            TextBlock.TextProperty,
            source,
            new ICompiledBindingPathSegment[]
            {
                new CompiledBindingPathSegment<BindingViewModel, BindingChild>(
                    nameof(BindingViewModel.Child),
                    static value => value.Child),
                new CompiledBindingPathSegment<BindingChild, string?>(
                    nameof(BindingChild.Name),
                    static value => value.Name)
            },
            new CompiledBindingOptions { Mode = BindingMode.OneWay });

        Assert.Equal(string.Empty, target.Text);
        Assert.Equal(BindingExpressionStatus.Inactive, expression.Status);

        bindings.Initialize();
        Assert.Equal("initial", target.Text);
        Assert.Equal(BindingExpressionStatus.Active, expression.Status);

        child.Name = "tracked";
        Assert.Equal("tracked", target.Text);

        bindings.StopTracking();
        Assert.Equal(BindingExpressionStatus.Inactive, expression.Status);
        child.Name = "stopped";
        Assert.Equal("tracked", target.Text);

        bindings.Update();
        Assert.Equal("stopped", target.Text);
        Assert.Equal(BindingExpressionStatus.Active, expression.Status);

        bindings.Initialize();
        child.Name = "initialized-once";
        Assert.Equal("initialized-once", target.Text);

        CompiledBindingOperations.ClearBindingsForSource(source);
        Assert.Equal(BindingExpressionStatus.Detached, expression.Status);
        var replacementTarget = new TextBlock();
        var replacementBindings = CompiledBindingOperations.BeginBindings(source);
        CompiledBindingOperations.SetBinding(
            replacementTarget,
            TextBlock.TextProperty,
            source,
            new ICompiledBindingPathSegment[]
            {
                new CompiledBindingPathSegment<BindingViewModel, BindingChild>(
                    nameof(BindingViewModel.Child),
                    static value => value.Child),
                new CompiledBindingPathSegment<BindingChild, string?>(
                    nameof(BindingChild.Name),
                    static value => value.Name)
            },
            new CompiledBindingOptions { Mode = BindingMode.OneWay });
        replacementBindings.Initialize();
        Assert.Equal("initialized-once", replacementTarget.Text);

        bindings.StopTracking();
        child.Name = "reloaded";
        Assert.Equal("initialized-once", target.Text);
        Assert.Equal("reloaded", replacementTarget.Text);

        bindings.Update();
        child.Name = "replacement-still-tracked";
        Assert.Equal("initialized-once", target.Text);
        Assert.Equal("replacement-still-tracked", replacementTarget.Text);
    }

    [Fact]
    public void DeferredTemplateBindingsOwnTrackingPerMaterializedRoot()
    {
        var template = new DataTemplate();
        XamlTemplateFactory.SetFactory(
            template,
            static context =>
            {
                var bindings = CompiledBindingOperations.BeginBindings();
                var root = new TextBlock();
                CompiledBindingOperations.SetBinding(
                    root,
                    TextBlock.TextProperty,
                    context!,
                    new ICompiledBindingPathSegment[]
                    {
                        new CompiledBindingPathSegment<BindingChild, string?>(
                            nameof(BindingChild.Name),
                            static value => value.Name,
                            static (value, name) => value.Name = name)
                    },
                    new CompiledBindingOptions { Mode = BindingMode.OneWay },
                    bindings);
                XamlTemplateFactory.AttachBindings(root, bindings);
                return root;
            });
        var item = new BindingChild { Name = "initial" };

        var first = Assert.IsType<TextBlock>(
            XamlTemplateFactory.Build(template, item));
        var second = Assert.IsType<TextBlock>(
            XamlTemplateFactory.Build(template, item));

        Assert.Equal("initial", first.Text);
        Assert.Equal("initial", second.Text);
        item.Name = "both";
        Assert.Equal("both", first.Text);
        Assert.Equal("both", second.Text);

        XamlTemplateFactory.Release(first);
        var firstExpression = CompiledBindingOperations.GetBindingExpression(
            first,
            TextBlock.TextProperty);
        Assert.Null(firstExpression);
        item.Name = "second-only";
        Assert.Equal("both", first.Text);
        Assert.Equal("second-only", second.Text);

        second.FireUnloaded();
        item.Name = "released";
        Assert.Equal("second-only", second.Text);
        Assert.Null(CompiledBindingOperations.GetBindingExpression(
            second,
            TextBlock.TextProperty));
    }

    [Fact]
    public void DeferredOrdinaryBindingsOwnTrackingPerMaterializedRoot()
    {
        BindingMemberAccessorRegistry.Register<BindingChild, string?>(
            nameof(BindingChild.Name),
            static value => value.Name,
            static (value, name) => value.Name = name);
        var template = new DataTemplate();
        XamlTemplateFactory.SetFactory(
            template,
            static context =>
            {
                var lifetime = BindingOperations.BeginBindings();
                var root = new TextBlock();
                var expression = BindingOperations.SetBinding(
                    root,
                    TextBlock.TextProperty,
                    new Binding
                    {
                        Path = nameof(BindingChild.Name),
                        Mode = BindingMode.OneWay
                    },
                    context,
                    root,
                    lifetime);
                Assert.Equal(BindingExpressionStatus.Inactive, expression.Status);
                XamlTemplateFactory.AttachLifetime(root, lifetime);
                Assert.Equal(BindingExpressionStatus.Active, expression.Status);
                return root;
            });
        var item = new BindingChild { Name = "initial" };

        var first = Assert.IsType<TextBlock>(
            XamlTemplateFactory.Build(template, item));
        var second = Assert.IsType<TextBlock>(
            XamlTemplateFactory.Build(template, item));

        Assert.Equal("initial", first.Text);
        Assert.Equal("initial", second.Text);
        item.Name = "both";
        Assert.Equal("both", first.Text);
        Assert.Equal("both", second.Text);

        XamlTemplateFactory.Release(first);
        Assert.Null(BindingOperations.GetBindingExpression(
            first,
            TextBlock.TextProperty));
        item.Name = "second-only";
        Assert.Equal("both", first.Text);
        Assert.Equal("second-only", second.Text);

        second.FireUnloaded();
        item.Name = "released";
        Assert.Equal("second-only", second.Text);
        Assert.Null(BindingOperations.GetBindingExpression(
            second,
            TextBlock.TextProperty));
    }

    [Fact]
    public void MaterializedRootComposesOrdinaryAndCompiledBindingLifetimes()
    {
        BindingMemberAccessorRegistry.Register<BindingChild, string?>(
            nameof(BindingChild.Name),
            static value => value.Name);
        var item = new BindingChild { Name = "initial" };
        var root = new StackPanel();
        var ordinaryTarget = new TextBlock();
        var compiledTarget = new TextBlock();
        root.Children.Add(ordinaryTarget);
        root.Children.Add(compiledTarget);

        var ordinaryLifetime = BindingOperations.BeginBindings();
        var ordinaryExpression = BindingOperations.SetBinding(
            ordinaryTarget,
            TextBlock.TextProperty,
            new Binding { Path = nameof(BindingChild.Name) },
            item,
            root,
            ordinaryLifetime);
        var compiledBindings = CompiledBindingOperations.BeginBindings();
        CompiledBindingOperations.SetBinding(
            compiledTarget,
            TextBlock.TextProperty,
            item,
            new ICompiledBindingPathSegment[]
            {
                new CompiledBindingPathSegment<BindingChild, string?>(
                    nameof(BindingChild.Name),
                    static value => value.Name)
            },
            new CompiledBindingOptions { Mode = BindingMode.OneWay },
            compiledBindings);

        XamlTemplateFactory.AttachLifetime(root, ordinaryLifetime);
        XamlTemplateFactory.AttachBindings(root, compiledBindings);
        Assert.Equal(BindingExpressionStatus.Active, ordinaryExpression.Status);
        Assert.Equal("initial", ordinaryTarget.Text);
        Assert.Equal("initial", compiledTarget.Text);

        item.Name = "both";
        Assert.Equal("both", ordinaryTarget.Text);
        Assert.Equal("both", compiledTarget.Text);

        XamlTemplateFactory.Release(root);
        item.Name = "released";
        Assert.Equal("both", ordinaryTarget.Text);
        Assert.Equal("both", compiledTarget.Text);
        Assert.Null(BindingOperations.GetBindingExpression(
            ordinaryTarget,
            TextBlock.TextProperty));
        Assert.Null(CompiledBindingOperations.GetBindingExpression(
            compiledTarget,
            TextBlock.TextProperty));
    }

    [Fact]
    public void TemplateLifetimeInitializationFailureReleasesCompositeInReverseOrder()
    {
        var root = new StackPanel();
        var events = new List<string>();
        var first = new RecordingTemplateLifetime(
            () => events.Add("initialize:first"),
            () => events.Add("dispose:first"));
        var second = new RecordingTemplateLifetime(
            () =>
            {
                events.Add("initialize:second");
                throw new InvalidOperationException("activation failed");
            },
            () => events.Add("dispose:second"));

        XamlTemplateFactory.AttachLifetime(root, first);
        var error = Assert.Throws<InvalidOperationException>(
            () => XamlTemplateFactory.AttachLifetime(root, second));

        Assert.Equal("activation failed", error.Message);
        Assert.Equal(
            new[]
            {
                "initialize:first",
                "initialize:second",
                "dispose:second",
                "dispose:first"
            },
            events);
        XamlTemplateFactory.Release(root);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public void GeneratedBindingsSampleMaterializesTwoIndependentTrackedTemplates()
    {
        ProGPU.Samples.XamlCompilerBindingSources.Current.Title =
            "Static source item";
        ProGPU.Samples.XamlCompilerBindingSources.Items[0].Title =
            "Ordinary indexed item";
        var page = new ProGPU.Samples.XamlCompilerBindingsPage();
        var firstRoot = Assert.IsType<StackPanel>(
            page.FirstMaterializedTemplate);
        var secondRoot = Assert.IsType<StackPanel>(
            page.SecondMaterializedTemplate);
        var firstCompiled = Assert.IsType<TextBlock>(
            page.FirstCompiledTemplateText);
        var firstOrdinary = Assert.IsType<TextBlock>(
            page.FirstOrdinaryTemplateText);
        var secondCompiled = Assert.IsType<TextBlock>(
            page.SecondCompiledTemplateText);
        var secondOrdinary = Assert.IsType<TextBlock>(
            page.SecondOrdinaryTemplateText);

        Assert.Equal("SelfSourceText", page.SelfSourceTextValue);
        Assert.Equal("Static source item", page.StaticSourceTextValue);
        ProGPU.Samples.XamlCompilerBindingSources.Current.Title =
            "Static source update";
        Assert.Equal("Static source update", page.StaticSourceTextValue);
        Assert.Equal("Ordinary indexed item", page.OrdinaryIndexerTextValue);
        ProGPU.Samples.XamlCompilerBindingSources.Items[0].Title =
            "Ordinary indexed update";
        Assert.Equal("Ordinary indexed update", page.OrdinaryIndexerTextValue);
        page.OrdinaryIndexerTextValue = "Ordinary indexed write";
        Assert.Equal(
            "Ordinary indexed write",
            ProGPU.Samples.XamlCompilerBindingSources.Items[0].Title);

        Assert.NotSame(firstRoot, secondRoot);
        Assert.Equal(page.Items[0].Title, firstCompiled.Text);
        Assert.Equal(page.Items[0].Title, firstOrdinary.Text);
        Assert.Equal(page.Items[0].Title, secondCompiled.Text);
        Assert.Equal(page.Items[0].Title, secondOrdinary.Text);

        page.Items[0].Title = "shared update";
        Assert.Equal("shared update", firstCompiled.Text);
        Assert.Equal("shared update", firstOrdinary.Text);
        Assert.Equal("shared update", secondCompiled.Text);
        Assert.Equal("shared update", secondOrdinary.Text);

        XamlTemplateFactory.Release(firstRoot);
        page.Items[0].Title = "second instance only";
        Assert.Equal("shared update", firstCompiled.Text);
        Assert.Equal("shared update", firstOrdinary.Text);
        Assert.Equal("second instance only", secondCompiled.Text);
        Assert.Equal("second instance only", secondOrdinary.Text);

        page.Content = new StackPanel();
        page.Items[0].Title = "detached subtree";
        Assert.Equal("second instance only", secondCompiled.Text);
        Assert.Equal("second instance only", secondOrdinary.Text);
        Assert.Null(CompiledBindingOperations.GetBindingExpression(
            secondCompiled,
            TextBlock.TextProperty));
        Assert.Null(BindingOperations.GetBindingExpression(
            secondOrdinary,
            TextBlock.TextProperty));
    }

    [Fact]
    public void CompiledBindingUsesExactDependencyPropertyAndBindBackDelegates()
    {
        var dependencySource = new BindingSource { Value = "first" };
        var target = new TextBlock();
        var dependencyPath = new ICompiledBindingPathSegment[]
        {
            new CompiledBindingPathSegment<BindingSource, string?>(
                nameof(BindingSource.Value),
                static value => value.Value,
                static (value, text) => value.Value = text,
                BindingSource.ValueProperty)
        };
        CompiledBindingOperations.SetBinding(
            target,
            TextBlock.TextProperty,
            dependencySource,
            dependencyPath,
            new CompiledBindingOptions { Mode = BindingMode.OneWay });

        dependencySource.Value = "second";
        Assert.Equal("second", target.Text);

        var readOnlySource = new BindBackSource("read-only");
        var editable = new TextBlock();
        CompiledBindingOperations.SetBinding(
            editable,
            TextBlock.TextProperty,
            readOnlySource,
            new ICompiledBindingPathSegment[]
            {
                new CompiledBindingPathSegment<BindBackSource, string>(
                    nameof(BindBackSource.Value),
                    static value => value.Value)
            },
            new CompiledBindingOptions
            {
                Mode = BindingMode.TwoWay,
                BindBack = static (source, value) =>
                    ((BindBackSource)source).SetValue((string)value!)
            });

        editable.Text = "written";
        Assert.Equal("written", readOnlySource.Value);
    }

    [Fact]
    public void CompiledBindingIndexerTracksCollectionChangesAndWritesThrough()
    {
        var source = new ObservableCollection<string> { "first" };
        var target = new TextBlock();
        var expression = CompiledBindingOperations.SetBinding(
            target,
            TextBlock.TextProperty,
            source,
            new ICompiledBindingPathSegment[]
            {
                new CompiledBindingIndexerPathSegment<ObservableCollection<string>, string>(
                    0,
                    static values => values[0],
                    static (values, value) => values[0] = value)
            },
            new CompiledBindingOptions { Mode = BindingMode.TwoWay });

        Assert.Equal("first", target.Text);
        source[0] = "collection";
        Assert.Equal("collection", target.Text);

        target.Text = "target";
        Assert.Equal("target", source[0]);
        Assert.Equal(BindingExpressionStatus.Active, expression.Status);
    }

    [Fact]
    public void CompiledBindingCastAndAttachedMemberRemainTypedAndTrackable()
    {
        object source = new BindingChild { Name = "cast" };
        var castTarget = new TextBlock();
        CompiledBindingOperations.SetBinding(
            castTarget,
            TextBlock.TextProperty,
            source,
            new ICompiledBindingPathSegment[]
            {
                new CompiledBindingCastPathSegment<object, BindingChild>(
                    typeof(BindingChild).FullName!,
                    static value => (BindingChild)value),
                new CompiledBindingPathSegment<BindingChild, string?>(
                    nameof(BindingChild.Name),
                    static value => value.Name,
                    static (value, text) => value.Name = text)
            },
            new CompiledBindingOptions { Mode = BindingMode.OneWay });

        Assert.Equal("cast", castTarget.Text);
        ((BindingChild)source).Name = "tracked";
        Assert.Equal("tracked", castTarget.Text);

        var attachedSource = new TextBlock();
        var attachedTarget = new TextBlock();
        CompiledBindingOperations.SetBinding(
            attachedTarget,
            TextBlock.TextProperty,
            attachedSource,
            new ICompiledBindingPathSegment[]
            {
                new CompiledBindingPathSegment<TextBlock, int>(
                    "Grid.Row",
                    static value => Grid.GetRow(value),
                    static (value, row) => Grid.SetRow(value, row),
                    Grid.RowProperty)
            },
            new CompiledBindingOptions { Mode = BindingMode.TwoWay });

        Grid.SetRow(attachedSource, 2);
        Assert.Equal("2", attachedTarget.Text);
        attachedTarget.Text = "3";
        Assert.Equal(3, Grid.GetRow(attachedSource));
    }

    [Fact]
    public void CompiledBindingFunctionTracksAndRewiresEveryArgumentPath()
    {
        var original = new BindingChild { Name = "first" };
        var source = new FunctionViewModel
        {
            Child = original,
            Prefix = "prefix"
        };
        var target = new TextBlock();
        var childPath = new ICompiledBindingPathSegment[]
        {
            new CompiledBindingPathSegment<FunctionViewModel, BindingChild>(
                nameof(FunctionViewModel.Child),
                static value => value.Child,
                static (value, child) => value.Child = child),
            new CompiledBindingPathSegment<BindingChild, string?>(
                nameof(BindingChild.Name),
                static value => value.Name,
                static (value, name) => value.Name = name)
        };
        var prefixPath = new ICompiledBindingPathSegment[]
        {
            new CompiledBindingPathSegment<FunctionViewModel, string>(
                nameof(FunctionViewModel.Prefix),
                static value => value.Prefix,
                static (value, prefix) => value.Prefix = prefix)
        };
        CompiledBindingOperations.SetBinding(
            target,
            TextBlock.TextProperty,
            source,
            new ICompiledBindingPathSegment[]
            {
                new CompiledBindingFunctionPathSegment<FunctionViewModel, string>(
                    nameof(FunctionViewModel.Format),
                    static value => value.Format(value.Child.Name, value.Prefix),
                    Array.Empty<ICompiledBindingPathSegment>(),
                    new[] { childPath, prefixPath })
            },
            new CompiledBindingOptions { Mode = BindingMode.OneWay });

        Assert.Equal("prefix:first", target.Text);
        original.Name = "updated";
        Assert.Equal("prefix:updated", target.Text);
        source.Prefix = "next";
        Assert.Equal("next:updated", target.Text);

        var replacement = new BindingChild { Name = "replacement" };
        source.Child = replacement;
        Assert.Equal("next:replacement", target.Text);
        original.Name = "detached";
        Assert.Equal("next:replacement", target.Text);
        replacement.Name = "tracked";
        Assert.Equal("next:tracked", target.Text);

        var ownerTarget = new TextBlock();
        var ownerPath = new ICompiledBindingPathSegment[]
        {
            new CompiledBindingPathSegment<FunctionViewModel, FunctionFormatter>(
                nameof(FunctionViewModel.Formatter),
                static value => value.Formatter)
        };
        CompiledBindingOperations.SetBinding(
            ownerTarget,
            TextBlock.TextProperty,
            source,
            new ICompiledBindingPathSegment[]
            {
                new CompiledBindingFunctionPathSegment<FunctionViewModel, string>(
                    nameof(FunctionFormatter.Compose),
                    static value => value.Formatter.Compose(value.Child.Name),
                    ownerPath,
                    new[] { childPath })
            },
            new CompiledBindingOptions { Mode = BindingMode.OneWay });

        Assert.Equal("owner:tracked", ownerTarget.Text);
        source.Formatter.SetPrefix("changed");
        Assert.Equal("changed:tracked", ownerTarget.Text);
    }

    private sealed class BindingSource : DependencyObject
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(
                nameof(Value),
                typeof(string),
                typeof(BindingSource),
                new PropertyMetadata(null));

        public string? Value
        {
            get => GetValue(ValueProperty) as string;
            set => SetValue(ValueProperty, value);
        }
    }

    private sealed class RecordingTemplateLifetime : IXamlTemplateLifetime
    {
        private readonly Action _initialize;
        private readonly Action _dispose;

        public RecordingTemplateLifetime(
            Action initialize,
            Action dispose)
        {
            _initialize = initialize;
            _dispose = dispose;
        }

        public int DisposeCount { get; private set; }

        public void Initialize() =>
            _initialize();

        public void Dispose()
        {
            DisposeCount++;
            _dispose();
        }
    }

    private abstract class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool Set<T>(
            ref T field,
            T value,
            [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        protected void Raise(string propertyName) =>
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
    }

    private sealed class BindingViewModel : NotifyBase
    {
        private BindingChild _child = new();

        public BindingChild Child
        {
            get => _child;
            set => Set(ref _child, value);
        }
    }

    private sealed class BindingChild : NotifyBase
    {
        private string? _name;

        public string? Name
        {
            get => _name;
            set => Set(ref _name, value);
        }
    }

    private sealed class FunctionViewModel : NotifyBase
    {
        private BindingChild _child = new();
        private string _prefix = string.Empty;

        public BindingChild Child
        {
            get => _child;
            set => Set(ref _child, value);
        }

        public string Prefix
        {
            get => _prefix;
            set => Set(ref _prefix, value);
        }

        public FunctionFormatter Formatter { get; } = new();

        public string Format(string? value, string prefix) =>
            prefix + ":" + value;
    }

    private sealed class FunctionFormatter : NotifyBase
    {
        private string _prefix = "owner";

        public string Compose(string? value) => _prefix + ":" + value;

        public void SetPrefix(string prefix)
        {
            _prefix = prefix;
            Raise(nameof(Compose));
        }
    }

    private sealed class NumericViewModel : NotifyBase
    {
        private int _value;

        public int Value
        {
            get => _value;
            set => Set(ref _value, value);
        }
    }

    private sealed class NullableViewModel
    {
        public string? Value => null;
    }

    private sealed class BindBackSource
    {
        public BindBackSource(string value) => Value = value;
        public string Value { get; private set; }
        public void SetValue(string value) => Value = value;
    }

    private sealed class IntegerTextConverter : IValueConverter
    {
        public object? Convert(
            object? value,
            Type targetType,
            object? parameter,
            string language) =>
            $"#{value}";

        public object? ConvertBack(
            object? value,
            Type targetType,
            object? parameter,
            string language) =>
            int.Parse(((string)value!).TrimStart('#'));
    }
}
