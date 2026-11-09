//! Lazy canonical mask pipelines and immutable per-scene mask bindings.
use super::*;
use crate::{
    image::{GpuImage, ImageFormat, ImageSource, Pixels},
    mask::{MaskSampling, MaskUniforms},
    scene::CompiledDrawState,
};
use std::{
    collections::{HashMap, HashSet},
    sync::Weak,
};

struct Upload {
    source: Weak<Pixels>,
    image: GpuImage,
}
pub(super) struct MaskState {
    layout: wgpu::BindGroupLayout,
    pipelines: [Option<wgpu::RenderPipeline>; 3],
    uploads: HashMap<usize, Upload>,
    placeholder: wgpu::TextureView,
    samplers: [wgpu::Sampler; 2],
}
pub(super) struct PreparedState {
    pub binding: Option<wgpu::BindGroup>,
    pub scissor: [u32; 4],
}
pub(super) struct PreparedMasks {
    pub resolved: Vec<CompiledDrawState>,
    pub composition: crate::mask::MaskCompositionMetrics,
    pub states: Vec<PreparedState>,
    pub pipelines: [Option<wgpu::RenderPipeline>; 3],
}

impl MaskState {
    pub(super) fn placeholder(&self) -> wgpu::TextureView {
        self.placeholder.clone()
    }
    pub(super) fn new(device: &wgpu::Device) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU unused analytic-mask texture"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::R8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        Self {
            layout: layout::mask(device),
            pipelines: [None, None, None],
            uploads: HashMap::new(),
            placeholder: texture.create_view(&Default::default()),
            samplers: [wgpu::FilterMode::Nearest, wgpu::FilterMode::Linear].map(|filter| {
                device.create_sampler(&wgpu::SamplerDescriptor {
                    label: Some("ProGPU native coverage sampler"),
                    min_filter: filter,
                    mag_filter: filter,
                    lod_max_clamp: 0.0,
                    ..Default::default()
                })
            }),
        }
    }
    async fn pipeline(
        &mut self,
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        index: usize,
    ) -> Result<(), Error> {
        if self.pipelines[index].is_some() {
            return Ok(());
        }
        let family = [
            layout::Family::Vector,
            layout::Family::Text,
            layout::Family::Image,
        ][index];
        let source = [shaders::VECTOR, shaders::TEXT, shaders::TEXTURE][index];
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU canonical masked draw shader"),
            source: wgpu::ShaderSource::Wgsl(source.into()),
        });
        let pipeline_layout = layout::pipeline(device, family, Some(&self.layout));
        let vector_attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2,3=>Float32,4=>Float32x2,5=>Float32,6=>Float32,7=>Float32];
        let text_attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x2,2=>Float32x2,3=>Float32x4,4=>Float32x4,5=>Float32x4,6=>Float32x4,7=>Float32];
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU ordered four-mask draw"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &module,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &[Some(wgpu::VertexBufferLayout {
                    array_stride: if index == 1 { 96 } else { 56 },
                    step_mode: if index == 1 {
                        wgpu::VertexStepMode::Instance
                    } else {
                        wgpu::VertexStepMode::Vertex
                    },
                    attributes: if index == 1 {
                        &text_attributes
                    } else {
                        &vector_attributes
                    },
                })],
            },
            primitive: Default::default(),
            depth_stencil: None,
            multisample: Default::default(),
            fragment: Some(wgpu::FragmentState {
                module: &module,
                entry_point: Some(if index == 2 {
                    "fs_retained_image_chain"
                } else {
                    "fs_main_chain"
                }),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: Some(if index == 2 {
                        wgpu::BlendState::PREMULTIPLIED_ALPHA_BLENDING
                    } else {
                        wgpu::BlendState::ALPHA_BLENDING
                    }),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        self.pipelines[index] = Some(pipeline);
        Ok(())
    }
    pub(super) fn image(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        source: &ImageSource,
        metrics: &mut UploadMetrics,
        textures: &mut HashSet<(u8, usize)>,
    ) -> Result<GpuImage, Error> {
        let image = match source {
            ImageSource::Gpu(image) => {
                if image.device() != device {
                    return Err(Error::ResourceDomain);
                }
                image.clone()
            }
            ImageSource::Pixels(source) => {
                let pixels = &source.0;
                let key = Arc::as_ptr(pixels) as usize;
                let present = self.uploads.get(&key).is_some_and(|entry| {
                    entry
                        .source
                        .upgrade()
                        .is_some_and(|old| Arc::ptr_eq(&old, pixels))
                });
                if !present {
                    let [width, height] = pixels.size;
                    let format = match pixels.format {
                        ImageFormat::R8 => wgpu::TextureFormat::R8Unorm,
                        ImageFormat::Rgba8 => wgpu::TextureFormat::Rgba8Unorm,
                        ImageFormat::Bgra8 => wgpu::TextureFormat::Bgra8Unorm,
                    };
                    let texture = device.create_texture(&wgpu::TextureDescriptor {
                        label: Some("ProGPU original sampled-mask pixels"),
                        size: wgpu::Extent3d {
                            width,
                            height,
                            depth_or_array_layers: 1,
                        },
                        mip_level_count: 1,
                        sample_count: 1,
                        dimension: wgpu::TextureDimension::D2,
                        format,
                        usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                        view_formats: &[],
                    });
                    let bytes = u64::from(pixels.stride) * u64::from(height - 1)
                        + u64::from(width) * u64::from(pixels.format.bytes_per_pixel());
                    let length = usize::try_from(bytes).map_err(|_| Error::Capacity)?;
                    queue.write_texture(
                        texture.as_image_copy(),
                        &pixels.data[..length],
                        wgpu::TexelCopyBufferLayout {
                            offset: 0,
                            bytes_per_row: Some(pixels.stride),
                            rows_per_image: Some(height),
                        },
                        texture.size(),
                    );
                    metrics.mask_upload_bytes += bytes;
                    self.uploads.insert(
                        key,
                        Upload {
                            source: Arc::downgrade(pixels),
                            image: GpuImage::from_texture(device, &texture, pixels.alpha)?,
                        },
                    );
                }
                self.uploads
                    .get(&key)
                    .ok_or(Error::ResourceDomain)?
                    .image
                    .clone()
            }
        };
        if textures.insert(source.identity()) {
            let bpp = if image.texture().format() == wgpu::TextureFormat::R8Unorm {
                1
            } else {
                4
            };
            metrics.mask_texture_bytes +=
                u64::from(image.size()[0]) * u64::from(image.size()[1]) * bpp;
        }
        Ok(image)
    }
    pub(super) fn binding(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        source: &CompiledDrawState,
        metrics: &mut UploadMetrics,
        textures: &mut HashSet<(u8, usize)>,
    ) -> Result<wgpu::BindGroup, Error> {
        let (view, sampler) = if let Some(coverage) = &source.coverage {
            let image = self.image(device, queue, &coverage.source, metrics, textures)?;
            (
                image.texture().create_view(&Default::default()),
                usize::from(coverage.sampling == MaskSampling::Linear),
            )
        } else {
            (self.placeholder.clone(), 0)
        };
        let primary = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("ProGPU retained primary mask"),
            contents: bytemuck::bytes_of(&source.masks[0]),
            usage: wgpu::BufferUsages::UNIFORM,
        });
        let chain = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("ProGPU retained mask continuation"),
            contents: bytemuck::cast_slice(&source.masks[1..]),
            usage: wgpu::BufferUsages::UNIFORM,
        });
        metrics.uniform_bytes += (4 * size_of::<MaskUniforms>()) as u64;
        metrics.mask_bindings += 1;
        Ok(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU immutable four-mask binding"),
            layout: &self.layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::Sampler(&self.samplers[sampler]),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(&view),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: primary.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: chain.as_entire_binding(),
                },
            ],
        }))
    }
}
impl Renderer {
    /// Release cached coverage textures whose original immutable sources died.
    /// Previously prepared scenes retain their own bindings and remain valid.
    pub fn trim_mask_cache(&mut self) {
        if let Some(state) = &mut self.masks {
            state
                .uploads
                .retain(|_, entry| entry.source.strong_count() != 0);
        }
    }
    pub(super) fn validate_masks(&self, compiled: &CompiledScene) -> Result<(), Error> {
        self.validate_mask_states(&compiled.states)
    }
    pub(super) fn validate_mask_states(&self, states: &[CompiledDrawState]) -> Result<(), Error> {
        for state in states {
            for component in state.components.iter() {
                let source = match &component.mask {
                    crate::mask::Mask::Coverage(c) => ImageSource::Pixels(c.image.clone()),
                    crate::mask::Mask::Texture(c) => c.source.clone(),
                    _ => continue,
                };
                if source
                    .size()
                    .iter()
                    .any(|v| *v > self.device.limits().max_texture_dimension_2d)
                {
                    return Err(Error::Capacity);
                }
                if let ImageSource::Gpu(source) = &source
                    && source.device() != &self.device
                {
                    return Err(Error::ResourceDomain);
                }
            }
        }
        Ok(())
    }
    pub(super) async fn prepare_masks(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        self.trim_mask_cache();
        if compiled.states.len() == 1 && compiled.states[0].clip.is_none() {
            return Ok(());
        }
        let mut required = [false; 3];
        let mut current = 0;
        for draw in &compiled.draws {
            if let DrawRange::State(index) = draw {
                current = *index;
                continue;
            }
            if matches!(
                draw,
                DrawRange::Advanced(_)
                    | DrawRange::Clear(_)
                    | DrawRange::Axis(_)
                    | DrawRange::ImageEffect(_)
                    | DrawRange::ShaderEffect(_)
            ) {
                continue;
            }
            if compiled.states[current].count != 0 {
                required[match draw {
                    DrawRange::Vector(_) => 0,
                    DrawRange::Text(_) => 1,
                    DrawRange::Image(..) | DrawRange::Advanced(_) => 2,
                    _ => unreachable!(),
                }] = true;
            }
        }
        let needs_masks = compiled.states.iter().any(|s| s.count != 0);
        let mut cache = if needs_masks {
            Some(
                self.masks
                    .take()
                    .unwrap_or_else(|| MaskState::new(&self.device)),
            )
        } else {
            None
        };
        if let Some(cache) = &mut cache {
            for (index, required) in required.into_iter().enumerate() {
                if required {
                    cache.pipeline(&self.device, self.format, index).await?;
                }
            }
        }
        let (resolved, composition) = if let Some(cache) = &mut cache {
            self.resolve_mask_states(compiled, frame, cache, &mut prepared.metrics)
                .await?
        } else {
            (compiled.states.clone(), Default::default())
        };
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let result = (|| {
            let mut states = Vec::new();
            states
                .try_reserve_exact(compiled.states.len())
                .map_err(|_| Error::Capacity)?;
            let mut textures = HashSet::new();
            let mut bindings =
                HashMap::<([[u32; 24]; 4], Option<((u8, usize), bool)>), wgpu::BindGroup>::new();
            for source in &resolved {
                let [ox, oy] = prepared.origin;
                let [left, top, right, bottom] = source.clip.unwrap_or([
                    ox,
                    oy,
                    ox + prepared.frame.width,
                    oy + prepared.frame.height,
                ]);
                let left = left.saturating_sub(ox).min(prepared.frame.width);
                let top = top.saturating_sub(oy).min(prepared.frame.height);
                let scissor = [
                    left,
                    top,
                    right
                        .saturating_sub(ox)
                        .min(prepared.frame.width)
                        .saturating_sub(left),
                    bottom
                        .saturating_sub(oy)
                        .min(prepared.frame.height)
                        .saturating_sub(top),
                ];
                let binding = if source.count != 0 {
                    // Exact original uniform bits and actual retained source;
                    // clip-only transitions share one immutable mask binding.
                    let key = (
                        source.masks.map(bytemuck::cast),
                        source
                            .coverage
                            .as_ref()
                            .map(|c| (c.source.identity(), c.sampling == MaskSampling::Linear)),
                    );
                    if let Some(binding) = bindings.get(&key) {
                        Some(binding.clone())
                    } else {
                        let binding = cache.as_mut().ok_or(Error::ResourceDomain)?.binding(
                            &self.device,
                            &self.queue,
                            source,
                            &mut prepared.metrics,
                            &mut textures,
                        )?;
                        bindings.insert(key, binding.clone());
                        Some(binding)
                    }
                } else {
                    None
                };
                states.push(PreparedState { binding, scissor });
            }
            Ok::<_, Error>(PreparedMasks {
                resolved,
                composition,
                states,
                pipelines: cache
                    .as_ref()
                    .map_or([None, None, None], |c| c.pipelines.clone()),
            })
        })();
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        prepared.masks = Some(result?);
        if let Some(cache) = cache {
            self.masks = Some(cache);
        }
        Ok(())
    }
}
