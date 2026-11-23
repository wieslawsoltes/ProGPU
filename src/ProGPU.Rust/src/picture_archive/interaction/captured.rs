//! Actual non-wire source input annotations. They never alter raster archives.
use super::*;
/// Original source-only primitive override and point-region scope kinds.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum SourceHitGeometryKind {
    #[default]
    None,
    Excluded,
    Line,
    Rectangle,
    RoundedRectangle,
    Ellipse,
    PointRectangleBegin,
    PointRectangleEnd,
    PointEmptyBegin,
}
/// Unsnapped source geometry delivered by an actual owning source adapter.
/// These values are intentionally absent from original picture archive v1-v10.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct SourceHitGeometry {
    pub kind: SourceHitGeometryKind,
    pub coordinates: [f32; 4],
}
/// Original raster command plus separately captured source input contract.
/// Constructing this value does not qualify an adapter or infer its source facts.
#[derive(Clone, Debug)]
pub struct CapturedHitCommand {
    pub raster: Arc<ArchiveCommand>,
    pub provider: Option<Arc<ArchivePicture>>,
    pub source: SourceHitGeometry,
    pub image_hit_scope: bool,
    pub source_opacity_scope: bool,
}
impl CapturedHitCommand {
    pub fn ordinary(raster: Arc<ArchiveCommand>) -> Self {
        Self {
            raster,
            provider: None,
            source: Default::default(),
            image_hit_scope: false,
            source_opacity_scope: false,
        }
    }
}
impl ArchiveHitTestCache {
    /// Apply an explicitly captured original source contract before ordinary
    /// geometry lowering. Errors fault all index publication until clear.
    pub fn add_captured_command(
        &mut self,
        source: Arc<CapturedHitCommand>,
        transform: Matrix4,
        id: Option<i32>,
    ) -> Result<()> {
        if self.failed {
            return Err(ArchiveError::InvalidData);
        }
        let result = (|| {
            push(&mut self.captured, source.clone())?;
            self.captured_command(&source, normalize(transform), id)
        })();
        if result.is_err() {
            self.failed = true;
        }
        result
    }
    fn captured_command(
        &mut self,
        source: &CapturedHitCommand,
        m: Matrix4,
        id: Option<i32>,
    ) -> Result<()> {
        use SourceHitGeometryKind as S;
        let c = &source.raster;
        if let Some(p) = &c.pen {
            p.to_pen()?.validate_join_semantics()?;
        }
        if c.kind == K::DrawHintedGlyphs {
            return Err(ArchiveError::Unsupported);
        }
        if matches!(
            source.source.kind,
            S::PointRectangleBegin | S::PointRectangleEnd | S::PointEmptyBegin
        ) {
            let begin = source.source.kind != S::PointRectangleEnd;
            let empty = source.source.kind == S::PointEmptyBegin;
            if c.kind != if begin { K::PushOpacity } else { K::PopOpacity }
                || (begin && c.font_size != 1.0)
            {
                return Err(ArchiveError::Unsupported);
            }
            if self.image_depth != 0 {
                return Ok(());
            }
            if begin {
                let r = source.source.coordinates;
                if (empty && r != [0.0; 4])
                    || r.iter().any(|v| !v.is_finite())
                    || !(r[0] + r[2]).is_finite()
                    || !(r[1] + r[3]).is_finite()
                    || r[2] < 0.0
                    || r[3] < 0.0
                {
                    return Err(ArchiveError::Unsupported);
                }
                push(&mut self.point_stack, self.participation)?;
                if !empty && self.participation != HitPrimitive::REGION_ONLY && invertible(m) {
                    self.participation = HitPrimitive::POINT_ONLY;
                    let id = self.id(id, c.hit_test_id);
                    self.append(HitPrimitive::rectangle_fill(
                        id,
                        edges(r),
                        [0.0; 2],
                        m,
                        self.primitives.len() as f32,
                    ))?;
                }
                self.participation = HitPrimitive::REGION_ONLY;
            } else {
                self.participation = self.point_stack.pop().ok_or(ArchiveError::InvalidData)?;
            }
            return Ok(());
        }
        if source.source.kind != S::None {
            let c = source.source.apply(c)?;
            if source.source.kind != S::Excluded {
                return self.command(&c, m, source.provider.as_deref(), id);
            }
            return Ok(());
        }
        if self.image_depth != 0 {
            return self.command(c, m, source.provider.as_deref(), id);
        }
        if c.kind == K::PushClip && source.image_hit_scope {
            if self.active_opacity.unwrap_or(1.0) > 0.0001 && !c.use_gpu_transforms && invertible(m)
            {
                let id = self.id(id, c.hit_test_id);
                self.rectangle_coverage(c.rect, m, id, self.primitives.len() as f32)?;
            }
            self.image_depth = 1;
            return Ok(());
        }
        if c.kind == K::PushOpacity && source.source_opacity_scope {
            let mut c = (**c).clone();
            c.font_size = 1.0;
            return self.command(&c, m, source.provider.as_deref(), id);
        }
        self.command(c, m, source.provider.as_deref(), id)
    }
}
impl SourceHitGeometry {
    fn apply(self, raster: &ArchiveCommand) -> Result<ArchiveCommand> {
        use SourceHitGeometryKind as S;
        let p = self.coordinates;
        if p.iter().any(|v| !v.is_finite()) {
            return Err(ArchiveError::Unsupported);
        }
        let mut result = raster.clone();
        match (self.kind, raster.kind) {
            (S::Excluded, K::DrawEllipse | K::FillTriangle) => {}
            (S::Line, K::DrawLine) => {
                result.position = [p[0], p[1]];
                result.position2 = [p[2], p[3]];
            }
            (S::Rectangle, K::DrawRect) | (S::RoundedRectangle, K::DrawRoundedRect) => {
                if p[2] < 0.0 || p[3] < 0.0 {
                    return Err(ArchiveError::Unsupported);
                }
                result.rect = p;
            }
            (S::Ellipse, K::DrawEllipse) => {
                if p[2] < 0.0 || p[3] < 0.0 {
                    return Err(ArchiveError::Unsupported);
                }
                result.position2 = [p[0], p[1]];
                result.radius_x = p[2];
                result.radius_y = p[3];
            }
            _ => return Err(ArchiveError::Unsupported),
        }
        Ok(result)
    }
}
