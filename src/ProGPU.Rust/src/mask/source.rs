//! Original retained opacity-mask source paint, independent of a target texture.
use crate::{Error, scene::Scene, vector::Matrix4};
use std::sync::Arc;

/// An immutable original mask recording and its complete source placement.
/// The renderer draws this source through preceding coverage before quantizing
/// alpha. No sampled texture or bounding rectangle is accepted as source proof.
#[derive(Clone, Debug)]
pub struct SourceMask {
    scene: Arc<Scene>,
    transform: Matrix4,
    depth: u8,
    bounds: Option<[f32; 4]>,
    bounded_source: bool,
    solid_alpha: Option<f32>,
}
impl SourceMask {
    /// Retain a balanced source generation. Placement is composed before path,
    /// stroke, image and text preparation at the actual receiving target.
    pub fn new(scene: Arc<Scene>, transform: Matrix4) -> Result<Self, Error> {
        let depth = scene
            .source_mask_depth()?
            .checked_add(1)
            .ok_or(Error::Capacity)?;
        let result = Self {
            scene,
            transform,
            depth,
            bounds: None,
            bounded_source: false,
            solid_alpha: None,
        };
        result.validate()?;
        Ok(result)
    }
    /// Original bounded opacity-mask recording. Bounds are declared source XYWH
    /// allocation metadata, not a replacement for geometry or a source clip.
    /// Picture producers retain their independent original clip in the scene.
    pub fn bounded(scene: Arc<Scene>, bounds: [f32; 4], transform: Matrix4) -> Result<Self, Error> {
        let mut result = Self::new(scene, transform)?;
        result.bounds = Some(bounds);
        result.bounded_source = true;
        result.validate()?;
        Ok(result)
    }
    /// Original brush opacity-mask producer. The actual rectangle and brush are
    /// retained; the solid fast path remains subject to receiving-depth and
    /// preceding-mask admission, independently of declared storage bounds.
    pub fn bounded_brush(
        brush: Arc<crate::vector::Brush>,
        bounds: [f32; 4],
        transform: Matrix4,
    ) -> Result<Self, Error> {
        use crate::{
            picture_archive::{
                ArchiveBrush, ArchiveCommand, ArchiveCommandKind, ArchiveVectorDraw,
            },
            vector::BrushKind,
        };
        let mut scene = Scene::default();
        // Reuse the owned original CompileRectCommand producer, whose padding,
        // corner sums and material center differ from the independent native API.
        let command = ArchiveCommand {
            kind: ArchiveCommandKind::DrawRect,
            rect: bounds,
            brush: Some(Arc::new(ArchiveBrush::Vector((*brush).clone()))),
            ..ArchiveCommand::default()
        };
        scene.draw_archive_vector(ArchiveVectorDraw::new(
            Arc::new(command),
            Arc::from([]),
            Matrix4::IDENTITY,
            1.0,
        )?)?;
        let mut result = Self::bounded(Arc::new(scene), bounds, transform)?;
        if let BrushKind::Solid(color) = &brush.kind {
            let alpha = color[3] * brush.opacity;
            result.solid_alpha = Some(if alpha.is_finite() {
                alpha.clamp(0.0, 1.0)
            } else {
                0.0
            });
        }
        Ok(result)
    }
    /// Original declared allocation bounds. None keeps the existing full-target
    /// source route; bounded producers use actual R8 source drawing.
    pub fn bounds(&self) -> Option<[f32; 4]> {
        self.bounds
    }
    /// Whether this is the original direct R8 opacity-mask producer. A geometry
    /// with unavailable bounds uses the original full-target allocation fallback.
    pub fn is_bounded_source(&self) -> bool {
        self.bounded_source
    }
    pub(crate) fn bounded_full(scene: Arc<Scene>, transform: Matrix4) -> Result<Self, Error> {
        let mut result = Self::new(scene, transform)?;
        result.bounded_source = true;
        Ok(result)
    }
    pub(crate) fn solid_alpha(&self) -> Option<f32> {
        self.solid_alpha
    }
    /// Actual immutable producer recording.
    pub fn scene(&self) -> &Arc<Scene> {
        &self.scene
    }
    /// Original source-to-target logical transform.
    pub fn transform(&self) -> Matrix4 {
        self.transform
    }
    pub(crate) fn depth(&self) -> u8 {
        self.depth
    }
    pub(crate) fn placed(&self, placement: Matrix4) -> Result<Self, Error> {
        let mut result = self.clone();
        result.transform = self.transform * placement;
        result.validate()?;
        Ok(result)
    }
    pub(crate) fn validate(&self) -> Result<(), Error> {
        if self.depth > 16 {
            return Err(Error::Capacity);
        }
        if self
            .bounds
            .is_some_and(|bounds| bounds.iter().any(|v| !v.is_finite()))
        {
            return Err(Error::InvalidInput);
        }
        let m = self.transform.0;
        if m.iter().flatten().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        if m[0][2] != 0.0
            || m[0][3] != 0.0
            || m[1][2] != 0.0
            || m[1][3] != 0.0
            || m[2] != [0.0, 0.0, 1.0, 0.0]
            || m[3][2] != 0.0
            || m[3][3] != 1.0
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(())
    }
}

/// Original source geometry clip, resolved to the canonical analytic or painted
/// mask route only after the actual receiving frame and preceding masks exist.
#[derive(Clone, Debug)]
pub struct GeometryMask {
    /// Actual immutable source geometry, including its original fill rule.
    pub geometry: Arc<crate::vector::PathGeometry>,
    /// Complete source-to-target logical mapping.
    pub transform: crate::geometry::Affine,
}
