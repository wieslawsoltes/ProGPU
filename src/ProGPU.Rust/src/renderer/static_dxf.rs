//! Actual-target execution of the original retained static extension0 lease.
use super::*;
use crate::{
    picture_archive::{
        PreparedStaticDxf, StaticDxfEngine, StaticDxfMetrics, StaticDxfTarget, StaticHatchTarget,
    },
    scene::StaticDxfDraw,
    shader_effect::{ManagedShaderMask, SampleLattice},
    vector::Matrix4,
};
use std::collections::HashSet;

/// Upload work and target preparation are independent. A warm generation keeps
/// its actual resident resources while reporting zero repeated source uploads.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct StaticDxfFrameMetrics {
    pub upload: StaticDxfMetrics,
    pub target: StaticDxfMetrics,
}
pub(super) struct PreparedStaticDxfDraws {
    draws: Vec<Option<PreparedStaticDxf>>,
    metrics: Vec<StaticDxfFrameMetrics>,
}
impl PreparedStaticDxfDraws {
    pub(super) fn draw_count(&self, index: usize) -> Result<u32, Error> {
        self.draws
            .get(index)
            .ok_or(Error::ResourceDomain)?
            .as_ref()
            .map_or(Ok(0), |draw| {
                u32::try_from(draw.metrics().draw_count()).map_err(|_| Error::Capacity)
            })
    }
    pub(super) fn draw(&self, pass: &mut wgpu::RenderPass<'_>, index: usize) -> Result<(), Error> {
        if let Some(draw) = self.draws.get(index).ok_or(Error::ResourceDomain)? {
            // The enclosing ordered target already selected the actual binary
            // scissor. Static inner command clips are not replayed by the source.
            draw.draw(pass, None)?;
        }
        Ok(())
    }
    pub(super) fn retain_submitted(&self, queue: &wgpu::Queue) {
        for draw in self.draws.iter().flatten() {
            draw.retain_submitted(queue);
        }
    }
}
impl PreparedScene {
    pub fn static_dxf_metrics(&self) -> impl Iterator<Item = StaticDxfFrameMetrics> + '_ {
        self.static_dxf
            .iter()
            .flat_map(|p| p.metrics.iter().copied())
    }
}
fn target(
    draw: &StaticDxfDraw,
    frame: RenderFrame,
    format: wgpu::TextureFormat,
    maximum: u32,
    blend: crate::scene::LayerBlend,
    mask: Option<Arc<ManagedShaderMask>>,
) -> Result<StaticDxfTarget, Error> {
    draw.validate()?;
    let uniforms = frame.uniforms(maximum)?;
    Ok(StaticDxfTarget {
        frame: StaticHatchTarget {
            projection: Matrix4(std::array::from_fn(|r| {
                std::array::from_fn(|c| uniforms.projection[r * 4 + c])
            })),
            placement: draw.transform,
            viewport: draw.viewport,
            size: [frame.width, frame.height],
            canvas_size: uniforms.canvas_size,
            dpi_scale: frame.dpi_scale,
            format,
            sample_count: 1,
        },
        blend,
        mask,
    })
}
struct Cost {
    generations: HashSet<usize>,
    bytes: u64,
    passes: u32,
    draws: usize,
}
impl Cost {
    fn new() -> Self {
        Self {
            generations: HashSet::new(),
            bytes: 0,
            passes: 0,
            draws: 0,
        }
    }
    fn add(
        &mut self,
        renderer: &Renderer,
        draw: &StaticDxfDraw,
        frame: RenderFrame,
        state: &crate::scene::CompiledDrawState,
    ) -> Result<(), Error> {
        draw.validate_source_policy(
            frame
                .source
                .is_some_and(|source| source.suppress_cached_cleartype()),
        )?;
        self.draws = self.draws.checked_add(1).ok_or(Error::Capacity)?;
        if self.draws > 16384 {
            return Err(Error::Capacity);
        }
        // Original null payload is a no-op, after source/target policy checks.
        let Some(buffer) = &draw.buffer else {
            return Ok(());
        };
        let resident = StaticDxfEngine::preflight_upload(&renderer.device, buffer)?;
        if frame.empty() {
            return Ok(());
        }
        target(
            draw,
            frame,
            renderer.format,
            renderer.device.limits().max_texture_dimension_2d,
            state.blend,
            None,
        )?
        .validate(&renderer.device)?;
        if state.count != 0
            && frame.origin != [0; 2]
            && !state.materialized
            && !state.local_coverage
        {
            return Err(Error::UnsupportedExecution);
        }
        self.generations
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        if self.generations.insert(buffer.identity()) {
            self.bytes = self.bytes.checked_add(resident).ok_or(Error::Capacity)?;
            self.passes = self
                .passes
                .checked_add(
                    u32::try_from(buffer.path_raster_count()).map_err(|_| Error::Capacity)?,
                )
                .ok_or(Error::Capacity)?;
        }
        self.bytes = self
            .bytes
            .checked_add(buffer.target_uniform_bytes(state.count != 0))
            .ok_or(Error::Capacity)?;
        if self.bytes > 512 * 1024 * 1024 || self.passes > 16384 {
            return Err(Error::Capacity);
        }
        Ok(())
    }
}
impl Renderer {
    pub(super) fn preflight_static_dxf(
        &self,
        scene: &Scene,
        frame: RenderFrame,
    ) -> Result<(u64, u32), Error> {
        let (states, changes) = scene.compile_states(frame.dpi_scale)?;
        let mut current = 0;
        let mut cursor = 0;
        let mut cost = Cost::new();
        for (index, draw) in scene.static_dxf_draws() {
            for next in changes.get(cursor..=index).unwrap_or(&[]).iter().flatten() {
                current = *next;
            }
            cursor = index + 1;
            cost.add(
                self,
                draw,
                frame,
                states.get(current).ok_or(Error::ResourceDomain)?,
            )?;
        }
        Ok((cost.bytes, cost.passes))
    }
    pub(super) fn validate_static_dxf(
        &self,
        compiled: &CompiledScene,
        frame: RenderFrame,
    ) -> Result<(u64, u32), Error> {
        let mut current = 0;
        let mut cost = Cost::new();
        for command in &compiled.draws {
            match command {
                DrawRange::State(index) => current = *index,
                DrawRange::StaticDxf(index) => cost.add(
                    self,
                    compiled
                        .static_dxf
                        .get(*index)
                        .ok_or(Error::ResourceDomain)?,
                    frame,
                    compiled.states.get(current).ok_or(Error::ResourceDomain)?,
                )?,
                _ => {}
            }
        }
        Ok((cost.bytes, cost.passes))
    }
    pub(super) async fn prepare_static_dxf(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        if compiled.static_dxf.is_empty() {
            return Ok(());
        }
        self.validate_static_dxf(compiled, frame)?;
        let mut targets = vec![None; compiled.static_dxf.len()];
        let mut current = 0;
        for command in &compiled.draws {
            if let DrawRange::State(index) = command {
                current = *index;
                continue;
            }
            let DrawRange::StaticDxf(index) = command else {
                continue;
            };
            let draw = &compiled.static_dxf[*index];
            if draw.buffer.is_none() {
                continue;
            }
            let state = prepared
                .masks
                .as_ref()
                .and_then(|m| m.resolved.get(current))
                .unwrap_or(&compiled.states[current]);
            if !frame.clip_intersects(state.clip) {
                continue;
            }
            let mask = if state.count == 0 {
                None
            } else {
                if state.count != 1 {
                    return Err(Error::UnsupportedExecution);
                }
                let mut uniforms = state.masks[0];
                if state.local_coverage {
                    uniforms.coordinate0[0] -= frame.origin[0] as f32;
                    uniforms.coordinate0[1] -= frame.origin[1] as f32;
                } else if frame.origin != [0; 2] {
                    return Err(Error::UnsupportedExecution);
                }
                let lattice = SampleLattice {
                    origin: [0; 2],
                    size: [frame.width, frame.height],
                };
                let mask = if let Some(coverage) = &state.coverage {
                    let image = self.masks.as_mut().ok_or(Error::ResourceDomain)?.image(
                        &self.device,
                        &self.queue,
                        &coverage.source,
                        &mut UploadMetrics::default(),
                        &mut HashSet::new(),
                    )?;
                    ManagedShaderMask::sampled(
                        lattice,
                        uniforms,
                        image,
                        if coverage.sampling == crate::mask::MaskSampling::Linear {
                            crate::image::ImageSampling::Linear
                        } else {
                            crate::image::ImageSampling::Nearest
                        },
                    )?
                } else {
                    ManagedShaderMask::analytic(lattice, uniforms)?
                };
                Some(Arc::new(mask))
            };
            targets[*index] = Some(target(
                draw,
                frame,
                self.format,
                self.device.limits().max_texture_dimension_2d,
                state.blend,
                mask,
            )?);
        }
        let mut draws = Vec::new();
        let mut metrics = Vec::new();
        let mut resident_generations = HashSet::new();
        resident_generations
            .try_reserve(targets.len())
            .map_err(|_| Error::Capacity)?;
        draws
            .try_reserve_exact(targets.len())
            .map_err(|_| Error::Capacity)?;
        metrics
            .try_reserve_exact(targets.len())
            .map_err(|_| Error::Capacity)?;
        if targets.iter().all(Option::is_none) {
            draws.resize_with(targets.len(), || None);
            metrics.resize(targets.len(), StaticDxfFrameMetrics::default());
            prepared.static_dxf = Some(PreparedStaticDxfDraws { draws, metrics });
            return Ok(());
        }
        if self.static_dxf_engine.is_none() {
            self.static_dxf_engine = Some(StaticDxfEngine::new(&self.device, &self.queue)?);
        }
        let engine = self
            .static_dxf_engine
            .as_mut()
            .ok_or(Error::ResourceDomain)?;
        engine.retire_unused_generations();
        for (draw, target) in compiled.static_dxf.iter().zip(targets) {
            let (Some(buffer), Some(target)) = (&draw.buffer, target) else {
                draws.push(None);
                metrics.push(StaticDxfFrameMetrics::default());
                continue;
            };
            let uploaded = engine.upload(buffer).await?;
            let value = engine.prepare(&uploaded, target).await?;
            let upload = uploaded.metrics();
            let target = value.metrics();
            let add = |a: &mut u64, b: u64| -> Result<(), Error> {
                *a = a.checked_add(b).ok_or(Error::Capacity)?;
                Ok(())
            };
            add(
                &mut prepared.metrics.geometry_bytes,
                upload
                    .geometry_upload_bytes
                    .checked_add(upload.boundary_upload_bytes)
                    .ok_or(Error::Capacity)?,
            )?;
            add(
                &mut prepared.metrics.material_bytes,
                upload.material_upload_bytes,
            )?;
            add(
                &mut prepared.metrics.glyph_upload_bytes,
                upload.text_upload_bytes,
            )?;
            add(
                &mut prepared.metrics.uniform_bytes,
                target
                    .uniform_upload_bytes
                    .checked_add(target.mask_uniform_upload_bytes)
                    .ok_or(Error::Capacity)?,
            )?;
            if resident_generations.insert(buffer.identity()) {
                add(
                    &mut prepared.metrics.atlas_bytes,
                    upload.coverage_texture_bytes,
                )?;
                add(
                    &mut prepared.metrics.glyph_atlas_bytes,
                    upload.glyph_texture_bytes,
                )?;
                prepared.metrics.glyph_instances = prepared
                    .metrics
                    .glyph_instances
                    .checked_add(
                        u32::try_from(buffer.text_instance_count()).map_err(|_| Error::Capacity)?,
                    )
                    .ok_or(Error::Capacity)?;
            }
            if upload.generation_cache_hits == 0 {
                prepared.metrics.rasterized_paths = prepared
                    .metrics
                    .rasterized_paths
                    .checked_add(upload.path_rasters)
                    .ok_or(Error::Capacity)?;
            }
            metrics.push(StaticDxfFrameMetrics { upload, target });
            draws.push(Some(value));
        }
        prepared.static_dxf = Some(PreparedStaticDxfDraws { draws, metrics });
        Ok(())
    }
}
