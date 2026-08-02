using Microsoft.UI.Content;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.WinUI.Platform;
using ProGPU.WinUI.Input;
using Silk.NET.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Microsoft.UI.Windowing;
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

    [Fact]
    public void ActivationListenerTracksIslandFocusChangesOnly()
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
                InputActivationListener listener =
                    InputActivationListener
                        .GetForIsland(island);
                Assert.Same(
                    listener,
                    InputActivationListener
                        .GetForIsland(island));
                Assert.Equal(
                    InputActivationState.Deactivated,
                    listener.State);
                int changes = 0;
                listener.InputActivationChanged +=
                    (_, _) => changes++;

                Assert.True(
                    InputFocusController
                        .GetForIsland(island)
                        .TrySetFocus());
                Assert.Equal(
                    InputActivationState.Activated,
                    listener.State);
                Assert.Equal(1, changes);

                Assert.True(
                    InputFocusController
                        .GetForIsland(island)
                        .TrySetFocus());
                Assert.Equal(1, changes);

                InputSystem.InjectFocusLost();
                Assert.Equal(
                    InputActivationState.Deactivated,
                    listener.State);
                Assert.Equal(2, changes);
            }
            finally
            {
                island.Dispose();
                InputSystem.Current = previous;
            }
        });
    }

    [Fact]
    public void ActivationListenerTracksAppWindowAndInvalidLookups()
    {
        RunOnDispatcherThread(() =>
        {
            AppWindow appWindow =
                AppWindow.Create();
            InputActivationListener listener =
                InputActivationListener
                    .GetForWindowId(appWindow.Id);
            Assert.Same(
                listener,
                InputActivationListener
                    .GetForWindowId(appWindow.Id));
            Assert.Equal(
                InputActivationState.Deactivated,
                listener.State);
            Assert.Null(
                InputActivationListener
                    .GetForWindowId(default));
            int changes = 0;
            listener.InputActivationChanged +=
                (_, _) => changes++;

            Assert.True(
                InputActivationRegistration
                    .NotifyWindowActivation(
                        appWindow.Id,
                        InputActivationState.Activated));
            Assert.Equal(
                InputActivationState.Activated,
                listener.State);
            Assert.Equal(1, changes);
            Assert.True(
                InputActivationRegistration
                    .NotifyWindowActivation(
                        appWindow.Id,
                        InputActivationState.Activated));
            Assert.Equal(1, changes);

            Assert.Null(Task.Run(
                () => InputActivationListener
                    .GetForWindowId(appWindow.Id))
                .GetAwaiter()
                .GetResult());

            appWindow.Destroy();
            Assert.Equal(
                InputActivationState.None,
                listener.State);
            Assert.False(
                InputActivationRegistration
                    .NotifyWindowActivation(
                        appWindow.Id,
                        InputActivationState.Deactivated));
        });
    }

    [Fact]
    public void PreTranslateKeyboardSourceIsStableAndSameThreadOnly()
    {
        RunOnDispatcherThread(() =>
        {
            var island = new TestContentIsland();
            InputPreTranslateKeyboardSource source =
                InputPreTranslateKeyboardSource
                    .GetForIsland(island);
            Assert.Same(
                source,
                InputPreTranslateKeyboardSource
                    .GetForIsland(island));
            Assert.Null(Task.Run(
                () => InputPreTranslateKeyboardSource
                    .GetForIsland(island))
                .GetAwaiter()
                .GetResult());

            island.Dispose();
            Assert.Null(
                InputPreTranslateKeyboardSource
                    .GetForIsland(island));
        });
    }

    [Fact]
    public void LightDismissActionIsStableAndSameThreadOnly()
    {
        RunOnDispatcherThread(() =>
        {
            AppWindow appWindow =
                AppWindow.Create();
            InputLightDismissAction action =
                InputLightDismissAction
                    .GetForWindowId(appWindow.Id);

            Assert.Same(
                action,
                InputLightDismissAction
                    .GetForWindowId(appWindow.Id));
            Assert.Null(
                InputLightDismissAction
                    .GetForWindowId(default));
            Assert.Null(Task.Run(
                () => InputLightDismissAction
                    .GetForWindowId(appWindow.Id))
                .GetAwaiter()
                .GetResult());
            Assert.False(Task.Run(
                () => InputLightDismissRegistration
                    .Notify(appWindow.Id))
                .GetAwaiter()
                .GetResult());

            appWindow.Destroy();
            Assert.Null(
                InputLightDismissAction
                    .GetForWindowId(appWindow.Id));
        });
    }

    [Fact]
    public void LightDismissTracksActivationLossAndTypedTriggers()
    {
        RunOnDispatcherThread(() =>
        {
            AppWindow appWindow =
                AppWindow.Create();
            InputLightDismissAction action =
                InputLightDismissAction
                    .GetForWindowId(appWindow.Id);
            int dismissed = 0;
            action.Dismissed += (_, _) =>
                dismissed++;

            Assert.True(
                InputActivationRegistration
                    .NotifyWindowActivation(
                        appWindow.Id,
                        InputActivationState.Deactivated));
            Assert.Equal(0, dismissed);
            Assert.True(
                InputActivationRegistration
                    .NotifyWindowActivation(
                        appWindow.Id,
                        InputActivationState.Activated));
            Assert.Equal(0, dismissed);
            Assert.True(
                InputActivationRegistration
                    .NotifyWindowActivation(
                        appWindow.Id,
                        InputActivationState.Deactivated));
            Assert.Equal(1, dismissed);
            Assert.True(
                InputActivationRegistration
                    .NotifyWindowActivation(
                        appWindow.Id,
                        InputActivationState.Deactivated));
            Assert.Equal(1, dismissed);

            Assert.True(
                InputLightDismissRegistration
                    .Notify(appWindow.Id));
            Assert.Equal(2, dismissed);

            appWindow.Destroy();
            Assert.False(
                InputLightDismissRegistration
                    .Notify(appWindow.Id));
            Assert.False(
                InputActivationRegistration
                    .NotifyWindowActivation(
                        default,
                        InputActivationState.Deactivated));
        });
    }

    [Fact]
    public void LightDismissTypedTriggerRequiresAnExistingAction()
    {
        RunOnDispatcherThread(() =>
        {
            AppWindow appWindow =
                AppWindow.Create();

            Assert.False(
                InputLightDismissRegistration
                    .Notify(appWindow.Id));
            _ = InputLightDismissAction
                .GetForWindowId(appWindow.Id);
            Assert.True(
                InputLightDismissRegistration
                    .Notify(appWindow.Id));

            appWindow.Destroy();
        });
    }

    [Fact]
    public void SubscriberFreeLightDismissTriggersAreAllocationFree()
    {
        RunOnDispatcherThread(() =>
        {
            const int Count = 100_000;
            AppWindow appWindow =
                AppWindow.Create();
            _ = InputLightDismissAction
                .GetForWindowId(appWindow.Id);
            Assert.True(
                InputLightDismissRegistration
                    .Notify(appWindow.Id));

            int warmDelivered = NotifyLightDismiss(
                appWindow.Id,
                Count);
            _ = GC.GetAllocatedBytesForCurrentThread();
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            int delivered = NotifyLightDismiss(
                appWindow.Id,
                Count);
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;

            Assert.Equal(Count, warmDelivered);
            Assert.Equal(Count, delivered);
            Assert.Equal(0, allocated);
            appWindow.Destroy();
        });
    }

    [Fact]
    public void ActivationStateReadsAreAllocationFree()
    {
        RunOnDispatcherThread(() =>
        {
            const int Count = 100_000;
            var island = new TestContentIsland();
            ContentIslandInputRegistration.Attach(
                island,
                InputSystem.CreateExternalState());
            InputActivationListener listener =
                InputActivationListener
                    .GetForIsland(island);
            int checksum = ReadActivationState(
                listener,
                Count);
            _ = GC.GetAllocatedBytesForCurrentThread();
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            checksum ^= ReadActivationState(
                listener,
                Count);
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;
            GC.KeepAlive(checksum);
            Assert.Equal(0, allocated);
            island.Dispose();
        });
    }

    [Fact]
    public void PointerSourceIsStableAndPublishesOrderedHandledEvents()
    {
        RunOnDispatcherThread(() =>
        {
            WindowInputState previous =
                InputSystem.Current;
            WindowInputState state =
                InputSystem.CreateExternalState();
            var island = new TestContentIsland();
            ContentIslandInputRegistration.Attach(
                island,
                state);

            try
            {
                InputSystem.Current = state;
                InputPointerSource source =
                    InputPointerSource.GetForIsland(
                        island);
                Assert.Same(
                    source,
                    InputPointerSource.GetForIsland(
                        island));
                Assert.Equal(
                    InputPointerSourceDeviceKinds
                        .Touch |
                    InputPointerSourceDeviceKinds.Pen |
                    InputPointerSourceDeviceKinds.Mouse,
                    source.DeviceKinds);

                var order = new List<string>();
                PointerEventArgs? pressed = null;
                source.PointerEntered += (_, _) =>
                    order.Add("entered");
                source.PointerPressed += (_, args) =>
                {
                    order.Add("pressed");
                    pressed = args;
                    args.Handled = true;
                };
                source.PointerReleased += (_, _) =>
                    order.Add("released");
                source.PointerExited += (_, _) =>
                    order.Add("exited");
                source.PointerCaptureLost += (_, _) =>
                    order.Add("capture-lost");

                InputSystem.InjectPointer(
                    CreatePointerInput(
                        PointerInputKind.Pressed,
                        isInContact: true));

                Assert.Equal(
                    ["entered", "pressed"],
                    order);
                Assert.NotNull(pressed);
                Assert.Equal(
                    7u,
                    pressed!.CurrentPoint.PointerId);
                Assert.Equal(
                    10d,
                    pressed.CurrentPoint.Position.X);
                Assert.Equal(
                    Windows.System
                        .VirtualKeyModifiers.Control,
                    pressed.KeyModifiers);
                Assert.True(pressed.Handled);

                InputSystem.InjectPointer(
                    CreatePointerInput(
                        PointerInputKind.Released,
                        isInContact: false));
                Assert.Equal(
                    [
                        "entered",
                        "pressed",
                        "released",
                        "exited"
                    ],
                    order);

                order.Clear();
                InputSystem.InjectPointer(
                    CreatePointerInput(
                        PointerInputKind.Pressed,
                        isInContact: true));
                InputSystem.InjectPointer(
                    CreatePointerInput(
                        PointerInputKind.Canceled,
                        isInContact: false));
                Assert.Equal(
                    [
                        "entered",
                        "pressed",
                        "capture-lost"
                    ],
                    order);
            }
            finally
            {
                island.Dispose();
                InputSystem.Current = previous;
            }
        });
    }

    [Fact]
    public void PointerSourceCursorUsesTypedProviderAndDetaches()
    {
        RunOnDispatcherThread(() =>
        {
            WindowInputState state =
                InputSystem.CreateExternalState();
            var provider =
                new TestCursorProvider();
            InputCursorProviderRegistration
                .SetProvider(state, provider);
            var island = new TestContentIsland();
            ContentIslandInputRegistration.Attach(
                island,
                state);
            InputPointerSource source =
                InputPointerSource.GetForIsland(
                    island);

            var hand = InputSystemCursor.Create(
                InputSystemCursorShape.Hand);
            source.Cursor = hand;

            Assert.Same(hand, provider.Cursor);
            island.Dispose();
            Assert.Null(provider.Cursor);
            Assert.False(
                InputPointerSourceRegistration.Raise(
                    source,
                    InputPointerSourceEventKind
                        .RoutedAway,
                    new PointerEventArgs(
                        CreatePoint())));
        });
    }

    [Fact]
    public void PointerSourceInvalidOrCrossThreadIslandReturnsNull()
    {
        RunOnDispatcherThread(() =>
        {
            var island = new TestContentIsland();
            InputPointerSource? otherThreadResult =
                InputPointerSource.GetForIsland(
                    island);
            var thread = new Thread(() =>
            {
                otherThreadResult =
                    InputPointerSource.GetForIsland(
                        island);
            });
            thread.Start();
            thread.Join();
            Assert.Null(otherThreadResult);

            island.Dispose();
            Assert.Null(
                InputPointerSource.GetForIsland(
                    island));
        });
    }

    [Fact]
    public void SubscriberFreePointerSourceDispatchIsAllocationFree()
    {
        RunOnDispatcherThread(() =>
        {
            const int Count = 100_000;
            var island = new TestContentIsland();
            InputPointerSource source =
                InputPointerSource.GetForIsland(
                    island);
            PointerInputEvent input =
                CreatePointerInput(
                    PointerInputKind.Moved,
                    isInContact: false);
            _ = source.Process(input);

            _ = GC.GetAllocatedBytesForCurrentThread();
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            int handled = 0;
            for (int index = 0;
                 index < Count;
                 index++)
            {
                if (source.Process(input))
                    handled++;
            }
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;

            Assert.Equal(0, handled);
            Assert.Equal(0, allocated);
            island.Dispose();
        });
    }

    private static PointerInputEvent CreatePointerInput(
        PointerInputKind kind,
        bool isInContact) =>
        new(
            kind,
            7,
            Windows.Devices.Input
                .PointerDeviceType.Touch,
            new System.Numerics.Vector2(10, 20),
            100,
            IsPrimary: true,
            IsInContact: isInContact,
            Pressure: isInContact ? 0.5f : 0f,
            ContactRect:
                new ProGPU.Scene.Rect(
                    9,
                    19,
                    2,
                    2),
            Modifiers:
                Microsoft.UI.Xaml.Input
                    .VirtualKeyModifiers.Control);

    private static PointerPoint CreatePoint() =>
        new(
            7,
            100,
            new System.Numerics.Vector2(10, 20),
            new System.Numerics.Vector2(10, 20),
            Windows.Devices.Input
                .PointerDeviceType.Touch,
            true,
            new PointerPointProperties());

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

    private static int ReadActivationState(
        InputActivationListener listener,
        int count)
    {
        int checksum = 0;
        for (int index = 0;
             index < count;
             index++)
        {
            checksum ^= (int)listener.State;
        }
        return checksum;
    }

    private static int NotifyLightDismiss(
        Microsoft.UI.WindowId windowId,
        int count)
    {
        int delivered = 0;
        for (int index = 0;
             index < count;
             index++)
        {
            if (InputLightDismissRegistration
                .Notify(windowId))
            {
                delivered++;
            }
        }

        return delivered;
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

    private sealed class TestCursorProvider :
        IInputCursorProvider
    {
        public InputCursor? Cursor { get; private set; }

        public void SetCursor(
            InputCursor? cursor)
        {
            Cursor = cursor;
        }
    }
}
