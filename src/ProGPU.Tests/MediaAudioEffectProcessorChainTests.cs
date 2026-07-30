using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using Xunit;

namespace ProGPU.Tests;

public sealed class MediaAudioEffectProcessorChainTests
{
    [Fact]
    public void
        TypedEffectChainPreservesOrderAndWarmProcessingDoesNotAllocate()
    {
        var registry = new MediaEffectRegistry();
        List<string> disposalOrder = [];
        using IDisposable firstRegistration =
            registry.Register(
                new TransformEffectFactory(
                    "ProGPU.Tests.FirstTransform",
                    scale: 2f,
                    offset: 0f,
                    disposalOrder));
        using IDisposable secondRegistration =
            registry.Register(
                new TransformEffectFactory(
                    "ProGPU.Tests.SecondTransform",
                    scale: 1f,
                    offset: 0.25f,
                    disposalOrder));
        MediaCompositionEffectDefinition[] definitions =
        [
            Definition("ProGPU.Tests.FirstTransform"),
            Definition("ProGPU.Tests.SecondTransform")
        ];

        Assert.True(
            MediaAudioEffectProcessorChain
                .TryCreate(
                    registry,
                    definitions,
                    out MediaAudioEffectProcessorChain?
                        chain));
        Assert.NotNull(chain);
        Assert.Equal(2, chain.Count);
        var context =
            new MediaAudioProcessContext(
                new MediaAudioFormat(48_000, 2),
                FrameCount: 2,
                PresentationTime:
                    TimeSpan.FromSeconds(1));
        var samples = new float[4];

        using (chain)
        {
            Array.Fill(samples, 1f);
            chain.Process(samples, context);
            Assert.All(
                samples,
                sample =>
                    Assert.Equal(2.25f, sample));

            Array.Fill(samples, 1f);
            chain.Process(samples, context);
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0;
                 iteration < 100;
                 iteration++)
            {
                Array.Fill(samples, 1f);
                chain.Process(samples, context);
            }
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;

            Assert.Equal(0, allocated);
        }

        Assert.Equal(
            [
                "ProGPU.Tests.SecondTransform",
                "ProGPU.Tests.FirstTransform"
            ],
            disposalOrder);
    }

    [Fact]
    public void
        TypedEffectChainDisposesPartialActivationOnFailure()
    {
        var registry = new MediaEffectRegistry();
        List<string> disposalOrder = [];
        using IDisposable registration =
            registry.Register(
                new TransformEffectFactory(
                    "ProGPU.Tests.PreparedTransform",
                    scale: 1f,
                    offset: 0f,
                    disposalOrder));

        Assert.False(
            MediaAudioEffectProcessorChain
                .TryCreate(
                    registry,
                    [
                        Definition(
                            "ProGPU.Tests.PreparedTransform"),
                        Definition(
                            "ProGPU.Tests.MissingTransform")
                    ],
                    out MediaAudioEffectProcessorChain?
                        chain));
        Assert.Null(chain);
        Assert.Equal(
            ["ProGPU.Tests.PreparedTransform"],
            disposalOrder);
    }

    [Fact]
    public void
        TypedEffectChainDisposesInvalidFactoryResultExactlyOnce()
    {
        var registry = new MediaEffectRegistry();
        var factory = new InvalidEffectFactory();
        using IDisposable registration =
            registry.Register(factory);

        Assert.False(
            MediaAudioEffectProcessorChain
                .TryCreate(
                    registry,
                    [
                        Definition(
                            factory.ActivatableClassId)
                    ],
                    out MediaAudioEffectProcessorChain?
                        chain));
        Assert.Null(chain);
        Assert.Equal(1, factory.DisposeCount);
    }

    private static MediaCompositionEffectDefinition
        Definition(string id) =>
        new(
            id,
            new Dictionary<string, object?>());

    private sealed class TransformEffectFactory :
        IMediaEffectFactory
    {
        private readonly float _scale;
        private readonly float _offset;
        private readonly List<string> _disposalOrder;

        internal TransformEffectFactory(
            string id,
            float scale,
            float offset,
            List<string> disposalOrder)
        {
            ActivatableClassId = id;
            _scale = scale;
            _offset = offset;
            _disposalOrder = disposalOrder;
        }

        public string ActivatableClassId { get; }

        public IMediaEffect Create(
            in MediaEffectDescriptor descriptor)
        {
            Assert.Equal(
                ActivatableClassId,
                descriptor.ActivatableClassId);
            Assert.Equal(
                MediaEffectKind.Audio,
                descriptor.Kind);
            return new TransformEffect(
                ActivatableClassId,
                _scale,
                _offset,
                _disposalOrder);
        }
    }

    private sealed class TransformEffect :
        IMediaAudioEffect
    {
        private readonly float _scale;
        private readonly float _offset;
        private readonly List<string> _disposalOrder;

        internal TransformEffect(
            string id,
            float scale,
            float offset,
            List<string> disposalOrder)
        {
            Id = id;
            _scale = scale;
            _offset = offset;
            _disposalOrder = disposalOrder;
        }

        public string Id { get; }

        public MediaEffectKind Kind =>
            MediaEffectKind.Audio;

        public void Process(
            Span<float> interleavedSamples,
            in MediaAudioProcessContext context)
        {
            int sampleCount = checked(
                context.FrameCount *
                context.Format.ChannelCount);
            Span<float> samples =
                interleavedSamples[..sampleCount];
            for (int index = 0;
                 index < samples.Length;
                 index++)
            {
                samples[index] =
                    samples[index] *
                    _scale +
                    _offset;
            }
        }

        public void Dispose() =>
            _disposalOrder.Add(Id);
    }

    private sealed class InvalidEffectFactory :
        IMediaEffectFactory
    {
        private int _disposeCount;

        public string ActivatableClassId =>
            "ProGPU.Tests.InvalidAudioTransform";

        internal int DisposeCount =>
            Volatile.Read(ref _disposeCount);

        public IMediaEffect Create(
            in MediaEffectDescriptor descriptor) =>
            new InvalidEffect(this);

        private sealed class InvalidEffect(
            InvalidEffectFactory owner) :
            IMediaEffect
        {
            public string Id =>
                owner.ActivatableClassId;

            public MediaEffectKind Kind =>
                MediaEffectKind.Audio;

            public void Dispose() =>
                Interlocked.Increment(
                    ref owner._disposeCount);
        }
    }
}
