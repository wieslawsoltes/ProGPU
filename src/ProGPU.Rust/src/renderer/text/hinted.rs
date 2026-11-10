//! Original per-occurrence spatial paint with canonical glyph coverage and masks.
use super::*;

pub(in crate::renderer) struct HintedPaintState {
    pipelines: [wgpu::RenderPipeline; 2],
    placeholder: wgpu::BindGroup,
}
pub(in crate::renderer) struct PreparedHintedPaint {
    bindings: wgpu::BindGroup,
    placeholder: wgpu::BindGroup,
}
impl HintedPaintState {
    pub(super) async fn new(renderer: &Renderer) -> Result<Self, Error> {
        let device = &renderer.device;
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU canonical HintedGlyphPaint.wgsl"),
            source: wgpu::ShaderSource::Wgsl(shaders::HINTED_GLYPH_PAINT.into()),
        });
        let pipeline_layout = layout::hinted_text(device);
        let attributes = wgpu::vertex_attr_array![0=>Float32x2, 1=>Float32x2, 2=>Float32x2,
            3=>Float32x4, 4=>Float32x4, 5=>Float32x4, 6=>Float32x4, 7=>Float32, 8=>Uint32];
        let pipelines = ["fs_main_unmasked", "fs_main_mask_chain"].map(|entry| {
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("ProGPU original direct hinted glyph material"),
                layout: Some(&pipeline_layout),
                vertex: wgpu::VertexState {
                    module: &module,
                    entry_point: Some("vs_main"),
                    compilation_options: Default::default(),
                    buffers: &[Some(wgpu::VertexBufferLayout {
                        array_stride: 96,
                        step_mode: wgpu::VertexStepMode::Instance,
                        attributes: &attributes,
                    })],
                },
                primitive: Default::default(),
                depth_stencil: None,
                multisample: Default::default(),
                fragment: Some(wgpu::FragmentState {
                    module: &module,
                    entry_point: Some(entry),
                    compilation_options: Default::default(),
                    targets: &[Some(wgpu::ColorTargetState {
                        format: renderer.format,
                        blend: Some(wgpu::BlendState::ALPHA_BLENDING),
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                }),
                multiview_mask: None,
                cache: None,
            })
        });
        // The original direct-paint module retains its texture branch. Registered
        // materials bind one actual unused texture rather than changing the shader.
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU unused hinted material texture"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("ProGPU hinted material placeholder sampler"),
            min_filter: wgpu::FilterMode::Linear,
            mag_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });
        let view = texture.create_view(&Default::default());
        let placeholder = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU original registered-material texture binding"),
            layout: &pipelines[0].get_bind_group_layout(3),
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
            pipelines,
            placeholder,
        })
    }
    pub(super) fn upload(
        &self,
        device: &wgpu::Device,
        uniform: &wgpu::Buffer,
        compiled: &CompiledScene,
    ) -> Result<PreparedHintedPaint, Error> {
        let page = compiled.materials();
        let placeholder_brush = [GpuBrush::zeroed()];
        let placeholder_stop = [GpuGradientStop::zeroed()];
        let brushes = if page.brushes().is_empty() {
            &placeholder_brush[..]
        } else {
            page.brushes()
        };
        let stops = if page.records().is_empty() {
            &placeholder_stop[..]
        } else {
            page.records()
        };
        let payloads = [
            bytemuck::cast_slice(brushes),
            bytemuck::cast_slice(stops),
            bytemuck::cast_slice(compiled.hinted_paints.as_slice()),
        ];
        let limits = device.limits();
        for payload in payloads {
            if payload.is_empty()
                || payload.len() as u64 > limits.max_buffer_size
                || payload.len() as u64 > u64::from(limits.max_storage_buffer_binding_size)
            {
                return Err(Error::Capacity);
            }
        }
        for paint in &compiled.hinted_paints {
            if paint.kind > 1
                || paint.reserved != 0
                || (paint.kind == 0
                    && (paint.brush_index as usize >= brushes.len() || paint.flags != 0))
                || (paint.kind == 1
                    && (paint.flags & !(7 | (0xff << 8)) != 0 || paint.flags >> 8 > 10))
                || !paint
                    .source_offset_opacity
                    .iter()
                    .chain(&paint.uv_bounds)
                    .chain(&paint.texture_quad_01)
                    .chain(&paint.texture_quad_23)
                    .chain(&paint.sampling)
                    .all(|v| v.is_finite())
            {
                return Err(Error::InvalidInput);
            }
        }
        let buffers = payloads.map(|contents| {
            device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("ProGPU immutable hinted material records"),
                contents,
                usage: wgpu::BufferUsages::STORAGE,
            })
        });
        let bindings = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU immutable direct hinted paint frame"),
            layout: &self.pipelines[0].get_bind_group_layout(0),
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: uniform.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: buffers[0].as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: buffers[1].as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: buffers[2].as_entire_binding(),
                },
            ],
        });
        Ok(PreparedHintedPaint {
            bindings,
            placeholder: self.placeholder.clone(),
        })
    }
    pub(in crate::renderer) fn draw(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        text: &PreparedText,
        range: std::ops::Range<u32>,
        vertex_count: u32,
        mask: Option<&wgpu::BindGroup>,
        texture: Option<&wgpu::BindGroup>,
        pipeline: Option<&wgpu::RenderPipeline>,
    ) -> Result<(), Error> {
        let paint = text.hinted.as_ref().ok_or(Error::ResourceDomain)?;
        if !matches!(vertex_count, 6 | 12) {
            return Err(Error::InvalidInput);
        }
        pass.set_pipeline(pipeline.unwrap_or(&self.pipelines[usize::from(mask.is_some())]));
        pass.set_bind_group(0, &paint.bindings, &[]);
        pass.set_bind_group(1, &text.atlas, &[]);
        if let Some(mask) = mask {
            pass.set_bind_group(2, mask, &[]);
        }
        pass.set_bind_group(3, texture.unwrap_or(&paint.placeholder), &[]);
        pass.set_vertex_buffer(0, text.instances.slice(..));
        pass.draw(0..vertex_count, range);
        Ok(())
    }
}
