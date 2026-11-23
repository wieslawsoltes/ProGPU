//! Original static atlas and retained analytic glyph GPU families.
use super::*;
use crate::{
    mask::MaskUniforms,
    text::paint::{GlyphInstance, GpuTextStyle},
};

#[derive(Clone, Copy, PartialEq, Eq, Hash)]
struct Key {
    format: wgpu::TextureFormat,
    samples: u32,
    blend: LayerBlend,
    retained: bool,
}
struct TextPipeline {
    pipeline: wgpu::RenderPipeline,
    frame: wgpu::BindGroupLayout,
    atlas: Option<wgpu::BindGroupLayout>,
    mask: wgpu::BindGroupLayout,
}
#[derive(Default)]
pub(super) struct TextEngine {
    pipelines: HashMap<Key, Arc<TextPipeline>>,
}
pub(super) struct UploadedText {
    records: Option<wgpu::Buffer>,
    segments: Option<wgpu::Buffer>,
    retained: Option<wgpu::Buffer>,
    instances: Option<wgpu::Buffer>,
    styles: Option<wgpu::Buffer>,
}
impl UploadedText {
    pub fn bytes(&self) -> u64 {
        [
            &self.records,
            &self.segments,
            &self.retained,
            &self.instances,
            &self.styles,
        ]
        .into_iter()
        .map(|b| b.as_ref().map_or(0, |b| b.size()))
        .sum()
    }
}
struct Bindings {
    pipeline: Arc<TextPipeline>,
    frame: wgpu::BindGroup,
    atlas: Option<wgpu::BindGroup>,
    mask: wgpu::BindGroup,
}
pub(super) struct PreparedText {
    atlas: Option<Bindings>,
    retained: Option<Bindings>,
    _mask_uniform: wgpu::Buffer,
}
impl PreparedText {
    pub fn mask_uniform_bytes(&self) -> u64 {
        self._mask_uniform.size()
    }
    pub fn draw_atlas(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        uploaded: &UploadedText,
        range: Range<u32>,
    ) -> Result<(), Error> {
        let bindings = self.atlas.as_ref().ok_or(Error::ResourceDomain)?;
        pass.set_pipeline(&bindings.pipeline.pipeline);
        pass.set_bind_group(0, &bindings.frame, &[]);
        pass.set_bind_group(
            1,
            bindings.atlas.as_ref().ok_or(Error::ResourceDomain)?,
            &[],
        );
        pass.set_bind_group(2, &bindings.mask, &[]);
        pass.set_vertex_buffer(
            0,
            uploaded
                .instances
                .as_ref()
                .ok_or(Error::ResourceDomain)?
                .slice(..),
        );
        pass.draw(0..6, range);
        Ok(())
    }
    pub fn draw_retained(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        count: u32,
    ) -> Result<bool, Error> {
        if count == 0 {
            return Ok(false);
        }
        let bindings = self.retained.as_ref().ok_or(Error::ResourceDomain)?;
        pass.set_pipeline(&bindings.pipeline.pipeline);
        pass.set_bind_group(0, &bindings.frame, &[]);
        pass.set_bind_group(1, &bindings.mask, &[]);
        pass.draw(0..6, 0..count);
        Ok(true)
    }
}
pub(super) fn texture_bytes(source: &StaticDxfBuffer) -> u64 {
    source.0.glyph_atlas.as_ref().map_or(0, |owner| {
        let a = owner.coverage.size();
        let b = owner.color.size();
        u64::from(a[0]) * u64::from(a[1]) + 4 * u64::from(b[0]) * u64::from(b[1])
    })
}
pub(super) fn preflight(device: &wgpu::Device, source: &StaticDxfBuffer) -> Result<u64, Error> {
    let retained = source.retained_glyphs();
    let mut sizes = vec![
        bytes::<crate::vector::GpuPathRecord>(retained.records().len())?,
        bytes::<crate::path::Segment>(retained.segments().len())?,
        bytes::<StaticRetainedGlyphInstance>(retained.instances().len())?,
    ];
    if source.text_instance_count() != 0 {
        let owner = source.0.glyph_atlas.as_ref().ok_or(Error::ResourceDomain)?;
        if &owner.device != device || owner.residency.is_empty() {
            return Err(Error::ResourceDomain);
        }
        sizes.push(size_of::<GpuTextStyle>() as u64);
    }
    if let Some(owner) = &source.0.glyph_atlas {
        if &owner.device != device {
            return Err(Error::ResourceDomain);
        }
    }
    let limit = device.limits();
    if sizes.iter().any(|v| {
        *v > u64::from(limit.max_storage_buffer_binding_size) || *v > limit.max_buffer_size
    }) {
        return Err(Error::Capacity);
    }
    let vertices = bytes::<GlyphInstance>(source.text_instance_count())?;
    if vertices > limit.max_buffer_size {
        return Err(Error::Capacity);
    }
    u32::try_from(retained.instances().len()).map_err(|_| Error::Capacity)?;
    for record in retained.records() {
        if record
            .start_segment
            .checked_add(record.segment_count)
            .is_none_or(|v| v as usize > retained.segments().len())
        {
            return Err(Error::ResourceDomain);
        }
    }
    for instance in retained.instances() {
        let record = instance.metadata[0];
        if !record.is_finite()
            || record < 0.0
            || record.fract() != 0.0
            || record as usize >= retained.records().len()
        {
            return Err(Error::ResourceDomain);
        }
    }
    sizes.into_iter().try_fold(
        vertices
            .checked_add(texture_bytes(source))
            .ok_or(Error::Capacity)?,
        |sum, v| sum.checked_add(v).ok_or(Error::Capacity),
    )
}
pub(super) fn upload(device: &wgpu::Device, source: &StaticDxfBuffer) -> Option<UploadedText> {
    let retained = source.retained_glyphs();
    let has_retained = !retained.is_empty();
    let has_atlas = source.text_instance_count() != 0;
    if !has_retained && !has_atlas {
        return None;
    }
    Some(UploadedText {
        records: has_retained.then(|| {
            buffer(
                device,
                "ProGPU static glyph paths",
                retained.records(),
                wgpu::BufferUsages::STORAGE,
            )
        }),
        segments: has_retained.then(|| {
            buffer(
                device,
                "ProGPU static glyph edges",
                retained.segments(),
                wgpu::BufferUsages::STORAGE,
            )
        }),
        retained: has_retained.then(|| {
            buffer(
                device,
                "ProGPU static analytic glyph instances",
                retained.instances(),
                wgpu::BufferUsages::STORAGE,
            )
        }),
        instances: has_atlas.then(|| {
            buffer(
                device,
                "ProGPU static atlas glyph instances",
                &source.0.compiled.glyphs,
                wgpu::BufferUsages::VERTEX,
            )
        }),
        styles: has_atlas.then(|| {
            buffer(
                device,
                "ProGPU original static legacy text style",
                &[GpuTextStyle::zeroed()],
                wgpu::BufferUsages::STORAGE,
            )
        }),
    })
}
impl TextEngine {
    async fn pipeline(
        &mut self,
        device: &wgpu::Device,
        target: &StaticDxfTarget,
        retained: bool,
    ) -> Result<Arc<TextPipeline>, Error> {
        let key = Key {
            format: target.frame.format,
            samples: target.frame.sample_count,
            blend: target.blend.managed_vector_effective_mode(),
            retained,
        };
        if let Some(pipeline) = self.pipelines.get(&key) {
            return Ok(pipeline.clone());
        }
        self.pipelines.try_reserve(1).map_err(|_| Error::Capacity)?;
        let pipeline = Arc::new(TextPipeline::new(device, key).await?);
        self.pipelines.insert(key, pipeline.clone());
        Ok(pipeline)
    }
    pub(super) async fn prepare(
        &mut self,
        device: &wgpu::Device,
        source: &StaticDxfBuffer,
        uploaded: &UploadedText,
        target: &StaticDxfTarget,
        uniform: &wgpu::Buffer,
        white: &wgpu::Texture,
        sampler: &wgpu::Sampler,
    ) -> Result<PreparedText, Error> {
        let atlas_pipeline = if source.text_instance_count() != 0 {
            Some(self.pipeline(device, target, false).await?)
        } else {
            None
        };
        let retained_pipeline = if !source.retained_glyphs().is_empty() {
            Some(self.pipeline(device, target, true).await?)
        } else {
            None
        };
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let mask_uniform = buffer(
            device,
            "ProGPU static text actual outer mask",
            &[target
                .mask
                .as_ref()
                .map_or_else(MaskUniforms::zeroed, |m| *m.uniforms())],
            wgpu::BufferUsages::UNIFORM,
        );
        let mask_view = target
            .mask
            .as_ref()
            .and_then(|m| m.image())
            .map_or(white, |i| i.texture())
            .create_view(&Default::default());
        let filter = if target
            .mask
            .as_ref()
            .is_some_and(|m| m.sampling() == ImageSampling::Nearest)
        {
            wgpu::FilterMode::Nearest
        } else {
            wgpu::FilterMode::Linear
        };
        let mask_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("ProGPU static text outer mask sampler"),
            mag_filter: filter,
            min_filter: filter,
            ..Default::default()
        });
        let bind_mask = |layout: &wgpu::BindGroupLayout| {
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU static text resolved mask"),
                layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(&mask_sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&mask_view),
                    },
                    entry(2, &mask_uniform),
                ],
            })
        };
        let atlas = if let Some(pipeline) = atlas_pipeline {
            let atlas = &source.0.glyph_atlas.as_ref().ok_or(Error::ResourceDomain)?;
            let gray = atlas.coverage.texture().create_view(&Default::default());
            let color = atlas.color.texture().create_view(&Default::default());
            let frame = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU static legacy text frame"),
                layout: &pipeline.frame,
                entries: &[
                    entry(0, uniform),
                    entry(1, uploaded.styles.as_ref().ok_or(Error::ResourceDomain)?),
                ],
            });
            let coverage = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU static immutable glyph atlas"),
                layout: pipeline.atlas.as_ref().ok_or(Error::ResourceDomain)?,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&gray),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: wgpu::BindingResource::TextureView(&color),
                    },
                ],
            });
            Some(Bindings {
                mask: bind_mask(&pipeline.mask),
                pipeline,
                frame,
                atlas: Some(coverage),
            })
        } else {
            None
        };
        let retained = if let Some(pipeline) = retained_pipeline {
            let frame = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU static retained analytic glyph frame"),
                layout: &pipeline.frame,
                entries: &[
                    entry(0, uniform),
                    entry(1, uploaded.records.as_ref().ok_or(Error::ResourceDomain)?),
                    entry(2, uploaded.segments.as_ref().ok_or(Error::ResourceDomain)?),
                    entry(3, uploaded.retained.as_ref().ok_or(Error::ResourceDomain)?),
                ],
            });
            Some(Bindings {
                mask: bind_mask(&pipeline.mask),
                pipeline,
                frame,
                atlas: None,
            })
        } else {
            None
        };
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(PreparedText {
            atlas,
            retained,
            _mask_uniform: mask_uniform,
        })
    }
}
fn buffer_layout(
    binding: u32,
    size: u64,
    storage: bool,
    visibility: wgpu::ShaderStages,
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
            min_binding_size: std::num::NonZeroU64::new(size),
        },
        count: None,
    }
}
fn sampler_layout() -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding: 0,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
        count: None,
    }
}
fn texture_layout(binding: u32) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Texture {
            sample_type: wgpu::TextureSampleType::Float { filterable: true },
            view_dimension: wgpu::TextureViewDimension::D2,
            multisampled: false,
        },
        count: None,
    }
}
impl TextPipeline {
    async fn new(device: &wgpu::Device, key: Key) -> Result<Self, Error> {
        use wgpu::ShaderStages as S;
        let blend = if key.retained {
            key.blend.managed_draw_state(true)?
        } else {
            key.blend.straight_draw_state()?
        };
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU original static text shader"),
            source: wgpu::ShaderSource::Wgsl(
                if key.retained {
                    crate::shaders::RETAINED_GLYPH
                } else {
                    crate::shaders::TEXT
                }
                .into(),
            ),
        });
        let entries = if key.retained {
            vec![
                buffer_layout(0, 208, false, S::VERTEX_FRAGMENT),
                buffer_layout(1, 32, true, S::FRAGMENT),
                buffer_layout(2, 48, true, S::FRAGMENT),
                buffer_layout(3, 112, true, S::VERTEX),
            ]
        } else {
            vec![
                buffer_layout(0, 224, false, S::VERTEX_FRAGMENT),
                buffer_layout(1, 32, true, S::VERTEX),
            ]
        };
        let frame = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU static text storage layout"),
            entries: &entries,
        });
        let atlas = (!key.retained).then(|| {
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("ProGPU static glyph atlas layout"),
                entries: &[sampler_layout(), texture_layout(1), texture_layout(2)],
            })
        });
        let mask = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU static text mask layout"),
            entries: &[
                sampler_layout(),
                texture_layout(1),
                buffer_layout(2, 96, false, S::FRAGMENT),
            ],
        });
        let mut layouts = vec![Some(&frame)];
        if let Some(atlas) = &atlas {
            layouts.push(Some(atlas));
        }
        layouts.push(Some(&mask));
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU static original text pipeline layout"),
            bind_group_layouts: &layouts,
            immediate_size: 0,
        });
        let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x2,2=>Float32x2,3=>Float32x4,4=>Float32x4,5=>Float32x4,6=>Float32x4,7=>Float32];
        let vertices = [Some(wgpu::VertexBufferLayout {
            array_stride: 96,
            step_mode: wgpu::VertexStepMode::Instance,
            attributes: &attributes,
        })];
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU original static glyph rendering"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: if key.retained { &[] } else { &vertices },
            },
            primitive: Default::default(),
            depth_stencil: None,
            multisample: wgpu::MultisampleState {
                count: key.samples,
                ..Default::default()
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some(if !key.retained && key.blend.requires_premultiplied() {
                    "fs_main_premultiplied"
                } else {
                    "fs_main"
                }),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format: key.format,
                    blend: Some(blend),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self {
            pipeline,
            frame,
            atlas,
            mask,
        })
    }
}
