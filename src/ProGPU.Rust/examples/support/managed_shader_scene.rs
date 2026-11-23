//! Authored retained managed WGSL gallery using original in-repository fixtures.
use progpu::{
    geometry::{Affine, AnalyticPrimitive, Shape},
    image::{GpuImage, ImageAlpha, ImageSampling},
    mask::{ClipPath, ClipPlan, MaskUniforms},
    path::{FillRule, Path, Segment},
    renderer::{Frame, Renderer},
    scene::{LayerBlend, ManagedShaderEffectDraw, Scene},
    shader_effect::{
        ManagedShaderDescriptor, ManagedShaderMask, ManagedShaderParameters, ManagedShaderProgram,
        ManagedShaderQuad, ManagedShaderSampler, SampleLattice,
    },
};
use std::sync::Arc;
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

// Exact original authored bodies from ProGPU.Tests/WpfShaderEffectRenderTests.cs.
const TINT: &str = r#"
fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> {
    let tint = wpf_constant(0u);
    return vec4<f32>(tint.r, inputColor.g * tint.g, tint.b, inputColor.a);
}
"#;
const REGISTER_ONE: &str = r#"
fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> {
    return wpf_sample_register(1u, uv);
}
"#;
const REGISTER_FIFTEEN: &str = r#"
fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> {
    return wpf_sample_register(15u, uv);
}
"#;
const SOURCE_ONE: &str = r#"
fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> {
    return vec4<f32>(inputColor.g, inputColor.r * 0.5, 1.0, inputColor.a);
}
"#;

fn straight_texel(device: &wgpu::Device, queue: &wgpu::Queue, pixel: [u8; 4]) -> Result<GpuImage> {
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("ProGPU authored straight-alpha managed sampler"),
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
    queue.write_texture(
        wgpu::TexelCopyTextureInfo {
            texture: &texture,
            mip_level: 0,
            origin: wgpu::Origin3d::ZERO,
            aspect: wgpu::TextureAspect::All,
        },
        &pixel,
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
    Ok(GpuImage::from_texture(
        device,
        &texture,
        ImageAlpha::Straight,
    )?)
}
async fn pattern(renderer: &mut Renderer, size: [u32; 2], phase: usize) -> Result<GpuImage> {
    let [w, h] = size.map(|v| v as f32);
    let mut scene = Scene::default();
    scene.draw_analytic(AnalyticPrimitive::rectangle(
        [0.0, 0.0, w, h],
        [0.08, 0.18, 0.45, 1.0],
    ))?;
    for i in 0..5 {
        let color = if (i + phase) % 2 == 0 {
            [1.0, 0.35, 0.12, 0.85]
        } else {
            [0.1, 0.9, 0.6, 0.8]
        };
        let mut shape =
            AnalyticPrimitive::rectangle([w * i as f32 / 5.0, h * 0.1, w * 0.28, h * 0.8], color);
        if (i + phase) % 2 == 0 {
            shape.shape = Shape::Ellipse;
        }
        scene.draw_analytic(shape)?;
    }
    let prepared = renderer
        .prepare_scene(
            &scene,
            Frame {
                width: size[0],
                height: size[1],
                dpi_scale: 1.0,
            },
        )
        .await?;
    Ok(renderer.capture(&prepared, None).await?.into_image())
}

/// Twelve actual-target panels: constants, independent and sixteen-register
/// samplers, source override, three alpha wrappers, affine/UV and owned masks.
pub async fn managed_shader_scene(
    renderer: &mut Renderer,
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    dpi: f32,
    size: [u32; 2],
) -> Result<Scene> {
    let mut scene = Scene::default();
    let target = SampleLattice {
        origin: [0; 2],
        size,
    };
    let input = pattern(
        renderer,
        [(176.0 * dpi).ceil() as u32, (102.0 * dpi).ceil() as u32],
        0,
    )
    .await?;
    let alternate = pattern(
        renderer,
        [(113.0 * dpi).ceil() as u32, (79.0 * dpi).ceil() as u32],
        1,
    )
    .await?;
    let straight = straight_texel(device, queue, [80, 190, 250, 144])?;
    let red = straight_texel(device, queue, [255, 0, 0, 255])?;
    let green = straight_texel(device, queue, [0, 192, 64, 255])?;
    for index in 0..12 {
        let x = 12.25 + (index % 4) as f32 * 236.0;
        let y = 12.5 + (index / 4) as f32 * 186.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 224.0, 172.0],
            [0.13, 0.18, 0.25, 1.0],
        ))?;
        scene.push_clip_rect([x + 6.0, y + 6.0, 212.0, 160.0])?;
        let source = match index {
            1 => TINT,
            2 => REGISTER_ONE,
            3 => REGISTER_FIFTEEN,
            4 | 5 => SOURCE_ONE,
            _ => "",
        };
        let mut descriptor = ManagedShaderDescriptor::new(ManagedShaderProgram::new(
            source,
            format!("managed_gallery_{index}"),
        ));
        descriptor.input = Some(input.clone());
        descriptor.constants = Arc::from([1.0, 0.25, 0.75, 1.0]);
        descriptor.sampling = if index % 2 == 0 {
            ImageSampling::Nearest
        } else {
            ImageSampling::Linear
        };
        if index == 2 {
            descriptor.samplers = Arc::from([ManagedShaderSampler::new(
                1,
                Some(alternate.clone()),
                ImageSampling::Linear,
            )?]);
        }
        if index == 3 {
            descriptor.input = None;
            descriptor.samplers = (0..16)
                .map(|register| {
                    ManagedShaderSampler::new(
                        register,
                        Some(if register == 15 {
                            green.clone()
                        } else {
                            red.clone()
                        }),
                        ImageSampling::Nearest,
                    )
                })
                .collect::<std::result::Result<Vec<_>, _>>()?
                .into();
        }
        if index == 4 || index == 5 {
            descriptor.source_register = 1;
            descriptor.samplers = Arc::from([
                ManagedShaderSampler::new(0, Some(red.clone()), ImageSampling::Nearest)?,
                ManagedShaderSampler::new(1, Some(alternate.clone()), ImageSampling::Linear)?,
            ]);
        }
        if index == 6 || index == 7 {
            descriptor.input = Some(straight.clone());
        }
        let mut parameters = ManagedShaderParameters::new(descriptor)?;
        if index == 5 {
            parameters = parameters.with_captured_input(input.clone());
        }
        let mut quad = ManagedShaderQuad::new([0.0, 0.0, 176.0, 102.0]);
        quad.transform = Affine([1.0, 0.0, 0.0, 1.0, x + 24.0, y + 32.0]);
        quad.opacity = if index == 6 { 0.7 } else { 1.0 };
        if index == 8 {
            quad.transform = Affine([0.94, -0.13, 0.20, 1.02, x + 17.0, y + 39.0]);
        }
        if index == 9 {
            quad.uv = [0.10, 0.16, 0.94, 0.89];
            quad.output_edges = Some([-3.25, 2.5, 178.5, 101.25]);
        }
        let mask = if index == 10 {
            let path = Path::new(
                vec![
                    Segment::line([x + 12.0, y + 19.0], [x + 207.0, y + 19.0]),
                    Segment::quadratic([
                        [x + 207.0, y + 19.0],
                        [x + 154.0, y + 154.0],
                        [x + 28.0, y + 154.0],
                    ]),
                    Segment::line([x + 28.0, y + 154.0], [x + 12.0, y + 19.0]),
                ],
                [x + 12.0, y + 19.0, x + 207.0, y + 154.0],
                FillRule::NonZero,
            )?;
            let plan = ClipPlan::new(&[ClipPath::new(path)], size, dpi)?;
            let coverage = renderer.prepare_shader_vector_mask(&plan, [0; 2]).await?;
            Some(Arc::new(ManagedShaderMask::from_vector(&coverage)?))
        } else if index == 11 {
            Some(Arc::new(ManagedShaderMask::analytic(
                target,
                MaskUniforms {
                    coordinate0: [1.0 / dpi, 0.0, 0.0, 0.0],
                    coordinate1: [0.0, 1.0 / dpi, 0.0, 0.0],
                    bounds: [x + 38.0, y + 38.0, x + 180.0, y + 126.0],
                    radii_x: [24.0; 4],
                    radii_y: [24.0; 4],
                    options: [2.0, 0.8, 0.0, 0.0],
                },
            )?))
        } else {
            None
        };
        scene.draw_managed_shader_effect(ManagedShaderEffectDraw {
            parameters,
            quad,
            target,
            blend: if index == 7 {
                LayerBlend::Screen
            } else {
                LayerBlend::SrcOver
            },
            mask,
        })?;
        scene.pop_scope()?;
    }
    Ok(scene)
}
