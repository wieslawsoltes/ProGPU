//! Original _clipScopeIsGeometryMask, _clipStack and _maskStack policy.
use super::*;
use crate::scene::CoverageKind;

#[derive(Default)]
pub(super) struct CoverageScopes {
    clips: Vec<bool>,
    axis_depth: usize,
    mask_depth: usize,
}
impl CoverageScopes {
    pub(super) fn is_empty(&self) -> bool {
        self.clips.is_empty() && self.axis_depth == 0 && self.mask_depth == 0
    }
    pub(super) fn push_clip(&mut self, geometry: bool) -> Result<()> {
        self.clips
            .try_reserve(1)
            .map_err(|_| ArchiveError::Capacity)?;
        let depth = if geometry {
            &mut self.mask_depth
        } else {
            &mut self.axis_depth
        };
        *depth = depth.checked_add(1).ok_or(ArchiveError::Capacity)?;
        self.clips.push(geometry);
        Ok(())
    }
    pub(super) fn push_mask(&mut self) -> Result<()> {
        self.mask_depth = self
            .mask_depth
            .checked_add(1)
            .ok_or(ArchiveError::Capacity)?;
        Ok(())
    }
    pub(super) fn pop_clip(&mut self, scene: &mut Scene) -> Result<()> {
        let geometry = self.clips.last().copied().unwrap_or(false);
        self.pop(
            scene,
            if geometry {
                CoverageKind::Mask
            } else {
                CoverageKind::Clip
            },
            true,
        )?;
        self.clips.pop();
        Ok(())
    }
    pub(super) fn pop_mask(&mut self, scene: &mut Scene) -> Result<()> {
        self.pop(scene, CoverageKind::Mask, false)
    }
    fn pop(&mut self, scene: &mut Scene, kind: CoverageKind, source_clip: bool) -> Result<()> {
        let depth = match kind {
            CoverageKind::Clip => &mut self.axis_depth,
            CoverageKind::Mask => &mut self.mask_depth,
        };
        if *depth != 0 {
            scene.pop_archive_coverage(kind, source_clip)?;
            *depth -= 1;
        }
        Ok(())
    }
}
