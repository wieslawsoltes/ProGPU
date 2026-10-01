namespace HintedTextureSamplingProbe;

// Executes against the real pinned shader components, without creating a device
// or invoking native code. Run with --verify-source-only.
internal static class ShaderSourceControls
{
    internal static int Run()
    {
        int passed = 0;
        foreach (bool paint in new[] { false, true })
        {
            _ = ShaderDiagnostics.VerifySource(paint);
            string[] lfComponents = ShaderDiagnostics.ReadComponents(paint)
                .Select(value => value.Replace("\r\n", "\n", StringComparison.Ordinal)).ToArray();
            string lf = string.Join("\n", lfComponents);
            foreach (bool windows in new[] { false, true })
            {
                string[] components = windows
                    ? lfComponents.Select(value => value.Replace("\n", "\r\n", StringComparison.Ordinal)).ToArray()
                    : lfComponents;
                // The production composer always inserts LF BETWEEN components,
                // even when each checked-out resource internally contains CRLF.
                string original = string.Join("\n", components);
                string exact = ShaderDiagnostics.VerifySource(original, components, paint);
                Check(ReferenceEquals(original, exact), "Verification rewrote the compiled module.");
                Check(ShaderDiagnostics.CanonicalHash(exact) == ShaderDiagnostics.Hash(lf), "Canonical content hash differs.");
                Check((ShaderDiagnostics.Hash(exact) != ShaderDiagnostics.Hash(lf)) == windows,
                    "Exact compiled-source identity did not preserve checkout line endings.");

                foreach (bool sample in new[] { false, true })
                {
                    string instrumented = ShaderDiagnostics.Instrument(exact, paint, sample);
                    string expected = ShaderDiagnostics.Instrument(lf, paint, sample);
                    Check(instrumented.Replace("\r\n", "\n", StringComparison.Ordinal) == expected,
                        "LF/CRLF instrumentation changed shader semantics.");
                    int firstAnchor = exact.IndexOf("fn text_coverage_to_alpha(", StringComparison.Ordinal);
                    Check(instrumented.StartsWith(exact[..firstAnchor], StringComparison.Ordinal),
                        "Instrumentation rewrote untouched vertex or mask source bytes.");
                    int lastFragment = exact.LastIndexOf("@fragment", StringComparison.Ordinal);
                    Check(instrumented.EndsWith(exact[lastFragment..], StringComparison.Ordinal),
                        "Instrumentation rewrote untouched fragment entrypoint bytes.");
                }

                string[] changed = components.Select(value => value.Replace("1.43", "1.44", StringComparison.Ordinal)).ToArray();
                Reject(() => ShaderDiagnostics.VerifySource(string.Join("\n", changed), changed, paint),
                    "Changed production scalar passed the pinned parent check.");
                string[] changedCopy = components.ToArray();
                changedCopy[0] += " ";
                Reject(() => ShaderDiagnostics.VerifySource(original, changedCopy, paint),
                    "Mismatched embedded/copied component bytes were accepted.");
            }
        }
        return passed;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
        }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException) { passed++; return; }
            throw new InvalidOperationException(message);
        }
    }
}
