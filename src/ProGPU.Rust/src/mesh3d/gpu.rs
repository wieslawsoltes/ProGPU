//! Original Mesh3D offscreen preparation with immutable submission-owned resources.
use super::*;
use crate::{
    image::{GpuImage, ImageAlpha, ImageSampling},
    image_effect::{BlurInput, BlurStorage, ImageBlurEngine, PreparedImageBlur},
    path::ExecutionPreference,
};
use std::collections::HashMap;
use wgpu::util::DeviceExt;
mod cost;
pub use cost::Mesh3DResourcePlan;
mod pipeline;
mod resources;
mod texture;
use pipeline::{Layouts, Variant};
use resources::{EdgeBuffer, GeometryBuffer, RecordBuffers, Targets, ViewportCache};

/// One actual viewport target, independent of the receiving 2D picture frame.
#[derive(Clone, Debug)]
pub struct Mesh3DRequest {
    pub viewport: Arc<Mesh3DViewport>,
    pub projection: Matrix4,
    pub view: Matrix4,
    pub size: [u32; 2],
    pub sample_count: u32,
    pub opacity: f32,
}
impl Mesh3DRequest {
    pub fn is_empty(&self) -> bool {
        self.viewport.meshes.is_empty()
    }
    pub fn effective_sample_count(&self) -> u32 {
        if self.sample_count == 1 { 1 } else { 4 }
    }
    /// CPU-only target/resource-size admission; source acquisition remains preparation work.
    pub fn validate(&self, device: &wgpu::Device) -> Result<(), Error> {
        let plan = self.resource_plan(device)?;
        if plan.cost().0 > 512 * 1024 * 1024 || plan.cost().1 > 16 * 1024 {
            return Err(Error::Capacity);
        }
        Ok(())
    }
    fn validate_base(&self, device: &wgpu::Device) -> Result<(), Error> {
        self.viewport.validate()?;
        if self.is_empty()
            || !self.opacity.is_finite()
            || self.size.contains(&0)
            || self
                .projection
                .0
                .iter()
                .chain(&self.view.0)
                .flatten()
                .any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        let limits = device.limits();
        if self
            .size
            .iter()
            .any(|v| *v > limits.max_texture_dimension_2d)
            || limits.max_uniform_buffer_binding_size < 224
            || limits.max_storage_buffers_per_shader_stage < 3
            || limits.max_sampled_textures_per_shader_stage < 2
        {
            return Err(Error::Capacity);
        }
        let pixels = u64::from(self.size[0]) * u64::from(self.size[1]);
        if pixels
            .checked_mul(
                4 * (1
                    + u64::from(self.effective_sample_count())
                    + if self.effective_sample_count() > 1 {
                        4
                    } else {
                        0
                    }),
            )
            .is_none_or(|v| v > 512 * 1024 * 1024)
        {
            return Err(Error::Capacity);
        }
        resources::check_storage(device, self.viewport.meshes.len(), 560)?;
        for mesh in self.viewport.meshes.iter() {
            if let Some(geometry) = &mesh.geometry {
                resources::check_buffer(device, geometry.indices().len(), 32)?;
            }
        }
        Ok(())
    }
}
/// Actual preparation uploads and ordered offscreen work, independent of qualification.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Mesh3DMetrics {
    pub frame_number: u64,
    pub scene_generation: u64,
    pub record_generation: u64,
    pub scene_reused: bool,
    pub mesh_count: u32,
    pub scene_compilation_count: u32,
    pub model_visual_visit_count: u32,
    pub draw_calls: u32,
    pub geometry_cache_hits: u32,
    pub geometry_cache_misses: u32,
    pub geometry_vertex_upload_bytes: u64,
    pub record_upload_bytes: u64,
    pub record_index_upload_bytes: u64,
    pub edge_upload_bytes: u64,
    pub uniform_upload_bytes: u64,
    pub light_upload_bytes: u64,
    pub gradient_upload_bytes: u64,
    pub geometry_resident_count: u32,
    pub geometry_buffer_resident_bytes: u64,
    pub viewport_buffer_bytes: u64,
    pub logical_target_texture_bytes: u64,
    pub live_blur_passes: u32,
    pub live_blur_texture_bytes: u64,
    pub live_blur_texture_allocations: u32,
    pub pipeline_count: u32,
}
struct MeshDraw {
    geometry: Arc<GeometryBuffer>,
    record: u32,
    pipeline: wgpu::RenderPipeline,
    texture: Option<wgpu::BindGroup>,
}
struct EdgeDraw {
    pipeline: wgpu::RenderPipeline,
    vertices: u32,
    instances: u32,
}
struct PreparedResources {
    _request: Mesh3DRequest,
    targets: Targets,
    _uniform: wgpu::Buffer,
    records: Arc<RecordBuffers>,
    edges: Option<Arc<EdgeBuffer>>,
    _lights: wgpu::Buffer,
    _stops: wgpu::Buffer,
    solid: Option<wgpu::BindGroup>,
    wire: Option<wgpu::BindGroup>,
    meshes: Vec<MeshDraw>,
    edge_draws: Vec<EdgeDraw>,
    blurs: Vec<Arc<PreparedImageBlur>>,
    leases: Vec<MeshTextureLease>,
    metrics: Mesh3DMetrics,
}
impl std::fmt::Debug for PreparedResources {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Mesh3DPreparedResources")
            .field("metrics", &self.metrics)
            .finish_non_exhaustive()
    }
}
/// Full immutable preparation. Its output image also retains these source leases.
pub struct PreparedMesh3D {
    resources: Arc<PreparedResources>,
    image: GpuImage,
}
impl PreparedMesh3D {
    pub fn image(&self) -> &GpuImage {
        &self.image
    }
    pub fn metrics(&self) -> Mesh3DMetrics {
        self.resources.metrics
    }
    /// Encode original live-material blur, viewport clear, fronts, backs and edges.
    /// No source callbacks or queue submissions occur here.
    pub fn encode(&self, encoder: &mut wgpu::CommandEncoder) -> Result<Mesh3DMetrics, Error> {
        let r = &self.resources;
        for blur in &r.blurs {
            blur.encode_owned(encoder);
        }
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("ProGPU original Mesh3D offscreen"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: r
                    .targets
                    .msaa_view
                    .as_ref()
                    .unwrap_or(&r.targets.color_view),
                depth_slice: None,
                resolve_target: r.targets.msaa_view.as_ref().map(|_| &r.targets.color_view),
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Clear(wgpu::Color {
                        r: 0.05,
                        g: 0.05,
                        b: 0.06,
                        a: 1.0,
                    }),
                    store: if r.targets.msaa_view.is_some() {
                        wgpu::StoreOp::Discard
                    } else {
                        wgpu::StoreOp::Store
                    },
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: &r.targets.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load: wgpu::LoadOp::Clear(1.0),
                    store: wgpu::StoreOp::Store,
                }),
                stencil_ops: Some(wgpu::Operations {
                    load: wgpu::LoadOp::Clear(0),
                    store: wgpu::StoreOp::Store,
                }),
            }),
            ..Default::default()
        });
        for draw in &r.meshes {
            pass.set_pipeline(&draw.pipeline);
            pass.set_bind_group(
                0,
                if draw.texture.is_some() {
                    r.solid.as_ref()
                } else {
                    r.wire.as_ref()
                }
                .ok_or(Error::ResourceDomain)?,
                &[],
            );
            if let Some(binding) = &draw.texture {
                pass.set_bind_group(1, binding, &[]);
            }
            pass.set_vertex_buffer(0, draw.geometry.buffer.slice(..));
            let start = u64::from(draw.record) * 4;
            pass.set_vertex_buffer(1, r.records.indices.slice(start..start + 4));
            pass.draw(0..draw.geometry.count, 0..1);
        }
        for draw in &r.edge_draws {
            pass.set_pipeline(&draw.pipeline);
            pass.set_bind_group(0, r.solid.as_ref().ok_or(Error::ResourceDomain)?, &[]);
            pass.set_vertex_buffer(
                0,
                r.edges
                    .as_ref()
                    .ok_or(Error::ResourceDomain)?
                    .buffer
                    .slice(..),
            );
            pass.draw(0..draw.vertices, 0..draw.instances);
        }
        drop(pass);
        Ok(r.metrics)
    }
    /// Retain all actual producer callbacks after the caller's real submission.
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        for lease in &self.resources.leases {
            lease.retain_submitted(queue);
        }
        for blur in &self.resources.blurs {
            blur.retain_submitted(queue);
        }
        let owner = self.resources.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
/// Render-thread cache. Prepared scenes remain independent of later cache replacement.
pub struct Mesh3DEngine {
    device: wgpu::Device,
    queue: wgpu::Queue,
    layouts: Option<Layouts>,
    pipelines: HashMap<(Variant, u32), wgpu::RenderPipeline>,
    shaders: HashMap<(u8, u32), wgpu::ShaderModule>,
    geometries: HashMap<usize, Arc<GeometryBuffer>>,
    viewports: Vec<ViewportCache>,
    viewport_index: usize,
    white: Option<wgpu::Texture>,
    white_bindings: [Option<wgpu::BindGroup>; 2],
    samplers: HashMap<ImageSampling, wgpu::Sampler>,
    blur: Option<ImageBlurEngine>,
    blur_pool: Vec<Arc<crate::image_effect::ImageBlurStorage>>,
    used_blurs: usize,
    frame: u64,
    explicit_frame: bool,
}
impl Mesh3DEngine {
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Result<Self, Error> {
        Self::new_with_preference(device, queue, ExecutionPreference::Automatic)
    }
    pub fn new_with_preference(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        preference: ExecutionPreference,
    ) -> Result<Self, Error> {
        if !matches!(
            preference,
            ExecutionPreference::Automatic | ExecutionPreference::GpuShader
        ) {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self {
            device: device.clone(),
            queue: queue.clone(),
            layouts: None,
            pipelines: HashMap::new(),
            shaders: HashMap::new(),
            geometries: HashMap::new(),
            viewports: Vec::new(),
            viewport_index: 0,
            white: None,
            white_bindings: [None, None],
            samplers: HashMap::new(),
            blur: None,
            blur_pool: Vec::new(),
            used_blurs: 0,
            frame: 0,
            explicit_frame: false,
        })
    }
    /// Actual host frame boundary; never inferred from nested encodes/submissions.
    pub fn begin_frame(&mut self, frame: u64) {
        self.frame = frame;
        self.viewport_index = 0;
        self.used_blurs = 0;
        self.retire_unused_blurs();
        self.explicit_frame = true;
    }
    pub fn execution_path(&self) -> crate::path::ExecutionPath {
        crate::path::ExecutionPath::GpuShader
    }
    fn retire_unused_blurs(&mut self) {
        let frame = self.frame;
        self.blur_pool
            .retain(|storage| frame.saturating_sub(storage.last_used()) <= 240);
    }
    pub fn pipeline_count(&self) -> usize {
        self.pipelines.len()
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub async fn prepare(&mut self, request: &Mesh3DRequest) -> Result<PreparedMesh3D, Error> {
        let plan = request.resource_plan(&self.device)?;
        self.prepare_with_plan(request, &plan).await
    }
    pub async fn prepare_with_plan(
        &mut self,
        request: &Mesh3DRequest,
        plan: &Mesh3DResourcePlan,
    ) -> Result<PreparedMesh3D, Error> {
        request.validate_base(&self.device)?;
        plan.admits(request, &self.device)?;
        if plan.cost().0 > 512 * 1024 * 1024 || plan.cost().1 > 16 * 1024 {
            return Err(Error::Capacity);
        }
        if !self.explicit_frame {
            self.frame = self.frame.checked_add(1).ok_or(Error::Capacity)?;
            self.viewport_index = 0;
            self.used_blurs = 0;
            self.retire_unused_blurs();
        }
        let uniforms = request
            .viewport
            .uniforms(request.projection, request.view, request.size)?;
        // Preserve the original failed-inversion camera NaNs. Shader record data
        // is not silently sanitized into a different camera or light policy.
        if self.layouts.is_none() {
            self.layouts = Some(Layouts::new(&self.device).await?);
        }
        self.ensure_white();
        let mut metrics = Mesh3DMetrics {
            frame_number: self.frame,
            scene_generation: request.viewport.scene_generation,
            record_generation: request.viewport.record_generation,
            scene_reused: request.viewport.scene_reused,
            scene_compilation_count: request.viewport.scene_compilation_count,
            model_visual_visit_count: request.viewport.model_visual_visit_count,
            mesh_count: u32::try_from(request.viewport.meshes.len())
                .map_err(|_| Error::Capacity)?,
            ..Default::default()
        };
        let materials = self.prepare_materials(request, plan, &mut metrics).await?;
        let mut states = Vec::new();
        states
            .try_reserve_exact(materials.values.len())
            .map_err(|_| Error::Capacity)?;
        states.extend(materials.values.iter().map(|v| v.state));
        let records = request.viewport.compile_records(request.opacity, &states)?;
        let (record_buffers, edges) =
            self.prepare_record_buffers(request, &records, &mut metrics)?;
        // Original retained-record fast path skips ApplyMaterialBrush and writes
        // one zero gradient stop, leaving the previous trailing slots intact.
        let gradient_snapshot =
            self.gradient_snapshot(&records.gradient_stops, metrics.record_upload_bytes == 0)?;
        let mut geometries = Vec::new();
        geometries
            .try_reserve_exact(request.viewport.meshes.len())
            .map_err(|_| Error::Capacity)?;
        for mesh in request.viewport.meshes.iter() {
            geometries.push(
                mesh.geometry
                    .as_ref()
                    .map(|v| self.geometry(v, &mut metrics))
                    .transpose()?,
            );
        }
        let samples = request.effective_sample_count();
        let mut meshes = Vec::new();
        meshes
            .try_reserve_exact(geometries.len())
            .map_err(|_| Error::Capacity)?;
        // Source solid mode emits front records first, then back records. Both
        // wireframe modes use their single original two-sided shader pass.
        let solid = request.viewport.render_mode == RenderMode3D::Solid;
        for back in if solid {
            &[false, true][..]
        } else {
            &[false][..]
        } {
            for (index, mesh) in request.viewport.meshes.iter().enumerate() {
                let Some(geometry) = &geometries[index] else {
                    continue;
                };
                if solid && mesh.is_back_face != *back {
                    continue;
                }
                let material = &materials.values[index];
                let variant = if !solid {
                    Variant::Wireframe
                } else {
                    match (*back, material.unfilterable) {
                        (false, false) => Variant::Front,
                        (true, false) => Variant::Back,
                        (false, true) => Variant::UnfilterableFront,
                        (true, true) => Variant::UnfilterableBack,
                    }
                };
                let pipeline = self.pipeline(variant, samples).await?;
                meshes.push(MeshDraw {
                    geometry: geometry.clone(),
                    record: index as u32,
                    pipeline,
                    texture: solid.then(|| material.binding.clone()),
                });
            }
        }
        let mut edge_draws = Vec::new();
        let edge_count = u32::try_from(records.edges.len()).map_err(|_| Error::Capacity)?;
        let display = request.viewport.edge_style.display.0;
        if edge_count > 0 && display & 7 != 0 {
            for variant in [
                Some(Variant::VisibleEdges),
                (display & 8 != 0).then_some(Variant::OccludedEdges),
            ]
            .into_iter()
            .flatten()
            {
                edge_draws.push(EdgeDraw {
                    pipeline: self.pipeline(variant, samples).await?,
                    vertices: if request.viewport.edge_style.jitter_amount > 0.0 {
                        18
                    } else {
                        6
                    },
                    instances: edge_count,
                });
            }
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let uniform = resources::initialized(
            &self.device,
            "ProGPU immutable Mesh3D uniforms",
            bytemuck::bytes_of(&uniforms),
            wgpu::BufferUsages::UNIFORM,
        );
        let lights = resources::initialized(
            &self.device,
            "ProGPU immutable Mesh3D lights",
            bytemuck::cast_slice(&records.lights),
            wgpu::BufferUsages::STORAGE,
        );
        let stops = resources::initialized(
            &self.device,
            "ProGPU immutable Mesh3D gradients",
            bytemuck::cast_slice(gradient_snapshot.as_ref()),
            wgpu::BufferUsages::STORAGE,
        );
        let bindings = [
            wgpu::BindGroupEntry {
                binding: 0,
                resource: uniform.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: record_buffers.records.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 2,
                resource: lights.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 3,
                resource: stops.as_entire_binding(),
            },
        ];
        let layouts = self.layouts.as_ref().ok_or(Error::ResourceDomain)?;
        let solid_binding = if (solid && !meshes.is_empty()) || !edge_draws.is_empty() {
            Some(self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU retained Mesh3D material records"),
                layout: &layouts.solid,
                entries: &bindings,
            }))
        } else {
            None
        };
        let wire = if !solid && !meshes.is_empty() {
            Some(self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU retained Mesh3D wireframe records"),
                layout: &layouts.wire,
                entries: &bindings[..3],
            }))
        } else {
            None
        };
        let targets = Targets::new(&self.device, request.size, samples);
        let image =
            GpuImage::from_texture(&self.device, &targets.color, ImageAlpha::Premultiplied)?;
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        metrics.draw_calls =
            u32::try_from(meshes.len() + edge_draws.len()).map_err(|_| Error::Capacity)?;
        metrics.uniform_upload_bytes = 224;
        metrics.light_upload_bytes = lights.size();
        metrics.gradient_upload_bytes = stops.size();
        metrics.viewport_buffer_bytes = 224
            + record_buffers.records.size()
            + record_buffers.indices.size()
            + edges.as_ref().map_or(0, |v| v.buffer.size())
            + lights.size()
            + stops.size();
        metrics.logical_target_texture_bytes = targets.bytes;
        metrics.geometry_resident_count =
            u32::try_from(self.geometries.len()).map_err(|_| Error::Capacity)?;
        metrics.geometry_buffer_resident_bytes =
            self.geometries.values().map(|v| v.buffer.size()).sum();
        metrics.pipeline_count = self.pipelines.len() as u32;
        let resources = Arc::new(PreparedResources {
            _request: request.clone(),
            targets,
            _uniform: uniform,
            records: record_buffers,
            edges,
            _lights: lights,
            _stops: stops,
            solid: solid_binding,
            wire,
            meshes,
            edge_draws,
            blurs: materials.blurs,
            leases: materials.leases,
            metrics,
        });
        self.viewport_index = self.viewport_index.checked_add(1).ok_or(Error::Capacity)?;
        Ok(PreparedMesh3D {
            image: image.with_producer(resources.clone()),
            resources,
        })
    }
    async fn pipeline(
        &mut self,
        variant: Variant,
        samples: u32,
    ) -> Result<wgpu::RenderPipeline, Error> {
        if let Some(p) = self.pipelines.get(&(variant, samples)) {
            return Ok(p.clone());
        }
        let key = (variant.family(), samples);
        if !self.shaders.contains_key(&key) {
            let source = match key.0 {
                1 => crate::shaders::MESH_3D_WIREFRAME,
                2 => crate::shaders::MESH_3D_EDGES,
                _ => crate::shaders::MESH_3D_SOLID,
            };
            let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
            let shader = self
                .device
                .create_shader_module(wgpu::ShaderModuleDescriptor {
                    label: Some("ProGPU canonical Mesh3D shader generation"),
                    source: wgpu::ShaderSource::Wgsl(source.into()),
                });
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            self.shaders.try_reserve(1).map_err(|_| Error::Capacity)?;
            self.shaders.insert(key, shader);
        }
        let pipeline = self
            .layouts
            .as_ref()
            .ok_or(Error::ResourceDomain)?
            .pipeline(
                &self.device,
                variant,
                samples,
                self.shaders.get(&key).ok_or(Error::ResourceDomain)?,
            )
            .await?;
        self.pipelines.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.pipelines.insert((variant, samples), pipeline.clone());
        Ok(pipeline)
    }
}
