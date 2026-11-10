use super::*;

/// Exact native binding schemas. Explicit layouts also make independently
/// retained image, path and glyph pages compatible with the common pipelines.
#[derive(Clone, Debug)]
pub struct NativeRenderLayouts {
    pub frame: wgpu::BindGroupLayout,
    pub vector: wgpu::BindGroupLayout,
    pub text: wgpu::BindGroupLayout,
    pub text_atlas: wgpu::BindGroupLayout,
    pub mask_chain: wgpu::BindGroupLayout,
    pub glyph_paint: wgpu::BindGroupLayout,
    pub empty: wgpu::BindGroupLayout,
    pub image: NativeImageLayouts,
}
impl NativeRenderLayouts {
    pub(super) fn new(device: &wgpu::Device) -> Self {
        use wgpu::ShaderStages as Stage;
        let make = |label, entries: &[wgpu::BindGroupLayoutEntry]| {
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some(label),
                entries,
            })
        };
        Self {
            frame: make(
                "Native frame layout",
                &[buffer(0, Stage::VERTEX_FRAGMENT, false, 224)],
            ),
            vector: make(
                "Native vector material layout",
                &[
                    buffer(0, Stage::VERTEX_FRAGMENT, false, 224),
                    buffer(1, Stage::VERTEX_FRAGMENT, true, 256),
                    buffer(2, Stage::FRAGMENT, true, 32),
                ],
            ),
            text: make(
                "Native positioned text layout",
                &[
                    buffer(0, Stage::VERTEX_FRAGMENT, false, 224),
                    buffer(1, Stage::VERTEX, true, 32),
                ],
            ),
            text_atlas: make(
                "Native grayscale and color glyph atlas layout",
                &[sampler(0), texture(1), texture(2)],
            ),
            mask_chain: make(
                "Native original three-mask chain layout",
                &[
                    sampler(0),
                    texture(1),
                    buffer(2, Stage::FRAGMENT, false, 96),
                    buffer(3, Stage::FRAGMENT, false, 288),
                ],
            ),
            glyph_paint: make(
                "Native original hinted glyph paint layout",
                &[
                    buffer(0, Stage::VERTEX_FRAGMENT, false, 224),
                    buffer(1, Stage::VERTEX_FRAGMENT, true, 256),
                    buffer(2, Stage::VERTEX_FRAGMENT, true, 32),
                    buffer(
                        3,
                        Stage::VERTEX_FRAGMENT,
                        true,
                        wire::GlyphPaint::WIRE_SIZE as u64,
                    ),
                ],
            ),
            empty: make("Native explicit empty hinted mask slot", &[]),
            image: NativeImageLayouts::new(device),
        }
    }
}
fn buffer(
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
            min_binding_size: std::num::NonZeroU64::new(size),
        },
        count: None,
    }
}
fn sampler(binding: u32) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
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
pub(super) fn bind(
    device: &wgpu::Device,
    layout: &wgpu::BindGroupLayout,
    entries: &[wgpu::BindGroupEntry<'_>],
) -> wgpu::BindGroup {
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("Native retained command binding"),
        layout,
        entries,
    })
}
pub(super) fn buffer_entry(binding: u32, buffer: &wgpu::Buffer) -> wgpu::BindGroupEntry<'_> {
    wgpu::BindGroupEntry {
        binding,
        resource: buffer.as_entire_binding(),
    }
}
