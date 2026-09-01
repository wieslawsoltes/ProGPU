using BenchmarkDotNet.Attributes;
using ProGPU.SystemDrawing;
using ProGPU.Scene;
using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Text;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class MetafileBenchmarks
{
    private const int StateRecordCount = 4_096;
    private byte[] _fixture = null!;
    private Bitmap _target = null!;
    private Graphics _graphics = null!;
    private Metafile _metafile = null!;
    private DrawingContext _playbackContext = null!;
    private Graphics _playbackGraphics = null!;
    private Metafile _playbackMetafile = null!;
    private Metafile _emfArcPlaybackMetafile = null!;
    private Metafile _emfPolyDrawPlaybackMetafile = null!;
    private Metafile _emfClipPlaybackMetafile = null!;
    private Metafile _emfRegionClipPlaybackMetafile = null!;
    private Metafile _emfDibPlaybackMetafile = null!;
    private Metafile _emfPathPlaybackMetafile = null!;
    private Metafile _emfExtendedTextPlaybackMetafile = null!;
    private Metafile _emfPdyExtendedTextPlaybackMetafile = null!;
    private Metafile _emfGlyphIndexExtendedTextPlaybackMetafile = null!;
    private Metafile _emfNaturalGlyphIndexExtendedTextPlaybackMetafile = null!;
    private Metafile _emfAnsiExtendedTextPlaybackMetafile = null!;
    private Metafile _emfPolyTextPlaybackMetafile = null!;
    private Metafile _emfSmallTextPlaybackMetafile = null!;
    private Metafile _wmfPlaybackMetafile = null!;
    private Metafile _wmfRectanglePlaybackMetafile = null!;
    private Metafile _wmfClippedRectanglePlaybackMetafile = null!;
    private Metafile _wmfEllipsePlaybackMetafile = null!;
    private Metafile _wmfRoundRectanglePlaybackMetafile = null!;
    private Metafile _wmfPiePlaybackMetafile = null!;
    private Metafile _wmfChordPlaybackMetafile = null!;
    private Metafile _wmfLinePlaybackMetafile = null!;
    private Metafile _wmfPixelPlaybackMetafile = null!;
    private Metafile _wmfPolyPolygonPlaybackMetafile = null!;
    private Metafile _wmfMappedPixelPlaybackMetafile = null!;
    private Metafile _wmfPatBltPlaybackMetafile = null!;
    private Metafile _wmfOffsetClipPatBltPlaybackMetafile = null!;
    private Metafile _wmfSourceIndependentBitmapPlaybackMetafile = null!;
    private Metafile _wmfDestinationOnlyBitmapPlaybackMetafile = null!;
    private Metafile _wmfBitmap16AdapterPlaybackMetafile = null!;
    private Metafile _wmfDibPlaybackMetafile = null!;
    private Metafile _bitFieldDibPlaybackMetafile = null!;
    private Metafile _rleDibPlaybackMetafile = null!;
    private Metafile _encodedDibPlaybackMetafile = null!;
    private Metafile _logicalPaletteDibPlaybackMetafile = null!;
    private Metafile _cmykDibPlaybackMetafile = null!;
    private Metafile _notSourceCopyDibPlaybackMetafile = null!;
    private Metafile _destinationDependentDibPlaybackMetafile = null!;
    private Metafile _wmfTextPlaybackMetafile = null!;
    private Metafile _wmfSpacedRotatedTextPlaybackMetafile = null!;
    private Metafile _wmfJustifiedRotatedTextPlaybackMetafile = null!;
    private Metafile _wmfExtendedTextPlaybackMetafile = null!;
    private Metafile _wmfRotatedExtendedTextPlaybackMetafile = null!;
    private readonly Graphics.EnumerateMetafileProc _enumerate = static (_, _, _, _, _) => true;
    private readonly byte[] _comment = new byte[64];
    private IDisposable _wmfBitmap16Registration = null!;

    [GlobalSetup]
    public void CreateFixture()
    {
        _fixture = CreateEmf(StateRecordCount);
        _target = new Bitmap(1, 1);
        _graphics = Graphics.FromImage(_target);
        _metafile = new Metafile(new MemoryStream(_fixture, writable: false));
        _playbackContext = new DrawingContext();
        _playbackGraphics = Graphics.FromProGpuDrawingContext(_playbackContext);
        _playbackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackEmf(256), writable: false));
        _emfArcPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackEmfArcs(256), writable: false));
        _emfPolyDrawPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackEmfPolyDraw16(256), writable: false));
        _emfClipPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackEmfClipSequences(256), writable: false));
        _emfRegionClipPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackEmfRegionClips(256), writable: false));
        _emfDibPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackEmfDibImages(256), writable: false));
        _emfPathPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackEmfPathBrackets(256), writable: false));
        _emfExtendedTextPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackEmfExtendedText(256), writable: false));
        _emfPdyExtendedTextPlaybackMetafile = new Metafile(
            new MemoryStream(
                CreatePlaybackEmfExtendedText(256, pdy: true),
                writable: false));
        _emfGlyphIndexExtendedTextPlaybackMetafile = new Metafile(
            new MemoryStream(
                CreatePlaybackEmfExtendedText(256, glyphIndices: true),
                writable: false));
        _emfNaturalGlyphIndexExtendedTextPlaybackMetafile = new Metafile(
            new MemoryStream(
                CreatePlaybackEmfExtendedText(
                    256,
                    glyphIndices: true,
                    naturalGlyphAdvances: true),
                writable: false));
        _emfAnsiExtendedTextPlaybackMetafile = new Metafile(
            new MemoryStream(
                CreatePlaybackEmfExtendedText(256, unicode: false),
                writable: false));
        _emfPolyTextPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackEmfPolyText(256), writable: false));
        _emfSmallTextPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackEmfSmallText(256), writable: false));
        _wmfPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmf(256), writable: false));
        _wmfRectanglePlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfBoxes(256, 0x041B), writable: false));
        _wmfClippedRectanglePlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfBoxes(256, 0x041B, includeClipState: true), writable: false));
        _wmfEllipsePlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfBoxes(256, 0x0418), writable: false));
        _wmfRoundRectanglePlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfBoxes(256, 0x061C), writable: false));
        _wmfPiePlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfFilledArcs(256, 0x081A), writable: false));
        _wmfChordPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfFilledArcs(256, 0x0830), writable: false));
        _wmfLinePlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfLineOrPixels(256, setPixels: false), writable: false));
        _wmfPixelPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfLineOrPixels(256, setPixels: true), writable: false));
        _wmfPolyPolygonPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfPolyPolygons(256), writable: false));
        _wmfMappedPixelPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfMappedPixels(256), writable: false));
        _wmfPatBltPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfPatBlts(256), writable: false));
        _wmfOffsetClipPatBltPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfPatBlts(256, includeOffsetClipState: true), writable: false));
        _wmfSourceIndependentBitmapPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfSourceIndependentBitmapRecords(256), writable: false));
        _wmfDestinationOnlyBitmapPlaybackMetafile = new Metafile(
            new MemoryStream(
                CreatePlaybackWmfSourceIndependentBitmapRecords(
                    256,
                    rasterOperation: 0x0055_0009),
                writable: false));
        _wmfBitmap16Registration = WmfBitmap16DecodeServices.Register(
            new PassthroughWmfBitmap16DecodeService());
        _wmfBitmap16AdapterPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfBitmap16AdapterRecords(256), writable: false));
        _wmfDibPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfDibImages(256), writable: false));
        _bitFieldDibPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfDibImages(256, bitFields: true), writable: false));
        _rleDibPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfRleImages(256), writable: false));
        _encodedDibPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfEncodedImages(256), writable: false));
        _logicalPaletteDibPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfLogicalPaletteImages(256), writable: false));
        _cmykDibPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfCmykImages(256), writable: false));
        _notSourceCopyDibPlaybackMetafile = new Metafile(
            new MemoryStream(
                CreatePlaybackWmfDibImages(256, rasterOperation: 0x0033_0008),
                writable: false));
        _destinationDependentDibPlaybackMetafile = new Metafile(
            new MemoryStream(
                CreatePlaybackWmfDibImages(256, rasterOperation: 0x0066_0046),
                writable: false));
        _wmfTextPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfText(256), writable: false));
        _wmfSpacedRotatedTextPlaybackMetafile = new Metafile(
            new MemoryStream(
                CreatePlaybackWmfText(256, escapement: 900, characterExtra: 4),
                writable: false));
        _wmfJustifiedRotatedTextPlaybackMetafile = new Metafile(
            new MemoryStream(
                CreatePlaybackWmfText(
                    256,
                    escapement: 900,
                    characterExtra: 2,
                    breakExtra: 5,
                    breakCount: 1),
                writable: false));
        _wmfExtendedTextPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfExtendedText(256), writable: false));
        _wmfRotatedExtendedTextPlaybackMetafile = new Metafile(
            new MemoryStream(CreatePlaybackWmfExtendedText(256, escapement: 900), writable: false));
    }

    [GlobalCleanup]
    public void DisposeFixture()
    {
        _metafile.Dispose();
        _playbackMetafile.Dispose();
        _emfArcPlaybackMetafile.Dispose();
        _emfPolyDrawPlaybackMetafile.Dispose();
        _emfClipPlaybackMetafile.Dispose();
        _emfRegionClipPlaybackMetafile.Dispose();
        _emfDibPlaybackMetafile.Dispose();
        _emfPathPlaybackMetafile.Dispose();
        _emfExtendedTextPlaybackMetafile.Dispose();
        _emfPdyExtendedTextPlaybackMetafile.Dispose();
        _emfGlyphIndexExtendedTextPlaybackMetafile.Dispose();
        _emfNaturalGlyphIndexExtendedTextPlaybackMetafile.Dispose();
        _emfAnsiExtendedTextPlaybackMetafile.Dispose();
        _emfPolyTextPlaybackMetafile.Dispose();
        _emfSmallTextPlaybackMetafile.Dispose();
        _wmfPlaybackMetafile.Dispose();
        _wmfRectanglePlaybackMetafile.Dispose();
        _wmfClippedRectanglePlaybackMetafile.Dispose();
        _wmfEllipsePlaybackMetafile.Dispose();
        _wmfRoundRectanglePlaybackMetafile.Dispose();
        _wmfPiePlaybackMetafile.Dispose();
        _wmfChordPlaybackMetafile.Dispose();
        _wmfLinePlaybackMetafile.Dispose();
        _wmfPixelPlaybackMetafile.Dispose();
        _wmfPolyPolygonPlaybackMetafile.Dispose();
        _wmfMappedPixelPlaybackMetafile.Dispose();
        _wmfPatBltPlaybackMetafile.Dispose();
        _wmfOffsetClipPatBltPlaybackMetafile.Dispose();
        _wmfSourceIndependentBitmapPlaybackMetafile.Dispose();
        _wmfDestinationOnlyBitmapPlaybackMetafile.Dispose();
        _wmfBitmap16AdapterPlaybackMetafile.Dispose();
        _wmfBitmap16Registration.Dispose();
        _wmfDibPlaybackMetafile.Dispose();
        _bitFieldDibPlaybackMetafile.Dispose();
        _rleDibPlaybackMetafile.Dispose();
        _encodedDibPlaybackMetafile.Dispose();
        _logicalPaletteDibPlaybackMetafile.Dispose();
        _cmykDibPlaybackMetafile.Dispose();
        _notSourceCopyDibPlaybackMetafile.Dispose();
        _destinationDependentDibPlaybackMetafile.Dispose();
        _wmfTextPlaybackMetafile.Dispose();
        _wmfSpacedRotatedTextPlaybackMetafile.Dispose();
        _wmfJustifiedRotatedTextPlaybackMetafile.Dispose();
        _wmfExtendedTextPlaybackMetafile.Dispose();
        _wmfRotatedExtendedTextPlaybackMetafile.Dispose();
        _playbackGraphics.Dispose();
        _graphics.Dispose();
        _target.Dispose();
    }

    [Benchmark]
    public int ParseAndEnumerate4096RecordFixture()
    {
        using var stream = new MemoryStream(_fixture, writable: false);
        using var metafile = new Metafile(stream);
        return metafile.Records.Length;
    }

    [Benchmark]
    public void Enumerate4098RecordsWithoutPayloadCopies() =>
        _graphics.EnumerateMetafile(_metafile, Point.Empty, _enumerate);

    [Benchmark]
    public int RecordAndFinalize256PortableComments()
    {
        using var target = new MemoryStream(capacity: 24 * 1024);
        using Metafile metafile = PortableMetafile.Create(target, new Rectangle(0, 0, 640, 480));
        using (Graphics recorder = Graphics.FromImage(metafile))
        {
            for (int index = 0; index < 256; index++)
            {
                recorder.AddMetafileComment(_comment);
            }
        }

        return checked((int)target.Length);
    }

    [Benchmark]
    public int Playback256RectanglesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_playbackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EmfArcFamilyToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _emfArcPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EmfPolyDraw16ToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _emfPolyDrawPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EmfOffsetExcludeClipSequences()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _emfClipPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EmfRegionDataClipSelections()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _emfRegionClipPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EmfDibImagesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _emfDibPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfDibImagesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _wmfDibPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256BitFieldDibImagesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _bitFieldDibPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256RleDibImagesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _rleDibPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EncodedDibImagesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _encodedDibPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256LogicalPaletteDibImagesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _logicalPaletteDibPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256CmykDibImagesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _cmykDibPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256NotSourceCopyDibImagesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _notSourceCopyDibPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256DestinationDependentDibImagesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _destinationDependentDibPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EmfPathBracketsToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _emfPathPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EmfExtTextOutWWithAdvances()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _emfExtendedTextPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EmfExtTextOutWPdyAdvances()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _emfPdyExtendedTextPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EmfExtTextOutWGlyphIndices()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _emfGlyphIndexExtendedTextPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EmfExtTextOutWNaturalGlyphIndices()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _emfNaturalGlyphIndexExtendedTextPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EmfExtTextOutAWithAdvances()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _emfAnsiExtendedTextPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EmfPolyTextOutWTwoStringsWithAdvances()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _emfPolyTextPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256EmfSmallTextOutSmallChars()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _emfSmallTextPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfPolygonsToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfRectanglesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfRectanglePlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfRectanglesWithClipState()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfClippedRectanglePlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfEllipsesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfEllipsePlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfRoundRectanglesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfRoundRectanglePlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfPiesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfPiePlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfChordsToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfChordPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfLinesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfLinePlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfSetPixelsToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfPixelPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfPolyPolygonsToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfPolyPolygonPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfMappedPixelsWithViewportState()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfMappedPixelPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfPatternCopiesToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfPatBltPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfPatternCopiesWithOffsetClipState()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfOffsetClipPatBltPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfSourceIndependentBitmapRecordsToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _wmfSourceIndependentBitmapPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfDestinationOnlyBitmapRecordsToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _wmfDestinationOnlyBitmapPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfBitmap16AdapterRecordsToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _wmfBitmap16AdapterPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfTextOutToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(_wmfTextPlaybackMetafile, new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfSpacedRotatedTextOutToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _wmfSpacedRotatedTextPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfJustifiedRotatedTextOutToRetainedCommands()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _wmfJustifiedRotatedTextPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfExtTextOutWithClipAndAdvances()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _wmfExtendedTextPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    [Benchmark]
    public int Playback256WmfRotatedExtTextOutWithAdvances()
    {
        _playbackContext.Clear();
        _playbackGraphics.DrawImage(
            _wmfRotatedExtendedTextPlaybackMetafile,
            new Rectangle(0, 0, 640, 480));
        int commandCount = _playbackContext.Commands.Count;
        _playbackContext.Clear();
        return commandCount;
    }

    private static byte[] CreateEmf(int stateRecordCount)
    {
        int totalBytes = checked(88 + stateRecordCount * 8 + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, 1);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, (uint)(stateRecordCount + 2));
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        for (int index = 0; index < stateRecordCount; index++)
        {
            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfSaveDC);
            WriteUInt32(bytes, cursor + 4, 8);
            cursor += 8;
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static byte[] CreatePlaybackEmf(int rectangleCount)
    {
        int totalBytes = checked(88 + 24 + rectangleCount * 24 + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, checked((uint)(rectangleCount + 4)));
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        WriteSelectObject(bytes, cursor, 0x8000_0004);
        cursor += 12;
        WriteSelectObject(bytes, cursor, 0x8000_0008);
        cursor += 12;
        for (int index = 0; index < rectangleCount; index++)
        {
            int x = (index % 16) * 40;
            int y = (index / 16) * 30;
            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfRectangle);
            WriteUInt32(bytes, cursor + 4, 24);
            WriteInt32(bytes, cursor + 8, x);
            WriteInt32(bytes, cursor + 12, y);
            WriteInt32(bytes, cursor + 16, x + 32);
            WriteInt32(bytes, cursor + 20, y + 22);
            cursor += 24;
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static byte[] CreatePlaybackEmfDibImages(int recordCount)
    {
        const int recordSize = 136;
        const int bitmapInfoOffset = 80;
        const int bitmapBitsOffset = 120;
        int totalBytes = checked(88 + recordCount * recordSize + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, checked((uint)(recordCount + 2)));
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        for (int index = 0; index < recordCount; index++)
        {
            int x = (index % 16) * 40;
            int y = (index / 16) * 30;
            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfStretchDIBits);
            WriteUInt32(bytes, cursor + 4, recordSize);
            WriteInt32(bytes, cursor + 24, x);
            WriteInt32(bytes, cursor + 28, y);
            WriteInt32(bytes, cursor + 32, 0);
            WriteInt32(bytes, cursor + 36, 0);
            WriteInt32(bytes, cursor + 40, 2);
            WriteInt32(bytes, cursor + 44, 2);
            WriteUInt32(bytes, cursor + 48, bitmapInfoOffset);
            WriteUInt32(bytes, cursor + 52, 40);
            WriteUInt32(bytes, cursor + 56, bitmapBitsOffset);
            WriteUInt32(bytes, cursor + 60, 16);
            WriteUInt32(bytes, cursor + 64, 0);
            WriteUInt32(bytes, cursor + 68, 0x00CC_0020);
            WriteInt32(bytes, cursor + 72, 32);
            WriteInt32(bytes, cursor + 76, 22);

            int info = cursor + bitmapInfoOffset;
            WriteUInt32(bytes, info, 40);
            WriteInt32(bytes, info + 4, 2);
            WriteInt32(bytes, info + 8, -2);
            WriteUInt16(bytes, info + 12, 1);
            WriteUInt16(bytes, info + 14, 32);
            WriteUInt32(bytes, info + 20, 16);

            int bits = cursor + bitmapBitsOffset;
            WriteUInt32(bytes, bits, 0x0000_00FF);
            WriteUInt32(bytes, bits + 4, 0x0000_FF00);
            WriteUInt32(bytes, bits + 8, 0x00FF_0000);
            WriteUInt32(bytes, bits + 12, 0x00FF_FFFF);
            cursor += recordSize;
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static byte[] CreatePlaybackEmfArcs(int recordCount)
    {
        int totalBytes = checked(88 + 24 + 12 + recordCount * 40 + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, checked((uint)(recordCount + 5)));
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        WriteSelectObject(bytes, cursor, 0x8000_0004);
        cursor += 12;
        WriteSelectObject(bytes, cursor, 0x8000_0008);
        cursor += 12;
        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfSetArcDirection);
        WriteUInt32(bytes, cursor + 4, 12);
        WriteInt32(bytes, cursor + 8, 2);
        cursor += 12;
        for (int index = 0; index < recordCount; index++)
        {
            int left = (index % 16) * 40;
            int top = (index / 16) * 30;
            int right = left + 32;
            int bottom = top + 22;
            EmfPlusRecordType type = (index % 3) switch
            {
                0 => EmfPlusRecordType.EmfRoundArc,
                1 => EmfPlusRecordType.EmfPie,
                _ => EmfPlusRecordType.EmfChord
            };
            WriteUInt32(bytes, cursor, (uint)type);
            WriteUInt32(bytes, cursor + 4, 40);
            WriteInt32(bytes, cursor + 8, left);
            WriteInt32(bytes, cursor + 12, top);
            WriteInt32(bytes, cursor + 16, right);
            WriteInt32(bytes, cursor + 20, bottom);
            WriteInt32(bytes, cursor + 24, right);
            WriteInt32(bytes, cursor + 28, top + 11);
            WriteInt32(bytes, cursor + 32, left + 16);
            WriteInt32(bytes, cursor + 36, bottom);
            cursor += 40;
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static byte[] CreatePlaybackEmfPolyDraw16(int recordCount)
    {
        const int RecordSize = 48;
        int totalBytes = checked(88 + recordCount * RecordSize + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, checked((uint)(recordCount + 2)));
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index % 16) * 40 + 4));
            short y = checked((short)((index / 16) * 30 + 4));
            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfPolyDraw16);
            WriteUInt32(bytes, cursor + 4, RecordSize);
            WriteUInt32(bytes, cursor + 24, 4);
            WriteInt16(bytes, cursor + 28, x);
            WriteInt16(bytes, cursor + 30, y);
            WriteInt16(bytes, cursor + 32, checked((short)(x + 8)));
            WriteInt16(bytes, cursor + 34, y);
            WriteInt16(bytes, cursor + 36, checked((short)(x + 16)));
            WriteInt16(bytes, cursor + 38, checked((short)(y + 18)));
            WriteInt16(bytes, cursor + 40, checked((short)(x + 24)));
            WriteInt16(bytes, cursor + 42, checked((short)(y + 18)));
            bytes[cursor + 44] = 0x06;
            bytes[cursor + 45] = 0x04;
            bytes[cursor + 46] = 0x04;
            bytes[cursor + 47] = 0x04;
            cursor += RecordSize;
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static byte[] CreatePlaybackEmfClipSequences(int sequenceCount)
    {
        const int SequenceSize = 84;
        int totalBytes = checked(88 + 24 + 24 + sequenceCount * SequenceSize + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, checked((uint)(sequenceCount * 5 + 5)));
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        WriteSelectObject(bytes, cursor, 0x8000_0004);
        cursor += 12;
        WriteSelectObject(bytes, cursor, 0x8000_0008);
        cursor += 12;
        WriteRectangleRecord(
            bytes,
            ref cursor,
            EmfPlusRecordType.EmfIntersectClipRect,
            0,
            0,
            640,
            480);
        for (int index = 0; index < sequenceCount; index++)
        {
            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfSaveDC);
            WriteUInt32(bytes, cursor + 4, 8);
            cursor += 8;

            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfOffsetClipRgn);
            WriteUInt32(bytes, cursor + 4, 16);
            WriteInt32(bytes, cursor + 8, index & 7);
            WriteInt32(bytes, cursor + 12, (index >> 3) & 7);
            cursor += 16;

            int x = (index % 16) * 40;
            int y = (index / 16) * 30;
            WriteRectangleRecord(
                bytes,
                ref cursor,
                EmfPlusRecordType.EmfExcludeClipRect,
                x + 8,
                y + 6,
                x + 24,
                y + 18);
            WriteRectangleRecord(
                bytes,
                ref cursor,
                EmfPlusRecordType.EmfRectangle,
                x,
                y,
                x + 32,
                y + 24);

            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfRestoreDC);
            WriteUInt32(bytes, cursor + 4, 12);
            WriteInt32(bytes, cursor + 8, -1);
            cursor += 12;
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static byte[] CreatePlaybackEmfRegionClips(int sequenceCount)
    {
        const int RegionRecordSize = 80;
        const int RectangleRecordSize = 24;
        int totalBytes = checked(
            88 + 24 + RegionRecordSize + 8 +
            sequenceCount * (RegionRecordSize + RectangleRecordSize) + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, checked((uint)(sequenceCount * 2 + 6)));
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        WriteSelectObject(bytes, cursor, 0x8000_0004);
        cursor += 12;
        WriteSelectObject(bytes, cursor, 0x8000_0008);
        cursor += 12;
        WriteRegionRecord(bytes, ref cursor, 5, 0, 0, 320, 480, 320, 0, 640, 480);
        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfSetMetaRgn);
        WriteUInt32(bytes, cursor + 4, 8);
        cursor += 8;

        for (int index = 0; index < sequenceCount; index++)
        {
            int x = (index % 16) * 40;
            int y = (index / 16) * 30;
            WriteRegionRecord(
                bytes,
                ref cursor,
                index % 5 + 1,
                x,
                y,
                x + 16,
                y + 22,
                x + 20,
                y,
                x + 36,
                y + 22);
            WriteRectangleRecord(
                bytes,
                ref cursor,
                EmfPlusRecordType.EmfRectangle,
                x,
                y,
                x + 36,
                y + 22);
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static void WriteRegionRecord(
        byte[] bytes,
        ref int cursor,
        int mode,
        int left0,
        int top0,
        int right0,
        int bottom0,
        int left1,
        int top1,
        int right1,
        int bottom1)
    {
        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfExtSelectClipRgn);
        WriteUInt32(bytes, cursor + 4, 80);
        WriteUInt32(bytes, cursor + 8, 64);
        WriteInt32(bytes, cursor + 12, mode);
        WriteUInt32(bytes, cursor + 16, 32);
        WriteUInt32(bytes, cursor + 20, 1);
        WriteUInt32(bytes, cursor + 24, 2);
        WriteUInt32(bytes, cursor + 28, 32);
        WriteInt32(bytes, cursor + 32, Math.Min(left0, left1));
        WriteInt32(bytes, cursor + 36, Math.Min(top0, top1));
        WriteInt32(bytes, cursor + 40, Math.Max(right0, right1));
        WriteInt32(bytes, cursor + 44, Math.Max(bottom0, bottom1));
        WriteInt32(bytes, cursor + 48, left0);
        WriteInt32(bytes, cursor + 52, top0);
        WriteInt32(bytes, cursor + 56, right0);
        WriteInt32(bytes, cursor + 60, bottom0);
        WriteInt32(bytes, cursor + 64, left1);
        WriteInt32(bytes, cursor + 68, top1);
        WriteInt32(bytes, cursor + 72, right1);
        WriteInt32(bytes, cursor + 76, bottom1);
        cursor += 80;
    }

    private static byte[] CreatePlaybackEmfPathBrackets(int sequenceCount)
    {
        const int SequenceSize = 64;
        int totalBytes = checked(88 + 24 + sequenceCount * SequenceSize + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, checked((uint)(sequenceCount * 4 + 4)));
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        WriteSelectObject(bytes, cursor, 0x8000_0004);
        cursor += 12;
        WriteSelectObject(bytes, cursor, 0x8000_0008);
        cursor += 12;
        for (int index = 0; index < sequenceCount; index++)
        {
            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfBeginPath);
            WriteUInt32(bytes, cursor + 4, 8);
            cursor += 8;

            int x = (index % 16) * 40;
            int y = (index / 16) * 30;
            WriteRectangleRecord(
                bytes,
                ref cursor,
                EmfPlusRecordType.EmfRectangle,
                x,
                y,
                x + 32,
                y + 22);

            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEndPath);
            WriteUInt32(bytes, cursor + 4, 8);
            cursor += 8;

            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfStrokeAndFillPath);
            WriteUInt32(bytes, cursor + 4, 24);
            WriteInt32(bytes, cursor + 8, x);
            WriteInt32(bytes, cursor + 12, y);
            WriteInt32(bytes, cursor + 16, x + 32);
            WriteInt32(bytes, cursor + 20, y + 22);
            cursor += 24;
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static byte[] CreatePlaybackEmfExtendedText(
        int recordCount,
        bool unicode = true,
        bool pdy = false,
        bool glyphIndices = false,
        bool naturalGlyphAdvances = false)
    {
        const int fontRecordSize = 104;
        if (pdy && !unicode)
        {
            throw new ArgumentException("The PDY benchmark fixture requires Unicode text.", nameof(unicode));
        }
        if (glyphIndices && !unicode)
        {
            throw new ArgumentException("The glyph-index benchmark fixture requires Unicode storage.", nameof(unicode));
        }
        if (naturalGlyphAdvances && !glyphIndices)
        {
            throw new ArgumentException(
                "Natural glyph advances require a glyph-index fixture.",
                nameof(naturalGlyphAdvances));
        }
        int textRecordSize = pdy ? 108 : naturalGlyphAdvances ? 84 : unicode ? 96 : 92;
        int totalBytes = checked(
            88 + fontRecordSize + 12 + 12 + 12 + recordCount * textRecordSize + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, checked((uint)recordCount + 6));
        WriteUInt16(bytes, 56, 2);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfExtCreateFontIndirect);
        WriteUInt32(bytes, cursor + 4, fontRecordSize);
        WriteUInt32(bytes, cursor + 8, 1);
        WriteInt32(bytes, cursor + 12, -14);
        WriteInt32(bytes, cursor + 28, 400);
        bytes[cursor + 35] = 1;
        byte[] faceName = Encoding.Unicode.GetBytes(SystemFonts.DefaultFont.Name);
        faceName.AsSpan(0, Math.Min(faceName.Length, 62)).CopyTo(bytes.AsSpan(cursor + 40, 62));
        cursor += fontRecordSize;

        WriteSelectObject(bytes, cursor, 1);
        cursor += 12;
        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfSetBkMode);
        WriteUInt32(bytes, cursor + 4, 12);
        WriteInt32(bytes, cursor + 8, 1);
        cursor += 12;
        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfSetTextColor);
        WriteUInt32(bytes, cursor + 4, 12);
        WriteUInt32(bytes, cursor + 8, 0x0044_4444);
        cursor += 12;

        for (int index = 0; index < recordCount; index++)
        {
            int x = (index % 16) * 40;
            int y = (index / 16) * 30;
            WriteUInt32(
                bytes,
                cursor,
                (uint)(unicode
                    ? EmfPlusRecordType.EmfExtTextOutW
                    : EmfPlusRecordType.EmfExtTextOutA));
            WriteUInt32(bytes, cursor + 4, checked((uint)textRecordSize));
            WriteUInt32(bytes, cursor + 24, 1);
            WriteSingle(bytes, cursor + 28, 1f);
            WriteSingle(bytes, cursor + 32, 1f);
            WriteInt32(bytes, cursor + 36, x);
            WriteInt32(bytes, cursor + 40, y);
            WriteUInt32(bytes, cursor + 44, 3);
            WriteUInt32(bytes, cursor + 48, 76);
            WriteUInt32(
                bytes,
                cursor + 52,
                glyphIndices ? 0x0000_0010u : pdy ? 0x0000_3000u : 0x0000_1000u);
            int advancesOffset = unicode ? 84 : 80;
            if (!naturalGlyphAdvances)
            {
                WriteUInt32(bytes, cursor + 72, checked((uint)advancesOffset));
            }
            if (glyphIndices)
            {
                WriteUInt16(bytes, cursor + 76, 1);
                WriteUInt16(bytes, cursor + 78, 2);
                WriteUInt16(bytes, cursor + 80, 3);
            }
            else
            {
                bytes[cursor + 76] = (byte)'W';
                bytes[cursor + (unicode ? 78 : 77)] = (byte)'M';
                bytes[cursor + (unicode ? 80 : 78)] = (byte)'F';
            }
            if (naturalGlyphAdvances)
            {
                // The record ends after its padded inline glyph-index buffer.
            }
            else if (pdy)
            {
                WriteUInt32(bytes, cursor + advancesOffset, 10);
                WriteUInt32(bytes, cursor + advancesOffset + 4, 1);
                WriteUInt32(bytes, cursor + advancesOffset + 8, 10);
                WriteUInt32(bytes, cursor + advancesOffset + 12, 2);
                WriteUInt32(bytes, cursor + advancesOffset + 16, 10);
                WriteUInt32(bytes, cursor + advancesOffset + 20, 3);
            }
            else
            {
                WriteUInt32(bytes, cursor + advancesOffset, 10);
                WriteUInt32(bytes, cursor + advancesOffset + 4, 10);
                WriteUInt32(bytes, cursor + advancesOffset + 8, 10);
            }
            cursor += textRecordSize;
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static byte[] CreatePlaybackEmfPolyText(int recordCount)
    {
        const int fontRecordSize = 104;
        const int textRecordSize = 160;
        int totalBytes = checked(
            88 + fontRecordSize + 12 + 12 + 12 + recordCount * textRecordSize + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, checked((uint)recordCount + 6));
        WriteUInt16(bytes, 56, 2);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfExtCreateFontIndirect);
        WriteUInt32(bytes, cursor + 4, fontRecordSize);
        WriteUInt32(bytes, cursor + 8, 1);
        WriteInt32(bytes, cursor + 12, -14);
        WriteInt32(bytes, cursor + 28, 400);
        bytes[cursor + 35] = 1;
        byte[] faceName = Encoding.Unicode.GetBytes(SystemFonts.DefaultFont.Name);
        faceName.AsSpan(0, Math.Min(faceName.Length, 62)).CopyTo(bytes.AsSpan(cursor + 40, 62));
        cursor += fontRecordSize;

        WriteSelectObject(bytes, cursor, 1);
        cursor += 12;
        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfSetBkMode);
        WriteUInt32(bytes, cursor + 4, 12);
        WriteInt32(bytes, cursor + 8, 1);
        cursor += 12;
        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfSetTextColor);
        WriteUInt32(bytes, cursor + 4, 12);
        WriteUInt32(bytes, cursor + 8, 0x0044_4444);
        cursor += 12;

        for (int index = 0; index < recordCount; index++)
        {
            int x = (index % 16) * 40;
            int y = (index / 16) * 30;
            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfPolyTextOutW);
            WriteUInt32(bytes, cursor + 4, textRecordSize);
            WriteUInt32(bytes, cursor + 24, 1);
            WriteSingle(bytes, cursor + 28, 1f);
            WriteSingle(bytes, cursor + 32, 1f);
            WriteUInt32(bytes, cursor + 36, 2);
            WritePolyTextDescriptor(bytes, cursor + 40, x, y, 120, 128);
            WritePolyTextDescriptor(bytes, cursor + 80, x + 18, y + 14, 140, 148);
            WriteUnicodeWmf(bytes, cursor + 120);
            WriteAdvances(bytes, cursor + 128);
            WriteUnicodeWmf(bytes, cursor + 140);
            WriteAdvances(bytes, cursor + 148);
            cursor += textRecordSize;
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static byte[] CreatePlaybackEmfSmallText(int recordCount)
    {
        const int fontRecordSize = 104;
        const int textRecordSize = 40;
        int totalBytes = checked(
            88 + fontRecordSize + 12 + 12 + 12 + recordCount * textRecordSize + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, checked((uint)recordCount + 6));
        WriteUInt16(bytes, 56, 2);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfExtCreateFontIndirect);
        WriteUInt32(bytes, cursor + 4, fontRecordSize);
        WriteUInt32(bytes, cursor + 8, 1);
        WriteInt32(bytes, cursor + 12, -14);
        WriteInt32(bytes, cursor + 28, 400);
        bytes[cursor + 35] = 1;
        byte[] faceName = Encoding.Unicode.GetBytes(SystemFonts.DefaultFont.Name);
        faceName.AsSpan(0, Math.Min(faceName.Length, 62)).CopyTo(bytes.AsSpan(cursor + 40, 62));
        cursor += fontRecordSize;

        WriteSelectObject(bytes, cursor, 1);
        cursor += 12;
        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfSetBkMode);
        WriteUInt32(bytes, cursor + 4, 12);
        WriteInt32(bytes, cursor + 8, 1);
        cursor += 12;
        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfSetTextColor);
        WriteUInt32(bytes, cursor + 4, 12);
        WriteUInt32(bytes, cursor + 8, 0x0044_4444);
        cursor += 12;

        for (int index = 0; index < recordCount; index++)
        {
            int x = (index % 16) * 40;
            int y = (index / 16) * 30;
            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfSmallTextOut);
            WriteUInt32(bytes, cursor + 4, textRecordSize);
            WriteInt32(bytes, cursor + 8, x);
            WriteInt32(bytes, cursor + 12, y);
            WriteUInt32(bytes, cursor + 16, 3);
            WriteUInt32(bytes, cursor + 20, 0x0000_0300);
            WriteUInt32(bytes, cursor + 24, 1);
            WriteSingle(bytes, cursor + 28, 1f);
            WriteSingle(bytes, cursor + 32, 1f);
            bytes[cursor + 36] = (byte)'W';
            bytes[cursor + 37] = (byte)'M';
            bytes[cursor + 38] = (byte)'F';
            cursor += textRecordSize;
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static void WritePolyTextDescriptor(
        byte[] target,
        int offset,
        int x,
        int y,
        uint stringOffset,
        uint advancesOffset)
    {
        WriteInt32(target, offset, x);
        WriteInt32(target, offset + 4, y);
        WriteUInt32(target, offset + 8, 3);
        WriteUInt32(target, offset + 12, stringOffset);
        WriteUInt32(target, offset + 16, 0x0000_1000);
        WriteUInt32(target, offset + 36, advancesOffset);
    }

    private static void WriteUnicodeWmf(byte[] target, int offset)
    {
        WriteUInt16(target, offset, 'W');
        WriteUInt16(target, offset + 2, 'M');
        WriteUInt16(target, offset + 4, 'F');
    }

    private static void WriteAdvances(byte[] target, int offset)
    {
        WriteUInt32(target, offset, 10);
        WriteUInt32(target, offset + 4, 10);
        WriteUInt32(target, offset + 8, 10);
    }

    private static byte[] CreatePlaybackWmfDibImages(
        int recordCount,
        bool bitFields = false,
        uint rasterOperation = 0x00CC_0020)
    {
        int recordWords = bitFields ? 44 : 42;
        int recordBytes = recordWords * 2;
        int declaredWords = checked(9 + recordCount * recordWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 0);
        WriteUInt32(bytes, 34, checked((uint)recordWords));

        int cursor = 40;
        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, checked((uint)recordWords));
            WriteUInt16(bytes, cursor + 4, 0x0F43);
            WriteUInt32(bytes, cursor + 6, rasterOperation);
            WriteInt16(bytes, cursor + 12, 2);
            WriteInt16(bytes, cursor + 14, 2);
            WriteInt16(bytes, cursor + 20, 22);
            WriteInt16(bytes, cursor + 22, 32);
            WriteInt16(bytes, cursor + 24, y);
            WriteInt16(bytes, cursor + 26, x);

            int info = cursor + 28;
            WriteUInt32(bytes, info, 40);
            WriteInt32(bytes, info + 4, 2);
            WriteInt32(bytes, info + 8, -2);
            WriteUInt16(bytes, info + 12, 1);
            WriteUInt16(bytes, info + 14, bitFields ? (ushort)16 : (ushort)32);
            WriteUInt32(bytes, info + 16, bitFields ? 3u : 0u);
            WriteUInt32(bytes, info + 20, bitFields ? 8u : 16u);

            int bits;
            if (bitFields)
            {
                WriteUInt32(bytes, info + 40, 0xF800);
                WriteUInt32(bytes, info + 44, 0x07E0);
                WriteUInt32(bytes, info + 48, 0x001F);
                bits = cursor + 80;
                WriteUInt16(bytes, bits, 0xF800);
                WriteUInt16(bytes, bits + 2, 0x07E0);
                WriteUInt16(bytes, bits + 4, 0x001F);
                WriteUInt16(bytes, bits + 6, 0xFFFF);
            }
            else
            {
                bits = cursor + 68;
                WriteUInt32(bytes, bits, 0x0000_00FF);
                WriteUInt32(bytes, bits + 4, 0x0000_FF00);
                WriteUInt32(bytes, bits + 8, 0x00FF_0000);
                WriteUInt32(bytes, bits + 12, 0x00FF_FFFF);
            }
            cursor += recordBytes;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfRleImages(int recordCount)
    {
        const int recordWords = 44;
        const int recordBytes = recordWords * 2;
        int declaredWords = checked(9 + recordCount * recordWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 0);
        WriteUInt32(bytes, 34, recordWords);

        int cursor = 40;
        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, recordWords);
            WriteUInt16(bytes, cursor + 4, 0x0F43);
            WriteUInt32(bytes, cursor + 6, 0x00CC_0020);
            WriteInt16(bytes, cursor + 12, 2);
            WriteInt16(bytes, cursor + 14, 2);
            WriteInt16(bytes, cursor + 20, 22);
            WriteInt16(bytes, cursor + 22, 32);
            WriteInt16(bytes, cursor + 24, y);
            WriteInt16(bytes, cursor + 26, x);

            int info = cursor + 28;
            WriteUInt32(bytes, info, 40);
            WriteInt32(bytes, info + 4, 2);
            WriteInt32(bytes, info + 8, 2);
            WriteUInt16(bytes, info + 12, 1);
            WriteUInt16(bytes, info + 14, 8);
            WriteUInt32(bytes, info + 16, 1);
            WriteUInt32(bytes, info + 20, 8);
            WriteUInt32(bytes, info + 32, 3);
            WriteUInt32(bytes, info + 44, 0x0000_00FF);
            WriteUInt32(bytes, info + 48, 0x00FF_0000);

            int bits = cursor + 80;
            bytes[bits] = 2;
            bytes[bits + 1] = 1;
            bytes[bits + 2] = 0;
            bytes[bits + 3] = 0;
            bytes[bits + 4] = 2;
            bytes[bits + 5] = 2;
            bytes[bits + 6] = 0;
            bytes[bits + 7] = 1;
            cursor += recordBytes;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfCmykImages(int recordCount)
    {
        const int recordWords = 42;
        const int recordBytes = recordWords * 2;
        int declaredWords = checked(9 + recordCount * recordWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 0);
        WriteUInt32(bytes, 34, recordWords);

        byte[] pixels =
        [
            0, 255, 255, 0,
            255, 0, 255, 0,
            255, 255, 0, 0,
            0, 0, 0, 0
        ];
        int cursor = 40;
        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, recordWords);
            WriteUInt16(bytes, cursor + 4, 0x0F43);
            WriteUInt32(bytes, cursor + 6, 0x00CC_0020);
            WriteInt16(bytes, cursor + 12, 2);
            WriteInt16(bytes, cursor + 14, 2);
            WriteInt16(bytes, cursor + 20, 22);
            WriteInt16(bytes, cursor + 22, 32);
            WriteInt16(bytes, cursor + 24, y);
            WriteInt16(bytes, cursor + 26, x);

            int info = cursor + 28;
            WriteUInt32(bytes, info, 40);
            WriteInt32(bytes, info + 4, 2);
            WriteInt32(bytes, info + 8, -2);
            WriteUInt16(bytes, info + 12, 1);
            WriteUInt16(bytes, info + 14, 32);
            WriteUInt32(bytes, info + 16, 11);
            WriteUInt32(bytes, info + 20, 16);
            int bits = info + 40;
            pixels.CopyTo(bytes, bits);
            cursor += recordBytes;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfLogicalPaletteImages(int recordCount)
    {
        const int paletteRecordWords = 13;
        const int selectRecordWords = 4;
        const int imageRecordWords = 38;
        int declaredWords = checked(
            9 + paletteRecordWords + selectRecordWords + recordCount * imageRecordWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 1);
        WriteUInt32(bytes, 34, imageRecordWords);

        int cursor = 40;
        WriteUInt32(bytes, cursor, paletteRecordWords);
        WriteUInt16(bytes, cursor + 4, 0x00F7);
        WriteUInt16(bytes, cursor + 6, 0x0300);
        WriteUInt16(bytes, cursor + 8, 4);
        Color[] palette = [Color.Red, Color.Lime, Color.Blue, Color.White];
        for (int index = 0; index < palette.Length; index++)
        {
            Color color = palette[index];
            int entry = cursor + 10 + index * 4;
            bytes[entry] = color.R;
            bytes[entry + 1] = color.G;
            bytes[entry + 2] = color.B;
        }
        cursor += paletteRecordWords * 2;

        WriteUInt32(bytes, cursor, selectRecordWords);
        WriteUInt16(bytes, cursor + 4, 0x0234);
        WriteUInt16(bytes, cursor + 6, 0);
        cursor += selectRecordWords * 2;

        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, imageRecordWords);
            WriteUInt16(bytes, cursor + 4, 0x0F43);
            WriteUInt32(bytes, cursor + 6, 0x00CC_0020);
            WriteUInt16(bytes, cursor + 10, 2);
            WriteInt16(bytes, cursor + 12, 2);
            WriteInt16(bytes, cursor + 14, 2);
            WriteInt16(bytes, cursor + 20, 22);
            WriteInt16(bytes, cursor + 22, 32);
            WriteInt16(bytes, cursor + 24, y);
            WriteInt16(bytes, cursor + 26, x);

            int info = cursor + 28;
            WriteUInt32(bytes, info, 40);
            WriteInt32(bytes, info + 4, 2);
            WriteInt32(bytes, info + 8, -2);
            WriteUInt16(bytes, info + 12, 1);
            WriteUInt16(bytes, info + 14, 8);
            WriteUInt32(bytes, info + 20, 8);
            int bits = info + 40;
            bytes[bits] = 0;
            bytes[bits + 1] = 1;
            bytes[bits + 4] = 2;
            bytes[bits + 5] = 3;
            cursor += imageRecordWords * 2;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfEncodedImages(int recordCount)
    {
        byte[] encodedImage;
        using (var source = new Bitmap(2, 2))
        {
            source.SetPixel(0, 0, Color.Red);
            source.SetPixel(1, 0, Color.Lime);
            source.SetPixel(0, 1, Color.Blue);
            source.SetPixel(1, 1, Color.White);
            using var stream = new MemoryStream();
            source.Save(stream, ImageFormat.Png);
            encodedImage = stream.ToArray();
        }

        int recordBytes = checked((6 + 22 + 40 + encodedImage.Length + 1) & ~1);
        int recordWords = recordBytes / 2;
        int declaredWords = checked(9 + recordCount * recordWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 0);
        WriteUInt32(bytes, 34, checked((uint)recordWords));

        int cursor = 40;
        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, checked((uint)recordWords));
            WriteUInt16(bytes, cursor + 4, 0x0F43);
            WriteUInt32(bytes, cursor + 6, 0x00CC_0020);
            WriteInt16(bytes, cursor + 12, 2);
            WriteInt16(bytes, cursor + 14, 2);
            WriteInt16(bytes, cursor + 20, 22);
            WriteInt16(bytes, cursor + 22, 32);
            WriteInt16(bytes, cursor + 24, y);
            WriteInt16(bytes, cursor + 26, x);

            int info = cursor + 28;
            WriteUInt32(bytes, info, 40);
            WriteInt32(bytes, info + 4, 2);
            WriteInt32(bytes, info + 8, 2);
            WriteUInt16(bytes, info + 12, 1);
            WriteUInt16(bytes, info + 14, 0);
            WriteUInt32(bytes, info + 16, 5);
            WriteUInt32(bytes, info + 20, checked((uint)encodedImage.Length));
            encodedImage.CopyTo(bytes, info + 40);
            cursor += recordBytes;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmf(int polygonCount)
    {
        int declaredWords = checked(9 + 7 + 8 + 4 + 4 + polygonCount * 12 + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 2);
        WriteUInt32(bytes, 34, 12);

        int cursor = 40;
        WriteUInt32(bytes, cursor, 7);
        WriteUInt16(bytes, cursor + 4, 0x02FC);
        WriteUInt32(bytes, cursor + 8, 0x0044_4444);
        cursor += 14;

        WriteUInt32(bytes, cursor, 8);
        WriteUInt16(bytes, cursor + 4, 0x02FA);
        WriteUInt16(bytes, cursor + 6, 5);
        WriteInt16(bytes, cursor + 8, 1);
        cursor += 16;

        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
        cursor += 8;
        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 1);
        cursor += 8;

        for (int index = 0; index < polygonCount; index++)
        {
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, 12);
            WriteUInt16(bytes, cursor + 4, 0x0324);
            WriteInt16(bytes, cursor + 6, 4);
            WriteWmfPoint(bytes, cursor + 8, x, y);
            WriteWmfPoint(bytes, cursor + 12, checked((short)(x + 32)), y);
            WriteWmfPoint(bytes, cursor + 16, checked((short)(x + 32)), checked((short)(y + 22)));
            WriteWmfPoint(bytes, cursor + 20, x, checked((short)(y + 22)));
            cursor += 24;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfPolyPolygons(int recordCount)
    {
        const int recordWords = 22;
        int declaredWords = checked(9 + 7 + 8 + 4 + 4 + recordCount * recordWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 2);
        WriteUInt32(bytes, 34, recordWords);

        int cursor = 40;
        WriteUInt32(bytes, cursor, 7);
        WriteUInt16(bytes, cursor + 4, 0x02FC);
        WriteUInt32(bytes, cursor + 8, 0x0044_4444);
        cursor += 14;

        WriteUInt32(bytes, cursor, 8);
        WriteUInt16(bytes, cursor + 4, 0x02FA);
        WriteUInt16(bytes, cursor + 6, 0);
        WriteInt16(bytes, cursor + 8, 1);
        cursor += 16;

        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
        cursor += 8;
        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 1);
        cursor += 8;

        for (int index = 0; index < recordCount; index++)
        {
            short left = checked((short)((index % 16) * 40));
            short top = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, recordWords);
            WriteUInt16(bytes, cursor + 4, 0x0538);
            WriteUInt16(bytes, cursor + 6, 2);
            WriteUInt16(bytes, cursor + 8, 4);
            WriteUInt16(bytes, cursor + 10, 4);
            WriteWmfPoint(bytes, cursor + 12, left, top);
            WriteWmfPoint(bytes, cursor + 16, checked((short)(left + 14)), top);
            WriteWmfPoint(bytes, cursor + 20, checked((short)(left + 14)), checked((short)(top + 22)));
            WriteWmfPoint(bytes, cursor + 24, left, checked((short)(top + 22)));
            WriteWmfPoint(bytes, cursor + 28, checked((short)(left + 18)), top);
            WriteWmfPoint(bytes, cursor + 32, checked((short)(left + 32)), top);
            WriteWmfPoint(bytes, cursor + 36, checked((short)(left + 32)), checked((short)(top + 22)));
            WriteWmfPoint(bytes, cursor + 40, checked((short)(left + 18)), checked((short)(top + 22)));
            cursor += recordWords * 2;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfBoxes(
        int recordCount,
        ushort function,
        bool includeClipState = false)
    {
        bool roundRectangle = function == 0x061C;
        int shapeWords = roundRectangle ? 9 : 7;
        int clipWords = includeClipState ? 21 : 0;
        int declaredWords = checked(9 + 7 + 8 + 4 + 4 + clipWords + recordCount * shapeWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 2);
        WriteUInt32(bytes, 34, roundRectangle ? 9u : 8u);

        int cursor = 40;
        WriteUInt32(bytes, cursor, 7);
        WriteUInt16(bytes, cursor + 4, 0x02FC);
        WriteUInt32(bytes, cursor + 8, 0x0044_4444);
        cursor += 14;

        WriteUInt32(bytes, cursor, 8);
        WriteUInt16(bytes, cursor + 4, 0x02FA);
        WriteUInt16(bytes, cursor + 6, 0);
        WriteInt16(bytes, cursor + 8, 1);
        cursor += 16;

        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
        cursor += 8;
        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 1);
        cursor += 8;

        if (includeClipState)
        {
            WriteWmfBoxRecord(bytes, cursor, 0x0416, left: 0, top: 0, right: 640, bottom: 480);
            cursor += 14;
            WriteUInt32(bytes, cursor, 3);
            WriteUInt16(bytes, cursor + 4, 0x001E);
            cursor += 6;
            WriteWmfBoxRecord(bytes, cursor, 0x0415, left: 280, top: 180, right: 360, bottom: 300);
            cursor += 14;
        }

        for (int index = 0; index < recordCount; index++)
        {
            if (includeClipState && index == recordCount / 2)
            {
                WriteUInt32(bytes, cursor, 4);
                WriteUInt16(bytes, cursor + 4, 0x0127);
                WriteInt16(bytes, cursor + 6, -1);
                cursor += 8;
            }

            short left = checked((short)((index % 16) * 40));
            short top = checked((short)((index / 16) * 30));
            if (roundRectangle)
            {
                WriteWmfRoundRectangleRecord(
                    bytes,
                    cursor,
                    left,
                    top,
                    checked((short)(left + 32)),
                    checked((short)(top + 22)),
                    width: 8,
                    height: 8);
                cursor += 18;
            }
            else
            {
                WriteWmfBoxRecord(
                    bytes,
                    cursor,
                    function,
                    left,
                    top,
                    checked((short)(left + 32)),
                    checked((short)(top + 22)));
                cursor += 14;
            }
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfFilledArcs(int recordCount, ushort function)
    {
        int declaredWords = checked(9 + 7 + 8 + 4 + 4 + recordCount * 11 + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 2);
        WriteUInt32(bytes, 34, 11);

        int cursor = 40;
        WriteUInt32(bytes, cursor, 7);
        WriteUInt16(bytes, cursor + 4, 0x02FC);
        WriteUInt32(bytes, cursor + 8, 0x0044_4444);
        cursor += 14;

        WriteUInt32(bytes, cursor, 8);
        WriteUInt16(bytes, cursor + 4, 0x02FA);
        WriteUInt16(bytes, cursor + 6, 0);
        WriteInt16(bytes, cursor + 8, 1);
        cursor += 16;

        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
        cursor += 8;
        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 1);
        cursor += 8;

        for (int index = 0; index < recordCount; index++)
        {
            short left = checked((short)((index % 16) * 40));
            short top = checked((short)((index / 16) * 30));
            short right = checked((short)(left + 32));
            short bottom = checked((short)(top + 22));
            WriteWmfFilledArcRecord(bytes, cursor, function, left, top, right, bottom);
            cursor += 22;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfLineOrPixels(int recordCount, bool setPixels)
    {
        int setupWords = setPixels ? 0 : 8 + 4 + 5;
        int recordWords = setPixels ? 7 : 5;
        int declaredWords = checked(9 + setupWords + recordCount * recordWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, setPixels ? (ushort)0 : (ushort)1);
        WriteUInt32(bytes, 34, setPixels ? 7u : 8u);

        int cursor = 40;
        if (!setPixels)
        {
            WriteUInt32(bytes, cursor, 8);
            WriteUInt16(bytes, cursor + 4, 0x02FA);
            WriteUInt16(bytes, cursor + 6, 0);
            WriteInt16(bytes, cursor + 8, 1);
            cursor += 16;

            WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
            cursor += 8;
            WriteUInt32(bytes, cursor, 5);
            WriteUInt16(bytes, cursor + 4, 0x0214);
            WriteInt16(bytes, cursor + 6, 0);
            WriteInt16(bytes, cursor + 8, 0);
            cursor += 10;
        }

        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index * 37) % 640));
            short y = checked((short)((index * 29) % 480));
            if (setPixels)
            {
                WriteUInt32(bytes, cursor, 7);
                WriteUInt16(bytes, cursor + 4, 0x041F);
                WriteUInt32(bytes, cursor + 6, 0x00FF_00FF);
                WriteInt16(bytes, cursor + 10, y);
                WriteInt16(bytes, cursor + 12, x);
                cursor += 14;
            }
            else
            {
                WriteUInt32(bytes, cursor, 5);
                WriteUInt16(bytes, cursor + 4, 0x0213);
                WriteInt16(bytes, cursor + 6, y);
                WriteInt16(bytes, cursor + 8, x);
                cursor += 10;
            }
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfMappedPixels(int recordCount)
    {
        const int setupWords = 24;
        const int cycleWords = 55;
        int declaredWords = checked(9 + setupWords + recordCount * cycleWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 0);
        WriteUInt32(bytes, 34, 7);

        int cursor = 40;
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x0103, 8);
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x020C, 480, 640);
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x020B, 0, 0);
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x020E, 480, 640);
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x020D, 0, 0);

        for (int index = 0; index < recordCount; index++)
        {
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x020F, 1, 1);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x020F, -1, -1);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x0211, 1, 1);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x0211, -1, -1);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x0410, 1, 2, 1, 2);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x0410, 2, 1, 2, 1);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x0412, 1, 2, 1, 2);
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x0412, 2, 1, 2, 1);

            short x = checked((short)((index * 37) % 640));
            short y = checked((short)((index * 29) % 480));
            WriteUInt32(bytes, cursor, 7);
            WriteUInt16(bytes, cursor + 4, 0x041F);
            WriteUInt32(bytes, cursor + 6, 0x00FF_FF00);
            WriteInt16(bytes, cursor + 10, y);
            WriteInt16(bytes, cursor + 12, x);
            cursor += 14;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfPatBlts(
        int recordCount,
        bool includeOffsetClipState = false)
    {
        const int recordWords = 9;
        int clipSetupWords = includeOffsetClipState ? 7 : 0;
        int perRecordWords = includeOffsetClipState ? 19 : recordWords;
        int declaredWords = checked(9 + 7 + 4 + clipSetupWords + recordCount * perRecordWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 1);
        WriteUInt32(bytes, 34, recordWords);

        int cursor = 40;
        WriteUInt32(bytes, cursor, 7);
        WriteUInt16(bytes, cursor + 4, 0x02FC);
        WriteUInt32(bytes, cursor + 8, 0x0044_4444);
        cursor += 14;
        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
        cursor += 8;
        if (includeOffsetClipState)
        {
            WriteWmfBoxRecord(bytes, cursor, 0x0416, left: 0, top: 0, right: 640, bottom: 480);
            cursor += 14;
        }

        for (int index = 0; index < recordCount; index++)
        {
            if (includeOffsetClipState)
            {
                cursor += WriteWmfWordsRecord(bytes, cursor, 0x0220, 1, 1);
            }
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, recordWords);
            WriteUInt16(bytes, cursor + 4, 0x061D);
            WriteUInt32(bytes, cursor + 6, 0x00F0_0021);
            WriteInt16(bytes, cursor + 10, 22);
            WriteInt16(bytes, cursor + 12, 32);
            WriteInt16(bytes, cursor + 14, y);
            WriteInt16(bytes, cursor + 16, x);
            cursor += recordWords * 2;
            if (includeOffsetClipState)
            {
                cursor += WriteWmfWordsRecord(bytes, cursor, 0x0220, -1, -1);
            }
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfSourceIndependentBitmapRecords(
        int recordCount,
        uint rasterOperation = 0x00F0_0021)
    {
        const int recordWords = 12;
        int declaredWords = checked(9 + 7 + 4 + recordCount * recordWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 1);
        WriteUInt32(bytes, 34, recordWords);

        int cursor = 40;
        WriteUInt32(bytes, cursor, 7);
        WriteUInt16(bytes, cursor + 4, 0x02FC);
        WriteUInt32(bytes, cursor + 8, 0x0044_4444);
        cursor += 14;
        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
        cursor += 8;

        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, recordWords);
            WriteUInt16(bytes, cursor + 4, 0x0922);
            WriteUInt32(bytes, cursor + 6, rasterOperation);
            WriteInt16(bytes, cursor + 16, 22);
            WriteInt16(bytes, cursor + 18, 32);
            WriteInt16(bytes, cursor + 20, y);
            WriteInt16(bytes, cursor + 22, x);
            cursor += recordWords * 2;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfBitmap16AdapterRecords(int recordCount)
    {
        const int bitmapWidth = 8;
        const int bitmapHeight = 8;
        const int bitmapStride = bitmapWidth * 4;
        const int bitmapByteCount = bitmapStride * bitmapHeight;
        const int recordWords = (6 + 16 + 10 + bitmapByteCount) / 2;
        int declaredWords = checked(9 + recordCount * recordWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 0);
        WriteUInt32(bytes, 34, recordWords);

        int cursor = 40;
        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, recordWords);
            WriteUInt16(bytes, cursor + 4, 0x0922);
            WriteUInt32(bytes, cursor + 6, 0x00CC_0020);
            WriteInt16(bytes, cursor + 14, bitmapHeight);
            WriteInt16(bytes, cursor + 16, bitmapWidth);
            WriteInt16(bytes, cursor + 18, y);
            WriteInt16(bytes, cursor + 20, x);
            WriteInt16(bytes, cursor + 24, bitmapWidth);
            WriteInt16(bytes, cursor + 26, bitmapHeight);
            WriteInt16(bytes, cursor + 28, bitmapStride);
            bytes[cursor + 30] = 1;
            bytes[cursor + 31] = 32;
            for (int pixel = 0; pixel < bitmapWidth * bitmapHeight; pixel++)
            {
                int pixelOffset = cursor + 32 + pixel * 4;
                bytes[pixelOffset] = (byte)(index + pixel);
                bytes[pixelOffset + 1] = (byte)(index * 3 + pixel);
                bytes[pixelOffset + 2] = (byte)(index * 7 + pixel);
                bytes[pixelOffset + 3] = byte.MaxValue;
            }
            cursor += recordWords * 2;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfText(
        int recordCount,
        short escapement = 0,
        short characterExtra = 0,
        short breakExtra = 0,
        short breakCount = 0)
    {
        const int fontWords = 28;
        const int textWords = 8;
        int characterExtraWords = characterExtra == 0 ? 0 : 4;
        int justificationWords = breakExtra == 0 ? 0 : 5;
        int declaredWords = checked(
            9 + fontWords + 4 + 4 + characterExtraWords + justificationWords + 5 +
            recordCount * textWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 1);
        WriteUInt32(bytes, 34, fontWords);

        int cursor = 40;
        WriteUInt32(bytes, cursor, fontWords);
        WriteUInt16(bytes, cursor + 4, 0x02FB);
        WriteInt16(bytes, cursor + 6, -14);
        WriteInt16(bytes, cursor + 10, escapement);
        WriteInt16(bytes, cursor + 12, escapement);
        WriteInt16(bytes, cursor + 14, 400);
        bytes[cursor + 19] = 1;
        cursor += fontWords * 2;

        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
        cursor += 8;
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x0102, 1);
        if (characterExtra != 0)
        {
            cursor += WriteWmfWordsRecord(bytes, cursor, 0x0108, characterExtra);
        }
        if (breakExtra != 0)
        {
            cursor += WriteWmfWordsRecord(
                bytes,
                cursor,
                0x020A,
                breakCount,
                breakExtra);
        }

        WriteUInt32(bytes, cursor, 5);
        WriteUInt16(bytes, cursor + 4, 0x0209);
        WriteUInt32(bytes, cursor + 6, 0x0044_4444);
        cursor += 10;

        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, textWords);
            WriteUInt16(bytes, cursor + 4, 0x0521);
            WriteInt16(bytes, cursor + 6, 3);
            bytes[cursor + 8] = (byte)'W';
            bytes[cursor + 9] = breakExtra == 0 ? (byte)'M' : (byte)' ';
            bytes[cursor + 10] = (byte)'F';
            WriteInt16(bytes, cursor + 12, y);
            WriteInt16(bytes, cursor + 14, x);
            cursor += textWords * 2;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static byte[] CreatePlaybackWmfExtendedText(int recordCount, short escapement = 0)
    {
        const int fontWords = 28;
        const int recordWords = 16;
        int declaredWords = checked(9 + fontWords + 4 + 4 + 5 + 5 + recordCount * recordWords + 3);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 10, 640);
        WriteInt16(bytes, 12, 480);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 1);
        WriteUInt32(bytes, 34, fontWords);

        int cursor = 40;
        WriteUInt32(bytes, cursor, fontWords);
        WriteUInt16(bytes, cursor + 4, 0x02FB);
        WriteInt16(bytes, cursor + 6, -14);
        WriteInt16(bytes, cursor + 10, escapement);
        WriteInt16(bytes, cursor + 12, escapement);
        WriteInt16(bytes, cursor + 14, 400);
        bytes[cursor + 19] = 1;
        cursor += fontWords * 2;
        WriteWmfObjectIndexRecord(bytes, cursor, 0x012D, 0);
        cursor += 8;
        cursor += WriteWmfWordsRecord(bytes, cursor, 0x0102, 1);

        WriteUInt32(bytes, cursor, 5);
        WriteUInt16(bytes, cursor + 4, 0x0201);
        WriteUInt32(bytes, cursor + 6, 0x00FF_FFFF);
        cursor += 10;
        WriteUInt32(bytes, cursor, 5);
        WriteUInt16(bytes, cursor + 4, 0x0209);
        WriteUInt32(bytes, cursor + 6, 0x0044_4444);
        cursor += 10;

        for (int index = 0; index < recordCount; index++)
        {
            short x = checked((short)((index % 16) * 40));
            short y = checked((short)((index / 16) * 30));
            WriteUInt32(bytes, cursor, recordWords);
            WriteUInt16(bytes, cursor + 4, 0x0A32);
            WriteInt16(bytes, cursor + 6, y);
            WriteInt16(bytes, cursor + 8, x);
            WriteInt16(bytes, cursor + 10, 3);
            WriteUInt16(bytes, cursor + 12, 0x0006);
            WriteInt16(bytes, cursor + 14, x);
            WriteInt16(bytes, cursor + 16, y);
            WriteInt16(bytes, cursor + 18, checked((short)(x + 32)));
            WriteInt16(bytes, cursor + 20, checked((short)(y + 22)));
            bytes[cursor + 22] = (byte)'W';
            bytes[cursor + 23] = (byte)'M';
            bytes[cursor + 24] = (byte)'F';
            WriteInt16(bytes, cursor + 26, 10);
            WriteInt16(bytes, cursor + 28, 10);
            WriteInt16(bytes, cursor + 30, 10);
            cursor += recordWords * 2;
        }

        WriteUInt32(bytes, cursor, 3);
        return bytes;
    }

    private static void WriteWmfBoxRecord(
        byte[] target,
        int offset,
        ushort function,
        short left,
        short top,
        short right,
        short bottom)
    {
        WriteUInt32(target, offset, 7);
        WriteUInt16(target, offset + 4, function);
        WriteInt16(target, offset + 6, bottom);
        WriteInt16(target, offset + 8, right);
        WriteInt16(target, offset + 10, top);
        WriteInt16(target, offset + 12, left);
    }

    private static void WriteWmfRoundRectangleRecord(
        byte[] target,
        int offset,
        short left,
        short top,
        short right,
        short bottom,
        short width,
        short height)
    {
        WriteUInt32(target, offset, 9);
        WriteUInt16(target, offset + 4, 0x061C);
        WriteInt16(target, offset + 6, height);
        WriteInt16(target, offset + 8, width);
        WriteInt16(target, offset + 10, bottom);
        WriteInt16(target, offset + 12, right);
        WriteInt16(target, offset + 14, top);
        WriteInt16(target, offset + 16, left);
    }

    private static void WriteWmfFilledArcRecord(
        byte[] target,
        int offset,
        ushort function,
        short left,
        short top,
        short right,
        short bottom)
    {
        WriteUInt32(target, offset, 11);
        WriteUInt16(target, offset + 4, function);
        WriteInt16(target, offset + 6, top);
        WriteInt16(target, offset + 8, checked((short)(left + (right - left) / 2)));
        WriteInt16(target, offset + 10, checked((short)(top + (bottom - top) / 2)));
        WriteInt16(target, offset + 12, right);
        WriteInt16(target, offset + 14, bottom);
        WriteInt16(target, offset + 16, right);
        WriteInt16(target, offset + 18, top);
        WriteInt16(target, offset + 20, left);
    }

    private static void WriteWmfObjectIndexRecord(
        byte[] target,
        int offset,
        ushort function,
        ushort index)
    {
        WriteUInt32(target, offset, 4);
        WriteUInt16(target, offset + 4, function);
        WriteUInt16(target, offset + 6, index);
    }

    private static int WriteWmfWordsRecord(
        byte[] target,
        int offset,
        ushort function,
        params short[] values)
    {
        int byteCount = checked(6 + values.Length * 2);
        WriteUInt32(target, offset, checked((uint)(byteCount / 2)));
        WriteUInt16(target, offset + 4, function);
        for (int index = 0; index < values.Length; index++)
        {
            WriteInt16(target, offset + 6 + index * 2, values[index]);
        }
        return byteCount;
    }

    private static void WriteWmfPoint(byte[] target, int offset, short x, short y)
    {
        WriteInt16(target, offset, x);
        WriteInt16(target, offset + 2, y);
    }

    private static void WriteSelectObject(byte[] target, int offset, uint index)
    {
        WriteUInt32(target, offset, (uint)EmfPlusRecordType.EmfSelectObject);
        WriteUInt32(target, offset + 4, 12);
        WriteUInt32(target, offset + 8, index);
    }

    private static void WriteRectangleRecord(
        byte[] target,
        ref int offset,
        EmfPlusRecordType type,
        int left,
        int top,
        int right,
        int bottom)
    {
        WriteUInt32(target, offset, (uint)type);
        WriteUInt32(target, offset + 4, 24);
        WriteInt32(target, offset + 8, left);
        WriteInt32(target, offset + 12, top);
        WriteInt32(target, offset + 16, right);
        WriteInt32(target, offset + 20, bottom);
        offset += 24;
    }

    private static void WriteUInt16(byte[] target, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), value);

    private static void WriteInt16(byte[] target, int offset, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(target.AsSpan(offset, 2), value);

    private static void WriteInt32(byte[] target, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset, 4), value);

    private static void WriteSingle(byte[] target, int offset, float value) =>
        WriteInt32(target, offset, BitConverter.SingleToInt32Bits(value));

    private sealed class PassthroughWmfBitmap16DecodeService : IWmfBitmap16DecodeService
    {
        public void Decode(
            in WmfBitmap16Info bitmap,
            ReadOnlySpan<byte> bits,
            WmfBitmap16DecodeDestination destination)
            => destination.SetRgba(bits);
    }
}
