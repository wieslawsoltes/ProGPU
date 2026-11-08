//! Retained wgpu execution of ProGPU's canonical analytic vector shader.
//!
//! CPU geometry is prepared once, uploaded once and retained by an immutable
//! scene. Frames submit one ordered draw with no geometry/uniform re-upload.
//! wgpu retains encoded dependencies through submission; dropping this renderer
//! never explicitly destroys a buffer still referenced by a submitted frame.

use crate::{
    Error,
    geometry::{Mesh, VectorVertex},
    shaders,
};
use bytemuck::{Pod, Zeroable};
use std::sync::Arc;
use wgpu::util::DeviceExt;

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
    /// Original vertex and index payload bytes uploaded once at preparation.
    pub geometry_bytes: u64,
    /// Uniform bytes uploaded once at preparation.
    pub uniform_bytes: u64,
    /// Ordered indexed draw count (zero for empty scenes).
    pub draws: u32,
}

/// GPU resources for a frozen scene and physical frame.
pub struct PreparedScene {
    owner: Arc<()>,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    bindings: wgpu::BindGroup,
    index_count: u32,
    frame: Frame,
    metrics: UploadMetrics,
}

impl PreparedScene {
    /// Physical frame used when preparing this scene.
    pub fn frame(&self) -> Frame {
        self.frame
    }
    /// Actual preparation payload counts; replay performs no uploads.
    pub fn metrics(&self) -> UploadMetrics {
        self.metrics
    }
}

/// A single-device analytic vector renderer with immutable prepared generations.
pub struct Renderer {
    owner: Arc<()>,
    device: wgpu::Device,
    queue: wgpu::Queue,
    pipeline: wgpu::RenderPipeline,
    brush: wgpu::Buffer,
    stops: wgpu::Buffer,
    atlas: wgpu::BindGroup,
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
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU analytic vector"),
            layout: None,
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
            owner: Arc::new(()),
            device: device.clone(),
            queue: queue.clone(),
            pipeline,
            brush,
            stops,
            atlas,
        })
    }

    /// Freeze and upload geometry for a physical target. On failure no scene is
    /// published. All byte/index/device limits are checked before allocation.
    pub fn prepare(&self, mesh: &Mesh, frame: Frame) -> Result<PreparedScene, Error> {
        let limits = self.device.limits();
        let uniforms = frame.uniforms(limits.max_texture_dimension_2d)?;
        let index_count = u32::try_from(mesh.indices().len()).map_err(|_| Error::Capacity)?;
        let vertices = bytemuck::cast_slice(mesh.vertices());
        let indices = bytemuck::cast_slice(mesh.indices());
        if vertices.len() as u64 > limits.max_buffer_size
            || indices.len() as u64 > limits.max_buffer_size
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
                    resource: self.brush.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: self.stops.as_entire_binding(),
                },
            ],
        });
        Ok(PreparedScene {
            owner: self.owner.clone(),
            vertices: vertex_buffer,
            indices: index_buffer,
            bindings,
            index_count,
            frame,
            metrics: UploadMetrics {
                geometry_bytes: (vertices.len() + indices.len()) as u64,
                uniform_bytes: size_of::<Uniforms>() as u64,
                draws: u32::from(index_count != 0),
            },
        })
    }

    /// Render one frozen scene in painter order, without uploads or CPU readback.
    ///
    /// The caller supplies a same-device, single-sample view with the configured
    /// format and the prepared physical extent. wgpu validates target compatibility.
    pub fn render(
        &self,
        scene: &PreparedScene,
        target: &wgpu::TextureView,
        clear: wgpu::Color,
    ) -> Result<wgpu::SubmissionIndex, Error> {
        if !Arc::ptr_eq(&self.owner, &scene.owner) {
            return Err(Error::ResourceDomain);
        }
        if ![clear.r, clear.g, clear.b, clear.a]
            .iter()
            .all(|v| v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU frame"),
            });
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("ProGPU vector pass"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: target,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(clear),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
            if scene.index_count != 0 {
                pass.set_pipeline(&self.pipeline);
                pass.set_bind_group(0, &scene.bindings, &[]);
                pass.set_bind_group(1, &self.atlas, &[]);
                pass.set_vertex_buffer(0, scene.vertices.slice(..));
                pass.set_index_buffer(scene.indices.slice(..), wgpu::IndexFormat::Uint32);
                pass.draw_indexed(0..scene.index_count, 0, 0..1);
            }
        }
        Ok(self.queue.submit([encoder.finish()]))
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
