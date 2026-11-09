//! Canonical ordered text execution with actual atlas residency ownership.
use super::*;
use crate::{
    scene::ResolvedSceneText,
    text::{
        glyph::{GlyphAtlas, GlyphAtlasOptions, GlyphAtlasResidency, GlyphAtlasStatistics},
        paint::{GlyphInstance, GpuTextStyle, TextPainter},
    },
};

pub(super) struct TextState {
    pub pipeline: wgpu::RenderPipeline,
    atlas: GlyphAtlas,
    painter: TextPainter,
}
pub(super) struct PreparedText {
    pub instances: wgpu::Buffer,
    pub bindings: wgpu::BindGroup,
    pub atlas: wgpu::BindGroup,
    residency: GlyphAtlasResidency,
    statistics: GlyphAtlasStatistics,
}

impl TextState {
    async fn new(renderer: &Renderer) -> Result<Self, Error> {
        let device = &renderer.device;
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU canonical Text.wgsl"),
            source: wgpu::ShaderSource::Wgsl(shaders::TEXT.into()),
        });
        let attributes = wgpu::vertex_attr_array![0=>Float32x2, 1=>Float32x2, 2=>Float32x2,
            3=>Float32x4, 4=>Float32x4, 5=>Float32x4, 6=>Float32x4, 7=>Float32];
        let pipeline_layout = layout::pipeline(device, layout::Family::Text, None);
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU retained text"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &module,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &[Some(wgpu::VertexBufferLayout {
                    array_stride: size_of::<GlyphInstance>() as u64,
                    step_mode: wgpu::VertexStepMode::Instance,
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
                    format: renderer.format,
                    blend: Some(wgpu::BlendState::ALPHA_BLENDING),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let atlas = GlyphAtlas::new(device, &renderer.queue, renderer.text_options).await?;
        Ok(Self {
            pipeline,
            atlas,
            painter: TextPainter::default(),
        })
    }

    fn current(&self, text: &PreparedText) -> bool {
        let current = self.atlas.statistics();
        current.generation == text.statistics.generation
            && current.texture_revision == text.statistics.texture_revision
            && self.atlas.contains_residency(&text.residency)
    }

    fn upload(
        &self,
        renderer: &Renderer,
        compiled: &CompiledScene,
        resolved: ResolvedSceneText,
        frame: RenderFrame,
    ) -> Result<PreparedText, Error> {
        let device = &renderer.device;
        let limits = device.limits();
        let uniforms = frame.uniforms(limits.max_texture_dimension_2d)?;
        let placeholder = GpuTextStyle::zeroed();
        let styles = if resolved.styles.is_empty() {
            std::slice::from_ref(&placeholder)
        } else {
            &resolved.styles
        };
        let style_bytes = bytemuck::cast_slice(styles);
        let instance_bytes = bytemuck::cast_slice(&compiled.glyphs);
        if style_bytes.len() as u64 > limits.max_storage_buffer_binding_size
            || style_bytes.len() as u64 > limits.max_buffer_size
            || instance_bytes.len() as u64 > limits.max_buffer_size
            || compiled.glyphs.len() > u32::MAX as usize
        {
            return Err(Error::Capacity);
        }
        for instance in &compiled.glyphs {
            if bytemuck::cast_slice::<_, f32>(std::slice::from_ref(instance))
                .iter()
                .any(|v| !v.is_finite())
                || (instance.brush_index >= 0.0
                    && (instance.brush_index.fract() != 0.0
                        || instance.brush_index >= styles.len() as f32))
            {
                return Err(Error::InvalidInput);
            }
        }
        if styles
            .iter()
            .any(|s| s.color.iter().any(|v| !v.is_finite()))
        {
            return Err(Error::InvalidInput);
        }
        let buffer = |label, contents: &[u8], usage| {
            device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some(label),
                contents: if contents.is_empty() {
                    &[0; 4]
                } else {
                    contents
                },
                usage,
            })
        };
        let instances = buffer(
            "ProGPU immutable glyph instances",
            instance_bytes,
            wgpu::BufferUsages::VERTEX,
        );
        let style_buffer = buffer(
            "ProGPU immutable text styles",
            style_bytes,
            wgpu::BufferUsages::STORAGE,
        );
        let uniform_buffer = buffer(
            "ProGPU immutable text frame",
            bytemuck::bytes_of(&uniforms),
            wgpu::BufferUsages::UNIFORM,
        );
        let bindings = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU immutable text bindings"),
            layout: &self.pipeline.get_bind_group_layout(0),
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: uniform_buffer.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: style_buffer.as_entire_binding(),
                },
            ],
        });
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("ProGPU glyph atlas sampler"),
            min_filter: wgpu::FilterMode::Linear,
            mag_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });
        let coverage = resolved.coverage.texture().create_view(&Default::default());
        let color = resolved.color.texture().create_view(&Default::default());
        let atlas = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU final glyph atlas generations"),
            layout: &self.pipeline.get_bind_group_layout(1),
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::Sampler(&sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(&coverage),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::TextureView(&color),
                },
            ],
        });
        Ok(PreparedText {
            instances,
            bindings,
            atlas,
            residency: resolved.residency,
            statistics: resolved.statistics,
        })
    }
}

impl Renderer {
    /// Configure typed glyph execution and atlas budgets before first text use.
    /// Forced incompatible routes reject during lazy initialization.
    pub fn configure_text_atlas(&mut self, options: GlyphAtlasOptions) -> Result<(), Error> {
        if self.text.is_some() {
            return Err(Error::ResourceDomain);
        }
        self.text_options = options;
        Ok(())
    }

    /// Whether exact atlas owner, generation, texture revision and every required
    /// key still match. Reprepare a stale scene before rendering it again.
    pub fn is_scene_current(&self, scene: &PreparedScene) -> bool {
        Arc::ptr_eq(&self.owner, &scene.owner)
            && scene
                .text
                .as_ref()
                .is_none_or(|text| self.text.as_ref().is_some_and(|state| state.current(text)))
    }

    pub(super) async fn prepare_text_scene(
        &mut self,
        scene: &Scene,
        frame: RenderFrame,
    ) -> Result<PreparedScene, Error> {
        // Exclusive transaction ownership: cancellation or error drops this
        // state. No Drop implementation submits, polls, destroys live textures,
        // or leaves a half-open atlas available to the renderer's next request.
        let mut state = match self.text.take() {
            Some(state) => state,
            None => TextState::new(self).await?,
        };
        let before = state.atlas.gpu_metrics();
        let resolved = scene
            .resolve_text(&mut state.painter, &mut state.atlas, frame.dpi_scale)
            .await?;
        let compiled = resolved.scene.compile_with_sampling(
            frame.dpi_scale,
            &mut self.materials,
            self.image_sampling
                .unwrap_or(crate::image::ImageSamplingPath::NativeSampler),
        )?;
        let mut prepared = self.prepare_compiled_geometry(&compiled, frame).await?;
        let after = state.atlas.gpu_metrics();
        prepared.metrics.glyph_upload_bytes = (after.outline_upload_bytes
            - before.outline_upload_bytes)
            + (after.uniform_upload_bytes - before.uniform_upload_bytes)
            + (after.bitmap_upload_bytes - before.bitmap_upload_bytes);
        prepared.metrics.rasterized_glyphs = after.rasterized_glyphs - before.rasterized_glyphs;
        prepared.metrics.glyph_atlas_bytes = state.atlas.persistent_texture_bytes();
        if !compiled.glyphs.is_empty() {
            prepared.metrics.geometry_bytes +=
                std::mem::size_of_val(compiled.glyphs.as_slice()) as u64;
            prepared.metrics.glyph_instances =
                u32::try_from(compiled.glyphs.len()).map_err(|_| Error::Capacity)?;
            prepared.metrics.text_style_bytes =
                (resolved.styles.len().max(1) * size_of::<GpuTextStyle>()) as u64;
            prepared.metrics.uniform_bytes += size_of::<Uniforms>() as u64;
            prepared.text = Some(state.upload(self, &compiled, resolved, frame)?);
        }
        self.text = Some(state);
        Ok(prepared)
    }
}
