//! Canonical Vector fs_mask_target_space with original immutable source material.
use super::*;
pub(super) struct PreparedPaint {
    pub texture: NativeImageTexture,
    pipeline: wgpu::RenderPipeline,
    frame: wgpu::BindGroup,
    atlas: wgpu::BindGroup,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    count: u32,
    scissor: NativeScissor,
    _buffers: [wgpu::Buffer; 3],
}
impl PreparedPaint {
    pub fn encode(&self, encoder: &mut wgpu::CommandEncoder) {
        let mut pass = begin(encoder, self.texture.view());
        if self.scissor.drawable && self.count != 0 {
            pass.set_pipeline(&self.pipeline);
            pass.set_bind_group(0, &self.frame, &[]);
            pass.set_bind_group(1, &self.atlas, &[]);
            pass.set_vertex_buffer(0, self.vertices.slice(..));
            pass.set_index_buffer(self.indices.slice(..), wgpu::IndexFormat::Uint32);
            pass.set_scissor_rect(
                self.scissor.x,
                self.scissor.y,
                self.scissor.width,
                self.scissor.height,
            );
            pass.draw_indexed(0..self.count, 0, 0..1);
        }
    }
}
pub(super) async fn prepare(
    engine: &mut NativeMaskEngine,
    plan: brush::BrushPlan,
    target: NativeMaskTarget,
) -> Result<PreparedPaint, Error> {
    let pipelines = engine.pipelines.as_mut().ok_or(Error::ResourceDomain)?;
    let (pipeline, _) = pipelines
        .prepare(NativeRenderPipeline::BrushOpacityMask)
        .await?;
    let d = &engine.device;
    let limits = d.limits();
    let vertex_bytes = plan.vertices.len() as u64 * 56;
    let index_bytes = plan.indices.len() as u64 * 4;
    let gradient_bytes = (plan.stops.len() as u64 * 32).max(32);
    if vertex_bytes > limits.max_buffer_size
        || index_bytes > limits.max_buffer_size
        || gradient_bytes
            > limits
                .max_buffer_size
                .min(u64::from(limits.max_storage_buffer_binding_size))
    {
        return Err(Error::Capacity);
    }
    let mut uniforms = NativeGpuUniforms::new(target.size(), target.dpi_scale)?;
    uniforms.view[0] = plan.view[0];
    uniforms.view[5] = plan.view[1];
    uniforms.view[12] = plan.view[2];
    uniforms.view[13] = plan.view[3];
    let mut material = [0; wire::Brush::WIRE_SIZE];
    plan.brush
        .write_into(&mut material)
        .map_err(|_| Error::InvalidInput)?;
    let mut stops = allocate(gradient_bytes as usize)?;
    stops.resize(gradient_bytes as usize, 0);
    for (slot, stop) in stops.chunks_exact_mut(32).zip(plan.stops.iter()) {
        stop.write_into(slot).map_err(|_| Error::InvalidInput)?;
    }
    let scope = d.push_error_scope(wgpu::ErrorFilter::Validation);
    let texture = owned(
        d,
        &texture(d, target.size(), wgpu::TextureUsages::RENDER_ATTACHMENT),
    )?;
    let vertices = buffer(
        d,
        "Native brush-mask vertices",
        bytemuck::cast_slice(&plan.vertices),
        wgpu::BufferUsages::VERTEX,
    );
    let indices = buffer(
        d,
        "Native brush-mask indices",
        bytemuck::cast_slice(&plan.indices),
        wgpu::BufferUsages::INDEX,
    );
    let frame_buffer = buffer(
        d,
        "Native brush-mask target frame",
        &uniforms.to_bytes(),
        wgpu::BufferUsages::UNIFORM,
    );
    let brush_buffer = buffer(
        d,
        "Native mask material",
        &material,
        wgpu::BufferUsages::STORAGE,
    );
    let gradient_buffer = buffer(
        d,
        "Native mask original stops",
        &stops,
        wgpu::BufferUsages::STORAGE,
    );
    let layouts = pipelines.layouts();
    let frame = d.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("Native brush-mask frame and material"),
        layout: &layouts.vector,
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: frame_buffer.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: brush_buffer.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 2,
                resource: gradient_buffer.as_entire_binding(),
            },
        ],
    });
    let dummy = engine.dummy.as_ref().ok_or(Error::ResourceDomain)?;
    let sampler = &engine.samplers.as_ref().ok_or(Error::ResourceDomain)?[1];
    let atlas = d.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("Native brush-mask canonical atlas slot"),
        layout: &layouts.image.texture,
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::Sampler(sampler),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: wgpu::BindingResource::TextureView(dummy.view()),
            },
        ],
    });
    if let Some(error) = scope.pop().await {
        return Err(error.into());
    }
    Ok(PreparedPaint {
        texture,
        pipeline,
        frame,
        atlas,
        vertices,
        indices,
        count: u32::try_from(plan.indices.len()).map_err(|_| Error::Capacity)?,
        scissor: plan.scissor,
        _buffers: [frame_buffer, brush_buffer, gradient_buffer],
    })
}
