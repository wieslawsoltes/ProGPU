using System.Numerics;
using Avalonia.ProGpu;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Avalonia.ContractTests;

public sealed class AvaloniaRetainedCommandCacheContractTests
{
    [Fact]
    public void OrdinaryVectorCommandsUseCompactReplayStorage()
    {
        var cache = new AvaloniaRetainedCommandCache();
        var recording = new DrawingContext();
        var brush = new SolidColorBrush(
            new Vector4(0.25f, 0.5f, 0.75f, 1f));
        var pen = new Pen(brush, 2.5f);
        var transform = Matrix4x4.CreateTranslation(3f, 4f, 0f);
        recording.Commands.Add(
            new RenderCommand
            {
                Type = RenderCommandType.DrawRoundedRect,
                HitTestId = 42,
                Rect = new Rect(1f, 2f, 30f, 40f),
                Brush = brush,
                Pen = pen,
                Transform = transform,
                RadiusX = 5f,
                RadiusY = 6f,
                IsEdgeAliased = true,
                IsPenThicknessLocal = true,
                PathSampleGrid = 4,
                PathCoverageGamma = 0.75f
            });

        Assert.True(cache.TryCompactOrdinaryCommands(recording));

        Assert.Equal(1, cache.Count);
        Assert.Single(recording.Commands);

        RenderCommand replay = cache.GetCommand(0);
        Assert.Equal(RenderCommandType.DrawRoundedRect, replay.Type);
        Assert.Equal(42, replay.HitTestId);
        Assert.Equal(new Rect(1f, 2f, 30f, 40f), replay.Rect);
        Assert.Same(brush, replay.Brush);
        Assert.Same(pen, replay.Pen);
        Assert.Equal(transform, replay.Transform);
        Assert.Equal(5f, replay.RadiusX);
        Assert.Equal(6f, replay.RadiusY);
        Assert.True(replay.IsEdgeAliased);
        Assert.True(replay.IsPenThicknessLocal);
        Assert.Equal(4u, replay.PathSampleGrid);
        Assert.Equal(0.75f, replay.PathCoverageGamma);
    }

    [Fact]
    public void GlyphRunArraysAreBorrowedWithoutCopying()
    {
        var cache = new AvaloniaRetainedCommandCache();
        var recording = new DrawingContext();
        ushort[] glyphs = [10, 11, 12];
        Vector2[] positions =
        [
            new(0f, 0f),
            new(8f, 0f),
            new(16f, 0f)
        ];
        var brush = new SolidColorBrush(Vector4.One);
        recording.Commands.Add(
            new RenderCommand
            {
                Type = RenderCommandType.DrawGlyphRun,
                Brush = brush,
                FontSize = 16f,
                Position = new Vector2(4f, 5f),
                GlyphIndices = glyphs,
                GlyphPositions = positions,
                GlyphRangeStart = 1,
                GlyphRangeCount = 2,
                IsBold = true,
                PreferGlyphAtlas = true,
                UseLogicalGlyphAtlasResolution = true
            });

        Assert.True(cache.TryCompactOrdinaryCommands(recording));
        RenderCommand replay = cache.GetCommand(0);

        Assert.Single(recording.Commands);
        Assert.Same(glyphs, replay.GlyphIndices);
        Assert.Same(positions, replay.GlyphPositions);
        Assert.Same(brush, replay.Brush);
        Assert.Equal(1, replay.GlyphRangeStart);
        Assert.Equal(2, replay.GlyphRangeCount);
        Assert.True(replay.IsBold);
        Assert.True(replay.PreferGlyphAtlas);
        Assert.True(replay.UseLogicalGlyphAtlasResolution);
    }

    [Fact]
    public void CircleUsesCompactReplayStorage()
    {
        var cache = new AvaloniaRetainedCommandCache();
        var recording = new DrawingContext();
        var brush = new SolidColorBrush(Vector4.One);
        var pen = new Pen(brush, 1.5f);
        recording.DrawCircle(
            brush,
            pen,
            new Vector2(12f, 13f),
            7f);

        Assert.True(cache.TryCompactOrdinaryCommands(recording));

        RenderCommand replay = cache.GetCommand(0);
        Assert.Equal(RenderCommandType.DrawCircle, replay.Type);
        Assert.Equal(new Vector2(12f, 13f), replay.Position2);
        Assert.Equal(7f, replay.RadiusX);
        Assert.Same(brush, replay.Brush);
        Assert.Same(pen, replay.Pen);
    }

    [Fact]
    public void StableCompactShapeUpdatesStorageInPlace()
    {
        var cache = new AvaloniaRetainedCommandCache();
        var recording = new DrawingContext();
        var firstBrush = new SolidColorBrush(Vector4.One);
        recording.Commands.Add(
            new RenderCommand
            {
                Type = RenderCommandType.DrawRect,
                Rect = new Rect(0f, 0f, 10f, 10f),
                Brush = firstBrush
            });

        Assert.True(cache.TryCompactOrdinaryCommands(recording));
        object storage = Assert.IsType<CompactAvaloniaVectorCommand>(
            cache.CompactStorageIdentity);

        var secondBrush = new SolidColorBrush(
            new Vector4(0.5f, 0.25f, 0.75f, 1f));
        recording.Commands[0] = new RenderCommand
        {
            Type = RenderCommandType.DrawRoundedRect,
            Rect = new Rect(2f, 3f, 20f, 30f),
            Brush = secondBrush,
            RadiusX = 4f,
            RadiusY = 5f
        };

        Assert.True(cache.TryCompactOrdinaryCommands(recording));
        Assert.Same(storage, cache.CompactStorageIdentity);

        RenderCommand replay = cache.GetCommand(0);
        Assert.Equal(RenderCommandType.DrawRoundedRect, replay.Type);
        Assert.Equal(new Rect(2f, 3f, 20f, 30f), replay.Rect);
        Assert.Same(secondBrush, replay.Brush);
        Assert.Equal(4f, replay.RadiusX);
        Assert.Equal(5f, replay.RadiusY);
    }

    [Fact]
    public void CanvasStateCommandsUseCompactReplayStorage()
    {
        var cache = new AvaloniaRetainedCommandCache();
        var recording = new DrawingContext();
        var clipPath = new PathGeometry();
        var transform = Matrix4x4.CreateTranslation(3f, 5f, 0f);
        recording.Commands.Add(
            new RenderCommand
            {
                Type = RenderCommandType.PushClip,
                Rect = new Rect(1f, 2f, 30f, 40f),
                Transform = transform
            });
        recording.Commands.Add(
            new RenderCommand
            {
                Type = RenderCommandType.PushGeometryClip,
                Path = clipPath,
                Transform = transform
            });
        recording.PushOpacity(0.75f);
        recording.PopOpacity();
        recording.PushBlendMode(GpuBlendMode.Multiply);
        recording.PopBlendMode();
        recording.PopGeometryClip();
        recording.PopClip();

        Assert.True(cache.TryCompactOrdinaryCommands(recording));
        Assert.Equal(8, cache.Count);
        Assert.All(
            Assert.IsType<CompactAvaloniaCommand[]>(
                cache.CompactStorageIdentity),
            command => Assert.IsType<CompactAvaloniaStateCommand>(command));

        RenderCommand rectangleClip = cache.GetCommand(0);
        Assert.Equal(RenderCommandType.PushClip, rectangleClip.Type);
        Assert.Equal(new Rect(1f, 2f, 30f, 40f), rectangleClip.Rect);
        Assert.Equal(transform, rectangleClip.Transform);

        RenderCommand geometryClip = cache.GetCommand(1);
        Assert.Equal(
            RenderCommandType.PushGeometryClip,
            geometryClip.Type);
        Assert.Same(clipPath, geometryClip.Path);
        Assert.Equal(transform, geometryClip.Transform);
        Assert.Equal(0.75f, cache.GetCommand(2).FontSize);
        Assert.Equal(
            (int)GpuBlendMode.Multiply,
            cache.GetCommand(4).IntParam);
    }

    [Fact]
    public void StableCanvasStateUpdatesStorageInPlace()
    {
        var cache = new AvaloniaRetainedCommandCache();
        var recording = new DrawingContext();
        recording.PushClip(new Rect(0f, 0f, 10f, 10f));
        recording.PopClip();

        Assert.True(cache.TryCompactOrdinaryCommands(recording));
        object storage = Assert.IsType<CompactAvaloniaCommand[]>(
            cache.CompactStorageIdentity);

        recording.Commands[0] = new RenderCommand
        {
            Type = RenderCommandType.PushClip,
            Rect = new Rect(2f, 3f, 20f, 30f)
        };
        Assert.True(
            cache.TryCompactOrdinaryCommands(
                recording,
                out bool contentChanged));

        Assert.True(contentChanged);
        Assert.Same(storage, cache.CompactStorageIdentity);
        Assert.Equal(
            new Rect(2f, 3f, 20f, 30f),
            cache.GetCommand(0).Rect);
    }

    [Fact]
    public void CompactCacheClassifiesPresentationOnlyRerecording()
    {
        var cache = new AvaloniaRetainedCommandCache();
        var recording = new DrawingContext();
        ushort[] glyphs = [7, 8];
        Vector2[] positions = [Vector2.Zero, new Vector2(8f, 0f)];
        var brush = new SolidColorBrush(Vector4.One);
        recording.Commands.Add(
            new RenderCommand
            {
                Type = RenderCommandType.DrawGlyphRun,
                Brush = brush,
                GlyphIndices = glyphs,
                GlyphPositions = positions,
                GlyphRangeCount = glyphs.Length,
                TextRenderingMode = TextRenderingMode.Grayscale,
                TextHintingMode = TextHintingMode.Auto,
                PresentationDependencies =
                    RenderCommandPresentationDependencies.TextRendering |
                    RenderCommandPresentationDependencies.TextHinting
            });

        Assert.True(
            cache.TryCompactOrdinaryCommands(
                recording,
                out bool initialContentChanged));
        Assert.True(initialContentChanged);
        object storage = Assert.IsType<CompactAvaloniaGlyphRunCommand>(
            cache.CompactStorageIdentity);

        RenderCommand rerecorded = recording.Commands[0];
        rerecorded.TextRenderingMode = TextRenderingMode.Aliased;
        rerecorded.TextHintingMode = TextHintingMode.Fixed;
        recording.Commands[0] = rerecorded;

        Assert.True(
            cache.TryCompactOrdinaryCommands(
                recording,
                out bool presentationOnlyChanged));
        Assert.False(presentationOnlyChanged);
        Assert.Same(storage, cache.CompactStorageIdentity);

        rerecorded.Position = new Vector2(2f, 3f);
        recording.Commands[0] = rerecorded;
        Assert.True(
            cache.TryCompactOrdinaryCommands(
                recording,
                out bool geometryChanged));
        Assert.True(geometryChanged);
        Assert.Same(storage, cache.CompactStorageIdentity);
    }

    [Fact]
    public void VisualLateBindsOnlyInheritedPresentationFields()
    {
        var visual = new AvaloniaCompositionVisual();
        Assert.True(visual.SynchronizeDrawingOptions(
            localRenderOptions: default,
            new global::Avalonia.Media.TextOptions
            {
                TextRenderingMode =
                    global::Avalonia.Media.TextRenderingMode.Alias,
                TextHintingMode =
                    global::Avalonia.Media.TextHintingMode.Strong
            },
            inheritedRenderOptions: default,
            inheritedTextOptions: default,
            inheritedDisablesSubpixelText: false,
            out _));

        DrawingContext commands = visual.GetOrCreateCommands();
        commands.Commands.Add(
            new RenderCommand
            {
                Type = RenderCommandType.DrawGlyphRun,
                TextRenderingMode = TextRenderingMode.Grayscale,
                TextHintingMode = TextHintingMode.Auto,
                PresentationDependencies =
                    RenderCommandPresentationDependencies.TextRendering |
                    RenderCommandPresentationDependencies.TextHinting
            });
        commands.Commands.Add(
            new RenderCommand
            {
                Type = RenderCommandType.DrawGlyphRun,
                TextRenderingMode = TextRenderingMode.ClearType,
                TextHintingMode = TextHintingMode.Fixed,
                PresentationDependencies =
                    RenderCommandPresentationDependencies.None
            });

        var incrementalCache = (IIncrementalRenderCommandCache)visual;
        IncrementalRenderPresentationState presentationState =
            incrementalCache.IncrementalPresentationState;
        Assert.Equal(
            RenderCommandPresentationDependencies.TextRendering |
            RenderCommandPresentationDependencies.TextHinting,
            presentationState.Dependencies);
        Assert.Equal(
            TextRenderingMode.Aliased,
            presentationState.TextRenderingMode);
        Assert.Equal(
            TextHintingMode.Fixed,
            presentationState.TextHintingMode);

        var cache = (IOwnedRenderCommandCache)visual;
        RenderCommand inherited = cache.GetRenderCommand(0);
        Assert.Equal(TextRenderingMode.Aliased, inherited.TextRenderingMode);
        Assert.Equal(TextHintingMode.Fixed, inherited.TextHintingMode);

        RenderCommand explicitLocal = cache.GetRenderCommand(1);
        Assert.Equal(
            TextRenderingMode.ClearType,
            explicitLocal.TextRenderingMode);
        Assert.Equal(TextHintingMode.Fixed, explicitLocal.TextHintingMode);

        Assert.True(visual.SynchronizeDrawingOptions(
            localRenderOptions: default,
            new global::Avalonia.Media.TextOptions
            {
                TextRenderingMode =
                    global::Avalonia.Media.TextRenderingMode.Antialias,
                TextHintingMode =
                    global::Avalonia.Media.TextHintingMode.None
            },
            inheritedRenderOptions: default,
            inheritedTextOptions: default,
            inheritedDisablesSubpixelText: false,
            out _));

        presentationState = incrementalCache.IncrementalPresentationState;
        Assert.Equal(
            TextRenderingMode.Grayscale,
            presentationState.TextRenderingMode);
        Assert.Equal(
            TextHintingMode.Animated,
            presentationState.TextHintingMode);

        inherited = cache.GetRenderCommand(0);
        Assert.Equal(
            TextRenderingMode.Grayscale,
            inherited.TextRenderingMode);
        Assert.Equal(TextHintingMode.Animated, inherited.TextHintingMode);

        explicitLocal = cache.GetRenderCommand(1);
        Assert.Equal(
            TextRenderingMode.ClearType,
            explicitLocal.TextRenderingMode);
        Assert.Equal(TextHintingMode.Fixed, explicitLocal.TextHintingMode);
    }

    [Fact]
    public void UnsupportedCommandsKeepTheGeneralRepresentation()
    {
        var cache = new AvaloniaRetainedCommandCache();
        var recording = new DrawingContext();
        recording.Commands.Add(
            new RenderCommand
            {
                Type = RenderCommandType.DrawTexture,
                Rect = new Rect(0f, 0f, 10f, 10f)
            });

        Assert.False(cache.TryCompactOrdinaryCommands(recording));
        cache.BeginRecording().Commands.Add(recording.Commands[0]);

        Assert.Equal(1, cache.Count);
        Assert.Equal(
            RenderCommandType.DrawTexture,
            cache.GetCommand(0).Type);
    }
}
