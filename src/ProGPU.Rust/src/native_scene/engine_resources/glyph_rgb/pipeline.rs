//! Native named RGB shaders and exact raster/channel-write binding contracts.
use super::*;
use crate::native_scene::engine_gpu::{NativeMaskMode, NativeRenderLayouts};
use std::{collections::BTreeMap, num::NonZeroU64};
#[derive(Clone)]
pub(super) enum Raster {
    Compute(wgpu::ComputePipeline),
    Fragment(wgpu::RenderPipeline),
}
pub(super) struct Pipelines {
    pub raster_layout: wgpu::BindGroupLayout,
    pub composite_layout: wgpu::BindGroupLayout,
    pub raster: Raster,
    pub composite: [wgpu::RenderPipeline; 3],
    pub empty: Option<wgpu::BindGroup>,
}
pub(super) struct Cache {
    device: wgpu::Device,
    format: wgpu::TextureFormat,
    layouts: NativeRenderLayouts,
    entries: BTreeMap<(bool, u8), Arc<Pipelines>>,
}
impl Cache {
    pub fn new(
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        layouts: NativeRenderLayouts,
    ) -> Self {
        Self {
            device: device.clone(),
            format,
            layouts,
            entries: BTreeMap::new(),
        }
    }
    pub async fn prepare(
        &mut self,
        fragment: bool,
        mode: NativeMaskMode,
    ) -> Result<Arc<Pipelines>, Error> {
        let key = (
            fragment,
            match mode {
                NativeMaskMode::None => 0,
                NativeMaskMode::Sampled => 1,
                NativeMaskMode::Chain => 2,
            },
        );
        if let Some(found) = self.entries.get(&key) {
            return Ok(found.clone());
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let raster_shader = self
            .device
            .create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("ProGPU RGB glyph raster shader"),
                source: wgpu::ShaderSource::Wgsl(crate::shaders::GLYPH_RGB_RASTER.into()),
            });
        let composite_shader = self
            .device
            .create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("ProGPU RGB glyph composite shader"),
                source: wgpu::ShaderSource::Wgsl(crate::shaders::GLYPH_RGB_COMPOSITE.into()),
            });
        let count = if fragment { 4 } else { 5 };
        let mut entries = allocate(count)?;
        for index in 0..count {
            let binding = if fragment && index == 3 {
                4
            } else {
                index as u32
            };
            entries.push(wgpu::BindGroupLayoutEntry {
                binding,
                visibility: if fragment {
                    wgpu::ShaderStages::FRAGMENT
                } else {
                    wgpu::ShaderStages::COMPUTE
                },
                ty: wgpu::BindingType::Buffer {
                    ty: if binding == 0 || binding == 4 {
                        wgpu::BufferBindingType::Uniform
                    } else {
                        wgpu::BufferBindingType::Storage {
                            read_only: binding != 3,
                        }
                    },
                    has_dynamic_offset: binding == 0,
                    min_binding_size: NonZeroU64::new(match binding {
                        0 => 48,
                        1 => 32,
                        2 => 48,
                        4 => 16,
                        _ => 4,
                    }),
                },
                count: None,
            });
        }
        let raster_layout =
            self.device
                .create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                    label: Some("ProGPU RGB glyph raster bindings"),
                    entries: &entries,
                });
        let raster_pipeline_layout =
            self.device
                .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                    label: Some("ProGPU RGB glyph raster layout"),
                    bind_group_layouts: &[Some(&raster_layout)],
                    immediate_size: 0,
                });
        let raster =
            if fragment {
                Raster::Fragment(self.device.create_render_pipeline(
                    &wgpu::RenderPipelineDescriptor {
                        label: Some("ProGPU RGB glyph fragment raster pipeline"),
                        layout: Some(&raster_pipeline_layout),
                        vertex: wgpu::VertexState {
                            module: &raster_shader,
                            entry_point: Some("vs_raster_fallback"),
                            compilation_options: Default::default(),
                            buffers: &[],
                        },
                        primitive: Default::default(),
                        depth_stencil: None,
                        multisample: Default::default(),
                        fragment: Some(wgpu::FragmentState {
                            module: &raster_shader,
                            entry_point: Some("fs_rgb"),
                            compilation_options: Default::default(),
                            targets: &[Some(wgpu::ColorTargetState {
                                format: wgpu::TextureFormat::Rgba8Unorm,
                                blend: None,
                                write_mask: wgpu::ColorWrites::ALL,
                            })],
                        }),
                        multiview_mask: None,
                        cache: None,
                    },
                ))
            } else {
                Raster::Compute(self.device.create_compute_pipeline(
                    &wgpu::ComputePipelineDescriptor {
                        label: Some("ProGPU RGB glyph compute raster pipeline"),
                        layout: Some(&raster_pipeline_layout),
                        module: &raster_shader,
                        entry_point: Some("cs_rgb"),
                        compilation_options: Default::default(),
                        cache: None,
                    },
                ))
            };
        let composite_layout =
            self.device
                .create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                    label: Some("ProGPU RGB glyph composite bindings"),
                    entries: &[
                        texture_entry(0),
                        buffer_entry(1, wgpu::ShaderStages::FRAGMENT, true, 48),
                        buffer_entry(2, wgpu::ShaderStages::VERTEX_FRAGMENT, false, 32),
                        texture_entry(3),
                        buffer_entry(4, wgpu::ShaderStages::VERTEX_FRAGMENT, true, 32),
                        buffer_entry(5, wgpu::ShaderStages::FRAGMENT, true, 4),
                    ],
                });
        let mut group_layouts = vec![Some(&composite_layout)];
        if mode != NativeMaskMode::None {
            group_layouts.push(Some(&self.layouts.empty));
            group_layouts.push(Some(if mode == NativeMaskMode::Chain {
                &self.layouts.mask_chain
            } else {
                &self.layouts.image.mask
            }));
        }
        let composite_pipeline_layout =
            self.device
                .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                    label: Some("ProGPU RGB glyph composite layout"),
                    bind_group_layouts: &group_layouts,
                    immediate_size: 0,
                });
        let names = match mode {
            NativeMaskMode::None => ["fs_rgb_red", "fs_rgb_green", "fs_rgb_blue"],
            NativeMaskMode::Sampled => [
                "fs_rgb_red_masked",
                "fs_rgb_green_masked",
                "fs_rgb_blue_masked",
            ],
            NativeMaskMode::Chain => [
                "fs_rgb_red_chain",
                "fs_rgb_green_chain",
                "fs_rgb_blue_chain",
            ],
        };
        let writes = [
            wgpu::ColorWrites::RED,
            wgpu::ColorWrites::GREEN,
            wgpu::ColorWrites::BLUE,
        ];
        let mut composite = allocate(3)?;
        for (name, mask) in names.into_iter().zip(writes) {
            composite.push(
                self.device
                    .create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                        label: Some(name),
                        layout: Some(&composite_pipeline_layout),
                        vertex: wgpu::VertexState {
                            module: &composite_shader,
                            entry_point: Some("vs_rgb_composite"),
                            compilation_options: Default::default(),
                            buffers: &[],
                        },
                        primitive: Default::default(),
                        depth_stencil: None,
                        multisample: Default::default(),
                        fragment: Some(wgpu::FragmentState {
                            module: &composite_shader,
                            entry_point: Some(name),
                            compilation_options: Default::default(),
                            targets: &[Some(wgpu::ColorTargetState {
                                format: self.format,
                                blend: None,
                                write_mask: mask,
                            })],
                        }),
                        multiview_mask: None,
                        cache: None,
                    }),
            );
        }
        let empty = (mode != NativeMaskMode::None).then(|| {
            self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU RGB glyph empty mask slot"),
                layout: &self.layouts.empty,
                entries: &[],
            })
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let owned = Arc::new(Pipelines {
            raster_layout,
            composite_layout,
            raster,
            composite: composite.try_into().map_err(|_| Error::InvalidInput)?,
            empty,
        });
        self.entries.insert(key, owned.clone());
        Ok(owned)
    }
}
fn texture_entry(binding: u32) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Texture {
            sample_type: wgpu::TextureSampleType::Float { filterable: false },
            view_dimension: wgpu::TextureViewDimension::D2,
            multisampled: false,
        },
        count: None,
    }
}
fn buffer_entry(
    binding: u32,
    visibility: wgpu::ShaderStages,
    storage: bool,
    size: u64,
) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility,
        ty: wgpu::BindingType::Buffer {
            ty: if storage {
                wgpu::BufferBindingType::Storage { read_only: true }
            } else {
                wgpu::BufferBindingType::Uniform
            },
            has_dynamic_offset: false,
            min_binding_size: NonZeroU64::new(size),
        },
        count: None,
    }
}
