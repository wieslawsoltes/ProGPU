use super::*;
use std::collections::HashMap;

pub(super) fn compile_options(
    device: &wgpu::Device,
    format: NativeTargetFormat,
    options: NativeSceneRendererOptions,
    resource_scope: u64,
) -> NativeSceneCompileOptions {
    let limits = device.limits();
    NativeSceneCompileOptions {
        format,
        engine_flags: options.engine_flags,
        resource_scope,
        borrows_shared_vector_pipeline: false,
        maximum_buffer_bytes: limits.max_buffer_size,
        maximum_storage_bytes: u64::from(limits.max_storage_buffer_binding_size),
        antialias_padding_pixels: options.antialias_padding_pixels,
    }
}
pub(super) fn admit_plan(
    device: &wgpu::Device,
    format: NativeTargetFormat,
    options: NativeSceneRendererOptions,
    plan: &NativeSceneResourcePlan,
    external: &NativeExternalImages,
) -> Result<(), Error> {
    let actual = plan.options();
    let expected = compile_options(device, format, options, actual.resource_scope);
    if external.device() != device
        || external.metadata() != plan.draws().external_images()
        || actual.format != expected.format
        || actual.engine_flags != expected.engine_flags
        || actual.maximum_buffer_bytes != expected.maximum_buffer_bytes
        || actual.maximum_storage_bytes != expected.maximum_storage_bytes
        || actual.antialias_padding_pixels.to_bits() != expected.antialias_padding_pixels.to_bits()
    {
        return Err(Error::ResourceDomain);
    }
    Ok(())
}

/// Exact occurrence indices stay aligned with the original replay operations.
/// Whole-source/device and nested-picture preflight precedes GPU preparation.
pub(super) struct NativeSceneCandidate {
    pub plan: Arc<NativeSceneResourcePlan>,
    pub raster: Arc<NativeSceneRasterPages>,
    pub replay: Arc<NativeReplayPlan>,
    pub masks: Vec<Arc<NativeMaskRequest>>,
    pub mask_indices: Vec<Option<usize>>,
    pub shaders: Vec<Option<Arc<NativeShaderEffectPlan>>>,
    pub rgb: Vec<Option<Arc<NativeRgbGlyphPage>>>,
}
impl NativeSceneCandidate {
    pub fn new(
        plan: Arc<NativeSceneResourcePlan>,
        pool: &NativeLayerTargetPool,
        path_atlas: u32,
        glyph_atlas: u32,
        retained_raster: Option<Arc<NativeSceneRasterPages>>,
    ) -> Result<Self, Error> {
        let target_plan = Arc::new(pool.plan(
            plan.frame(),
            plan.layers().layer_budget(),
            plan.layers().cache_budget(),
            NativeLayerPoolOptions {
                root_color: plan.budget().destination_sampling,
                depth: plan.draws().metrics().three_d_draw_count != 0,
            },
        )?);
        let replay = Arc::new(NativeReplayPlan::compile(plan.clone(), target_plan)?);
        let raster = if let Some(raster) = retained_raster {
            if !Arc::ptr_eq(raster.source(), &plan) {
                return Err(Error::ResourceDomain);
            }
            raster
        } else {
            Arc::new(plan.raster_pages(path_atlas, glyph_atlas, false)?)
        };
        let count = replay.operations().len();
        let mut masks = Vec::new();
        masks.try_reserve(count).map_err(|_| Error::Capacity)?;
        let mut identities = HashMap::<usize, usize>::new();
        identities.try_reserve(count).map_err(|_| Error::Capacity)?;
        let mut mask_indices = slots(count)?;
        let mut shaders = slots(count)?;
        let mut rgb = slots(count)?;
        for (index, operation) in replay.operations().iter().enumerate() {
            let request = match operation {
                NativeReplayOperation::Bundle(b) => b.mask.as_ref(),
                NativeReplayOperation::PopLayer(p) => p.composite.mask.as_ref(),
                NativeReplayOperation::Rgb { draw: Some(d), .. } => d.mask.as_ref(),
                _ => None,
            };
            if let Some(request) = request {
                let identity = Arc::as_ptr(request) as usize;
                let selected = *identities.entry(identity).or_insert_with(|| {
                    let i = masks.len();
                    masks.push(request.clone());
                    i
                });
                mask_indices[index] = Some(selected);
            }
            if let NativeReplayOperation::PopLayer(p) = operation {
                if let Some(NativeReplayEffect::Shader(shader)) = &p.effect {
                    shaders[index] = Some(Arc::new(NativeShaderEffectPlan::new(shader.clone())?));
                }
            }
            if let NativeReplayOperation::Rgb { draw: Some(d), .. } = operation {
                let frame = plan
                    .draws()
                    .rgb_frames()
                    .get(d.page_index as usize)
                    .ok_or(Error::ResourceDomain)?
                    .clone();
                // Pooled physical dimensions never replace the source frame's
                // logical extent, translation or original scissor.
                rgb[index] = Some(Arc::new(NativeRgbGlyphPage::compile_for_target(
                    frame,
                    d.scissor,
                    d.allocation.size,
                    plan.options().maximum_buffer_bytes,
                    plan.options().engine_flags,
                )?));
            }
        }
        Ok(Self {
            plan,
            raster,
            replay,
            masks,
            mask_indices,
            shaders,
            rgb,
        })
    }
    pub fn preflight(
        &self,
        pages: &NativeScenePageEngine,
        masks: &NativeMaskEngine,
        shaders: &NativeShaderEffectEngine,
        pictures: &NativePictureRuntime,
        external: &NativeExternalImages,
    ) -> Result<NativePictureWorkMetrics, Error> {
        let ordinary = pages.preflight(&self.raster, external, pictures)?;
        let mut mask_metrics = NativeMaskMetrics::default();
        for request in &self.masks {
            mask_metrics.add(masks.preflight(request, pictures)?)?;
        }
        let mut shader_metrics = Vec::new();
        shader_metrics
            .try_reserve(self.shaders.len())
            .map_err(|_| Error::Capacity)?;
        for plan in self.shaders.iter().flatten() {
            shader_metrics.push(shaders.preflight_sources(plan, pictures)?);
        }
        metrics::preflight(&self.plan, ordinary, mask_metrics, &shader_metrics)
    }
}
pub(super) fn slots<T>(count: usize) -> Result<Vec<Option<T>>, Error> {
    let mut values = Vec::new();
    values
        .try_reserve_exact(count)
        .map_err(|_| Error::Capacity)?;
    values.resize_with(count, || None);
    Ok(values)
}
