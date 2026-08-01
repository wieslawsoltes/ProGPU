using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using ProGPU.WinUI.Platform;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Xunit;

namespace ProGPU.Tests;

public sealed class AppWindowTests
{
    [Fact]
    public void AppWindowRetainsIdentityGeometryPresenterAndLifecycle()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        AppWindow? window = null;
        try
        {
            window = AppWindow.Create();
            Assert.NotEqual(default, window.Id);
            Assert.Same(
                window,
                AppWindow.GetFromWindowId(window.Id));
            Assert.Same(
                controller.DispatcherQueue,
                window.DispatcherQueue);
            Assert.Equal(
                AppWindowPresenterKind.Overlapped,
                window.Presenter.Kind);

            var changes = new List<AppWindowChangedEventArgs>();
            window.Changed += (_, args) => changes.Add(args);
            window.Move(new PointInt32(120, 80));
            window.Resize(new SizeInt32(900, 600));
            window.SetPresenter(
                CompactOverlayPresenter.Create());

            Assert.Equal(new PointInt32(120, 80), window.Position);
            Assert.Equal(new SizeInt32(900, 600), window.Size);
            Assert.True(changes[0].DidPositionChange);
            Assert.True(changes[1].DidSizeChange);
            Assert.True(changes[2].DidPresenterChange);

            int closing = 0;
            int destroying = 0;
            window.Closing += (_, args) =>
            {
                closing++;
                if (closing == 1)
                    args.Cancel = true;
            };
            window.Destroying += (_, _) => destroying++;

            window.Destroy();
            Assert.Same(
                window,
                AppWindow.GetFromWindowId(window.Id));
            Assert.Equal(0, destroying);

            window.Destroy();
            Assert.Null(AppWindow.GetFromWindowId(window.Id));
            Assert.Equal(2, closing);
            Assert.Equal(1, destroying);
            Assert.Throws<ObjectDisposedException>(
                () => window.Move(default));
        }
        finally
        {
            window?.Destroy();
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void WindowCloseRequestRaisesCancellableAppWindowClosingFirst()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        AppWindow? window = null;
        try
        {
            window = AppWindow.Create();
            int closing = 0;
            window.Closing += (_, args) =>
            {
                closing++;
                args.Cancel = closing == 1;
            };

            Assert.False(window.XamlWindow.TryClose());
            Assert.Same(window, AppWindow.GetFromWindowId(window.Id));

            Assert.True(window.XamlWindow.TryClose());
            Assert.Equal(2, closing);
            Assert.Null(AppWindow.GetFromWindowId(window.Id));
            window = null;
        }
        finally
        {
            window?.Destroy();
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void NativePositionChangeUpdatesAppWindowOnce()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        AppWindow? window = null;
        try
        {
            window = AppWindow.Create();
            var changes = new List<AppWindowChangedEventArgs>();
            window.Changed += (_, args) => changes.Add(args);

            window.XamlWindow.NotifyHostPositionChanged(
                new PointInt32(640, 320));
            window.XamlWindow.NotifyHostPositionChanged(
                new PointInt32(640, 320));

            Assert.Equal(
                new PointInt32(640, 320),
                window.Position);
            Assert.Equal(640d, window.XamlWindow.Bounds.X);
            Assert.Equal(320d, window.XamlWindow.Bounds.Y);
            Assert.Single(changes);
            Assert.True(changes[0].DidPositionChange);
        }
        finally
        {
            window?.Destroy();
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void ModalOwnerStaysDisabledUntilItsLastModalChildIsReleased()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        AppWindow? owner = null;
        AppWindow? first = null;
        AppWindow? second = null;
        try
        {
            owner = AppWindow.Create();
            OverlappedPresenter firstPresenter =
                OverlappedPresenter.CreateForDialog();
            firstPresenter.IsModal = true;
            OverlappedPresenter secondPresenter =
                OverlappedPresenter.CreateForDialog();
            secondPresenter.IsModal = true;
            first = AppWindow.Create(firstPresenter, owner.Id);
            second = AppWindow.Create(secondPresenter, owner.Id);

            Assert.False(owner.XamlWindow.IsEnabled);
            owner.SetPresenter(CompactOverlayPresenter.Create());
            Assert.False(owner.XamlWindow.IsEnabled);

            first.SetPresenter(CompactOverlayPresenter.Create());
            Assert.False(owner.XamlWindow.IsEnabled);

            second.Destroy();
            second = null;
            Assert.True(owner.XamlWindow.IsEnabled);
        }
        finally
        {
            first?.Destroy();
            second?.Destroy();
            owner?.Destroy();
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void CompactOverlayLeavesFullscreenBeforeApplyingItsState()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        AppWindow? window = null;
        try
        {
            window = AppWindow.Create(FullScreenPresenter.Create());
            Assert.Equal(
                Silk.NET.Windowing.WindowState.Fullscreen,
                window.XamlWindow.NativeWindowState);

            window.SetPresenter(CompactOverlayPresenter.Create());

            Assert.Equal(
                Silk.NET.Windowing.WindowState.Normal,
                window.XamlWindow.NativeWindowState);
        }
        finally
        {
            window?.Destroy();
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void AppWindowUsesTypedIconAndZOrderProvider()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        IAppWindowPlatformProvider? previous =
            WindowingPlatformServices.AppWindows;
        AppWindow? window = null;
        try
        {
            var provider = new TestAppWindowProvider();
            WindowingPlatformServices.AppWindows = provider;
            window = AppWindow.Create();
            var zOrderChanges = new List<AppWindowChangedEventArgs>();
            window.Changed += (_, args) =>
            {
                if (args.DidZOrderChange)
                    zOrderChanges.Add(args);
            };

            window.SetIcon(new IconId(11));
            window.SetTaskbarIcon("taskbar.ico");
            window.SetTitleBarIcon(new IconId(12));
            window.MoveInZOrderAtTop();
            window.MoveInZOrderBelow(new WindowId(99));
            window.MoveInZOrderAtBottom();

            Assert.Equal(11UL, provider.WindowIcon.Value);
            Assert.Equal("taskbar.ico", provider.TaskbarPath);
            Assert.Equal(12UL, provider.TitleBarIcon.Value);
            Assert.True(zOrderChanges[0].IsZOrderAtTop);
            Assert.Equal(
                new WindowId(99),
                zOrderChanges[1].ZOrderBelowWindowId);
            Assert.True(zOrderChanges[2].IsZOrderAtBottom);
        }
        finally
        {
            window?.Destroy();
            WindowingPlatformServices.AppWindows = previous;
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void AppWindowFailsExplicitlyForMissingPlatformOperation()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        IAppWindowPlatformProvider? previous =
            WindowingPlatformServices.AppWindows;
        AppWindow? window = null;
        try
        {
            WindowingPlatformServices.AppWindows = null;
            window = AppWindow.Create();

            Assert.Throws<PlatformNotSupportedException>(
                () => window.SetIcon(new IconId(1)));
            Assert.Throws<PlatformNotSupportedException>(
                window.MoveInZOrderAtTop);
        }
        finally
        {
            window?.Destroy();
            WindowingPlatformServices.AppWindows = previous;
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void DispatcherShutdownDestroysAssociatedAppWindow()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        AppWindow window = AppWindow.Create();
        int destroying = 0;
        window.Closing += (_, args) => args.Cancel = true;
        window.Destroying += (_, _) => destroying++;

        controller.ShutdownQueue();

        Assert.Null(AppWindow.GetFromWindowId(window.Id));
        Assert.Equal(1, destroying);
    }

    [Fact]
    public void AppWindowTitleBarRetainsOfficialOptions()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        AppWindow? window = null;
        try
        {
            window = AppWindow.Create();
            AppWindowTitleBar titleBar = window.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.PreferredHeightOption =
                TitleBarHeightOption.Tall;
            titleBar.PreferredTheme = TitleBarTheme.Dark;
            titleBar.IconShowOptions =
                IconShowOptions.HideIconAndSystemMenu;
            titleBar.BackgroundColor = Colors.Navy;
            titleBar.SetDragRectangles(
                [new RectInt32(0, 0, 600, 48)]);

            Assert.True(titleBar.ExtendsContentIntoTitleBar);
            Assert.Equal(48, titleBar.Height);
            Assert.Equal(TitleBarTheme.Dark, titleBar.PreferredTheme);
            Assert.Equal(
                IconShowOptions.HideIconAndSystemMenu,
                titleBar.IconShowOptions);
            Assert.Equal(Colors.Navy, titleBar.BackgroundColor);

            titleBar.ResetToDefault();
            Assert.False(titleBar.ExtendsContentIntoTitleBar);
            Assert.Equal(
                TitleBarHeightOption.Standard,
                titleBar.PreferredHeightOption);
            Assert.Null(titleBar.BackgroundColor);
        }
        finally
        {
            window?.Destroy();
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void DisplayAreaQueriesUseTypedPlatformSnapshots()
    {
        IWindowingDisplayAreaProvider? previous =
            WindowingPlatformServices.DisplayAreas;
        try
        {
            var provider = new TestDisplayAreaProvider();
            WindowingPlatformServices.DisplayAreas = provider;

            Assert.Equal(new DisplayId(1), DisplayArea.Primary.DisplayId);
            Assert.Equal(
                new DisplayId(2),
                DisplayArea.GetFromPoint(
                    new PointInt32(2_100, 100),
                    DisplayAreaFallback.None).DisplayId);
            Assert.Equal(
                new DisplayId(1),
                DisplayArea.GetFromRect(
                    new RectInt32(1_800, 20, 200, 100),
                    DisplayAreaFallback.Primary).DisplayId);
            Assert.Equal(
                new DisplayId(2),
                DisplayArea.GetFromPoint(
                    new PointInt32(4_000, 100),
                    DisplayAreaFallback.Nearest).DisplayId);
            Assert.Null(
                DisplayArea.GetFromPoint(
                    new PointInt32(-10_000, -10_000),
                    DisplayAreaFallback.None));
        }
        finally
        {
            WindowingPlatformServices.DisplayAreas = previous;
        }
    }

    [Fact]
    public void DisplayAreaWatcherPublishesStableStateTransitions()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        IWindowingDisplayAreaProvider? previous =
            WindowingPlatformServices.DisplayAreas;
        try
        {
            var provider = new TestDisplayAreaProvider();
            WindowingPlatformServices.DisplayAreas = provider;
            DisplayAreaWatcher watcher = DisplayArea.CreateWatcher();
            var events = new List<string>();
            watcher.Added += (_, area) =>
                events.Add($"added:{area.DisplayId.Value}");
            watcher.Updated += (_, area) =>
                events.Add($"updated:{area.DisplayId.Value}");
            watcher.Removed += (_, area) =>
                events.Add($"removed:{area.DisplayId.Value}");
            watcher.EnumerationCompleted += (_, _) =>
                events.Add("enumerated");
            watcher.Stopped += (_, _) =>
                events.Add("stopped");

            watcher.Start();
            Assert.Equal(
                DisplayAreaWatcherStatus.EnumerationCompleted,
                watcher.Status);

            provider.Publish(
                [
                    new(
                        new DisplayId(1),
                        new RectInt32(0, 0, 1920, 1080),
                        new RectInt32(0, 0, 1920, 1000),
                        true),
                    new(
                        new DisplayId(3),
                        new RectInt32(1920, 0, 1280, 1024),
                        new RectInt32(1920, 0, 1280, 984),
                        false)
                ]);
            watcher.Stop();

            Assert.Equal(
                [
                    "added:1",
                    "added:2",
                    "enumerated",
                    "updated:1",
                    "added:3",
                    "removed:2",
                    "stopped"
                ],
                events);
            Assert.Equal(
                DisplayAreaWatcherStatus.Stopped,
                watcher.Status);
        }
        finally
        {
            WindowingPlatformServices.DisplayAreas = previous;
            controller.ShutdownQueue();
        }
    }

    [Theory]
    [InlineData(typeof(AppWindow), 0x00010000u)]
    [InlineData(typeof(AppWindowChangedEventArgs), 0x00010000u)]
    [InlineData(typeof(AppWindowClosingEventArgs), 0x00010000u)]
    [InlineData(typeof(AppWindowTitleBar), 0x00010000u)]
    [InlineData(typeof(DisplayArea), 0x00010000u)]
    [InlineData(typeof(DisplayAreaWatcher), 0x00010000u)]
    public void WindowingTypesPublishOfficialContractVersion(
        Type type,
        uint expectedVersion)
    {
        var attribute = Assert.Single(
            type.GetCustomAttributesData(),
            candidate =>
                candidate.AttributeType ==
                typeof(ContractVersionAttribute));
        Assert.Equal(
            expectedVersion,
            attribute.ConstructorArguments[1].Value);
    }

    [Fact]
    public void AppWindowPropertyReadsAreAllocationFree()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        AppWindow? window = null;
        try
        {
            window = AppWindow.Create();
            _ = window.Id;
            long before = GC.GetAllocatedBytesForCurrentThread();
            ulong sum = 0;
            for (int index = 0; index < 100_000; index++)
            {
                sum += window.Id.Value;
                sum += (ulong)window.Position.X;
                sum += (ulong)window.Size.Width;
                sum += (ulong)window.Presenter.Kind;
            }

            long allocated =
                GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.NotEqual(0UL, sum);
            Assert.Equal(0, allocated);
        }
        finally
        {
            window?.Destroy();
            controller.ShutdownQueue();
        }
    }

    private sealed class TestDisplayAreaProvider :
        IWindowingDisplayAreaProvider
    {
        private IReadOnlyList<WindowingDisplayAreaInfo> _areas =
        [
            new(
                new DisplayId(1),
                new RectInt32(0, 0, 1920, 1080),
                new RectInt32(0, 0, 1920, 1040),
                true),
            new(
                new DisplayId(2),
                new RectInt32(1920, 0, 1920, 1080),
                new RectInt32(1920, 0, 1920, 1040),
                false)
        ];

        public event EventHandler? DisplayAreasChanged;

        public IReadOnlyList<WindowingDisplayAreaInfo> GetDisplayAreas() =>
            _areas;

        public void Publish(
            IReadOnlyList<WindowingDisplayAreaInfo> areas)
        {
            _areas = areas;
            DisplayAreasChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class TestAppWindowProvider :
        IAppWindowPlatformProvider
    {
        public IconId WindowIcon { get; private set; }

        public string? TaskbarPath { get; private set; }

        public IconId TitleBarIcon { get; private set; }

        public bool TrySetIcon(WindowId windowId, IconId iconId)
        {
            WindowIcon = iconId;
            return true;
        }

        public bool TrySetIcon(WindowId windowId, string iconPath) => true;

        public bool TrySetTaskbarIcon(
            WindowId windowId,
            IconId iconId) => true;

        public bool TrySetTaskbarIcon(
            WindowId windowId,
            string iconPath)
        {
            TaskbarPath = iconPath;
            return true;
        }

        public bool TrySetTitleBarIcon(
            WindowId windowId,
            IconId iconId)
        {
            TitleBarIcon = iconId;
            return true;
        }

        public bool TrySetTitleBarIcon(
            WindowId windowId,
            string iconPath) => true;

        public bool TryMoveInZOrder(
            WindowId windowId,
            WindowId belowWindowId,
            bool atTop,
            bool atBottom) => true;
    }
}
