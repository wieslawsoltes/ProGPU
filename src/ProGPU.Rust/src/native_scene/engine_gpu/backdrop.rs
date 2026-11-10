//! Original parent-copy, alpha-only initialization and filtered backdrop resolve.
use super::frames::NativeSceneFrames;
use super::*;
use crate::native_scene::{
    engine_resources::{
        effects::PreparedNativeEffects,
        layers::{NativeLayerTarget, PreparedNativeLayerComposite},
    },
    execution::{
        NativeReplayEffect, NativeReplayLayerPush, NativeReplayOperation, NativeReplayPlan,
    },
};
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeBackdropReplayMetrics {
    pub texture_copy_bytes: u64,
    pub render_passes: u32,
    pub draws: u32,
    pub effect_passes: u32,
    /// Original outer semantic counter is independent of actual emitted draws.
    pub source_draw_counter: u32,
}
pub struct PreparedNativeBackdrop {
    source: Arc<NativeReplayPlan>,
    operation_index: usize,
    parent: Arc<NativeLayerTarget>,
    child: Arc<NativeLayerTarget>,
    vertices: Arc<PreparedNativeLayerComposite>,
    effects: Option<(Arc<PreparedNativeEffects>, usize)>,
    alpha_pipeline: Option<wgpu::RenderPipeline>,
    copy_pipeline: Option<wgpu::RenderPipeline>,
}
impl PreparedNativeBackdrop {
    pub(crate) async fn prepare(
        pipelines: &mut NativePipelineCache,
        source: Arc<NativeReplayPlan>,
        operation_index: usize,
        frames: &NativeSceneFrames,
        vertices: Arc<PreparedNativeLayerComposite>,
        effects: Option<(Arc<PreparedNativeEffects>, usize)>,
    ) -> Result<Arc<Self>, Error> {
        let NativeReplayOperation::PushLayer(push) = source
            .operations()
            .get(operation_index)
            .ok_or(Error::InvalidInput)?
        else {
            return Err(Error::InvalidInput);
        };
        if !push.backdrop
            || !Arc::ptr_eq(&source, frames.source())
            || vertices.device() != pipelines.device()
        {
            return Err(Error::ResourceDomain);
        }
        let parent = frames
            .targets()
            .target(push.parent)
            .ok_or(Error::ResourceDomain)?
            .clone();
        let child = frames
            .targets()
            .target(push.target)
            .ok_or(Error::ResourceDomain)?
            .clone();
        let p = parent.color().ok_or(Error::ResourceDomain)?;
        let c = child.color().ok_or(Error::ResourceDomain)?;
        let size = [push.source_extent.width, push.source_extent.height];
        let origin = push.backdrop_origin;
        if p.device() != pipelines.device()
            || c.device() != pipelines.device()
            || p.texture().format() != c.texture().format()
            || !p.texture().usage().contains(wgpu::TextureUsages::COPY_SRC)
            || !c.texture().usage().contains(wgpu::TextureUsages::COPY_DST)
            || (0..2).any(|i| {
                origin[i] > p.size()[i]
                    || size[i] > p.size()[i] - origin[i]
                    || size[i] > c.size()[i]
            })
            || child.frame().is_none()
        {
            return Err(Error::ResourceDomain);
        }
        match (&push.effect, &effects) {
            (None, None) => {}
            (Some(NativeReplayEffect::Builtin(plan)), Some((programs, index))) => {
                let request = programs.request(*index).ok_or(Error::ResourceDomain)?;
                if !Arc::ptr_eq(&request.target, &child) || !Arc::ptr_eq(&request.plan, plan) {
                    return Err(Error::ResourceDomain);
                }
            }
            _ => return Err(Error::ResourceDomain),
        }
        let visible = !size.contains(&0);
        if visible && (push.ignore_alpha || effects.is_some()) {
            let first = push
                .first_backdrop_resolve_vertex
                .ok_or(Error::InvalidInput)?;
            if first % 4 != 0 || first as usize + 4 > vertices.vertices().len() {
                return Err(Error::InvalidInput);
            }
        }
        let alpha_pipeline = if visible && push.ignore_alpha {
            Some(
                pipelines
                    .prepare(NativeRenderPipeline::Group(NativeGroupPipeline {
                        blend_mode: BLEND_SRC,
                        masked: false,
                        coverage_only: true,
                        channels: NativeWriteChannels::Alpha,
                        linear_byte_opacity: false,
                    }))
                    .await?
                    .0,
            )
        } else {
            None
        };
        let copy_pipeline = if visible && effects.is_some() {
            Some(
                pipelines
                    .prepare(NativeRenderPipeline::Group(NativeGroupPipeline {
                        blend_mode: BLEND_SRC,
                        masked: false,
                        coverage_only: false,
                        channels: NativeWriteChannels::All,
                        linear_byte_opacity: false,
                    }))
                    .await?
                    .0,
            )
        } else {
            None
        };
        Ok(Arc::new(Self {
            source,
            operation_index,
            parent,
            child,
            vertices,
            effects,
            alpha_pipeline,
            copy_pipeline,
        }))
    }
    pub fn source(&self) -> &NativeReplayLayerPush {
        match &self.source.operations()[self.operation_index] {
            NativeReplayOperation::PushLayer(push) => push,
            _ => unreachable!("prepared backdrop operation"),
        }
    }
    pub fn encode(
        &self,
        encoder: &mut wgpu::CommandEncoder,
    ) -> Result<NativeBackdropReplayMetrics, Error> {
        let push = self.source();
        let size = [push.source_extent.width, push.source_extent.height];
        let child = self.child.color().ok_or(Error::ResourceDomain)?;
        let parent = self.parent.color().ok_or(Error::ResourceDomain)?;
        let mut metrics = NativeBackdropReplayMetrics {
            render_passes: 1,
            source_draw_counter: u32::from(push.ignore_alpha || self.effects.is_some()),
            ..Default::default()
        };
        // Whole pooled storage clears before the logical source extent is copied.
        drop(begin(encoder, child.view(), false));
        if size.contains(&0) {
            return Ok(metrics);
        }
        encoder.copy_texture_to_texture(
            wgpu::TexelCopyTextureInfo {
                texture: parent.texture(),
                mip_level: 0,
                origin: wgpu::Origin3d {
                    x: push.backdrop_origin[0],
                    y: push.backdrop_origin[1],
                    z: 0,
                },
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::TexelCopyTextureInfo {
                texture: child.texture(),
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::Extent3d {
                width: size[0],
                height: size[1],
                depth_or_array_layers: 1,
            },
        );
        metrics.texture_copy_bytes = u64::from(size[0]) * u64::from(size[1]) * 4;
        if let Some(pipeline) = &self.alpha_pipeline {
            let mut pass = begin(encoder, child.view(), true);
            self.draw(&mut pass, pipeline, parent.linear_binding())?;
            metrics.render_passes += 1;
            metrics.draws += 1;
        }
        if let Some((effects, index)) = &self.effects {
            effects.encode(encoder, *index)?;
            metrics.effect_passes = effects
                .request(*index)
                .ok_or(Error::ResourceDomain)?
                .plan
                .pass_count();
            let output = effects.output(*index).ok_or(Error::ResourceDomain)?;
            let mut pass = begin(encoder, child.view(), false);
            pass.set_scissor_rect(0, 0, size[0], size[1]);
            self.draw(
                &mut pass,
                self.copy_pipeline.as_ref().ok_or(Error::ResourceDomain)?,
                output.linear_binding(),
            )?;
            metrics.render_passes += 1;
            metrics.draws += 1;
        }
        Ok(metrics)
    }
    fn draw(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        pipeline: &wgpu::RenderPipeline,
        texture: &wgpu::BindGroup,
    ) -> Result<(), Error> {
        let first = self
            .source()
            .first_backdrop_resolve_vertex
            .ok_or(Error::InvalidInput)?;
        let offset = u64::from(first) * 56;
        pass.set_pipeline(pipeline);
        pass.set_bind_group(
            0,
            &self.child.frame().ok_or(Error::ResourceDomain)?.frame,
            &[],
        );
        pass.set_bind_group(1, texture, &[]);
        pass.set_vertex_buffer(0, self.vertices.buffer().slice(offset..offset + 224));
        pass.set_index_buffer(
            self.vertices.indices().slice(..24),
            wgpu::IndexFormat::Uint32,
        );
        pass.draw_indexed(0..6, 0, 0..1);
        Ok(())
    }
    pub fn retain_submitted(self: &Arc<Self>, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
fn begin<'a>(
    encoder: &'a mut wgpu::CommandEncoder,
    view: &wgpu::TextureView,
    load: bool,
) -> wgpu::RenderPass<'a> {
    encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
        label: Some("Native original backdrop storage pass"),
        color_attachments: &[Some(wgpu::RenderPassColorAttachment {
            view,
            depth_slice: None,
            resolve_target: None,
            ops: wgpu::Operations {
                load: if load {
                    wgpu::LoadOp::Load
                } else {
                    wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT)
                },
                store: wgpu::StoreOp::Store,
            },
        })],
        ..Default::default()
    })
}
