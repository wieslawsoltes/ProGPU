//! Original retained composition timing, easing, keyframes and natural motion.
use crate::Error;
use std::{cmp::Ordering, sync::Arc};
use wide::f32x4 as V;

/// Original terminal/loop policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum AnimationIterationBehavior {
    /// Clamp progress to one and retain completion once reached.
    #[default]
    Single,
    /// Repeat the original duration with floating remainder.
    Forever,
}
/// An immutable easing owner; evaluation occurs only while advancing animations.
pub trait CompositionEasingFunction {
    /// Apply the original normalized-progress policy.
    fn ease(&self, progress: f32) -> f32;
}
/// Original identity easing, without extra clamping.
#[derive(Clone, Copy, Debug, Default)]
pub struct LinearEasingFunction;
impl CompositionEasingFunction for LinearEasingFunction {
    fn ease(&self, progress: f32) -> f32 {
        progress
    }
}
/// Original eight-step Newton solve and sixteen-step bisection fallback.
#[derive(Clone, Copy, Debug)]
pub struct CubicBezierEasingFunction {
    /// Original first control point, retained without sanitization.
    pub control_point1: [f32; 2],
    /// Original second control point, retained without sanitization.
    pub control_point2: [f32; 2],
}
impl CubicBezierEasingFunction {
    fn point(self, t: f32) -> [f32; 2] {
        let square = t * t;
        let inverse = 1.0 - t;
        let inverse_square = inverse * inverse;
        // X/Y are independent lanes; preserve the original product/add order.
        let result = V::splat(3.0 * inverse_square * t)
            * V::from([self.control_point1[0], self.control_point1[1], 0.0, 0.0])
            + V::splat(3.0 * inverse * square)
                * V::from([self.control_point2[0], self.control_point2[1], 0.0, 0.0])
            + V::splat(square * t);
        let [x, y, _, _] = result.to_array();
        [x, y]
    }
    fn slope(self, t: f32) -> f32 {
        let inverse = 1.0 - t;
        3.0 * inverse * inverse * self.control_point1[0]
            + 6.0 * inverse * t * (self.control_point2[0] - self.control_point1[0])
            + 3.0 * t * t * (1.0 - self.control_point2[0])
    }
    fn parameter(self, x: f32) -> f32 {
        let mut t = x;
        for _ in 0..8 {
            let current = self.point(t)[0] - x;
            let slope = self.slope(t);
            if slope.abs() < 1e-6 {
                break;
            }
            t -= current / slope;
        }
        if t < 0.0 || t > 1.0 {
            let mut low = 0.0;
            let mut high = 1.0;
            t = x;
            for _ in 0..16 {
                let current = self.point(t)[0];
                if (current - x).abs() < 1e-6 {
                    break;
                }
                if x > current {
                    low = t;
                } else {
                    high = t;
                }
                t = (low + high) * 0.5;
            }
        }
        t.clamp(0.0, 1.0)
    }
}
impl CompositionEasingFunction for CubicBezierEasingFunction {
    fn ease(&self, progress: f32) -> f32 {
        if progress <= 0.0 {
            0.0
        } else if progress >= 1.0 {
            1.0
        } else {
            self.point(self.parameter(progress))[1]
        }
    }
}
/// Original floor-based steps, including an exact terminal one.
#[derive(Clone, Copy, Debug)]
pub struct StepEasingFunction {
    steps: i32,
}
impl StepEasingFunction {
    /// Preserve the original minimum step count of one.
    pub fn new(steps: i32) -> Self {
        Self {
            steps: steps.max(1),
        }
    }
    /// Original normalized positive step count.
    pub fn step_count(self) -> i32 {
        self.steps
    }
}
impl CompositionEasingFunction for StepEasingFunction {
    fn ease(&self, progress: f32) -> f32 {
        if progress <= 0.0 {
            return 0.0;
        }
        if progress >= 1.0 {
            return 1.0;
        }
        let width = 1.0 / self.steps as f32;
        (progress / width).floor() / self.steps as f32
    }
}
/// Typed composition animation; advancing time never performs GPU work.
pub trait CompositionAnimation {
    /// Original retained value type.
    type Value;
    /// Current immutable value.
    fn current_value(&self) -> &Self::Value;
    /// Original latched completion flag.
    fn is_completed(&self) -> bool;
    /// Advance by the caller's original elapsed seconds, including negative/NaN.
    fn tick(&mut self, elapsed_seconds: f32);
}
/// Exact original TimeSpan tick inventory (one tick is 100 ns).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct KeyFrameTiming {
    /// Original duration ticks; zero and negative durations retain terminal policy.
    pub duration_ticks: i64,
    /// Original delay ticks, including negative delays.
    pub delay_ticks: i64,
    /// Independent terminal/loop policy.
    pub iteration: AnimationIterationBehavior,
}
impl Default for KeyFrameTiming {
    fn default() -> Self {
        Self {
            duration_ticks: 10_000_000,
            delay_ticks: 0,
            iteration: AnimationIterationBehavior::Single,
        }
    }
}
mod sealed {
    pub trait Value {}
    impl Value for f32 {}
    impl Value for [f32; 2] {}
    impl Value for [f32; 3] {}
}
/// Original scalar/Vector2/Vector3 interpolation contracts.
pub trait AnimationValue: sealed::Value + Copy + Default {
    /// Original family-specific interpolation, without sanitizing values.
    fn interpolate(start: Self, end: Self, progress: f32) -> Self;
}
impl AnimationValue for f32 {
    fn interpolate(start: Self, end: Self, t: f32) -> Self {
        start + (end - start) * t
    }
}
fn vector_lerp(start: [f32; 4], end: [f32; 4], t: f32) -> [f32; 4] {
    // Independent original vector lanes, retaining the public Vector.Lerp
    // weighted-sum contract. Runtime fused arithmetic is a separate source gate.
    (V::from(start) * V::splat(1.0 - t) + V::from(end) * V::splat(t)).to_array()
}
impl AnimationValue for [f32; 2] {
    fn interpolate(a: Self, b: Self, t: f32) -> Self {
        let [x, y, _, _] = vector_lerp([a[0], a[1], 0.0, 0.0], [b[0], b[1], 0.0, 0.0], t);
        [x, y]
    }
}
impl AnimationValue for [f32; 3] {
    fn interpolate(a: Self, b: Self, t: f32) -> Self {
        let [x, y, z, _] = vector_lerp([a[0], a[1], a[2], 0.0], [b[0], b[1], b[2], 0.0], t);
        [x, y, z]
    }
}
struct KeyFrame<T> {
    progress: f32,
    value: T,
    easing: Option<Arc<dyn CompositionEasingFunction>>,
}
/// Original typed keyframe generation and independently mutable playback clock.
/// Duplicate/NaN progress retains the original comparer and first-interval route.
pub struct KeyFrameAnimation<T: AnimationValue> {
    /// Original delay, duration and iteration configuration.
    pub timing: KeyFrameTiming,
    keyframes: Vec<KeyFrame<T>>,
    current: T,
    accumulated: f32,
    completed: bool,
}
impl<T: AnimationValue> Default for KeyFrameAnimation<T> {
    fn default() -> Self {
        Self {
            timing: Default::default(),
            keyframes: Vec::new(),
            current: T::default(),
            accumulated: 0.0,
            completed: false,
        }
    }
}
/// Original scalar keyframe family.
pub type ScalarKeyFrameAnimation = KeyFrameAnimation<f32>;
/// Original two-lane keyframe family.
pub type Vector2KeyFrameAnimation = KeyFrameAnimation<[f32; 2]>;
/// Original three-lane keyframe family.
pub type Vector3KeyFrameAnimation = KeyFrameAnimation<[f32; 3]>;
impl<T: AnimationValue> KeyFrameAnimation<T> {
    /// Insert original clamped progress and optional target-key easing. Allocation
    /// failure leaves the entire prior generation and playback state unchanged.
    pub fn insert_keyframe(
        &mut self,
        progress: f32,
        value: T,
        easing: Option<Arc<dyn CompositionEasingFunction>>,
    ) -> Result<(), Error> {
        self.keyframes.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.keyframes.push(KeyFrame {
            progress: progress.clamp(0.0, 1.0),
            value,
            easing,
        });
        self.keyframes
            .sort_unstable_by(|a, b| compare(a.progress, b.progress));
        Ok(())
    }
    /// Original stored key count, including repeated progress values.
    pub fn keyframe_count(&self) -> usize {
        self.keyframes.len()
    }
    /// Original accumulated binary32 seconds; empty animations do not advance.
    pub fn accumulated_time(&self) -> f32 {
        self.accumulated
    }
    fn value_at(&self, progress: f32) -> T {
        let keys = &self.keyframes;
        if keys.is_empty() {
            return T::default();
        }
        if keys.len() == 1 {
            return keys[0].value;
        }
        if progress <= keys[0].progress {
            return keys[0].value;
        }
        if progress >= keys[keys.len() - 1].progress {
            return keys[keys.len() - 1].value;
        }
        let index = keys
            .windows(2)
            .position(|k| progress >= k[0].progress && progress <= k[1].progress)
            .unwrap_or(0);
        let a = &keys[index];
        let b = &keys[index + 1];
        let range = b.progress - a.progress;
        let mut t = if range <= 0.0 {
            1.0
        } else {
            (progress - a.progress) / range
        };
        if let Some(easing) = &b.easing {
            t = easing.ease(t);
        }
        T::interpolate(a.value, b.value, t)
    }
}
fn compare(a: f32, b: f32) -> Ordering {
    if a < b {
        Ordering::Less
    } else if a > b {
        Ordering::Greater
    } else if a == b {
        Ordering::Equal
    } else if a.is_nan() {
        if b.is_nan() {
            Ordering::Equal
        } else {
            Ordering::Less
        }
    } else {
        Ordering::Greater
    }
}
impl<T: AnimationValue> CompositionAnimation for KeyFrameAnimation<T> {
    type Value = T;
    fn current_value(&self) -> &T {
        &self.current
    }
    fn is_completed(&self) -> bool {
        self.completed
    }
    fn tick(&mut self, elapsed: f32) {
        if self.keyframes.is_empty() {
            self.current = T::default();
            return;
        }
        self.accumulated += elapsed;
        let delay = (self.timing.delay_ticks as f64 / 10_000_000.0) as f32;
        let duration = (self.timing.duration_ticks as f64 / 10_000_000.0) as f32;
        let progress = if self.accumulated < delay {
            0.0
        } else {
            let mut time = self.accumulated - delay;
            if duration <= 0.0 {
                if self.timing.iteration == AnimationIterationBehavior::Single {
                    self.completed = true;
                }
                1.0
            } else if time >= duration {
                if self.timing.iteration == AnimationIterationBehavior::Forever {
                    time %= duration;
                    time / duration
                } else {
                    self.completed = true;
                    1.0
                }
            } else {
                time / duration
            }
        };
        self.current = self.value_at(progress);
    }
}
/// Original underdamped, critical and overdamped scalar natural motion.
#[derive(Clone, Debug)]
pub struct SpringScalarNaturalMotionAnimation {
    current: f32,
    completed: bool,
    accumulated: f32,
    damping: f32,
    period: f32,
    velocity: f32,
    initial: f32,
    final_value: f32,
}
impl Default for SpringScalarNaturalMotionAnimation {
    fn default() -> Self {
        Self {
            current: 0.0,
            completed: false,
            accumulated: 0.0,
            damping: 0.5,
            period: 0.5,
            velocity: 0.0,
            initial: 0.0,
            final_value: 1.0,
        }
    }
}
impl SpringScalarNaturalMotionAnimation {
    fn reset(&mut self) {
        self.accumulated = 0.0;
        self.completed = false;
    }
    /// Original damping ratio; values are not clamped to a motion family.
    pub fn damping_ratio(&self) -> f32 {
        self.damping
    }
    /// Change damping and restart only when the original float comparison differs.
    pub fn set_damping_ratio(&mut self, value: f32) {
        if self.damping != value {
            self.damping = value;
            self.reset();
        }
    }
    /// Original period in seconds.
    pub fn period(&self) -> f32 {
        self.period
    }
    /// Change period and restart, preserving the original nonpositive policy.
    pub fn set_period(&mut self, value: f32) {
        if self.period != value {
            self.period = value;
            self.reset();
        }
    }
    /// Original initial velocity.
    pub fn initial_velocity(&self) -> f32 {
        self.velocity
    }
    /// Change velocity and restart without changing the current painted value.
    pub fn set_initial_velocity(&mut self, value: f32) {
        if self.velocity != value {
            self.velocity = value;
            self.reset();
        }
    }
    /// Original initial scalar value.
    pub fn initial_value(&self) -> f32 {
        self.initial
    }
    /// Change the initial value, update the current value and restart.
    pub fn set_initial_value(&mut self, value: f32) {
        if self.initial != value {
            self.initial = value;
            self.current = value;
            self.reset();
        }
    }
    /// Original final target scalar.
    pub fn final_value(&self) -> f32 {
        self.final_value
    }
    /// Change the target and restart, retaining the current painted value.
    pub fn set_final_value(&mut self, value: f32) {
        if self.final_value != value {
            self.final_value = value;
            self.reset();
        }
    }
}
impl CompositionAnimation for SpringScalarNaturalMotionAnimation {
    type Value = f32;
    fn current_value(&self) -> &f32 {
        &self.current
    }
    fn is_completed(&self) -> bool {
        self.completed
    }
    fn tick(&mut self, elapsed: f32) {
        if self.completed {
            self.current = self.final_value;
            return;
        }
        self.accumulated += elapsed;
        let t = self.accumulated;
        let x0 = self.initial;
        let xf = self.final_value;
        let v0 = self.velocity;
        let damping = self.damping;
        let period = self.period;
        if period <= 0.0 {
            self.current = xf;
            self.completed = true;
            return;
        }
        let omega = (2.0 * std::f32::consts::PI) / period;
        let (x, v) = if damping < 1.0 {
            let frequency = omega * (1.0 - damping * damping).sqrt();
            let a = x0 - xf;
            let b = (v0 + damping * omega * a) / frequency;
            let exp = (-damping * omega * t).exp();
            let cos = (frequency * t).cos();
            let sin = (frequency * t).sin();
            (
                xf + exp * (a * cos + b * sin),
                exp * ((b * frequency - damping * omega * a) * cos
                    - (a * frequency + damping * omega * b) * sin),
            )
        } else if (damping - 1.0).abs() < 1e-5 {
            let a = x0 - xf;
            let b = v0 + omega * a;
            let exp = (-omega * t).exp();
            (
                xf + exp * (a + b * t),
                exp * (b - omega * a - omega * b * t),
            )
        } else {
            let frequency = omega * (damping * damping - 1.0).sqrt();
            let gamma1 = -damping * omega + frequency;
            let gamma2 = -damping * omega - frequency;
            let a = x0 - xf;
            let c1 = (v0 - a * gamma2) / (gamma1 - gamma2);
            let c2 = a - c1;
            let exp1 = (gamma1 * t).exp();
            let exp2 = (gamma2 * t).exp();
            (
                xf + c1 * exp1 + c2 * exp2,
                c1 * gamma1 * exp1 + c2 * gamma2 * exp2,
            )
        };
        self.current = x;
        if (x - xf).abs() < 1e-3 && v.abs() < 1e-3 {
            self.current = xf;
            self.completed = true;
        }
    }
}
/// Original explicit expression callback, evaluated once per tick, never while
/// replaying a prepared GPU draw. A typed result replaces source object boxing.
pub struct ExpressionAnimation<T: Default> {
    expression: Option<Box<dyn FnMut() -> T>>,
    current: T,
}
impl<T: Default> Default for ExpressionAnimation<T> {
    fn default() -> Self {
        Self {
            expression: None,
            current: T::default(),
        }
    }
}
impl<T: Default> ExpressionAnimation<T> {
    /// Replace the expression without eagerly evaluating it or changing its value.
    pub fn set_expression(&mut self, expression: impl FnMut() -> T + 'static) {
        self.expression = Some(Box::new(expression));
    }
}
impl<T: Default> CompositionAnimation for ExpressionAnimation<T> {
    type Value = T;
    fn current_value(&self) -> &T {
        &self.current
    }
    fn is_completed(&self) -> bool {
        false
    }
    fn tick(&mut self, _elapsed: f32) {
        if let Some(expression) = &mut self.expression {
            self.current = expression();
        }
    }
}
