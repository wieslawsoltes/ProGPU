using System.Runtime.InteropServices;
using ProGPU.Backend.Native;

internal static unsafe class TextEditWordBoundaryValidation
{
    private const uint Sentinel = 0xBAD0C0DE;

    internal static void Run(bool dawn)
    {
        string stem = dawn ? "progpu_native_dawn" : "progpu_native";
        string file = OperatingSystem.IsWindows() ? stem + ".dll" :
            OperatingSystem.IsMacOS() ? "lib" + stem + ".dylib" : "lib" + stem + ".so";
        string rid = (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") +
            "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string path = Path.Combine(AppContext.BaseDirectory, file);
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", file);
        Check(File.Exists(path), "selected packaged provider image is missing: " + path);
        nint module = NativeLibrary.Load(path);
        try
        {
            var resolve = (delegate* unmanaged[Cdecl]<ushort*, uint, int, uint*, uint, NativeEditWordBoundaryResult>)
                NativeLibrary.GetExport(module, "progpu_native_text_resolve_edit_word_boundaries_utf16");
            var abi = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(module, "progpu_native_get_abi_version");
            Check(abi() == 5 && sizeof(NativeEditWordBoundaryResult) == 16, "packaged ABI identity");
            if (dawn)
            {
                var adapterAbi = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(module, "progpu_native_dawn_get_adapter_abi_version");
                Check(adapterAbi() == NativeDawnAdapter.AdapterAbiVersion, "Dawn adapter identity");
            }
            ReferenceInventories(resolve, managed: !dawn);
            FailureControls(resolve);
            Console.WriteLine($"package-consumer: EDIT original UTF-16 inventories, owned Thai dictionary, direction and atomic buffers passed; provider={path}; no GPU or source UI admission");
        }
        finally { NativeLibrary.Free(module); }
    }

    private static void ReferenceInventories(
        delegate* unmanaged[Cdecl]<ushort*, uint, int, uint*, uint, NativeEditWordBoundaryResult> resolve, bool managed)
    {
        // Independent literal original24 Microsoft EDIT inventories retained from
        // progpu_native_edit_word_boundary_tests.cpp at 8b05be723. These are not
        // generated from the classifier, ICU, shaping or modern grapheme rules.
        // Receipt: LibreWinForms Build 36802156343, SHA256
        // 647da7cdd14bfad8c5b4567b553bcbfa5ceacfde3c3823524abc0271ad430a0b.
        Verify("spaces", "  alpha  beta  ", [0, 2, 9, 15], leading: 2);
        Verify("punctuation", "alpha,beta.gamma! (tail) - end ", [0, 18, 25, 27, 31]);
        Verify("tabs-single", "one\t\ttwo \tthree ", [0, 5, 9, 10, 16]);
        Verify("tabs-multiline", "one\t\ttwo \tthree ", [0, 5, 9, 10, 16]);
        Verify("crlf-single", "alpha\r\nbeta\r\n\r\ngamma ", [0, 5, 7, 11, 13, 15, 21]);
        Verify("crlf-multiline", "alpha\r\nbeta\r\n\r\ngamma ", [0, 5, 7, 11, 13, 15, 21]);
        Verify("surrogate-combining", "go A\U0001F600 e\u0301 fin ", [0, 3, 4, 7, 10, 14]);
        Verify("mixed-ltr", "abc \u05D0\u05D1\u05D2, \u0639\u0631\u0628\u0649 end ", [0, 4, 9, 14, 18]);
        Verify("mixed-rtl", "abc \u05D0\u05D1\u05D2, \u0639\u0631\u0628\u0649 end ", [0, 4, 9, 14, 18], level: 1);
        // Oracle text only. Password source must bypass the boundary provider.
        Verify("password-oracle-only", "alpha, beta\tend ", [0, 7, 12, 16]);
        Verify("leading-tabs", "  alpha,beta \ttail ", [0, 2, 13, 14, 19], leading: 2);
        Verify("words", "alpha beta gamma delta end ", [0, 6, 11, 17, 23, 27]);
        Verify("space-classes", "a\u00A0b\u2003c\u202Fd\u3000e ", [0, 4, 6, 8, 10]);
        Verify("symbols", "a\u00A9b\u2603c\U0001F600d ", [0, 5, 7, 9]);
        Verify("emoji-interior", "a\u2603\uFE0Fb\U0001F469\u200D\U0001F4BBc ", [0, 4, 7, 9, 11]);
        Verify("hard-single", "ab\rcd\nef ", [0, 2, 3, 6, 9]);
        Verify("hard-multiline", "ab\rcd\nef ", [0, 2, 3, 6, 9]);
        // Interior CRCRLF coordinates were unavailable in the receipt. This
        // atomic group is the documented EditWordBreakProc contract.
        Verify("crcrlf-contract", "ab\r\r\ncd ", [0, 2, 5, 8]);
        Verify("long-word", "abcdefghijklmnopqrstuvwxyz0123456789 ", [0, 37]);
        Verify("supplementary", "a\U00010400b\U0001D11Ec\U00020000d ", [0, 7, 9, 11]);
        Verify("thai-adjacent", "\u0E20\u0E32\u0E29\u0E32\u0E44\u0E17\u0E22\u0E20\u0E32\u0E29\u0E32\u0E44\u0E17\u0E22 ", [0, 4, 7, 11, 15]);
        Verify("thai-spaced", "\u0E20\u0E32\u0E29\u0E32\u0E44\u0E17\u0E22 \u0E20\u0E32\u0E29\u0E32\u0E44\u0E17\u0E22 ", [0, 4, 8, 12, 16]);
        Verify("lao", "\u0EAA\u0EB0\u0E9A\u0EB2\u0E8D\u0E94\u0EB5\u0EAA\u0EB0\u0E9A\u0EB2\u0E8D\u0E94\u0EB5 ", [0, 15]);
        Verify("khmer", "\u1797\u17B6\u179F\u17B6\u1781\u17D2\u1798\u17C2\u179A\u1797\u17B6\u179F\u17B6\u1781\u17D2\u1798\u17C2\u179A ", [0, 18, 19]);

        Verify("empty", "", [0]);
        // Original full symbol sweep: keep the former rejection requests as
        // positive inventories, including independent VS16 and CJK contexts.
        Verify("hangul-symbol-latin", "a\u3200b ", [0, 4]);
        Verify("hangul-symbol-variation", "a\u3200\uFE0Fb ", [0, 5]);
        Verify("hangul-symbol-cjk", "\u4E00\u3200\u4E8C ", [0, 4]);
        Verify("balinese-symbol-latin", "a\u1B61b ", [0, 1, 4]);
        Verify("balinese-symbol-variation", "a\u1B61\uFE0Fb ", [0, 1, 5]);
        Verify("balinese-symbol-cjk", "\u4E00\u1B61\u4E8C ", [0, 1, 4]);
        // Independent contextual receipt controls retained by the original CPU
        // harness; both original source directions must reach the export.
        foreach (int level in new[] { 0, 1 })
        {
            Verify("arabic-entry", "x\u0628\u062Ay ", [0, 1, 5], level: level);
            Verify("thai-cjk-entry", "\u4E00\u0E01\u0E02y ", [0, 1, 5], level: level);
            Verify("thai-space-entry", "x \u0E01\u0E02y ", [0, 2, 6], level: level);
            Verify("thai-numeric-singleton", "\u0E50\u0E51\u0E01y ", [0, 5], level: level);
            Verify("thai-numeric-singleton-latin", "x\u0E50\u0E51\u0E01y ", [0, 6], level: level);
            Verify("lao-numeric-singleton", "\u0ED0\u0ED1\u0E81y ", [0, 5], level: level);
            Verify("lao-numeric-singleton-latin", "x\u0ED0\u0ED1\u0E81y ", [0, 6], level: level);
            Verify("khmer-numeric-singleton", "\u17E0\u17E1\u1780y ", [0, 5], level: level);
            Verify("khmer-numeric-singleton-latin", "x\u17E0\u17E1\u1780y ", [0, 6], level: level);

            // Separate original source-role observations, Windows run36900452884
            // at2dafdc178; source-roles.json SHA256
            // 858e7c8e34a694a1ff4e153b7a4c6946f451daaf6dc371cc0296519a468048b8.
            Verify("hebrew-presentation-bare", "\uFB1D\u05D1y ", [0, 4], level: level);
            Verify("hebrew-presentation-latin", "x\uFB1D\u05D1y ", [0, 5], level: level);
            Verify("arabic-supplementary-bare", "\U0001EE00\U0001EE01y ", [0, 6], level: level);
            Verify("arabic-supplementary-latin", "x\U0001EE00\U0001EE01y ", [0, 1, 7], level: level);
            Verify("devanagari-digits-bare", "\u0966\u0967\u0915y ", [0, 5], level: level);
            Verify("devanagari-digits-latin", "x\u0966\u0967\u0915y ", [0, 6], level: level);
            Verify("hangul-symbols-bare", "\u3200\u3201y ", [0, 4], level: level);
            Verify("hangul-symbols-latin", "x\u3200\u3201y ", [0, 5], level: level);
            Verify("balinese-symbols-bare", "\u1B61\u1B62y ", [0, 4], level: level);
            Verify("balinese-symbols-latin", "x\u1B61\u1B62y ", [0, 1, 5], level: level);
            Verify("myanmar-entry", "x\u1000\u1001y ", [0, 1, 2, 5], level: level);
            Verify("myanmar-stack", "x\u1000\u1039\u1001\u1002y ", [0, 1, 4, 7], level: level);
            Verify("myanmar-broken", "x\u1000\u102D\u103A\u1001y ", [0, 1, 3, 4, 7], level: level);
            Verify("myanmar-zwj", "x\u1000\u1039\u200D\u1001y ", [0, 1, 3, 4, 7], level: level);
            Verify("myanmar-zwnj", "x\u1000\u1039\u200C\u1001y ", [0, 1, 3, 4, 7], level: level);
        }

        void Verify(string name, string text, uint[] expected, uint leading = 0, int level = 0)
        {
            char[] source = text.ToCharArray();
            uint[] output = new uint[source.Length + 4];
            Array.Fill(output, Sentinel);
            fixed (char* input = source)
            fixed (uint* positions = output)
                VerifyResult(resolve((ushort*)input, (uint)source.Length, level, positions, (uint)output.Length));
            Check(source.AsSpan().SequenceEqual(text), name + ": source units changed");
            Array.Clear(source);
            Check(output.AsSpan(0, expected.Length).SequenceEqual(expected), name + ": retained source storage");
            if (managed)
            {
                Array.Fill(output, Sentinel);
                VerifyResult(NativeEditWordBoundaryInterop.Resolve(text, level, output));
            }

            void VerifyResult(NativeEditWordBoundaryResult result)
            {
                Check(result.Status == NativeRendererStatus.Success && result.ErrorCode == NativeEditWordBoundaryError.None &&
                    result.BoundaryCount == expected.Length && result.LeadingContentStart == leading,
                    $"{name}: exact result metadata ({result.Status}/{result.ErrorCode})");
                Check(output.AsSpan(0, expected.Length).SequenceEqual(expected), name + ": original boundary inventory");
                Check(output.AsSpan(expected.Length).IndexOfAnyExcept(Sentinel) < 0, name + ": untouched output tail");
            }
        }
    }

    private static void FailureControls(
        delegate* unmanaged[Cdecl]<ushort*, uint, int, uint*, uint, NativeEditWordBoundaryResult> resolve)
    {
        uint[] output = new uint[32];
        Array.Fill(output, Sentinel);
        char[] source = "alpha ".ToCharArray();
        fixed (char* input = source)
        fixed (uint* positions = output)
        {
            foreach (int level in new[] { -1, 2, 256, int.MinValue, int.MaxValue })
                Failed(resolve((ushort*)input, 6, level, positions, 32), NativeEditWordBoundaryError.InvalidParagraphLevel);
            Failed(resolve((ushort*)input, 6, 0, positions, 1), NativeEditWordBoundaryError.OutputTooSmall);
            Failed(resolve(null, 0, 0, null, 0), NativeEditWordBoundaryError.OutputTooSmall);
            Failed(resolve(null, 1, 0, positions, 32), NativeEditWordBoundaryError.InvalidBuffer);
            Failed(resolve((ushort*)input, 6, 0, null, 1), NativeEditWordBoundaryError.InvalidBuffer);
            Failed(resolve((ushort*)((byte*)input + 1), 1, 0, positions, 32), NativeEditWordBoundaryError.InvalidBuffer);
            Failed(resolve((ushort*)input, 6, 0, (uint*)((byte*)positions + 1), 1), NativeEditWordBoundaryError.InvalidBuffer);
            Failed(resolve((ushort*)(nuint.MaxValue - 1), 1, 0, positions, 32), NativeEditWordBoundaryError.InvalidBuffer);
            Failed(resolve((ushort*)input, 6, 0, (uint*)(nuint.MaxValue - 3), 1), NativeEditWordBoundaryError.InvalidBuffer);
            Failed(resolve((ushort*)input, 0x80000000, 0, positions, 32), NativeEditWordBoundaryError.InputTooLarge);
        }
        Check(source.AsSpan().SequenceEqual("alpha "), "failed request changed source");
        uint[] alias = new uint[16];
        Span<char> aliasText = MemoryMarshal.Cast<uint, char>(alias.AsSpan()).Slice(12, 6);
        source.CopyTo(aliasText);
        uint[] original = (uint[])alias.Clone();
        fixed (uint* positions = alias)
            Failed(resolve((ushort*)positions + 12, 6, 0, positions, 16), NativeEditWordBoundaryError.InvalidBuffer);
        Check(alias.AsSpan().SequenceEqual(original), "source alias in unused capacity changed");

        Reject("\uD800", NativeEditWordBoundaryError.InvalidEncoding, NativeRendererStatus.InvalidArgument);
        Reject("\uDC00", NativeEditWordBoundaryError.InvalidEncoding, NativeRendererStatus.InvalidArgument);
        // Common U+327F does not acquire the Hangul Script policy. Mixed
        // ordinary Hangul and unobserved symbol attachments stay unqualified.
        Reject("a\u327Fb ", NativeEditWordBoundaryError.UnqualifiedBmpSymbolPolicy);
        foreach (int level in new[] { 0, 1 })
        {
            Reject("\u3200\uAC00", NativeEditWordBoundaryError.UnqualifiedScriptItemTransitionPolicy, level: level);
            Reject("\u1B61\u0301y ", NativeEditWordBoundaryError.UnqualifiedScriptItemTransitionPolicy, level: level);
            Reject("x\u1A20\u1A21y ", NativeEditWordBoundaryError.UnqualifiedComplexScriptPolicy, level: level);
            Reject("x\u0711y ", NativeEditWordBoundaryError.UnqualifiedScriptItemTransitionPolicy, level: level);
        }
        // Rejection must not poison a later source generation.
        fixed (char* input = source)
        fixed (uint* positions = output)
        {
            var result = resolve((ushort*)input, 6, 0, positions, 32);
            Check(result.Status == NativeRendererStatus.Success && result.ErrorCode == NativeEditWordBoundaryError.None &&
                result.BoundaryCount == 2 && result.LeadingContentStart == 0 && output[0] == 0 && output[1] == 6 &&
                output.AsSpan(2).IndexOfAnyExcept(Sentinel) < 0, "recovery after rejected requests");
        }

        void Reject(string text, NativeEditWordBoundaryError error,
            NativeRendererStatus status = NativeRendererStatus.Unsupported, int level = 0)
        {
            fixed (char* input = text)
            fixed (uint* positions = output)
                Failed(resolve((ushort*)input, (uint)text.Length, level, positions, 32), error, status);
        }

        void Failed(NativeEditWordBoundaryResult result, NativeEditWordBoundaryError error,
            NativeRendererStatus status = NativeRendererStatus.InvalidArgument)
        {
            Check(result.Status == status && result.ErrorCode == error && result.BoundaryCount == 0 && result.LeadingContentStart == 0,
                $"atomic failure metadata: expected {status}/{error}, actual {result.Status}/{result.ErrorCode}");
            Check(output.AsSpan().IndexOfAnyExcept(Sentinel) < 0, "failed request changed output");
        }
    }

    private static void Check(bool value, string contract)
    {
        if (!value) throw new InvalidOperationException("Packaged EDIT boundary contract failed: " + contract);
    }
}
