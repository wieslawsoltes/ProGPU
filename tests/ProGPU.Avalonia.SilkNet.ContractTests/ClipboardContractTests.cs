using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.SilkNet;
using Xunit;

namespace ProGPU.Avalonia.SilkNet.ContractTests;

public sealed class ClipboardContractTests
{
    [Fact]
    public async Task ReassigningOwnedTransferDoesNotDisposeIt()
    {
        var clipboard = new SilkNetClipboard();
        var transfer = OwnedTransfer.CreateText("same");

        await clipboard.SetDataAsync(transfer);
        await clipboard.SetDataAsync(transfer);

        Assert.False(transfer.IsDisposed);
        Assert.Same(
            transfer,
            await clipboard.TryGetInProcessDataAsync());

        await clipboard.ClearAsync();
        Assert.True(transfer.IsDisposed);
    }

    [Fact]
    public async Task ReplacingOwnedTransferDisposesPreviousOwner()
    {
        var clipboard = new SilkNetClipboard();
        var first = OwnedTransfer.CreateText("first");
        var second = OwnedTransfer.CreateText("second");

        await clipboard.SetDataAsync(first);
        await clipboard.SetDataAsync(second);

        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        await clipboard.ClearAsync();
    }

    [Fact]
    public async Task EmptyTextRemainsARepresentableClipboardValue()
    {
        var clipboard = new SilkNetClipboard();
        var transfer = OwnedTransfer.CreateText(string.Empty);
        await clipboard.SetDataAsync(transfer);

        using IAsyncDataTransfer? received =
            await clipboard.TryGetDataAsync();

        Assert.NotNull(received);
        Assert.Equal(
            string.Empty,
            await received.Items[0].TryGetTextAsync());
        await clipboard.ClearAsync();
    }

    [Fact]
    public async Task NonTextReplacementClearsExternalTextProjection()
    {
        var clipboard = new SilkNetClipboard();
        var text = OwnedTransfer.CreateText("old");
        var custom = OwnedTransfer.CreateCustom("new");

        await clipboard.SetDataAsync(text);
        await clipboard.SetDataAsync(custom);

        Assert.True(text.IsDisposed);
        Assert.Null(await clipboard.TryGetDataAsync());
        Assert.Same(
            custom,
            await clipboard.TryGetInProcessDataAsync());
        await clipboard.ClearAsync();
    }

    private sealed class OwnedTransfer : IAsyncDataTransfer
    {
        private readonly DataTransfer _inner;

        private OwnedTransfer(DataTransfer inner)
        {
            _inner = inner;
        }

        public IReadOnlyList<DataFormat> Formats =>
            _inner.Formats;

        public IReadOnlyList<IAsyncDataTransferItem> Items =>
            ((IAsyncDataTransfer)_inner).Items;

        internal bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;

        internal static OwnedTransfer CreateText(string text)
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(text));
            return new OwnedTransfer(transfer);
        }

        internal static OwnedTransfer CreateCustom(string value)
        {
            var transfer = new DataTransfer();
            transfer.Add(
                DataTransferItem.Create(
                    DataFormat.CreateStringApplicationFormat(
                        "progpu.contract"),
                    value));
            return new OwnedTransfer(transfer);
        }
    }
}
