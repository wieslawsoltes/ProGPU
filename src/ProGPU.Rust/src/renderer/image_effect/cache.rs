//! Original per-frame pools with independently retained prepared generations.
use super::*;
use crate::image_effect::{ImageBlurStorage, ImageEffectUniforms, ResourceUse};
use std::sync::{
    Arc,
    atomic::{AtomicU64, Ordering},
};

const MAX_ENTRIES: usize = 16 * 1024;
const MAX_BLUR_BYTES: u64 = 512 * 1024 * 1024;

pub(super) struct Parameters {
    buffer: wgpu::Buffer,
    pub binding: wgpu::BindGroup,
    pub usage: Arc<ResourceUse>,
}
#[derive(Clone, PartialEq, Eq, Hash)]
pub(super) struct BlurKey {
    textures: [wgpu::Texture; 2],
    generations: [u32; 5],
    sigma: u32,
    conversion: Option<[u32; 16]>,
    storage: wgpu::TextureFormat,
    premultiplied: bool,
    native: bool,
}
impl BlurKey {
    pub fn new(source: &BlurInput, sigma: f32, policy: BlurStorage) -> Option<Self> {
        let (textures, generations) = source.identities();
        let generations = generations?;
        let conversion = source.conversion().map(|value| {
            let values: &[f32] = bytemuck::cast_slice(std::slice::from_ref(&value));
            // Original Vector4 equality considers the two zero signs equal;
            // sigma independently retains its exact BitConverter bits.
            std::array::from_fn(|i| {
                if values[i] == 0.0 {
                    0
                } else {
                    values[i].to_bits()
                }
            })
        });
        Some(Self {
            textures,
            generations,
            sigma: sigma.to_bits(),
            conversion,
            storage: source.storage_format(policy),
            premultiplied: source.alpha() == crate::image::ImageAlpha::Premultiplied,
            native: policy == BlurStorage::NativeRgba8,
        })
    }
}
#[derive(PartialEq, Eq, Hash)]
pub(super) enum PreflightBlurKey {
    Pixels((u8, usize), u32, bool),
    Gpu(BlurKey),
}
impl PreflightBlurKey {
    pub fn new(draw: &crate::image_effect::ImageEffectDraw) -> Option<Self> {
        if draw.policy != ImageEffectDrawPolicy::ManagedExtension {
            return None;
        }
        match &draw.source {
            ImageEffectSource::Rgb(source @ ImageSource::Pixels(_)) => Some(Self::Pixels(
                source.identity(),
                draw.effect.effects1[2].to_bits(),
                draw.effective_sampling().uses_mipmaps(),
            )),
            ImageEffectSource::Gpu(source) => {
                BlurKey::new(source, draw.effect.effects1[2], BlurStorage::ManagedSource)
                    .map(Self::Gpu)
            }
            _ => None,
        }
    }
}
pub(super) fn distinct_cost(
    draw: &crate::image_effect::ImageEffectDraw,
    cost: (u64, u32),
    keys: &mut HashSet<PreflightBlurKey>,
) -> Result<(u64, u32), Error> {
    if cost.0 != 0
        && let Some(key) = PreflightBlurKey::new(draw)
    {
        keys.try_reserve(1).map_err(|_| Error::Capacity)?;
        if !keys.insert(key) {
            return Ok((0, 1));
        }
    }
    Ok(cost)
}
#[derive(Clone, PartialEq, Eq, Hash)]
struct TextureKey {
    textures: [wgpu::Texture; 2],
    // Content generations deliberately do not invalidate immutable texture views.
    views: Option<[u32; 3]>,
    sampling: crate::image::ImageSampling,
    address: [crate::image::ImageAddress; 2],
    managed: bool,
    target: wgpu::TextureFormat,
}
pub(super) struct TextureBinding {
    pub binding: wgpu::BindGroup,
    _source: BlurInput,
    last_used: AtomicU64,
}
impl TextureBinding {
    pub fn touch(&self, frame: u64) {
        self.last_used.fetch_max(frame, Ordering::AcqRel);
    }
}
struct StorageEntry {
    storage: Arc<ImageBlurStorage>,
}
#[derive(Default)]
pub(super) struct Resources {
    frame: u64,
    clock: Arc<AtomicU64>,
    host_frame: bool,
    parameters: Vec<Arc<Parameters>>,
    used_parameters: usize,
    textures: HashMap<TextureKey, Arc<TextureBinding>>,
    storage: Vec<StorageEntry>,
    used_storage: usize,
}
impl Resources {
    pub fn host_frame(&mut self, frame: u64) -> Result<(), Error> {
        if self.host_frame && frame < self.frame {
            return Err(Error::InvalidInput);
        }
        if !self.host_frame {
            // Preparation epochs and host display frames are distinct domains.
            // Drop only the old cache leases when selecting the explicit clock.
            self.textures.clear();
            self.storage.clear();
            self.used_storage = 0;
        }
        self.frame = frame;
        self.clock.store(frame, Ordering::Release);
        self.host_frame = true;
        self.prune();
        Ok(())
    }
    pub fn begin(&mut self) -> Result<(), Error> {
        if !self.host_frame {
            self.frame = self.frame.checked_add(1).ok_or(Error::Capacity)?;
        }
        self.clock.store(self.frame, Ordering::Release);
        self.used_parameters = 0;
        self.used_storage = 0;
        self.prune();
        Ok(())
    }
    fn prune(&mut self) {
        let frame = self.frame;
        self.textures.retain(|_, value| {
            frame.saturating_sub(value.last_used.load(Ordering::Acquire)) <= 120
        });
        self.storage
            .retain(|value| frame.saturating_sub(value.storage.last_used()) <= 240);
    }
    pub fn clock(&self) -> Arc<AtomicU64> {
        self.clock.clone()
    }
    pub fn counts(&self) -> [u32; 3] {
        [
            self.parameters.len() as u32,
            self.textures.len() as u32,
            self.storage.len() as u32,
        ]
    }
    pub fn parameters(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        layout: &wgpu::BindGroupLayout,
        value: &ImageEffectUniforms,
    ) -> Result<(Arc<Parameters>, bool), Error> {
        let found = (self.used_parameters..self.parameters.len()).find(|&i| {
            Arc::strong_count(&self.parameters[i]) == 1 && self.parameters[i].usage.reusable()
        });
        if let Some(index) = found {
            self.parameters.swap(self.used_parameters, index);
            let value_owner = Arc::get_mut(&mut self.parameters[self.used_parameters])
                .ok_or(Error::ResourceDomain)?;
            queue.write_buffer(&value_owner.buffer, 0, bytemuck::bytes_of(value));
            value_owner.usage = Arc::default();
            let result = self.parameters[self.used_parameters].clone();
            self.used_parameters += 1;
            return Ok((result, true));
        }
        let buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("ProGPU original pooled image-effect uniform"),
            contents: bytemuck::bytes_of(value),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });
        let binding = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU original pooled image-effect binding"),
            layout,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: buffer.as_entire_binding(),
            }],
        });
        let result = Arc::new(Parameters {
            buffer,
            binding,
            usage: Arc::default(),
        });
        if self.parameters.len() < MAX_ENTRIES {
            self.parameters
                .try_reserve(1)
                .map_err(|_| Error::Capacity)?;
            self.parameters.push(result.clone());
            let last = self.parameters.len() - 1;
            self.parameters.swap(self.used_parameters, last);
            self.used_parameters += 1;
        }
        Ok((result, false))
    }
    pub fn acquire_storage(
        &mut self,
        source: &BlurInput,
        policy: BlurStorage,
    ) -> Option<Arc<ImageBlurStorage>> {
        let index = (self.used_storage..self.storage.len()).find(|&i| {
            let value = &self.storage[i].storage;
            Arc::strong_count(value) == 1 && value.usage.reusable() && value.matches(source, policy)
        })?;
        self.storage.swap(self.used_storage, index);
        let value = &mut self.storage[self.used_storage];
        Arc::get_mut(&mut value.storage)?.reset();
        value.storage.touch(self.frame);
        let result = value.storage.clone();
        self.used_storage += 1;
        Some(result)
    }
    pub fn publish_storage(&mut self, storage: Arc<ImageBlurStorage>) -> Result<(), Error> {
        if self
            .storage
            .iter()
            .any(|v| Arc::ptr_eq(&v.storage, &storage))
        {
            return Ok(());
        }
        let bytes: u64 = self.storage.iter().map(|v| v.storage.bytes()).sum();
        if self.storage.len() == MAX_ENTRIES
            || bytes.saturating_add(storage.bytes()) > MAX_BLUR_BYTES
        {
            // Private prepared ownership remains complete when the bounded idle
            // cache cannot retain another entry; never evict a lease by destroy().
            return Ok(());
        }
        self.storage.try_reserve(1).map_err(|_| Error::Capacity)?;
        storage.touch(self.frame);
        self.storage.push(StorageEntry { storage });
        let last = self.storage.len() - 1;
        self.storage.swap(self.used_storage, last);
        self.used_storage += 1;
        Ok(())
    }
    pub fn textures(
        &mut self,
        device: &wgpu::Device,
        layout: &wgpu::BindGroupLayout,
        source: &BlurInput,
        sampler: &wgpu::Sampler,
        sampling: crate::image::ImageSampling,
        address: [crate::image::ImageAddress; 2],
        managed: bool,
        target: wgpu::TextureFormat,
    ) -> Result<(Arc<TextureBinding>, bool), Error> {
        let (textures, generations) = source.identities();
        let key = TextureKey {
            textures,
            views: generations.map(|g| [g[0], g[2], g[4]]),
            sampling,
            address,
            managed,
            target,
        };
        if let Some(value) = self.textures.get_mut(&key) {
            value.touch(self.frame);
            return Ok((value.clone(), true));
        }
        let views = source.views();
        let binding = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU retained source/view-generation effect binding"),
            layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::Sampler(sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(&views[0]),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::TextureView(&views[1]),
                },
            ],
        });
        let result = Arc::new(TextureBinding {
            binding,
            _source: source.clone(),
            last_used: AtomicU64::new(self.frame),
        });
        if self.textures.len() < MAX_ENTRIES {
            self.textures.try_reserve(1).map_err(|_| Error::Capacity)?;
            self.textures.insert(key, result.clone());
        }
        Ok((result, false))
    }
}
