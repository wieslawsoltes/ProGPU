//! Original retained image shader execution and GPU-only linear mip generation.
use super::*;
use crate::image::{
    GpuImage, Image, ImageAddress, ImageFormat, ImageSampling, ImageSamplingPath,
    ImageSamplingPreference, ImageSource, Pixels,
};
use crate::scene::LayerBlend;
use std::{
    collections::{HashMap, HashSet},
    sync::Weak,
};

#[derive(Clone, Copy, PartialEq, Eq, Hash)]
struct SamplerKey {
    mode: ImageSampling,
    anisotropy: u8,
    address: [ImageAddress; 2],
}
struct Upload {
    source: Weak<Pixels>,
    image: GpuImage,
}
pub(super) struct ImageState {
    pipeline: wgpu::RenderPipeline,
    uploads: HashMap<(usize, bool), Upload>,
    samplers: HashMap<SamplerKey, wgpu::Sampler>,
    mips: HashMap<wgpu::TextureFormat, wgpu::RenderPipeline>,
    composites: HashMap<(LayerBlend, bool), wgpu::RenderPipeline>,
}
pub(super) struct PreparedImages {
    pub pipeline: wgpu::RenderPipeline,
    pub composites: HashMap<(LayerBlend, bool), wgpu::RenderPipeline>,
    pub vertices: wgpu::Buffer,
    pub indices: wgpu::Buffer,
    pub uniforms: wgpu::BindGroup,
    pub bindings: Vec<wgpu::BindGroup>,
}

pub(super) async fn pipeline(
    device: &wgpu::Device,
    format: wgpu::TextureFormat,
    mip: bool,
    blend: LayerBlend,
    masked: bool,
) -> Result<wgpu::RenderPipeline, Error> {
    pipeline_entry(device, format, mip, blend, masked, None).await
}
pub(super) async fn pipeline_entry(
    device: &wgpu::Device,
    format: wgpu::TextureFormat,
    mip: bool,
    blend: LayerBlend,
    masked: bool,
    entry: Option<&'static str>,
) -> Result<wgpu::RenderPipeline, Error> {
    let blend_state = if mip || entry == Some("fs_layer_coverage") {
        None
    } else {
        Some(blend.fixed()?)
    };
    let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
    let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some(if mip {
            "ProGPU canonical MipGenerator"
        } else {
            "ProGPU canonical Texture"
        }),
        source: wgpu::ShaderSource::Wgsl(
            if mip {
                shaders::MIP_GENERATOR
            } else {
                shaders::TEXTURE
            }
            .into(),
        ),
    });
    let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2,3=>Float32,4=>Float32x2,5=>Float32,6=>Float32,7=>Float32];
    let layout = Some(wgpu::VertexBufferLayout {
        array_stride: size_of::<VectorVertex>() as u64,
        step_mode: wgpu::VertexStepMode::Vertex,
        attributes: &attributes,
    });
    let buffers = [layout];
    let mask_layout = masked.then(|| super::layout::mask(device));
    let pipeline_layout = (!mip).then(|| {
        super::layout::pipeline(device, super::layout::Family::Image, mask_layout.as_ref())
    });
    let result = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some(if mip {
            "ProGPU linear mip generation"
        } else {
            "ProGPU retained image"
        }),
        layout: pipeline_layout.as_ref(),
        vertex: wgpu::VertexState {
            module: &module,
            entry_point: Some("vs_main"),
            compilation_options: Default::default(),
            buffers: if mip { &[] } else { &buffers },
        },
        primitive: wgpu::PrimitiveState::default(),
        depth_stencil: None,
        multisample: wgpu::MultisampleState::default(),
        fragment: Some(wgpu::FragmentState {
            module: &module,
            entry_point: Some(entry.unwrap_or(if mip {
                "fs_main"
            } else if masked {
                "fs_retained_image_chain"
            } else {
                "fs_retained_image_unmasked"
            })),
            compilation_options: Default::default(),
            targets: &[Some(wgpu::ColorTargetState {
                format,
                blend: blend_state,
                write_mask: if entry == Some("fs_layer_coverage") {
                    wgpu::ColorWrites::RED
                } else {
                    wgpu::ColorWrites::ALL
                },
            })],
        }),
        multiview_mask: None,
        cache: None,
    });
    if let Some(error) = scope.pop().await {
        return Err(error.into());
    }
    Ok(result)
}
impl ImageState {
    async fn new(device: &wgpu::Device, format: wgpu::TextureFormat) -> Result<Self, Error> {
        Ok(Self {
            pipeline: pipeline(device, format, false, LayerBlend::SrcOver, false).await?,
            uploads: HashMap::new(),
            samplers: HashMap::new(),
            mips: HashMap::new(),
            composites: HashMap::new(),
        })
    }
    fn prune(&mut self) {
        self.uploads
            .retain(|_, entry| entry.source.strong_count() != 0);
    }
    fn sampler(&mut self, device: &wgpu::Device, key: SamplerKey) -> &wgpu::Sampler {
        self.samplers.entry(key).or_insert_with(|| {
            let address = |mode| match mode {
                ImageAddress::Clamp => wgpu::AddressMode::ClampToEdge,
                ImageAddress::Repeat => wgpu::AddressMode::Repeat,
                ImageAddress::MirrorRepeat => wgpu::AddressMode::MirrorRepeat,
            };
            let filter = |linear| {
                if linear {
                    wgpu::FilterMode::Linear
                } else {
                    wgpu::FilterMode::Nearest
                }
            };
            let [mag, min, mip] = key.mode.filters();
            device.create_sampler(&wgpu::SamplerDescriptor {
                label: Some("ProGPU retained image sampler"),
                address_mode_u: address(key.address[0]),
                address_mode_v: address(key.address[1]),
                mag_filter: filter(mag),
                min_filter: filter(min),
                mipmap_filter: if mip {
                    wgpu::MipmapFilterMode::Linear
                } else {
                    wgpu::MipmapFilterMode::Nearest
                },
                lod_min_clamp: 0.0,
                lod_max_clamp: if key.mode.uses_mipmaps() || key.address != [ImageAddress::Clamp; 2]
                {
                    32.0
                } else {
                    0.0
                },
                anisotropy_clamp: u16::from(key.anisotropy),
                ..Default::default()
            })
        })
    }
    async fn upload(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        source: &Image,
        mips: bool,
    ) -> Result<(GpuImage, u64, u32), Error> {
        let key = (Arc::as_ptr(&source.0) as usize, mips);
        if let Some(cached) = self.uploads.get(&key)
            && cached
                .source
                .upgrade()
                .is_some_and(|owner| Arc::ptr_eq(&owner, &source.0))
        {
            return Ok((cached.image.clone(), 0, 0));
        }
        let [width, height] = source.size();
        if width > device.limits().max_texture_dimension_2d
            || height > device.limits().max_texture_dimension_2d
        {
            return Err(Error::Capacity);
        }
        let format = match source.format() {
            ImageFormat::Rgba8 => wgpu::TextureFormat::Rgba8Unorm,
            ImageFormat::Bgra8 => wgpu::TextureFormat::Bgra8Unorm,
            ImageFormat::R8 => wgpu::TextureFormat::R8Unorm,
        };
        let levels = if mips {
            32 - width.max(height).leading_zeros()
        } else {
            1
        };
        if levels > 1 && !self.mips.contains_key(&format) {
            self.mips.insert(
                format,
                pipeline(device, format, true, LayerBlend::SrcOver, false).await?,
            );
        }
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU immutable image generation"),
            size: wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
            mip_level_count: levels,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format,
            usage: wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_DST
                | if levels > 1 {
                    wgpu::TextureUsages::RENDER_ATTACHMENT
                } else {
                    wgpu::TextureUsages::empty()
                },
            view_formats: &[],
        });
        let bytes = u64::from(source.stride()) * u64::from(height - 1)
            + u64::from(width) * u64::from(source.format().bytes_per_pixel());
        queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            &source.pixels()[..usize::try_from(bytes).map_err(|_| Error::Capacity)?],
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(source.stride()),
                rows_per_image: Some(height),
            },
            texture.size(),
        );
        if levels > 1 {
            let pipeline = self.mips.get(&format).ok_or(Error::UnsupportedExecution)?;
            let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
                label: Some("ProGPU linear mip sampler"),
                min_filter: wgpu::FilterMode::Linear,
                mag_filter: wgpu::FilterMode::Linear,
                ..Default::default()
            });
            let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU original image mip chain"),
            });
            for level in 1..levels {
                let view = |level| {
                    texture.create_view(&wgpu::TextureViewDescriptor {
                        base_mip_level: level,
                        mip_level_count: Some(1),
                        ..Default::default()
                    })
                };
                let source = view(level - 1);
                let destination = view(level);
                let bindings = device.create_bind_group(&wgpu::BindGroupDescriptor {
                    label: Some("ProGPU previous mip sampling"),
                    layout: &pipeline.get_bind_group_layout(0),
                    entries: &[
                        wgpu::BindGroupEntry {
                            binding: 0,
                            resource: wgpu::BindingResource::Sampler(&sampler),
                        },
                        wgpu::BindGroupEntry {
                            binding: 1,
                            resource: wgpu::BindingResource::TextureView(&source),
                        },
                    ],
                });
                let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                    label: Some("ProGPU linear mip pass"),
                    color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                        view: &destination,
                        depth_slice: None,
                        resolve_target: None,
                        ops: wgpu::Operations {
                            load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                            store: wgpu::StoreOp::Store,
                        },
                    })],
                    ..Default::default()
                });
                pass.set_pipeline(pipeline);
                pass.set_bind_group(0, &bindings, &[]);
                pass.draw(0..3, 0..1);
            }
            queue.submit([encoder.finish()]);
        }
        let image = GpuImage::from_texture(device, &texture, source.alpha())?;
        self.uploads.insert(
            key,
            Upload {
                source: Arc::downgrade(&source.0),
                image: image.clone(),
            },
        );
        Ok((image, bytes, levels - 1))
    }
}

impl Renderer {
    /// Select original image sampling using the actual adapter receipt. Call
    /// before preparing images; missing adapter policy fails explicitly.
    pub fn configure_image_sampling(
        &mut self,
        preference: ImageSamplingPreference,
        adapter: &wgpu::AdapterInfo,
    ) -> Result<ImageSamplingPath, Error> {
        let path = preference.resolve(adapter.backend == wgpu::Backend::Dx12, &adapter.name)?;
        self.image_sampling = Some(path);
        self.image_preference = preference;
        Ok(path)
    }
    /// Actual selected image sampling route, absent until adapter configuration.
    pub fn image_sampling_path(&self) -> Option<ImageSamplingPath> {
        self.image_sampling
    }
    /// Retire cached uploads whose immutable CPU source owner is no longer live.
    /// Prepared bindings and submitted frames retain their own actual GPU handles.
    pub fn trim_image_cache(&mut self) {
        if let Some(state) = &mut self.images {
            state.prune();
        }
    }

    pub(super) async fn prepare_images(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        self.trim_image_cache();
        if compiled.images.is_empty() {
            return Ok(());
        }
        if self.image_sampling != Some(compiled.image_sampling) {
            return Err(Error::UnsupportedExecution);
        }
        if compiled.has_tile_images
            && self.image_preference == ImageSamplingPreference::NativeSampler
        {
            return Err(Error::UnsupportedExecution);
        }
        let uniforms = self.validate_geometry(&compiled.image_mesh, frame)?;
        let mut state = match self.images.take() {
            Some(state) => state,
            None => ImageState::new(&self.device, self.format).await?,
        };
        let mut needed = HashSet::new();
        let mut mask_index = 0;
        for draw in &compiled.draws {
            match draw {
                DrawRange::State(index) => mask_index = *index,
                DrawRange::Image(_, _, blend) if *blend != LayerBlend::SrcOver => {
                    needed.insert((*blend, compiled.states[mask_index].count != 0));
                }
                _ => {}
            }
        }
        for key in needed {
            if let std::collections::hash_map::Entry::Vacant(entry) = state.composites.entry(key) {
                entry.insert(pipeline(&self.device, self.format, false, key.0, key.1).await?);
            }
        }
        // Failure/cancellation drops this exclusive candidate. Prepared scenes
        // retain their original pipeline/layouts and actual textures independently.
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let result = Self::populate_images(
            &self.device,
            &self.queue,
            &mut state,
            compiled,
            uniforms,
            prepared,
        )
        .await;
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        result?;
        self.images = Some(state);
        Ok(())
    }

    async fn populate_images(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        state: &mut ImageState,
        compiled: &CompiledScene,
        uniforms: Uniforms,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        let mut bindings = Vec::new();
        bindings
            .try_reserve_exact(compiled.images.len())
            .map_err(|_| Error::Capacity)?;
        let mut textures = HashSet::new();
        for binding in &compiled.images {
            let image = match &binding.image {
                ImageSource::Pixels(source) => {
                    let (image, bytes, passes) = state
                        .upload(device, queue, source, binding.key.sampling.uses_mipmaps())
                        .await?;
                    prepared.metrics.image_upload_bytes += bytes;
                    prepared.metrics.image_mip_passes += passes;
                    image
                }
                ImageSource::Gpu(source) => {
                    if source.device() != device {
                        return Err(Error::ResourceDomain);
                    }
                    source.clone()
                }
            };
            if textures.insert(image.identity()) {
                let mut size = image.size();
                let bpp = if image.texture().format() == wgpu::TextureFormat::R8Unorm {
                    1
                } else {
                    4
                };
                for _ in 0..image.mip_level_count() {
                    prepared.metrics.image_texture_bytes +=
                        u64::from(size[0]) * u64::from(size[1]) * bpp;
                    size = size.map(|v| (v / 2).max(1));
                }
            }
            let view = image.texture().create_view(&Default::default());
            let layout = state.pipeline.get_bind_group_layout(1);
            let sampler = state.sampler(
                device,
                SamplerKey {
                    mode: binding.key.sampling,
                    anisotropy: binding.key.anisotropy,
                    address: binding.key.address,
                },
            );
            bindings.push(device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU retained image source"),
                layout: &layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&view),
                    },
                ],
            }));
        }
        let buffer = |label, contents: &[u8], usage| {
            device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some(label),
                contents,
                usage,
            })
        };
        let vertices = buffer(
            "ProGPU immutable image vertices",
            bytemuck::cast_slice(compiled.image_mesh.vertices()),
            wgpu::BufferUsages::VERTEX,
        );
        let indices = buffer(
            "ProGPU immutable image indices",
            bytemuck::cast_slice(compiled.image_mesh.indices()),
            wgpu::BufferUsages::INDEX,
        );
        let frame_buffer = buffer(
            "ProGPU immutable image frame",
            bytemuck::bytes_of(&uniforms),
            wgpu::BufferUsages::UNIFORM,
        );
        let uniforms = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU retained image frame"),
            layout: &state.pipeline.get_bind_group_layout(0),
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: frame_buffer.as_entire_binding(),
            }],
        });
        prepared.metrics.geometry_bytes += vertices.size() + indices.size();
        prepared.metrics.uniform_bytes += size_of::<Uniforms>() as u64;
        prepared.images = Some(PreparedImages {
            pipeline: state.pipeline.clone(),
            composites: state.composites.clone(),
            vertices,
            indices,
            uniforms,
            bindings,
        });
        Ok(())
    }
}
