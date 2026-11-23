use super::*;
use crate::{
    geometry::Affine,
    image::ImageColorMatrix,
    scene::LayerBlend,
    vector::{
        Brush, BrushKind, GradientInterpolation, GradientOptions, GradientSpread, GradientStop,
        LinearGradient, RadialGradient,
    },
};

#[derive(Clone)]
pub(super) enum Subscriber {
    Visual(Weak<RefCell<Node>>),
    Shape(Weak<RefCell<shapes::ShapeNode>>),
    Brush(Weak<RefCell<ResourceState<CompositionBrushValue>>>),
}
impl Subscriber {
    fn same(&self, other: &Self) -> bool {
        match (self, other) {
            (Self::Visual(a), Self::Visual(b)) => a.ptr_eq(b),
            (Self::Shape(a), Self::Shape(b)) => a.ptr_eq(b),
            (Self::Brush(a), Self::Brush(b)) => a.ptr_eq(b),
            _ => false,
        }
    }
    fn is_alive(&self) -> bool {
        match self {
            Self::Visual(v) => v.strong_count() != 0,
            Self::Shape(v) => v.strong_count() != 0,
            Self::Brush(v) => v.strong_count() != 0,
        }
    }
    fn notify(self) {
        match self {
            Self::Visual(v) => {
                if let Some(v) = v.upgrade() {
                    Visual(v).invalidate();
                }
            }
            Self::Shape(v) => {
                if let Some(v) = v.upgrade() {
                    CompositionShape(v).invalidate();
                }
            }
            Self::Brush(v) => {
                if let Some(v) = v.upgrade() {
                    CompositionResource(v).invalidate();
                }
            }
        }
    }
}
struct Subscription {
    owner: Subscriber,
    bindings: usize,
}
pub(super) struct ResourceState<T> {
    domain: Rc<Domain>,
    value: T,
    revision: i64,
    owners: Vec<Subscription>,
}
/// Shared mutable composition resource whose published values are immutable
/// snapshots. Subscribers are weak, counted per actual binding and notified once.
pub struct CompositionResource<T>(pub(super) Rc<RefCell<ResourceState<T>>>);
impl<T> Clone for CompositionResource<T> {
    fn clone(&self) -> Self {
        Self(self.0.clone())
    }
}
impl<T> PartialEq for CompositionResource<T> {
    fn eq(&self, other: &Self) -> bool {
        Rc::ptr_eq(&self.0, &other.0)
    }
}
impl<T> Eq for CompositionResource<T> {}
impl<T> std::fmt::Debug for CompositionResource<T> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CompositionResource")
            .field("identity", &Rc::as_ptr(&self.0))
            .field("revision", &self.0.borrow().revision)
            .finish()
    }
}
impl<T: Clone> CompositionResource<T> {
    pub(super) fn new(domain: Rc<Domain>, value: T) -> Self {
        Self(Rc::new(RefCell::new(ResourceState {
            domain,
            value,
            revision: 0,
            owners: Vec::new(),
        })))
    }
    /// Snapshot the current descriptor while retaining any referenced generations.
    pub fn value(&self) -> T {
        self.0.borrow().value.clone()
    }
    /// Original monotonically changing resource generation, with source overflow policy.
    pub fn revision(&self) -> i64 {
        self.0.borrow().revision
    }
    /// Actual owner identity, independent from equality of current descriptors.
    pub fn same_owner(&self, other: &Self) -> bool {
        self == other
    }
    pub(super) fn ensure_domain(&self, domain: &Rc<Domain>) -> Result<(), Error> {
        if Rc::ptr_eq(&self.0.borrow().domain, domain) {
            Ok(())
        } else {
            Err(Error::ResourceDomain)
        }
    }
    pub(super) fn add_owner(&self, owner: Subscriber) {
        let mut value = self.0.borrow_mut();
        value.owners.retain(|v| v.owner.is_alive());
        if let Some(existing) = value.owners.iter_mut().find(|v| v.owner.same(&owner)) {
            existing.bindings += 1;
        } else {
            value.owners.push(Subscription { owner, bindings: 1 });
        }
    }
    pub(super) fn remove_owner(&self, owner: &Subscriber) {
        let mut value = self.0.borrow_mut();
        if let Some(index) = value.owners.iter().position(|v| v.owner.same(owner)) {
            if value.owners[index].bindings > 1 {
                value.owners[index].bindings -= 1;
            } else {
                value.owners.remove(index);
            }
        }
        value.owners.retain(|v| v.owner.is_alive());
    }
    pub(super) fn invalidate(&self) {
        let owners = {
            let mut value = self.0.borrow_mut();
            increment(&mut value.revision);
            value.owners.retain(|v| v.owner.is_alive());
            value
                .owners
                .iter()
                .rev()
                .map(|v| v.owner.clone())
                .collect::<Vec<_>>()
        };
        for owner in owners {
            owner.notify();
        }
    }
    pub(super) fn replace(&self, value: T)
    where
        T: PartialEq,
    {
        let changed = {
            let mut state = self.0.borrow_mut();
            if state.value == value {
                false
            } else {
                state.value = value;
                true
            }
        };
        if changed {
            self.invalidate();
        }
    }
}
pub(super) fn same_resource<T>(
    a: Option<&CompositionResource<T>>,
    b: Option<&CompositionResource<T>>,
) -> bool {
    match (a, b) {
        (None, None) => true,
        (Some(a), Some(b)) => a == b,
        _ => false,
    }
}

/// Original byte RGBA color and [0,1] gradient offset.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CompositionGradientStopValue {
    /// Original byte components, converted independently with division by 255.
    pub color: [u8; 4],
    /// Finite source stop offset.
    pub offset: f32,
}
/// Shared stop identity; duplicate entries remain duplicate ordered stops.
pub type CompositionGradientStop = CompositionResource<CompositionGradientStopValue>;
impl CompositionGradientStop {
    /// Publish a changed stop and invalidate every subscribed brush generation.
    pub fn set(&self, value: CompositionGradientStopValue) -> Result<(), Error> {
        validate_stop(value)?;
        self.replace(value);
        Ok(())
    }
}
fn validate_stop(value: CompositionGradientStopValue) -> Result<(), Error> {
    if value.offset.is_finite() && (0.0..=1.0).contains(&value.offset) {
        Ok(())
    } else {
        Err(Error::InvalidInput)
    }
}

/// Original composition gradient interpolation labels. Only RgbLinear selects
/// scene scRGB interpolation; the other four preserve the source sRGB route.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum CompositionColorSpace {
    #[default]
    Auto,
    Hsl,
    Rgb,
    HslLinear,
    RgbLinear,
}
/// Original gradient coordinate mapping policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum CompositionMappingMode {
    Absolute,
    #[default]
    Relative,
}
/// Original supported gradient extension modes.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum CompositionGradientExtendMode {
    #[default]
    Clamp,
    Wrap,
    Mirror,
}
/// Shared linear/radial gradient state. Values retain original source order;
/// material preparation obtains a stable offset-sorted stop generation.
#[derive(Clone, Debug, PartialEq)]
pub struct CompositionGradient {
    /// Original mutable stop identities, including duplicate references.
    pub stops: Vec<CompositionGradientStop>,
    /// Relative is the source default.
    pub mapping: CompositionMappingMode,
    /// Original extension selection.
    pub extend: CompositionGradientExtendMode,
    /// Original interpolation label.
    pub interpolation: CompositionColorSpace,
    /// Relative origin subtraction uses the actual receiving brush bounds.
    pub anchor: [f32; 2],
    /// Original center/scale/rotation/matrix/offset in this exact order.
    pub transform: CompositionTransform,
}
impl Default for CompositionGradient {
    fn default() -> Self {
        Self {
            stops: Vec::new(),
            mapping: Default::default(),
            extend: Default::default(),
            interpolation: Default::default(),
            anchor: [0.0; 2],
            transform: Default::default(),
        }
    }
}
impl CompositionGradient {
    fn validate(&self, domain: &Rc<Domain>) -> Result<(), Error> {
        self.transform.validate()?;
        if !self.anchor.iter().copied().all(f32::is_finite) {
            return Err(Error::InvalidInput);
        }
        for stop in &self.stops {
            stop.ensure_domain(domain)?;
        }
        Ok(())
    }
    fn point(&self, value: [f32; 2], bounds: [f32; 4]) -> [f32; 2] {
        [0, 1].map(|i| {
            bounds[i]
                + if self.mapping == CompositionMappingMode::Relative {
                    value[i] * bounds[i + 2]
                } else {
                    value[i]
                }
        })
    }
    fn size(&self, value: [f32; 2], bounds: [f32; 4]) -> [f32; 2] {
        if self.mapping == CompositionMappingMode::Relative {
            [value[0] * bounds[2], value[1] * bounds[3]]
        } else {
            value
        }
    }
    fn options(&self, bounds: [f32; 4]) -> GradientOptions {
        let mut placement = self.transform;
        placement.offset = [
            placement.offset[0] - self.anchor[0] * bounds[2],
            placement.offset[1] - self.anchor[1] * bounds[3],
        ];
        let inverse = properties::inverse(placement.local_transform()).unwrap_or(Affine([0.0; 6]));
        GradientOptions {
            transform: Matrix4::from_affine(inverse),
            spread: match self.extend {
                CompositionGradientExtendMode::Clamp => GradientSpread::Pad,
                CompositionGradientExtendMode::Wrap => GradientSpread::Repeat,
                CompositionGradientExtendMode::Mirror => GradientSpread::Reflect,
            },
            interpolation: if self.interpolation == CompositionColorSpace::RgbLinear {
                GradientInterpolation::ScRgbLinear
            } else {
                GradientInterpolation::SRgbLinear
            },
        }
    }
    fn stops(&self) -> Vec<GradientStop> {
        let mut values = self
            .stops
            .iter()
            .map(|s| {
                let v = s.value();
                GradientStop {
                    color: v.color.map(|v| f32::from(v) / 255.0),
                    offset: v.offset,
                }
            })
            .collect::<Vec<_>>();
        // Stable sorting retains the exact order of duplicate offsets.
        values.sort_by(|a, b| {
            a.offset
                .partial_cmp(&b.offset)
                .unwrap_or(std::cmp::Ordering::Equal)
        });
        values
    }
}

/// Original composition color and gradient material families. Surface, mask,
/// backdrop and effect brushes require their separate source/render contracts.
#[derive(Clone, Debug, PartialEq)]
pub enum CompositionBrushValue {
    /// Actual original surface owner, mapping and sampling.
    #[cfg(feature = "gpu")]
    Surface(CompositionSurfaceBrush),
    /// Original source and opacity-mask brush bindings. Nested mask inputs reject.
    Mask {
        source: Option<CompositionBrush>,
        mask: Option<CompositionBrush>,
    },
    /// Byte RGBA color, matching the original per-channel conversion.
    Color([u8; 4]),
    /// Immutable already-owned original scene material.
    Material(Arc<Brush>),
    /// Original linear endpoints; defaults are [0,0] and [1,1].
    Linear {
        start: [f32; 2],
        end: [f32; 2],
        gradient: CompositionGradient,
    },
    /// Original center, nonnegative radius and independent focal displacement.
    Radial {
        center: [f32; 2],
        radius: [f32; 2],
        origin_offset: [f32; 2],
        gradient: CompositionGradient,
    },
}
impl CompositionBrushValue {
    fn gradient(&self) -> Option<&CompositionGradient> {
        match self {
            Self::Linear { gradient, .. } | Self::Radial { gradient, .. } => Some(gradient),
            _ => None,
        }
    }
    fn validate(&self, domain: &Rc<Domain>) -> Result<(), Error> {
        match self {
            #[cfg(feature = "gpu")]
            Self::Surface(value) => {
                let mut candidate = value.clone();
                candidate.normalize()?;
                if let Some(surface) = &value.surface {
                    surface.ensure_domain(domain)?;
                }
                Ok(())
            }
            Self::Mask { source, mask } => {
                for input in source.iter().chain(mask.iter()) {
                    input.ensure_domain(domain)?;
                    if matches!(input.value(), Self::Mask { .. }) {
                        return Err(Error::UnsupportedExecution);
                    }
                }
                Ok(())
            }
            Self::Linear {
                start,
                end,
                gradient,
            } => {
                if !start.iter().chain(end.iter()).copied().all(f32::is_finite) {
                    return Err(Error::InvalidInput);
                }
                gradient.validate(domain)
            }
            Self::Radial {
                center,
                radius,
                origin_offset,
                gradient,
            } => {
                if !center
                    .iter()
                    .chain(radius.iter())
                    .chain(origin_offset.iter())
                    .copied()
                    .all(f32::is_finite)
                    || radius.iter().any(|v| *v < 0.0)
                {
                    return Err(Error::InvalidInput);
                }
                gradient.validate(domain)
            }
            _ => Ok(()),
        }
    }
    /// Produce the real scene brush in its original receiving bounds.
    fn resolve_material(&self, bounds: [f32; 4]) -> Result<Arc<Brush>, Error> {
        Ok(Arc::new(match self {
            Self::Color(color) => Brush::new(BrushKind::Solid(color.map(|v| f32::from(v) / 255.0))),
            Self::Material(brush) => return Ok(brush.clone()),
            Self::Linear {
                start,
                end,
                gradient,
            } => Brush::new(BrushKind::Linear(LinearGradient {
                start: gradient.point(*start, bounds),
                end: gradient.point(*end, bounds),
                stops: gradient.stops(),
                options: gradient.options(bounds),
            })),
            Self::Radial {
                center,
                radius,
                origin_offset,
                gradient,
            } => {
                let center = gradient.point(*center, bounds);
                let offset = gradient.size(*origin_offset, bounds);
                Brush::new(BrushKind::Radial(RadialGradient {
                    center,
                    origin: [center[0] + offset[0], center[1] + offset[1]],
                    radii: gradient.size(*radius, bounds),
                    stops: gradient.stops(),
                    options: gradient.options(bounds),
                }))
            }
            _ => return Err(Error::UnsupportedExecution),
        }))
    }
    /// Freeze original material/surface/mask scope semantics at receiving bounds.
    pub fn resolve(&self, bounds: [f32; 4]) -> Result<CompositionBrushSnapshot, Error> {
        if bounds.iter().any(|v| !v.is_finite()) || bounds[2] < 0.0 || bounds[3] < 0.0 {
            return Err(Error::InvalidInput);
        }
        match self {
            #[cfg(feature = "gpu")]
            Self::Surface(value) => {
                let mut value = value.clone();
                value.normalize()?;
                value.snapshot(bounds)
            }
            Self::Mask { source, mask } => {
                let source = source
                    .as_ref()
                    .map(|v| v.resolve(bounds))
                    .transpose()?
                    .unwrap_or(CompositionBrushSnapshot::Empty);
                let mask = mask
                    .as_ref()
                    .map(|v| v.resolve(bounds))
                    .transpose()?
                    .unwrap_or(CompositionBrushSnapshot::Empty);
                Ok(CompositionBrushSnapshot::Mask {
                    source: Box::new(source),
                    mask: Box::new(mask),
                })
            }
            _ => Ok(CompositionBrushSnapshot::Material(
                self.resolve_material(bounds)?,
            )),
        }
    }
    fn bindings(&self, owner: &Subscriber, add: bool) {
        if let Some(gradient) = self.gradient() {
            for stop in &gradient.stops {
                if add {
                    stop.add_owner(owner.clone())
                } else {
                    stop.remove_owner(owner)
                }
            }
        }
        if let Self::Mask { source, mask } = self {
            for input in source.iter().chain(mask.iter()) {
                if add {
                    input.add_owner(owner.clone())
                } else {
                    input.remove_owner(owner)
                }
            }
        }
        #[cfg(feature = "gpu")]
        if let Self::Surface(value) = self
            && let Some(surface) = &value.surface
        {
            if add {
                surface.add_owner(owner.clone())
            } else {
                surface.remove_owner(owner)
            }
        }
    }
    fn normalize(&mut self) -> Result<(), Error> {
        #[cfg(feature = "gpu")]
        if let Self::Surface(value) = self {
            value.normalize()?;
        }
        Ok(())
    }
}
/// Shared color/gradient/material owner with original weak brush subscribers.
pub type CompositionBrush = CompositionResource<CompositionBrushValue>;
impl CompositionBrush {
    /// Publish a complete candidate only after checking all gradient-stop owners.
    pub fn set(&self, mut value: CompositionBrushValue) -> Result<(), Error> {
        value.normalize()?;
        value.validate(&self.0.borrow().domain)?;
        if let CompositionBrushValue::Mask { source, mask } = &value {
            if source.iter().chain(mask.iter()).any(|v| v.same_owner(self)) {
                return Err(Error::InvalidInput);
            }
            // C# runtime brush classes cannot change. This enum must also keep
            // existing mask input bindings from changing into an inadmissible class.
            if self.0.borrow().owners.iter().any(|s| matches!(&s.owner, Subscriber::Brush(owner) if owner.upgrade().is_some_and(|v| matches!(v.borrow().value, CompositionBrushValue::Mask { .. })))) {
                return Err(Error::UnsupportedExecution);
            }
        }
        let old = self.value();
        if old == value {
            return Ok(());
        }
        let subscriber = Subscriber::Brush(Rc::downgrade(&self.0));
        old.bindings(&subscriber, false);
        value.bindings(&subscriber, true);
        self.replace(value);
        Ok(())
    }
    /// Freeze actual source generations and ordered brush scopes for these bounds.
    pub fn resolve(&self, bounds: [f32; 4]) -> Result<CompositionBrushSnapshot, Error> {
        self.value().resolve(bounds)
    }
}

/// Original blur-kernel discriminants.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum VisualBlurKernel {
    #[default]
    Gaussian,
    Box,
}
/// Original visual-effect metadata; actual subtree capture and execution belong
/// to a renderer consuming the visual snapshot's exact target/frame.
#[derive(Clone, Debug, PartialEq)]
pub enum CompositionEffectValue {
    /// Original typed WpfShaderEffect parameters or actual-target owned recipe.
    #[cfg(feature = "gpu")]
    WpfShader(super::WpfVisualEffect),
    /// Independent raw scalar radius and original kernel selection.
    Blur {
        radius: f32,
        kernel: VisualBlurKernel,
    },
    /// Independent horizontal/vertical radii, original shadow color and ordering.
    DropShadow {
        radii: [f32; 2],
        offset: [f32; 2],
        color: [f32; 4],
        draw_source: bool,
    },
    /// Original shadow with an independently owned opacity-mask producer.
    DropShadowSource {
        radii: [f32; 2],
        offset: [f32; 2],
        color: [f32; 4],
        draw_source: bool,
        source: Arc<ShadowOpacitySource>,
    },
    /// Original isolated affine color transform.
    ColorMatrix(ImageColorMatrix),
    /// Original destination blend applied once to the completed subtree.
    Blend(LayerBlend),
}
impl CompositionEffectValue {
    /// Original explicit input-neutral effect declaration; this never makes an
    /// expanded raster extent into hit geometry or bypasses actual source clips.
    pub fn preserves_source_hit_geometry(&self) -> bool {
        matches!(
            self,
            Self::Blur { .. } | Self::DropShadow { .. } | Self::DropShadowSource { .. }
        )
    }
}
/// Weak-owner effect resource. Mutations invalidate local render content exactly
/// as original EffectBase.NotifyOwners, while assigned identity is retained.
pub type CompositionEffect = CompositionResource<CompositionEffectValue>;
impl CompositionEffect {
    /// Preserve raw numeric effect values for later actual-target admission.
    pub fn set(&self, value: CompositionEffectValue) {
        self.replace(value);
    }
}

impl VisualCompositor {
    /// Create an owned original byte-color/gradient material.
    pub fn create_brush(
        &self,
        mut value: CompositionBrushValue,
    ) -> Result<CompositionBrush, Error> {
        value.normalize()?;
        value.validate(&self.0)?;
        let brush = CompositionResource::new(self.0.clone(), value);
        brush
            .value()
            .bindings(&Subscriber::Brush(Rc::downgrade(&brush.0)), true);
        Ok(brush)
    }
    /// Create a mutable surface identity retaining only completed texture owners.
    #[cfg(feature = "gpu")]
    pub fn create_surface(
        &self,
        value: CompositionSurfaceValue,
    ) -> Result<CompositionSurface, Error> {
        brushes::validate_surface(&value)?;
        Ok(CompositionResource::new(self.0.clone(), value))
    }
    /// Create a separately mutable, shareable color-stop identity.
    pub fn create_gradient_stop(
        &self,
        value: CompositionGradientStopValue,
    ) -> Result<CompositionGradientStop, Error> {
        validate_stop(value)?;
        Ok(CompositionResource::new(self.0.clone(), value))
    }
    /// Create the original effect owner with generation zero.
    pub fn create_effect(&self, value: CompositionEffectValue) -> CompositionEffect {
        CompositionResource::new(self.0.clone(), value)
    }
}
