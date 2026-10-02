using Microsoft.UI.Xaml.Controls;
using ProGPU.Fonts.Inter;
using ProGPU.Hmi.Dcs.Sample;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiDcsStartupFontTests
{
    [Fact]
    public void ColdSampleStartupSuppliesBundledFontBeforeAnyNativeWindowLoads()
    {
        TtfFont? previous = PopupService.DefaultFont;
        try
        {
            PopupService.DefaultFont = null;
            using HmiDcsStudio studio = DcsStartup.CreateStudio(automaticTicks: false);
            Assert.Same(InterFontFamily.Regular, PopupService.DefaultFont);
            AssertWorkplaceFonts(studio, InterFontFamily.Regular);
            Assert.False(studio.Operator.Session.Runtime.IsRunning);
            Assert.False(studio.Operator.Session.Runtime.AllowLocalWrites);
            Assert.Null(studio.Engineering);
            Assert.Null(studio.ConnectionFactory);
        }
        finally { PopupService.DefaultFont = previous; }
    }

    [Fact]
    public void SampleStartupPreservesExplicitHostFontInsteadOfReplacingIt()
    {
        TtfFont? previous = PopupService.DefaultFont;
        try
        {
            TtfFont supplied = InterFontFamily.Bold;
            PopupService.DefaultFont = supplied;
            using HmiDcsStudio studio = DcsStartup.CreateStudio(automaticTicks: false);
            Assert.Same(supplied, PopupService.DefaultFont);
            AssertWorkplaceFonts(studio, supplied);
        }
        finally { PopupService.DefaultFont = previous; }
    }

    [Fact]
    public void CapturedFontSurvivesLaterGlobalDefaultChangeAndNavigation()
    {
        TtfFont? previous = PopupService.DefaultFont;
        try
        {
            PopupService.DefaultFont = null;
            using HmiDcsStudio studio = DcsStartup.CreateStudio(automaticTicks: false);
            PopupService.DefaultFont = InterFontFamily.Bold;
            studio.Operator.Session.Navigate("pumps");
            AssertWorkplaceFonts(studio, InterFontFamily.Regular);
            studio.ShowEngineering();
            Assert.NotNull(studio.Engineering);
            Assert.All(Descendants(studio.Engineering!).OfType<TextBlock>().Where(label => label.Text.Length != 0),
                label => Assert.NotNull(label.Font));
            studio.ShowOperator();
            AssertWorkplaceFonts(studio, InterFontFamily.Regular);
        }
        finally { PopupService.DefaultFont = previous; }
    }

    private static void AssertWorkplaceFonts(HmiDcsStudio studio, TtfFont font)
    {
        TextBlock[] labels = Descendants(studio).OfType<TextBlock>().Where(label => label.Text.Length != 0).ToArray();
        Assert.Contains(labels, label => label.Text == "Operator Workplace");
        Assert.Contains(labels, label => label.Text == "PLANT EXPLORER");
        Assert.Contains(labels, label => label.Text == "Simulate");
        Assert.All(labels, label => Assert.Same(font, label.Font));
        Assert.NotNull(studio.Operator.ProcessView);
        Assert.NotEmpty(studio.Operator.ProcessView!.Controls);
        Assert.All(studio.Operator.ProcessView.Controls, control => Assert.Same(font, control.Font));
    }

    private static IEnumerable<Visual> Descendants(Visual root)
    {
        yield return root;
        if (root is ContainerVisual container)
            foreach (Visual child in container.Children)
                foreach (Visual descendant in Descendants(child))
                    yield return descendant;
    }
}
