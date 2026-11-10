use super::*;
use crate::{mask::MaskUniforms, native_scene::engine_resources::NativeImageTexture};
use wgpu::util::DeviceExt;

/// The already resolved original sampled/analytic mask and optional three-record
/// chain. This binding preserves actual source views and uniform bytes; source
/// policy and physical-frame admission belong to the owning mask producer.
pub struct NativeMaskBinding {
    pub(super) device: wgpu::Device,
    pub(super) binding: wgpu::BindGroup,
    pub(super) chained: bool,
    source: NativeImageTexture,
    primary: wgpu::Buffer,
    chain: Option<wgpu::Buffer>,
}
impl NativeMaskBinding {
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn bind_group(&self) -> &wgpu::BindGroup {
        &self.binding
    }
    pub fn mode(&self) -> NativeMaskMode {
        if self.chained {
            NativeMaskMode::Chain
        } else {
            NativeMaskMode::Sampled
        }
    }
    pub fn source(&self) -> &NativeImageTexture {
        &self.source
    }
    pub fn uniform_buffers(&self) -> (&wgpu::Buffer, Option<&wgpu::Buffer>) {
        (&self.primary, self.chain.as_ref())
    }
    pub fn upload_bytes(&self) -> u64 {
        if self.chained { 384 } else { 96 }
    }
    pub async fn prepare(
        pipelines: &NativePipelineCache,
        source: NativeImageTexture,
        sampler: &wgpu::Sampler,
        primary: MaskUniforms,
        chain: Option<[MaskUniforms; 3]>,
    ) -> Result<Self, Error> {
        let device = pipelines.device();
        if device != source.device() {
            return Err(Error::ResourceDomain);
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let make = |label, contents: &[u8]| {
            device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some(label),
                contents,
                usage: wgpu::BufferUsages::UNIFORM,
            })
        };
        let primary = make("Native immutable primary mask frame", &mask_bytes(primary));
        let chain = chain.map(|records| {
            let mut bytes = [0; 288];
            for (target, record) in bytes.chunks_exact_mut(96).zip(records) {
                target.copy_from_slice(&mask_bytes(record));
            }
            make("Native immutable analytic mask chain", &bytes)
        });
        let mut entries = vec![
            wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::Sampler(sampler),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: wgpu::BindingResource::TextureView(source.view()),
            },
            layout::buffer_entry(2, &primary),
        ];
        if let Some(chain) = &chain {
            entries.push(layout::buffer_entry(3, chain));
        }
        let layouts = pipelines.layouts();
        let binding = layout::bind(
            device,
            if chain.is_some() {
                &layouts.mask_chain
            } else {
                &layouts.image.mask
            },
            &entries,
        );
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self {
            device: device.clone(),
            binding,
            chained: chain.is_some(),
            source,
            primary,
            chain,
        })
    }
}
fn mask_bytes(record: MaskUniforms) -> [u8; 96] {
    let mut result = [0; 96];
    for (target, value) in result.chunks_exact_mut(4).zip(
        record
            .coordinate0
            .into_iter()
            .chain(record.coordinate1)
            .chain(record.bounds)
            .chain(record.radii_x)
            .chain(record.radii_y)
            .chain(record.options),
    ) {
        target.copy_from_slice(&value.to_le_bytes());
    }
    result
}
