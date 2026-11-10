use super::*;
use crate::{
    geometry::{Affine, AnalyticPrimitive, Shape},
    scene::{MaterialPaint, PathDraw, PathSampling},
    vector::{
        ArcSegment, PathFigure, PathGeometry, PathSegment, PathSegmentKind, Pen, PenLineCap,
        PenLineJoin, PenOptions, StrokeDraw, SweepDirection,
    },
};
mod path_measure;
pub use path_measure::CompositionPath;

/// Original clamped trim endpoints and independently wrapping origin offset.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CompositionTrim {
    /// Original start, finite but not clamped until resolving.
    pub start: f32,
    /// Original end, default one.
    pub end: f32,
    /// Original wrapping offset.
    pub offset: f32,
}
impl Default for CompositionTrim {
    fn default() -> Self {
        Self {
            start: 0.0,
            end: 1.0,
            offset: 0.0,
        }
    }
}
impl CompositionTrim {
    /// Original full-trim test; offset is ignored by full-geometry routes.
    pub fn is_full(self) -> bool {
        self.end.clamp(0.0, 1.0) - self.start.clamp(0.0, 1.0) >= 1.0
    }
    /// Original independent nonnegative interval length.
    pub fn length(self) -> f32 {
        (self.end.clamp(0.0, 1.0) - self.start.clamp(0.0, 1.0)).max(0.0)
    }
    /// Original normalized start plus offset, without changing length.
    pub fn origin(self) -> f32 {
        let origin = self.start.clamp(0.0, 1.0) + self.offset;
        origin - origin.floor()
    }
}
/// All typed original composition geometry families.
#[derive(Clone, Debug, PartialEq)]
pub enum CompositionGeometryKind {
    /// Original center/radius pair.
    Ellipse { center: [f32; 2], radius: [f32; 2] },
    /// Original offset and nonnegative size.
    Rectangle { offset: [f32; 2], size: [f32; 2] },
    /// Original independent corner radii; clamp occurs at geometry preparation.
    RoundedRectangle {
        offset: [f32; 2],
        size: [f32; 2],
        radius: [f32; 2],
    },
    /// Original line, stroked only even when a fill material is supplied.
    Line { start: [f32; 2], end: [f32; 2] },
    /// Typed immutable source path; None is the source no-path state.
    Path(Option<CompositionPath>),
}
/// Mutable geometry descriptor and original independent trim controls.
#[derive(Clone, Debug, PartialEq)]
pub struct CompositionGeometryValue {
    /// Actual geometry family and source generation.
    pub kind: CompositionGeometryKind,
    /// Original trim values.
    pub trim: CompositionTrim,
}
impl CompositionGeometryValue {
    /// Original full, untrimmed geometry descriptor.
    pub fn new(kind: CompositionGeometryKind) -> Self {
        Self {
            kind,
            trim: CompositionTrim::default(),
        }
    }
    fn validate(&self) -> Result<(), Error> {
        if ![self.trim.start, self.trim.end, self.trim.offset]
            .into_iter()
            .all(f32::is_finite)
        {
            return Err(Error::InvalidInput);
        }
        let finite = |p: [f32; 2]| p.into_iter().all(f32::is_finite);
        let nonnegative = |p: [f32; 2]| finite(p) && p.into_iter().all(|v| v >= 0.0);
        let valid = match self.kind {
            CompositionGeometryKind::Ellipse { center, radius } => {
                finite(center) && nonnegative(radius)
            }
            CompositionGeometryKind::Rectangle { offset, size } => {
                finite(offset) && nonnegative(size)
            }
            CompositionGeometryKind::RoundedRectangle {
                offset,
                size,
                radius,
            } => finite(offset) && nonnegative(size) && nonnegative(radius),
            CompositionGeometryKind::Line { start, end } => finite(start) && finite(end),
            CompositionGeometryKind::Path(_) => true,
        };
        if valid {
            Ok(())
        } else {
            Err(Error::InvalidInput)
        }
    }
    /// Actual retained path used by geometry clips and non-scaling strokes.
    /// Trimming retains exact analytic subsegments; samples only measure length.
    pub fn path(&self) -> Result<Option<Arc<PathGeometry>>, Error> {
        self.validate()?;
        let full = self.trim.is_full();
        let length = self.trim.length();
        let origin = self.trim.origin();
        let path = match &self.kind {
            CompositionGeometryKind::Path(path) => {
                return path
                    .as_ref()
                    .map(|p| {
                        if full {
                            Ok(p.geometry())
                        } else {
                            p.trimmed(origin, length).map(Arc::new)
                        }
                    })
                    .transpose();
            }
            CompositionGeometryKind::RoundedRectangle {
                offset,
                size,
                radius,
            } => {
                let radius = [radius[0].min(size[0] * 0.5), radius[1].min(size[1] * 0.5)];
                let path = PathGeometry::rounded_rectangle(
                    offset[0], offset[1], size[0], size[1], radius[0], radius[1],
                );
                if full {
                    path
                } else {
                    CompositionPath::from_owned(Arc::new(path))?.trimmed(origin, length)?
                }
            }
            CompositionGeometryKind::Ellipse { center, radius } => {
                if full {
                    PathGeometry::ellipse(*center, radius[0], radius[1])
                } else if length <= 0.0 {
                    PathGeometry::default()
                } else {
                    let start_angle = origin * std::f32::consts::TAU;
                    let end_angle = (origin + length) * std::f32::consts::TAU;
                    let start = [
                        center[0] + start_angle.cos() * radius[0],
                        center[1] + start_angle.sin() * radius[1],
                    ];
                    let end = [
                        center[0] + end_angle.cos() * radius[0],
                        center[1] + end_angle.sin() * radius[1],
                    ];
                    let mut figure = PathFigure::new(start, false);
                    figure
                        .segments
                        .push(PathSegment::new(PathSegmentKind::Arc(ArcSegment {
                            end,
                            size: *radius,
                            rotation: 0.0,
                            large: length > 0.5,
                            sweep: SweepDirection::Clockwise,
                        })));
                    PathGeometry {
                        figures: vec![figure],
                        ..Default::default()
                    }
                }
            }
            CompositionGeometryKind::Rectangle { offset, size } => {
                if full {
                    PathGeometry::rectangle(offset[0], offset[1], size[0], size[1])
                } else if length <= 0.0 {
                    PathGeometry::default()
                } else {
                    let perimeter = 2.0 * (size[0] + size[1]);
                    let end = origin + length;
                    let mut figure =
                        PathFigure::new(perimeter_point(*offset, *size, origin), false);
                    let corners = [
                        size[0] / perimeter,
                        (size[0] + size[1]) / perimeter,
                        ((2.0 * size[0]) + size[1]) / perimeter,
                        1.0,
                    ];
                    for lap in 0..=1 {
                        for corner in corners {
                            let corner = corner + lap as f32;
                            if corner > origin && corner < end {
                                figure
                                    .segments
                                    .push(line(perimeter_point(*offset, *size, corner)));
                            }
                        }
                    }
                    figure
                        .segments
                        .push(line(perimeter_point(*offset, *size, end)));
                    PathGeometry {
                        figures: vec![figure],
                        ..Default::default()
                    }
                }
            }
            CompositionGeometryKind::Line { start, end } => {
                if length <= 0.0 {
                    PathGeometry::default()
                } else {
                    let origin = if full { 0.0 } else { origin };
                    let first = origin.clamp(0.0, 1.0);
                    let last = (origin + length).clamp(0.0, 1.0);
                    let mut figure = PathFigure::new(lerp(*start, *end, first), false);
                    figure.is_filled = false;
                    figure.segments.push(line(lerp(*start, *end, last)));
                    PathGeometry {
                        figures: vec![figure],
                        ..Default::default()
                    }
                }
            }
        };
        Ok(Some(Arc::new(path)))
    }
    fn renderable(&self) -> bool {
        match self.kind {
            CompositionGeometryKind::Ellipse { radius, .. } => radius[0] > 0.0 && radius[1] > 0.0,
            CompositionGeometryKind::Rectangle { size, .. }
            | CompositionGeometryKind::RoundedRectangle { size, .. } => {
                size[0] > 0.0 && size[1] > 0.0
            }
            _ => true,
        }
    }
    fn analytic(&self) -> Option<AnalyticPrimitive> {
        if !self.trim.is_full() {
            return None;
        }
        match self.kind {
            CompositionGeometryKind::Ellipse { center, radius } => {
                let mut p = AnalyticPrimitive::rectangle(
                    [
                        center[0] - radius[0],
                        center[1] - radius[1],
                        radius[0] * 2.0,
                        radius[1] * 2.0,
                    ],
                    [1.0; 4],
                );
                p.shape = Shape::Ellipse;
                Some(p)
            }
            CompositionGeometryKind::Rectangle { offset, size } => Some(
                AnalyticPrimitive::rectangle([offset[0], offset[1], size[0], size[1]], [1.0; 4]),
            ),
            CompositionGeometryKind::RoundedRectangle {
                offset,
                size,
                radius,
            } if radius[0].min(size[0] * 0.5) == radius[1].min(size[1] * 0.5) => {
                let mut p = AnalyticPrimitive::rectangle(
                    [offset[0], offset[1], size[0], size[1]],
                    [1.0; 4],
                );
                p.shape = Shape::RoundedRectangle;
                p.corner_radius = radius[0].min(size[0] * 0.5);
                Some(p)
            }
            _ => None,
        }
    }
}
fn line(p: [f32; 2]) -> PathSegment {
    PathSegment::new(PathSegmentKind::Line(p))
}
fn lerp(a: [f32; 2], b: [f32; 2], t: f32) -> [f32; 2] {
    [0, 1].map(|i| a[i] * (1.0 - t) + b[i] * t)
}
fn perimeter_point(offset: [f32; 2], size: [f32; 2], mut progress: f32) -> [f32; 2] {
    progress -= progress.floor();
    let mut distance = progress * (2.0 * (size[0] + size[1]));
    let local = if distance <= size[0] {
        [distance, 0.0]
    } else {
        distance -= size[0];
        if distance <= size[1] {
            [size[0], distance]
        } else {
            distance -= size[1];
            if distance <= size[0] {
                [size[0] - distance, size[1]]
            } else {
                distance -= size[0];
                [0.0, size[1] - distance]
            }
        }
    };
    [offset[0] + local[0], offset[1] + local[1]]
}
/// Shared geometry owner. Geometry edits invalidate every actual shape consumer.
pub type CompositionGeometry = CompositionResource<CompositionGeometryValue>;
impl CompositionGeometry {
    /// Validate and publish the whole source geometry/trim candidate.
    pub fn set(&self, value: CompositionGeometryValue) -> Result<(), Error> {
        value.validate()?;
        self.replace(value);
        Ok(())
    }
}

/// Original composition view-box stretching modes.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum CompositionStretch {
    None,
    Fill,
    #[default]
    Uniform,
    UniformToFill,
}
/// Original view-box descriptor; alignment ratios remain finite but unclamped.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CompositionViewBoxValue {
    /// Original source origin.
    pub offset: [f32; 2],
    /// Nonnegative original source size.
    pub size: [f32; 2],
    /// Original stretch mode.
    pub stretch: CompositionStretch,
    /// Horizontal/vertical alignment ratios, default half.
    pub alignment: [f32; 2],
}
impl Default for CompositionViewBoxValue {
    fn default() -> Self {
        Self {
            offset: [0.0; 2],
            size: [0.0; 2],
            stretch: Default::default(),
            alignment: [0.5; 2],
        }
    }
}
impl CompositionViewBoxValue {
    fn validate(self) -> Result<(), Error> {
        if self
            .offset
            .iter()
            .chain(self.size.iter())
            .chain(self.alignment.iter())
            .copied()
            .all(f32::is_finite)
            && self.size.iter().all(|v| *v >= 0.0)
        {
            Ok(())
        } else {
            Err(Error::InvalidInput)
        }
    }
    /// Original translation/scale/alignment expression; zero source extent keeps identity.
    pub fn transform(self, destination: [f32; 2]) -> Affine {
        if self.size[0] <= 0.0 || self.size[1] <= 0.0 {
            return Affine::IDENTITY;
        }
        let ratios = [destination[0] / self.size[0], destination[1] / self.size[1]];
        let scale = match self.stretch {
            CompositionStretch::None => [1.0; 2],
            CompositionStretch::Fill => ratios,
            CompositionStretch::Uniform => [ratios[0].min(ratios[1]); 2],
            CompositionStretch::UniformToFill => [ratios[0].max(ratios[1]); 2],
        };
        let alignment =
            [0, 1].map(|i| (destination[i] - self.size[i] * scale[i]) * self.alignment[i]);
        properties::multiply(
            properties::multiply(
                Affine([1.0, 0.0, 0.0, 1.0, -self.offset[0], -self.offset[1]]),
                Affine([scale[0], 0.0, 0.0, scale[1], 0.0, 0.0]),
            ),
            Affine([1.0, 0.0, 0.0, 1.0, alignment[0], alignment[1]]),
        )
    }
}
/// Shareable original view-box owner with weak visual subscribers.
pub type CompositionViewBox = CompositionResource<CompositionViewBoxValue>;
impl CompositionViewBox {
    /// Publish one complete view-box candidate.
    pub fn set(&self, value: CompositionViewBoxValue) -> Result<(), Error> {
        value.validate()?;
        self.replace(value);
        Ok(())
    }
}

/// Original composition stroke-join labels. The source adapter maps
/// MiterOrBevel through its default Miter branch; this port retains that policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum CompositionStrokeLineJoin {
    #[default]
    Miter,
    Bevel,
    Round,
    MiterOrBevel,
}
/// Original shape stroke state, including independent double dash snapshots.
#[derive(Clone, Debug, PartialEq)]
pub struct CompositionStroke {
    /// Positive thickness admits a stroke; default zero suppresses it.
    pub thickness: f32,
    /// Expand the transformed centerline with an unchanged pen.
    pub non_scaling: bool,
    /// Original miter limit, clamped to at least one when constructing the pen.
    pub miter_limit: f32,
    /// Original composition join label.
    pub join: CompositionStrokeLineJoin,
    /// Independent source endpoint caps.
    pub start_cap: PenLineCap,
    /// Independent source end cap.
    pub end_cap: PenLineCap,
    /// Independent dash cap.
    pub dash_cap: PenLineCap,
    /// Original finite dash offset.
    pub dash_offset: f32,
    /// Original nonnegative finite dash entries, copied to doubles for the pen.
    pub dashes: Arc<[f32]>,
}
impl Default for CompositionStroke {
    fn default() -> Self {
        Self {
            thickness: 0.0,
            non_scaling: false,
            miter_limit: 10.0,
            join: Default::default(),
            start_cap: Default::default(),
            end_cap: Default::default(),
            dash_cap: Default::default(),
            dash_offset: 0.0,
            dashes: Arc::from([]),
        }
    }
}
impl CompositionStroke {
    fn validate(&self) -> Result<(), Error> {
        if !self.thickness.is_finite()
            || self.thickness < 0.0
            || !self.miter_limit.is_finite()
            || self.miter_limit < 0.0
            || !self.dash_offset.is_finite()
            || self.dashes.iter().any(|v| !v.is_finite() || *v < 0.0)
        {
            Err(Error::InvalidInput)
        } else {
            Ok(())
        }
    }
    fn pen(&self, brush: Arc<crate::vector::Brush>) -> Result<Arc<Pen>, Error> {
        let options = PenOptions {
            thickness: self.thickness,
            join: match self.join {
                CompositionStrokeLineJoin::Bevel => PenLineJoin::Bevel,
                CompositionStrokeLineJoin::Round => PenLineJoin::Round,
                _ => PenLineJoin::Miter,
            },
            miter_limit: self.miter_limit.max(1.0),
            start_cap: self.start_cap,
            end_cap: self.end_cap,
            dash_cap: self.dash_cap,
            dash_offset: f64::from(self.dash_offset),
            ..Default::default()
        };
        let dashes = self
            .dashes
            .iter()
            .copied()
            .map(f64::from)
            .collect::<Vec<_>>();
        Ok(Arc::new(Pen::with_options(
            brush,
            options,
            (!dashes.is_empty()).then_some(dashes.as_slice()),
        )?))
    }
}
/// Complete original sprite-shape bindings and stroke state.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct CompositionSpriteShape {
    /// Optional actual geometry owner.
    pub geometry: Option<CompositionGeometry>,
    /// Optional independent fill material owner.
    pub fill: Option<CompositionBrush>,
    /// Optional independent stroke material owner.
    pub stroke_brush: Option<CompositionBrush>,
    /// Original stroke and dash policy.
    pub stroke: CompositionStroke,
}
impl CompositionSpriteShape {
    fn validate(&self, domain: &Rc<Domain>) -> Result<(), Error> {
        self.stroke.validate()?;
        if let Some(v) = &self.geometry {
            v.ensure_domain(domain)?;
        }
        if let Some(v) = &self.fill {
            v.ensure_domain(domain)?;
        }
        if let Some(v) = &self.stroke_brush {
            v.ensure_domain(domain)?;
        }
        Ok(())
    }
    fn subscribe(&self, owner: Subscriber, add: bool) {
        macro_rules! bind {
            ($value:expr) => {
                if let Some(value) = $value {
                    if add {
                        value.add_owner(owner.clone());
                    } else {
                        value.remove_owner(&owner);
                    }
                }
            };
        }
        bind!(&self.geometry);
        bind!(&self.fill);
        bind!(&self.stroke_brush);
    }
    fn record(&self, scene: &mut Scene, transform: Affine, bounds: [f32; 4]) -> Result<(), Error> {
        let Some(geometry) = &self.geometry else {
            return Ok(());
        };
        let geometry = geometry.value();
        if !geometry.renderable() {
            return Ok(());
        }
        let path = geometry.path()?;
        if let Some(fill) = &self.fill
            && !matches!(geometry.kind, CompositionGeometryKind::Line { .. })
        {
            let paint = MaterialPaint::new(fill.resolve(bounds));
            if let Some(mut primitive) = geometry.analytic() {
                primitive.transform = transform;
                scene.draw_material_analytic(primitive, paint)?;
            } else if let Some(path) = &path
                && let Some(path) = path.compile_raster_path()?
            {
                scene.draw_material_path(
                    PathDraw {
                        path,
                        transform,
                        color: [1.0; 4],
                        sampling: PathSampling::default(),
                    },
                    paint,
                )?;
            }
        }
        if let Some(brush) = &self.stroke_brush
            && self.stroke.thickness > 0.0
            && let Some(path) = path
        {
            let pen = self.stroke.pen(brush.resolve(bounds))?;
            let (path, transform) = if self.stroke.non_scaling {
                (
                    Arc::new(path.transformed(Matrix4::from_affine(transform))?),
                    Matrix4::IDENTITY,
                )
            } else {
                (path, Matrix4::from_affine(transform))
            };
            let mut draw = StrokeDraw::new(path, pen);
            draw.frame.transform = transform;
            scene.draw_stroke(draw)?;
        }
        Ok(())
    }
}

#[derive(Clone)]
enum ShapeContent {
    Container(Vec<CompositionShape>),
    Sprite(CompositionSpriteShape),
}
#[derive(Clone)]
enum ShapeParent {
    None,
    Visual(Weak<RefCell<Node>>),
    Shape(Weak<RefCell<ShapeNode>>),
}
pub(super) struct ShapeNode {
    domain: Rc<Domain>,
    parent: ShapeParent,
    transform: CompositionTransform,
    content: ShapeContent,
}
/// Ordered owned composition shape. A shape belongs to one collection, whose
/// parent edge is weak; reparenting and cycle rejection preserve source order.
#[derive(Clone)]
pub struct CompositionShape(pub(super) Rc<RefCell<ShapeNode>>);
impl PartialEq for CompositionShape {
    fn eq(&self, other: &Self) -> bool {
        Rc::ptr_eq(&self.0, &other.0)
    }
}
impl Eq for CompositionShape {}
impl std::fmt::Debug for CompositionShape {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_tuple("CompositionShape")
            .field(&Rc::as_ptr(&self.0))
            .finish()
    }
}
impl CompositionShape {
    /// Original six-coefficient transform descriptor.
    pub fn transform(&self) -> CompositionTransform {
        self.0.borrow().transform
    }
    /// Publish a finite local transform and invalidate the owning visual page.
    pub fn set_transform(&self, transform: CompositionTransform) -> Result<(), Error> {
        transform.validate()?;
        let changed = {
            let mut n = self.0.borrow_mut();
            if n.transform == transform {
                false
            } else {
                n.transform = transform;
                true
            }
        };
        if changed {
            self.invalidate();
        }
        Ok(())
    }
    /// Replace a complete sprite descriptor atomically; containers reject.
    pub fn set_sprite(&self, value: CompositionSpriteShape) -> Result<(), Error> {
        value.validate(&self.0.borrow().domain)?;
        let old = {
            let mut n = self.0.borrow_mut();
            let ShapeContent::Sprite(current) = &mut n.content else {
                return Err(Error::UnsupportedExecution);
            };
            if *current == value {
                return Ok(());
            }
            std::mem::replace(current, value.clone())
        };
        let owner = Subscriber::Shape(Rc::downgrade(&self.0));
        old.subscribe(owner.clone(), false);
        value.subscribe(owner, true);
        self.invalidate();
        Ok(())
    }
    /// Painter-ordered container children; sprite shapes return an empty list.
    pub fn children(&self) -> Vec<CompositionShape> {
        match &self.0.borrow().content {
            ShapeContent::Container(v) => v.clone(),
            _ => Vec::new(),
        }
    }
    /// Insert or move a shape in this container's exact source position.
    pub fn insert_shape(&self, index: usize, shape: CompositionShape) -> Result<(), Error> {
        Collection::Shape(self.clone()).insert(index, shape)
    }
    /// Append a shape at the top of this container.
    pub fn add_shape(&self, shape: CompositionShape) -> Result<(), Error> {
        self.insert_shape(self.children().len(), shape)
    }
    /// Remove only the matching child identity.
    pub fn remove_shape(&self, shape: &CompositionShape) -> bool {
        Collection::Shape(self.clone()).remove(shape)
    }
    /// Detach all actual shape children.
    pub fn clear_shapes(&self) {
        Collection::Shape(self.clone()).clear();
    }
    pub(super) fn invalidate(&self) {
        let mut parent = self.0.borrow().parent.clone();
        loop {
            match parent {
                ShapeParent::None => break,
                ShapeParent::Visual(v) => {
                    if let Some(v) = v.upgrade() {
                        Visual(v).invalidate();
                    }
                    break;
                }
                ShapeParent::Shape(v) => {
                    let Some(v) = v.upgrade() else {
                        break;
                    };
                    parent = v.borrow().parent.clone();
                }
            }
        }
    }
    fn record(
        &self,
        scene: &mut Scene,
        parent: Affine,
        bounds: [f32; 4],
        depth: usize,
    ) -> Result<(), Error> {
        if depth > 256 {
            return Err(Error::Capacity);
        }
        let (transform, content) = {
            let n = self.0.borrow();
            (n.transform.resolve(parent), n.content.clone())
        };
        match content {
            ShapeContent::Container(children) => {
                for child in children {
                    child.record(scene, transform, bounds, depth + 1)?;
                }
            }
            ShapeContent::Sprite(sprite) => sprite.record(scene, transform, bounds)?,
        }
        Ok(())
    }
}
enum Collection {
    Visual(Visual),
    Shape(CompositionShape),
}
impl Collection {
    fn parent(&self) -> ShapeParent {
        match self {
            Self::Visual(v) => ShapeParent::Visual(Rc::downgrade(&v.0)),
            Self::Shape(v) => ShapeParent::Shape(Rc::downgrade(&v.0)),
        }
    }
    fn domain(&self) -> Rc<Domain> {
        match self {
            Self::Visual(v) => v.0.borrow().domain.clone(),
            Self::Shape(v) => v.0.borrow().domain.clone(),
        }
    }
    fn edit<T>(&self, edit: impl FnOnce(&mut Vec<CompositionShape>) -> T) -> Result<T, Error> {
        match self {
            Self::Visual(v) => {
                let mut n = v.0.borrow_mut();
                let Content::Shapes { shapes, .. } = &mut n.content else {
                    return Err(Error::UnsupportedExecution);
                };
                Ok(edit(shapes))
            }
            Self::Shape(v) => {
                let mut n = v.0.borrow_mut();
                let ShapeContent::Container(shapes) = &mut n.content else {
                    return Err(Error::UnsupportedExecution);
                };
                Ok(edit(shapes))
            }
        }
    }
    fn invalidate(&self) {
        match self {
            Self::Visual(v) => v.invalidate(),
            Self::Shape(v) => v.invalidate(),
        }
    }
    fn insert(&self, mut index: usize, shape: CompositionShape) -> Result<(), Error> {
        if !Rc::ptr_eq(&self.domain(), &shape.0.borrow().domain) {
            return Err(Error::ResourceDomain);
        }
        let mut parent = self.parent();
        while let ShapeParent::Shape(v) = parent {
            let Some(v) = v.upgrade() else {
                break;
            };
            if Rc::ptr_eq(&v, &shape.0) {
                return Err(Error::InvalidInput);
            }
            parent = v.borrow().parent.clone();
        }
        self.edit(|items| {
            if index > items.len() {
                return Err(Error::InvalidInput);
            }
            items.try_reserve(1).map_err(|_| Error::Capacity)
        })??;
        let old = shape.0.borrow().parent.clone();
        if let Some(previous) = Self::from_parent(old) {
            if previous.same(self) {
                self.edit(|items| {
                    if let Some(old) = items.iter().position(|v| v == &shape) {
                        items.remove(old);
                        if old < index {
                            index -= 1;
                        }
                    }
                })?;
            } else {
                previous.remove(&shape);
            }
        }
        self.edit(|items| items.insert(index, shape.clone()))?;
        shape.0.borrow_mut().parent = self.parent();
        self.invalidate();
        Ok(())
    }
    fn same(&self, other: &Self) -> bool {
        match (self, other) {
            (Self::Visual(a), Self::Visual(b)) => a == b,
            (Self::Shape(a), Self::Shape(b)) => a == b,
            _ => false,
        }
    }
    fn from_parent(parent: ShapeParent) -> Option<Self> {
        match parent {
            ShapeParent::None => None,
            ShapeParent::Visual(v) => v.upgrade().map(|v| Self::Visual(Visual(v))),
            ShapeParent::Shape(v) => v.upgrade().map(|v| Self::Shape(CompositionShape(v))),
        }
    }
    fn remove(&self, shape: &CompositionShape) -> bool {
        let Ok(removed) = self.edit(|items| {
            items
                .iter()
                .position(|v| v == shape)
                .map(|i| items.remove(i))
        }) else {
            return false;
        };
        if let Some(removed) = removed {
            removed.0.borrow_mut().parent = ShapeParent::None;
            self.invalidate();
            true
        } else {
            false
        }
    }
    fn clear(&self) {
        if let Ok(items) = self.edit(std::mem::take)
            && !items.is_empty()
        {
            for item in items {
                item.0.borrow_mut().parent = ShapeParent::None;
            }
            self.invalidate();
        }
    }
}
impl ShapeVisual {
    /// Exact current shape collection order.
    pub fn shapes(&self) -> Vec<CompositionShape> {
        let n = self.0.0.borrow();
        let Content::Shapes { shapes, .. } = &n.content else {
            unreachable!()
        };
        shapes.clone()
    }
    /// Move or insert one actual shape identity, rejecting cycles before mutation.
    pub fn insert_shape(&self, index: usize, shape: CompositionShape) -> Result<(), Error> {
        Collection::Visual(self.0.clone()).insert(index, shape)
    }
    /// Append a topmost shape in this visual's own command stream.
    pub fn add_shape(&self, shape: CompositionShape) -> Result<(), Error> {
        self.insert_shape(self.shapes().len(), shape)
    }
    /// Remove an actual shape identity if present.
    pub fn remove_shape(&self, shape: &CompositionShape) -> bool {
        Collection::Visual(self.0.clone()).remove(shape)
    }
    /// Detach all shape entries.
    pub fn clear_shapes(&self) {
        Collection::Visual(self.0.clone()).clear();
    }
    /// Bind an independent view-box owner and subscribe to future changes.
    pub fn set_view_box(&self, view_box: Option<CompositionViewBox>) -> Result<(), Error> {
        if let Some(value) = &view_box {
            value.ensure_domain(&self.0.0.borrow().domain)?;
        }
        let old = {
            let mut n = self.0.0.borrow_mut();
            let Content::Shapes {
                view_box: current, ..
            } = &mut n.content
            else {
                unreachable!()
            };
            if same_resource(current.as_ref(), view_box.as_ref()) {
                return Ok(());
            }
            std::mem::replace(current, view_box.clone())
        };
        let owner = Subscriber::Visual(Rc::downgrade(&self.0.0));
        if let Some(old) = old {
            old.remove_owner(&owner);
        }
        if let Some(value) = view_box {
            value.add_owner(owner);
        }
        self.invalidate();
        Ok(())
    }
}
impl VisualCompositor {
    /// Create a shared typed geometry resource.
    pub fn create_geometry(
        &self,
        value: CompositionGeometryValue,
    ) -> Result<CompositionGeometry, Error> {
        value.validate()?;
        Ok(CompositionResource::new(self.0.clone(), value))
    }
    /// Create an original independently owned view box.
    pub fn create_view_box(
        &self,
        value: CompositionViewBoxValue,
    ) -> Result<CompositionViewBox, Error> {
        value.validate()?;
        Ok(CompositionResource::new(self.0.clone(), value))
    }
    /// Create an empty owned composition shape container.
    pub fn create_container_shape(&self) -> CompositionShape {
        CompositionShape(Rc::new(RefCell::new(ShapeNode {
            domain: self.0.clone(),
            parent: ShapeParent::None,
            transform: Default::default(),
            content: ShapeContent::Container(Vec::new()),
        })))
    }
    /// Create a real source-geometry/material shape, retaining every dependency.
    pub fn create_sprite_shape(
        &self,
        value: CompositionSpriteShape,
    ) -> Result<CompositionShape, Error> {
        value.validate(&self.0)?;
        let shape = CompositionShape(Rc::new(RefCell::new(ShapeNode {
            domain: self.0.clone(),
            parent: ShapeParent::None,
            transform: Default::default(),
            content: ShapeContent::Sprite(value.clone()),
        })));
        value.subscribe(Subscriber::Shape(Rc::downgrade(&shape.0)), true);
        Ok(shape)
    }
}
pub(super) fn record_shapes(
    scene: &mut Scene,
    shapes: &[CompositionShape],
    view_box: Option<&CompositionViewBox>,
    size: [f32; 2],
) -> Result<(), Error> {
    let transform = view_box.map_or(Affine::IDENTITY, |v| v.value().transform(size));
    for shape in shapes {
        shape.record(scene, transform, [0.0, 0.0, size[0], size[1]], 0)?;
    }
    Ok(())
}
