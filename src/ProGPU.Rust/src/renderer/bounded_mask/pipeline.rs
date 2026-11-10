//! Original alpha output entries with per-draw premultiplied R8 blending.
use super::*;
use std::collections::HashMap;

#[derive(Clone, Copy, PartialEq, Eq, Hash)]
pub(super) enum Family {
    Vector,
    Text,
    Hinted,
    Image,
}
#[derive(Default)]
pub(in crate::renderer) struct BoundedMaskPipelines {
    pipelines: HashMap<(Family, LayerBlend, bool), wgpu::RenderPipeline>,
}
impl BoundedMaskPipelines {
    pub(super) async fn prepare(
        &mut self,
        device: &wgpu::Device,
        scene: &PreparedScene,
    ) -> Result<(), Error> {
        let mut current = 0;
        // Complete draw-family/blend/resource admission before compiling a pipeline.
        let mut keys = Vec::new();
        keys.try_reserve(scene.draws.len())
            .map_err(|_| Error::Capacity)?;
        for draw in &scene.draws {
            if let DrawRange::State(index) = draw {
                current = *index;
                continue;
            }
            let state = scene.masks.as_ref().and_then(|m| m.states.get(current));
            let blend = state.map_or(LayerBlend::SrcOver, |s| s.blend);
            let (family, blend) = match draw {
                DrawRange::Vector(_) => (Family::Vector, blend),
                DrawRange::Text(_) => (Family::Text, blend),
                DrawRange::HintedText(_, vertices, _) if matches!(vertices, 6 | 12) => {
                    (Family::Hinted, blend)
                }
                DrawRange::Image(_, _, blend) => (Family::Image, *blend),
                // Original mask render passes have only these four painter families.
                // Other source operators must lower to a genuine supported draw.
                _ => return Err(Error::UnsupportedExecution),
            };
            blend.fixed()?;
            let key = (family, blend, state.is_some_and(|s| s.binding.is_some()));
            if !self.pipelines.contains_key(&key) && !keys.contains(&key) {
                keys.push(key);
            }
        }
        self.pipelines
            .try_reserve(keys.len())
            .map_err(|_| Error::Capacity)?;
        for key in keys {
            let pipeline = create(device, key).await?;
            self.pipelines.insert(key, pipeline);
        }
        Ok(())
    }
    pub(super) fn get(
        &self,
        family: Family,
        blend: LayerBlend,
        masked: bool,
    ) -> Result<&wgpu::RenderPipeline, Error> {
        self.pipelines
            .get(&(family, blend, masked))
            .ok_or(Error::ResourceDomain)
    }
}
async fn create(
    device: &wgpu::Device,
    (family, blend, masked): (Family, LayerBlend, bool),
) -> Result<wgpu::RenderPipeline, Error> {
    let source = match family {
        Family::Vector => shaders::VECTOR,
        Family::Text => shaders::TEXT,
        Family::Hinted => shaders::HINTED_GLYPH_PAINT,
        Family::Image => shaders::TEXTURE,
    };
    // Original owned shader functions evaluate the same alpha. This additive
    // entry transports the existing Rust analytic continuation without changing
    // the source operation order or quantizing its masks in an extra R8 pass.
    let continuation = match family {
        Family::Vector => {
            "@fragment fn fs_bounded_mask_chain(input: VertexOutput) -> @location(0) vec4<f32> { let maskAlpha = sample_mask_alpha(input.position.xy) * sample_mask_chain_alpha(input.position.xy); let color = vector_fs_main(input, maskAlpha, false); if (maskAlpha <= 0.0) { discard; } return vec4<f32>(color.a, 0.0, 0.0, color.a); }"
        }
        Family::Text => {
            "@fragment fn fs_bounded_mask_chain(input: VertexOutput) -> @location(0) vec4<f32> { let color = text_fs_main_with_mask_alpha(input, sample_mask_alpha(input.position.xy) * sample_mask_chain_alpha(input.position.xy)); return vec4<f32>(color.a, 0.0, 0.0, color.a); }"
        }
        Family::Image => {
            "@fragment fn fs_bounded_mask_chain(input: VertexOutput) -> @location(0) vec4<f32> { let color = texture_fs_main_with_mask(input, sample_mask_alpha(input.position.xy) * sample_mask_chain_alpha(input.position.xy)); return vec4<f32>(color.a, 0.0, 0.0, color.a); }"
        }
        Family::Hinted => "",
    };
    let shader_source = if masked && family != Family::Hinted {
        format!("{source}\n{continuation}")
    } else {
        source.to_owned()
    };
    let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("ProGPU original bounded R8 mask shader"),
        source: wgpu::ShaderSource::Wgsl(shader_source.into()),
    });
    let mask = masked.then(|| layout::mask(device));
    let layout = match family {
        Family::Vector => layout::pipeline(device, layout::Family::Vector, mask.as_ref()),
        Family::Text => layout::pipeline(device, layout::Family::Text, mask.as_ref()),
        Family::Image => layout::pipeline(device, layout::Family::Image, mask.as_ref()),
        Family::Hinted => layout::hinted_text(device),
    };
    let vector = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2,3=>Float32,4=>Float32x2,5=>Float32,6=>Float32,7=>Float32];
    let text = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x2,2=>Float32x2,3=>Float32x4,4=>Float32x4,5=>Float32x4,6=>Float32x4,7=>Float32];
    let hinted = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x2,2=>Float32x2,3=>Float32x4,4=>Float32x4,5=>Float32x4,6=>Float32x4,7=>Float32,8=>Uint32];
    let (attributes, stride, step) = match family {
        Family::Vector | Family::Image => (vector.as_slice(), 56, wgpu::VertexStepMode::Vertex),
        Family::Text => (text.as_slice(), 96, wgpu::VertexStepMode::Instance),
        Family::Hinted => (hinted.as_slice(), 96, wgpu::VertexStepMode::Instance),
    };
    let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some("ProGPU original bounded R8 opacity mask"),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module: &shader,
            entry_point: Some("vs_main"),
            compilation_options: Default::default(),
            buffers: &[Some(wgpu::VertexBufferLayout {
                array_stride: stride,
                step_mode: step,
                attributes,
            })],
        },
        fragment: Some(wgpu::FragmentState {
            module: &shader,
            entry_point: Some(if !masked {
                "fs_mask_unmasked"
            } else if family == Family::Hinted {
                "fs_mask_chain"
            } else {
                "fs_bounded_mask_chain"
            }),
            compilation_options: Default::default(),
            targets: &[Some(wgpu::ColorTargetState {
                format: wgpu::TextureFormat::R8Unorm,
                blend: Some(blend.fixed()?),
                write_mask: wgpu::ColorWrites::RED,
            })],
        }),
        primitive: Default::default(),
        depth_stencil: None,
        multisample: Default::default(),
        multiview_mask: None,
        cache: None,
    });
    if let Some(error) = scope.pop().await {
        return Err(error.into());
    }
    Ok(pipeline)
}

impl Renderer {
    pub(in crate::renderer) async fn capture_prepared_bounded_mask(
        &self,
        prepared: &PreparedScene,
        pipelines: &mut BoundedMaskPipelines,
    ) -> Result<(crate::image::GpuImage, MaskCompositionMetrics), Error> {
        if !self.is_scene_current(prepared) {
            return Err(Error::ResourceDomain);
        }
        pipelines.prepare(&self.device, prepared).await?;
        let size = [prepared.frame.width, prepared.frame.height];
        let bytes = u64::from(size[0]) * u64::from(size[1]);
        if bytes > 512 * 1024 * 1024 {
            return Err(Error::Capacity);
        }
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU bounded original R8 opacity source"),
            size: wgpu::Extent3d {
                width: size[0],
                height: size[1],
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::R8Unorm,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                | wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let view = texture.create_view(&Default::default());
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU original bounded opacity source"),
            });
        let mut draws = 0u32;
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("ProGPU bounded source alpha"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
            let mut current = 0;
            for draw in &prepared.draws {
                if let DrawRange::State(index) = draw {
                    current = *index;
                    continue;
                }
                draws = draws.checked_add(1).ok_or(Error::Capacity)?;
                let state = prepared.masks.as_ref().and_then(|m| m.states.get(current));
                let scissor = state.map_or([0, 0, size[0], size[1]], |s| s.scissor);
                if scissor[2] == 0 || scissor[3] == 0 {
                    continue;
                }
                pass.set_scissor_rect(scissor[0], scissor[1], scissor[2], scissor[3]);
                let mask = state.and_then(|s| s.binding.as_ref());
                let blend = state.map_or(LayerBlend::SrcOver, |s| s.blend);
                match draw {
                    DrawRange::Vector(range) => {
                        pass.set_pipeline(pipelines.get(Family::Vector, blend, mask.is_some())?);
                        pass.set_bind_group(0, &prepared.bindings, &[]);
                        pass.set_bind_group(1, &prepared.atlas, &[]);
                        if let Some(mask) = mask {
                            pass.set_bind_group(2, mask, &[]);
                        }
                        pass.set_vertex_buffer(0, prepared.vertices.slice(..));
                        pass.set_index_buffer(
                            prepared.indices.slice(..),
                            wgpu::IndexFormat::Uint32,
                        );
                        pass.draw_indexed(range.clone(), 0, 0..1);
                    }
                    DrawRange::Text(range) => {
                        let text = prepared.text.as_ref().ok_or(Error::ResourceDomain)?;
                        pass.set_pipeline(pipelines.get(Family::Text, blend, mask.is_some())?);
                        pass.set_bind_group(0, &text.bindings, &[]);
                        pass.set_bind_group(1, &text.atlas, &[]);
                        if let Some(mask) = mask {
                            pass.set_bind_group(2, mask, &[]);
                        }
                        pass.set_vertex_buffer(0, text.instances.slice(..));
                        pass.draw(0..6, range.clone());
                    }
                    DrawRange::HintedText(range, vertices, binding) => {
                        let text = prepared.text.as_ref().ok_or(Error::ResourceDomain)?;
                        let state = self
                            .text
                            .as_ref()
                            .and_then(|t| t.hinted.as_ref())
                            .ok_or(Error::ResourceDomain)?;
                        let image = binding
                            .map(|index| {
                                prepared
                                    .images
                                    .as_ref()
                                    .and_then(|i| i.bindings.get(index))
                                    .ok_or(Error::ResourceDomain)
                            })
                            .transpose()?;
                        state.draw(
                            &mut pass,
                            text,
                            range.clone(),
                            *vertices,
                            mask,
                            image,
                            Some(pipelines.get(Family::Hinted, blend, mask.is_some())?),
                            None,
                            false,
                        )?;
                    }
                    DrawRange::Image(range, binding, blend) => {
                        let images = prepared.images.as_ref().ok_or(Error::ResourceDomain)?;
                        pass.set_pipeline(pipelines.get(Family::Image, *blend, mask.is_some())?);
                        pass.set_bind_group(0, &images.uniforms, &[]);
                        pass.set_bind_group(
                            1,
                            images.bindings.get(*binding).ok_or(Error::ResourceDomain)?,
                            &[],
                        );
                        if let Some(mask) = mask {
                            pass.set_bind_group(2, mask, &[]);
                        }
                        pass.set_vertex_buffer(0, images.vertices.slice(..));
                        pass.set_index_buffer(images.indices.slice(..), wgpu::IndexFormat::Uint32);
                        pass.draw_indexed(range.clone(), 0, 0..1);
                    }
                    _ => return Err(Error::UnsupportedExecution),
                }
            }
        }
        self.submit_encoded_scene(prepared, encoder)?;
        Ok((
            crate::image::GpuImage::from_texture(
                &self.device,
                &texture,
                crate::image::ImageAlpha::Straight,
            )?,
            MaskCompositionMetrics {
                source_captures: 1,
                source_draws: draws,
                render_passes: 1,
                texture_bytes: bytes,
                geometry_bytes: prepared.metrics.geometry_bytes,
                uniform_bytes: prepared.metrics.uniform_bytes,
                atlas_bytes: prepared.metrics.atlas_bytes,
                ..Default::default()
            },
        ))
    }
}
