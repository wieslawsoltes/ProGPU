//! Original static text producer and retained outline instance generation.
use super::*;
use crate::{
    font::ManagedFont,
    path::Segment,
    scene::Scene,
    text::paint::{
        TextPaintCommand, TextPaintPlan, TextPainter,
        retained::{RetainedGlyphPaint, RetainedGlyphSink},
    },
    vector::{GpuPathRecord, PathFigure, PathGeometry, PathSegment, PathSegmentKind},
};
use bytemuck::{Pod, Zeroable};
use std::collections::HashMap;

/// Original 112-byte analytic outline placement; metadata is record index,
/// coverage gamma/explicit transfer, sample grid and the original zero word.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
pub struct StaticRetainedGlyphInstance {
    pub transform: [[f32; 4]; 4],
    pub color: [f32; 4],
    pub min_bounds: [f32; 2],
    pub max_bounds: [f32; 2],
    pub metadata: [f32; 4],
}
const _: () = assert!(size_of::<StaticRetainedGlyphInstance>() == 112);

/// Actual immutable font identities, unique outline records and ordered source
/// placements. No atlas coverage or hinted-source identity is fabricated.
#[derive(Clone, Debug, Default)]
pub struct StaticRetainedGlyphGeometry {
    fonts: Vec<ManagedFont>,
    records: Vec<GpuPathRecord>,
    segments: Vec<Segment>,
    instances: Vec<StaticRetainedGlyphInstance>,
}
impl StaticRetainedGlyphGeometry {
    pub fn fonts(&self) -> &[ManagedFont] {
        &self.fonts
    }
    pub fn records(&self) -> &[GpuPathRecord] {
        &self.records
    }
    pub fn segments(&self) -> &[Segment] {
        &self.segments
    }
    pub fn instances(&self) -> &[StaticRetainedGlyphInstance] {
        &self.instances
    }
    pub fn is_empty(&self) -> bool {
        self.instances.is_empty()
    }
}
#[derive(Default)]
struct Builder {
    cache: HashMap<((usize, usize), u16), u32>,
    output: StaticRetainedGlyphGeometry,
}
impl Builder {
    fn glyph(&mut self, font: &ManagedFont, glyph: u16) -> Result<Option<u32>, Error> {
        let key = (font.instance_identity(), glyph);
        if let Some(index) = self.cache.get(&key) {
            return Ok(Some(*index));
        }
        let Some(outline) = font
            .glyph_outline(glyph)
            .map_err(|e| Error::Text(Box::new(e.into())))?
        else {
            return Ok(None);
        };
        let mut path = PathGeometry::default();
        path.figures
            .try_reserve_exact(outline.figures().len())
            .map_err(|_| Error::Capacity)?;
        for source in outline.figures() {
            let mut figure = PathFigure::new(source.start, true);
            figure
                .segments
                .try_reserve_exact(source.segments.len())
                .map_err(|_| Error::Capacity)?;
            for segment in &source.segments {
                figure.segments.push(PathSegment::new(match segment.kind {
                    0 => PathSegmentKind::Line(segment.points[1]),
                    1 => PathSegmentKind::Quadratic([segment.points[1], segment.points[2]]),
                    2 => PathSegmentKind::Cubic([
                        segment.points[1],
                        segment.points[2],
                        segment.points[3],
                    ]),
                    _ => return Err(Error::ResourceDomain),
                }));
            }
            path.figures.push(figure);
        }
        let compiled = path.compile_fill_path()?;
        let Some(mut record) = compiled.record else {
            return Ok(None);
        };
        if compiled.segments.is_empty() {
            return Ok(None);
        }
        let index = u32::try_from(self.output.records.len()).map_err(|_| Error::Capacity)?;
        // Original shader carries the index in a scalar float. Reject loss of
        // exact ownership identity before appending any cache/output record.
        if index >= 1 << 24 {
            return Err(Error::Capacity);
        }
        record.start_segment = record
            .start_segment
            .checked_add(u32::try_from(self.output.segments.len()).map_err(|_| Error::Capacity)?)
            .ok_or(Error::Capacity)?;
        u32::try_from(
            self.output
                .segments
                .len()
                .checked_add(compiled.segments.len())
                .ok_or(Error::Capacity)?,
        )
        .map_err(|_| Error::Capacity)?;
        self.output
            .records
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        self.output
            .segments
            .try_reserve(compiled.segments.len())
            .map_err(|_| Error::Capacity)?;
        self.output
            .fonts
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        self.cache.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.output.records.push(record);
        self.output.segments.extend(compiled.segments);
        self.output.fonts.push(font.clone());
        self.cache.insert(key, index);
        Ok(Some(index))
    }
}
impl RetainedGlyphSink for Builder {
    fn try_glyph(
        &mut self,
        paint: RetainedGlyphPaint<'_>,
    ) -> std::result::Result<bool, crate::text::paint::TextPaintError> {
        let Some(index) = self.glyph(paint.font, paint.glyph)? else {
            return Ok(false);
        };
        let bounds = self.output.records[index as usize].bounds;
        let em = paint.font_size / f32::from(paint.font.metadata().units_per_em);
        let count = self
            .output
            .instances
            .len()
            .checked_add(paint.pass_count)
            .ok_or(Error::Capacity)?;
        u32::try_from(count).map_err(|_| Error::Capacity)?;
        self.output
            .instances
            .try_reserve(paint.pass_count)
            .map_err(|_| Error::Capacity)?;
        for pass in 0..paint.pass_count {
            let transform = Matrix4([
                [em * paint.scale_x, 0.0, 0.0, 0.0],
                [em * paint.italic_skew, -em, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0],
                [
                    paint.position[0] + pass as f32 * paint.bold_offset,
                    paint.position[1],
                    0.0,
                    1.0,
                ],
            ]) * paint.transform;
            self.output.instances.push(StaticRetainedGlyphInstance {
                transform: transform.0,
                color: paint.color,
                min_bounds: [bounds[0], bounds[1]],
                max_bounds: [bounds[2], bounds[3]],
                metadata: [
                    index as f32,
                    paint.coverage_gamma,
                    paint.sample_grid as f32,
                    0.0,
                ],
            });
        }
        Ok(true)
    }
}

pub(super) struct Compilation {
    pub chunks: HashMap<usize, Scene>,
    pub retained: StaticRetainedGlyphGeometry,
    #[cfg(feature = "gpu")]
    pub atlas: Option<Arc<FrozenAtlas>>,
}
struct Plans {
    painter: TextPainter,
    plans: Vec<(usize, TextPaintPlan)>,
    retained: StaticRetainedGlyphGeometry,
}
fn plans(source: &ArchivePicture, options: &StaticDxfCompileOptions) -> Result<Plans, Error> {
    let mut painter = TextPainter::default();
    let mut builder = Builder::default();
    let mut plans = Vec::new();
    let mut clips = super::clip::Clips::default();
    for (index, command) in source.commands.iter().enumerate() {
        let transform = if command.transform == Matrix4([[0.0; 4]; 4]) {
            Matrix4::IDENTITY
        } else {
            command.transform
        };
        match command.kind {
            K::PushClip => {
                clips.push(command.rect, transform)?;
                continue;
            }
            K::PopClip => {
                clips.pop();
                continue;
            }
            K::DrawText | K::DrawGlyphRun => {}
            _ => continue,
        }
        // Preserve source validation before missing-font/array early returns.
        if command.text_hinting_mode == crate::text::paint::TextHintingMode::IntegerPixel {
            return Err(Error::UnsupportedExecution);
        }
        let mut plain = command.clone();
        plain.use_gpu_transforms = false;
        let mut replay = ArchiveReplayOptions::default();
        replay.materials = options.materials.clone();
        replay.text = options.text.clone();
        replay.text_context.dpi_scale = options.dpi_scale;
        let resolved;
        let plain = if let Some(environment) = &options.materials {
            resolved = environment
                .command(&plain)
                .map_err(super::super::drawing_error)?;
            resolved.as_ref()
        } else {
            &plain
        };
        if let Some(draw) = super::super::replay::text::prepare(plain, &replay)
            .map_err(super::super::drawing_error)?
        {
            let mut context = replay.text_context;
            context.transform = transform;
            context.use_mvp = true;
            context.static_zoom = options.static_zoom;
            context.gpu_transforms_active = command.use_gpu_transforms;
            context.camera_view = Matrix4::IDENTITY;
            context.clip = clips.active();
            let plan = painter
                .plan_retained(&draw, context, &mut builder)
                .map_err(|e| Error::Text(Box::new(e)))?;
            plans.try_reserve(1).map_err(|_| Error::Capacity)?;
            plans.push((index, plan));
        }
    }
    Ok(Plans {
        painter,
        plans,
        retained: builder.output,
    })
}
pub(super) fn compile(
    source: &ArchivePicture,
    options: &StaticDxfCompileOptions,
) -> Result<Compilation, Error> {
    let prepared = plans(source, options)?;
    let mut chunks = HashMap::new();
    chunks
        .try_reserve(prepared.plans.len())
        .map_err(|_| Error::Capacity)?;
    for (index, plan) in prepared.plans {
        let mut chunk = Scene::default();
        for command in plan.commands() {
            match command {
                TextPaintCommand::Vector(path) => chunk.draw_text_path(path.clone())?,
                TextPaintCommand::Atlas(_) => return Err(Error::UnsupportedExecution),
            }
        }
        chunks.insert(index, chunk);
    }
    Ok(Compilation {
        chunks,
        retained: prepared.retained,
        #[cfg(feature = "gpu")]
        atlas: None,
    })
}

#[cfg(feature = "gpu")]
pub(super) struct FrozenAtlas {
    pub device: wgpu::Device,
    pub coverage: crate::text::glyph::GlyphCoverageTexture,
    pub color: crate::text::glyph::GlyphColorTexture,
    pub statistics: crate::text::glyph::GlyphAtlasStatistics,
    pub producer_metrics: crate::text::glyph::GlyphGpuMetrics,
    pub residency: crate::text::glyph::GlyphAtlasResidency,
}
#[cfg(feature = "gpu")]
impl std::fmt::Debug for FrozenAtlas {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StaticDxfFrozenGlyphAtlas")
            .field("statistics", &self.statistics)
            .field("residency", &self.residency)
            .finish_non_exhaustive()
    }
}
#[cfg(feature = "gpu")]
pub(super) async fn compile_with_atlas(
    source: &ArchivePicture,
    options: &StaticDxfCompileOptions,
    mut atlas: crate::text::glyph::GlyphAtlas,
) -> Result<Compilation, Error> {
    use crate::text::paint::ResolvedTextCommand;
    if atlas.is_batch_active() {
        return Err(Error::ResourceDomain);
    }
    let mut prepared = plans(source, options)?;
    let mut chunks = HashMap::new();
    chunks
        .try_reserve(prepared.plans.len())
        .map_err(|_| Error::Capacity)?;
    atlas.begin_batch().await?;
    let start = atlas.batch_usage_count();
    let mut has_instances = false;
    for (index, plan) in prepared.plans {
        let (commands, styles) = prepared
            .painter
            .resolve_pending(&mut atlas, plan)
            .await
            .map_err(|e| Error::Text(Box::new(e)))?;
        if !styles.is_empty() {
            return Err(Error::ResourceDomain);
        }
        let mut chunk = Scene::default();
        for command in commands {
            match command {
                ResolvedTextCommand::Vector(path) => chunk.draw_text_path(path)?,
                ResolvedTextCommand::Glyph(instance) => {
                    chunk.append_static_dxf_glyph(instance)?;
                    has_instances = true;
                }
            }
        }
        chunks.insert(index, chunk);
    }
    atlas.end_batch().await?;
    atlas.flush_pending().await?;
    let residency = atlas.capture_batch_residency(start, None)?;
    if !atlas.contains_residency(&residency) {
        return Err(Error::ResourceDomain);
    }
    // Exclusive ownership freezes actual atlas slots, source keys and physical
    // texture generation. No caller can mutate this atlas after publication.
    Ok(Compilation {
        chunks,
        retained: prepared.retained,
        atlas: has_instances.then(|| {
            Arc::new(FrozenAtlas {
                device: atlas.device().clone(),
                coverage: atlas.coverage().clone(),
                color: atlas.color().clone(),
                statistics: atlas.statistics(),
                producer_metrics: atlas.gpu_metrics(),
                residency,
            })
        }),
    })
}
