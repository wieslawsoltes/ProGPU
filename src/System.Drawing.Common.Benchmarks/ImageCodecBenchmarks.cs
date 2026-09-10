using BenchmarkDotNet.Attributes;
using System.Drawing.Imaging;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class ImageCodecBenchmarks
{
    private Bitmap _bitmap = null!;
    private ImageCodecInfo _jpeg = null!;
    private EncoderParameters _parameters = null!;
    private MemoryStream _stream = null!;

    [GlobalSetup]
    public void CreateBitmap()
    {
        const int width = 256;
        const int height = 256;
        _bitmap = new Bitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                _bitmap.SetPixel(x, y, Color.FromArgb(255, x, y, (x + y) / 2));
            }
        }

        _jpeg = ImageCodecInfo.GetImageEncoders().Single(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        _parameters = new EncoderParameters(1)
        {
            Param = [new EncoderParameter(Encoder.Quality, 82L)]
        };
        _stream = new MemoryStream(capacity: width * height * 4);
        EncodeJpegToReusableStream();
    }

    [Benchmark]
    public long EncodeJpegToReusableStream()
    {
        _stream.Position = 0;
        _stream.SetLength(0);
        _bitmap.Save(_stream, _jpeg, _parameters);
        return _stream.Length;
    }

    [GlobalCleanup]
    public void DisposeBitmap()
    {
        _parameters.Dispose();
        _stream.Dispose();
        _bitmap.Dispose();
    }
}
