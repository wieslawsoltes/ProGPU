using System.Drawing;
using System.Drawing.Printing;
using Xunit;

namespace System.Drawing.Tests;

public class PrintDocumentLifecycleTests
{
    [Theory]
    [InlineData(false, false, PrintAction.PrintToPrinter)]
    [InlineData(false, true, PrintAction.PrintToFile)]
    [InlineData(true, false, PrintAction.PrintToPreview)]
    [InlineData(true, true, PrintAction.PrintToPreview)]
    public void PrintActionDescribesTheSelectedController(bool preview, bool toFile, PrintAction expected)
    {
        using Scenario scenario = new(preview);
        scenario.Document.PrinterSettings.PrintToFile = toFile;

        scenario.Document.Print();

        Assert.Equal(expected, scenario.BeginArgs!.PrintAction);
        Assert.Same(scenario.BeginArgs, scenario.StartArgs);
        Assert.Same(scenario.BeginArgs, scenario.EndArgs);
        Assert.Same(scenario.BeginArgs, scenario.FinishArgs);
        Assert.Equal("Begin,Start,Query,StartPage,Page,EndPage,End,Finish", scenario.Order);
        Assert.False(scenario.EndSawCancel);
        Assert.False(scenario.FinishSawCancel);
    }

    [Theory]
    [InlineData("Begin", "Begin,End", true, null)]
    [InlineData("Start", "Begin,Start,End,Finish", true, true)]
    [InlineData("Query", "Begin,Start,Query,End,Finish", false, true)]
    [InlineData("StartPage", "Begin,Start,Query,StartPage,Page,EndPage,End,Finish", false, true)]
    [InlineData("Page", "Begin,Start,Query,StartPage,Page,EndPage,End,Finish", false, true)]
    [InlineData("EndPage", "Begin,Start,Query,StartPage,Page,EndPage,End,Finish", false, true)]
    [InlineData("End", "Begin,Start,Query,StartPage,Page,EndPage,Query,StartPage,Page,EndPage,End,Finish", false, true)]
    public void CancellationPreservesEveryRequiredCallback(string cancelAt, string order, bool endCancel, bool? finishCancel)
    {
        using Scenario scenario = new() { CancelAt = cancelAt, ContinueOnce = true };

        scenario.Document.Print();

        // End-event cancellation is applied only after that event observes its
        // input. It must not be erased by an otherwise successful page result.
        Assert.Equal(order, scenario.Order);
        Assert.Equal(endCancel, scenario.EndSawCancel);
        Assert.Equal(finishCancel, scenario.FinishSawCancel);
    }

    [Fact]
    public void SuccessfulMultiplePagesFinishOnlyOnceAndPermitAnotherPrint()
    {
        using Scenario scenario = new() { ContinueOnce = true };
        scenario.Document.Print();

        Assert.Equal("Begin,Start,Query,StartPage,Page,EndPage,Query,StartPage,Page,EndPage,End,Finish", scenario.Order);
        Assert.Equal(2, scenario.PageCount);
        Assert.False(scenario.FinishSawCancel);

        scenario.Trace.Clear();
        scenario.PageCount = 0;
        scenario.ContinueOnce = false;
        scenario.Document.Print();
        Assert.Equal("Begin,Start,Query,StartPage,Page,EndPage,End,Finish", scenario.Order);
        Assert.Equal(1, scenario.PageCount);
        Assert.False(scenario.FinishSawCancel);
    }

    [Theory]
    [InlineData("Begin", "Begin", null)]
    [InlineData("Start", "Begin,Start", null)]
    [InlineData("Query", "Begin,Start,Query,End,Finish", true)]
    [InlineData("StartPage", "Begin,Start,Query,StartPage,End,Finish", true)]
    [InlineData("Page", "Begin,Start,Query,StartPage,Page,End,Finish", true)]
    [InlineData("EndPage", "Begin,Start,Query,StartPage,Page,EndPage,End,Finish", true)]
    [InlineData("End", "Begin,Start,Query,StartPage,Page,EndPage,End,Finish", false)]
    [InlineData("Finish", "Begin,Start,Query,StartPage,Page,EndPage,End,Finish", false)]
    public void ExceptionsPreserveCompletionBoundaryAndOriginalFailure(string throwAt, string order, bool? finishCancel)
    {
        using Scenario scenario = new() { ThrowAt = throwAt };

        Assert.Same(scenario.Failure, Assert.Throws<InvalidOperationException>(scenario.Document.Print));

        Assert.Equal(order, scenario.Order);
        Assert.Equal(finishCancel, scenario.FinishSawCancel);
    }

    [Fact]
    public void QueriedPageSettingsPersistAcrossPagesWithoutMutatingDocumentDefaults()
    {
        using Scenario scenario = new() { ContinueOnce = true };
        PageSettings defaults = scenario.Document.DefaultPageSettings;
        QueryPageSettingsEventArgs? firstQuery = null;
        PageSettings? queriedSettings = null;
        int queries = 0;
        List<Rectangle> printedMargins = [];
        scenario.Document.QueryPageSettings += (_, e) =>
        {
            if (++queries == 1)
            {
                firstQuery = e;
                queriedSettings = e.PageSettings;
                Assert.NotSame(defaults, queriedSettings);
                Assert.NotSame(defaults.Margins, queriedSettings.Margins);
                queriedSettings.Margins.Left = 2;
                queriedSettings.Color = true;
            }
            else
            {
                Assert.Equal(2, queries);
                Assert.Same(firstQuery, e);
                Assert.Same(queriedSettings, e.PageSettings);
                Assert.Equal(2, e.PageSettings.Margins.Left);
                Assert.True(e.PageSettings.Color);
            }
        };
        scenario.Document.PrintPage += (_, e) =>
        {
            Assert.Same(queriedSettings, e.PageSettings);
            printedMargins.Add(e.MarginBounds);
        };

        scenario.Document.Print();

        Assert.Equal(2, queries);
        Assert.Equal([new Rectangle(2, 0, 6, 8), new Rectangle(2, 0, 6, 8)], printedMargins);
        Assert.Same(defaults, scenario.Document.DefaultPageSettings);
        Assert.Equal(0, defaults.Margins.Left);
        Assert.False(defaults.Color);
        Assert.Equal("Begin,Start,Query,StartPage,Page,EndPage,Query,StartPage,Page,EndPage,End,Finish", scenario.Order);
    }

    [Theory]
    [InlineData("Begin", "Begin,End")]
    [InlineData("Start", "Begin,Start,End")]
    public void EarlyCancellationEndHandlerFailureDoesNotCompleteTheController(string cancelAt, string order)
    {
        using Scenario scenario = new() { CancelAt = cancelAt, ThrowAt = "End" };

        Assert.Same(scenario.Failure, Assert.Throws<InvalidOperationException>(scenario.Document.Print));

        Assert.Equal(order, scenario.Order);
        Assert.Null(scenario.FinishArgs);
    }

    [Fact]
    public void EndHandlerFailureAfterPageCancellationStillCompletesWithoutRewritingItsInput()
    {
        using Scenario scenario = new() { CancelAt = "Query", ThrowAt = "End" };

        Assert.Same(scenario.Failure, Assert.Throws<InvalidOperationException>(scenario.Document.Print));

        Assert.Equal("Begin,Start,Query,End,Finish", scenario.Order);
        Assert.False(scenario.FinishSawCancel);
    }

    [Fact]
    public void ControllerCompletionFailureSupersedesEndHandlerFailure()
    {
        using Scenario scenario = new() { ThrowAt = "End" };
        InvalidOperationException completionFailure = new("controller completion failed");
        scenario.Controller.Completing = () => throw completionFailure;

        Assert.Same(completionFailure, Assert.Throws<InvalidOperationException>(scenario.Document.Print));
        Assert.Equal("Begin,Start,Query,StartPage,Page,EndPage,End,Finish", scenario.Order);
    }

    [Fact]
    public void EndHandlerFailureSupersedesPageFailureAndStillCompletesController()
    {
        using Scenario scenario = new() { ThrowAt = "Page" };
        InvalidOperationException endFailure = new("application completion failed");
        scenario.Document.EndPrint += (_, _) => throw endFailure;

        Assert.Same(endFailure, Assert.Throws<InvalidOperationException>(scenario.Document.Print));
        Assert.Equal("Begin,Start,Query,StartPage,Page,End,Finish", scenario.Order);
        Assert.False(scenario.FinishSawCancel);
    }

    [Fact]
    public void ControllerSelectionRemainsStableAcrossApplicationCallbacks()
    {
        using Scenario scenario = new(preview: true);
        using Scenario replacement = new();
        scenario.Document.BeginPrint += (_, _) => scenario.Document.PrintController = replacement.Controller;

        scenario.Document.Print();

        Assert.Equal(PrintAction.PrintToPreview, scenario.BeginArgs!.PrintAction);
        Assert.Equal("Begin,Start,Query,StartPage,Page,EndPage,End,Finish", scenario.Order);
        Assert.Empty(replacement.Trace);
        Assert.Same(replacement.Controller, scenario.Document.PrintController);
    }

    [Fact]
    public void DefaultControllerStillRejectsNativePrintingWithoutInventingCompletion()
    {
        using PrintDocument document = new();
        List<string> order = [];
        document.BeginPrint += (_, _) => order.Add("Begin");
        document.EndPrint += (_, _) => order.Add("End");
        document.PrintPage += (_, _) => order.Add("Page");

        Assert.Throws<PlatformNotSupportedException>(document.Print);

        Assert.Equal(["Begin"], order);
        Assert.IsType<StandardPrintController>(document.PrintController);
    }

    private sealed class Scenario : IDisposable
    {
        public Scenario(bool preview = false)
        {
            Document.DefaultPageSettings.PaperSize = new PaperSize("Lifecycle", 8, 8);
            Document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            Controller = new RecordingController(this, preview);
            Document.PrintController = Controller;
            Document.BeginPrint += (_, e) =>
            {
                BeginArgs = e;
                Record("Begin");
                if (CancelAt == "Begin") e.Cancel = true;
            };
            Document.QueryPageSettings += (_, e) =>
            {
                Record("Query");
                if (CancelAt == "Query") e.Cancel = true;
            };
            Document.PrintPage += (_, e) =>
            {
                Assert.InRange(++PageCount, 1, 2);
                Record("Page");
                if (CancelAt == "Page") e.Cancel = true;
                e.HasMorePages = ContinueOnce && PageCount == 1;
            };
            Document.EndPrint += (_, e) =>
            {
                EndArgs = e;
                EndSawCancel = e.Cancel;
                Record("End");
                if (CancelAt == "End") e.Cancel = true;
            };
        }

        public PrintDocument Document { get; } = new();
        public RecordingController Controller { get; }
        public List<string> Trace { get; } = [];
        public string Order => string.Join(",", Trace);
        public string? CancelAt { get; init; }
        public string? ThrowAt { get; init; }
        public bool ContinueOnce { get; set; }
        public int PageCount { get; set; }
        public InvalidOperationException Failure { get; } = new("printing callback failed");
        public PrintEventArgs? BeginArgs { get; private set; }
        public PrintEventArgs? StartArgs { get; set; }
        public PrintEventArgs? EndArgs { get; private set; }
        public PrintEventArgs? FinishArgs { get; set; }
        public bool? EndSawCancel { get; private set; }
        public bool? FinishSawCancel { get; set; }

        public void Record(string operation)
        {
            Trace.Add(operation);
            if (ThrowAt == operation) throw Failure;
        }

        public void Dispose() => Document.Dispose();
    }

    private sealed class RecordingController(Scenario scenario, bool preview) : PrintController
    {
        public override bool IsPreview => preview;
        public Action? Completing { get; set; }

        public override void OnStartPrint(PrintDocument document, PrintEventArgs e)
        {
            Assert.Same(scenario.Document, document);
            scenario.StartArgs = e;
            scenario.Record("Start");
            if (scenario.CancelAt == "Start") e.Cancel = true;
        }

        public override Graphics? OnStartPage(PrintDocument document, PrintPageEventArgs e)
        {
            Assert.Same(scenario.Document, document);
            scenario.Record("StartPage");
            if (scenario.CancelAt == "StartPage") e.Cancel = true;
            return e.Graphics;
        }

        public override void OnEndPage(PrintDocument document, PrintPageEventArgs e)
        {
            Assert.Same(scenario.Document, document);
            scenario.Record("EndPage");
            if (scenario.CancelAt == "EndPage") e.Cancel = true;
        }

        public override void OnEndPrint(PrintDocument document, PrintEventArgs e)
        {
            Assert.Same(scenario.Document, document);
            scenario.FinishArgs = e;
            scenario.FinishSawCancel = e.Cancel;
            scenario.Record("Finish");
            Completing?.Invoke();
        }
    }
}
