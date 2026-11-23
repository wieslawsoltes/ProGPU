//! Original GpuPictureBounds depth, scope balance and source-frame traversal.
use super::*;
use std::collections::HashSet;
impl ArchivePicture {
    /// Build the ordinary retained-command input inventory. This is not source
    /// Visual input capture: archive v1-v10 omits its hit overrides/scope flags.
    pub fn interaction_index(self: &Arc<Self>, transform: Matrix4) -> Result<ArchiveHitIndex> {
        let mut cache = ArchiveHitTestCache::new();
        cache.add_picture(self.clone(), transform)?;
        cache.build()
    }
    /// Original conservative raster bounds (x,y,width,height). Valid empty
    /// pictures return zero; unsupported producers remain a distinct error.
    /// Source-only hit overrides are absent in the archive and never fabricated.
    pub fn raster_bounds(self: &Arc<Self>, transform: Matrix4) -> Result<[f32; 4]> {
        if !affine(transform) {
            return Err(ArchiveError::Unsupported);
        }
        let mut cache = ArchiveHitTestCache::new();
        let mut active = HashSet::new();
        cache.picture(self, transform, &mut active, 0, true)?;
        Ok(cache
            .bounds
            .map_or([0.0; 4], |b| [b[0], b[1], b[2] - b[0], b[3] - b[1]]))
    }
}
impl PictureArchive {
    pub fn interaction_index(&self, transform: Matrix4) -> Result<ArchiveHitIndex> {
        self.picture.interaction_index(transform)
    }
    pub fn raster_bounds(&self, transform: Matrix4) -> Result<[f32; 4]> {
        self.picture.raster_bounds(transform)
    }
}
impl ArchiveHitTestCache {
    pub fn add_picture(&mut self, picture: Arc<ArchivePicture>, transform: Matrix4) -> Result<()> {
        if self.failed {
            return Err(ArchiveError::InvalidData);
        }
        let result = self.picture(
            &picture,
            normalize(transform),
            &mut HashSet::new(),
            0,
            false,
        );
        if result.is_err() {
            self.failed = true;
        }
        result
    }
    fn picture(
        &mut self,
        picture: &Arc<ArchivePicture>,
        parent: Matrix4,
        active: &mut HashSet<usize>,
        depth: u32,
        raster: bool,
    ) -> Result<()> {
        let key = Arc::as_ptr(picture) as usize;
        if depth >= 64 || !active.insert(key) {
            return Err(ArchiveError::Unsupported);
        }
        let result = (|| {
            push(&mut self.pictures, picture.clone())?;
            let mut scopes = [0u32; 4];
            for c in picture.commands.iter() {
                if raster && (c.use_gpu_transforms || !supported(c.kind)) {
                    return Err(ArchiveError::Unsupported);
                }
                let transform = if c.transform == Matrix4([[0.0; 4]; 4]) {
                    parent
                } else {
                    c.transform * parent
                };
                if raster && !affine(transform) {
                    return Err(ArchiveError::Unsupported);
                }
                if c.kind == K::DrawPicture {
                    let child = c.picture.as_ref().ok_or(ArchiveError::Unsupported)?;
                    self.picture(child, transform, active, depth + 1, raster)?;
                    continue;
                }
                // Original raster bounds require actual hinted geometry and its
                // exact DPI/phase producer, intentionally absent in this wire.
                if c.kind == K::DrawHintedGlyphs {
                    return Err(ArchiveError::Unsupported);
                }
                update_depth(c.kind, &mut scopes)?;
                self.command(
                    c,
                    if c.kind == K::DrawPath {
                        parent
                    } else {
                        transform
                    },
                    Some(picture),
                    None,
                )?;
            }
            if scopes != [0; 4] {
                return Err(ArchiveError::InvalidData);
            }
            Ok(())
        })();
        active.remove(&key);
        result
    }
}
fn supported(k: K) -> bool {
    matches!(
        k,
        K::DrawRect
            | K::DrawPath
            | K::DrawText
            | K::DrawTexture
            | K::PushClip
            | K::PopClip
            | K::PushOpacity
            | K::PopOpacity
            | K::DrawLine
            | K::DrawEllipse
            | K::DrawCircle
            | K::DrawRoundedRect
            | K::DrawBezier
            | K::DrawCubicBezier
            | K::DrawPolyline
            | K::FillTriangle
            | K::FillQuad
            | K::DrawPicture
            | K::PushGeometryClip
            | K::PopGeometryClip
            | K::PushOpacityMask
            | K::PopOpacityMask
            | K::PushBlendMode
            | K::PopBlendMode
            | K::DrawGlyphRun
            | K::DrawHintedGlyphs
            | K::DrawVertexMesh
            | K::DrawPointBatch
            | K::DrawDotGrid
    )
}
fn update_depth(k: K, d: &mut [u32; 4]) -> Result<()> {
    let state = match k {
        K::PushClip | K::PushGeometryClip => Some((0, true)),
        K::PopClip | K::PopGeometryClip => Some((0, false)),
        K::PushOpacity => Some((1, true)),
        K::PopOpacity => Some((1, false)),
        K::PushOpacityMask => Some((2, true)),
        K::PopOpacityMask => Some((2, false)),
        K::PushBlendMode => Some((3, true)),
        K::PopBlendMode => Some((3, false)),
        _ => None,
    };
    if let Some((i, push)) = state {
        d[i] = if push {
            d[i].checked_add(1)
        } else {
            d[i].checked_sub(1)
        }
        .ok_or(ArchiveError::InvalidData)?;
    }
    Ok(())
}
fn affine(m: Matrix4) -> bool {
    let m = m.0;
    [m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1]]
        .iter()
        .all(|v| v.is_finite())
        && m[0][2] == 0.0
        && m[0][3] == 0.0
        && m[1][2] == 0.0
        && m[1][3] == 0.0
        && m[2][0] == 0.0
        && m[2][1] == 0.0
        && m[2][2] == 1.0
        && m[2][3] == 0.0
        && m[3][2] == 0.0
        && m[3][3] == 1.0
}
