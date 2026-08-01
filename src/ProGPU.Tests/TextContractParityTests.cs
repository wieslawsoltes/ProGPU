using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using ProGPU.WinUI.Text;
using System.Reflection;
using Windows.Foundation.Metadata;
using Xunit;

namespace ProGPU.Tests;

public sealed class TextContractParityTests
{
    [Fact]
    public void TextEnumsPublishOfficialStorageAndValues()
    {
        Assert.Equal(typeof(uint), Enum.GetUnderlyingType(typeof(FindOptions)));
        Assert.Equal(typeof(uint), Enum.GetUnderlyingType(typeof(TextGetOptions)));
        Assert.Equal(typeof(uint), Enum.GetUnderlyingType(typeof(TextSetOptions)));
        Assert.Equal(0U, (uint)FindOptions.None);
        Assert.Equal(4U, (uint)FindOptions.Case);
        Assert.Equal(0x01000000U, (uint)TextGetOptions.UseLf);
        Assert.Equal(0x00002000U, (uint)TextGetOptions.FormatRtf);
        Assert.Equal(0x00004000U, (uint)TextSetOptions.ApplyRtfDocumentDefaults);
        Assert.Equal(3, (int)FormatEffect.Undefined);
        Assert.DoesNotContain(
            nameof(FindOptions.None),
            Enum.GetNames<SelectionOptions>());
    }

    [Fact]
    public void TextRuntimeClassesPublishOfficialContractShape()
    {
        Assert.True(typeof(FontWeights).IsSealed);
        Assert.False(typeof(FontWeights).IsAbstract);
        Assert.True(typeof(RichEditTextRange).IsSealed);
        Assert.Null(
            typeof(RichEditTextDocument).GetMethod(
                "GetRange2",
                BindingFlags.Public |
                BindingFlags.Instance));
        Assert.Null(
            typeof(RichEditTextDocument).GetEvent(
                "ContentsChanged",
                BindingFlags.Public |
                BindingFlags.Instance));
        Assert.False(
            GetImplementationType(
                "Microsoft.UI.Text.RichEditTextCharacterFormat")
            .IsPublic);
        Assert.False(
            GetImplementationType(
                "Microsoft.UI.Text.RichEditTextParagraphFormat")
            .IsPublic);
        Assert.False(
            GetImplementationType(
                "Microsoft.UI.Text.RichEditTextSelection")
            .IsPublic);
        Assert.NotNull(
            typeof(RichEditTextRangeExtensions).GetMethod(
                nameof(RichEditTextRangeExtensions.InsertTable),
                BindingFlags.Public |
                BindingFlags.Static));
        Assert.Equal(
            "value",
            typeof(ITextRange)
                .GetMethod(nameof(ITextRange.Collapse))!
                .GetParameters()[0]
                .Name);
        AssertContractVersion(typeof(FontWeights), 0x00010000U);
        AssertContractVersion(typeof(ITextCharacterFormat), 0x00010000U);
        AssertContractVersion(typeof(ITextParagraphFormat), 0x00010000U);
        AssertContractVersion(typeof(ITextRange), 0x00010000U);
        AssertContractVersion(typeof(ITextSelection), 0x00010000U);
        AssertContractVersion(typeof(RichEditTextDocument), 0x00010000U);
        AssertContractVersion(typeof(RichEditTextRange), 0x00010000U);
        AssertContractVersion(typeof(TextConstants), 0x00010000U);

        CustomAttributeData contractAttribute = Assert.Single(
            typeof(TextApiContract).GetCustomAttributesData(),
            static attribute =>
                attribute.AttributeType ==
                typeof(ContractVersionAttribute));
        Assert.Equal(
            0x00020000U,
            Assert.IsType<uint>(
                contractAttribute.ConstructorArguments[0].Value));
    }

    [Fact]
    public void FontWeightReadsRemainAllocationFree()
    {
        const int Count = 100_000;
        _ = FontWeights.Bold.Weight;
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        ushort checksum = 0;
        for (int index = 0; index < Count; index++)
        {
            checksum ^= FontWeights.Thin.Weight;
            checksum ^= FontWeights.Normal.Weight;
            checksum ^= FontWeights.ExtraBlack.Weight;
        }

        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SelectionAdapterPropertyReadsRemainAllocationFree()
    {
        const int Count = 100_000;
        var editor = new RichEditBox
        {
            Text = "retained selection"
        };
        editor.SelectionStart = 2;
        editor.SelectionLength = 8;
        ITextSelection selection =
            editor.TextDocument.Selection;
        _ = selection.Options;
        _ = selection.Type;
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int index = 0; index < Count; index++)
        {
            checksum ^= selection.StartPosition;
            checksum ^= selection.EndPosition;
            checksum ^= selection.Length;
            checksum ^= (int)selection.Options;
            checksum ^= (int)selection.Type;
        }

        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }

    private static void AssertContractVersion(
        Type type,
        uint expectedVersion)
    {
        CustomAttributeData attribute = Assert.Single(
            type.GetCustomAttributesData(),
            static candidate =>
                candidate.AttributeType ==
                typeof(ContractVersionAttribute));
        Assert.Equal(
            "Microsoft.UI.Text.TextApiContract",
            Assert.IsType<string>(
                attribute.ConstructorArguments[0].Value));
        Assert.Equal(
            expectedVersion,
            Assert.IsType<uint>(
                attribute.ConstructorArguments[1].Value));
    }

    private static Type GetImplementationType(
        string typeName) =>
        typeof(RichEditTextDocument).Assembly.GetType(
            typeName,
            throwOnError: true)!;
}
