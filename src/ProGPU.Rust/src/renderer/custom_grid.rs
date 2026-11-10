//! Original binding-free CustomGrid shader, separate from ordinary vector grids.
use super::*;
pub(super) struct PreparedCustomGrid {
    pipeline: wgpu::RenderPipeline,
    _sources: Vec<Arc<crate::picture_archive::ArchiveCommand>>,
}
impl PreparedCustomGrid {
    pub(super) fn draw(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        scene: &PreparedScene,
        range: std::ops::Range<u32>,
    ) {
        pass.set_pipeline(&self.pipeline);
        pass.set_vertex_buffer(0, scene.vertices.slice(..));
        pass.set_index_buffer(scene.indices.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(range, 0, 0..1);
    }
}
impl Renderer {
    pub(super) async fn prepare_custom_grid(
        &mut self,
        compiled: &CompiledScene,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        if compiled.custom_grids.is_empty() {
            return Ok(());
        }
        if self.custom_grid.is_none() {
            let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
            let shader = self
                .device
                .create_shader_module(wgpu::ShaderModuleDescriptor {
                    label: Some("ProGPU original CustomGrid"),
                    source: wgpu::ShaderSource::Wgsl(shaders::CUSTOM_GRID.into()),
                });
            let layout = self
                .device
                .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                    label: Some("ProGPU binding-free grid layout"),
                    bind_group_layouts: &[],
                    immediate_size: 0,
                });
            let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2,
                3=>Float32,4=>Float32x2,5=>Float32,6=>Float32,7=>Float32];
            let pipeline = self
                .device
                .create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                    label: Some("ProGPU fullscreen procedural grid"),
                    layout: Some(&layout),
                    vertex: wgpu::VertexState {
                        module: &shader,
                        entry_point: Some("vs_main"),
                        compilation_options: Default::default(),
                        buffers: &[Some(wgpu::VertexBufferLayout {
                            array_stride: size_of::<VectorVertex>() as u64,
                            step_mode: wgpu::VertexStepMode::Vertex,
                            attributes: &attributes,
                        })],
                    },
                    primitive: Default::default(),
                    depth_stencil: None,
                    multisample: Default::default(),
                    fragment: Some(wgpu::FragmentState {
                        module: &shader,
                        entry_point: Some("fs_main"),
                        compilation_options: Default::default(),
                        targets: &[Some(wgpu::ColorTargetState {
                            format: self.format,
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
            self.custom_grid = Some(pipeline);
        }
        let mut sources = Vec::new();
        sources
            .try_reserve_exact(compiled.custom_grids.len())
            .map_err(|_| Error::Capacity)?;
        sources.extend(compiled.custom_grids.iter().cloned());
        prepared.custom_grid = Some(PreparedCustomGrid {
            pipeline: self
                .custom_grid
                .as_ref()
                .ok_or(Error::ResourceDomain)?
                .clone(),
            _sources: sources,
        });
        Ok(())
    }
}
