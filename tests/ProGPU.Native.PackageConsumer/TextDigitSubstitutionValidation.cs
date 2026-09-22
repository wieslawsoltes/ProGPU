using System.Runtime.InteropServices;
using ProGPU.Backend.Native;

internal static class TextDigitSubstitutionValidation
{
    internal static void Run(string fontPath)
    {
        using var context = new NativeTextShapingContext(File.ReadAllBytes(fontPath));
        int cases = 0;
        foreach (var (source, rendered, contextual, direction) in new[]
        {
            ("123", "\u0661\u0662\u0663", false, NativeTextDirection.LeftToRight),
            ("123", "\u0661\u0662\u0663", false, NativeTextDirection.RightToLeft),
            ("A123 \u0627 456", "A123 \u0627 \u0664\u0665\u0666", true, NativeTextDirection.LeftToRight),
            ("A\n123", "A\n\u0661\u0662\u0663", true, NativeTextDirection.RightToLeft),
            ("123 456 789", "\u0661\u0662\u0663 \u0664\u0665\u0666 \u0667\u0668\u0669", false, NativeTextDirection.LeftToRight),
        })
        {
            var options = new NativeTextParagraphOptions(16f / 2048, 45, 20);
            NativeTextParagraphStyle[] actualStyles = [new(0, source.Length, 0, options.Scale,
                DigitZero: 0x0660, ContextualDigits: contextual)];
            NativeTextParagraphStyle[] expectedStyles = [new(0, rendered.Length, 0, options.Scale)];
            var actual = NativeTextParagraphSnapshot.Create(context, source, direction, options, styles: actualStyles);
            var expected = NativeTextParagraphSnapshot.Create(context, rendered, direction, options, styles: expectedStyles);
            Equal(expected, actual);
            if (source == "123" && direction == NativeTextDirection.LeftToRight &&
                actual.BidiLevels.Span.ContainsAnyExcept((sbyte)2))
                throw new InvalidOperationException("Substituted Arabic digits lost their resolved level 2.");
            if (actual.Lines.Length > 1)
            {
                int start = actual.Lines.Span[1].InputStart;
                Equal(NativeTextParagraphSnapshot.CreateContinued(context, rendered, direction, options,
                    start, styles: expectedStyles), NativeTextParagraphSnapshot.CreateContinued(context,
                    source, direction, options, start, styles: actualStyles));
                cases++;
            }
            cases++;
        }

        const string inline = "12\ufffc3";
        NativeTextParagraphStyle[] inlineStyles = [new(0, inline.Length, 0, 16f / 2048, DigitZero: 0x0660)];
        NativeTextParagraphStyle[] renderedStyles = [new(0, inline.Length, 0, 16f / 2048)];
        NativeTextStyleMetrics[] metrics = [new() { Ascent = 12, Descent = 4 }];
        NativeTextParagraphInlineObject[] objects = [new(2, 20, 12, 4)];
        var inlineOptions = new NativeTextParagraphOptions(16f / 2048, 80, 20);
        Equal(NativeTextParagraphSnapshot.CreateWithInlineObjects(context, "\u0661\u0662\ufffc\u0663",
            NativeTextDirection.LeftToRight, inlineOptions, renderedStyles, metrics, objects),
            NativeTextParagraphSnapshot.CreateWithInlineObjects(context, inline,
            NativeTextDirection.LeftToRight, inlineOptions, inlineStyles, metrics, objects));
        Console.WriteLine($"Native digit text metadata passed: {cases + 1} ordinary/continued/inline layouts.");
    }

    private static void Equal(NativeTextParagraphSnapshot expected, NativeTextParagraphSnapshot actual)
    {
        if (!Same(expected.Glyphs, actual.Glyphs) || !Same(expected.Lines, actual.Lines) ||
            !Same(expected.ClusterEnds, actual.ClusterEnds) || !Same(expected.BidiLevels, actual.BidiLevels) ||
            !Same(expected.Boxes, actual.Boxes) || !Same(expected.Carets, actual.Carets) ||
            !Same(expected.InlineObjects, actual.InlineObjects))
            throw new InvalidOperationException("Styled digit glyph/source/bidi/interaction metadata differs from rendered scalar input.");
    }

    private static bool Same<T>(ReadOnlyMemory<T> expected, ReadOnlyMemory<T> actual) where T : unmanaged
        => MemoryMarshal.AsBytes(expected.Span).SequenceEqual(MemoryMarshal.AsBytes(actual.Span));
}
