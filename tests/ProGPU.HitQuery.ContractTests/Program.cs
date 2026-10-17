using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Backend.Native;
using ProGPU.Vector;
using Silk.NET.WebGPU;

// Native WebGPU contract probe, not a product dispatcher or CPU hit-test fallback.
internal static unsafe class Program
{
    private static readonly string[] Families = ["bounds", "rect_fill", "rect_stroke", "ellipse_fill", "ellipse_stroke", "line_stroke", "path_fill", "path_stroke", "other"];

    private static void Main(string[] args)
    {
        bool stagedOnly = args.Contains("--staged-only");
        bool admittedIndex = args.Contains("--admitted-index");
        if (args.Contains("--native-product") && !admittedIndex)
            throw new ArgumentException("Native product comparison requires --admitted-index; raw unknown-kind controls are not valid native scene records.");
        bool traceStages = args.Contains("--trace-stages");
        string? expectedPath = Option("--expected-results");
        string? referencePath = Option("--write-reference");
        string? referenceShaderPath = Option("--reference-shader");
        if (stagedOnly && (expectedPath == null || referencePath != null))
            throw new ArgumentException("Staged-only execution requires an independent reference file and cannot publish one.");
        byte[]? expectedResults = expectedPath == null ? null : File.ReadAllBytes(expectedPath);
        using var referenceResults = new MemoryStream();
        bool useDawn = args.Contains("--dawn");
        if (useDawn && args.Contains("--software-adapter"))
            throw new ArgumentException("The Dawn Metal device factory does not support forcing a software adapter.");
        using var dawn = useDawn ? DawnGpuContext.CreateMetalPresentation() : null;
        using var ownedContext = useDawn ? null : new WgpuContext { ForceFallbackAdapter = args.Contains("--software-adapter") };
        var context = dawn?.Context ?? ownedContext!;
        if (ownedContext != null) context.Initialize(null);
        if (args.Contains("--require-fxc") && context.SelectedDx12ShaderCompiler != WgpuDx12ShaderCompiler.Fxc)
            throw new InvalidOperationException("This qualification requires the actual FXC compiler; another compiler is not an equivalent pass.");
        if (args.Contains("--require-automatic-fxc") &&
            (context.HitTestExecutionPreference != GpuHitTestExecutionPreference.Automatic ||
             context.AdapterBackendType != BackendType.D3D12 ||
             context.SelectedDx12ShaderCompiler != WgpuDx12ShaderCompiler.Fxc ||
             context.HitTestExecutionPath != GpuHitTestExecutionPreference.OrderedStages))
            throw new InvalidOperationException("Automatic FXC qualification requires the owned D3D12/FXC device and its automatic ordered-query selection.");
        if (args.Contains("--require-automatic-dxc") &&
            (context.HitTestExecutionPreference != GpuHitTestExecutionPreference.Automatic ||
             context.AdapterBackendType != BackendType.D3D12 ||
             context.SelectedDx12ShaderCompiler != WgpuDx12ShaderCompiler.Dxc ||
             context.HitTestExecutionPath != GpuHitTestExecutionPreference.OrderedStages))
            throw new InvalidOperationException("Automatic DXC qualification requires the owned D3D12/DXC device and its automatic ordered-query selection.");
        Console.WriteLine($"Query policy: {context.HitTestExecutionPath}; adapter={context.AdapterName}; architecture={RuntimeInformation.ProcessArchitecture}.");
        using var cache = new RenderPipelineCache(context);
        var shader = cache.GetOrCreateShader("HitQueryStages", ShaderResource.Load(typeof(GpuHitTestEngine), "GpuHitTesting.wgsl"));
        var referenceShader = referenceShaderPath == null ? shader :
            cache.GetOrCreateShader("HitQueryReference", File.ReadAllText(referenceShaderPath));
        var min = Vector2.Zero;
        var max = new Vector2(20);
        var shapes = new List<GpuHitTestPrimitive>();
        GpuPathSegment[] pathSegments = [
            new() { P0 = min, P1 = new(20, 0), SegmentType = 0 },
            new() { P0 = new(20, 0), P1 = new(25, 10), P2 = max, SegmentType = 1 },
            new() { P0 = max, P1 = new(10, 25), P2 = new(-5, 10), P3 = min, SegmentType = 2 },
            new() { P0 = new(20, 10), P1 = new(10, 20), P2 = new(10), P3 = new(10), SegmentType = 3, Pad1 = BitConverter.SingleToUInt32Bits(MathF.PI / 2) },
            new() { P0 = min, P1 = new(20, 0), P2 = max, SegmentType = 4, Pad0 = BitConverter.SingleToUInt32Bits(0.7f) },
            new() { P0 = min, P1 = new(10, -5), P2 = new(25, 10), P3 = max, SegmentType = 5, Pad0 = BitConverter.SingleToUInt32Bits(0.7f), Pad1 = BitConverter.SingleToUInt32Bits(1.3f) },
            new() { P0 = min, P1 = max, SegmentType = 99 },
            new() { P0 = new(5), P1 = new(5), SegmentType = 0 }
        ];
        if (admittedIndex)
            pathSegments[6] = new() { P0 = min, P1 = max, SegmentType = 0 };
        // Interleave families at identical depth, reuse owner ids, and exceed a
        // single workgroup. This detects reordering by family and list truncation.
        for (int i = 0; i < 160; i++)
        {
            int owner = i % 11;
            var shape = (i % 9) switch
            {
                0 => GpuHitTestPrimitive.Bounds(owner, min, max),
                1 => GpuHitTestPrimitive.RectangleFill(owner, min, max, new Vector2(3)),
                2 => GpuHitTestPrimitive.EllipseFill(owner, min, max),
                3 => GpuHitTestPrimitive.RectangleStroke(owner, min, max, new Vector2(3), 4),
                4 => GpuHitTestPrimitive.EllipseStroke(owner, min, max, 4),
                5 => GpuHitTestPrimitive.LineStroke(owner, min, max, 3, LineGeometryCap.Triangle, LineGeometryCap.Round),
                6 => GpuHitTestPrimitive.PathFill(owner, min, new(25), 0, 3, i % 2 == 0 ? FillRule.Nonzero : FillRule.EvenOdd, Matrix4x4.Identity),
                7 => GpuHitTestPrimitive.PathStroke(owner, new(-5), new(25), (uint)(i % 8), 1, 3, 0,
                    (LineGeometryCap)((i / 9) % 4), (LineGeometryCap)((i / 9 + 1) % 4), Matrix4x4.CreateRotationZ(i % 2 == 0 ? 0.1f : 0)),
                _ => admittedIndex ? GpuHitTestPrimitive.Bounds(owner, min, max)
                    : new GpuHitTestPrimitive((GpuHitTestPrimitiveKind)8, owner, min, max, new(0, 0, 20, 20), default, default, new(1, 0, 0, 0), new(0, 1, 0, 0), 0)
            };
            shape = new(shape.Kind, shape.Id, shape.BoundsMin, shape.BoundsMax, shape.Data0, shape.Data1, shape.Data2,
                shape.InverseTransform0, shape.InverseTransform1, i % 3 == 0 ? 0 : i % 5, shape.Flags);
            if (i % 7 == 0) shape = shape.WithClip(0, 3, FillRule.Nonzero);
            if (i % 13 == 0) shape = shape.WithFlags(shape.Flags | GpuHitTestPrimitiveFlags.PointOnly);
            else if (i % 17 == 0) shape = shape.WithFlags(shape.Flags | GpuHitTestPrimitiveFlags.RegionOnly);
            else if (i % 19 == 0) shape = shape.WithFlags(GpuHitTestPrimitiveFlags.None);
            shapes.Add(shape);
        }
        bool sparse = args.Contains("--sparse");
        bool largeCapacity = args.Contains("--large-capacity");
        int resultSlots = largeCapacity ? 257 : 17;
        if (largeCapacity)
        {
            // More distinct overlapping owners than the public maximum, plus
            // higher/lower/equal-depth duplicates: exercise a full ordered list,
            // replacement and rejection, not merely a large empty allocation.
            for (int i = 0; i < 320; i++)
                shapes.Add(GpuHitTestPrimitive.Bounds(1000 + i, min, max, zIndex: i % 7));
            for (int i = 0; i < 320; i++)
                shapes.Add(GpuHitTestPrimitive.Bounds(1000 + i, min, max, zIndex: i % 3 == 0 ? 9 : i % 3 == 1 ? -1 : i % 7));
        }
        if (sparse)
        {
            // Retain root-local coverage as well as spatially separated children;
            // the original shader remains the independent traversal-order oracle.
            shapes.Add(GpuHitTestPrimitive.Bounds(7, new(-1), new(1000), zIndex: 0));
            for (int i = 0; i < 64; i++)
            {
                var origin = new Vector2(100 + i % 8 * 100, 100 + i / 8 * 100);
                shapes.Add(GpuHitTestPrimitive.RectangleFill(i % 11, origin, origin + new Vector2(30), new(3), zIndex: i % 3));
                shapes.Add(GpuHitTestPrimitive.Bounds(i % 11, origin + new Vector2(5), origin + new Vector2(25), zIndex: i % 3));
            }
        }
        var index = GpuHitTestIndex.Build(CollectionsMarshal.AsSpan(shapes), pathSegments, maxPrimitivesPerNode: 4);
        if (sparse && index.Nodes.Count < 8) throw new InvalidOperationException("Sparse fixture did not create a multi-level tree.");
        Vector2[] points = [new(10), new(-1), new(100), new(0), new(19), new(10, 0), new(20, 10), new(5), new(25), new(15, 5)];
        if (sparse) points = [.. points, new(110), new(310, 410), new(810, 710), new(500, 900)];
        using var productIndex = args.Contains("--product") ? new GpuHitTestDeviceIndex(context, index) : null;
        using var productCache = productIndex == null ? null : new RenderPipelineCache(context);
        if (args.Contains("--native-product") && useDawn)
            throw new ArgumentException("The native product probe currently requires the wgpu-native device; use the independent Dawn reference file.");
        using var nativeProduct = args.Contains("--native-product") ? new NativeCompositor(context, TextureFormat.Rgba8Unorm) : null;
        if (nativeProduct != null)
        {
            // Same packed index contract as GpuPictureNativeSceneCompiler,
            // installed through the actual C++ scene/index ownership path.
            byte[] storage = new byte[checked(4096 + index.Primitives.Count * sizeof(GpuHitTestPrimitive) +
                index.Nodes.Count * sizeof(GpuHitTestNode) + index.PrimitiveIndices.Count * sizeof(uint) +
                index.PathSegments.Count * sizeof(GpuPathSegment))];
            var builder = new NativeSceneStreamBuilder(storage, 817, 1, 0, 1);
            if (!builder.TryAddHitTestIndexResource(1, 1,
                    MemoryMarshal.Cast<GpuHitTestPrimitive, NativeGpuHitTestPrimitive>(index.PrimitiveSpan),
                    MemoryMarshal.Cast<GpuHitTestNode, NativeGpuHitTestNode>(index.NodeSpan), index.PrimitiveIndexSpan,
                    MemoryMarshal.Cast<GpuPathSegment, NativePathSegment>(index.PathSegmentSpan), out _) ||
                !builder.TryBuild(out var stream))
                throw new InvalidOperationException("Could not construct native product query index.");
            nativeProduct.UpdateScene(stream);
        }
        if (productIndex != null && context.HitTestExecutionPath != GpuHitTestExecutionPreference.OrderedStages)
            throw new ArgumentException("Product-stage comparison requires the resolved ordered GPU query path.");
        int productComparisons = 0;
        int nativeComparisons = 0;
        int candidateCapacity = Option("--candidate-capacity") is { } candidateValue
            ? int.Parse(candidateValue, CultureInfo.InvariantCulture) : index.PrimitiveIndices.Count;
        if (candidateCapacity < 1 || candidateCapacity > index.PrimitiveIndices.Count)
            throw new ArgumentOutOfRangeException(nameof(candidateCapacity));
        if (!stagedOnly)
        {
            // Exercise the unchanged product six-binding layout, not just the
            // explicit seven-binding layout used by this experimental dispatcher.
            var owners = new GpuHitTestResult[16];
            if (!GpuHitTestEngine.TryHitTestPoint(context, index, new(10), out _) ||
                !GpuHitTestEngine.TryQueryBoundsAll(context, index, new(10), new(13), owners, out _, out _) ||
                !GpuHitTestEngine.TryQueryEllipseAll(context, index, new(10), new(13), owners, out _, out _))
                throw new InvalidOperationException("Existing product query entrypoints must retain positive coverage.");
        }
        using var queryBuffer = Buffer<GpuHitTestQuery>(context, new GpuHitTestQuery[1]);
        using var nodes = Buffer<GpuHitTestNode>(context, index.NodeSpan);
        using var indices = Buffer<uint>(context, index.PrimitiveIndexSpan);
        using var primitives = Buffer<GpuHitTestPrimitive>(context, index.PrimitiveSpan);
        using var segments = Buffer<GpuPathSegment>(context, index.PathSegmentSpan);
        using var output = Buffer<GpuHitTestResult>(context, new GpuHitTestResult[resultSlots]);
        using var scratch = new GpuBuffer(context, checked((uint)(32 + candidateCapacity * 8)),
            BufferUsage.Storage | BufferUsage.CopySrc);
        // Storage writes and indirect reads cannot share a compute usage scope.
        using var dispatchArguments = new GpuBuffer(context, 12, BufferUsage.Indirect | BufferUsage.CopyDst);
        GpuBuffer[] buffers = [queryBuffer, nodes, indices, primitives, output, segments, scratch];
        var entries = stackalloc BindGroupLayoutEntry[7];
        var bindings = stackalloc BindGroupEntry[7];
        for (int i = 0; i < 7; i++)
        {
            uint binding = i == 6 ? 7u : (uint)i;
            entries[i] = new BindGroupLayoutEntry {
                Binding = binding, Visibility = ShaderStage.Compute,
                Buffer = new BufferBindingLayout { Type = i is 4 or 6 ? BufferBindingType.Storage : BufferBindingType.ReadOnlyStorage }
            };
            bindings[i] = new BindGroupEntry { Binding = binding, Buffer = buffers[i].BufferPtr, Size = buffers[i].Size };
        }
        var layoutDescriptor = new BindGroupLayoutDescriptor { EntryCount = 7, Entries = entries };
        var layout = context.Api.DeviceCreateBindGroupLayout(context.Device, &layoutDescriptor);
        var pipelineDescriptor = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &layout };
        var pipelineLayout = context.Api.DeviceCreatePipelineLayout(context.Device, &pipelineDescriptor);
        var groupDescriptor = new BindGroupDescriptor { Layout = layout, EntryCount = 7, Entries = bindings };
        var group = context.Api.DeviceCreateBindGroup(context.Device, &groupDescriptor);
        try
        {
            int compared = 0;
            int mismatches = 0;
            int fullCapacityQueries = 0;
            foreach (uint mode in new uint[] { 0, 0x80000000, 0xc0000000 })
            foreach (uint capacity in largeCapacity ? new uint[] { 0, 1, 4, 16, 256 } : new uint[] { 0, 1, 4, 16 })
            foreach (Vector2 point in points)
            {
                var query = new GpuHitTestQuery {
                    Point = point, RegionMax = point + new Vector2(3), PrimitiveCount = (uint)index.Primitives.Count,
                    NodeCount = (uint)index.Nodes.Count, PrimitiveIndexCount = (uint)index.PrimitiveIndices.Count,
                    Flags = mode | capacity, PathSegmentCount = (uint)pathSegments.Length
                };
                queryBuffer.Write<GpuHitTestQuery>(new[] { query });
                string modeName = mode == 0 ? "point" : mode == 0x80000000 ? "bounds" : "ellipse";
                byte[]? reference = stagedOnly ? null : Execute(false, modeName);
                byte[] staged = Execute(true, modeName);
                if (capacity == 256 && MemoryMarshal.Cast<byte, GpuHitTestResult>(staged)[256].HasHit)
                    fullCapacityQueries++;
                if (nativeProduct != null)
                {
                    ReadOnlySpan<byte> oracle = expectedResults != null
                        ? expectedResults.AsSpan(checked(compared * staged.Length), staged.Length)
                        : reference ?? throw new InvalidOperationException("Native product queries require an independent reference.");
                    CheckNativeProduct(mode, capacity, point, oracle);
                    nativeComparisons++;
                }
                if (productIndex != null && (capacity != 0 || mode == 0))
                {
                    ReadOnlySpan<byte> oracle = expectedResults != null
                        ? expectedResults.AsSpan(checked(compared * staged.Length), staged.Length)
                        : reference ?? throw new InvalidOperationException("Product queries require an independent reference.");
                    CheckProduct(mode, capacity, point, MemoryMarshal.Cast<byte, GpuHitTestResult>(oracle));
                    productComparisons++;
                }
                if (expectedResults != null)
                {
                    int offset = checked(compared * staged.Length);
                    if (offset > expectedResults.Length - staged.Length || !expectedResults.AsSpan(offset, staged.Length).SequenceEqual(staged))
                    {
                        if (offset <= expectedResults.Length - staged.Length)
                            ReportDifference(expectedResults.AsSpan(offset, staged.Length), staged);
                        throw new InvalidOperationException($"Independent reference mismatch: {modeName}, capacity={capacity}, point={point}");
                    }
                }
                var scratchWords = MemoryMarshal.Cast<byte, uint>(scratch.ReadBytes());
                if (scratchWords[1] != 0) throw new InvalidOperationException("Candidate overflow must not be accepted.");
                if (reference != null && !reference.AsSpan().SequenceEqual(staged))
                {
                    Console.WriteLine($"scratch count={scratchWords[0]}, dispatch={scratchWords[4]}");
                    ReportDifference(reference, staged);
                    Console.WriteLine($"Ordered query mismatch: {modeName}, capacity={capacity}, point={point}");
                    mismatches++;
                }
                if (reference != null) referenceResults.Write(reference);
                compared++;
                if (traceStages) Console.WriteLine($"matched {compared}: {modeName}, capacity={capacity}, point={point}");
            }
            if (mismatches != 0) throw new InvalidOperationException($"{mismatches} ordered queries differed.");
            if (largeCapacity && fullCapacityQueries == 0)
                throw new InvalidOperationException("Large-capacity fixture never filled all 256 result entries.");
            if (expectedResults != null && expectedResults.Length != compared * resultSlots * sizeof(GpuHitTestResult))
                throw new InvalidOperationException("Independent reference has unexpected trailing records.");
            if (referencePath != null)
            {
                using var referenceFile = new FileStream(referencePath, FileMode.CreateNew, FileAccess.Write);
                referenceResults.Position = 0;
                referenceResults.CopyTo(referenceFile);
            }
            Console.WriteLine($"Hit query stages: {compared} complete query records matched byte-for-byte; compiler={context.SelectedDx12ShaderCompiler?.ToString() ?? "not-D3D12"}.");
            if (largeCapacity) Console.WriteLine($"Full 256-result lists: {fullCapacityQueries}; slots checked per query: {resultSlots}.");
            if (productIndex != null) Console.WriteLine($"Product ordered queries: {productComparisons} public query results matched the reference.");
            if (nativeProduct != null) Console.WriteLine($"Native product queries: {nativeComparisons} public query results matched the reference.");
        }
        finally
        {
            context.WaitIdle();
            context.Api.BindGroupRelease(group);
            context.Api.PipelineLayoutRelease(pipelineLayout);
            context.Api.BindGroupLayoutRelease(layout);
        }

        byte[] Execute(bool staged, string modeName)
        {
            var initial = new GpuHitTestResult[resultSlots];
            for (int i = 0; i < initial.Length; i++)
                initial[i] = new() { Id = -1, PrimitiveIndex = uint.MaxValue, ZIndex = -float.MaxValue };
            output.Write<GpuHitTestResult>(initial);
            var encoderDescriptor = new CommandEncoderDescriptor();
            var encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDescriptor);
            try
            {
                if (staged)
                {
                    Dispatch("cs_collect", false);
                    context.Api.CommandEncoderCopyBufferToBuffer(encoder, scratch.BufferPtr, 16, dispatchArguments.BufferPtr, 0, 12);
                    Dispatch($"cs_{modeName}_clip", true);
                    foreach (string family in Families) Dispatch($"cs_{modeName}_{family}", true);
                    Dispatch("cs_merge", false);
                }
                else Dispatch($"cs_{modeName}", false);
                var commandsDescriptor = new CommandBufferDescriptor();
                var commands = context.Api.CommandEncoderFinish(encoder, &commandsDescriptor);
                try { context.Submit(1, &commands); }
                finally { context.Api.CommandBufferRelease(commands); }
                return output.ReadBytes();
            }
            finally { context.Api.CommandEncoderRelease(encoder); }

            void Dispatch(string entry, bool indirect)
            {
                var timer = Stopwatch.StartNew();
                var pipeline = cache.GetOrCreateComputePipeline(entry, staged ? shader : referenceShader, entry, pipelineLayout);
                if (timer.ElapsedMilliseconds > 50) Console.WriteLine($"compile {entry}: {timer.ElapsedMilliseconds} ms");
                var passDescriptor = new ComputePassDescriptor();
                var pass = context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
                context.Api.ComputePassEncoderSetPipeline(pass, pipeline);
                context.Api.ComputePassEncoderSetBindGroup(pass, 0, group, 0, null);
                if (indirect) context.Api.ComputePassEncoderDispatchWorkgroupsIndirect(pass, dispatchArguments.BufferPtr, 0);
                else context.Api.ComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
                context.Api.ComputePassEncoderEnd(pass);
                context.Api.ComputePassEncoderRelease(pass);
                if (traceStages)
                {
                    Console.WriteLine($"submit {entry}");
                    var descriptor = new CommandBufferDescriptor();
                    var commands = context.Api.CommandEncoderFinish(encoder, &descriptor);
                    try { context.Submit(1, &commands); }
                    finally { context.Api.CommandBufferRelease(commands); }
                    context.WaitIdle();
                    Console.WriteLine($"completed {entry}");
                    if (entry is "cs_collect" or "cs_merge")
                    {
                        var words = MemoryMarshal.Cast<byte, uint>(output.ReadBytes());
                        Console.WriteLine($"counters after {entry}: candidates={words[4]}, nodes={words[5]}, precise={words[6]}");
                    }
                    context.Api.CommandEncoderRelease(encoder);
                    var nextEncoderDescriptor = new CommandEncoderDescriptor();
                    encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &nextEncoderDescriptor);
                }
            }
        }

        void CheckNativeProduct(uint mode, uint capacity, Vector2 point, ReadOnlySpan<byte> oracleBytes)
        {
            var oracle = MemoryMarshal.Cast<byte, NativeGpuHitTestResult>(oracleBytes);
            var query = mode == 0 ? NativeGpuHitTestQuery.PointQuery(point, (int)capacity)
                : mode == 0x80000000u ? NativeGpuHitTestQuery.BoundsQuery(point, point + new Vector2(3), (int)capacity)
                : NativeGpuHitTestQuery.EllipseQuery(point, point + new Vector2(3), (int)capacity);
            var token = nativeProduct!.BeginGpuHitTest(query, 817, 1);
            var actual = new NativeGpuHitTestResult[resultSlots - 1];
            var sentinel = new NativeGpuHitTestResult { Hit = 42, Id = -1234567 };
            actual.AsSpan().Fill(sentinel);
            int count = nativeProduct.WaitGpuHitTest(token, actual, out var summary);
            int expectedCount = 0;
            while (expectedCount < capacity && oracle[expectedCount + 1].HasHit) expectedCount++;
            if (count != expectedCount) throw new InvalidOperationException($"Native product count differs: {count} versus {expectedCount}.");
            RequireEqual(oracle[0], summary);
            for (int i = 0; i < actual.Length; i++)
                RequireEqual(i < count ? oracle[i + 1] : sentinel, actual[i]);

            static void RequireEqual(NativeGpuHitTestResult expected, NativeGpuHitTestResult actual)
            {
                var expectedBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref expected, 1));
                var actualBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref actual, 1));
                if (!expectedBytes.SequenceEqual(actualBytes))
                {
                    ReportDifference(expectedBytes, actualBytes);
                    throw new InvalidOperationException("Native product result differs from the independent GPU reference.");
                }
            }
        }

        void CheckProduct(uint mode, uint capacity, Vector2 point, ReadOnlySpan<GpuHitTestResult> oracle)
        {
            if (capacity == 0)
            {
                bool hit = GpuHitTestEngine.TryHitTestPoint(context, productCache!, productIndex!, point, out var actual);
                RequireEqual(oracle[0], actual);
                if (hit != actual.HasHit) throw new InvalidOperationException("Product point hit status differs.");
                return;
            }
            Span<GpuHitTestResult> actualResults = stackalloc GpuHitTestResult[(int)capacity];
            var sentinel = new GpuHitTestResult { Id = -1234567, Hit = 42 };
            actualResults.Fill(sentinel);
            int count;
            GpuHitTestResult summary;
            bool hasHit = mode == 0
                ? GpuHitTestEngine.TryHitTestPointAll(context, productCache!, productIndex!, point, actualResults, out count, out summary)
                : mode == 0x80000000u
                    ? GpuHitTestEngine.TryQueryBoundsAll(context, productCache!, productIndex!, point, point + new Vector2(3), actualResults, out count, out summary)
                    : GpuHitTestEngine.TryQueryEllipseAll(context, productCache!, productIndex!, point, point + new Vector2(3), actualResults, out count, out summary);
            RequireEqual(oracle[0], summary);
            int expectedCount = 0;
            while (expectedCount < capacity && oracle[expectedCount + 1].HasHit) expectedCount++;
            if (count != expectedCount || hasHit != (count != 0))
                throw new InvalidOperationException("Product list count/status differs.");
            for (int i = 0; i < actualResults.Length; i++)
                RequireEqual(i < count ? oracle[i + 1] : sentinel, actualResults[i]);

            static void RequireEqual(GpuHitTestResult expected, GpuHitTestResult actual)
            {
                // The existing managed API initializes empty depths to -Infinity;
                // the raw/native reference uses -FLT_MAX. Preserve both contracts.
                if (expected.ZIndex == -float.MaxValue) expected.ZIndex = float.NegativeInfinity;
                if (!MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref expected, 1)).SequenceEqual(
                    MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref actual, 1))))
                {
                    ReportDifference(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref expected, 1)),
                        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref actual, 1)));
                    throw new InvalidOperationException($"Product result differs: expected owner={expected.Id}, count={expected.Hit}; actual owner={actual.Id}, count={actual.Hit}.");
                }
            }
        }

        string? Option(string name)
        {
            int optionIndex = Array.IndexOf(args, name);
            if (optionIndex < 0) return null;
            if (optionIndex + 1 >= args.Length) throw new ArgumentException($"Missing value for {name}.");
            return args[optionIndex + 1];
        }
    }

    private static void ReportDifference(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        var expectedWords = MemoryMarshal.Cast<byte, uint>(expected);
        var actualWords = MemoryMarshal.Cast<byte, uint>(actual);
        for (int word = 0; word < expectedWords.Length; word++)
            if (expectedWords[word] != actualWords[word])
                Console.WriteLine($"word {word}: expected={expectedWords[word]} actual={actualWords[word]}");
    }

    private static GpuBuffer Buffer<T>(WgpuContext context, ReadOnlySpan<T> data) where T : unmanaged
    {
        var buffer = new GpuBuffer(context, checked((uint)(data.Length * sizeof(T))),
            BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
        buffer.Write(data);
        return buffer;
    }
}
