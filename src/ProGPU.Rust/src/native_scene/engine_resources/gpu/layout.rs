use super::*;
#[derive(Clone, Debug)]
pub struct NativeImageLayouts {
    pub texture: wgpu::BindGroupLayout,
    pub mask: wgpu::BindGroupLayout,
    pub effect_uniform: wgpu::BindGroupLayout,
    pub effect_texture: wgpu::BindGroupLayout,
}
fn sampler(binding: u32, filtering: bool) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Sampler(if filtering {
            wgpu::SamplerBindingType::Filtering
        } else {
            wgpu::SamplerBindingType::NonFiltering
        }),
        count: None,
    }
}
fn texture(binding: u32, filterable: bool) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Texture {
            sample_type: wgpu::TextureSampleType::Float { filterable },
            view_dimension: wgpu::TextureViewDimension::D2,
            multisampled: false,
        },
        count: None,
    }
}
fn uniform(binding: u32, size: u64) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Buffer {
            ty: wgpu::BufferBindingType::Uniform,
            has_dynamic_offset: false,
            min_binding_size: std::num::NonZeroU64::new(size),
        },
        count: None,
    }
}
impl NativeImageLayouts {
    pub(crate) fn new(device: &wgpu::Device) -> Self {
        let make = |label, entries: &[wgpu::BindGroupLayoutEntry]| {
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some(label),
                entries,
            })
        };
        Self {
            texture: make(
                "Native retained image texture",
                &[sampler(0, true), texture(1, true)],
            ),
            mask: make(
                "Native retained image mask or matrix",
                &[sampler(0, true), texture(1, true), uniform(2, 96)],
            ),
            effect_uniform: make("Native retained image effect uniform", &[uniform(0, 288)]),
            effect_texture: make(
                "Native retained image effect source",
                &[sampler(0, true), texture(1, true), texture(2, true)],
            ),
        }
    }
}
pub(super) fn blur_layout(device: &wgpu::Device, unfilterable: bool) -> wgpu::BindGroupLayout {
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("Native view-preserving Gaussian bindings"),
        entries: &[
            sampler(0, !unfilterable),
            texture(1, !unfilterable),
            texture(2, !unfilterable),
            uniform(3, 912),
        ],
    })
}
pub(crate) fn image_sampler(
    device: &wgpu::Device,
    image: &wire::ImageDraw,
) -> Result<wgpu::Sampler, Error> {
    let options = crate::native_scene::semantic::values::image_sampler_options(
        image.sampling,
        image.max_anisotropy,
    )
    .ok_or(Error::InvalidInput)?;
    let address = |v| match v {
        0 => Ok(wgpu::AddressMode::ClampToEdge),
        1 => Ok(wgpu::AddressMode::Repeat),
        2 => Ok(wgpu::AddressMode::MirrorRepeat),
        _ => Err(Error::InvalidInput),
    };
    let u = (image.flags & SCENE_IMAGE_ADDRESS_U_MASK) >> SCENE_IMAGE_ADDRESS_U_SHIFT;
    let v = (image.flags & SCENE_IMAGE_ADDRESS_V_MASK) >> SCENE_IMAGE_ADDRESS_V_SHIFT;
    let filter = |linear| {
        if linear {
            wgpu::FilterMode::Linear
        } else {
            wgpu::FilterMode::Nearest
        }
    };
    Ok(device.create_sampler(&wgpu::SamplerDescriptor {
        label: Some("Native retained image sampling"),
        address_mode_u: address(u)?,
        address_mode_v: address(v)?,
        address_mode_w: wgpu::AddressMode::ClampToEdge,
        mag_filter: filter(options.mag_linear),
        min_filter: filter(options.min_linear),
        mipmap_filter: if options.mip_linear {
            wgpu::MipmapFilterMode::Linear
        } else {
            wgpu::MipmapFilterMode::Nearest
        },
        lod_min_clamp: 0.0,
        lod_max_clamp: if u == 0
            && v == 0
            && matches!(
                image.sampling,
                IMAGE_SAMPLING_NEAREST
                    | IMAGE_SAMPLING_LINEAR
                    | IMAGE_SAMPLING_CUBIC
                    | IMAGE_SAMPLING_FANT
            ) {
            0.0
        } else {
            32.0
        },
        anisotropy_clamp: options.max_anisotropy,
        ..Default::default()
    }))
}
pub(super) fn binding(
    device: &wgpu::Device,
    layout: &wgpu::BindGroupLayout,
    entries: &[wgpu::BindGroupEntry],
) -> wgpu::BindGroup {
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("Native retained image bindings"),
        layout,
        entries,
    })
}
pub(super) fn sampler_entry(binding: u32, sampler: &wgpu::Sampler) -> wgpu::BindGroupEntry<'_> {
    wgpu::BindGroupEntry {
        binding,
        resource: wgpu::BindingResource::Sampler(sampler),
    }
}
pub(super) fn view_entry(binding: u32, view: &wgpu::TextureView) -> wgpu::BindGroupEntry<'_> {
    wgpu::BindGroupEntry {
        binding,
        resource: wgpu::BindingResource::TextureView(view),
    }
}
pub(super) fn buffer_entry(binding: u32, buffer: &wgpu::Buffer) -> wgpu::BindGroupEntry<'_> {
    wgpu::BindGroupEntry {
        binding,
        resource: buffer.as_entire_binding(),
    }
}
