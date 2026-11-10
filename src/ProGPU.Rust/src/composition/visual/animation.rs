use super::*;
use crate::scene::animation::CompositionAnimation;

/// Original properties recognized by Scene.Visual.TickAnimations. Using typed
/// names keeps unknown source properties distinct from a supported binding.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AnimationProperty {
    Opacity,
    Rotation,
    Offset,
    Size,
    Scale,
}
impl AnimationProperty {
    /// Original ASCII property names compare without case; other names are not
    /// silently interpreted as a different known property.
    pub fn from_name(name: &str) -> Option<Self> {
        [
            ("opacity", Self::Opacity),
            ("rotation", Self::Rotation),
            ("offset", Self::Offset),
            ("size", Self::Size),
            ("scale", Self::Scale),
        ]
        .into_iter()
        .find_map(|(key, value)| name.eq_ignore_ascii_case(key).then_some(value))
    }
}
type Shared<T> = Rc<RefCell<Box<dyn CompositionAnimation<Value = T>>>>;
/// Actual shared animation object. Binding the same handle twice retains the
/// source behavior of ticking that same animation once for each active binding.
#[derive(Clone)]
pub enum VisualAnimation {
    /// Scalar key-frame, expression or natural-motion animation.
    Scalar(Shared<f32>),
    /// Original two-component animation values.
    Vector2(Shared<[f32; 2]>),
    /// Original three-component animation values.
    Vector3(Shared<[f32; 3]>),
}
impl VisualAnimation {
    /// Retain any original scalar animation implementation without boxing values.
    pub fn scalar(value: impl CompositionAnimation<Value = f32> + 'static) -> Self {
        Self::Scalar(Rc::new(RefCell::new(Box::new(value))))
    }
    /// Retain an original two-axis animation.
    pub fn vector2(value: impl CompositionAnimation<Value = [f32; 2]> + 'static) -> Self {
        Self::Vector2(Rc::new(RefCell::new(Box::new(value))))
    }
    /// Retain an original three-axis animation.
    pub fn vector3(value: impl CompositionAnimation<Value = [f32; 3]> + 'static) -> Self {
        Self::Vector3(Rc::new(RefCell::new(Box::new(value))))
    }
    fn tick(&self, elapsed: f32) -> Result<(Value, bool), Error> {
        match self {
            Self::Scalar(v) => {
                let mut a = v.try_borrow_mut().map_err(|_| Error::InvalidInput)?;
                a.tick(elapsed);
                Ok((Value::Scalar(*a.current_value()), a.is_completed()))
            }
            Self::Vector2(v) => {
                let mut a = v.try_borrow_mut().map_err(|_| Error::InvalidInput)?;
                a.tick(elapsed);
                Ok((Value::Vector2(*a.current_value()), a.is_completed()))
            }
            Self::Vector3(v) => {
                let mut a = v.try_borrow_mut().map_err(|_| Error::InvalidInput)?;
                a.tick(elapsed);
                Ok((Value::Vector3(*a.current_value()), a.is_completed()))
            }
        }
    }
    fn same_owner(&self, other: &Self) -> bool {
        match (self, other) {
            (Self::Scalar(a), Self::Scalar(b)) => Rc::ptr_eq(a, b),
            (Self::Vector2(a), Self::Vector2(b)) => Rc::ptr_eq(a, b),
            (Self::Vector3(a), Self::Vector3(b)) => Rc::ptr_eq(a, b),
            _ => false,
        }
    }
}
enum Value {
    Scalar(f32),
    Vector2([f32; 2]),
    Vector3([f32; 3]),
}

impl Visual {
    /// Start/replace one original property animation without eagerly ticking or
    /// resetting it. A type mismatch remains a no-op value binding while ticking.
    pub fn start_animation(&self, property: AnimationProperty, animation: VisualAnimation) {
        let was_empty = {
            let mut node = self.0.borrow_mut();
            let empty = node.animations.is_empty();
            if let Some(entry) = node.animations.iter_mut().find(|entry| entry.0 == property) {
                entry.1 = animation;
            } else {
                node.animations.push((property, animation));
            }
            empty
        };
        if was_empty {
            self.adjust_activity(1);
        }
        self.invalidate_visual_state();
    }
    /// Remove only this binding, retaining its current painted property value.
    pub fn stop_animation(&self, property: AnimationProperty) {
        let empty = {
            let mut node = self.0.borrow_mut();
            let Some(index) = node.animations.iter().position(|entry| entry.0 == property) else {
                return;
            };
            node.animations.remove(index);
            node.animations.is_empty()
        };
        if empty {
            self.adjust_activity(-1);
        }
        self.invalidate_visual_state();
    }
    /// Number of active built-in/custom animation owners in this actual subtree.
    pub fn active_animation_subtree_count(&self) -> usize {
        self.0.borrow().active_subtree
    }
    /// Bind a control-owned update callback. It runs before ordinary animations,
    /// outside node/resource borrows and only during explicit preparation ticks.
    pub fn set_custom_animation(&self, callback: Option<Rc<RefCell<dyn FnMut(f32)>>>) {
        let delta = {
            let mut node = self.0.borrow_mut();
            let old = node.custom_animation.is_some();
            let new = callback.is_some();
            node.custom_animation = callback;
            (if new { 1 } else { 0 }) - (if old { 1 } else { 0 })
        };
        self.adjust_activity(delta);
    }
    pub(super) fn adjust_activity(&self, delta: isize) {
        if delta == 0 {
            return;
        }
        let mut current = Some(self.clone());
        while let Some(visual) = current {
            let mut node = visual.0.borrow_mut();
            node.active_subtree = node
                .active_subtree
                .checked_add_signed(delta)
                .expect("balanced visual animation ownership");
            current = node.parent.upgrade().map(Visual);
        }
    }
    /// Tick local bindings in insertion order, publish one invalidation, then
    /// retire completed bindings. Size animation intentionally follows the
    /// original direct-field route without parent-size callbacks.
    pub fn tick_animations(&self, elapsed_seconds: f32) -> Result<(), Error> {
        let animations = self.0.borrow().animations.clone();
        let mut values = Vec::with_capacity(animations.len());
        for (property, animation) in &animations {
            let (value, done) = animation.tick(elapsed_seconds)?;
            values.push((*property, animation.clone(), value, done));
        }
        let (changed, content, emptied) = {
            let mut node = self.0.borrow_mut();
            let previously_active = !node.animations.is_empty();
            let mut changed = false;
            let mut content = false;
            for (property, animation, value, done) in values {
                // Expression callbacks may replace a binding. Never erase that
                // replacement or publish the retired animation's value into it.
                let Some(index) = node
                    .animations
                    .iter()
                    .position(|entry| entry.0 == property && entry.1.same_owner(&animation))
                else {
                    continue;
                };
                let state = &mut node.properties;
                match (property, value) {
                    (AnimationProperty::Opacity, Value::Scalar(v)) => {
                        if state.opacity != v {
                            state.opacity = v;
                            changed = true;
                        }
                    }
                    (AnimationProperty::Rotation, Value::Scalar(v)) => {
                        if state.rotation != v {
                            state.rotation = v;
                            changed = true;
                        }
                    }
                    (AnimationProperty::Offset, Value::Vector2(v)) => {
                        if state.offset != v {
                            state.offset = v;
                            changed = true;
                        }
                    }
                    (AnimationProperty::Size, Value::Vector2(v)) => {
                        if state.size != v {
                            state.size = v;
                            changed = true;
                            content = true;
                        }
                    }
                    (AnimationProperty::Scale, Value::Vector3(v)) => {
                        if state.scale != v {
                            state.scale = v;
                            changed = true;
                        }
                    }
                    (AnimationProperty::Scale, Value::Vector2([x, y])) => {
                        let v = [x, y, 1.0];
                        if state.scale != v {
                            state.scale = v;
                            changed = true;
                        }
                    }
                    _ => {}
                }
                if done {
                    node.animations.remove(index);
                }
            }
            (
                changed,
                content,
                previously_active && node.animations.is_empty(),
            )
        };
        if changed {
            self.invalidate_core(content);
        }
        if emptied {
            self.adjust_activity(-1);
        }
        Ok(())
    }
    /// Traverse only active branches, original custom-before-built-in-before-
    /// children order. Child identities are retained across callback mutation.
    pub fn update_animations(&self, elapsed_seconds: f32) -> Result<(), Error> {
        let custom = {
            let node = self.0.borrow();
            if node.active_subtree == 0 {
                return Ok(());
            }
            node.custom_animation.clone()
        };
        if let Some(custom) = custom {
            custom.try_borrow_mut().map_err(|_| Error::InvalidInput)?(elapsed_seconds);
        }
        self.tick_animations(elapsed_seconds)?;
        for child in self.children() {
            if child.active_animation_subtree_count() != 0 {
                child.update_animations(elapsed_seconds)?;
            }
        }
        Ok(())
    }
}
