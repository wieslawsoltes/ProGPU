using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static partial class Program
{
    private sealed record SamplerTransformAnimationCase(string Name, int Kind, int Seconds,
        Matrix PhysicalMatrix, int Left, int Right, int Split, bool Reversed = false,
        string? EquivalentTo = null);

    private static IEnumerable<SamplerTransformAnimationCase> SamplerTransformAnimationCases()
    {
        // Literal matrices and physical color intervals; neither the product
        // resolver nor the original animation evaluator supplies expected data.
        yield return new("matrix-origin", 0, 0, new(1, 0, 0, 1, 0, 0), 0, 32, 16);
        yield return new("matrix-translate", 0, 1, new(1, 0, 0, 1, 8, 0), 8, 32, 24);
        yield return new("matrix-mirror", 0, 2, new(-1, 0, 0, 1, 32, 0), 0, 32, 16, true);
        yield return new("translate-positive", 1, 0, new(1, 0, 0, 1, 8, 0), 8, 32, 24, false, "matrix-translate");
        yield return new("translate-negative", 1, 2, new(1, 0, 0, 1, -8, 0), 0, 24, 8);
        yield return new("scale-centered", 2, 0, new(.5, 0, 0, 1, 8, 0), 8, 24, 16);
        yield return new("scale-mirror", 2, 2, new(-1, 0, 0, 1, 32, 0), 0, 32, 16, true, "matrix-mirror");
        yield return new("group-translate-scale", 3, 0, new(.5, 0, 0, 1, 4, 0), 4, 20, 12);
        yield return new("group-current-translate", 3, 2, new(.5, 0, 0, 1, 2, 0), 2, 18, 10);
        yield return new("group-scale-translate", 4, 2, new(.5, 0, 0, 1, 4, 0), 4, 20, 12, false, "group-translate-scale");
        yield return new("group-repeated-child", 5, 2, new(.5, 0, 0, 1, 4, 0), 4, 20, 12, false, "group-translate-scale");
        yield return new("matrix-detached-base", 6, 2, new(1, 0, 0, 1, 0, 0), 0, 32, 16, false, "matrix-origin");
    }

    private static void CaptureImageSamplerTransformAnimations(string directory, string commit, bool unavailable, Stopwatch timer)
    {
        var observations = new List<object>();
        var failures = new List<string>();
        var absoluteImages = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        byte[] sourcePixels = [0, 0, 255, 255, 0, 255, 0, 255];
        foreach (bool relative in new[] { false, true })
        {
            var scene = new AnimatedSamplerScene(sourcePixels, .5, new(0, 0, 1, 1), new(0, 0, 1, 1));
            using var transforms = new SamplerTransformAnimationOwner(relative, timer);
            var prior = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            string prefix = relative ? "sampler-relative-animation-" : "sampler-transform-animation-";
            SaveSamplerBitmap(directory, prefix + "source", scene.Bitmap, sourcePixels);
            try
            {
                foreach (var test in SamplerTransformAnimationCases())
                {
                    CheckSamplerAnimationDeadline(timer);
                    transforms.Apply(scene.Brush, test);
                    string name = prefix + test.Name;
                    var input = new
                    {
                        Name = name, RelativeTransform = relative, test.Kind, test.Seconds,
                        ExpectedMatrixBits = SamplerMatrixBits(transforms.LiteralMatrix(test)),
                        PhysicalMatrixBits = SamplerMatrixBits(test.PhysicalMatrix),
                        test.Left, test.Right, test.Split, test.Reversed, test.EquivalentTo,
                        ShaderWords = SamplerWords, ShaderRenderMode = "SoftwareOnly",
                        SamplingMode = "NearestNeighbor", ActualVisualBitmapScalingMode = "NearestNeighbor",
                        SourcePixels = sourcePixels, SourcePixelSha256 = Convert.ToHexString(SHA256.HashData(sourcePixels)),
                        Bounds = new Rect(8, 10, 32, 24), Clip = new Rect(8, 10, 32, 24),
                        SourceDpi = 96, Opacity = .5, Viewbox = new Rect(0, 0, 1, 1), Viewport = new Rect(0, 0, 1, 1),
                        Initial = transforms.Describe(scene.Brush, test)
                    };
                    using (var file = new FileStream(Path.Combine(directory, name + ".input.json"), FileMode.CreateNew))
                        JsonSerializer.Serialize(file, input, new JsonSerializerOptions { WriteIndented = true });
                    byte[]? first = null;
                    var replays = new List<object>();
                    for (int replay = 0; replay < 3; ++replay)
                    {
                        CheckSamplerAnimationDeadline(timer);
                        object before = transforms.Describe(scene.Brush, test);
                        AnimatedSamplerScene current = replay < 2 ? scene :
                            new AnimatedSamplerScene(sourcePixels, .5, new(0, 0, 1, 1), new(0, 0, 1, 1));
                        if (replay == 2)
                        {
                            var literal = new MatrixTransform(transforms.LiteralMatrix(test));
                            if (relative) current.Brush.RelativeTransform = literal;
                            else current.Brush.Transform = literal;
                        }
                        current.AssertIdentity(sourcePixels);
                        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(current.Root);
                        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                        if (Volatile.Read(ref invalidShaders) != 0)
                            throw new InvalidOperationException($"Original WPF rejected {name}.");
                        object after = transforms.Describe(scene.Brush, test);
                        current.AssertIdentity(sourcePixels);
                        var pixels = new byte[64 * 64 * 4];
                        bitmap.CopyPixels(pixels, 64 * 4, 0);
                        SaveSamplerBitmap(directory, name + $".replay-{replay}", bitmap, pixels);
                        try { AssertSamplerTransformAnimationPixels(name, test, pixels, unavailable); }
                        catch (InvalidOperationException error) { failures.Add($"Replay {replay}: {error.Message}"); }
                        if (first != null && !first.AsSpan().SequenceEqual(pixels))
                            failures.Add($"{name}: same-owner/warm/independent-literal pixels differ.");
                        first ??= pixels;
                        replays.Add(new { Replay = replay, SameSourceObjects = replay < 2, IndependentLiteralMatrix = replay == 2,
                            Before = before, After = after, Pixels = pixels, PixelSha256 = Convert.ToHexString(SHA256.HashData(pixels)) });
                    }
                    if (test.EquivalentTo != null && !prior[test.EquivalentTo].AsSpan().SequenceEqual(first))
                        failures.Add($"{name}: equivalent source transform does not restore the original raster.");
                    if (relative && !absoluteImages[test.Name].AsSpan().SequenceEqual(first))
                        failures.Add($"{name}: absolute/relative physical mappings differ.");
                    if (!relative) absoluteImages.Add(test.Name, first!);
                    prior.Add(test.Name, first!);
                    observations.Add(new { Name = name, Input = input, Replays = replays });
                }
            }
            finally
            {
                scene.Brush.Transform = Transform.Identity;
                scene.Brush.RelativeTransform = Transform.Identity;
            }
        }
        if (observations.Count != 24) throw new InvalidOperationException("Original transform-animation inventory is incomplete.");
        CheckSamplerAnimationDeadline(timer);
        var receipt = new
        {
            Schema = 1, SourceCommit = commit, CaseFamily = "image-brush-typed-transform-animation",
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            PresentationCore = FileIdentity(typeof(MatrixTransform).Assembly.Location),
            Producer = FileIdentity(Assembly.GetExecutingAssembly().Location),
            CaseCount = observations.Count, Replays = 72, Cases = observations, Failures = failures,
            CaptureMode = unavailable ? "unsupported-software-control" : "shader-pixels",
            QualifiedShaderCases = unavailable || failures.Count != 0 ? 0 : observations.Count,
            Qualification = "Original Microsoft WPF SoftwareOnly transform-animation controls only; no hardware, native provider, package or source-host parity.",
            NumericScope = "Exact dyadic Matrix/Translate/Scale and ordered groups; no named Rotate/Skew or interpolated trigonometry."
        };
        using (var file = new FileStream(Path.Combine(directory, failures.Count == 0 ?
            "image-sampler-transform-animations.json" : "image-sampler-transform-animations.failed.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine($"Original sampler transform animations: 24 states / 72 replays; {(unavailable ? 0 : 24)} source shader cases qualified.");
    }

    private static void AssertSamplerTransformAnimationPixels(string name, SamplerTransformAnimationCase test,
        byte[] pixels, bool unavailable)
    {
        // Literal bounded nearest-color intervals. No original/product matrix
        // inverse, raster output, animation value or shader UV supplies this oracle.
        for (int y = 0; y < 64; ++y) for (int x = 0; x < 64; ++x)
        {
            bool inside = y >= 10 && y < 34 && x >= 8 + test.Left && x < 8 + test.Right;
            bool red = (x < 8 + test.Split) != test.Reversed;
            for (int channel = 0; channel < 4; ++channel)
            {
                byte expected = channel == 3 ? (byte)255 :
                    !unavailable && inside && channel == (red ? 2 : 1) ? (byte)128 : (byte)0;
                byte actual = pixels[(y * 64 + x) * 4 + channel];
                if (actual != expected)
                    throw new InvalidOperationException($"{name}: ({x},{y}) BGRA[{channel}]={actual}, expected {expected}.");
            }
        }
    }

    private static string[] SamplerMatrixBits(Matrix matrix) =>
        new[] { matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.OffsetX, matrix.OffsetY }
            .Select(value => BitConverter.DoubleToUInt64Bits(value).ToString("X16")).ToArray();

    private sealed class SamplerTransformAnimationOwner : IDisposable
    {
        private readonly bool relative;
        private readonly MatrixTransform matrix = new(Matrix.Identity);
        private readonly TranslateTransform translation = new();
        private readonly ScaleTransform scale;
        private readonly TranslateTransform groupTranslation = new();
        private readonly ScaleTransform groupScale = new(.5, 1);
        private readonly TransformGroup group = new();
        private readonly ClockGroup clock;
        private readonly ClockController controller;
        private Transform? selected;
        private readonly int startupCompositionNotifications;

        public SamplerTransformAnimationOwner(bool relative, Stopwatch timer)
        {
            this.relative = relative;
            double unit = relative ? 1.0 / 32 : 1;
            scale = new ScaleTransform(1, 1, 16 * unit, 0);
            var duration = new Duration(TimeSpan.FromSeconds(4));
            var timeline = new ParallelTimeline { Duration = duration, FillBehavior = FillBehavior.HoldEnd };
            var matrices = new MatrixAnimationUsingKeyFrames { Duration = duration };
            matrices.KeyFrames.Add(new DiscreteMatrixKeyFrame(Matrix.Identity, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            matrices.KeyFrames.Add(new DiscreteMatrixKeyFrame(new(1, 0, 0, 1, 8 * unit, 0), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1))));
            matrices.KeyFrames.Add(new DiscreteMatrixKeyFrame(new(-1, 0, 0, 1, 32 * unit, 0), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2))));
            timeline.Children.Add(matrices);
            timeline.Children.Add(new DoubleAnimation(8 * unit, -24 * unit, duration));
            timeline.Children.Add(new DoubleAnimation(.5, -2.5, duration));
            timeline.Children.Add(new DoubleAnimation(8 * unit, 0, duration));
            clock = (ClockGroup)timeline.CreateClock(hasControllableRoot: true);
            controller = clock.Controller ?? throw new InvalidOperationException("Original transform clock is not controllable.");
            matrix.ApplyAnimationClock(MatrixTransform.MatrixProperty, (AnimationClock)clock.Children[0]);
            translation.ApplyAnimationClock(TranslateTransform.XProperty, (AnimationClock)clock.Children[1]);
            scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, (AnimationClock)clock.Children[2]);
            groupTranslation.ApplyAnimationClock(TranslateTransform.XProperty, (AnimationClock)clock.Children[3]);
            try { startupCompositionNotifications = BeginPausedSamplerClock(clock, controller, timer); }
            catch { Dispose(); throw; }
        }

        public Matrix LiteralMatrix(SamplerTransformAnimationCase test)
        {
            Matrix value = test.PhysicalMatrix;
            if (relative) { value.OffsetX /= 32; value.OffsetY /= 24; }
            return value;
        }

        public void Apply(ImageBrush brush, SamplerTransformAnimationCase test)
        {
            controller.SeekAlignedToLastTick(TimeSpan.FromSeconds(test.Seconds), TimeSeekOrigin.BeginTime);
            selected = test.Kind switch { 0 or 6 => matrix, 1 => translation, 2 => scale, _ => group };
            if (test.Kind == 6) matrix.ApplyAnimationClock(MatrixTransform.MatrixProperty, null);
            if (test.Kind >= 3 && test.Kind <= 5)
            {
                group.Children.Clear();
                if (test.Kind == 4) { group.Children.Add(groupScale); group.Children.Add(groupTranslation); }
                else
                {
                    group.Children.Add(groupTranslation);
                    if (test.Kind == 5) group.Children.Add(groupTranslation);
                    group.Children.Add(groupScale);
                }
            }
            if (relative) brush.RelativeTransform = selected;
            else brush.Transform = selected;
        }

        public object Describe(ImageBrush brush, SamplerTransformAnimationCase test)
        {
            TimeSpan expected = TimeSpan.FromSeconds(test.Seconds);
            if (startupCompositionNotifications == 0 || !clock.IsPaused || clock.CurrentState != ClockState.Active || clock.CurrentTime != expected ||
                clock.CurrentProgress != test.Seconds / 4.0 || clock.CurrentGlobalSpeed != 0 ||
                clock.Children.Any(child => child.CurrentTime != expected))
                throw new InvalidOperationException("Original transform clock did not retain the exact paused current time.");
            Transform actual = relative ? brush.RelativeTransform : brush.Transform;
            Transform other = relative ? brush.Transform : brush.RelativeTransform;
            if (!ReferenceEquals(actual, selected) || !SamplerMatrixBits(actual.Value).SequenceEqual(SamplerMatrixBits(LiteralMatrix(test))) ||
                !other.Value.IsIdentity || other.HasAnimatedProperties || brush.Opacity != .5 ||
                brush.Viewport != new Rect(0, 0, 1, 1) || brush.Viewbox != new Rect(0, 0, 1, 1))
                throw new InvalidOperationException("Original sampler changed its retained transform identity, matrix or brush inputs.");
            if (test.Kind >= 3 && test.Kind <= 5)
            {
                Transform[] children = test.Kind == 4 ? [groupScale, groupTranslation] :
                    test.Kind == 5 ? [groupTranslation, groupTranslation, groupScale] : [groupTranslation, groupScale];
                if (group.Children.Count != children.Length || children.Where((item, index) => !ReferenceEquals(item, group.Children[index])).Any() ||
                    !DependencyPropertyHelper.GetValueSource(groupTranslation, TranslateTransform.XProperty).IsAnimated ||
                    groupScale.HasAnimatedProperties)
                    throw new InvalidOperationException("Original ordered group lost child identity, repetition or current-value ownership.");
            }
            else
            {
                DependencyProperty property = test.Kind is 0 or 6 ? MatrixTransform.MatrixProperty :
                    test.Kind == 1 ? TranslateTransform.XProperty : ScaleTransform.ScaleXProperty;
                if (DependencyPropertyHelper.GetValueSource(actual, property).IsAnimated != (test.Kind != 6))
                    throw new InvalidOperationException("Original typed transform animation attachment differs.");
            }
            if (!SamplerMatrixBits((Matrix)matrix.GetAnimationBaseValue(MatrixTransform.MatrixProperty)).SequenceEqual(SamplerMatrixBits(Matrix.Identity)) ||
                (double)translation.GetAnimationBaseValue(TranslateTransform.XProperty) != 0 ||
                (double)scale.GetAnimationBaseValue(ScaleTransform.ScaleXProperty) != 1 ||
                (double)groupTranslation.GetAnimationBaseValue(TranslateTransform.XProperty) != 0)
                throw new InvalidOperationException("Original transform animation silently replaced a source base value.");
            return new
            {
                StartupCompositionNotifications = startupCompositionNotifications,
                clock.IsPaused, CurrentTimeTicks = clock.CurrentTime?.Ticks, clock.CurrentGlobalSpeed,
                Kind = test.Kind, MatrixBits = SamplerMatrixBits(actual.Value),
                MatrixCurrentBits = SamplerMatrixBits(matrix.Matrix), ScaleX = scale.ScaleX, scale.CenterX,
                TranslationX = translation.X, GroupTranslationX = groupTranslation.X,
                GroupChildCount = group.Children.Count, RepeatedChild = test.Kind == 5,
                DetachedMatrix = test.Kind == 6
            };
        }

        public void Dispose()
        {
            matrix.ApplyAnimationClock(MatrixTransform.MatrixProperty, null);
            translation.ApplyAnimationClock(TranslateTransform.XProperty, null);
            scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, null);
            groupTranslation.ApplyAnimationClock(TranslateTransform.XProperty, null);
            controller.Remove();
        }
    }
}
