//! Original builder placements and bounded positioned storage retirement.
use super::*;
use std::sync::{Mutex, Weak};
mod buffers;
mod raw;
pub use buffers::TextBlobRunBuffer;
const LIMIT: usize = 1_048_576;
/// Original bounded typed positioned scratch inventory.
pub const MAX_REUSABLE_POSITIONED_GLYPHS: usize = 1024;
#[derive(Debug)]
enum Placement {
    Default([f32; 2]),
    Horizontal {
        y: f32,
        positions: Vec<f32>,
    },
    Positioned {
        positions: Arc<[[f32; 2]]>,
        initialized: bool,
    },
    RotationScale(Vec<RotationScaleMatrix>),
    Completed(TextBlobRun),
}
#[derive(Debug)]
struct Run {
    font: CanvasFont,
    glyphs: Arc<[u16]>,
    glyphs_initialized: bool,
    placement: Placement,
    text: Vec<u8>,
    clusters: Vec<u32>,
    leaseable: bool,
}
impl Run {
    fn glyphs_mut(&mut self) -> &mut [u16] {
        let glyphs =
            Arc::get_mut(&mut self.glyphs).expect("unpublished builder glyphs have one owner");
        if !self.glyphs_initialized {
            glyphs.fill(0);
            self.glyphs_initialized = true;
        }
        glyphs
    }
    fn points_mut(&mut self) -> &mut [[f32; 2]] {
        let Placement::Positioned {
            positions,
            initialized,
        } = &mut self.placement
        else {
            unreachable!("typed positioned buffer")
        };
        let values = Arc::get_mut(positions).expect("unpublished builder positions have one owner");
        if !*initialized {
            values.fill([0.0; 2]);
            *initialized = true;
        }
        values
    }
    fn snapshot(&self) -> Result<TextBlobRun> {
        if let Placement::Completed(run) = &self.placement {
            return TextBlobRun::new(
                run.font.clone(),
                copy(&run.glyphs)?,
                copy(&run.positions)?,
                run.transforms.as_ref().map(|v| copy(v)).transpose()?,
            );
        }
        let glyphs = copy(&self.glyphs)?;
        let (points, transforms) = match &self.placement {
            Placement::Default(origin) => (
                Arc::from(self.font.glyph_positions(&glyphs, *origin)?),
                None,
            ),
            Placement::Horizontal { y, positions } => {
                let mut points = allocate(positions.len(), [0.0, *y])?;
                for (out, &x) in points.iter_mut().zip(positions) {
                    out[0] = x;
                }
                (Arc::from(points), None)
            }
            Placement::Positioned { positions, .. } => (copy(positions)?, None),
            Placement::RotationScale(transforms) => {
                let mut points = allocate(transforms.len(), [0.0; 2])?;
                for (out, matrix) in points.iter_mut().zip(transforms) {
                    *out = matrix.translation;
                }
                (Arc::from(points), Some(copy(transforms)?))
            }
            Placement::Completed(_) => unreachable!(),
        };
        TextBlobRun::new(self.font.clone(), glyphs, points, transforms)
    }
}
#[derive(Debug)]
struct Scratch {
    glyphs: Arc<[u16]>,
    positions: Arc<[[f32; 2]]>,
    clusters: Vec<u32>,
}
#[derive(Debug, Default)]
struct Cache {
    positioned: Option<Scratch>,
}
/// Actual leased arrays return only after the immutable blob generation retires.
/// Independently retained run/renderer owners prevent later mutable admission.
#[derive(Debug)]
pub(super) struct BlobLease {
    owner: Weak<Mutex<Cache>>,
    scratch: Option<Scratch>,
}
impl Drop for BlobLease {
    fn drop(&mut self) {
        let Some(owner) = self.owner.upgrade() else {
            return;
        };
        let Some(scratch) = self.scratch.take() else {
            return;
        };
        let mut cache = owner.lock().unwrap_or_else(|e| e.into_inner());
        if cache.positioned.is_none() && scratch.glyphs.len() <= MAX_REUSABLE_POSITIONED_GLYPHS {
            cache.positioned = Some(scratch);
        }
    }
}
/// Source builder with safe exclusive mutable run leases. A live buffer borrows
/// this builder, so Build/reset cannot invalidate a writable alias or raw pointer.
#[derive(Debug, Default)]
pub struct TextBlobBuilder {
    runs: Vec<Run>,
    scratch: Arc<Mutex<Cache>>,
    glyph_count: usize,
}
#[derive(Clone, Copy)]
enum Kind {
    Default([f32; 2]),
    Horizontal(f32),
    Positioned,
    RotationScale,
}
impl TextBlobBuilder {
    /// Empty original builder; Build returns None until a run is allocated.
    pub fn new() -> Self {
        Self::default()
    }
    /// Number of actual retained pending runs, including zero-length runs.
    pub fn run_count(&self) -> usize {
        self.runs.len()
    }
    /// Drop pending source storage and bounded scratch without touching snapshots.
    pub fn reset(&mut self) {
        self.runs.clear();
        self.glyph_count = 0;
        self.scratch = Arc::new(Mutex::new(Cache::default()));
    }
    fn allocate(
        &mut self,
        font: &CanvasFont,
        count: usize,
        text_count: usize,
        kind: Kind,
        leaseable: bool,
    ) -> Result<&mut Run> {
        let total = self.glyph_count.checked_add(count).ok_or(Error::Capacity)?;
        if total > LIMIT || text_count > 16 * LIMIT || self.runs.len() >= 65_536 {
            return Err(Error::Capacity.into());
        }
        self.runs.try_reserve(1).map_err(|_| Error::Capacity)?;
        let mut cached = None;
        if matches!(kind, Kind::Positioned) && text_count == 0 {
            let mut cache = self.scratch.lock().unwrap_or_else(|e| e.into_inner());
            if let Some(scratch) = cache.positioned.as_mut() {
                if scratch.glyphs.len() == count
                    && Arc::get_mut(&mut scratch.glyphs).is_some()
                    && Arc::get_mut(&mut scratch.positions).is_some()
                {
                    cached = cache.positioned.take();
                }
            }
        }
        let (glyphs, clusters, placement) = if let Some(scratch) = cached {
            (
                scratch.glyphs,
                scratch.clusters,
                Placement::Positioned {
                    positions: scratch.positions,
                    initialized: false,
                },
            )
        } else {
            let glyphs = Arc::from(allocate(count, 0u16)?);
            let clusters = allocate(count, 0u32)?;
            let placement = match kind {
                Kind::Default(origin) => Placement::Default(origin),
                Kind::Horizontal(y) => Placement::Horizontal {
                    y,
                    positions: allocate(count, 0.0)?,
                },
                Kind::Positioned => Placement::Positioned {
                    positions: Arc::from(allocate(count, [0.0; 2])?),
                    initialized: false,
                },
                Kind::RotationScale => {
                    Placement::RotationScale(allocate(count, RotationScaleMatrix::EMPTY)?)
                }
            };
            (glyphs, clusters, placement)
        };
        let run = Run {
            font: font.clone(),
            glyphs,
            glyphs_initialized: false,
            placement,
            text: allocate(text_count, 0u8)?,
            clusters,
            leaseable,
        };
        self.runs.push(run);
        self.glyph_count = total;
        Ok(self.runs.last_mut().expect("newly appended source run"))
    }
    /// Original nominal run; unused bounds remain an ignored source hint.
    pub fn allocate_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        origin: [f32; 2],
        _bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, f32>> {
        Ok(TextBlobRunBuffer::new(
            self.allocate(font, count, 0, Kind::Default(origin), false)?,
            false,
        ))
    }
    /// Original nominal run with actual writable text bytes and cluster slots.
    pub fn allocate_text_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        origin: [f32; 2],
        text_bytes: usize,
        _bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, f32>> {
        Ok(TextBlobRunBuffer::new(
            self.allocate(font, count, text_bytes, Kind::Default(origin), false)?,
            false,
        ))
    }
    /// Original independent X positions and shared baseline Y.
    pub fn allocate_horizontal_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        y: f32,
        _bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, f32>> {
        Ok(TextBlobRunBuffer::new(
            self.allocate(font, count, 0, Kind::Horizontal(y), false)?,
            false,
        ))
    }
    /// Original horizontal run carrying text and cluster metadata during building.
    pub fn allocate_horizontal_text_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        y: f32,
        text_bytes: usize,
        _bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, f32>> {
        Ok(TextBlobRunBuffer::new(
            self.allocate(font, count, text_bytes, Kind::Horizontal(y), false)?,
            false,
        ))
    }
    /// Original typed positioned run eligible for bounded zero-copy Build.
    pub fn allocate_positioned_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        _bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, [f32; 2]>> {
        Ok(TextBlobRunBuffer::new(
            self.allocate(font, count, 0, Kind::Positioned, true)?,
            true,
        ))
    }
    /// Original positioned text run; snapshot publication copies this inventory.
    pub fn allocate_positioned_text_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        text_bytes: usize,
        _bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, [f32; 2]>> {
        Ok(TextBlobRunBuffer::new(
            self.allocate(font, count, text_bytes, Kind::Positioned, false)?,
            false,
        ))
    }
    /// Original per-glyph four-coefficient frames, initially zero rather than identity.
    pub fn allocate_rotation_scale_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        _bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, RotationScaleMatrix>> {
        Ok(TextBlobRunBuffer::new(
            self.allocate(font, count, 0, Kind::RotationScale, false)?,
            false,
        ))
    }
    /// Original framed run with actual writable text and cluster arrays.
    pub fn allocate_rotation_scale_text_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        text_bytes: usize,
        _bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, RotationScaleMatrix>> {
        Ok(TextBlobRunBuffer::new(
            self.allocate(font, count, text_bytes, Kind::RotationScale, false)?,
            false,
        ))
    }
    /// Safe replacement for the original raw positioned buffer: all mutable spans
    /// are initialized before borrowing and Build keeps its copied-snapshot route.
    pub fn allocate_raw_positioned_run(
        &mut self,
        font: &CanvasFont,
        count: usize,
        _bounds: Option<[f32; 4]>,
    ) -> Result<TextBlobRunBuffer<'_, [f32; 2]>> {
        let run = self.allocate(font, count, 0, Kind::Positioned, false)?;
        run.glyphs_mut();
        run.points_mut();
        run.clusters.fill(0);
        Ok(TextBlobRunBuffer::new(run, false))
    }
    /// Original complete zero-copy single-positioned or copied multi-run snapshot.
    /// Successful Build clears pending runs; failures leave them available to retry.
    pub fn build(&mut self) -> Result<Option<TextBlob>> {
        if self.runs.is_empty() {
            return Ok(None);
        }
        if self.runs.len() == 1
            && self.runs[0].leaseable
            && self.runs[0].text.is_empty()
            && self.runs[0].glyphs.len() <= MAX_REUSABLE_POSITIONED_GLYPHS
            && matches!(self.runs[0].placement, Placement::Positioned { .. })
        {
            let run = &mut self.runs[0];
            run.glyphs_mut();
            run.points_mut();
            let Placement::Positioned { positions, .. } = &run.placement else {
                unreachable!()
            };
            let snapshot = TextBlobRun::new(
                run.font.clone(),
                run.glyphs.clone(),
                positions.clone(),
                None,
            )?;
            let mut blob = TextBlob::from_runs(vec![snapshot])?;
            let run = self.runs.pop().expect("single run");
            let Placement::Positioned { positions, .. } = run.placement else {
                unreachable!()
            };
            Arc::get_mut(&mut blob.0)
                .expect("unpublished blob owner")
                ._lease = Some(BlobLease {
                owner: Arc::downgrade(&self.scratch),
                scratch: Some(Scratch {
                    glyphs: run.glyphs,
                    positions,
                    clusters: run.clusters,
                }),
            });
            self.glyph_count = 0;
            return Ok(Some(blob));
        }
        let mut snapshots = Vec::new();
        snapshots
            .try_reserve_exact(self.runs.len())
            .map_err(|_| Error::Capacity)?;
        for run in &self.runs {
            snapshots.push(run.snapshot()?);
        }
        let blob = TextBlob::from_runs(snapshots)?;
        self.runs.clear();
        self.glyph_count = 0;
        Ok(Some(blob))
    }
    /// Original nominal advances copied into a complete positioned source run.
    pub fn add_run(&mut self, glyphs: &[u16], font: &CanvasFont, origin: [f32; 2]) -> Result<()> {
        let positions = font.glyph_positions(glyphs, origin)?;
        self.add_positioned_run(glyphs, font, &positions)
    }
    /// Original horizontal convenience adapter, with zero-filled missing positions.
    pub fn add_horizontal_run(
        &mut self,
        glyphs: &[u16],
        font: &CanvasFont,
        positions: &[f32],
        y: f32,
    ) -> Result<()> {
        if positions.len() > glyphs.len() {
            return Err(Error::InvalidInput.into());
        }
        let mut run = self.allocate_horizontal_run(font, glyphs.len(), y, None)?;
        run.set_glyphs(glyphs)?;
        run.set_positions(positions)
    }
    /// Original positioned convenience adapter uses the copied raw snapshot route.
    pub fn add_positioned_run(
        &mut self,
        glyphs: &[u16],
        font: &CanvasFont,
        positions: &[[f32; 2]],
    ) -> Result<()> {
        if positions.len() > glyphs.len() {
            return Err(Error::InvalidInput.into());
        }
        let mut run = self.allocate_raw_positioned_run(font, glyphs.len(), None)?;
        run.set_glyphs(glyphs)?;
        run.set_positions(positions)
    }
    /// Original rotation/scale convenience adapter with matching source slots.
    pub fn add_rotation_scale_run(
        &mut self,
        glyphs: &[u16],
        font: &CanvasFont,
        positions: &[RotationScaleMatrix],
    ) -> Result<()> {
        if positions.len() > glyphs.len() {
            return Err(Error::InvalidInput.into());
        }
        let mut run = self.allocate_rotation_scale_run(font, glyphs.len(), None)?;
        run.set_glyphs(glyphs)?;
        run.set_positions(positions)
    }
    /// Original explicit contour placement retains each actual visible glyph;
    /// unlike the factory's contiguous slice, skipped slots are independently omitted.
    pub fn add_path_positioned_run(
        &mut self,
        glyphs: &[u16],
        font: &CanvasFont,
        widths: &[f32],
        offsets: &[[f32; 2]],
        path: &RetainedPath,
        align: TextAlign,
    ) -> Result<()> {
        if glyphs.len() != widths.len() || glyphs.len() != offsets.len() {
            return Err(Error::InvalidInput.into());
        }
        if glyphs.is_empty() {
            return Ok(());
        }
        if glyphs.len() > LIMIT {
            return Err(Error::Capacity.into());
        }
        let geometry = path.geometry()?;
        let measured = MeasuredPath::from_geometry(&geometry)?;
        let measure = PathMeasure::new(Some(&measured), false, 1.0)?;
        let length = measure.length();
        let last = glyphs.len() - 1;
        let width = offsets[last][0] + widths[last];
        let aligned = offsets[0][0] + (length - width) * (align as u8 as f32 * 0.5);
        let mut visible = Vec::new();
        let mut matrices = Vec::new();
        visible
            .try_reserve_exact(glyphs.len())
            .map_err(|_| Error::Capacity)?;
        matrices
            .try_reserve_exact(glyphs.len())
            .map_err(|_| Error::Capacity)?;
        for (index, offset) in offsets.iter().enumerate() {
            let half = widths[index] * 0.5;
            let distance = aligned + offset[0] + half;
            if distance < 0.0 || distance >= length {
                continue;
            }
            let Some((point, tangent)) = measure.position_and_tangent(distance) else {
                continue;
            };
            visible.push(glyphs[index]);
            matrices.push(RotationScaleMatrix::new(
                tangent[0],
                tangent[1],
                [
                    point[0] - tangent[0] * half - offset[1] * tangent[1],
                    point[1] - tangent[1] * half + offset[1] * tangent[0],
                ],
            ));
        }
        if visible.is_empty() {
            return Ok(());
        }
        let mut points = allocate(matrices.len(), [0.0; 2])?;
        for (point, matrix) in points.iter_mut().zip(&matrices) {
            *point = matrix.translation;
        }
        let count = visible.len();
        let total = self.glyph_count.checked_add(count).ok_or(Error::Capacity)?;
        if total > LIMIT || self.runs.len() >= 65_536 {
            return Err(Error::Capacity.into());
        }
        self.runs.try_reserve(1).map_err(|_| Error::Capacity)?;
        let completed = TextBlobRun::new(
            font.clone(),
            Arc::from(visible),
            Arc::from(points),
            Some(Arc::from(matrices)),
        )?;
        self.runs.push(Run {
            font: font.clone(),
            glyphs: completed.glyphs.clone(),
            glyphs_initialized: true,
            placement: Placement::Completed(completed),
            text: Vec::new(),
            clusters: Vec::new(),
            leaseable: false,
        });
        self.glyph_count = total;
        Ok(())
    }
}
