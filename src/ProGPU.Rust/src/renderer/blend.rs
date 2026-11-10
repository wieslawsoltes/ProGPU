//! Original fixed draw blending with canonical straight/premultiplied entries.
//! Pipeline variants are owned by the actual renderer/device and created lazily.
use super::*;
use crate::scene::LayerBlend;
use std::collections::HashMap;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(super) enum Family {
    Vector,
    Text,
    Hinted,
}
#[derive(Default)]
pub(super) struct DrawBlendPipelines {
    pipelines: HashMap<(Family, LayerBlend, bool), wgpu::RenderPipeline>,
}
impl DrawBlendPipelines {
    pub(super) fn get(
        &self,
        family: Family,
        blend: LayerBlend,
        masked: bool,
    ) -> Result<Option<&wgpu::RenderPipeline>, Error> {
        if blend == LayerBlend::SrcOver {
            return Ok(None);
        }
        self.pipelines
            .get(&(family, blend, masked))
            .map(Some)
            .ok_or(Error::ResourceDomain)
    }
    async fn prepare(
        &mut self,
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        family: Family,
        blend: LayerBlend,
        masked: bool,
    ) -> Result<(), Error> {
        let key = (family, blend, masked);
        if blend == LayerBlend::SrcOver || self.pipelines.contains_key(&key) {
            return Ok(());
        }
        let blend_state = blend.straight_draw_state()?;
        self.pipelines.try_reserve(1).map_err(|_| Error::Capacity)?;
        let source = match family {
            Family::Vector => shaders::VECTOR,
            Family::Text => shaders::TEXT,
            Family::Hinted => shaders::HINTED_GLYPH_PAINT,
        };
        let premultiplied = blend.requires_premultiplied();
        let entry = match (family, masked, premultiplied) {
            (_, false, false) => "fs_main_unmasked",
            (_, false, true) => "fs_main_premultiplied_unmasked",
            (Family::Hinted, true, false) => "fs_main_mask_chain",
            (Family::Hinted, true, true) => "fs_main_mask_chain_premultiplied",
            (_, true, false) => "fs_main_chain",
            // Original Vector/Text premultiplied entries consume one primary
            // mask. Scope compilation materializes longer chains first.
            (_, true, true) => "fs_main_premultiplied",
        };
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU canonical per-draw blend shader"),
            source: wgpu::ShaderSource::Wgsl(source.into()),
        });
        let mask_layout = masked.then(|| layout::mask(device));
        let pipeline_layout = match family {
            Family::Hinted => layout::hinted_text(device),
            Family::Vector => {
                layout::pipeline(device, layout::Family::Vector, mask_layout.as_ref())
            }
            Family::Text => layout::pipeline(device, layout::Family::Text, mask_layout.as_ref()),
        };
        let vector_attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2,
            3=>Float32,4=>Float32x2,5=>Float32,6=>Float32,7=>Float32];
        let text_attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x2,2=>Float32x2,
            3=>Float32x4,4=>Float32x4,5=>Float32x4,6=>Float32x4,7=>Float32];
        let hinted_attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x2,2=>Float32x2,
            3=>Float32x4,4=>Float32x4,5=>Float32x4,6=>Float32x4,7=>Float32,8=>Uint32];
        let attributes: &[wgpu::VertexAttribute] = match family {
            Family::Vector => &vector_attributes,
            Family::Text => &text_attributes,
            Family::Hinted => &hinted_attributes,
        };
        let vector = family == Family::Vector;
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU original fixed per-draw blend"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &[Some(wgpu::VertexBufferLayout {
                    array_stride: if vector { 56 } else { 96 },
                    step_mode: if vector {
                        wgpu::VertexStepMode::Vertex
                    } else {
                        wgpu::VertexStepMode::Instance
                    },
                    attributes,
                })],
            },
            primitive: Default::default(),
            depth_stencil: None,
            multisample: Default::default(),
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some(entry),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: Some(blend_state),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        self.pipelines.insert(key, pipeline);
        Ok(())
    }
}
impl Renderer {
    pub(super) async fn prepare_draw_blends(
        &mut self,
        compiled: &CompiledScene,
    ) -> Result<(), Error> {
        let mut state = 0;
        for draw in &compiled.draws {
            if let DrawRange::State(index) = draw {
                state = *index;
                continue;
            }
            let current = compiled.states.get(state).ok_or(Error::ResourceDomain)?;
            if current.blend == LayerBlend::SrcOver {
                continue;
            }
            let family = match draw {
                DrawRange::Vector(_) => Family::Vector,
                DrawRange::Text(_) => Family::Text,
                DrawRange::HintedText(..) => Family::Hinted,
                _ => continue,
            };
            if family != Family::Hinted
                && current.blend.requires_premultiplied()
                && current.count > 1
            {
                return Err(Error::ResourceDomain);
            }
            self.draw_blends
                .prepare(
                    &self.device,
                    self.format,
                    family,
                    current.blend,
                    current.count != 0,
                )
                .await?;
        }
        Ok(())
    }
}
