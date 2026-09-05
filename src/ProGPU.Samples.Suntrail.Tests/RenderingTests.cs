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
                pipeline.EnableSpecializedShaders = false;
                compositor.RenderScene(visual,1280,800,target.ViewPtr);
                Assert.Equal(pixels,target.ReadPixels());
                pipeline.EnableSpecializedShaders = true;
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
    public unsafe void UndergroundRoomsRenderWithTheirWorldMaterials()
    {
        using var context = new WgpuContext(); context.Initialize(null);
        using var compositor = new Compositor(context, TextureFormat.Rgba8Unorm);
        compositor.RegisterExtension(ProceduralDrawingContextExtensions.ExtensionId, new ProceduralPipeline());
        using var target = new GpuTexture(context, 1280, 800, TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Suntrail vault verification", alphaMode: GpuTextureAlphaMode.Premultiplied);
        var signatures = new HashSet<string>();
        for (int world = 0; world < 8; world++)
        {
            var game = new GameSession(); game.StartLevel(world);
            for (int tick = 0; tick < 2000 && !game.CanUsePipe; tick++)
            {
                float distance = 615 - game.Position.X;
                game.Step(new(Math.Clamp(distance * .07f, -1, 1), true, game.Grounded && Math.Abs(distance) < 190, false));
            }
            Assert.True(game.CanUsePipe);
            game.Step(new(0, false, false, false, true));
            Assert.True(game.Level.IsDungeon);
            for (int tick = 0; tick < 550; tick++) game.Step(RoutePilot.GetInput(game));
            var batch = new ProceduralBatch(); batch.Build(game, new(1280, 800), game.Time);
            var visual = new BatchVisual(batch);
            visual.Measure(new(1280, 800)); visual.Arrange(new Rect(0, 0, 1280, 800));
            compositor.RenderScene(visual, 1280, 800, target.ViewPtr);
            var pixels = target.ReadPixels();
            Assert.True(pixels.Where((_, i) => i % 4 != 3).Distinct().Count() > 100);
            signatures.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)));
            PngEncoder.SavePng(Path.Combine(Artifacts(), $"vault-{world + 1}.png"), pixels, 1280, 800);
        }
        Assert.Equal(8, signatures.Count);
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
            if (state == "phone-playing")
            {
                var previousInput = Microsoft.UI.Xaml.Input.InputSystem.Current;
                Microsoft.UI.Xaml.Input.InputSystem.Current = Microsoft.UI.Xaml.Input.InputSystem.CreateExternalState(view);
                try
                {
                    var touchPanel = (Microsoft.UI.Xaml.Controls.Grid)view.Children.Last();
                    var stick = (TouchStick)touchPanel.Children[2];
                    var start = Vector2.Transform(new Vector2(76, 80), stick.GetGlobalCoordinateTransformMatrix());
                    void Send(Microsoft.UI.Xaml.Input.PointerInputKind kind, Vector2 point) =>
                        Microsoft.UI.Xaml.Input.InputSystem.InjectPointer(new(kind, 10,
                            Windows.Devices.Input.PointerDeviceType.Touch, point, 1_000_000,
                            IsInContact: kind != Microsoft.UI.Xaml.Input.PointerInputKind.Canceled));
                    Send(Microsoft.UI.Xaml.Input.PointerInputKind.Pressed, start);
                    view.Measure(new(844, 390)); view.Arrange(new Rect(0, 0, 844, 390));
                    compositor.RenderScene(view, 844, 390, target.ViewPtr);
                    var centered = target.ReadPixels();
                    PngEncoder.SavePng(Path.Combine(Artifacts(), "phone-stick-center.png"), centered, 844, 390);
                    Send(Microsoft.UI.Xaml.Input.PointerInputKind.Moved, start + new Vector2(48, -18));
                    view.Measure(new(844, 390)); view.Arrange(new Rect(0, 0, 844, 390));
                    compositor.RenderScene(view, 844, 390, target.ViewPtr);
                    var dragged = target.ReadPixels();
                    Assert.False(centered.AsSpan().SequenceEqual(dragged), "Dragging must repaint the retained thumb visual without advancing the game.");
                    PngEncoder.SavePng(Path.Combine(Artifacts(), "phone-stick-drag.png"), dragged, 844, 390);
                    Send(Microsoft.UI.Xaml.Input.PointerInputKind.Canceled, start);
                }
                finally { Microsoft.UI.Xaml.Input.InputSystem.Current = previousInput; }
            }
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
        var settingsButton = (Microsoft.UI.Xaml.Controls.Button)actions.Children[2];
        settingsButton.OnKeyDown(new() { Key = Silk.NET.Input.Key.Enter });
        view.OnKeyDown(new() { Key = Silk.NET.Input.Key.Space });
        Assert.Equal(GameMode.Paused, view.Surface.Session.Mode);
        for (int pass = 0; pass < 3; pass++)
        {
            view.Measure(new(844, 390)); view.Arrange(new Rect(0, 0, 844, 390));
        }
        compositor.RenderScene(view, 844, 390, target.ViewPtr);
        PngEncoder.SavePng(Path.Combine(Artifacts(), "phone-settings.png"), target.ReadPixels(), 844, 390);
        view.OnKeyDown(new() { Key = Silk.NET.Input.Key.Escape });
        Assert.Equal(GameMode.Paused, view.Surface.Session.Mode);
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
