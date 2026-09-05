using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Samples.Suntrail.Game;
using ProGPU.Samples.Suntrail.Presentation;
using ProGPU.Samples.Suntrail.Rendering;
using ProGPU.Tests.Headless;
using Silk.NET.WebGPU;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ProGPU.Samples.Suntrail.Tests;

public sealed class RenderingTests : IDisposable
{
    private readonly Application _previousApplication = Application.Current;
    private readonly ElementTheme _previousTheme = ThemeManager.CurrentTheme;
    public RenderingTests() => Application.Current = new App();
    public void Dispose() { Application.Current = _previousApplication; ThemeManager.CurrentTheme = _previousTheme; }

    [Fact]
    public void ShaderResourceIsEmbeddedAndCached()
    {
        string shader=ShaderResource.Load(typeof(ProceduralPipeline),"Suntrail.wgsl");
        Assert.Same(shader,ShaderResource.Load(typeof(ProceduralPipeline),"Suntrail.wgsl"));
        Assert.StartsWith("// Algorithm:",shader);Assert.Contains("// Time complexity:",shader);Assert.Contains("// Space complexity:",shader);
        Assert.Equal(48,System.Runtime.InteropServices.Marshal.SizeOf<ProceduralSprite>());
    }

    [Fact]
    public unsafe void AllBiomesRenderAndUnchangedReplayDoesNotUpload()
    {
        using var context=new WgpuContext();context.Initialize(null);
        using var compositor=new Compositor(context,TextureFormat.Rgba8Unorm);
        var pipeline=new ProceduralPipeline();compositor.RegisterExtension(ProceduralDrawingContextExtensions.ExtensionId,pipeline);
        using var target=new GpuTexture(context,1280,800,TextureFormat.Rgba8Unorm,TextureUsage.RenderAttachment|TextureUsage.CopySrc,"Suntrail verification",alphaMode:GpuTextureAlphaMode.Premultiplied);
        var game=new GameSession();var batch=new ProceduralBatch();var visual=new BatchVisual(batch);
        visual.Measure(new(1280,800));visual.Arrange(new Rect(0,0,1280,800));
        var errors=new List<string>();void Error(ErrorType type,string message)=>errors.Add(message);
        WgpuContext.OnWebGpuError+=Error;
        try
        {
            for(int biome=0;biome<8;biome++)
            {
                game.StartLevel(biome);
                for(int tick=0;tick<440;tick++)game.Step(RoutePilot.GetInput(game));
                batch.Build(game,new(1280,800),game.Time);visual.Invalidate();
                compositor.RenderScene(visual,1280,800,target.ViewPtr);context.WaitIdle();
                Assert.True(errors.Count == 0, string.Join("\n", errors));
                var pixels=target.ReadPixels();
                Assert.True(pixels.Where((_,i)=>i%4!=3).Distinct().Count()>100,"Artwork needs a broad tonal range, not a blank clear.");
                PngEncoder.SavePng(Path.Combine(Artifacts(),$"biome-{biome+1}.png"),pixels,1280,800);
                long uploaded=pipeline.UploadedBytes;
                for(int replay=0;replay<5;replay++)compositor.RenderScene(visual,1280,800,target.ViewPtr);
                Assert.Equal(uploaded,pipeline.UploadedBytes);
                Assert.Equal(pixels,target.ReadPixels());
                batch.EnableBackgroundOcclusion = false;
                batch.Build(game,new(1280,800),game.Time);visual.Invalidate();
                compositor.RenderScene(visual,1280,800,target.ViewPtr);
                Assert.Equal(pixels,target.ReadPixels());
                batch.EnableBackgroundOcclusion = true;
                // Exercise culling after scrolling/elevation changes, including local emitters.
                for(int tick=440;tick<2400;tick++)game.Step(RoutePilot.GetInput(game));
                batch.Build(game,new(1280,800),game.Time);visual.Invalidate();
                compositor.RenderScene(visual,1280,800,target.ViewPtr);
                var scrolled=target.ReadPixels();
                batch.EnableBackgroundOcclusion = false;
                batch.Build(game,new(1280,800),game.Time);visual.Invalidate();
                compositor.RenderScene(visual,1280,800,target.ViewPtr);
                Assert.Equal(scrolled,target.ReadPixels());
                batch.EnableBackgroundOcclusion = true;
            }
        }
        finally { WgpuContext.OnWebGpuError-=Error; }
    }

    [Fact]
    public unsafe void TitleAndPlayingUiProduceReviewableCaptures()
    {
        using var context=new WgpuContext();context.Initialize(null);
        using var compositor=new Compositor(context,TextureFormat.Rgba8Unorm);
        compositor.RegisterExtension(ProceduralDrawingContextExtensions.ExtensionId,new ProceduralPipeline());
        using var target=new GpuTexture(context,1440,900,TextureFormat.Rgba8Unorm,TextureUsage.RenderAttachment|TextureUsage.CopySrc,"Suntrail UI verification",alphaMode:GpuTextureAlphaMode.Premultiplied);
        var view=new GameView();
        view.Measure(new(1440,900));view.Arrange(new Rect(0,0,1440,900));view.UpdateAnimations(.016f);
        compositor.RenderScene(view,1440,900,target.ViewPtr);
        PngEncoder.SavePng(Path.Combine(Artifacts(),"title.png"),target.ReadPixels(),1440,900);
        view.Surface.Session.StartLevel(0);
        for(int i=0;i<60;i++)view.Surface.Session.Step(default);
        view.UpdateAnimations(.016f);view.Measure(new(1440,900));view.Arrange(new Rect(0,0,1440,900));
        compositor.RenderScene(view,1440,900,target.ViewPtr);
        PngEncoder.SavePng(Path.Combine(Artifacts(),"playing.png"),target.ReadPixels(),1440,900);
    }
    [Fact]
    public unsafe void LandscapePhoneUiAndPauseRemainVisible()
    {
        using var context = new WgpuContext(); context.Initialize(null);
        using var compositor = new Compositor(context, TextureFormat.Rgba8Unorm);
        compositor.RegisterExtension(ProceduralDrawingContextExtensions.ExtensionId, new ProceduralPipeline());
        using var target = new GpuTexture(context, 844, 390, TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Suntrail phone verification", alphaMode: GpuTextureAlphaMode.Premultiplied);
        var view = new GameView();
        foreach (string state in new[] { "phone-title", "phone-playing", "phone-paused" })
        {
            if (state == "phone-playing") view.OnKeyDown(new() { Key = Silk.NET.Input.Key.Enter });
            if (state == "phone-paused") view.Deactivate();
            for (int pass = 0; pass < 3; pass++)
            {
                view.Measure(new(844, 390)); view.Arrange(new Rect(0, 0, 844, 390)); view.UpdateAnimations(.016f);
            }
            compositor.RenderScene(view, 844, 390, target.ViewPtr);
            PngEncoder.SavePng(Path.Combine(Artifacts(), state + ".png"), target.ReadPixels(), 844, 390);
        }
        // Exercise the primary action while hovered after the menu has been hidden.
        var menu = (Microsoft.UI.Xaml.Controls.StackPanel)view.Children[4];
        var actions = (Microsoft.UI.Xaml.Controls.StackPanel)menu.Children[3];
        var primary = (Microsoft.UI.Xaml.Controls.Button)actions.Children[0];
        primary.OnPointerEntered(new());
        view.Measure(new(844, 390)); view.Arrange(new Rect(0, 0, 844, 390));
        compositor.RenderScene(view, 844, 390, target.ViewPtr);
        var hovered = target.ReadPixels();
        PngEncoder.SavePng(Path.Combine(Artifacts(), "phone-paused-hover.png"), hovered, 844, 390);
        int sample = (250 * 844 + 35) * 4;
        Assert.True(hovered[sample] > 180 && hovered[sample + 1] > 130 && hovered[sample + 2] < 140,
            "The primary hover background must preserve contrast with its dark label.");
        Assert.Equal(GameMode.Paused, view.Surface.Session.Mode);
        Assert.Equal(default, view.Surface.Input);
    }

    [Fact]
    public void KeyboardMovementJumpAndFocusLossUseNormalInput()
    {
        var view = new GameView();
        view.OnKeyDown(new() { Key = Silk.NET.Input.Key.Enter });
        for (int i = 0; i < 60; i++) view.UpdateAnimations(GameSession.StepSeconds);
        float start = view.Surface.Session.Position.X;
        view.OnKeyDown(new() { Key = Silk.NET.Input.Key.D });
        view.OnKeyDown(new() { Key = Silk.NET.Input.Key.Space });
        for (int i = 0; i < 25; i++) view.UpdateAnimations(GameSession.StepSeconds);
        Assert.True(view.Surface.Session.Position.X > start + 20);
        Assert.True(view.Surface.Session.Position.Y < 530);
        view.OnKeyUp(new() { Key = Silk.NET.Input.Key.D });
        view.OnKeyUp(new() { Key = Silk.NET.Input.Key.Space });
        Assert.Equal(0, view.Surface.Input.Move);
        Assert.False(view.Surface.Input.JumpHeld);
        view.Deactivate();
        long tick = view.Surface.Session.Tick;
        view.UpdateAnimations(1);
        Assert.Equal(tick, view.Surface.Session.Tick);
        Assert.Equal(GameMode.Paused, view.Surface.Session.Mode);
    }

    [Fact]
    public void HeldControlReleasesOnCaptureLossCancellationAndFocusLoss()
    {
        var view = new GameView();
        view.OnKeyDown(new() { Key = Silk.NET.Input.Key.Enter });
        var touch = (Microsoft.UI.Xaml.Controls.Grid)view.Children.Last();
        var movement = (Microsoft.UI.Xaml.Controls.StackPanel)touch.Children[0];
        var right = (FrameworkElement)movement.Children[1];
        right.OnPointerPressed(new());
        view.UpdateAnimations(GameSession.StepSeconds);
        Assert.Equal(1, view.Surface.Input.Move);
        right.OnPointerCaptureLost(new());
        view.UpdateAnimations(GameSession.StepSeconds);
        Assert.Equal(0, view.Surface.Input.Move);
        right.OnPointerPressed(new());
        view.Deactivate();
        Assert.Equal(default, view.Surface.Input);
        view.OnKeyDown(new() { Key = Silk.NET.Input.Key.Enter });
        right.OnPointerPressed(new());
        view.UpdateAnimations(GameSession.StepSeconds);
        Assert.Equal(1, view.Surface.Input.Move);
        right.OnPointerCanceled(new());
        view.UpdateAnimations(GameSession.StepSeconds);
        Assert.Equal(0, view.Surface.Input.Move);
    }

    private static string Artifacts()
    {
        var path=Environment.GetEnvironmentVariable("SUNTRAIL_ARTIFACTS")??Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/suntrail"));
        Directory.CreateDirectory(path);return path;
    }
    private sealed class BatchVisual(ProceduralBatch batch):FrameworkElement
    {
        public override void OnRender(DrawingContext context)=>context.DrawProceduralWorld(batch);
        protected override Vector2 MeasureOverride(Vector2 availableSize)=>availableSize;
    }
}
