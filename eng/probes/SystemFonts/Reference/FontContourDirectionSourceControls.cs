using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

internal static partial class FontRasterSourceControls
{
    // Additional source observations only. The original 98-frame acceptance
    // remains in Run, with its original font, expectations and deadline.
    internal static void RunReversed(string output)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        const string reversedHash = "872C5A82825C5E1AA3E51589D758680B36A7E005B8FA982EFD21D19B239FB91D";
        string[] paths = [Path.Combine(AppContext.BaseDirectory, "ProGPU-Authored-Scan-1.ttf"),
            Path.Combine(AppContext.BaseDirectory, "ProGPU-Authored-Scan-Reversed.ttf")];
        string[] expectedHashes = [FontHash, reversedHash];
        for (int i = 0; i < paths.Length; i++)
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths[i]))) == expectedHashes[i],
                "Owned direction-control font changed.");
        byte[] fixture = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "BinaryGlyphOriginals.json"));
        Require(Convert.ToHexString(SHA256.HashData(fixture)) == FixtureHash, "Original fixture changed.");
        using JsonDocument document = JsonDocument.Parse(fixture);
        int[] selected = [6, 93, 94, 95, 182, 270, 358];
        JsonElement[] cases = document.RootElement.GetProperty("cases").EnumerateArray()
            .Where(row => row.GetProperty("gasp").GetInt32() == 1 &&
                selected.Contains(row.GetProperty("index").GetInt32())).ToArray();
        Require(cases.Select(row => row.GetProperty("index").GetInt32()).SequenceEqual(selected) &&
            cases.All(row => row.GetProperty("kind").GetString() == "right-edge"),
            "Complete seven-contour direction inventory required.");
        Require(!File.Exists(output), "Fresh source direction receipt required.");
        Stopwatch clock = Stopwatch.StartNew();
        nuint token = 0;
        nint[] collections = new nint[2], families = new nint[2];
        List<object> captures = [];
        List<int> cleanupFailures = [];
        Exception? primary = null;
        bool completed = false;
        int matchedOriginals = 0;
        try
        {
            Startup startup = new() { Version = 1 };
            Check(GdiplusStartup(out token, ref startup, 0));
            for (int i = 0; i < paths.Length; i++)
            {
                Check(GdipNewPrivateFontCollection(out collections[i]));
                Check(GdipPrivateAddFontFile(collections[i], paths[i]));
                Check(GdipGetFontCollectionFamilyCount(collections[i], out int count));
                Require(count == 1, "One exact private family required.");
                nint[] borrowed = new nint[1];
                Check(GdipGetFontCollectionFamilyList(collections[i], 1, borrowed, out int found));
                Require(found == 1 && borrowed[0] != 0, "Private direction family absent.");
                Check(GdipCloneFontFamily(borrowed[0], out families[i]));
                Require(families[i] != 0, "Owned direction family reference absent.");
            }
            foreach (JsonElement row in cases)
            foreach (int hint in new[] { 1, 2 })
            for (int direction = 0; direction < 2; direction++)
            {
                Require(clock.Elapsed < TimeSpan.FromSeconds(10), "Direction observation exceeded ten seconds.");
                ushort glyph = row.GetProperty("index").GetUInt16();
                uint[] pixels = Draw(families[direction], glyph, 32f, hint);
                int[] black = Enumerable.Range(0, pixels.Length).Where(i => pixels[i] == 0xff000000U).ToArray();
                bool binary = pixels.Length == 64 * 64 && pixels.All(p => p is 0xff000000U or 0xffffffffU);
                Require(binary, "Complete binary direction frame required.");
                bool? originalMatch = direction == 0 && hint == 1
                    ? black.SequenceEqual(row.GetProperty("black").EnumerateArray().Select(p => p.GetInt32())) : null;
                Require(originalMatch != false, "Original independent GridFit pixels changed.");
                if (originalMatch == true) matchedOriginals++;
                byte[] rgba = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    rgba[i*4] = (byte)(pixels[i] >> 16);
                    rgba[i*4+1] = (byte)(pixels[i] >> 8);
                    rgba[i*4+2] = (byte)pixels[i];
                    rgba[i*4+3] = (byte)(pixels[i] >> 24);
                }
                captures.Add(new { glyph, mode = row.GetProperty("mode").GetInt32(),
                    phase = row.GetProperty("phase").GetInt32(), reversed = direction == 1,
                    fontSha256 = expectedHashes[direction], pixelsPerEm = 32f,
                    pixelsPerEmBits = BitConverter.SingleToInt32Bits(32f), hint, originalMatch,
                    binary, black, rgbaSha256 = Convert.ToHexString(SHA256.HashData(rgba)),
                    rgbaBase64 = Convert.ToBase64String(rgba) });
            }
            Require(captures.Count == 28 && matchedOriginals == 7 &&
                clock.Elapsed < TimeSpan.FromSeconds(10), "Incomplete bounded direction observations.");
            completed = true;
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            for (int i = families.Length - 1; i >= 0; i--)
            {
                if (families[i] != 0)
                {
                    int status = GdipDeleteFontFamily(families[i]);
                    if (status != 0) cleanupFailures.Add(status);
                }
                if (collections[i] != 0)
                {
                    int status = GdipDeletePrivateFontCollection(ref collections[i]);
                    if (status != 0) cleanupFailures.Add(status);
                }
            }
            if (token != 0) GdiplusShutdown(token);
            try
            {
                File.WriteAllText(output, JsonSerializer.Serialize(new {
                    schema = "owned-contour-direction-observation-v1", qualified = false,
                    completed = completed && cleanupFailures.Count == 0,
                    retired = cleanupFailures.Count == 0, elapsedSeconds = clock.Elapsed.TotalSeconds,
                    matchedOriginals, inputEvents = 0, desktopWindows = 0,
                    fixtureSha256 = FixtureHash, captures, cleanupFailures,
                    error = primary?.ToString()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error)
            {
                if (primary == null) throw;
                primary.Data["DirectionReceiptFailure"] = error;
            }
            if (cleanupFailures.Count != 0)
            {
                if (primary == null) throw new InvalidOperationException("Direction native retirement failed.");
                primary.Data["DirectionRetirementFailures"] = cleanupFailures.ToArray();
            }
        }
    }
}
