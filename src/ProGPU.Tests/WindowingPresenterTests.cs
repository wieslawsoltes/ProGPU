using Microsoft.UI.Windowing;
using Windows.Foundation.Metadata;
using Xunit;

namespace ProGPU.Tests;

public sealed class WindowingPresenterTests
{
    [Fact]
    public void PresenterFactoriesPublishOfficialKinds()
    {
        Assert.Equal(
            AppWindowPresenterKind.CompactOverlay,
            CompactOverlayPresenter.Create().Kind);
        Assert.Equal(
            AppWindowPresenterKind.FullScreen,
            FullScreenPresenter.Create().Kind);
        Assert.Equal(
            AppWindowPresenterKind.Overlapped,
            OverlappedPresenter.Create().Kind);
    }

    [Fact]
    public void OverlappedPresetFactoriesUseDocumentedConfigurations()
    {
        AssertConfiguration(
            OverlappedPresenter.Create(),
            hasTitleBar: true,
            isMaximizable: true,
            isMinimizable: true,
            isResizable: true);
        AssertConfiguration(
            OverlappedPresenter.CreateForContextMenu(),
            hasTitleBar: false,
            isMaximizable: false,
            isMinimizable: false,
            isResizable: false);
        AssertConfiguration(
            OverlappedPresenter.CreateForDialog(),
            hasTitleBar: true,
            isMaximizable: false,
            isMinimizable: false,
            isResizable: false);
        AssertConfiguration(
            OverlappedPresenter.CreateForToolWindow(),
            hasTitleBar: true,
            isMaximizable: true,
            isMinimizable: true,
            isResizable: true);
    }

    [Fact]
    public void OverlappedPresenterTracksStateAndConfiguration()
    {
        OverlappedPresenter presenter =
            OverlappedPresenter.Create();

        presenter.Minimize(activateWindow: false);
        Assert.Equal(
            OverlappedPresenterState.Minimized,
            presenter.State);
        Assert.Equal(
            OverlappedPresenterState.Restored,
            OverlappedPresenter.RequestedStartupState);
        presenter.ApplyRequestedStartupState();
        Assert.Equal(
            OverlappedPresenterState.Restored,
            presenter.State);

        presenter.Maximize();
        Assert.Equal(
            OverlappedPresenterState.Maximized,
            presenter.State);

        presenter.Restore(activateWindow: false);
        Assert.Equal(
            OverlappedPresenterState.Restored,
            presenter.State);

        presenter.SetBorderAndTitleBar(
            hasBorder: false,
            hasTitleBar: false);
        Assert.False(presenter.HasBorder);
        Assert.False(presenter.HasTitleBar);

        presenter.IsAlwaysOnTop = true;
        presenter.IsModal = true;
        presenter.PreferredMinimumWidth = 320;
        presenter.PreferredMinimumHeight = 200;
        presenter.PreferredMaximumWidth = 1_920;
        presenter.PreferredMaximumHeight = 1_080;

        Assert.True(presenter.IsAlwaysOnTop);
        Assert.True(presenter.IsModal);
        Assert.Equal(320, presenter.PreferredMinimumWidth);
        Assert.Equal(200, presenter.PreferredMinimumHeight);
        Assert.Equal(1_920, presenter.PreferredMaximumWidth);
        Assert.Equal(1_080, presenter.PreferredMaximumHeight);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => presenter.PreferredMinimumWidth = -1);
    }

    [Fact]
    public void CompactOverlayInitialSizeDefaultsToSmall()
    {
        CompactOverlayPresenter presenter =
            CompactOverlayPresenter.Create();

        Assert.Equal(CompactOverlaySize.Small, presenter.InitialSize);
        presenter.InitialSize = CompactOverlaySize.Large;
        Assert.Equal(CompactOverlaySize.Large, presenter.InitialSize);
    }

    [Theory]
    [InlineData(typeof(AppWindowPresenterKind), 0x00010000u)]
    [InlineData(typeof(AppWindowPresenter), 0x00010000u)]
    [InlineData(typeof(CompactOverlayPresenter), 0x00010000u)]
    [InlineData(typeof(CompactOverlaySize), 0x00010000u)]
    [InlineData(typeof(DisplayAreaFallback), 0x00010000u)]
    [InlineData(typeof(DisplayAreaWatcherStatus), 0x00010000u)]
    [InlineData(typeof(FullScreenPresenter), 0x00010000u)]
    [InlineData(typeof(IconShowOptions), 0x00010000u)]
    [InlineData(typeof(OverlappedPresenter), 0x00010000u)]
    [InlineData(typeof(OverlappedPresenterState), 0x00010000u)]
    [InlineData(typeof(TitleBarHeightOption), 0x00010001u)]
    [InlineData(typeof(TitleBarTheme), 0x00010007u)]
    public void PresenterTypesPublishOfficialContractVersions(
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
    public void PresenterPropertyReadsAreAllocationFree()
    {
        OverlappedPresenter presenter =
            OverlappedPresenter.Create();
        _ = presenter.Kind;
        long before = GC.GetAllocatedBytesForCurrentThread();
        int sum = 0;
        for (int index = 0; index < 100_000; index++)
        {
            sum += (int)presenter.Kind;
            sum += (int)presenter.State;
            if (presenter.HasBorder)
                sum++;
        }

        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(600_000, sum);
        Assert.Equal(0, allocated);
    }

    private static void AssertConfiguration(
        OverlappedPresenter presenter,
        bool hasTitleBar,
        bool isMaximizable,
        bool isMinimizable,
        bool isResizable)
    {
        Assert.True(presenter.HasBorder);
        Assert.Equal(hasTitleBar, presenter.HasTitleBar);
        Assert.False(presenter.IsAlwaysOnTop);
        Assert.Equal(isMaximizable, presenter.IsMaximizable);
        Assert.Equal(isMinimizable, presenter.IsMinimizable);
        Assert.False(presenter.IsModal);
        Assert.Equal(isResizable, presenter.IsResizable);
        Assert.Equal(
            OverlappedPresenterState.Restored,
            presenter.State);
    }
}
