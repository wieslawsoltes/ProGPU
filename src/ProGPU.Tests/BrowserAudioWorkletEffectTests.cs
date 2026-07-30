using ProGPU.Browser;
using ProGPU.Media.Effects;
using Xunit;

namespace ProGPU.Tests;

public sealed class BrowserAudioWorkletEffectTests
{
    [Fact]
    public void StateValidatesAndRetainsTypedWorkletConfiguration()
    {
        var state = new BrowserAudioWorkletEffectState(
            "./effects/limiter.js",
            "progpu-limiter",
            """
            {
              "parameterData": {
                "threshold": -6
              },
              "processorOptions": {
                "lookAheadFrames": 0
              }
            }
            """);

        Assert.Equal(
            "./effects/limiter.js",
            state.ModuleUri);
        Assert.Equal(
            "progpu-limiter",
            state.ProcessorName);
        Assert.Contains(
            "\"threshold\"",
            state.NodeOptionsJson,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "processor", "{}")]
    [InlineData("effect.js", "", "{}")]
    [InlineData(" effect.js", "processor", "{}")]
    [InlineData("effect.js", "processor ", "{}")]
    [InlineData("effect.js", "processor", "[]")]
    [InlineData("effect.js", "processor", "null")]
    [InlineData("effect.js", "processor", "{")]
    public void StateRejectsInvalidWorkletConfiguration(
        string moduleUri,
        string processorName,
        string nodeOptionsJson)
    {
        Assert.ThrowsAny<Exception>(
            () => new BrowserAudioWorkletEffectState(
                moduleUri,
                processorName,
                nodeOptionsJson));
    }

    [Fact]
    public void StateRejectsUnboundedNodeOptions()
    {
        string nodeOptionsJson =
            "{\"value\":\"" +
            new string(
                'x',
                BrowserAudioWorkletEffectState
                    .MaximumNodeOptionsJsonLength) +
            "\"}";

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BrowserAudioWorkletEffectState(
                "effect.js",
                "processor",
                nodeOptionsJson));
    }

    [Fact]
    public void FactoryUsesWinUiDefinitionPropertyOverrides()
    {
        var factory =
            new BrowserAudioWorkletEffectFactory(
                "Tests.Browser.Worklet",
                "default.js",
                "default-processor");
        var descriptor = new MediaEffectDescriptor(
            factory.ActivatableClassId,
            MediaEffectKind.Audio,
            new Dictionary<string, object?>
            {
                [BrowserAudioWorkletEffectFactory
                    .ModuleUriPropertyName] =
                    "override.js",
                [BrowserAudioWorkletEffectFactory
                    .ProcessorNamePropertyName] =
                    "override-processor",
                [BrowserAudioWorkletEffectFactory
                    .NodeOptionsJsonPropertyName] =
                    "{\"parameterData\":{\"mix\":0.25}}"
            });

        using IMediaEffect effect =
            factory.Create(descriptor);
        var worklet =
            Assert.IsAssignableFrom<
                IBrowserAudioWorkletEffect>(
                    effect);
        BrowserAudioWorkletEffectState state =
            worklet.CaptureState();

        Assert.Equal(
            "Tests.Browser.Worklet",
            effect.Id);
        Assert.Equal(
            MediaEffectKind.Audio,
            effect.Kind);
        Assert.Equal(
            "override.js",
            state.ModuleUri);
        Assert.Equal(
            "override-processor",
            state.ProcessorName);
        Assert.Equal(
            "{\"parameterData\":{\"mix\":0.25}}",
            state.NodeOptionsJson);
    }

    [Fact]
    public void FactoryRejectsNonAudioActivation()
    {
        var factory =
            new BrowserAudioWorkletEffectFactory(
                "Tests.Browser.Worklet",
                "effect.js",
                "processor");
        var descriptor = new MediaEffectDescriptor(
            factory.ActivatableClassId,
            MediaEffectKind.Video,
            new Dictionary<string, object?>());

        Assert.Throws<ArgumentException>(
            () => factory.Create(descriptor));
    }

    [Fact]
    public void FactoryRejectsNonStringOverrides()
    {
        var factory =
            new BrowserAudioWorkletEffectFactory(
                "Tests.Browser.Worklet",
                "effect.js",
                "processor");
        var descriptor = new MediaEffectDescriptor(
            factory.ActivatableClassId,
            MediaEffectKind.Audio,
            new Dictionary<string, object?>
            {
                [BrowserAudioWorkletEffectFactory
                    .NodeOptionsJsonPropertyName] =
                    42
            });

        Assert.Throws<ArgumentException>(
            () => factory.Create(descriptor));
    }
}
