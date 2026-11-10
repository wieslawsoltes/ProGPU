//! Explicit original bindings shared by masked and unmasked pipeline variants.
use super::*;

#[derive(Clone, Copy)]
pub(super) enum Family {
    Vector,
    Text,
    Image,
}

fn buffer(binding: u32, storage: bool, size: u64) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::VERTEX_FRAGMENT,
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
fn sampler() -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding: 0,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
        count: None,
    }
}
fn texture(binding: u32) -> wgpu::BindGroupLayoutEntry {
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
pub(super) fn mask(device: &wgpu::Device) -> wgpu::BindGroupLayout {
    let mut primary = buffer(2, false, 96);
    let mut chain = buffer(3, false, 288);
    primary.visibility = wgpu::ShaderStages::FRAGMENT;
    chain.visibility = wgpu::ShaderStages::FRAGMENT;
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU common four-mask layout"),
        entries: &[sampler(), texture(1), primary, chain],
    })
}
pub(super) fn hinted_text(device: &wgpu::Device) -> wgpu::PipelineLayout {
    let frame = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU original hinted paint frame"),
        entries: &[
            buffer(0, false, size_of::<Uniforms>() as u64),
            buffer(1, true, 256),
            buffer(2, true, 32),
            buffer(3, true, 96),
        ],
    });
    let atlas = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU original hinted glyph atlas"),
        entries: &[sampler(), texture(1), texture(2)],
    });
    let mask = mask(device);
    let paint = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU original hinted image paint"),
        entries: &[sampler(), texture(1)],
    });
    device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("ProGPU original direct hinted paint pipeline layout"),
        bind_group_layouts: &[Some(&frame), Some(&atlas), Some(&mask), Some(&paint)],
        immediate_size: 0,
    })
}
pub(super) fn pipeline(
    device: &wgpu::Device,
    family: Family,
    mask: Option<&wgpu::BindGroupLayout>,
) -> wgpu::PipelineLayout {
    pipeline_with_color(device, family, mask, false)
}
pub(super) fn image_color(
    device: &wgpu::Device,
    mask: Option<&wgpu::BindGroupLayout>,
) -> wgpu::PipelineLayout {
    pipeline_with_color(device, Family::Image, mask, true)
}
pub(super) fn color_matrix(device: &wgpu::Device) -> wgpu::BindGroupLayout {
    let mut matrix = buffer(2, false, 96);
    matrix.visibility = wgpu::ShaderStages::FRAGMENT;
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU original image color-matrix layout"),
        entries: &[sampler(), texture(1), matrix],
    })
}
fn pipeline_with_color(
    device: &wgpu::Device,
    family: Family,
    mask: Option<&wgpu::BindGroupLayout>,
    color: bool,
) -> wgpu::PipelineLayout {
    let mut frame = vec![buffer(0, false, size_of::<Uniforms>() as u64)];
    match family {
        Family::Vector => {
            frame.push(buffer(1, true, 256));
            frame.push(buffer(2, true, 32));
        }
        Family::Text => frame.push(buffer(1, true, 32)),
        Family::Image => (),
    }
    let frame = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU retained frame/material layout"),
        entries: &frame,
    });
    let mut sources = vec![sampler(), texture(1)];
    if matches!(family, Family::Text) {
        sources.push(texture(2));
    }
    let sources = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU retained source layout"),
        entries: &sources,
    });
    let mut layouts = vec![Some(&frame), Some(&sources)];
    if let Some(mask) = mask {
        layouts.push(Some(mask));
    }
    let color_layout = color.then(|| color_matrix(device));
    if let Some(color) = &color_layout {
        layouts.push(Some(color));
    }
    device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("ProGPU explicit retained pipeline layout"),
        bind_group_layouts: &layouts,
        immediate_size: 0,
    })
}
