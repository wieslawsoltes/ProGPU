//! Original source acquisition, white fallback, Gaussian preparation and sampling.
use super::*;
use crate::image_effect::YuvConversion;
#[derive(Clone, PartialEq, Eq, Hash)]
struct BlurKey {
    textures: [wgpu::Texture; 2],
    generations: [u32; 5],
    sigma: u32,
    conversion: Option<[u32; 16]>,
}
pub(super) struct Material {
    pub state: MeshMaterialState,
    pub binding: wgpu::BindGroup,
    pub unfilterable: bool,
}
pub(super) struct Materials {
    pub values: Vec<Material>,
    pub leases: Vec<MeshTextureLease>,
    pub blurs: Vec<Arc<PreparedImageBlur>>,
}
impl Mesh3DEngine {
    pub(super) fn ensure_white(&mut self) {
        if self.white.is_some() {
            return;
        }
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU canonical white Mesh3D fallback"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        self.queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            &[255; 4],
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(4),
                rows_per_image: Some(1),
            },
            wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
        );
        self.white = Some(texture);
    }
    fn sampler(&mut self, mode: ImageSampling) -> wgpu::Sampler {
        self.samplers
            .entry(mode)
            .or_insert_with(|| {
                let [mag, min, mip] = if matches!(
                    mode,
                    ImageSampling::Linear | ImageSampling::Cubic | ImageSampling::Fant
                ) {
                    [true, true, true]
                } else {
                    mode.filters()
                };
                let filter = |v| {
                    if v {
                        wgpu::FilterMode::Linear
                    } else {
                        wgpu::FilterMode::Nearest
                    }
                };
                self.device.create_sampler(&wgpu::SamplerDescriptor {
                    label: Some("ProGPU original Mesh3D source sampler"),
                    mag_filter: filter(mag),
                    min_filter: filter(min),
                    mipmap_filter: if mip {
                        wgpu::MipmapFilterMode::Linear
                    } else {
                        wgpu::MipmapFilterMode::Nearest
                    },
                    lod_min_clamp: 0.0,
                    lod_max_clamp: if mode.uses_mipmaps() { 32.0 } else { 1.0 },
                    ..Default::default()
                })
            })
            .clone()
    }
    pub(super) async fn prepare_materials(
        &mut self,
        request: &Mesh3DRequest,
        plan: &Mesh3DResourcePlan,
        metrics: &mut Mesh3DMetrics,
    ) -> Result<Materials, Error> {
        let mut result = Materials {
            values: Vec::new(),
            leases: Vec::new(),
            blurs: Vec::new(),
        };
        result
            .values
            .try_reserve_exact(request.viewport.meshes.len())
            .map_err(|_| Error::Capacity)?;
        result
            .leases
            .try_reserve_exact(request.viewport.meshes.len())
            .map_err(|_| Error::Capacity)?;
        let mut prepared = HashMap::<BlurKey, Arc<PreparedImageBlur>>::new();
        for (index, mesh) in request.viewport.meshes.iter().enumerate() {
            let admission = plan.materials.get(index).ok_or(Error::ResourceDomain)?;
            let planar = admission.request == MeshTextureRequest::Planar;
            let acquired = match &mesh.texture_source {
                Some(source) => source.acquire(
                    &self.device,
                    if planar {
                        MeshTextureRequest::Planar
                    } else {
                        MeshTextureRequest::Rgb
                    },
                )?,
                None => None,
            };
            // The original false/wrong-device source result retires the actual
            // returned lease and selects white. It never tries a different source.
            let lease = acquired
                .filter(|v| v.device() == &self.device && (!planar || v.chroma().is_some()));
            let has_texture = lease.is_some();
            let mut source = self.white.as_ref().ok_or(Error::ResourceDomain)?.clone();
            let mut chroma = source.clone();
            let mut alpha = ImageAlpha::Straight;
            let mut has_yuv = false;
            let mut blurred = false;
            let mut unfilterable = false;
            if let Some(lease) = lease {
                source = lease.luma().clone();
                chroma = if planar {
                    lease.chroma().ok_or(Error::ResourceDomain)?.clone()
                } else {
                    source.clone()
                };
                alpha = lease.alpha();
                has_yuv = planar;
                let conversion = planar.then_some(mesh.yuv_conversion).flatten();
                let sigma = mesh.texture_effect.blur_sigma;
                let actual_cost = cost::blur_cost(
                    &self.device,
                    &MeshTextureMetadata::from_lease(&lease, planar),
                    conversion,
                    sigma,
                )?;
                if actual_cost.0 > admission.bytes || actual_cost.1 > admission.passes {
                    return Err(Error::ResourceDomain);
                }

                if blur_eligible(
                    &self.device,
                    &source,
                    planar.then_some(&chroma),
                    conversion,
                    sigma,
                ) {
                    let mut input = if let Some(conversion) = conversion {
                        BlurInput::planar(&self.device, &source, &chroma, conversion)?
                    } else {
                        BlurInput::rgb(&self.device, &source, alpha)?
                    };
                    if let Some((first, second)) = lease.generations() {
                        input =
                            input.with_generations(first, if planar { second } else { None })?;
                    }
                    let (textures, generations) = input.identities();
                    let key = generations.map(|generations| BlurKey {
                        textures,
                        generations,
                        sigma: sigma.to_bits(),
                        conversion: conversion.map(|v| {
                            bytemuck::cast::<YuvConversion, [f32; 16]>(v).map(f32::to_bits)
                        }),
                    });
                    let blur = if let Some(existing) = key.as_ref().and_then(|k| prepared.get(k)) {
                        existing.clone()
                    } else {
                        if self.blur.is_none() {
                            self.blur = Some(ImageBlurEngine::new(
                                &self.device,
                                ExecutionPreference::Automatic,
                            )?);
                        }
                        let slot = self
                            .blur_pool
                            .iter()
                            .enumerate()
                            .skip(self.used_blurs)
                            .find_map(|(index, storage)| {
                                (Arc::strong_count(storage) == 1
                                    && storage.usage.reusable()
                                    && storage.matches(&input, BlurStorage::ManagedSource))
                                .then_some(index)
                            });
                        let reused = if let Some(index) = slot {
                            self.blur_pool.swap(index, self.used_blurs);
                            let storage = &mut self.blur_pool[self.used_blurs];
                            Arc::get_mut(storage).ok_or(Error::ResourceDomain)?.reset();
                            Some(storage.clone())
                        } else {
                            None
                        };
                        let reused_storage = reused.is_some();
                        let blur = Arc::new(
                            self.blur
                                .as_mut()
                                .ok_or(Error::ResourceDomain)?
                                .prepare_reusing(
                                    &input,
                                    sigma,
                                    BlurStorage::ManagedSource,
                                    None,
                                    reused,
                                    Some(&self.queue),
                                )
                                .await?,
                        );
                        let storage = blur.storage();
                        storage.touch(self.frame);
                        if !reused_storage {
                            self.blur_pool.try_reserve(1).map_err(|_| Error::Capacity)?;
                            self.blur_pool.push(storage);
                            let last = self.blur_pool.len() - 1;
                            self.blur_pool.swap(last, self.used_blurs);
                        }
                        self.used_blurs = self.used_blurs.checked_add(1).ok_or(Error::Capacity)?;
                        metrics.live_blur_texture_allocations = metrics
                            .live_blur_texture_allocations
                            .checked_add(blur.metrics().texture_allocations)
                            .ok_or(Error::Capacity)?;
                        metrics.live_blur_passes = metrics
                            .live_blur_passes
                            .checked_add(2)
                            .ok_or(Error::Capacity)?;
                        metrics.live_blur_texture_bytes = metrics
                            .live_blur_texture_bytes
                            .checked_add(blur.metrics().texture_bytes)
                            .ok_or(Error::Capacity)?;
                        result.blurs.try_reserve(1).map_err(|_| Error::Capacity)?;
                        result.blurs.push(blur.clone());
                        if let Some(key) = key {
                            prepared.try_reserve(1).map_err(|_| Error::Capacity)?;
                            prepared.insert(key, blur.clone());
                        }
                        blur
                    };
                    source = blur.output().clone();
                    chroma = source.clone();
                    alpha = blur.alpha();
                    has_yuv = false;
                    blurred = true;
                } else {
                    unfilterable = has_yuv
                        && source.format() == wgpu::TextureFormat::R16Unorm
                        && chroma.format() == wgpu::TextureFormat::Rg16Unorm;
                }
                result.leases.push(lease);
            }
            let white_index = usize::from(mesh.texture_sampling == ImageSampling::Nearest);
            if !has_texture && let Some(binding) = &self.white_bindings[white_index] {
                result.values.push(Material {
                    state: MeshMaterialState {
                        size: [1, 1],
                        alpha: ImageAlpha::Straight,
                        has_texture: false,
                        has_yuv: false,
                        prepared_blur: false,
                    },
                    binding: binding.clone(),
                    unfilterable: false,
                });
                continue;
            }
            let sampler = (!unfilterable).then(|| self.sampler(mesh.texture_sampling));
            let views = [
                source.create_view(&Default::default()),
                chroma.create_view(&Default::default()),
            ];
            let entries = if unfilterable {
                vec![
                    wgpu::BindGroupEntry {
                        binding: 3,
                        resource: wgpu::BindingResource::TextureView(&views[0]),
                    },
                    wgpu::BindGroupEntry {
                        binding: 4,
                        resource: wgpu::BindingResource::TextureView(&views[1]),
                    },
                ]
            } else {
                vec![
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(
                            sampler.as_ref().ok_or(Error::ResourceDomain)?,
                        ),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&views[0]),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: wgpu::BindingResource::TextureView(&views[1]),
                    },
                ]
            };
            let layouts = self.layouts.as_ref().ok_or(Error::ResourceDomain)?;
            let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
            let binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU owned Mesh3D material planes"),
                layout: if unfilterable {
                    &layouts.unfilterable
                } else {
                    &layouts.texture
                },
                entries: &entries,
            });
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            if !has_texture {
                self.white_bindings[white_index] = Some(binding.clone());
            }
            result.values.push(Material {
                state: MeshMaterialState {
                    size: [source.width(), source.height()],
                    alpha,
                    has_texture,
                    has_yuv,
                    prepared_blur: blurred,
                },
                binding,
                unfilterable,
            });
        }
        Ok(result)
    }
}
fn blur_eligible(
    device: &wgpu::Device,
    source: &wgpu::Texture,
    chroma: Option<&wgpu::Texture>,
    conversion: Option<YuvConversion>,
    sigma: f32,
) -> bool {
    if !sigma.is_finite() || sigma <= 0.01 || sigma > 32.0 {
        return false;
    }
    if chroma.is_some() != conversion.is_some() {
        return false;
    }
    if let Some(chroma) = chroma {
        let formats = (source.format() == wgpu::TextureFormat::R8Unorm
            && chroma.format() == wgpu::TextureFormat::Rg8Unorm)
            || (device
                .features()
                .contains(wgpu::Features::TEXTURE_FORMAT_16BIT_NORM)
                && source.format() == wgpu::TextureFormat::R16Unorm
                && chroma.format() == wgpu::TextureFormat::Rg16Unorm);
        formats
            && chroma.width() == source.width().div_ceil(2)
            && chroma.height() == source.height().div_ceil(2)
    } else {
        matches!(
            source.format(),
            wgpu::TextureFormat::Rgba8Unorm
                | wgpu::TextureFormat::Rgba8UnormSrgb
                | wgpu::TextureFormat::Bgra8Unorm
                | wgpu::TextureFormat::Bgra8UnormSrgb
                | wgpu::TextureFormat::Rgba16Float
        )
    }
}
