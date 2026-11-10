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
        };
        result.validate()?;
        Ok(result)
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
