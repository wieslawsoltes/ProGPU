using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableWpfServiceRegistryTests
{
    private static readonly PortableWpfServiceKey ServiceKey =
        new($"PopupTests-{Guid.NewGuid():N}");

    [Fact]
    public void PopupRouterKeepsMultipleWindowServicesAndRoutesByOwner()
    {
        object firstOwner = new();
        object secondOwner = new();
        var first = new TestPopupService(ServiceKey, firstOwner);
        var second = new TestPopupService(ServiceKey, secondOwner);

        using IDisposable firstRegistration = PortableWpfServiceRegistry.RegisterPopupService(first);
        Assert.True(PortableWpfServiceRegistry.TryGetPopupService(ServiceKey, out var cachedRouter));
        using IDisposable secondRegistration = PortableWpfServiceRegistry.RegisterPopupService(second);
        Assert.True(PortableWpfServiceRegistry.TryGetPopupService(ServiceKey, out var currentRouter));
        Assert.Same(cachedRouter, currentRouter);

        PortablePopupCreateRequest firstRequest = CreateRequest(firstOwner);
        Assert.True(currentRouter.TryCreatePopup(firstRequest, out object? firstPopup));
        Assert.NotNull(firstPopup);
        Assert.Equal(1, second.CreateAttempts);
        Assert.Equal(1, first.CreateAttempts);

        PortablePopupCreateRequest secondRequest = CreateRequest(secondOwner);
        Assert.True(currentRouter.TryCreatePopup(secondRequest, out object? secondPopup));
        Assert.NotNull(secondPopup);
        Assert.Equal(2, second.CreateAttempts);
        Assert.Equal(1, first.CreateAttempts);

        Assert.True(currentRouter.TrySetPopupPosition(firstPopup!, 12, 34));
        Assert.True(currentRouter.TrySetPopupSize(secondPopup!, 320, 180));
        Assert.True(currentRouter.TryShowPopup(firstPopup));
        Assert.True(currentRouter.TryHidePopup(secondPopup));
        Assert.True(currentRouter.TrySetPopupHitTestable(firstPopup, false));
        Assert.True(currentRouter.TryDestroyPopup(secondPopup));

        Assert.Equal(3, first.OperationCount);
        Assert.Equal(3, second.OperationCount);
    }

    [Fact]
    public void CachedPopupRouterFallsBackAfterNewerWindowServiceIsDisposed()
    {
        object firstOwner = new();
        object secondOwner = new();
        var first = new TestPopupService(ServiceKey, firstOwner);
        var second = new TestPopupService(ServiceKey, secondOwner);

        using IDisposable firstRegistration = PortableWpfServiceRegistry.RegisterPopupService(first);
        using IDisposable secondRegistration = PortableWpfServiceRegistry.RegisterPopupService(second);
        Assert.True(PortableWpfServiceRegistry.TryGetPopupService(ServiceKey, out var cachedRouter));

        secondRegistration.Dispose();

        Assert.True(PortableWpfServiceRegistry.TryGetPopupService(ServiceKey, out var currentRouter));
        Assert.Same(cachedRouter, currentRouter);
        Assert.True(cachedRouter.TryCreatePopup(CreateRequest(firstOwner), out object? popup));
        Assert.NotNull(popup);
        Assert.Equal(0, second.CreateAttempts);
        Assert.Equal(1, first.CreateAttempts);
    }

    [Fact]
    public void PopupRouterSurvivesAWindowGapForPreviouslyCachedConsumers()
    {
        object firstOwner = new();
        var first = new TestPopupService(ServiceKey, firstOwner);
        using IDisposable firstRegistration = PortableWpfServiceRegistry.RegisterPopupService(first);
        Assert.True(PortableWpfServiceRegistry.TryGetPopupService(ServiceKey, out var cachedRouter));

        firstRegistration.Dispose();
        Assert.False(PortableWpfServiceRegistry.TryGetPopupService(ServiceKey, out _));
        Assert.False(cachedRouter.TryCreatePopup(CreateRequest(firstOwner), out _));

        object secondOwner = new();
        var second = new TestPopupService(ServiceKey, secondOwner);
        using IDisposable secondRegistration = PortableWpfServiceRegistry.RegisterPopupService(second);

        Assert.True(PortableWpfServiceRegistry.TryGetPopupService(ServiceKey, out var currentRouter));
        Assert.Same(cachedRouter, currentRouter);
        Assert.True(cachedRouter.TryCreatePopup(CreateRequest(secondOwner), out object? popup));
        Assert.NotNull(popup);
        Assert.Equal(1, second.CreateAttempts);
    }

    private static PortablePopupCreateRequest CreateRequest(object owner)
    {
        return new PortablePopupCreateRequest(
            placementTarget: null,
            ownerPresentationSource: owner,
            ownerHandle: IntPtr.Zero,
            x: 0,
            y: 0,
            isTransparent: true,
            isChildPopup: false);
    }

    private sealed class TestPopupService(
        PortableWpfServiceKey serviceKey,
        object owner) : IPortablePopupServiceRegistrar
    {
        private readonly HashSet<object> _popups = new(ReferenceEqualityComparer.Instance);

        public PortableWpfServiceKey ServiceKey { get; } = serviceKey;

        public int CreateAttempts { get; private set; }

        public int OperationCount { get; private set; }

        public bool TryCreatePopup(PortablePopupCreateRequest request, out object? presentationSource)
        {
            CreateAttempts++;
            if (!ReferenceEquals(request.OwnerPresentationSource, owner))
            {
                presentationSource = null;
                return false;
            }

            presentationSource = new object();
            _popups.Add(presentationSource);
            return true;
        }

        public bool TrySetPopupPosition(object presentationSource, int x, int y) =>
            RecordOperation(presentationSource);

        public bool TrySetPopupSize(object presentationSource, int width, int height) =>
            RecordOperation(presentationSource);

        public bool TryShowPopup(object presentationSource) => RecordOperation(presentationSource);

        public bool TryHidePopup(object presentationSource) => RecordOperation(presentationSource);

        public bool TrySetPopupHitTestable(object presentationSource, bool hitTestable) =>
            RecordOperation(presentationSource);

        public bool TryDestroyPopup(object presentationSource)
        {
            OperationCount++;
            return _popups.Remove(presentationSource);
        }

        public void Clear()
        {
            _popups.Clear();
        }

        private bool RecordOperation(object presentationSource)
        {
            OperationCount++;
            return _popups.Contains(presentationSource);
        }
    }
}
