//! Original native two-pass Gaussian using the actual selected source views.
use super::*;
use crate::image_effect::{ImageBlurUniforms, YuvConversion};
#[derive(Clone)]
pub(super) struct BlurPipeline {
    pipeline: wgpu::RenderPipeline,
    layout: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
}
pub(super) struct PreparedBlur {
    pub output: NativeImageTexture,
    intermediate: NativeImageTexture,
    passes: [BlurPass; 2],
}
struct BlurPass {
    pipeline: wgpu::RenderPipeline,
    binding: wgpu::BindGroup,
    target: wgpu::TextureView,
    _uniform: wgpu::Buffer,
}
impl PreparedBlur {
    pub fn encode(&self, encoder: &mut wgpu::CommandEncoder) {
        for p in &self.passes {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("Native image live blur axis"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &p.target,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
            pass.set_pipeline(&p.pipeline);
            pass.set_bind_group(0, &p.binding, &[]);
            pass.draw(0..3, 0..1);
        }
    }
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        self.output.retain_submitted(queue);
        self.intermediate.retain_submitted(queue);
    }
}
impl NativeImagePageEngine {
    async fn blur_pipeline(&mut self, unfilterable: bool) -> Result<BlurPipeline, Error> {
        if let Some(p) = &self.blur_pipelines[usize::from(unfilterable)] {
            return Ok(p.clone());
        }
        let device = &self.device;
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let layout = layout::blur_layout(device, unfilterable);
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("Native original view-preserving Gaussian"),
            source: wgpu::ShaderSource::Wgsl(
                if unfilterable {
                    crate::shaders::IMAGE_BLUR_UNFILTERABLE
                } else {
                    crate::shaders::IMAGE_BLUR
                }
                .into(),
            ),
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Native Gaussian pipeline layout"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("Native Gaussian pipeline"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &[],
            },
            primitive: Default::default(),
            depth_stencil: None,
            multisample: Default::default(),
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs_main"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format: wgpu::TextureFormat::Rgba8Unorm,
                    blend: None,
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });
        let filter = if unfilterable {
            wgpu::FilterMode::Nearest
        } else {
            wgpu::FilterMode::Linear
        };
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("Native Gaussian source sampler"),
            mag_filter: filter,
            min_filter: filter,
            lod_max_clamp: 0.0,
            ..Default::default()
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let result = BlurPipeline {
            pipeline,
            layout,
            sampler,
        };
        self.blur_pipelines[usize::from(unfilterable)] = Some(result.clone());
        Ok(result)
    }
    pub(super) async fn prepare_blur(
        &mut self,
        source: &NativeImageTexture,
        chroma: Option<&NativeImageTexture>,
        effect: &wire::ImageEffect,
    ) -> Result<PreparedBlur, Error> {
        let unfilterable = effect.flags & SCENE_IMAGE_EFFECT_UNFILTERABLE_PLANAR != 0;
        let decode = effect.flags0[0] > 0.5;
        if effect.effects1[2] <= 0.01 && !unfilterable
            || (decode || unfilterable) && chroma.is_none()
        {
            return Err(Error::InvalidInput);
        }
        let [w, h] = source.size();
        let conversion = decode.then_some(YuvConversion {
            range: effect.yuv_range,
            red: effect.yuv_red,
            green: effect.yuv_green,
            blue: effect.yuv_blue,
        });
        let horizontal =
            ImageBlurUniforms::new(effect.effects1[2], [1.0 / w as f32, 0.0], conversion)?;
        let vertical = ImageBlurUniforms::new(effect.effects1[2], [0.0, 1.0 / h as f32], None)?;
        let first = self.blur_pipeline(unfilterable).await?;
        let second = self.blur_pipeline(false).await?;
        let intermediate = allocate_texture(
            &self.device,
            [w, h],
            wgpu::TextureFormat::Rgba8Unorm,
            wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::RENDER_ATTACHMENT,
        )?;
        let output = allocate_texture(
            &self.device,
            [w, h],
            wgpu::TextureFormat::Rgba8Unorm,
            wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::RENDER_ATTACHMENT,
        )?;
        let make = |pipeline: &BlurPipeline,
                    source: &NativeImageTexture,
                    chroma: &NativeImageTexture,
                    target: &NativeImageTexture,
                    uniforms: &ImageBlurUniforms| {
            let uniform = uniform_buffer(&self.device, bytemuck::bytes_of(uniforms));
            let binding = layout::binding(
                &self.device,
                &pipeline.layout,
                &[
                    layout::sampler_entry(0, &pipeline.sampler),
                    layout::view_entry(1, source.view()),
                    layout::view_entry(2, chroma.view()),
                    layout::buffer_entry(3, &uniform),
                ],
            );
            BlurPass {
                pipeline: pipeline.pipeline.clone(),
                binding,
                target: target.view().clone(),
                _uniform: uniform,
            }
        };
        let passes = [
            make(
                &first,
                source,
                chroma.unwrap_or(source),
                &intermediate,
                &horizontal,
            ),
            make(&second, &intermediate, &intermediate, &output, &vertical),
        ];
        Ok(PreparedBlur {
            output,
            intermediate,
            passes,
        })
    }
}
