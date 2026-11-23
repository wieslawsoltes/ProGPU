use super::*;

pub(super) const STAGES: [[&str; 10]; 3] = [
    [
        "cs_point_clip",
        "cs_point_bounds",
        "cs_point_rect_fill",
        "cs_point_rect_stroke",
        "cs_point_ellipse_fill",
        "cs_point_ellipse_stroke",
        "cs_point_line_stroke",
        "cs_point_path_fill",
        "cs_point_path_stroke",
        "cs_point_other",
    ],
    [
        "cs_bounds_clip",
        "cs_bounds_bounds",
        "cs_bounds_rect_fill",
        "cs_bounds_rect_stroke",
        "cs_bounds_ellipse_fill",
        "cs_bounds_ellipse_stroke",
        "cs_bounds_line_stroke",
        "cs_bounds_path_fill",
        "cs_bounds_path_stroke",
        "cs_bounds_other",
    ],
    [
        "cs_ellipse_clip",
        "cs_ellipse_bounds",
        "cs_ellipse_rect_fill",
        "cs_ellipse_rect_stroke",
        "cs_ellipse_ellipse_fill",
        "cs_ellipse_ellipse_stroke",
        "cs_ellipse_line_stroke",
        "cs_ellipse_path_fill",
        "cs_ellipse_path_stroke",
        "cs_ellipse_other",
    ],
];
pub(super) fn layout(device: &wgpu::Device, ordered: bool) -> wgpu::BindGroupLayout {
    let entries: Vec<_> = (0..if ordered { 7 } else { 6 })
        .map(|index| wgpu::BindGroupLayoutEntry {
            binding: if index == 6 { 7 } else { index },
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Storage {
                    read_only: index != 4 && index != 6,
                },
                has_dynamic_offset: false,
                min_binding_size: None,
            },
            count: None,
        })
        .collect();
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU exact hit-query bindings"),
        entries: &entries,
    })
}
pub(super) fn bindings(
    device: &wgpu::Device,
    layout: &wgpu::BindGroupLayout,
    resources: &Resources,
    result: &wgpu::Buffer,
) -> wgpu::BindGroup {
    let mut entries: Vec<_> = [
        &resources.query,
        &resources.nodes,
        &resources.indices,
        &resources.primitives,
        result,
        &resources.segments,
    ]
    .into_iter()
    .enumerate()
    .map(|(binding, buffer)| wgpu::BindGroupEntry {
        binding: binding as u32,
        resource: buffer.as_entire_binding(),
    })
    .collect();
    if let Some(candidates) = &resources.candidates {
        entries.push(wgpu::BindGroupEntry {
            binding: 7,
            resource: candidates.as_entire_binding(),
        });
    }
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ProGPU retained hit-query resources"),
        layout,
        entries: &entries,
    })
}
impl GpuHitIndex {
    pub(super) async fn prepare_pipeline(&mut self, entry: &'static str) -> Result<()> {
        if self.pipelines.contains_key(entry) {
            return Ok(());
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let shader = self.shader.clone().unwrap_or_else(|| {
            self.device
                .create_shader_module(wgpu::ShaderModuleDescriptor {
                    label: Some("ProGPU original hit geometry"),
                    source: wgpu::ShaderSource::Wgsl(crate::shaders::HIT_TESTING.into()),
                })
        });
        let pipeline = self
            .device
            .create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
                label: Some(entry),
                layout: Some(&self.pipeline_layout),
                module: &shader,
                entry_point: Some(entry),
                compilation_options: Default::default(),
                cache: None,
            });
        if let Some(error) = scope.pop().await {
            return Err(Error::Gpu(error).into());
        }
        self.shader = Some(shader);
        self.pipelines.insert(entry, pipeline);
        self.metrics.pipeline_creations += 1;
        Ok(())
    }
}
