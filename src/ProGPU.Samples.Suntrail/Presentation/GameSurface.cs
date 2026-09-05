using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Samples.Suntrail.Game;
using ProGPU.Samples.Suntrail.Rendering;

namespace ProGPU.Samples.Suntrail.Presentation;

public sealed class GameSurface : FrameworkElement
{
    public GameSurface() => SetCustomAnimationActive(true);
    public GameSession Session { get; } = new();
    public ProceduralBatch Batch { get; } = new();
    public GameInput Input { get; set; }
    public bool AutoPlay { get; set; }
    // Opt-in measurement clock: identical simulation poses across render-speed comparisons.
    public bool FixedAutoPlayStep { get; set; }
    public event Action? Updated;
    private float _atmosphere;
    private uint _revision = uint.MaxValue;
    private Vector2 _builtSize;

    protected override Vector2 MeasureOverride(Vector2 availableSize) => new(float.IsFinite(availableSize.X) ? availableSize.X : 1280, float.IsFinite(availableSize.Y) ? availableSize.Y : 800);
    protected override void OnUpdateAnimations(float elapsedSeconds)
    {
        base.OnUpdateAnimations(elapsedSeconds);
        if (Visibility == Visibility.Collapsed) return;
        if (AutoPlay)
        {
            if (Session.Mode != GameMode.Playing) Session.Continue();
            Input = RoutePilot.GetInput(Session);
        }
        float gameElapsed = AutoPlay && FixedAutoPlayStep ? 1f / 60 : elapsedSeconds;
        Session.Advance(gameElapsed, Input);
        Input = Input with { JumpPressed = false, InteractPressed = false };
        bool animate = Session.Mode is GameMode.Playing or GameMode.Title;
        if (animate) _atmosphere += Math.Clamp(gameElapsed, 0, .1f);
        if (animate || _revision != Session.Revision || _builtSize != Size)
        {
            Batch.Build(Session, Size, _atmosphere);
            _revision = Session.Revision; _builtSize = Size;
            Invalidate();
        }
        Updated?.Invoke();
    }
    public override void OnRender(DrawingContext context)
    {
        if (Batch.Count > 0) context.DrawProceduralWorld(Batch);
    }
}
