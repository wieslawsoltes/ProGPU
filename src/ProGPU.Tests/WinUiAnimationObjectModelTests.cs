using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using ProGPU.Vector;
using System.Numerics;
using Xunit;

namespace ProGPU.Tests;

public sealed class WinUiAnimationObjectModelTests
{
    private sealed class ContextMarkupExtension : MarkupExtension
    {
        public object? SeenTarget { get; private set; }
        public object? SeenRoot { get; private set; }
        public ProvideValueTargetProperty? SeenProperty { get; private set; }
        public Uri? SeenBaseUri { get; private set; }

        protected override object? ProvideValue(IXamlServiceProvider serviceProvider)
        {
            var target = Assert.IsAssignableFrom<IProvideValueTarget>(serviceProvider.GetService(typeof(IProvideValueTarget)));
            var root = Assert.IsAssignableFrom<IRootObjectProvider>(serviceProvider.GetService(typeof(IRootObjectProvider)));
            var uri = Assert.IsAssignableFrom<IUriContext>(serviceProvider.GetService(typeof(IUriContext)));
            SeenTarget = target.TargetObject;
            SeenProperty = Assert.IsType<ProvideValueTargetProperty>(target.TargetProperty);
            SeenRoot = root.RootObject;
            SeenBaseUri = uri.BaseUri;
            return "provided";
        }
    }

    private sealed class RecordingCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public object? ExecutedParameter { get; private set; }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => ExecutedParameter = parameter;
    }

    private sealed class FixedTemplateSelector(DataTemplate template) : DataTemplateSelector
    {
        protected override DataTemplate? SelectTemplateCore(object? item, DependencyObject container) => template;
    }

    private sealed class MediaSource : Windows.Media.Playback.IMediaPlaybackSource
    {
    }

    [Fact]
    public void MarkupExtensionRuntimeSuppliesTypedTargetAndRootServices()
    {
        var extension = new ContextMarkupExtension();
        var target = new TextBlock();
        var root = new Grid();

        var result = ProGPU.Xaml.Runtime.WinUiMarkupExtensionRuntime.Evaluate<string>(
            extension,
            target,
            typeof(TextBlock),
            nameof(TextBlock.Text),
            root,
            "Pages/MarkupExtension.xaml");

        Assert.Equal("provided", result);
        Assert.Same(target, extension.SeenTarget);
        Assert.Same(root, extension.SeenRoot);
        Assert.Equal(typeof(TextBlock), extension.SeenProperty!.Type);
        Assert.Equal(nameof(TextBlock.Text), extension.SeenProperty.Name);
        Assert.Equal("Pages/MarkupExtension.xaml", extension.SeenBaseUri!.OriginalString);
        Assert.False(extension.SeenBaseUri.IsAbsoluteUri);
    }

    [Fact]
    public void StoryboardRetainsTypedChildrenAndAttachedTargets()
    {
        var animation = new ObjectAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteObjectKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = "value"
        });
        Storyboard.SetTargetName(animation, "Presenter");
        Storyboard.SetTargetProperty(animation, "Background");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);

        Assert.Same(animation, Assert.Single(storyboard.Children));
        Assert.Equal("Presenter", Storyboard.GetTargetName(animation));
        Assert.Equal("Background", Storyboard.GetTargetProperty(animation));
        Assert.Equal(TimeSpan.Zero, Assert.Single(animation.KeyFrames).KeyTime.TimeSpan);
    }

    [Fact]
    public void VisualStateOwnsItsStoryboardContent()
    {
        var storyboard = new Storyboard();
        var state = new VisualState { Name = "PointerOver", Storyboard = storyboard };

        Assert.Same(storyboard, state.Storyboard);
    }

    [Fact]
    public void GeneratedNameScopeResolvesNonVisualStoryboardTargetsAndNestedPaths()
    {
        var control = new Button();
        var transform = new CompositeTransform();
        control.RenderTransform = transform;
        XamlTemplateFactory.BeginNameScope(control);
        XamlTemplateFactory.RegisterName(
            control,
            "Transform",
            transform);
        var siblingRoot = new Button();
        var siblingTransform = new CompositeTransform();
        XamlTemplateFactory.BeginNameScope(siblingRoot);
        XamlTemplateFactory.RegisterName(
            siblingRoot,
            "Transform",
            siblingTransform);
        Assert.Throws<InvalidOperationException>(
            () => XamlTemplateFactory.RegisterName(
                control,
                "Transform",
                new CompositeTransform()));

        var directAnimation = new DoubleAnimation { To = 2d };
        Storyboard.SetTargetName(directAnimation, "Transform");
        Storyboard.SetTargetProperty(directAnimation, "ScaleX");
        var nestedAnimation = new DoubleAnimation { To = 3d };
        Storyboard.SetTargetProperty(
            nestedAnimation,
            "(UIElement.RenderTransform).(CompositeTransform.ScaleY)");
        var storyboard = new Storyboard();
        storyboard.Children.Add(directAnimation);
        storyboard.Children.Add(nestedAnimation);
        var state = new VisualState
        {
            Name = "Scaled",
            Storyboard = storyboard
        };
        var group = new VisualStateGroup { Name = "ScaleStates" };
        group.States.Add(state);
        VisualStateManager.GetVisualStateGroups(control).Add(group);

        Assert.Same(transform, control.FindName("Transform"));
        Assert.Same(
            siblingTransform,
            siblingRoot.FindName("Transform"));
        Assert.True(
            VisualStateManager.GoToState(
                control,
                "Scaled",
                false));
        Assert.Equal(2d, transform.ScaleX);
        Assert.Equal(3d, transform.ScaleY);

        XamlTemplateFactory.Release(control);
        XamlTemplateFactory.Release(siblingRoot);
        Assert.Null(
            XamlTemplateFactory.FindName(
            control,
            "Transform"));
    }

    [Fact]
    public void VisualStateAnimationValuesPreserveTheUnderlyingLocalValue()
    {
        var control = new Button
        {
            Opacity = 0.4d,
            Content = "base"
        };
        var animation = new DoubleAnimation { To = 0.8d };
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        var active = new VisualState
        {
            Name = "Active",
            Storyboard = storyboard
        };
        active.Setters.Add(
            new Setter(
                nameof(ContentControl.Content),
                null));
        var normal = new VisualState { Name = "Normal" };
        var group = new VisualStateGroup { Name = "CommonStates" };
        group.States.Add(active);
        group.States.Add(normal);
        VisualStateManager.GetVisualStateGroups(control).Add(group);

        Assert.True(VisualStateManager.GoToState(control, "Active", false));
        Assert.Equal(0.8d, control.Opacity);
        Assert.Null(control.Content);

        control.Opacity = 0.6d;
        control.Content = "changed";
        Assert.Equal(0.8d, control.Opacity);
        Assert.Null(control.Content);

        Assert.True(VisualStateManager.GoToState(control, "Normal", false));
        Assert.Equal(0.6d, control.Opacity);
        Assert.Equal("changed", control.Content);
    }

    [Fact]
    public void VisualStateBindingTracksItsTemplatedParentAndDetachesOnExit()
    {
        var initial = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f));
        var first = new SolidColorBrush(new Vector4(0f, 1f, 0f, 1f));
        var second = new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f));
        var control = new Button { Foreground = first };
        TextBlock? presenter = null;
        control.Template = new ControlTemplate(
            typeof(Button),
            _ =>
            {
                var templateRoot = new Grid();
                presenter = new TextBlock { Foreground = initial };
                XamlTemplateFactory.BeginNameScope(templateRoot);
                XamlTemplateFactory.RegisterName(
                    templateRoot,
                    "Presenter",
                    presenter);
                var animation = new ObjectAnimationUsingKeyFrames();
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame
                {
                    KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
                    Value = new Binding
                    {
                        Path = nameof(Control.Foreground),
                        RelativeSource = new RelativeSource
                        {
                            Mode = RelativeSourceMode.TemplatedParent
                        }
                    }
                });
                Storyboard.SetTargetName(animation, "Presenter");
                Storyboard.SetTargetProperty(
                    animation,
                    nameof(TextBlock.Foreground));
                var storyboard = new Storyboard();
                storyboard.Children.Add(animation);
                var active = new VisualState
                {
                    Name = "Active",
                    Storyboard = storyboard
                };
                var normal = new VisualState { Name = "Normal" };
                var group = new VisualStateGroup { Name = "CommonStates" };
                group.States.Add(active);
                group.States.Add(normal);
                VisualStateManager
                    .GetVisualStateGroups(templateRoot)
                    .Add(group);
                return templateRoot;
            });
        var firstPresenter = Assert.IsType<TextBlock>(presenter);

        Assert.True(VisualStateManager.GoToState(control, "Active", false));
        Assert.Same(first, firstPresenter.Foreground);

        control.Foreground = second;
        Assert.Same(second, firstPresenter.Foreground);

        Assert.True(VisualStateManager.GoToState(control, "Normal", false));
        Assert.Same(initial, firstPresenter.Foreground);

        control.Foreground = first;
        Assert.Same(initial, firstPresenter.Foreground);

        Assert.True(VisualStateManager.GoToState(control, "Active", false));
        Assert.Same(first, firstPresenter.Foreground);
        Assert.True(control.ApplyTemplate());
        var replacementPresenter = Assert.IsType<TextBlock>(presenter);
        Assert.NotSame(firstPresenter, replacementPresenter);

        control.Foreground = second;
        Assert.Same(first, firstPresenter.Foreground);
        Assert.Same(initial, replacementPresenter.Foreground);
    }

    [Fact]
    public void VisualStateObjectValuesConvertPathDataAndWinUiRectangles()
    {
        var control = new Button();
        var path = new Microsoft.UI.Xaml.Shapes.Path();
        var clip = new RectangleGeometry();
        XamlTemplateFactory.BeginNameScope(control);
        XamlTemplateFactory.RegisterName(control, "Path", path);
        XamlTemplateFactory.RegisterName(control, "Clip", clip);

        var pathAnimation = new ObjectAnimationUsingKeyFrames();
        pathAnimation.KeyFrames.Add(new DiscreteObjectKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = "M 0 0 L 8 0 L 8 8 Z"
        });
        Storyboard.SetTargetName(pathAnimation, "Path");
        Storyboard.SetTargetProperty(
            pathAnimation,
            nameof(Microsoft.UI.Xaml.Shapes.Path.Data));
        var rectAnimation = new ObjectAnimationUsingKeyFrames();
        rectAnimation.KeyFrames.Add(new DiscreteObjectKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = new Windows.Foundation.Rect(1, 2, 3, 4)
        });
        Storyboard.SetTargetName(rectAnimation, "Clip");
        Storyboard.SetTargetProperty(rectAnimation, nameof(RectangleGeometry.Rect));
        var storyboard = new Storyboard();
        storyboard.Children.Add(pathAnimation);
        storyboard.Children.Add(rectAnimation);
        var state = new VisualState
        {
            Name = "Converted",
            Storyboard = storyboard
        };
        var group = new VisualStateGroup { Name = "CommonStates" };
        group.States.Add(state);
        VisualStateManager.GetVisualStateGroups(control).Add(group);

        Assert.True(VisualStateManager.GoToState(control, "Converted", false));
        Assert.IsType<Microsoft.UI.Xaml.Media.PathGeometry>(path.Data);
        Assert.Equal(1f, clip.Rect.X);
        Assert.Equal(2f, clip.Rect.Y);
        Assert.Equal(3f, clip.Rect.Width);
        Assert.Equal(4f, clip.Rect.Height);
        XamlTemplateFactory.Release(control);
    }

    [Fact]
    public void TypographyValueObjectsPreservePublicSourcesAndWeights()
    {
        var family = new FontFamily("Segoe UI, Arial");

        Assert.Equal("Segoe UI, Arial", family.Source);
        Assert.Equal((ushort)400, FontWeights.Normal.Weight);
        Assert.Equal((ushort)600, FontWeights.SemiBold.Weight);
        Assert.Equal((ushort)950, FontWeights.ExtraBlack.Weight);
    }

    [Fact]
    public void FluentCommandAndScrollingContractsRetainTypedValues()
    {
        var command = new AppBarButton
        {
            Label = "Play",
            Icon = new SymbolIcon { Symbol = Symbol.Play },
            IsCompact = true
        };
        var target = new Grid();
        ScrollViewer.SetHorizontalScrollBarVisibility(target, ScrollBarVisibility.Hidden);
        ScrollViewer.SetIsHorizontalRailEnabled(target, false);

        Assert.Equal(57602, (int)((SymbolIcon)command.Icon!).Symbol);
        Assert.True(command.IsCompact);
        Assert.Equal(ScrollBarVisibility.Hidden, ScrollViewer.GetHorizontalScrollBarVisibility(target));
        Assert.False(ScrollViewer.GetIsHorizontalRailEnabled(target));
        Assert.Equal(ScrollingIndicatorMode.MouseIndicator, (ScrollingIndicatorMode)2);
    }

    [Fact]
    public void FluentBaseElementContractsExposeTypedDependencyProperties()
    {
        var element = new Grid
        {
            UseLayoutRounding = false,
            IsAccessKeyScope = true,
            TabIndex = 7
        };
        var control = new Button
        {
            TabNavigation = KeyboardNavigationMode.Cycle,
            IsFocusEngagementEnabled = true,
            ElementSoundMode = ElementSoundMode.FocusOnly
        };
        Control.SetIsTemplateKeyTipTarget(element, true);

        Assert.False(element.UseLayoutRounding);
        Assert.True(element.IsAccessKeyScope);
        Assert.Equal(7, element.TabIndex);
        Assert.Equal(KeyboardNavigationMode.Cycle, control.TabNavigation);
        Assert.True(control.IsFocusEngagementEnabled);
        Assert.Equal(ElementSoundMode.FocusOnly, control.ElementSoundMode);
        Assert.True(Control.GetIsTemplateKeyTipTarget(element));
        Assert.Equal(0, (int)ElementSoundMode.Default);
        Assert.Equal(1, (int)ElementSoundMode.FocusOnly);
        Assert.Equal(2, (int)ElementSoundMode.Off);
    }

    [Fact]
    public void FluentPanelContractsRetainBorderZIndexAndTransitionValues()
    {
        var transition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(120) };
        var panel = new StackPanel
        {
            BorderBrush = new SolidColorBrush(0x112233FF),
            BorderThickness = new Thickness(1, 2, 3, 4),
            CornerRadius = new CornerRadius(5, 6, 7, 8),
            BackgroundTransition = transition
        };
        Canvas.SetZIndex(panel, 42);

        Assert.NotNull(panel.BorderBrush);
        Assert.Equal(3d, panel.BorderThickness.Right);
        Assert.Equal(8d, panel.CornerRadius.BottomLeft);
        Assert.Same(transition, panel.BackgroundTransition);
        Assert.Equal(TimeSpan.FromMilliseconds(120), transition.Duration);
        Assert.Equal(42, Canvas.GetZIndex(panel));
    }

    [Fact]
    public void FluentTextContractsPreserveValuesAndRejectNegativeLineLimits()
    {
        var text = new TextBlock
        {
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            IsTextScaleFactorEnabled = false,
            MaxLines = 2,
            OpticalMarginAlignment = OpticalMarginAlignment.TrimSideBearings,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var presenter = new ContentPresenter
        {
            Content = "content",
            MaxLines = 3,
            OpticalMarginAlignment = OpticalMarginAlignment.TrimSideBearings
        };

        Assert.Equal(Windows.UI.Text.FontStyle.Italic, text.FontStyle);
        Assert.False(text.IsTextScaleFactorEnabled);
        Assert.Equal(2, text.MaxLines);
        Assert.Equal(OpticalMarginAlignment.TrimSideBearings, text.OpticalMarginAlignment);
        Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
        Assert.Equal(3, presenter.MaxLines);
        Assert.Equal(OpticalMarginAlignment.TrimSideBearings, presenter.OpticalMarginAlignment);
        Assert.Throws<ArgumentOutOfRangeException>(() => text.MaxLines = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => presenter.MaxLines = -1);
        Assert.Equal(0, (int)TextTrimming.None);
        Assert.Equal(3, (int)TextTrimming.Clip);
    }

    [Fact]
    public void FluentScrollAnimationAndGeometryContractsPreserveTypedBehavior()
    {
        var scrollViewer = new ScrollViewer
        {
            HorizontalSnapPointsType = SnapPointsType.OptionalSingle,
            VerticalSnapPointsType = SnapPointsType.Mandatory,
            HorizontalSnapPointsAlignment = SnapPointsAlignment.Center,
            VerticalSnapPointsAlignment = SnapPointsAlignment.Far,
            IsScrollInertiaEnabled = false,
            IsZoomInertiaEnabled = false,
            IsZoomChainingEnabled = false
        };
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        var toolTip = new ToolTip { Placement = PlacementMode.Mouse };
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            StrokeStartLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round,
            StrokeEndLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Square,
            StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Bevel
        };

        Assert.Equal(SnapPointsType.OptionalSingle, scrollViewer.HorizontalSnapPointsType);
        Assert.Equal(SnapPointsType.Mandatory, scrollViewer.VerticalSnapPointsType);
        Assert.Equal(SnapPointsAlignment.Center, scrollViewer.HorizontalSnapPointsAlignment);
        Assert.Equal(SnapPointsAlignment.Far, scrollViewer.VerticalSnapPointsAlignment);
        Assert.False(scrollViewer.IsScrollInertiaEnabled);
        Assert.False(scrollViewer.IsZoomInertiaEnabled);
        Assert.False(scrollViewer.IsZoomChainingEnabled);
        Assert.Equal(FillBehavior.Stop, animation.FillBehavior);
        Assert.Equal(PlacementMode.Mouse, toolTip.Placement);
        Assert.Equal(Microsoft.UI.Xaml.Media.PenLineCap.Round, path.StrokeStartLineCap);
        Assert.Equal(Microsoft.UI.Xaml.Media.PenLineCap.Square, path.StrokeEndLineCap);
        Assert.Equal(Microsoft.UI.Xaml.Media.PenLineJoin.Bevel, path.StrokeLineJoin);
        Assert.Equal(4, (int)SnapPointsType.MandatorySingle);
        Assert.Equal(2, (int)SnapPointsAlignment.Far);
        Assert.Equal(10, (int)PlacementMode.Top);
    }

    [Fact]
    public void FluentInputAndCalendarContractsEnforceObservableState()
    {
        var template = new DataTemplate();
        var textBox = new TextBox
        {
            Header = "Name",
            HeaderTemplate = template,
            Description = "Required",
            DesiredCandidateWindowAlignment = CandidateWindowAlignment.BottomEdge
        };
        var minimum = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var maximum = new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var calendar = new CalendarView
        {
            MinDate = minimum,
            MaxDate = maximum,
            FirstDayOfWeek = DayOfWeek.Monday,
            DisplayMode = CalendarViewDisplayMode.Month,
            CalendarIdentifier = "GregorianCalendar",
            DayOfWeekFormat = "{dayofweek.abbreviated(2)}",
            IsTodayHighlighted = false,
            IsOutOfScopeEnabled = false,
            IsGroupLabelVisible = true,
            DisplayDate = new DateTime(2026, 1, 1)
        };

        Assert.Equal("Name", textBox.Header);
        Assert.Same(template, textBox.HeaderTemplate);
        Assert.Equal("Required", textBox.Description);
        Assert.Equal(CandidateWindowAlignment.BottomEdge, textBox.DesiredCandidateWindowAlignment);
        Assert.Equal(maximum.LocalDateTime, calendar.DisplayDate);
        Assert.Equal(DayOfWeek.Monday, calendar.FirstDayOfWeek);
        Assert.False(calendar.IsTodayHighlighted);
        Assert.False(calendar.IsOutOfScopeEnabled);
        Assert.True(calendar.IsGroupLabelVisible);
        Assert.Throws<ArgumentOutOfRangeException>(() => calendar.MinDate = maximum.AddDays(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => calendar.MaxDate = minimum.AddDays(-1));
    }

    [Fact]
    public void FrameworkElementMinAndMaxConstraintsAffectMeasuredSize()
    {
        var minimum = new Grid { MinWidth = 80f, MinHeight = 40f };
        minimum.Measure(Vector2.Zero);
        Assert.Equal(new Vector2(80f, 40f), minimum.DesiredSize);

        var maximum = new Grid { Width = 200f, Height = 120f, MaxWidth = 90f, MaxHeight = 60f };
        maximum.Measure(new Vector2(500f, 500f));
        Assert.Equal(new Vector2(90f, 60f), maximum.DesiredSize);
    }

    [Fact]
    public void ItemsControlKeepsWinUiTemplatesSeparateFromRealizedProGpuHost()
    {
        var template = new DataTemplate();
        var panelTemplate = new ItemsPanelTemplate();
        var realizedHost = new UniformVirtualizingGridPanel();
        var control = new ItemsControl
        {
            ItemTemplate = template,
            ItemsPanel = panelTemplate,
            ItemsHost = realizedHost,
            ItemVisualFactory = static () => new Border()
        };

        control.Items.Add("first");

        Assert.Same(template, control.ItemTemplate);
        Assert.Same(panelTemplate, control.ItemsPanel);
        Assert.Same(realizedHost, control.ItemsPanelRoot);
        Assert.Equal("first", control.GetItemAt(0));
    }

    [Fact]
    public void CommandBarContentPropertyOwnsTypedCommandCollections()
    {
        var commandBar = new CommandBar { IsDynamicOverflowEnabled = false };
        var primary = new AppBarButton { Label = "Play", DynamicOverflowOrder = 4 };
        var secondary = new AppBarToggleButton { Label = "Repeat" };

        commandBar.PrimaryCommands.Add(primary);
        commandBar.SecondaryCommands.Add(secondary);

        Assert.Same(primary, Assert.Single(commandBar.PrimaryCommands));
        Assert.Same(secondary, Assert.Single(commandBar.SecondaryCommands));
        Assert.Equal(4, primary.DynamicOverflowOrder);
        Assert.False(commandBar.IsDynamicOverflowEnabled);
    }

    [Fact]
    public void RangeControlsShareCanonicalDoubleRangeContract()
    {
        Assert.Equal(typeof(RangeBase), typeof(Slider).BaseType);
        Assert.Equal(typeof(RangeBase), typeof(ProgressBar).BaseType);
        Assert.Equal(typeof(RangeBase), typeof(ScrollBar).BaseType);

        var scrollBar = new ScrollBar
        {
            Minimum = 10d,
            Maximum = 110d,
            Value = 200d,
            SmallChange = 2d,
            LargeChange = 20d,
            ViewportSize = 50d,
            Orientation = Orientation.Horizontal
        };

        Assert.Equal(110d, scrollBar.Value);
        Assert.Equal(2d, scrollBar.SmallChange);
        Assert.Equal(20d, scrollBar.LargeChange);
        Assert.Equal(50d, scrollBar.ViewportSize);
        Assert.Equal(Orientation.Horizontal, scrollBar.Orientation);

        scrollBar.Maximum = 5d;

        Assert.Equal(5d, scrollBar.Minimum);
        Assert.Equal(5d, scrollBar.Maximum);
        Assert.Equal(5d, scrollBar.Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => scrollBar.ViewportSize = -1d);
    }

    [Fact]
    public void CommandBarFlyoutAndContainerRetainTypedCommands()
    {
        Assert.Equal(typeof(CommandBarFlyout), typeof(TextCommandBarFlyout).BaseType);

        var flyout = new CommandBarFlyout { AlwaysExpanded = true };
        var container = new AppBarElementContainer
        {
            Content = new TextBlock { Text = "Custom" },
            IsCompact = true,
            DynamicOverflowOrder = 3
        };
        var secondary = new AppBarButton { Label = "Copy" };

        flyout.PrimaryCommands.Add(container);
        flyout.SecondaryCommands.Add(secondary);

        Assert.Same(container, Assert.Single(flyout.PrimaryCommands));
        Assert.Same(secondary, Assert.Single(flyout.SecondaryCommands));
        Assert.True(flyout.AlwaysExpanded);
        Assert.True(container.IsCompact);
        Assert.False(container.IsInOverflow);
        Assert.Equal(3, container.DynamicOverflowOrder);
    }

    [Fact]
    public void FluentItemsPanelsPublishAndApplyHorizontalLayoutContracts()
    {
        var first = new Border { Width = 40f, Height = 20f };
        var second = new Border { Width = 60f, Height = 30f };
        var carousel = new CarouselPanel();
        carousel.Children.Add(first);
        carousel.Children.Add(second);
        carousel.Measure(new Vector2(80f, 40f));
        carousel.Arrange(new ProGPU.Scene.Rect(0f, 0f, 80f, 40f));

        Assert.True(carousel.AreHorizontalSnapPointsRegular);
        Assert.False(carousel.AreVerticalSnapPointsRegular);
        Assert.Equal(100f, carousel.TotalVirtualWidth);
        Assert.Equal(60f, carousel.GetRegularSnapPoints(
            Orientation.Horizontal,
            SnapPointsAlignment.Near,
            out var offset));
        Assert.Equal(0f, offset);
        Assert.Equal(40f, second.Offset.X);

        var pivot = new PivotPanel();
        var page = new Border { Width = 30f, Height = 20f };
        pivot.Children.Add(page);
        pivot.Measure(new Vector2(200f, 100f));
        pivot.Arrange(new ProGPU.Scene.Rect(0f, 0f, 200f, 100f));

        Assert.True(pivot.AreHorizontalSnapPointsRegular);
        Assert.Equal(30f, page.Size.X);
        Assert.IsAssignableFrom<Canvas>(new PivotHeaderPanel());
    }

    [Fact]
    public void FluentSelectionAndHubControlsRetainCanonicalContentModels()
    {
        var flipView = new FlipView();
        flipView.Items.Add("first");
        flipView.Items.Add("second");

        Assert.Equal(typeof(Selector), typeof(FlipView).BaseType);
        Assert.Equal(typeof(SelectorItem), typeof(FlipViewItem).BaseType);
        Assert.Equal(typeof(ContentControl), typeof(GroupItem).BaseType);
        Assert.Equal(typeof(ContentControl), typeof(PivotHeaderItem).BaseType);
        Assert.Equal(2, flipView.ItemsPanelRoot!.Children.Count);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)flipView.ItemsPanelRoot.Children[0]).Visibility);

        flipView.SelectedIndex = 1;

        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)flipView.ItemsPanelRoot.Children[0]).Visibility);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)flipView.ItemsPanelRoot.Children[1]).Visibility);

        var template = new DataTemplate();
        XamlTemplateFactory.SetFactory(template, static context => new Border { Tag = context });
        var section = new HubSection
        {
            Header = "News",
            ContentTemplate = template,
            IsHeaderInteractive = true
        };
        var hub = new Hub();
        HubSection? clicked = null;
        hub.SectionHeaderClick += (_, args) => clicked = args.Section;
        hub.Sections.Add(section);
        section.InvokeHeader();

        Assert.Same(section, Assert.Single(hub.Sections));
        Assert.Equal("News", Assert.Single(hub.SectionHeaders));
        Assert.Same(section, clicked);
    }

    [Fact]
    public void TimeAndCalendarPickersCoerceValuesAndRaiseTypedEvents()
    {
        var timePicker = new TimePicker { MinuteIncrement = 15 };
        TimePickerValueChangedEventArgs? timeChanged = null;
        timePicker.TimeChanged += (_, args) => timeChanged = args;
        timePicker.Time = TimeSpan.FromMinutes(38);

        Assert.Equal(TimeSpan.FromMinutes(30), timePicker.Time);
        Assert.Equal(timePicker.Time, timePicker.SelectedTime);
        Assert.Equal(TimeSpan.FromMinutes(30), timeChanged!.NewTime);
        Assert.Throws<ArgumentOutOfRangeException>(() => timePicker.MinuteIncrement = 0);

        var minimum = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var maximum = new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var datePicker = new CalendarDatePicker
        {
            MinDate = minimum,
            MaxDate = maximum,
            Date = maximum.AddDays(20),
            FirstDayOfWeek = DayOfWeek.Monday,
            IsCalendarOpen = true
        };

        Assert.Equal(maximum, datePicker.Date);
        Assert.Equal(DayOfWeek.Monday, datePicker.FirstDayOfWeek);
        Assert.True(datePicker.IsCalendarOpen);
        Assert.Same(datePicker, Assert.Single(datePicker.Children).Parent);
    }

    [Fact]
    public void AutoSuggestBoxReportsUserSuggestionAndQueryTransitions()
    {
        var control = new AutoSuggestBox { TextMemberPath = "Name" };
        var reasons = new List<AutoSuggestionBoxTextChangeReason>();
        object? chosen = null;
        AutoSuggestBoxQuerySubmittedEventArgs? submitted = null;
        control.TextChanged += (_, args) => reasons.Add(args.Reason);
        control.SuggestionChosen += (_, args) => chosen = args.SelectedItem;
        control.QuerySubmitted += (_, args) => submitted = args;

        control.SetUserText("pro");
        var suggestion = new Dictionary<string, object?> { ["Name"] = "ProGPU" };
        control.ChooseSuggestion(suggestion);
        control.SubmitQuery(suggestion);

        Assert.Equal(
            new[]
            {
                AutoSuggestionBoxTextChangeReason.UserInput,
                AutoSuggestionBoxTextChangeReason.SuggestionChosen
            },
            reasons);
        Assert.Equal("ProGPU", control.Text);
        Assert.Same(suggestion, chosen);
        Assert.Same(suggestion, submitted!.ChosenSuggestion);
    }

    [Fact]
    public void PickerPresentersAndLoopingSelectorMatchFluentContracts()
    {
        Assert.Equal(typeof(Control), typeof(DatePickerFlyoutPresenter).BaseType);
        Assert.Equal(typeof(Control), typeof(ListPickerFlyoutPresenter).BaseType);
        Assert.Equal(typeof(ContentControl), typeof(PickerFlyoutPresenter).BaseType);
        Assert.Equal(typeof(Control), typeof(TimePickerFlyoutPresenter).BaseType);
        Assert.Equal(typeof(ContentControl), typeof(LoopingSelectorItem).BaseType);

        var selector = new LoopingSelector { ShouldLoop = true };
        selector.Items.Add("one");
        selector.Items.Add("two");
        selector.SelectedIndex = 1;
        selector.MoveNext();

        Assert.Equal(0, selector.SelectedIndex);
        Assert.Equal("one", selector.SelectedItem);

        selector.MovePrevious();

        Assert.Equal(1, selector.SelectedIndex);
        Assert.Equal("two", selector.SelectedItem);
    }

    [Fact]
    public void MediaElementConnectsTypedPlaybackPresenterAndTransportState()
    {
        var source = new MediaSource();
        var element = new MediaPlayerElement
        {
            Source = source,
            AutoPlay = true,
            Stretch = Stretch.UniformToFill,
            IsFullWindow = true
        };

        Assert.Same(source, element.MediaPlayer.Source);
        Assert.Equal(Windows.Media.Playback.MediaPlaybackState.Playing, element.MediaPlayer.PlaybackState);
        Assert.NotNull(element.TransportControls);
        Assert.Equal(2, element.Children.Count);

        element.AreTransportControlsEnabled = false;

        Assert.Single(element.Children);
        element.TransportControls!.Hide();
        Assert.Equal(Visibility.Collapsed, element.TransportControls.Visibility);
        Assert.Equal(0, (int)FastPlayFallbackBehaviour.Skip);
        Assert.Equal(2, (int)FastPlayFallbackBehaviour.Disable);
    }

    [Fact]
    public void ListViewItemPresenterUsesTypedThemeChromeContracts()
    {
        var presenter = new ListViewItemPresenter
        {
            CheckMode = ListViewItemPresenterCheckMode.Overlay,
            SelectedBorderThickness = new Thickness(1, 2, 3, 4),
            SelectionIndicatorCornerRadius = new CornerRadius(2, 3, 4, 5)
        };

        Assert.Equal(ListViewItemPresenterCheckMode.Overlay, presenter.CheckMode);
        Assert.Equal(3d, presenter.SelectedBorderThickness.Right);
        Assert.Equal(5d, presenter.SelectionIndicatorCornerRadius.BottomLeft);
    }

    [Fact]
    public void DeferredTemplateRuntimeFactoryCreatesFreshTrees()
    {
        var template = new ControlTemplate { TargetType = typeof(Button) };
        XamlTemplateFactory.SetFactory(template, static context => new Border { Tag = context });

        var firstOwner = new Button();
        var secondOwner = new Button();
        var first = XamlTemplateFactory.Build(template, firstOwner);
        var second = XamlTemplateFactory.Build(template, secondOwner);

        Assert.NotSame(first, second);
        Assert.Same(firstOwner, ((Border)first!).Tag);
        Assert.Same(secondOwner, ((Border)second!).Tag);
    }

    [Fact]
    public void RectangleClipTracksGeometryChanges()
    {
        var geometry = new RectangleGeometry { Rect = new ProGPU.Scene.Rect(1, 2, 30, 40) };
        var element = new Grid { Clip = geometry };
        Assert.Equal(30f, element.ClipBounds!.Value.Width);

        geometry.Rect = new ProGPU.Scene.Rect(3, 4, 50, 60);

        Assert.Equal(50f, element.ClipBounds!.Value.Width);
        Assert.Equal(60f, element.ClipBounds!.Value.Height);
    }

    [Fact]
    public void TransformGroupCollectionAndChildrenPropagateChanges()
    {
        var group = new TransformGroup();
        var scale = new ScaleTransform();
        var changes = 0;
        group.Changed += (_, args) =>
        {
            if (args.Property == TransformGroup.ChildrenProperty) changes++;
        };

        group.Children.Add(scale);
        scale.ScaleX = 2f;

        Assert.IsType<TransformCollection>(group.Children);
        Assert.Equal(2, changes);
        Assert.Equal(2f, group.Value.M11);
    }

    [Fact]
    public void ToolTipServiceAttachedValueDrivesFrameworkElementHoverContract()
    {
        var target = new Grid();
        var content = new TextBlock { Text = "Details" };
        ToolTipService.SetToolTip(target, content);

        var toolTip = new ToolTip { Content = content };

        Assert.Same(content, ToolTipService.GetToolTip(target));
        Assert.Same(content, target.ToolTip);
        Assert.Same(content, toolTip.Content);
        Assert.IsAssignableFrom<ContentControl>(toolTip);
    }

    [Fact]
    public void ButtonHierarchyAndCommandActivationMatchWinUiContracts()
    {
        Assert.Equal(typeof(ButtonBase), typeof(Button).BaseType);
        Assert.Equal(typeof(ButtonBase), typeof(ToggleButton).BaseType);
        Assert.Equal(typeof(ButtonBase), typeof(RepeatButton).BaseType);
        Assert.Equal(typeof(ToggleButton), typeof(CheckBox).BaseType);
        Assert.Equal(typeof(ToggleButton), typeof(RadioButton).BaseType);

        var command = new RecordingCommand();
        var button = new Button { Command = command, CommandParameter = "run" };
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        button.OnKeyDown(new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.Enter });

        Assert.Equal("run", command.ExecutedParameter);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void ContentControlMaterializesSelectedDeferredDataTemplate()
    {
        var template = new DataTemplate();
        XamlTemplateFactory.SetFactory(template, static context => new Border { Tag = context });
        var control = new ContentControl
        {
            ContentTemplateSelector = new FixedTemplateSelector(template),
            ContentTransitions = new TransitionCollection { new ContentThemeTransition() },
            Content = "row"
        };

        var root = Assert.IsType<Border>(control.ContentTemplateRoot);
        Assert.Equal("row", root.Tag);
        Assert.Equal("row", root.DataContext);
        Assert.Single(control.ContentTransitions);
    }

    [Fact]
    public void SemanticZoomTransfersActiveViewAndSelectedItem()
    {
        var detailed = new ListView();
        var index = new GridView();
        detailed.Items.Add("A");
        index.Items.Add("A");
        detailed.SelectedItem = "A";
        var zoom = new SemanticZoom { ZoomedInView = detailed, ZoomedOutView = index };

        Assert.True(detailed.IsActiveView);
        Assert.Same(zoom, detailed.SemanticZoomOwner);
        Assert.Same(detailed, Assert.Single(zoom.Children));

        zoom.IsZoomedInViewActive = false;

        Assert.False(detailed.IsActiveView);
        Assert.True(index.IsActiveView);
        Assert.Equal("A", index.SelectedItem);
        Assert.Same(index, Assert.Single(zoom.Children));
    }

    [Fact]
    public void FluentTemplateSettingsExposeTypedCalculatedContracts()
    {
        Assert.IsType<AppBarTemplateSettings>(
            new AppBar().TemplateSettings);
        Assert.IsType<AppBarButtonTemplateSettings>(
            new AppBarButton().TemplateSettings);
        Assert.IsType<AppBarToggleButtonTemplateSettings>(
            new AppBarToggleButton().TemplateSettings);
        Assert.IsType<CalendarViewTemplateSettings>(
            new CalendarView().TemplateSettings);
        Assert.IsType<ToggleSwitchTemplateSettings>(
            new ToggleSwitch().TemplateSettings);

        var splitView = new SplitView
        {
            OpenPaneLength = 280,
            CompactPaneLength = 48
        };
        var split = splitView.TemplateSettings;
        Assert.Equal(280, split.OpenPaneLength);
        Assert.Equal(-280, split.NegativeOpenPaneLength);
        Assert.Equal(232, split.OpenPaneLengthMinusCompactLength);
        Assert.Equal(
            -232,
            split.NegativeOpenPaneLengthMinusCompactLength);
        Assert.Equal(280, split.OpenPaneGridLength.Value);
        Assert.Equal(48, split.CompactPaneGridLength.Value);
        Assert.Equal(
            GridUnitType.Absolute,
            split.OpenPaneGridLength.UnitType);

        var command = new CommandBar().CommandBarTemplateSettings;
        Assert.Equal(
            Visibility.Visible,
            command.EffectiveOverflowButtonVisibility);
        Assert.Equal(default, command.OverflowContentClipRect);
        Assert.Equal(0, command.OverflowContentHeight);
        Assert.Equal(0, command.NegativeOverflowContentHeight);
        Assert.Equal(0, command.OverflowContentHorizontalOffset);
        Assert.Equal(0, command.OverflowContentMaxWidth);
    }
}
