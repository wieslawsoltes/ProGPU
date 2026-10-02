using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

internal static partial class Program
{
    private static void CaptureShaderLocalFrames(string directory, string commit, bool unavailable, Stopwatch timer)
    {
        double started = timer.Elapsed.TotalMilliseconds;
        int arithmeticControls = ShaderLocalCaptureOracle.VerifyArithmeticControls();
        var retained = new LocalCaptureVisualState();
        var observations = new List<object>();
        var failures = new List<string>();
        byte[]? before = null, expanded = null;
        OriginalLocalCaptureEffect? mutationOwner = null;
        int mutationChecks = 0, baselineCount = 0;
        foreach (LocalCaptureCase input in ShaderLocalCaptureOracle.Cases())
        {
            CheckPaddingDeadline(timer);
            retained.Prepare(input, withEffect:true);
            var actualBounds = VisualTreeHelper.GetContentBounds(retained.Source);
            var expectedBounds = new Rect(ShaderLocalCaptureOracle.ContentX, ShaderLocalCaptureOracle.ContentY,
                ShaderLocalCaptureOracle.ContentWidth, ShaderLocalCaptureOracle.ContentHeight);
            if (actualBounds != expectedBounds || VisualTreeHelper.GetDescendantBounds(retained.Source) != expectedBounds)
                throw new InvalidOperationException("Original fractional source inner bounds differ from the authored drawing.");
            if (input.Name == "local-generation-zero") mutationOwner = retained.Effect;
            if (input.Name is "local-generation-expand" or "local-generation-reset")
            {
                if (!ReferenceEquals(mutationOwner, retained.Effect))
                    throw new InvalidOperationException("Original local-frame mutation replaced its effect owner.");
                ++mutationChecks;
            }
            var description = new
            {
                input.Name, Output = input.Output.ToString(), SourceDpi = input.Dpi,
                PixelWidth = ShaderLocalCaptureOracle.Width, PixelHeight = ShaderLocalCaptureOracle.Height,
                OriginalContentBounds = actualBounds, OriginalDescendantBounds = VisualTreeHelper.GetDescendantBounds(retained.Source),
                OriginalPadding = new[] { input.Top,input.Bottom,input.Left,input.Right }, PaddingOrder = "Top,Bottom,Left,Right",
                OriginalPaddingBits = retained.Effect.PaddingBits(), ShaderWords = LocalCaptureWords(input), input.SquareUv,
                ShaderRenderMode = "SoftwareOnly", ShaderSampling = "NearestNeighbor", SamplerRegister = 0,
                DerivativeRegister = input.Output == PaddingOutput.Derivatives ? 0 : -1,
                Constant = new[] { .25,.5,.75,1 }, OriginalTransformHistory = retained.TransformHistory(),
                TransformOrder = "source * inner * placement * original RTB DPI root",
                input.History, AuthoredScaleSpaceAllocation = input.Allocation,
                AuthoredFinalFrame = new { X = input.FinalLeft,Y = input.FinalTop,input.Allocation.Width,input.Allocation.Height },
                AuthoredAliasedOutputClip = ShaderLocalCaptureOracle.OutputClip(input),
                FinalSourceClip = retained.Clip.Clip!.Bounds, input.NativeCandidate,
                NativeExclusion = !input.IntegralPlacement ? "fractional-final-device-placement"
                    : input.History == LocalCaptureHistory.SeparatelyNarrowed ? "double-aggregate-differs-from-original-float-history" : null,
                ActualVisualBitmapScalingMode = retained.Source.EmittedScalingMode.ToString(),
                SourceMutationOrdinal = retained.Generation,
                ImageBrush = input.Output == PaddingOutput.Image ? new
                {
                    PixelWidth = 2, PixelHeight = 1, DpiX = 144, DpiY = 192, PixelFormat = "Pbgra32",
                    Viewbox = new[] { 0,0,1,1 }, Viewport = new[] { 0,0,1,1 },
                    ViewboxUnits = "RelativeToBoundingBox", ViewportUnits = "RelativeToBoundingBox",
                    Stretch = "Fill", TileMode = "None", Opacity = 1,
                    SourcePixelSha256 = Convert.ToHexString(SHA256.HashData(PaddingImagePixels))
                } : null,
                Meaning = "Original public WPF drawing/effect only; authored frame is an expectation, not observed native metadata."
            };
            using (var file = new FileStream(Path.Combine(directory,input.Name + ".input.json"),FileMode.CreateNew))
                JsonSerializer.Serialize(file,description,new JsonSerializerOptions { WriteIndented = true });
            if (input.Output == PaddingOutput.Image)
                SaveSamplerBitmap(directory,input.Name + ".source",retained.Effect.SourceBitmap!,PaddingImagePixels);

            // This independently renders the original fractional drawing without
            // any effect. It does not predict alias edges from captured shader
            // output, and is also the explicit ARM64 unavailable-input control.
            var plain = new LocalCaptureVisualState();
            plain.Prepare(input,withEffect:false);
            byte[] inputBaseline = CaptureLocalBitmap(plain.Root,input,directory,input.Name + ".plain",timer);
            AssertOriginalPlainCapture(inputBaseline);
            ++baselineCount;
            byte[]? frameBaseline = null;
            if (!input.IntegralPlacement)
            {
                // Deferred native placement still gets a strict independent
                // ORIGINAL control: draw the authored final quad directly with
                // an ordinary opaque brush, not an effect or sampled result.
                var frame = new LocalCaptureVisualState();
                frame.PrepareFrameBaseline(input);
                frameBaseline = CaptureLocalBitmap(frame.Root,input,directory,input.Name + ".frame",timer);
                AssertOriginalColoredFrame(frameBaseline);
                ++baselineCount;
            }
            byte[]? first = null;
            var hashes = new List<string>();
            for (int replay = 0; replay < 3; ++replay)
            {
                LocalCaptureVisualState visual = retained;
                if (replay == 2) { visual = new LocalCaptureVisualState(); visual.Prepare(input,withEffect:true); }
                byte[] pixels = CaptureLocalBitmap(visual.Root,input,directory,input.Name + "-replay-" + replay,timer);
                try { AssertLocalCapture(input,pixels,inputBaseline,frameBaseline,unavailable); }
                catch (InvalidOperationException error) { failures.Add($"Replay {replay}: {error.Message}"); }
                if (first != null && !first.AsSpan().SequenceEqual(pixels))
                    failures.Add($"{input.Name}: cold/warm/independent pixels differ.");
                first ??= pixels;
                hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
            }
            if (input.Name == "local-generation-zero") before = first;
            if (input.Name == "local-generation-expand") expanded = first;
            if (input.Name == "local-generation-reset")
            {
                if (before == null || expanded == null || !before.AsSpan().SequenceEqual(first))
                    failures.Add("Original local-frame reset did not restore every pixel.");
                if (!unavailable && (before == null || before.AsSpan().SequenceEqual(expanded)))
                    failures.Add("Original local-frame padding expansion changed no pixels.");
                mutationChecks += 2;
            }
            observations.Add(new { Input = description, Replays = 3, Pixels = first, PixelSha256 = hashes[0],
                ReplaySha256 = hashes, PlainInputSha256 = Convert.ToHexString(SHA256.HashData(inputBaseline)),
                FinalQuadSha256 = frameBaseline == null ? null : Convert.ToHexString(SHA256.HashData(frameBaseline)) });
        }
        if (observations.Count != 21 || baselineCount != 23 || mutationChecks != 4 || arithmeticControls != 88)
            throw new InvalidOperationException("Original fractional local-frame inventory is incomplete.");
        var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Where(module => string.Equals(module.ModuleName,"wpfgfx_cor3.dll",StringComparison.OrdinalIgnoreCase))
            .Select(module => FileIdentity(module.FileName)).ToArray();
        if (modules.Length != 1) throw new InvalidOperationException("Original local-frame native renderer identity is ambiguous.");
        var receipt = new
        {
            Schema = 1, SourceCommit = commit, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            PresentationIdentity = typeof(ShaderEffect).Assembly.FullName,
            PresentationCore = FileIdentity(typeof(ShaderEffect).Assembly.Location),
            Producer = FileIdentity(Assembly.GetExecutingAssembly().Location), NativeModules = modules,
            CaseCount = observations.Count, Cases = observations, Replays = 63, OriginalDrawingBaselines = baselineCount,
            NativeCandidateCases = 18, DeferredFinalPlacementControls = 2, DeferredFloatHistoryControls = 1,
            ArithmeticControls = arithmeticControls, MutationChecks = mutationChecks, InvalidShaders = invalidShaders,
            CaptureMode = unavailable ? "unsupported-software-control" : "shader-pixels",
            QualifiedShaderCases = unavailable || failures.Count != 0 ? 0 : observations.Count,
            Failures = failures, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds - started,
            Qualification = "Original SOFTWARE source reference only; no native admission, hardware UV equivalence, package or application qualification."
        };
        using (var output = new FileStream(Path.Combine(directory,failures.Count == 0
            ? "shader-local-capture.json" : "shader-local-capture.failed.json"),FileMode.CreateNew))
            JsonSerializer.Serialize(output,receipt,new JsonSerializerOptions { WriteIndented = true });
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine,failures));
        Console.WriteLine($"Original local capture: 21 cases / 63 replays / 23 plain baselines / 4 mutation checks; {(unavailable ? 0 : 21)} software shader cases qualified.");
    }

    private static byte[] CaptureLocalBitmap(Visual root,LocalCaptureCase input,string directory,string name,Stopwatch timer)
    {
        CheckPaddingDeadline(timer);
        var bitmap = new RenderTargetBitmap(ShaderLocalCaptureOracle.Width,ShaderLocalCaptureOracle.Height,
            96 * input.Dpi,96 * input.Dpi,PixelFormats.Pbgra32);
        bitmap.Render(root);
        Dispatcher.CurrentDispatcher.Invoke(static () => { },DispatcherPriority.ApplicationIdle);
        if (Volatile.Read(ref invalidShaders) != 0) throw new InvalidOperationException($"Original WPF rejected {input.Name}.");
        var pixels = new byte[ShaderLocalCaptureOracle.Width * ShaderLocalCaptureOracle.Height * 4];
        bitmap.CopyPixels(pixels,ShaderLocalCaptureOracle.Width * 4,0);
        SaveSamplerBitmap(directory,name,bitmap,pixels);
        return pixels;
    }

    private static void AssertLocalCapture(LocalCaptureCase input,byte[] pixels,byte[] plain,byte[]? frame,bool unavailable)
    {
        for (int y = 0; y < ShaderLocalCaptureOracle.Height; ++y)
        for (int x = 0; x < ShaderLocalCaptureOracle.Width; ++x)
        for (int channel = 0; channel < 4; ++channel)
        {
            int offset = (y * ShaderLocalCaptureOracle.Width + x) * 4 + channel;
            byte expected = unavailable
                ? input.Output == PaddingOutput.Image ? channel == 3 ? (byte)255 : (byte)0 : plain[offset]
                : input.Output == PaddingOutput.Input ? plain[offset]
                : !input.IntegralPlacement ? ExpectedDeferredLocalCapture(input,x,y,channel,frame)
                : ShaderLocalCaptureOracle.Expected(input,x,y,channel);
            if (pixels[offset] != expected)
                throw new InvalidOperationException($"{input.Name}: ({x},{y}) BGRA[{channel}]={pixels[offset]}, expected {expected}.");
        }
    }

    private static void AssertOriginalPlainCapture(byte[] pixels)
    {
        bool hasWhite = false;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            byte value = pixels[offset];
            if ((value != 0 && value != 255) || pixels[offset + 1] != value ||
                pixels[offset + 2] != value || pixels[offset + 3] != 255)
                throw new InvalidOperationException("Original plain input was not the requested binary aliased opaque drawing.");
            hasWhite |= value == 255;
        }
        if (!hasWhite) throw new InvalidOperationException("Original positive-size plain input rendered no white pixels.");
    }

    private static void AssertOriginalColoredFrame(byte[] pixels)
    {
        bool hasColor = false;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            bool colored = pixels[offset + 2] == 64;
            if (pixels[offset] != (colored ? 191 : 0) || pixels[offset + 1] != (colored ? 128 : 0) ||
                pixels[offset + 2] != (colored ? 64 : 0) || pixels[offset + 3] != 255)
                throw new InvalidOperationException("Original final-frame baseline was not the requested binary opaque colored drawing.");
            hasColor |= colored;
        }
        if (!hasColor) throw new InvalidOperationException("Original positive-size final frame rendered no colored pixels.");
    }

    private static byte ExpectedDeferredLocalCapture(LocalCaptureCase input,int x,int y,int channel,byte[]? frame)
    {
        if (frame == null) throw new InvalidOperationException("Missing independent original final-quad control.");
        int offset = (y * ShaderLocalCaptureOracle.Width + x) * 4;
        // Validate the independent ordinary-brush control before borrowing its
        // exact aliased coverage. Bounds remain the authored scale-space frame.
        bool inside = frame[offset + 2] == 64;
        if (frame[offset] != (inside ? 191 : 0) || frame[offset + 1] != (inside ? 128 : 0) ||
            frame[offset + 2] != (inside ? 64 : 0) || frame[offset + 3] != 255)
            throw new InvalidOperationException("The original final-quad control was not a binary aliased opaque drawing.");
        // PreSubgraph also pushes the original padded visual bounds as an
        // aliased output clip. It does not shrink the intermediate/UV frame or
        // the independent ordinary final-quad baseline above.
        inside &= ShaderLocalCaptureOracle.OutputClip(input).Contains(x,y);
        return !inside ? channel == 3 ? (byte)255 : (byte)0
            : !input.SquareUv ? frame[offset + channel]
            : inside ? ShaderLocalCaptureOracle.SquaredUv(input,x,y,channel)
            : channel == 3 ? (byte)255 : (byte)0;
    }

    private static uint[] LocalCaptureWords(LocalCaptureCase input)
    {
        if (!input.SquareUv) return PaddingWords(input.Output);
        // Original ps_2_0 MUL of t0.xy with itself distinguishes evaluation on
        // the destination lattice from filtering an already evaluated image.
        return [0xffff0200,0x0200001f,0x80000000,0xb0030000,
            0x0200001f,0x90000000,0xa00f0800,
            0x05000051,0xa00f0002,0,0,0,0x3f800000,
            0x03000042,0x800f0000,0xb0e40000,0xa0e40800,
            0x03000005,0x80030800,0xb0e40000,0xb0e40000,
            0x02000001,0x800c0800,0xa0e40002,0xffff];
    }

    private sealed class LocalCaptureParent : ContainerVisual
    {
        internal LocalCaptureParent() { VisualEdgeMode = EdgeMode.Aliased; VisualBitmapScalingMode = BitmapScalingMode.NearestNeighbor; }
    }

    private sealed class LocalCaptureVisualState
    {
        internal readonly ContainerVisual Root = new();
        internal readonly LocalCaptureParent Clip = new();
        private readonly ContainerVisual placement = new(), inner = new();
        internal readonly PaddingDrawingVisual Source = new();
        private readonly DrawingVisual background = new();
        private readonly Dictionary<(PaddingOutput,bool),OriginalLocalCaptureEffect> effects = new();
        private bool hasSourceDrawing;
        internal OriginalLocalCaptureEffect Effect { get; private set; } = null!;
        internal int Generation { get; private set; }

        internal LocalCaptureVisualState()
        {
            Root.Children.Add(background); Root.Children.Add(Clip); Clip.Children.Add(placement);
            placement.Children.Add(inner); inner.Children.Add(Source);
        }

        internal void Prepare(LocalCaptureCase input,bool withEffect)
        {
            PrepareTarget(input);
            SetTransform(placement,new TranslateTransform(input.DeviceX / input.Dpi,input.DeviceY / input.Dpi));
            SetTransform(inner,input.History switch
            {
                LocalCaptureHistory.Nested => new ScaleTransform(2,2),
                LocalCaptureHistory.SeparatelyNarrowed => new MatrixTransform(ShaderLocalCaptureOracle.SeparateScale,0,0,1,0,0),
                _ => Transform.Identity
            });
            SetTransform(Source,input.History switch
            {
                LocalCaptureHistory.Nested => new ScaleTransform(.5,.5),
                LocalCaptureHistory.SeparatelyNarrowed => new MatrixTransform(ShaderLocalCaptureOracle.SeparateScale,0,0,1,0,0),
                _ => Transform.Identity
            });
            if (!hasSourceDrawing)
            {
                using var drawing = Source.RenderOpen();
                drawing.DrawRectangle(Brushes.White,null,new(ShaderLocalCaptureOracle.ContentX,ShaderLocalCaptureOracle.ContentY,
                    ShaderLocalCaptureOracle.ContentWidth,ShaderLocalCaptureOracle.ContentHeight));
                hasSourceDrawing = true;
            }
            if (!withEffect) { Source.Effect = null; return; }
            var key = (input.Output,input.SquareUv);
            if (!effects.TryGetValue(key,out var effect)) effects.Add(key,effect = new OriginalLocalCaptureEffect(input));
            Effect = effect;
            effect.SetPadding(input);
            if (!ReferenceEquals(Source.Effect,effect)) Source.Effect = effect;
            ++Generation;
        }

        internal void PrepareFrameBaseline(LocalCaptureCase input)
        {
            PrepareTarget(input);
            using var drawing = Source.RenderOpen();
            drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(255,64,128,191)),null,
                new(input.FinalLeft / input.Dpi,input.FinalTop / input.Dpi,
                    input.Allocation.Width / input.Dpi,input.Allocation.Height / input.Dpi));
        }

        private void PrepareTarget(LocalCaptureCase input)
        {
            using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.Black,null,
                new(0,0,ShaderLocalCaptureOracle.Width / input.Dpi,ShaderLocalCaptureOracle.Height / input.Dpi));
            PaddingFrame clip = input.Clip;
            Clip.Clip = new RectangleGeometry(new(clip.Left / input.Dpi,clip.Top / input.Dpi,clip.Width / input.Dpi,clip.Height / input.Dpi));
        }

        internal Matrix[] TransformHistory() => [Source.Transform.Value,inner.Transform.Value,placement.Transform.Value];

        private static void SetTransform(ContainerVisual visual,Transform transform)
        {
            if (visual.Transform == null || visual.Transform.Value != transform.Value) visual.Transform = transform;
        }
    }

    private sealed class OriginalLocalCaptureEffect : ShaderEffect
    {
        private static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
            "Input",typeof(OriginalLocalCaptureEffect),0,SamplingMode.NearestNeighbor);
        private static readonly DependencyProperty ConstantProperty = DependencyProperty.Register(
            "Constant",typeof(Point4D),typeof(OriginalLocalCaptureEffect),
            new UIPropertyMetadata(default(Point4D),PixelShaderConstantCallback(0)));
        private readonly LocalCaptureCase shaderInput;
        internal BitmapSource? SourceBitmap { get; }

        internal OriginalLocalCaptureEffect(LocalCaptureCase input)
        {
            shaderInput = input;
            uint[] words = LocalCaptureWords(input);
            var bytes = new byte[words.Length * 4];
            for (int i = 0; i < words.Length; ++i) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4,4),words[i]);
            var shader = new PixelShader { ShaderRenderMode = ShaderRenderMode.SoftwareOnly };
            using (var stream = new MemoryStream(bytes,writable:false)) shader.SetStreamSource(stream);
            PixelShader = shader;
            if (input.Output == PaddingOutput.Derivatives) DdxUvDdyUvRegisterIndex = 0;
            Brush brush = ImplicitInput;
            if (input.Output == PaddingOutput.Image)
            {
                SourceBitmap = BitmapSource.Create(2,1,144,192,PixelFormats.Pbgra32,null,PaddingImagePixels,8);
                brush = new ImageBrush(SourceBitmap)
                {
                    Viewbox = new(0,0,1,1), Viewport = new(0,0,1,1),
                    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                    Stretch = Stretch.Fill, TileMode = TileMode.None, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center
                };
            }
            SetValue(InputProperty,brush); UpdateShaderValue(InputProperty);
            SetValue(ConstantProperty,new Point4D(.25,.5,.75,1)); UpdateShaderValue(ConstantProperty);
        }

        internal void SetPadding(LocalCaptureCase input)
        {
            PaddingTop = input.Top; PaddingBottom = input.Bottom; PaddingLeft = input.Left; PaddingRight = input.Right;
            if (PaddingTop != input.Top || PaddingBottom != input.Bottom || PaddingLeft != input.Left || PaddingRight != input.Right)
                throw new InvalidOperationException("Original local-capture padding changed before publication.");
        }

        internal string[] PaddingBits() => new[] { PaddingTop,PaddingBottom,PaddingLeft,PaddingRight }
            .Select(value => BitConverter.DoubleToInt64Bits(value).ToString("X16")).ToArray();
        protected override Freezable CreateInstanceCore() => new OriginalLocalCaptureEffect(shaderInput);
    }
}
