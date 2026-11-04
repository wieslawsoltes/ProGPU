using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

// Independent Windows source observations of our own font. No renderer policy
// is inferred from a successful capture. Existing GridFit pixels stay exact;
// unfitted/fractional-size output is retained separately for attribution.
internal static class FontRasterSourceControls
{
    private const string FontHash = "60D952F14B770AEC08221B8ADEE1A1B7D82A8ABDC259FB2484DE9666A2F45832";
    private const string FixtureHash = "6D13E632B5F260217BC17D9C86B61E77506F463644EEEE923280400A52F3B092";

    internal static void Run(string output)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string fontPath = Path.Combine(AppContext.BaseDirectory, "ProGPU-Authored-Scan-1.ttf");
        byte[] fontBytes = File.ReadAllBytes(fontPath);
        byte[] fixture = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "BinaryGlyphOriginals.json"));
        Require(Convert.ToHexString(SHA256.HashData(fontBytes)) == FontHash, "Owned font changed.");
        Require(Convert.ToHexString(SHA256.HashData(fixture)) == FixtureHash, "Original GridFit fixture changed.");
        using JsonDocument document = JsonDocument.Parse(fixture);
        JsonElement[] cases = document.RootElement.GetProperty("cases").EnumerateArray()
            .Where(row => row.GetProperty("gasp").GetInt32() == 1 &&
                row.GetProperty("kind").GetString() == "right-edge" &&
                row.GetProperty("mode").GetInt32() == 1 &&
                Math.Abs(row.GetProperty("phase").GetInt32()) <= 1).ToArray();
        Require(cases.Select(row => row.GetProperty("index").GetInt32()).SequenceEqual(new[] { 93, 94, 95 }),
            "Original boundary inventory changed.");
        Stopwatch clock = Stopwatch.StartNew();
        nuint token = 0;
        nint collection = 0, family = 0;
        Exception? primary = null;
        var captures = new List<object>();
        int matchedOriginals = 0;
        try
        {
            Startup startup = new() { Version = 1 };
            Check(GdiplusStartup(out token, ref startup, 0));
            Check(GdipNewPrivateFontCollection(out collection));
            Check(GdipPrivateAddFontFile(collection, fontPath));
            Check(GdipGetFontCollectionFamilyCount(collection, out int familyCount));
            Require(familyCount == 1, "One private owned family is required.");
            nint[] families = new nint[1];
            Check(GdipGetFontCollectionFamilyList(collection, 1, families, out int found));
            family = families[0];
            Require(found == 1 && family != 0, "Private family was not realized.");
            foreach (JsonElement row in cases)
            foreach (float pixelsPerEm in new[] { 32f - 1f / 256f, 32f, 32f + 1f / 256f })
            foreach (int hint in new[] { 1, 2 }) // Original GDI+ fitted/unfitted monochrome.
            {
                Require(clock.Elapsed < TimeSpan.FromSeconds(10), "Owned source capture exceeded ten seconds.");
                ushort glyph = row.GetProperty("index").GetUInt16();
                uint[] pixels = Draw(family, glyph, pixelsPerEm, hint);
                int[] black = Enumerable.Range(0, pixels.Length).Where(i => pixels[i] == 0xff000000U).ToArray();
                bool originalGridFit = hint == 1 && pixelsPerEm == 32f;
                bool binary = pixels.Length == 64 * 64 && pixels.All(p => p is 0xff000000U or 0xffffffffU);
                bool? originalMatch = originalGridFit
                    ? binary && black.SequenceEqual(row.GetProperty("black").EnumerateArray().Select(p => p.GetInt32())) : null;
                byte[] rgba = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    rgba[i * 4] = (byte)(pixels[i] >> 16);
                    rgba[i * 4 + 1] = (byte)(pixels[i] >> 8);
                    rgba[i * 4 + 2] = (byte)pixels[i];
                    rgba[i * 4 + 3] = (byte)(pixels[i] >> 24);
                }
                captures.Add(new { glyph, pixelsPerEm, pixelsPerEmBits = BitConverter.SingleToInt32Bits(pixelsPerEm),
                    hint, originalGridFit, originalMatch, binary, black,
                    rgbaSha256 = Convert.ToHexString(SHA256.HashData(rgba)), rgbaBase64 = Convert.ToBase64String(rgba) });
                Require(binary, "The full original monochrome frame is required.");
                Require(originalMatch != false, $"Original GridFit pixels changed for glyph {glyph}.");
                if (originalGridFit) matchedOriginals++;
            }
            Require(captures.Count == 18, "Incomplete fitted/unfitted size matrix.");
            Require(clock.Elapsed < TimeSpan.FromSeconds(10), "Owned source capture exceeded ten seconds.");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            var failures = new List<int>();
            if (family != 0) failures.Add(GdipDeleteFontFamily(family));
            if (collection != 0) failures.Add(GdipDeletePrivateFontCollection(ref collection));
            if (token != 0) GdiplusShutdown(token);
            // Retain an actual rejected frame and every earlier observation;
            // neither a pixel rejection nor cleanup failure erases the evidence.
            try
            {
                File.WriteAllText(output, JsonSerializer.Serialize(new { schema = 1, fontSha256 = FontHash,
                    originalFixtureSha256 = FixtureHash, width = 64, height = 64, origin = new[] { 8, 24 },
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(), elapsedSeconds = clock.Elapsed.TotalSeconds,
                    completed = primary is null && failures.All(value => value == 0) && captures.Count == 18,
                    error = primary?.ToString(), cleanupStatuses = failures, originalGridFitComparisons = matchedOriginals,
                    unfittedOutputIsObservationOnly = true, captures }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error) when (primary is not null) { primary.Data["OriginalFontSourceReceipt"] = error.ToString(); }
            Cleanup(failures, primary);
        }
    }

    private static uint[] Draw(nint family, ushort glyph, float em, int hint)
    {
        nint font = 0, brush = 0, image = 0, graphics = 0;
        Exception? primary = null;
        try
        {
            Check(GdipCreateFont(family, em, 0, 2, out font)); // Pixel units, exact float size.
            Check(GdipCreateSolidFill(0xff000000U, out brush));
            Check(GdipCreateBitmapFromScan0(64, 64, 0, (int)PixelFormat.Format32bppArgb, 0, out image));
            Check(GdipBitmapSetResolution(image, 96, 96));
            Check(GdipGetImageGraphicsContext(image, out graphics));
            Check(GdipGraphicsClear(graphics, 0xffffffffU));
            Check(GdipSetTextRenderingHint(graphics, hint));
            Check(GdipSetTextContrast(graphics, 0));
            // Flags zero passes the retained original glyph ID; no cmap,
            // shaping, realized advance, source substitution or window input.
            Check(GdipDrawDriverString(graphics, new[] { glyph }, 1, font, brush,
                new[] { new PointF(8, 24) }, 0, 0));
            Check(GdipDeleteGraphics(graphics)); graphics = 0;
            uint[] pixels = new uint[64 * 64];
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                Check(GdipBitmapGetPixel(image, x, y, out pixels[y * 64 + x]));
            return pixels;
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            var failures = new List<int>();
            if (graphics != 0) failures.Add(GdipDeleteGraphics(graphics));
            if (image != 0) failures.Add(GdipDisposeImage(image));
            if (brush != 0) failures.Add(GdipDeleteBrush(brush));
            if (font != 0) failures.Add(GdipDeleteFont(font));
            Cleanup(failures, primary);
        }
    }

    private static void Cleanup(List<int> errors, Exception? primary)
    {
        if (!errors.Any(value => value != 0)) return;
        string message = "Original font-source cleanup failed: " + string.Join(',', errors);
        if (primary is not null) primary.Data["OriginalFontSourceCleanup"] = message;
        else throw new InvalidOperationException(message);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Check(int status) { if (status != 0) throw new InvalidOperationException("Original GDI+ status " + status); }
    [StructLayout(LayoutKind.Sequential)] private struct Startup { internal uint Version; internal nint DebugCallback; internal int SuppressBackgroundThread; internal int SuppressExternalCodecs; }
    [DllImport("gdiplus.dll")] private static extern int GdiplusStartup(out nuint token, ref Startup input, nint output);
    [DllImport("gdiplus.dll")] private static extern void GdiplusShutdown(nuint token);
    [DllImport("gdiplus.dll")] private static extern int GdipNewPrivateFontCollection(out nint collection);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipPrivateAddFontFile(nint collection, string path);
    [DllImport("gdiplus.dll")] private static extern int GdipGetFontCollectionFamilyCount(nint collection, out int count);
    [DllImport("gdiplus.dll")] private static extern int GdipGetFontCollectionFamilyList(nint collection, int count, [Out] nint[] families, out int found);
    [DllImport("gdiplus.dll")] private static extern int GdipDeletePrivateFontCollection(ref nint collection);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateFont(nint family, float size, int style, int unit, out nint font);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateSolidFill(uint color, out nint brush);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, nint scan, out nint bitmap);
    [DllImport("gdiplus.dll")] private static extern int GdipBitmapSetResolution(nint bitmap, float x, float y);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageGraphicsContext(nint image, out nint graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipGraphicsClear(nint graphics, uint color);
    [DllImport("gdiplus.dll")] private static extern int GdipSetTextRenderingHint(nint graphics, int hint);
    [DllImport("gdiplus.dll")] private static extern int GdipSetTextContrast(nint graphics, uint contrast);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawDriverString(nint graphics, [In] ushort[] glyphs, int length, nint font, nint brush, [In] PointF[] positions, int flags, nint matrix);
    [DllImport("gdiplus.dll")] private static extern int GdipBitmapGetPixel(nint bitmap, int x, int y, out uint argb);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteGraphics(nint graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipDisposeImage(nint image);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteBrush(nint brush);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteFont(nint font);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteFontFamily(nint family);
}
