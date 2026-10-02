using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

internal static partial class Program
{
    private static void CaptureMidpointCases(string font, FontFamily family, Stopwatch timer, List<object> cases)
    {
        foreach (MidpointInput input in MidpointCases.Create())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException("Original reference exceeded 60 seconds.");
            JsonElement original;
            try
            {
                original = JsonSerializer.SerializeToElement(Capture(font, family, input.Text,
                    Enum.Parse<TextFormattingMode>(input.Mode), Enum.Parse<FlowDirection>(input.Direction),
                    input.Dpi, input.Em, input.Width));
            }
            catch
            {
                Console.Error.WriteLine($"Original midpoint input rejected: {JsonSerializer.Serialize(input)}");
                throw;
            }
            // A label is not evidence of mark positioning or RTL shaping. Fail
            // before receipt publication if this physical font did not provide
            // the requested observable coverage. Never synthesize an offset.
            MidpointCoverage coverage = MidpointCases.Observe(input, original);
            cases.Add(new { Input = input, Original = original, Coverage = coverage });
        }
        if (cases.Count != MidpointCases.Count)
            throw new InvalidOperationException("Incomplete midpoint reference case inventory.");
        if (timer.Elapsed > TimeSpan.FromSeconds(60))
            throw new TimeoutException("Original reference exceeded 60 seconds.");
    }
}
