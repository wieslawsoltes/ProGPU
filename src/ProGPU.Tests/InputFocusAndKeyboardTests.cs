using Microsoft.UI.Content;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.WinUI.Platform;
using Silk.NET.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Xunit;

namespace ProGPU.Tests;

public sealed class InputFocusAndKeyboardTests
{
    [Fact]
    public void FocusNavigationRequestsRetainOfficialValueState()
    {
        var rect = new Rect(10, 20, 30, 40);
        Guid correlationId = Guid.NewGuid();

        FocusNavigationRequest basic =
            FocusNavigationRequest.Create(
                FocusNavigationReason.First);
        Assert.Equal(
            FocusNavigationReason.First,
            basic.Reason);
        Assert.Null(basic.HintRect);
        Assert.Equal(Guid.Empty, basic.CorrelationId);

        FocusNavigationRequest hinted =
            FocusNavigationRequest.Create(
                FocusNavigationReason.Right,
                rect);
        Assert.Equal(rect, hinted.HintRect);
        Assert.Equal(Guid.Empty, hinted.CorrelationId);

        FocusNavigationRequest correlated =
            FocusNavigationRequest.Create(
                FocusNavigationReason.Restore,
                rect,
                correlationId);
        Assert.Equal(rect, correlated.HintRect);
        Assert.Equal(
            correlationId,
            correlated.CorrelationId);
    }

    [Fact]
    public void FocusControllerIsStableAndTracksHostTransitions()
    {
        RunOnDispatcherThread(() =>
        {
            var root = new ContentControl();
            WindowInputState previous = InputSystem.Current;
            WindowInputState state =
                InputSystem.CreateExternalState(root);
            var island = new TestContentIsland();
            ContentIslandInputRegistration.Attach(
                island,
                state);

            try
            {
                InputSystem.Current = state;
                InputFocusController controller =
                    InputFocusController.GetForIsland(
                        island);
                Assert.Same(
                    controller,
                    InputFocusController.GetForIsland(
                        island));
                int gotFocus = 0;
                int lostFocus = 0;
                controller.GotFocus += (_, _) =>
                    gotFocus++;
                controller.LostFocus += (_, _) =>
                    lostFocus++;

                Assert.True(controller.TrySetFocus());
                Assert.True(controller.HasFocus);
                Assert.Equal(1, gotFocus);
                Assert.True(controller.TrySetFocus());
                Assert.Equal(1, gotFocus);

                InputSystem.InjectFocusLost();
                Assert.False(controller.HasFocus);
                Assert.Equal(1, lostFocus);
            }
            finally
            {
                island.Dispose();
                InputSystem.Current = previous;
            }
        });
    }

    [Fact]
    public void NavigationHostReturnsEventResultWithoutChangingFocus()
    {
        RunOnDispatcherThread(() =>
        {
            WindowInputState previous = InputSystem.Current;
            WindowInputState state =
                InputSystem.CreateExternalState(
                    new ContentControl());
            var island = new TestContentIsland();
            var link = new TestSiteLink(island);
            var bridge = new TestSiteBridge(
                island);
            ContentIslandInputRegistration.Attach(
                island,
                state);

            try
            {
                InputSystem.Current = state;
                InputFocusController controller =
                    InputFocusController.GetForIsland(
                        island);
                InputFocusNavigationHost bridgeHost =
                    InputFocusNavigationHost
                        .GetForSiteBridge(bridge);
                Assert.Same(
                    bridgeHost,
                    InputFocusNavigationHost
                        .GetForSiteBridge(bridge));
                InputFocusNavigationHost host =
                    InputFocusNavigationHost
                        .GetForSiteLink(link);
                FocusNavigationRequest request =
                    FocusNavigationRequest.Create(
                        FocusNavigationReason.First);
                controller.NavigateFocusRequested +=
                    (_, args) =>
                    {
                        Assert.Same(
                            request,
                            args.Request);
                        args.Result =
                            FocusNavigationResult.Moved;
                    };

                Assert.Equal(
                    FocusNavigationResult.Moved,
                    host.NavigateFocus(request));
                Assert.False(controller.HasFocus);
                Assert.False(host.ContainsFocus);

                host.DepartFocusRequested +=
                    (_, args) =>
                    {
                        Assert.Same(
                            request,
                            args.Request);
                        args.Result =
                            FocusNavigationResult
                                .NoFocusableElements;
                    };
                Assert.Equal(
                    FocusNavigationResult
                        .NoFocusableElements,
                    controller.DepartFocus(request));
            }
            finally
            {
                island.Dispose();
                InputSystem.Current = previous;
            }
        });
    }

    [Fact]
    public void KeyboardSourceRoutesNormalSystemContextAndCharacterEvents()
    {
        RunOnDispatcherThread(() =>
        {
            WindowInputState previous = InputSystem.Current;
            WindowInputState state =
                InputSystem.CreateExternalState(
                    new ContentControl());
            var island = new TestContentIsland();
            ContentIslandInputRegistration.Attach(
                island,
                state);

            try
            {
                InputSystem.Current = state;
                InputKeyboardSource source =
                    InputKeyboardSource.GetForIsland(
                        island);
                var events = new List<string>();
                source.KeyDown += (_, args) =>
                {
                    events.Add(
                        $"down:{args.VirtualKey}");
                    Assert.True(
                        source.GetKeyState(
                            args.VirtualKey)
                        .HasFlag(
                            VirtualKeyStates.Down));
                    args.Handled =
                        args.VirtualKey ==
                        VirtualKey.A;
                };
                source.KeyUp += (_, args) =>
                {
                    events.Add(
                        $"up:{args.VirtualKey}");
                    args.Handled = true;
                };
                source.SystemKeyDown += (_, args) =>
                {
                    events.Add(
                        $"system-down:{args.VirtualKey}");
                    args.Handled = true;
                };
                source.SystemKeyUp += (_, args) =>
                {
                    events.Add(
                        $"system-up:{args.VirtualKey}");
                    args.Handled = true;
                };
                source.CharacterReceived += (_, args) =>
                {
                    events.Add(
                        $"char:{args.KeyCode}");
                    args.Handled = true;
                };

                InputSystem.InjectKeyDown(Key.A);
                Assert.Equal(
                    VirtualKeyStates.Down,
                    source.GetCurrentKeyState(
                        VirtualKey.A));
                InputSystem.InjectKeyUp(Key.A);
                Assert.Equal(
                    VirtualKeyStates.None,
                    source.GetCurrentKeyState(
                        VirtualKey.A));
                InputSystem.InjectKeyDown(Key.AltLeft);
                InputSystem.InjectKeyUp(Key.AltLeft);
                InputSystem.InjectKeyChar('x');

                Assert.Equal(
                    [
                        "down:A",
                        "up:A",
                        "system-down:LeftMenu",
                        "system-up:LeftMenu",
                        $"char:{(uint)'x'}"
                    ],
                    events);

                int contextEvents = 0;
                source.ContextMenuKey += (_, args) =>
                {
                    contextEvents++;
                    args.Handled = true;
                };
                InputSystem.InjectKeyDown(Key.Menu);
                Assert.Equal(1, contextEvents);
                InputSystem.InjectKeyUp(Key.Menu);
                InputSystem.InjectKeyDown(Key.ShiftLeft);
                InputSystem.InjectKeyDown(Key.F10);
                Assert.Equal(2, contextEvents);
                InputSystem.InjectKeyUp(Key.F10);
                InputSystem.InjectKeyUp(Key.ShiftLeft);
            }
            finally
            {
                island.Dispose();
                InputSystem.Current = previous;
            }
        });
    }

    [Fact]
    public void LockedAndCurrentThreadKeyStateUseFixedStateMap()
    {
        RunOnDispatcherThread(() =>
        {
            WindowInputState previous = InputSystem.Current;
            WindowInputState state =
                InputSystem.CreateExternalState();
            var island = new TestContentIsland();
            ContentIslandInputRegistration.Attach(
                island,
                state);

            try
            {
                InputSystem.Current = state;
                InputKeyboardSource source =
                    InputKeyboardSource.GetForIsland(
                        island);
                InputSystem.InjectKeyDown(
                    Key.CapsLock);
                Assert.Equal(
                    VirtualKeyStates.Down |
                    VirtualKeyStates.Locked,
                    source.GetCurrentKeyState(
                        VirtualKey.CapitalLock));
                Assert.Equal(
                    CoreVirtualKeyStates.Down |
                    CoreVirtualKeyStates.Locked,
                    InputKeyboardSource
                        .GetKeyStateForCurrentThread(
                            VirtualKey.CapitalLock));
                InputSystem.InjectKeyUp(
                    Key.CapsLock);
                Assert.Equal(
                    VirtualKeyStates.Locked,
                    source.GetCurrentKeyState(
                        VirtualKey.CapitalLock));
            }
            finally
            {
                island.Dispose();
                InputSystem.Current = previous;
            }
        });
    }

    [Fact]
    public void KeyStateQueriesAreAllocationFreeAfterWarmup()
    {
        RunOnDispatcherThread(() =>
        {
            const int Count = 100_000;
            WindowInputState previous = InputSystem.Current;
            WindowInputState state =
                InputSystem.CreateExternalState();
            var island = new TestContentIsland();
            ContentIslandInputRegistration.Attach(
                island,
                state);

            try
            {
                InputSystem.Current = state;
                InputKeyboardSource source =
                    InputKeyboardSource.GetForIsland(
                        island);
                InputSystem.InjectKeyDown(Key.A);
                int checksum = QueryState(
                    source,
                    Count);
                _ = GC.GetAllocatedBytesForCurrentThread();
                long before =
                    GC.GetAllocatedBytesForCurrentThread();
                checksum ^= QueryState(
                    source,
                    Count);
                long allocated =
                    GC.GetAllocatedBytesForCurrentThread() -
                    before;
                GC.KeepAlive(checksum);
                Assert.Equal(0, allocated);
            }
            finally
            {
                island.Dispose();
                InputSystem.Current = previous;
            }
        });
    }

    [Fact]
    public void InputObjectsEnforceDispatcherThreadAffinity()
    {
        RunOnDispatcherThread(() =>
        {
            var island = new TestContentIsland();
            ContentIslandInputRegistration.Attach(
                island,
                InputSystem.CreateExternalState());
            InputKeyboardSource source =
                InputKeyboardSource.GetForIsland(
                    island);

            Exception? exception = Task.Run(
                () => Record.Exception(
                    () => source.GetCurrentKeyState(
                        VirtualKey.A)))
                .GetAwaiter()
                .GetResult();

            Assert.IsType<InvalidOperationException>(
                exception);
            island.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => InputKeyboardSource
                    .GetForIsland(island));
        });
    }

    [Fact]
    public void IslandDisposeRaisesFrameworkClosedBeforeClosed()
    {
        RunOnDispatcherThread(() =>
        {
            var island = new TestContentIsland();
            var events = new List<string>();
            island.FrameworkClosed += () =>
                events.Add("framework");
            island.Closed += () =>
                events.Add("closed");

            island.Dispose();
            island.Dispose();

            Assert.Equal(
                ["framework", "closed"],
                events);
        });
    }

    private static int QueryState(
        InputKeyboardSource source,
        int count)
    {
        int checksum = 0;
        for (int index = 0;
             index < count;
             index++)
        {
            checksum ^=
                (int)source.GetCurrentKeyState(
                    VirtualKey.A);
            checksum ^=
                (int)source.GetKeyState(
                    VirtualKey.A);
        }
        return checksum;
    }

    private static void RunOnDispatcherThread(
        Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            DispatcherQueueController controller =
                DispatcherQueueController
                    .CreateOnCurrentThread();
            try
            {
                action();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
            finally
            {
                controller.ShutdownQueue();
            }
        });
        thread.Start();
        thread.Join();
        if (exception is not null)
            throw exception;
    }

    private sealed class TestContentIsland :
        ContentIsland
    {
        public TestContentIsland()
            : base(new WinRT.DerivedComposed())
        {
        }
    }

    private sealed class TestSiteLink :
        IContentSiteLink
    {
        public TestSiteLink(
            ContentIsland parent)
        {
            Parent = parent;
        }

        public ContentIsland Parent { get; }
    }

    private sealed class TestSiteBridge :
        IContentSiteBridge,
        IContentIslandSiteProvider
    {
        public TestSiteBridge(
            ContentIsland island)
        {
            ContentIsland = island;
            DispatcherQueue =
                island.DispatcherQueue;
        }

        public ContentIsland? ContentIsland { get; }

        public DispatcherQueue DispatcherQueue { get; }

        public ContentLayoutDirection?
            LayoutDirectionOverride { get; set; }

        public float OverrideScale { get; set; }

        public void Dispose()
        {
        }
    }
}
