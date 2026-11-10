//! Retained wgpu execution of ProGPU's canonical vector and path shaders.
//!
//! CPU geometry is prepared once, uploaded once and retained by an immutable
//! scene. Frames submit ordered vector/text/image ranges without re-upload.
//! wgpu retains encoded dependencies through submission; dropping this renderer
//! never explicitly destroys a buffer still referenced by a submitted frame.

use crate::{
    Error,
    geometry::{Mesh, VectorVertex},
    path::{CoverageAtlas, ExecutionPreference, PathRasterizer},
    scene::{CompiledScene, DrawRange, Scene},
    shaders,
    vector::{GpuBrush, GpuGradientStop, MaterialPage, MaterialTable},
};
use bytemuck::{Pod, Zeroable};
use std::sync::Arc;
use wgpu::util::DeviceExt;
mod backdrop;
mod hatch;
pub use backdrop::BackdropMetrics;
pub use hatch::HatchMetrics;
mod advanced;
mod blend;
mod uniform_backdrop;
pub use advanced::AdvancedBlendMetrics;
pub use uniform_backdrop::{
    PreparedUniformBackdrop, UniformBackdropEngine, UniformBackdropMetrics, UniformBackdropUniforms,
};
mod axis_clip;
pub use axis_clip::{AxisClipMetrics, AxisClipPass, AxisClipReplayMetrics, PreparedAxisClip};
mod visual;
pub use visual::{PreparedVisual, VisualMetrics, VisualRenderOptions};
mod cached_picture;
mod capture;
pub use cached_picture::{CachedPictureMetrics, CachedPictureRaster};
mod owned_shader;
mod source_capture;
pub use owned_shader::{
    OwnedShaderMetrics, OwnedShaderTargetFrame, ShaderEffectPreparationContext,
};
pub use source_capture::{CacheSamplerRaster, CaptureSourceIdentity, CapturedSourceScene};
mod clear;
mod color_filter;
pub use clear::{RenderSubmission, ReplayMetrics, TargetClearRequest};
pub use color_filter::ColorFilterMetrics;
mod groups;
pub use groups::RetainedAaMetrics;
use groups::{GroupTarget, PreparedGroup, PreparedGroups};
mod image;
mod image_effect;
mod series;
pub use series::{GpuSeriesBuffer, SeriesMetrics};
mod image_filter;
mod image_ownership;
pub use image_effect::ImageEffectMetrics;
pub use image_filter::ImageFilterMetrics;
mod layer;
pub use layer::LayerMetrics;
use layer::RenderFrame;
mod bounded_mask;
mod layout;
mod mask;
mod materialized_mask;
pub use materialized_mask::{CapturedMask, MaskBrushFrame};
mod managed_shader;
mod shader_effect;
mod text;
pub use capture::CapturedScene;

/// Physical target dimensions and scalar logical-to-physical DPI.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Frame {
    /// Physical width in pixels.
    pub width: u32,
    /// Physical height in pixels.
    pub height: u32,
    /// Positive finite DPI ratio.
    pub dpi_scale: f32,
}

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct Uniforms {
    projection: [f32; 16],
    mvp: [f32; 16],
    view: [f32; 16],
    canvas_size: [f32; 2],
    dpi_scale: f32,
    pad0: f32,
    render_origin: [f32; 2],
    pad1: [f32; 2],
}
const _: () = assert!(size_of::<Uniforms>() == 224);

impl Frame {
    fn uniforms(self, max_dimension: u32) -> Result<Uniforms, Error> {
        if self.width == 0
            || self.height == 0
            || self.width > max_dimension
            || self.height > max_dimension
            || !self.dpi_scale.is_finite()
            || self.dpi_scale <= 0.0
        {
            return Err(Error::InvalidInput);
        }
        let width = self.width as f32 / self.dpi_scale;
        let height = self.height as f32 / self.dpi_scale;
        let identity = [
            1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0,
        ];
        let mut projection = identity;
        projection[0] = 2.0 / width;
        projection[5] = -2.0 / height;
        projection[10] = -1.0;
        projection[12] = -1.0;
        projection[13] = 1.0;
        if !width.is_finite() || !height.is_finite() || !projection.iter().all(|v| v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        Ok(Uniforms {
            projection,
            mvp: identity,
            view: identity,
            canvas_size: [width, height],
            dpi_scale: self.dpi_scale,
            pad0: 0.0,
            render_origin: [0.0; 2],
            pad1: [-(self.width as f32), -(self.height as f32)],
        })
    }
}

/// Upload counters for one immutable scene generation.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct UploadMetrics {
    /// Original vertex, index and extension boundary payload bytes uploaded once at preparation.
    pub geometry_bytes: u64,
    /// Frame uniform bytes uploaded once at preparation (excludes path compute).
    pub uniform_bytes: u64,
    /// Actual brush/auxiliary bytes uploaded for this immutable generation,
    /// including a required empty-record binding placeholder when applicable.
    pub material_bytes: u64,
    /// Actual registered brush records, excluding the legacy shared placeholder.
    pub material_brushes: u32,
    /// Actual auxiliary records, excluding an empty binding placeholder.
    pub material_records: u32,
    /// Ordered vector/text draw count (zero for empty scenes).
    pub draws: u32,
    /// Distinct path masks rasterized when this generation was prepared.
    pub rasterized_paths: u32,
    /// Owned R8 atlas bytes; zero for analytic-only generations.
    pub atlas_bytes: u64,
    /// Actual 96-byte glyph instances retained by this scene.
    pub glyph_instances: u32,
    /// Actual text style storage uploaded, including its required placeholder.
    pub text_style_bytes: u64,
    /// Actual outline, raster uniform and color bitmap upload bytes during preparation.
    pub glyph_upload_bytes: u64,
    /// Actual submitted glyph coverage requests during preparation.
    pub rasterized_glyphs: u64,
    /// Shared grayscale and color atlas residency, counted once per scene.
    pub glyph_atlas_bytes: u64,
    /// Original strided pixel payload supplied to actual image uploads.
    pub image_upload_bytes: u64,
    /// Distinct actual image texture storage, including generated mip levels.
    pub image_texture_bytes: u64,
    /// Actual GPU mip-generation render passes during preparation.
    pub image_mip_passes: u32,
    /// Original strided red/RGBA payload uploaded for retained coverage masks.
    pub mask_upload_bytes: u64,
    /// Distinct actual red/RGBA mask texture bytes referenced by this scene.
    pub mask_texture_bytes: u64,
    /// Immutable mask/chain bind groups uploaded for this scene.
    pub mask_bindings: u32,
}

/// GPU resources for a frozen scene and physical frame.
pub struct PreparedScene {
    final_target_paths: bool,
    hatches: Option<hatch::PreparedHatches>,
    frame_buffer: wgpu::Buffer,
    material_buffers: (wgpu::Buffer, wgpu::Buffer),
    _native_atlas_leases: Vec<Arc<crate::text::native_atlas::NativeAtlasPlan>>,
    _native_design_leases: Vec<Arc<crate::text::native_design_geometry::NativeDesignVectorPlan>>,
    owner: Arc<()>,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    bindings: wgpu::BindGroup,
    atlas: wgpu::BindGroup,
    draws: Vec<DrawRange>,
    text: Option<text::PreparedText>,
    images: Option<image::PreparedImages>,
    image_source_owners: Option<Arc<image_ownership::ImageSourceOwners>>,
    masks: Option<mask::PreparedMasks>,
    frame: Frame,
    origin: [u32; 2],
    render_frame: RenderFrame,
    layers: LayerMetrics,
    groups: PreparedGroups,
    image_effects: Option<image_effect::PreparedImageEffects>,
    series: Option<series::PreparedSeries>,
    color_filters: Option<color_filter::PreparedColorFilteredImages>,
    image_filters: Option<image_filter::PreparedImageFilteredImages>,
    shaders: Option<shader_effect::PreparedShaders>,
    managed_shaders: Option<managed_shader::PreparedManagedShaders>,
    owned_shaders: owned_shader::PreparedOwnedShaders,
    cached_pictures: cached_picture::PreparedCachedPictures,
    owned_shader_frame: u64,
    advanced: Option<advanced::PreparedAdvanced>,
    backdrops: Option<backdrop::PreparedBackdrops>,
    clears: Option<clear::PreparedClears>,
    metrics: UploadMetrics,
}

impl PreparedScene {
    fn draw_count(&self) -> Result<u32, Error> {
        let mut drawable = true;
        let mut count = 0u32;
        let mut blend = crate::scene::LayerBlend::SrcOver;
        for draw in &self.draws {
            if let DrawRange::State(index) = draw {
                let state = self
                    .masks
                    .as_ref()
                    .and_then(|m| m.states.get(*index))
                    .ok_or(Error::ResourceDomain)?;
                drawable = state.scissor[2] != 0 && state.scissor[3] != 0;
                blend = state.blend;
            } else if drawable
                && !matches!(draw, DrawRange::Advanced(_))
                && !(matches!(draw, DrawRange::HintedText(_, _, Some(_))) && !blend.is_fixed())
            {
                let draws = if let DrawRange::Axis(i) = draw {
                    let child = self.groups.children.get(*i).ok_or(Error::ResourceDomain)?;
                    child
                        .scene
                        .metrics
                        .draws
                        .checked_add(child.clip.metrics().resolve_draws)
                        .ok_or(Error::Capacity)?
                } else if let DrawRange::Series(index) = draw {
                    self.series
                        .as_ref()
                        .ok_or(Error::ResourceDomain)?
                        .draw_count(*index)?
                } else if let DrawRange::BackdropMaterial(index) = draw {
                    let backdrops = self.backdrops.as_ref().ok_or(Error::ResourceDomain)?;
                    if backdrops.is_host(*index) {
                        0
                    } else {
                        backdrops.draw_count(*index)?
                    }
                } else if let DrawRange::ManagedShaderEffect(index) = draw {
                    self.managed_shaders
                        .as_ref()
                        .ok_or(Error::ResourceDomain)?
                        .draw_count(*index)?
                } else {
                    1
                };
                count = count.checked_add(draws).ok_or(Error::Capacity)?;
            }
        }
        if self.advanced.is_none() && self.groups.root.as_ref().is_some_and(|r| !r.fixed) {
            count = count.checked_add(1).ok_or(Error::Capacity)?;
        }
        let filters = self.color_filter_metrics();
        let image_filters = self.image_filter_metrics();
        let image_filter_draws =
            u32::try_from(image_filters.replay_draws).map_err(|_| Error::Capacity)?;
        count
            .checked_add(self.image_effect_metrics().blur_passes)
            .and_then(|v| v.checked_add(self.advanced_blend_metrics().render_passes))
            .and_then(|v| v.checked_add(filters.render_passes))
            .and_then(|v| v.checked_add(filters.compute_passes))
            .and_then(|v| v.checked_add(filters.mip_passes))
            .and_then(|v| v.checked_add(image_filter_draws))
            .ok_or(Error::Capacity)
    }
    /// Physical frame used when preparing this scene.
    pub fn frame(&self) -> Frame {
        self.frame
    }
    /// Actual isolated subtree preparation, excluding this root scene.
    pub fn layer_metrics(&self) -> LayerMetrics {
        self.layers
    }
    /// Actual destination-aware blend resources and per-replay passes.
    pub fn advanced_blend_metrics(&self) -> AdvancedBlendMetrics {
        self.advanced
            .as_ref()
            .map_or_else(AdvancedBlendMetrics::default, |a| a.metrics)
    }
    /// Actual preparation payload counts; target-storage Clear has separately
    /// reported per-replay uploads through `replay_metrics()`.
    pub fn metrics(&self) -> UploadMetrics {
        self.metrics
    }
}

/// A single-device vector/text/image renderer with immutable prepared generations.
pub struct Renderer {
    hatch: Option<hatch::HatchPipeline>,
    owner: Arc<()>,
    device: wgpu::Device,
    queue: wgpu::Queue,
    pipeline: wgpu::RenderPipeline,
    draw_blends: blend::DrawBlendPipelines,
    format: wgpu::TextureFormat,
    brush: wgpu::Buffer,
    stops: wgpu::Buffer,
    atlas: wgpu::BindGroup,
    path_rasterizer: Option<PathRasterizer>,
    signed_winding_policy: crate::path::SignedWindingPolicy,
    materials: MaterialTable,
    text: Option<text::TextState>,
    text_tree_active: bool,
    text_options: crate::text::glyph::GlyphAtlasOptions,
    images: Option<image::ImageState>,
    image_effects: Option<image_effect::ImageEffectState>,
    series: Option<series::SeriesState>,
    color_filters: Option<color_filter::ColorFilterState>,
    image_filters: Option<image_filter::ImageFilterState>,
    shader_engine: Option<crate::shader_effect::ShaderEffectEngine>,
    managed_shader_engine: Option<crate::shader_effect::ManagedShaderEngine>,
    owned_shaders: owned_shader::OwnedShaderState,
    cached_pictures: cached_picture::CachedPictureState,
    visuals: visual::VisualState,
    preparation_mask_depth: std::rc::Rc<std::cell::Cell<u16>>,
    owned_shader_completed: std::sync::atomic::AtomicU64,
    masks: Option<mask::MaskState>,
    materialized_masks: Option<materialized_mask::MaterialMaskState>,
    effects: Option<crate::effects::EffectEngine>,
    advanced: Option<advanced::AdvancedState>,
    backdrop_state: backdrop::BackdropState,
    axis_clip: Option<axis_clip::AxisClipState>,
    clear: Option<clear::ClearPipeline>,
    image_sampling: Option<crate::image::ImageSamplingPath>,
    group_copy: Option<wgpu::RenderPipeline>,
    image_preference: crate::image::ImageSamplingPreference,
}

impl Renderer {
    /// Compile the canonical vector pipeline for a single-sample target format.
    ///
    /// Device and queue must come from the same wgpu device request. This
    /// constructor does not allocate text, path-compute or effect pipelines.
    pub async fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: wgpu::TextureFormat,
    ) -> Result<Self, Error> {
        if !matches!(
            format,
            wgpu::TextureFormat::Rgba8Unorm
                | wgpu::TextureFormat::Bgra8Unorm
                | wgpu::TextureFormat::Rgba8UnormSrgb
                | wgpu::TextureFormat::Bgra8UnormSrgb
                | wgpu::TextureFormat::Rgba16Float
        ) {
            return Err(Error::InvalidInput);
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU canonical Vector.wgsl"),
            source: wgpu::ShaderSource::Wgsl(shaders::VECTOR.into()),
        });
        let attributes = wgpu::vertex_attr_array![0 => Float32x2, 1 => Float32x4, 2 => Float32x2,
            3 => Float32, 4 => Float32x2, 5 => Float32, 6 => Float32, 7 => Float32];
        let pipeline_layout = layout::pipeline(device, layout::Family::Vector, None);
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU analytic vector"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &module,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &[Some(wgpu::VertexBufferLayout {
                    array_stride: size_of::<VectorVertex>() as u64,
                    step_mode: wgpu::VertexStepMode::Vertex,
                    attributes: &attributes,
                })],
            },
            primitive: wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample: wgpu::MultisampleState::default(),
            fragment: Some(wgpu::FragmentState {
                module: &module,
                entry_point: Some("fs_main_unmasked"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: Some(wgpu::BlendState::ALPHA_BLENDING),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });
        // Check pipeline compilation before asking for its inferred layouts.
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let mut brush_bytes = [0u8; 256];
        brush_bytes[4..8].copy_from_slice(&1.0f32.to_ne_bytes());
        let brush = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("ProGPU default solid brush"),
            contents: &brush_bytes,
            usage: wgpu::BufferUsages::STORAGE,
        });
        let stops = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("ProGPU empty gradient stops"),
            contents: &[0; 32],
            usage: wgpu::BufferUsages::STORAGE,
        });
        // The shared general entry requires an atlas binding, even for analytic
        // shapes. Its one-texel placeholder never substitutes a missing path.
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU analytic placeholder"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::R8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        let view = texture.create_view(&Default::default());
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor::default());
        let atlas = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU analytic atlas binding"),
            layout: &pipeline.get_bind_group_layout(1),
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::Sampler(&sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(&view),
                },
            ],
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self {
            hatch: None,
            owner: Arc::new(()),
            device: device.clone(),
            queue: queue.clone(),
            pipeline,
            draw_blends: Default::default(),
            format,
            brush,
            stops,
            atlas,
            path_rasterizer: None,
            signed_winding_policy: Default::default(),
            materials: MaterialTable::default(),
            text: None,
            text_tree_active: false,
            text_options: Default::default(),
            images: None,
            image_effects: None,
            series: None,
            color_filters: None,
            image_filters: None,
            shader_engine: None,
            managed_shader_engine: None,
            owned_shaders: Default::default(),
            cached_pictures: Default::default(),
            visuals: Default::default(),
            preparation_mask_depth: Default::default(),
            owned_shader_completed: Default::default(),
            masks: None,
            materialized_masks: None,
            effects: None,
            advanced: None,
            backdrop_state: Default::default(),
            axis_clip: None,
            clear: None,
            image_sampling: None,
            group_copy: None,
            image_preference: Default::default(),
        })
    }

    /// Freeze and upload geometry for a physical target. On failure no scene is
    /// published. All byte/index/device limits are checked before allocation.
    pub fn prepare(&self, mesh: &Mesh, frame: Frame) -> Result<PreparedScene, Error> {
        if mesh
            .vertices()
            .iter()
            .any(|v| matches!(v.shape_type, 4.0 | 1004.0))
        {
            return Err(Error::InvalidInput);
        }
        self.prepare_geometry(mesh, frame.into(), None, None)
    }

    /// Select original inline/staged signed-path execution for future scene preparation.
    /// Existing prepared scenes retain their original atlas generations.
    pub fn set_signed_winding_policy(&mut self, policy: crate::path::SignedWindingPolicy) {
        self.signed_winding_policy = policy;
        if let Some(rasterizer) = &mut self.path_rasterizer {
            rasterizer.set_signed_winding_policy(policy);
        }
    }
    /// Whether the optional path compute pipeline has been initialized.
    pub fn path_pipeline_is_initialized(&self) -> bool {
        self.path_rasterizer.is_some()
    }

    /// Compile and prepare an ordered mixed scene at the actual target frame.
    /// Path compute is initialized only when the scene contains path commands.
    pub async fn prepare_scene(
        &mut self,
        scene: &Scene,
        frame: Frame,
    ) -> Result<PreparedScene, Error> {
        self.prepare_target_scene(scene, RenderFrame::from(frame))
            .await
    }

    /// Prepare an explicit offscreen target before any source path raster
    /// decision. Original sharp path specialization remains atlas-based here.
    pub async fn prepare_capture_scene(
        &mut self,
        scene: &Scene,
        frame: Frame,
    ) -> Result<PreparedScene, Error> {
        let mut target = RenderFrame::from(frame);
        target.mask_depth = self
            .preparation_mask_depth
            .get()
            .checked_add(1)
            .ok_or(Error::Capacity)?;
        self.prepare_target_scene(scene, target).await
    }

    async fn prepare_target_scene(
        &mut self,
        scene: &Scene,
        target: RenderFrame,
    ) -> Result<PreparedScene, Error> {
        let transaction = owned_shader::OwnedShaderFrame::begin(self)?;
        let result = transaction
            .renderer
            .prepare_target_scene_inner(scene, target)
            .await?;
        transaction.commit()?;
        Ok(result)
    }
    async fn prepare_target_scene_inner(
        &mut self,
        scene: &Scene,
        target: RenderFrame,
    ) -> Result<PreparedScene, Error> {
        let _target_depth = self.enter_target_depth(target);
        self.preflight_owned_shaders(scene, target)?;
        self.preflight_backdrops(scene, target)?;
        target.uniforms(self.device.limits().max_texture_dimension_2d)?;
        self.preflight_shaders(scene, target)?;
        self.preflight_managed_shaders(scene, target)?;
        if scene.layer_depth() != 0 {
            self.preflight_layers(scene, target)?;
        } else {
            let (effect_bytes, effect_passes) = self.preflight_image_effects(scene)?;
            let (filter_bytes, filter_passes) = self.preflight_color_filters(scene)?;
            let (image_filter_bytes, image_filter_passes) = self.preflight_image_filters(scene)?;
            let (cached_bytes, cached_passes) = self.preflight_cached_pictures(scene, target)?;
            let (states, changes) = scene.compile_states(target.dpi_scale)?;
            self.validate_mask_states(&states)?;
            let (mask_bytes, mask_passes) =
                self.mask_resource_cost(scene, &states, &changes, target)?;
            if effect_bytes + mask_bytes + filter_bytes + image_filter_bytes + cached_bytes
                > 512 * 1024 * 1024
                || effect_passes + mask_passes + filter_passes + image_filter_passes + cached_passes
                    > 16384
            {
                return Err(Error::Capacity);
            }
        }
        let mut transaction =
            text::TreePreparation::new(self, scene.has_axis_clips() && scene.has_tree_text());
        transaction.start();
        let mut prepared = transaction
            .renderer
            .prepare_layer_tree(scene, target, None, None)
            .await?;
        transaction.finish(&mut prepared).await?;
        Ok(prepared)
    }

    async fn prepare_flat_scene(
        &mut self,
        scene: &Scene,
        frame: RenderFrame,
        groups: PreparedGroups,
    ) -> Result<PreparedScene, Error> {
        if scene.has_text() {
            let (states, _) = scene.compile_states(frame.dpi_scale)?;
            self.validate_mask_states(&states)?;
            return self.prepare_text_scene(scene, frame, groups).await;
        }
        let compiled = scene.compile_for_target(
            frame.dpi_scale,
            &mut self.materials,
            self.image_sampling
                .unwrap_or(crate::image::ImageSamplingPath::NativeSampler),
            frame.mask_depth != 0,
        )?;
        self.prepare_compiled_geometry(&compiled, frame, groups)
            .await
    }

    /// Upload a CPU-compiled immutable generation. Exact DPI must match the
    /// compilation; changing paint or geometry requires a new generation.
    /// Paths are deduplicated and populated in one atlas/compute submission,
    /// then mixed with analytic commands in one painter-ordered indexed draw.
    pub async fn prepare_compiled(
        &mut self,
        compiled: &CompiledScene,
        frame: Frame,
    ) -> Result<PreparedScene, Error> {
        if !compiled.glyphs.is_empty() {
            return Err(Error::ResourceDomain);
        }
        self.prepare_compiled_geometry(compiled, frame.into(), PreparedGroups::default())
            .await
    }

    async fn prepare_compiled_geometry(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        groups: PreparedGroups,
    ) -> Result<PreparedScene, Error> {
        if compiled.final_target_paths && frame.mask_depth != 0 {
            return Err(Error::ResourceDomain);
        }
        if compiled.dpi_scale().to_bits() != frame.dpi_scale.to_bits() {
            return Err(Error::InvalidInput);
        }
        self.validate_geometry(&compiled.mesh, frame)?;
        self.validate_masks(compiled)?;
        self.validate_hatches(compiled)?;
        self.validate_shaders(compiled, frame)?;
        self.validate_managed_shaders(compiled, frame)?;
        self.validate_backdrops(compiled, frame)?;
        let (mask_bytes, mask_passes) = self.compiled_mask_cost(compiled, frame)?;
        let (effect_bytes, effect_passes) = self.validate_image_effects(compiled)?;
        let (filter_bytes, filter_passes) = self.validate_color_filters(compiled)?;
        let (image_filter_bytes, image_filter_passes) = self.validate_image_filters(compiled)?;
        if mask_bytes + effect_bytes + filter_bytes + image_filter_bytes > 512 * 1024 * 1024
            || mask_passes + effect_passes + filter_passes + image_filter_passes > 16384
        {
            return Err(Error::Capacity);
        }
        self.validate_materials(&compiled.mesh, Some(compiled.materials()))?;
        if compiled.atlas_size() > self.device.limits().max_texture_dimension_2d {
            return Err(Error::Capacity);
        }
        let mut prepared = if compiled.rasters.is_empty() {
            self.prepare_geometry(&compiled.mesh, frame, None, Some(compiled.materials()))?
        } else {
            if self.path_rasterizer.is_none() {
                let mut rasterizer =
                    PathRasterizer::new(&self.device, &self.queue, ExecutionPreference::Automatic)
                        .await?;
                rasterizer.set_signed_winding_policy(self.signed_winding_policy);
                self.path_rasterizer = Some(rasterizer);
            }
            let requests: Vec<_> = compiled
                .rasters
                .iter()
                .map(|(path, request)| (path, *request))
                .collect();
            let rasterizer = self
                .path_rasterizer
                .as_mut()
                .ok_or(Error::UnsupportedExecution)?;
            rasterizer.prepare_programs(&requests).await?;
            let atlas = rasterizer.rasterize_atlas(&requests)?;
            self.prepare_geometry(
                &compiled.mesh,
                frame,
                Some(&atlas),
                Some(compiled.materials()),
            )?
        };
        prepared
            ._native_atlas_leases
            .try_reserve_exact(compiled.native_atlas_leases.len())
            .map_err(|_| Error::Capacity)?;
        prepared
            ._native_atlas_leases
            .extend(compiled.native_atlas_leases.iter().cloned());
        prepared
            ._native_design_leases
            .try_reserve_exact(compiled.native_design_leases.len())
            .map_err(|_| Error::Capacity)?;
        prepared
            ._native_design_leases
            .extend(compiled.native_design_leases.iter().cloned());
        prepared.image_source_owners = Some(image_ownership::ImageSourceOwners::capture(compiled));
        prepared.groups = groups;
        self.prepare_images(compiled, frame, &mut prepared).await?;
        self.prepare_hatches(compiled, &mut prepared).await?;
        self.prepare_masks(compiled, frame, &mut prepared).await?;
        self.prepare_draw_blends(compiled, &prepared).await?;
        self.prepare_clears(compiled, &mut prepared).await?;
        self.prepare_series(compiled, frame, &mut prepared).await?;
        self.prepare_image_effects(compiled, frame, &mut prepared)
            .await?;
        self.prepare_shaders(compiled, frame, &mut prepared).await?;
        self.prepare_managed_shaders(compiled, frame, &mut prepared)
            .await?;
        self.prepare_color_filters(compiled, frame, &mut prepared)
            .await?;
        self.prepare_image_filters(compiled, frame, &mut prepared)
            .await?;
        self.prepare_backdrops(compiled, frame, &mut prepared)
            .await?;
        self.prepare_advanced(compiled, frame, &mut prepared)
            .await?;
        self.finish_groups(&mut prepared).await?;
        prepared.draws = compiled.draws.clone();
        prepared.final_target_paths = compiled.final_target_paths;
        prepared.metrics.draws = prepared.draw_count()?;
        Ok(prepared)
    }

    fn validate_materials(&self, mesh: &Mesh, page: Option<&MaterialPage>) -> Result<(), Error> {
        let count = page.map_or(0, |p| p.brushes().len()).max(1);
        if mesh.vertices().iter().any(|v| {
            !v.brush_index.is_finite()
                || v.brush_index < 0.0
                || v.brush_index.fract() != 0.0
                || v.brush_index >= count as f32
        }) {
            return Err(Error::InvalidInput);
        }
        if let Some(page) = page {
            let limits = self.device.limits();
            let brush_bytes = page
                .brushes()
                .len()
                .max(1)
                .checked_mul(size_of::<GpuBrush>())
                .ok_or(Error::Capacity)? as u64;
            let record_bytes = page
                .records()
                .len()
                .max(1)
                .checked_mul(size_of::<GpuGradientStop>())
                .ok_or(Error::Capacity)? as u64;
            if [brush_bytes, record_bytes].into_iter().any(|size| {
                size > limits.max_buffer_size || size > limits.max_storage_buffer_binding_size
            }) {
                return Err(Error::Capacity);
            }
        }
        Ok(())
    }

    fn validate_geometry(&self, mesh: &Mesh, frame: RenderFrame) -> Result<Uniforms, Error> {
        let limits = self.device.limits();
        let uniforms = frame.uniforms(limits.max_texture_dimension_2d)?;
        u32::try_from(mesh.indices().len()).map_err(|_| Error::Capacity)?;
        if std::mem::size_of_val(mesh.vertices()) as u64 > limits.max_buffer_size
            || std::mem::size_of_val(mesh.indices()) as u64 > limits.max_buffer_size
        {
            return Err(Error::Capacity);
        }
        // Native geometry preserves finite-input overflow. Reject that at the
        // GPU publication boundary, without changing original geometry bits.
        if mesh.vertices().iter().any(|v| {
            bytemuck::cast_slice::<_, f32>(std::slice::from_ref(v))
                .iter()
                .any(|f| !f.is_finite())
        }) {
            return Err(Error::InvalidInput);
        }
        Ok(uniforms)
    }

    fn prepare_geometry(
        &self,
        mesh: &Mesh,
        frame: RenderFrame,
        coverage: Option<&CoverageAtlas>,
        materials: Option<&MaterialPage>,
    ) -> Result<PreparedScene, Error> {
        let uniforms = self.validate_geometry(mesh, frame)?;
        self.validate_materials(mesh, materials)?;
        if coverage.is_some_and(|atlas| atlas.device != self.device) {
            return Err(Error::ResourceDomain);
        }
        let index_count = u32::try_from(mesh.indices().len()).map_err(|_| Error::Capacity)?;
        let vertices = bytemuck::cast_slice(mesh.vertices());
        let indices = bytemuck::cast_slice(mesh.indices());
        let atlas = if let Some(coverage) = coverage {
            let view = coverage.texture().create_view(&Default::default());
            let sampler = self.device.create_sampler(&wgpu::SamplerDescriptor {
                label: Some("ProGPU path atlas sampler"),
                min_filter: wgpu::FilterMode::Linear,
                mag_filter: wgpu::FilterMode::Linear,
                ..Default::default()
            });
            self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU retained path atlas binding"),
                layout: &self.pipeline.get_bind_group_layout(1),
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(&sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&view),
                    },
                ],
            })
        } else {
            self.atlas.clone()
        };
        let buffer = |label, contents: &[u8], usage| {
            self.device
                .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                    label: Some(label),
                    contents: if contents.is_empty() {
                        &[0; 4]
                    } else {
                        contents
                    },
                    usage,
                })
        };
        let vertex_buffer = buffer(
            "ProGPU retained vertices",
            vertices,
            wgpu::BufferUsages::VERTEX,
        );
        let index_buffer = buffer(
            "ProGPU retained indices",
            indices,
            wgpu::BufferUsages::INDEX,
        );
        let uniform_buffer = buffer(
            "ProGPU immutable frame",
            bytemuck::bytes_of(&uniforms),
            wgpu::BufferUsages::UNIFORM,
        );
        let material_buffers = materials
            .filter(|page| !page.brushes().is_empty())
            .map(|page| {
                let brushes = buffer(
                    "ProGPU retained material brushes",
                    bytemuck::cast_slice(page.brushes()),
                    wgpu::BufferUsages::STORAGE,
                );
                let records = buffer(
                    "ProGPU retained material auxiliary records",
                    if page.records().is_empty() {
                        &[0; 32]
                    } else {
                        bytemuck::cast_slice(page.records())
                    },
                    wgpu::BufferUsages::STORAGE,
                );
                (brushes, records)
            });
        let (brushes, records) = material_buffers
            .as_ref()
            .map_or((&self.brush, &self.stops), |(brushes, records)| {
                (brushes, records)
            });
        let bindings = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU immutable scene bindings"),
            layout: &self.pipeline.get_bind_group_layout(0),
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: uniform_buffer.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: brushes.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: records.as_entire_binding(),
                },
            ],
        });
        Ok(PreparedScene {
            final_target_paths: false,
            hatches: None,
            frame_buffer: uniform_buffer,
            material_buffers: (brushes.clone(), records.clone()),
            _native_design_leases: Vec::new(),
            _native_atlas_leases: Vec::new(),
            owner: self.owner.clone(),
            vertices: vertex_buffer,
            indices: index_buffer,
            bindings,
            atlas,
            draws: if index_count == 0 {
                Vec::new()
            } else {
                vec![DrawRange::Vector(0..index_count)]
            },
            text: None,
            images: None,
            image_source_owners: None,
            masks: None,
            frame: frame.frame,
            origin: frame.origin,
            render_frame: frame,
            layers: LayerMetrics::default(),
            groups: PreparedGroups::default(),
            image_effects: None,
            series: None,
            color_filters: None,
            image_filters: None,
            shaders: None,
            managed_shaders: None,
            owned_shaders: Default::default(),
            cached_pictures: Default::default(),
            owned_shader_frame: self.owned_shaders.frame_number(),
            advanced: None,
            backdrops: None,
            clears: None,
            metrics: UploadMetrics {
                geometry_bytes: (vertices.len() + indices.len()) as u64,
                uniform_bytes: size_of::<Uniforms>() as u64,
                material_bytes: material_buffers
                    .as_ref()
                    .map_or(0, |(brushes, records)| brushes.size() + records.size()),
                material_brushes: materials.map_or(0, |p| p.brushes().len() as u32),
                material_records: materials.map_or(0, |p| p.records().len() as u32),
                draws: u32::from(index_count != 0),
                rasterized_paths: coverage.map_or(0, |a| a.tiles().len() as u32),
                atlas_bytes: coverage.map_or(0, |a| {
                    u64::from(a.texture().width()) * u64::from(a.texture().height())
                }),
                ..Default::default()
            },
        })
    }

    /// Render one frozen scene in painter order, without CPU readback. Explicit
    /// target-storage Clear retains its original 16-byte upload per occurrence.
    ///
    /// The caller supplies a same-device, single-sample view with the configured
    /// format and the prepared physical extent. wgpu validates target compatibility.
    pub fn render(
        &self,
        scene: &PreparedScene,
        target: &wgpu::TextureView,
        clear: wgpu::Color,
    ) -> Result<wgpu::SubmissionIndex, Error> {
        Ok(self
            .render_with_metrics(scene, target, clear)?
            .submission_index)
    }

    /// Submit one retained frame and return actual encoded draw/upload counts.
    pub fn render_with_metrics(
        &self,
        scene: &PreparedScene,
        target: &wgpu::TextureView,
        clear: wgpu::Color,
    ) -> Result<RenderSubmission, Error> {
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU frame"),
            });
        let metrics = self.encode_with_metrics(&mut encoder, scene, target, clear)?;
        let submission_index = self.queue.submit([encoder.finish()]);
        scene.retain_submitted_images(&self.queue);
        self.submitted_owned_shader_frame(scene);
        Ok(RenderSubmission {
            submission_index,
            metrics,
        })
    }

    /// Encode an ordered scene into a caller-owned same-device encoder. The
    /// target must match the scene's actual frame and renderer format. This
    /// allows capture, effects and composition to share one submission.
    pub fn encode(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        scene: &PreparedScene,
        target: &wgpu::TextureView,
        clear: wgpu::Color,
    ) -> Result<(), Error> {
        self.encode_with_metrics(encoder, scene, target, clear)
            .map(|_| ())
    }

    /// Encode the complete immutable scene and report its actual command work.
    /// Counts describe encoded commands, not GPU completion or pixel qualification.
    pub fn encode_with_metrics(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        scene: &PreparedScene,
        target: &wgpu::TextureView,
        clear: wgpu::Color,
    ) -> Result<ReplayMetrics, Error> {
        if scene.fixed_target() || !Arc::ptr_eq(&self.owner, &scene.owner) {
            return Err(Error::ResourceDomain);
        }
        if !self.is_scene_current(scene) {
            return Err(Error::ResourceDomain);
        }
        if ![clear.r, clear.g, clear.b, clear.a]
            .iter()
            .all(|v| v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        if let Some(filters) = &scene.color_filters {
            filters.encode_sources(encoder)?;
        }
        if let Some(filters) = &scene.image_filters {
            filters.encode_sources(self, encoder)?;
        }
        if let Some(effects) = &scene.image_effects {
            effects.encode_sources(encoder);
        }
        if let Some(advanced) = &scene.advanced {
            self.encode_advanced(encoder, scene, advanced, target, wgpu::LoadOp::Clear(clear))?;
        } else {
            let actual = scene.groups.root.as_ref().map_or(target, |r| &r.view);
            self.encode_span(
                encoder,
                scene,
                actual,
                wgpu::LoadOp::Clear(clear),
                0..scene.draws.len(),
                None,
            )?;
            if let Some(root) = &scene.groups.root {
                root.copy(encoder, target)?;
            }
        }
        Ok(scene.replay_metrics())
    }

    /// Replay into actual existing target storage without clearing it. This is
    /// the source-paint boundary for initialized AA groups. Destination-aware
    /// scenes first copy those exact bytes to their owned root, then preserve
    /// existing painter order. Target must be same-device and alpha-preserving.
    pub fn encode_loaded(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        scene: &PreparedScene,
        target: &wgpu::Texture,
    ) -> Result<ReplayMetrics, Error> {
        if !self.is_scene_current(scene) {
            return Err(Error::ResourceDomain);
        }
        if target.dimension() != wgpu::TextureDimension::D2
            || target.depth_or_array_layers() != 1
            || target.sample_count() != 1
            || target.format() != self.format
            || [target.width(), target.height()] != [scene.frame.width, scene.frame.height]
            || !target
                .usage()
                .contains(wgpu::TextureUsages::RENDER_ATTACHMENT)
            || (scene.advanced.is_some() && !target.usage().contains(wgpu::TextureUsages::COPY_SRC))
        {
            return Err(Error::InvalidInput);
        }
        if let Some(root) = &scene.groups.root
            && root.fixed
            && root.image.texture() != target
        {
            return Err(Error::ResourceDomain);
        }
        // Deferred AA children already own this exact attachment view. Keep
        // replay free of per-child view creation; public loaded targets retain
        // their separate caller-owned texture boundary.
        let caller_view = (!scene.fixed_target()).then(|| {
            target.create_view(&wgpu::TextureViewDescriptor {
                mip_level_count: Some(1),
                ..Default::default()
            })
        });
        let view = caller_view
            .as_ref()
            .or_else(|| scene.groups.root.as_ref().map(|r| &r.view))
            .ok_or(Error::ResourceDomain)?;
        let copy_target = scene
            .advanced
            .as_ref()
            .map(|a| &a.root_texture)
            .or_else(|| scene.groups.root.as_ref().map(|r| r.image.texture()));
        let copy = copy_target.filter(|t| *t != target);
        if let Some(copy) = copy {
            if !target.usage().contains(wgpu::TextureUsages::COPY_SRC) {
                return Err(Error::InvalidInput);
            }
            encoder.copy_texture_to_texture(
                target.as_image_copy(),
                copy.as_image_copy(),
                wgpu::Extent3d {
                    width: scene.frame.width,
                    height: scene.frame.height,
                    depth_or_array_layers: 1,
                },
            );
        }
        if let Some(filters) = &scene.color_filters {
            filters.encode_sources(encoder)?;
        }
        if let Some(filters) = &scene.image_filters {
            filters.encode_sources(self, encoder)?;
        }
        if let Some(effects) = &scene.image_effects {
            effects.encode_sources(encoder);
        }
        if let Some(advanced) = &scene.advanced {
            self.encode_advanced(encoder, scene, advanced, view, wgpu::LoadOp::Load)?;
        } else {
            let actual = scene.groups.root.as_ref().map_or(view, |r| &r.view);
            self.encode_span(
                encoder,
                scene,
                actual,
                wgpu::LoadOp::Load,
                0..scene.draws.len(),
                None,
            )?;
            if let Some(root) = &scene.groups.root
                && !root.fixed
            {
                root.copy(encoder, view)?;
            }
        }
        let mut metrics = scene.replay_metrics();
        if copy.is_some() {
            metrics.texture_copy_bytes +=
                u64::from(scene.frame.width) * u64::from(scene.frame.height) * 4;
        }
        Ok(metrics)
    }

    #[allow(clippy::too_many_arguments)] // One immutable painter span and its original attachment/scope state.
    fn encode_draw_span(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        scene: &PreparedScene,
        target: &wgpu::TextureView,
        load: wgpu::LoadOp<wgpu::Color>,
        range: std::ops::Range<usize>,
        initial_state: Option<usize>,
    ) -> Result<(), Error> {
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("ProGPU ordered scene pass"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: target,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load,
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
            let mut draw_state = initial_state
                .map(|index| {
                    scene
                        .masks
                        .as_ref()
                        .and_then(|m| m.states.get(index))
                        .ok_or(Error::ResourceDomain)
                })
                .transpose()?;
            for draw in &scene.draws[range] {
                if let DrawRange::State(index) = draw {
                    draw_state = Some(
                        scene
                            .masks
                            .as_ref()
                            .ok_or(Error::ResourceDomain)?
                            .states
                            .get(*index)
                            .ok_or(Error::ResourceDomain)?,
                    );
                    continue;
                }
                // Live advanced draws are separate ordered passes. Entries left
                // inside a span were proven disjoint from their actual scissor.
                if matches!(
                    draw,
                    DrawRange::Advanced(_) | DrawRange::Clear(_) | DrawRange::Axis(_)
                ) {
                    continue;
                }
                if let Some(state) = draw_state {
                    let [x, y, width, height] = state.scissor;
                    if width == 0 || height == 0 {
                        continue;
                    }
                    pass.set_scissor_rect(x, y, width, height);
                } else {
                    // Shader quad clipping belongs to that one draw. Restore
                    // the parent rectangle before the next unscoped command.
                    pass.set_scissor_rect(0, 0, scene.frame.width, scene.frame.height);
                }
                if let DrawRange::Hatch(range) = draw {
                    scene.hatches.as_ref().ok_or(Error::ResourceDomain)?.draw(
                        &mut pass,
                        scene,
                        range.clone(),
                    );
                    continue;
                }
                if let DrawRange::BackdropMaterial(index) = draw {
                    scene
                        .backdrops
                        .as_ref()
                        .ok_or(Error::ResourceDomain)?
                        .draw(&mut pass, *index, None)?;
                    continue;
                }
                if let DrawRange::ShaderEffect(index) = draw {
                    scene.shaders.as_ref().ok_or(Error::ResourceDomain)?.draw(
                        &mut pass,
                        *index,
                        draw_state.map(|s| s.scissor),
                    )?;
                    continue;
                }
                if let DrawRange::ManagedShaderEffect(index) = draw {
                    scene
                        .managed_shaders
                        .as_ref()
                        .ok_or(Error::ResourceDomain)?
                        .draw(&mut pass, *index, draw_state.map(|s| s.scissor))?;
                    continue;
                }
                if let DrawRange::Series(index) = draw {
                    scene
                        .series
                        .as_ref()
                        .ok_or(Error::ResourceDomain)?
                        .draw(&mut pass, *index)?;
                    continue;
                }
                if let DrawRange::ImageEffect(index) = draw {
                    scene
                        .image_effects
                        .as_ref()
                        .ok_or(Error::ResourceDomain)?
                        .draw(&mut pass, *index)?;
                    continue;
                }
                let masked = draw_state.and_then(|state| state.binding.as_ref());
                let blend =
                    draw_state.map_or(crate::scene::LayerBlend::SrcOver, |state| state.blend);
                if let DrawRange::ImageColor(range, binding, matrix) = draw {
                    scene
                        .images
                        .as_ref()
                        .ok_or(Error::ResourceDomain)?
                        .draw_color(&mut pass, range.clone(), *binding, *matrix, masked)?;
                    continue;
                }
                if let DrawRange::ImageFilteredImage(index) = draw {
                    scene
                        .image_filters
                        .as_ref()
                        .ok_or(Error::ResourceDomain)?
                        .draw(&mut pass, *index, masked)?;
                    continue;
                }
                if let DrawRange::ColorFilteredImage(index) = draw {
                    scene
                        .color_filters
                        .as_ref()
                        .ok_or(Error::ResourceDomain)?
                        .draw(&mut pass, *index, masked)?;
                    continue;
                }
                if let DrawRange::HintedText(range, vertex_count, binding) = draw {
                    if binding.is_some() && !blend.is_fixed() {
                        // Nonempty sources are consumed at this exact painter
                        // position by the destination executor; empty sources skip.
                        continue;
                    }
                    let state = self.text.as_ref().ok_or(Error::ResourceDomain)?;
                    let text = scene.text.as_ref().ok_or(Error::ResourceDomain)?;
                    let texture = binding
                        .map(|index| {
                            scene
                                .images
                                .as_ref()
                                .and_then(|images| images.bindings.get(index))
                                .ok_or(Error::ResourceDomain)
                        })
                        .transpose()?;
                    let source_premultiplied = binding
                        .map(|index| {
                            scene
                                .images
                                .as_ref()
                                .ok_or(Error::ResourceDomain)?
                                .source_premultiplied(index)
                        })
                        .transpose()?
                        .unwrap_or(false);
                    state.hinted.as_ref().ok_or(Error::ResourceDomain)?.draw(
                        &mut pass,
                        text,
                        range.clone(),
                        *vertex_count,
                        masked,
                        texture,
                        self.draw_blends.get(
                            blend::Family::Hinted,
                            blend,
                            masked.is_some(),
                            source_premultiplied,
                        )?,
                        None,
                        source_premultiplied,
                    )?;
                    continue;
                }
                let masked_pipeline = if masked.is_some() {
                    let index = match draw {
                        DrawRange::Vector(_) => 0,
                        DrawRange::Text(_) => 1,
                        DrawRange::Image(..) | DrawRange::Advanced(_) => 2,
                        _ => unreachable!(),
                    };
                    Some(
                        scene
                            .masks
                            .as_ref()
                            .and_then(|m| m.pipelines[index].as_ref())
                            .ok_or(Error::ResourceDomain)?,
                    )
                } else {
                    None
                };
                match draw {
                    DrawRange::State(_)
                    | DrawRange::Hatch(_)
                    | DrawRange::HintedText(..)
                    | DrawRange::Series(_)
                    | DrawRange::ImageEffect(_)
                    | DrawRange::ShaderEffect(_)
                    | DrawRange::ManagedShaderEffect(_)
                    | DrawRange::ImageColor(..)
                    | DrawRange::ColorFilteredImage(_)
                    | DrawRange::ImageFilteredImage(_) => unreachable!(),
                    DrawRange::Advanced(_)
                    | DrawRange::Clear(_)
                    | DrawRange::Axis(_)
                    | DrawRange::BackdropMaterial(_) => {
                        unreachable!()
                    }
                    DrawRange::Vector(range) => {
                        pass.set_pipeline(
                            self.draw_blends
                                .get(blend::Family::Vector, blend, masked.is_some(), false)?
                                .unwrap_or_else(|| masked_pipeline.unwrap_or(&self.pipeline)),
                        );
                        pass.set_bind_group(0, &scene.bindings, &[]);
                        pass.set_bind_group(1, &scene.atlas, &[]);
                        if let Some(mask) = masked {
                            pass.set_bind_group(2, mask, &[]);
                        }
                        pass.set_vertex_buffer(0, scene.vertices.slice(..));
                        pass.set_index_buffer(scene.indices.slice(..), wgpu::IndexFormat::Uint32);
                        pass.draw_indexed(range.clone(), 0, 0..1);
                    }
                    DrawRange::Text(range) => {
                        let state = self.text.as_ref().ok_or(Error::ResourceDomain)?;
                        let text = scene.text.as_ref().ok_or(Error::ResourceDomain)?;
                        pass.set_pipeline(
                            self.draw_blends
                                .get(blend::Family::Text, blend, masked.is_some(), false)?
                                .unwrap_or_else(|| masked_pipeline.unwrap_or(&state.pipeline)),
                        );
                        pass.set_bind_group(0, &text.bindings, &[]);
                        pass.set_bind_group(1, &text.atlas, &[]);
                        if let Some(mask) = masked {
                            pass.set_bind_group(2, mask, &[]);
                        }
                        pass.set_vertex_buffer(0, text.instances.slice(..));
                        pass.draw(0..6, range.clone());
                    }
                    DrawRange::Image(range, binding, blend) => {
                        let images = scene.images.as_ref().ok_or(Error::ResourceDomain)?;
                        let binding = images.bindings.get(*binding).ok_or(Error::ResourceDomain)?;
                        let pipeline = if *blend == crate::scene::LayerBlend::SrcOver {
                            masked_pipeline.unwrap_or(&images.pipeline)
                        } else {
                            images
                                .composites
                                .get(&(*blend, masked.is_some()))
                                .ok_or(Error::ResourceDomain)?
                        };
                        pass.set_pipeline(pipeline);
                        pass.set_bind_group(0, &images.uniforms, &[]);
                        pass.set_bind_group(1, binding, &[]);
                        if let Some(mask) = masked {
                            pass.set_bind_group(2, mask, &[]);
                        }
                        pass.set_vertex_buffer(0, images.vertices.slice(..));
                        pass.set_index_buffer(images.indices.slice(..), wgpu::IndexFormat::Uint32);
                        pass.draw_indexed(range.clone(), 0, 0..1);
                    }
                }
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn physical_frame_preserves_projection_and_rejects_overflow() {
        let frame = Frame {
            width: 640,
            height: 480,
            dpi_scale: 2.0,
        };
        let u = frame.uniforms(8192).unwrap();
        assert_eq!(u.canvas_size, [320.0, 240.0]);
        assert_eq!(u.pad1, [-640.0, -480.0]);
        assert_eq!(u.projection[0], 2.0 / 320.0);
        for dpi_scale in [0.0, -1.0, f32::NAN, f32::INFINITY, f32::from_bits(1)] {
            assert!(Frame { dpi_scale, ..frame }.uniforms(8192).is_err());
        }
        assert!(Frame { width: 0, ..frame }.uniforms(8192).is_err());
        assert!(frame.uniforms(32).is_err());
    }
}

/// Actual admitted color-target storage, independent of R8 coverage and the
/// original fixed RGBA8 compute-filter outputs. Overflow rejects before allocation.
fn target_texture_bytes(format: wgpu::TextureFormat, size: [u32; 2]) -> Result<u64, Error> {
    let texel = match format {
        wgpu::TextureFormat::Rgba16Float => 8,
        wgpu::TextureFormat::Rgba8Unorm
        | wgpu::TextureFormat::Bgra8Unorm
        | wgpu::TextureFormat::Rgba8UnormSrgb
        | wgpu::TextureFormat::Bgra8UnormSrgb => 4,
        _ => return Err(Error::UnsupportedExecution),
    };
    u64::from(size[0])
        .checked_mul(u64::from(size[1]))
        .and_then(|v| v.checked_mul(texel))
        .ok_or(Error::Capacity)
}
