using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
public static class ProGpuWpfImageBrushOracle
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected capture and output directories");
        Run(args[0], args[1]);
    }
    public static void Run(string captures, string output)
    {
        Directory.CreateDirectory(output);
        for (int c = 0; c < 8; ++c)
        {
            byte[] source = {0,0,255,255,255,0,0,255};
            BitmapSource bitmap = BitmapSource.Create(2, 1, c == 3 ? 6 : 96,
                c == 3 ? 12 : 96, PixelFormats.Bgra32, null, source, 8);
            ImageBrush brush = new ImageBrush(bitmap);
            brush.TileMode = TileMode.None;
            brush.Stretch = c == 1 || c == 4 ? Stretch.Uniform :
                c == 2 ? Stretch.UniformToFill : c == 3 ? Stretch.None : Stretch.Fill;
            brush.AlignmentX = AlignmentX.Center;
            brush.AlignmentY = AlignmentY.Center;
            brush.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
            brush.Viewport = new Rect(0, 0, 1, 1);
            brush.ViewboxUnits = c == 4 ? BrushMappingMode.Absolute : BrushMappingMode.RelativeToBoundingBox;
            brush.Viewbox = c == 4 ? new Rect(1,0,1,2) : new Rect(0,0,1,1);
            if (c == 5) brush.Transform = new MatrixTransform(0,1,-1,0,64,0);
            if (c == 6) brush.RelativeTransform = new MatrixTransform(.5,0,0,.5,.25,.25);
            if (c == 7) brush.Opacity = .5;
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);
            DrawingVisual visual = new DrawingVisual();
            RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Linear);
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0,0,64,64));
                dc.DrawRectangle(brush, null, new Rect(8,8,48,48));
            }
            RenderTargetBitmap target = new RenderTargetBitmap(64,64,96,96,PixelFormats.Pbgra32);
            // Keep the drawing's render options on a normal retained child.
            ContainerVisual root = new ContainerVisual();
            root.Children.Add(visual);
            target.Render(root);
            byte[] native = new byte[64*64*4];
            target.CopyPixels(native, 256, 0);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using (Stream file = File.Create(Path.Combine(output, "image-brush-" + c + ".png"))) encoder.Save(file);
            byte[] ppm = File.ReadAllBytes(Path.Combine(captures, "image-brush-linear-" + c + ".ppm"));
            byte[] header = Encoding.ASCII.GetBytes("P6\n64 64\n255\n");
            if (ppm.Length != header.Length + 64*64*3) throw new Exception("Invalid capture size");
            for (int i = 0; i < header.Length; ++i)
                if (ppm[i] != header[i]) throw new Exception("Invalid capture header");
            for (int y = 0; y < 64; ++y) for (int x = 0; x < 64; ++x)
            {
                int n = (y*64+x)*4, p = header.Length+(y*64+x)*3;
                if (native[n+1]!=0 || native[n+3]!=255)
                    throw new Exception("Native WPF oracle mismatch case="+c+" pixel="+x+","+y+
                        " actual BGRA="+native[n]+","+native[n+1]+","+native[n+2]+","+native[n+3]);
                if (Math.Abs(native[n+2]-ppm[p])>1 || Math.Abs(native[n+1]-ppm[p+1])>1 || Math.Abs(native[n]-ppm[p+2])>1)
                    throw new Exception("ProGPU/native WPF mismatch case="+c+" pixel="+x+","+y);
            }
            Console.WriteLine("ImageBrush native WPF/ProGPU comparison passed: case="+c+" pixels=4096 tolerance=1");
        }
        File.WriteAllText(Path.Combine(output,"result.txt"), "PASS: 8 cases, 32768 pixels; RGB tolerance=1; native alpha=255\n"+
            typeof(ImageBrush).Assembly.FullName+"\n");
    }
}
