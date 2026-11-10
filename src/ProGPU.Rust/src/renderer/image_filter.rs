//! Complete retained image-filter operation order and actual result generations.
use super::color_filter as cf;
use super::*;
use crate::{
    image::{
        ColorFilterComputeEngine, ColorFilterOperation, ColorTables, GpuImage, ImageAlpha,
        ImageDraw, ImageSamplingPath, ImageSource, PreparedColorFilterCompute,
    },
    image_filter::{
        FilterRect, FilterTransform, ImageFilterComputeEngine, ImageFilterOperation as Operation,
        ImageFilterPlan, ImageFilteredImageDraw, PreparedImageFilterCompute,
    },
    scene::LayerBlend,
};
mod capture;
mod gaussian;
mod preflight;
mod source;

/// Actual complete image-filter preparation and ordered replay inventory.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ImageFilterMetrics {
    /// Final retained filtered-image consumers.
    pub draws: u32,
    /// Actual original lowered graph operations.
    pub operations: u32,
    /// Original render/capture passes, including nested source render work.
    pub render_passes: u32,
    /// Canonical ordered compute passes.
    pub compute_passes: u32,
    /// Original complete zero-radius texture copies.
    pub copy_passes: u32,
    /// Final source mip-generation passes.
    pub mip_passes: u32,
    /// Original render draws and compute dispatches before final consumers;
    /// texture copies remain independently counted. Includes nested source work.
    pub replay_draws: u64,
    /// Actual newly allocated intermediate/capture/mip bytes.
    pub texture_bytes: u64,
    /// Immutable uniform and table upload bytes.
    pub uniform_bytes: u64,
    /// Immutable capture/final geometry payload.
    pub geometry_bytes: u64,
    /// Original uploaded convolution coefficient bytes.
    pub kernel_bytes: u64,
    /// Actual canonical compute workgroups.
    pub workgroups: u64,
    /// Complete original copied texel bytes, independently of compute.
    pub texture_copy_bytes: u64,
    /// Actual retained original scene/brush/image source captures.
    pub source_captures: u32,
    /// Actual nested prepared source draw/dispatch inventory.
    pub source_draws: u64,
    /// Actual source pixel, mask and glyph upload bytes.
    pub source_upload_bytes: u64,
    /// Original source target-Clear upload bytes on each replay.
    pub source_replay_uniform_bytes: u64,
}
impl ImageFilterMetrics {
    pub(super) fn merge(&mut self, other: Self) -> Result<(), Error> {
        let mut merged = *self;
        macro_rules! add {($($f:ident),*)=>{$(merged.$f=merged.$f.checked_add(other.$f).ok_or(Error::Capacity)?;)*};}
        add!(
            draws,
            operations,
            render_passes,
            compute_passes,
            copy_passes,
            mip_passes,
            replay_draws,
            texture_bytes,
            uniform_bytes,
            geometry_bytes,
            kernel_bytes,
            workgroups,
            texture_copy_bytes,
            source_captures,
            source_draws,
            source_upload_bytes,
            source_replay_uniform_bytes
        );
        *self = merged;
        Ok(())
    }
    fn capture(&mut self, value: cf::ColorFilterMetrics) -> Result<(), Error> {
        self.merge(Self {
            render_passes: value.render_passes,
            compute_passes: value.compute_passes,
            mip_passes: value.mip_passes,
            replay_draws: u64::from(value.render_passes)
                + u64::from(value.compute_passes)
                + u64::from(value.mip_passes),
            texture_bytes: value.texture_bytes,
            uniform_bytes: value.uniform_bytes,
            geometry_bytes: value.geometry_bytes,
            ..Default::default()
        })
    }
    fn compute(&mut self, value: &PreparedColorFilterCompute) -> Result<(), Error> {
        let m = value.metrics();
        self.merge(Self {
            compute_passes: m.compute_passes,
            replay_draws: u64::from(m.compute_passes),
            texture_bytes: m.texture_bytes,
            uniform_bytes: m.parameter_bytes,
            workgroups: m.workgroups,
            ..Default::default()
        })
    }
    fn image_compute(&mut self, value: &PreparedImageFilterCompute) -> Result<(), Error> {
        let m = value.metrics();
        self.merge(Self {
            compute_passes: m.compute_passes,
            copy_passes: u32::from(m.texture_copy_bytes != 0),
            replay_draws: u64::from(m.compute_passes),
            texture_bytes: m.texture_bytes,
            uniform_bytes: m.parameter_bytes,
            kernel_bytes: m.kernel_bytes,
            workgroups: m.workgroups,
            texture_copy_bytes: m.texture_copy_bytes,
            ..Default::default()
        })
    }
}
enum Pass {
    Capture(cf::Capture),
    Scene(capture::SceneCapture),
    Color(PreparedColorFilterCompute),
    Image(PreparedImageFilterCompute),
    Gaussian(gaussian::GaussianPass),
}
impl Pass {
    fn output(&self) -> &GpuImage {
        match self {
            Self::Capture(p) => &p.output,
            Self::Scene(p) => p.output(),
            Self::Color(p) => p.output(),
            Self::Image(p) => p.output(),
            Self::Gaussian(p) => p.output(),
        }
    }
    fn encode(&self, encoder: &mut wgpu::CommandEncoder) -> Result<(), Error> {
        match self {
            Self::Capture(p) => p.encode(encoder),
            Self::Scene(p) => return p.encode(encoder),
            Self::Color(p) => p.encode_owned(encoder),
            Self::Image(p) => p.encode_owned(encoder),
            Self::Gaussian(p) => p.encode(encoder),
        }
        Ok(())
    }
    fn current(&self) -> bool {
        match self {
            Self::Scene(p) => p.current(),
            _ => true,
        }
    }
}
struct FilteredDraw {
    _source: ImageFilteredImageDraw,
    _input: GpuImage,
    _plan: ImageFilterPlan,
    results: Vec<GpuImage>,
    passes: Vec<Pass>,
    mips: Option<cf::Mips>,
    draw: cf::DrawResources,
    masked: wgpu::RenderPipeline,
}
pub(super) struct PreparedImageFilteredImages {
    draws: Vec<FilteredDraw>,
    metrics: ImageFilterMetrics,
}
impl PreparedImageFilteredImages {
    pub(super) fn encode_sources(
        &self,
        _renderer: &Renderer,
        encoder: &mut wgpu::CommandEncoder,
    ) -> Result<(), Error> {
        if self
            .draws
            .iter()
            .flat_map(|d| &d.passes)
            .any(|p| !p.current())
        {
            return Err(Error::ResourceDomain);
        }
        for draw in &self.draws {
            let _retained_results = &draw.results;
            for pass in &draw.passes {
                pass.encode(encoder)?;
            }
            if let Some(mips) = &draw.mips {
                mips.encode(encoder);
            }
        }
        Ok(())
    }
    pub(super) fn draw(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        index: usize,
        mask: Option<&wgpu::BindGroup>,
    ) -> Result<(), Error> {
        let draw = self.draws.get(index).ok_or(Error::ResourceDomain)?;
        if let Some(mask) = mask {
            draw.draw.draw_masked(pass, &draw.masked, mask);
        } else {
            draw.draw.draw(pass);
        }
        Ok(())
    }
}
pub(super) struct ImageFilterState {
    compute: ImageFilterComputeEngine,
    color: ColorFilterComputeEngine,
    gaussian: gaussian::GaussianState,
    copy: Option<wgpu::RenderPipeline>,
    matrix: Option<wgpu::RenderPipeline>,
    solid: Option<wgpu::RenderPipeline>,
    mips: std::collections::HashMap<wgpu::TextureFormat, wgpu::RenderPipeline>,
    final_pipelines: [Option<wgpu::RenderPipeline>; 2],
}
impl ImageFilterState {
    fn new(device: &wgpu::Device, preference: ExecutionPreference) -> Result<Self, Error> {
        Ok(Self {
            compute: ImageFilterComputeEngine::new(device, preference)?,
            color: ColorFilterComputeEngine::new(device, preference)?,
            gaussian: Default::default(),
            copy: None,
            matrix: None,
            solid: None,
            mips: Default::default(),
            final_pipelines: [None, None],
        })
    }
}
impl PreparedScene {
    /// Actual complete original image-filter allocations and replay work.
    pub fn image_filter_metrics(&self) -> ImageFilterMetrics {
        self.image_filters
            .as_ref()
            .map_or_else(Default::default, |f| f.metrics)
    }
}
impl Renderer {
    /// Select the original GPU route before first image-filter preparation.
    pub fn configure_image_filters(
        &mut self,
        preference: ExecutionPreference,
    ) -> Result<(), Error> {
        if self.image_filters.is_some() {
            return Err(Error::ResourceDomain);
        }
        self.image_filters = Some(ImageFilterState::new(&self.device, preference)?);
        Ok(())
    }
    pub(super) fn preflight_image_filters(&self, scene: &Scene) -> Result<(u64, u32), Error> {
        preflight::scene(self, scene, 0)
    }
    pub(super) fn validate_image_filters(
        &self,
        compiled: &CompiledScene,
    ) -> Result<(u64, u32), Error> {
        let mut cost = (0u64, 0u32);
        for draw in &compiled.image_filtered_images {
            let mesh = draw
                .draw
                .compile(compiled.dpi_scale(), compiled.image_sampling)?;
            if bytemuck::cast_slice::<_, f32>(mesh.vertices())
                .iter()
                .any(|v| !v.is_finite())
                || std::mem::size_of_val(mesh.vertices()) as u64
                    > self.device.limits().max_buffer_size
                || std::mem::size_of_val(mesh.indices()) as u64
                    > self.device.limits().max_buffer_size
            {
                return Err(Error::Capacity);
            }
            preflight::add_cost(&mut cost, preflight::draw(self, draw, 0)?)?;
        }
        Ok(cost)
    }
    pub(super) async fn prepare_image_filters(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        if compiled.image_filtered_images.is_empty() {
            return Ok(());
        }
        self.validate_image_filters(compiled)?;
        if self.image_sampling != Some(compiled.image_sampling) {
            return Err(Error::UnsupportedExecution);
        }
        let mut state = match self.image_filters.take() {
            Some(s) => s,
            None => ImageFilterState::new(&self.device, ExecutionPreference::Automatic)?,
        };
        let mut images = self.images.take().unwrap_or_default();
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let result = self
            .build_image_filters(&mut state, &mut images, compiled, frame, prepared)
            .await;
        let error = scope.pop().await;
        self.image_filters = Some(state);
        self.images = Some(images);
        if let Some(error) = error {
            return Err(error.into());
        }
        result
    }
    async fn build_image_filters(
        &self,
        state: &mut ImageFilterState,
        images: &mut image::ImageState,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        for masked in [false, true] {
            let slot = usize::from(masked);
            if state.final_pipelines[slot].is_none() {
                state.final_pipelines[slot] = Some(
                    image::pipeline(
                        &self.device,
                        self.format,
                        false,
                        LayerBlend::SrcOver,
                        masked,
                    )
                    .await?,
                );
            }
        }
        let mut draws = Vec::new();
        draws
            .try_reserve_exact(compiled.image_filtered_images.len())
            .map_err(|_| Error::Capacity)?;
        let mut metrics = ImageFilterMetrics::default();
        for source in &compiled.image_filtered_images {
            let plan = source.plan()?;
            let input = upload(self, images, &source.draw.image, prepared).await?;
            let mut passes = Vec::new();
            let mut results = Vec::new();
            passes
                .try_reserve_exact(plan.steps().len() + 1)
                .map_err(|_| Error::Capacity)?;
            results
                .try_reserve_exact(plan.steps().len() + 1)
                .map_err(|_| Error::Capacity)?;
            if ImageFilteredImageDraw::needs_normalization(&source.draw.image) {
                let capture = normalize(
                    self,
                    state,
                    images,
                    &input,
                    compiled.image_sampling,
                    &mut metrics,
                )
                .await?;
                results.push(capture.output.clone());
                passes.push(Pass::Capture(capture));
            } else {
                results.push(input.clone());
            }
            for step in plan.steps() {
                let inputs: Vec<_> = step
                    .inputs
                    .iter()
                    .map(|i| {
                        results
                            .get(*i as usize)
                            .cloned()
                            .ok_or(Error::ResourceDomain)
                    })
                    .collect::<Result<_, _>>()?;
                let pass = self
                    .prepare_filter_operation(
                        state,
                        images,
                        &step.operation,
                        &inputs,
                        step.size,
                        compiled.image_sampling,
                        &mut metrics,
                        &mut passes,
                    )
                    .await?;
                if pass.output().size() != step.size
                    || pass.output().texture().mip_level_count() != step.mip_levels
                {
                    return Err(Error::ResourceDomain);
                }
                results.push(pass.output().clone());
                passes.push(pass);
            }
            let mut output = results
                .get(plan.output() as usize)
                .ok_or(Error::ResourceDomain)?
                .clone();
            let mips = if source.draw.sampling.uses_mipmaps() {
                let format = output.texture().format();
                if !state.mips.contains_key(&format) {
                    state.mips.insert(
                        format,
                        image::pipeline(&self.device, format, true, LayerBlend::SrcOver, false)
                            .await?,
                    );
                }
                let mut m = cf::ColorFilterMetrics::default();
                let mip = cf::prepare_mips_format(
                    &self.device,
                    state.mips.get(&format).ok_or(Error::ResourceDomain)?,
                    output,
                    format,
                    &mut m,
                )?;
                output = mip.output.clone();
                metrics.capture(m)?;
                Some(mip)
            } else {
                None
            };
            let mut final_draw = source.draw.clone();
            final_draw.image = output.into();
            let mut m = cf::ColorFilterMetrics::default();
            let draw = cf::prepare_image(
                &self.device,
                images,
                state.final_pipelines[0]
                    .as_ref()
                    .ok_or(Error::ResourceDomain)?,
                &final_draw,
                frame,
                compiled.image_sampling,
                &mut m,
            )?;
            metrics.capture(m)?;
            metrics.draws = metrics.draws.checked_add(1).ok_or(Error::Capacity)?;
            metrics.operations = metrics
                .operations
                .checked_add(plan.steps().len() as u32)
                .ok_or(Error::Capacity)?;
            draws.push(FilteredDraw {
                _source: source.clone(),
                _input: input,
                _plan: plan,
                results,
                passes,
                mips,
                draw,
                masked: state.final_pipelines[1]
                    .as_ref()
                    .ok_or(Error::ResourceDomain)?
                    .clone(),
            });
        }
        prepared.metrics.geometry_bytes = prepared
            .metrics
            .geometry_bytes
            .checked_add(metrics.geometry_bytes)
            .ok_or(Error::Capacity)?;
        prepared.metrics.uniform_bytes = prepared
            .metrics
            .uniform_bytes
            .checked_add(metrics.uniform_bytes)
            .ok_or(Error::Capacity)?;
        prepared.image_filters = Some(PreparedImageFilteredImages { draws, metrics });
        Ok(())
    }
    async fn prepare_filter_operation(
        &self,
        state: &mut ImageFilterState,
        images: &mut image::ImageState,
        operation: &Operation,
        inputs: &[GpuImage],
        size: [u32; 2],
        sampling: ImageSamplingPath,
        metrics: &mut ImageFilterMetrics,
        passes: &mut Vec<Pass>,
    ) -> Result<Pass, Error> {
        let first = || inputs.first().ok_or(Error::ResourceDomain);
        let second = || inputs.get(1).ok_or(Error::ResourceDomain);
        let pixel_frame = Frame {
            width: size[0],
            height: size[1],
            dpi_scale: 1.0,
        };
        let local = RenderFrame {
            frame: pixel_frame,
            root: pixel_frame,
            origin: [0; 2],
            source: Some(crate::composition::SourceRenderFrame::image_filter(
                size,
                crate::vector::Matrix4::IDENTITY,
            )?),
        };
        match operation {
            Operation::GaussianBlur(parameters) => {
                let p = state
                    .gaussian
                    .prepare(&self.device, first()?, *parameters)
                    .await?;
                metrics.merge(ImageFilterMetrics {
                    compute_passes: 2,
                    replay_draws: 2,
                    texture_bytes: u64::from(size[0]) * u64::from(size[1]) * 8,
                    uniform_bytes: 32,
                    workgroups: u64::from(size[0].div_ceil(16))
                        * u64::from(size[1].div_ceil(16))
                        * 2,
                    ..Default::default()
                })?;
                Ok(Pass::Gaussian(p))
            }
            Operation::Morphology(p) => {
                let p = state.compute.prepare_morphology(first()?, *p).await?;
                metrics.image_compute(&p)?;
                Ok(Pass::Image(p))
            }
            Operation::Convolution { parameters, kernel } => {
                let p = state
                    .compute
                    .prepare_convolution(first()?, parameters, kernel)
                    .await?;
                metrics.image_compute(&p)?;
                Ok(Pass::Image(p))
            }
            Operation::Displacement(p) => {
                let p = state
                    .compute
                    .prepare_displacement(first()?, second()?, p, size)
                    .await?;
                metrics.image_compute(&p)?;
                Ok(Pass::Image(p))
            }
            Operation::Magnifier(p) => {
                let p = state.compute.prepare_magnifier(first()?, p).await?;
                metrics.image_compute(&p)?;
                Ok(Pass::Image(p))
            }
            Operation::Lighting(p) => {
                let p = state.compute.prepare_lighting(first()?, p).await?;
                metrics.image_compute(&p)?;
                Ok(Pass::Image(p))
            }
            Operation::ColorMatrix(matrix) => {
                if state.matrix.is_none() {
                    state.matrix = Some(cf::matrix_pipeline(&self.device).await?);
                }
                let mut m = cf::ColorFilterMetrics::default();
                let p = cf::prepare_matrix_target(
                    &self.device,
                    state.matrix.as_ref().ok_or(Error::ResourceDomain)?,
                    first()?,
                    *matrix,
                    local,
                    &mut m,
                )?;
                metrics.capture(m)?;
                Ok(Pass::Capture(p))
            }
            Operation::Solid(color) => {
                if state.solid.is_none() {
                    state.solid = Some(cf::solid_pipeline(&self.device).await?);
                }
                let mut m = cf::ColorFilterMetrics::default();
                let p = cf::prepare_solid_target(
                    self,
                    state.solid.as_ref().ok_or(Error::ResourceDomain)?,
                    *color,
                    local,
                    &mut m,
                )?;
                metrics.capture(m)?;
                Ok(Pass::Capture(p))
            }
            Operation::ColorTable(t) => {
                color_pass(
                    state,
                    ColorFilterOperation::Table(t.clone()),
                    first()?,
                    None,
                    metrics,
                )
                .await
            }
            Operation::NonlinearColor(p) => {
                color_pass(
                    state,
                    ColorFilterOperation::Nonlinear(*p),
                    first()?,
                    None,
                    metrics,
                )
                .await
            }
            Operation::Overdraw(p) => {
                color_pass(
                    state,
                    ColorFilterOperation::Overdraw(*p),
                    first()?,
                    None,
                    metrics,
                )
                .await
            }
            Operation::TransferToSrgb(color) => {
                let identity = std::array::from_fn::<u8, 256, _>(|i| i as u8);
                let t = color.to_srgb_table();
                color_pass(
                    state,
                    ColorFilterOperation::Table(ColorTables::channels(&identity, t, t, t)?),
                    first()?,
                    None,
                    metrics,
                )
                .await
            }
            Operation::Blend(mode) => {
                let p = state
                    .color
                    .prepare_blend(first()?, second()?, *mode, false)
                    .await?;
                metrics.compute(&p)?;
                Ok(Pass::Color(p))
            }
            Operation::Arithmetic(parameters) => {
                let p = state
                    .color
                    .prepare_arithmetic(
                        first()?,
                        second()?,
                        parameters.coefficients,
                        parameters.enforce_premultiplied,
                    )
                    .await?;
                metrics.compute(&p)?;
                Ok(Pass::Color(p))
            }
            Operation::UnsupportedRuntimeBlender(_) => Err(Error::UnsupportedExecution),
            Operation::ShaderSource {
                source, transform, ..
            } => {
                source::prepare(
                    self, state, images, source, *transform, size, sampling, metrics, passes,
                )
                .await
            }
            _ => {
                let p = capture::operation(self, operation, inputs, size, sampling).await?;
                p.add_metrics(metrics)?;
                Ok(Pass::Scene(p))
            }
        }
    }
}
async fn color_pass(
    state: &mut ImageFilterState,
    operation: ColorFilterOperation,
    background: &GpuImage,
    foreground: Option<&GpuImage>,
    metrics: &mut ImageFilterMetrics,
) -> Result<Pass, Error> {
    let p = state
        .color
        .prepare(&operation, background, foreground)
        .await?;
    metrics.compute(&p)?;
    Ok(Pass::Color(p))
}
async fn upload(
    renderer: &Renderer,
    images: &mut image::ImageState,
    source: &ImageSource,
    prepared: &mut PreparedScene,
) -> Result<GpuImage, Error> {
    match source {
        ImageSource::Gpu(i) => Ok(i.clone()),
        ImageSource::Pixels(i) => {
            let (image, bytes, mips) = images
                .upload(&renderer.device, &renderer.queue, i, false)
                .await?;
            prepared.metrics.image_upload_bytes = prepared
                .metrics
                .image_upload_bytes
                .checked_add(bytes)
                .ok_or(Error::Capacity)?;
            prepared.metrics.image_mip_passes = prepared
                .metrics
                .image_mip_passes
                .checked_add(mips)
                .ok_or(Error::Capacity)?;
            prepared.metrics.image_texture_bytes = prepared
                .metrics
                .image_texture_bytes
                .checked_add(
                    u64::from(image.size()[0])
                        * u64::from(image.size()[1])
                        * if i.format() == crate::image::ImageFormat::R8 {
                            1
                        } else {
                            4
                        },
                )
                .ok_or(Error::Capacity)?;
            Ok(image)
        }
    }
}
async fn normalize(
    renderer: &Renderer,
    state: &mut ImageFilterState,
    images: &mut image::ImageState,
    input: &GpuImage,
    sampling: ImageSamplingPath,
    metrics: &mut ImageFilterMetrics,
) -> Result<cf::Capture, Error> {
    if state.copy.is_none() {
        state.copy = Some(
            image::pipeline(
                &renderer.device,
                wgpu::TextureFormat::Rgba8Unorm,
                false,
                LayerBlend::SrcOver,
                false,
            )
            .await?,
        );
    }
    let size = input.size();
    let draw = ImageDraw::new(input.clone(), [0.0, 0.0, size[0] as f32, size[1] as f32]);
    let mut m = cf::ColorFilterMetrics::default();
    let p = cf::prepare_capture_image(
        &renderer.device,
        images,
        state.copy.as_ref().ok_or(Error::ResourceDomain)?,
        &draw,
        Frame {
            width: size[0],
            height: size[1],
            dpi_scale: 1.0,
        },
        sampling,
        &mut m,
    )?;
    metrics.capture(m)?;
    Ok(p)
}
