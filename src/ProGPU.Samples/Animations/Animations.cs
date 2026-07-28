using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Scene;

namespace ProGPU.Samples;

public interface IAnimatedElement
{
    void Update(float delta);
}

public static class VisualExtensions
{
    private static readonly ConditionalWeakTable<Visual, AnimationRegistry> Registries = new();

    public static void UpdateSampleAnimations(this Visual visual, float delta)
    {
        if (visual == null)
            return;

        Registries.GetValue(visual, static _ => new AnimationRegistry())
            .Update(visual, delta);
    }

    private sealed class AnimationRegistry
    {
        private readonly List<IAnimatedElement> _elements = new();
        private long _treeVersion = -1;

        public void Update(Visual root, float delta)
        {
            if (_treeVersion != root.TreeVersion)
            {
                _elements.Clear();
                Collect(root);
                _treeVersion = root.TreeVersion;
            }

            for (var index = 0; index < _elements.Count; index++)
                _elements[index].Update(delta);
        }

        private void Collect(Visual visual)
        {
            if (visual is IAnimatedElement animated)
                _elements.Add(animated);

            if (visual is not ContainerVisual container)
                return;

            var children = container.Children;
            for (var index = 0; index < children.Count; index++)
                Collect(children[index]);
        }
    }
}

public class SpringScalarNaturalMotionAnimation
{
    public float TargetValue { get; set; }
    public float CurrentValue { get; set; }
    public float Velocity { get; set; }
    public float Stiffness { get; set; } = 150f;
    public float Damping { get; set; } = 15f;
    public float Mass { get; set; } = 1f;

    public void Update(float delta)
    {
        if (delta > 0.1f) delta = 0.1f;

        float force = -Stiffness * (CurrentValue - TargetValue) - Damping * Velocity;
        float acceleration = force / Mass;
        Velocity += acceleration * delta;
        CurrentValue += Velocity * delta;
    }
}

public class ExpressionAnimation
{
    private readonly Func<float> _expression;

    public ExpressionAnimation(Func<float> expression)
    {
        _expression = expression;
    }

    public float Evaluate() => _expression();
}

public class KeyframeAnimation<T>
{
    public List<(float Key, T Value)> Keyframes { get; } = new();
    public float Duration { get; set; } = 1f;
    public bool Loop { get; set; } = true;
    public float Time { get; set; }

    public void Update(float delta)
    {
        Time += delta;
        if (Time > Duration)
        {
            if (Loop)
            {
                Time %= Duration;
            }
            else
            {
                Time = Duration;
            }
        }
    }

    public T Evaluate(Func<T, T, float, T> interpolator)
    {
        if (Keyframes.Count == 0) return default!;
        if (Keyframes.Count == 1) return Keyframes[0].Value;

        float normalizedTime = Time / Duration;

        int nextIndex = 0;
        while (nextIndex < Keyframes.Count && Keyframes[nextIndex].Key < normalizedTime)
        {
            nextIndex++;
        }

        if (nextIndex == 0) return Keyframes[0].Value;
        if (nextIndex >= Keyframes.Count) return Keyframes[Keyframes.Count - 1].Value;

        var prev = Keyframes[nextIndex - 1];
        var next = Keyframes[nextIndex];

        float segmentDuration = next.Key - prev.Key;
        float t = segmentDuration > 0 ? (normalizedTime - prev.Key) / segmentDuration : 0f;

        return interpolator(prev.Value, next.Value, t);
    }
}
