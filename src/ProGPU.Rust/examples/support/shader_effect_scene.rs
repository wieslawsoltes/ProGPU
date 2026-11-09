//! Authored native bytecode gallery; final effects draw at actual parent samples.
use progpu::{
    geometry::{Affine, AnalyticPrimitive, Shape},
    image::{GpuImage, ImageDraw},
    mask::{ClipPath, ClipPlan},
    path::{ExecutionPreference, FillRule, Path, Segment},
    renderer::{Frame, Renderer},
    scene::{Scene, ShaderEffectDraw},
    shader_effect::{
        AffineFrameRecord, LegacyShaderRequest, SampleFrameRecord, SampleLattice, ShaderEffect,
        ShaderEffectEngine, ShaderFrame, ShaderProgram, ShaderSampling,
    },
};
use std::sync::Arc;
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

// Original authored bytecode fixture families from native shader pixel and
// derivative fixtures; no generated substitute shader or foreign implementation.
fn effect(three: bool, derivative: bool, coefficient: f32) -> Result<ShaderEffect> {
    let words: Vec<u32> = if derivative {
        vec![
            0xffff0200, 0x0200001f, 0x80000000, 0xb0030000, 0x0200001f, 0x90000000, 0xa00f0800,
            0x05000051, 0xa00f0002, 0, 0, 0, 0x3f800000, 0x03000042, 0x800f0000, 0xb0e40000,
            0xa0e40800, 0x02000001, 0x80070800, 0xa05c0000, 0x02000001, 0x80080800, 0xa0ff0002,
            0xffff,
        ]
    } else {
        vec![
            if three { 0xffff0300 } else { 0xffff0200 },
            0x0200001f,
            if three { 0x80000005 } else { 0x80000000 },
            if three { 0x90030000 } else { 0xb0030000 },
            0x0200001f,
            0x90000000,
            0xa00f0800,
            0x03000042,
            0x800f0000,
            if three { 0x90e40000 } else { 0xb0e40000 },
            0xa0e40800,
            0x03000005,
            0x800f0800,
            0x80e40000,
            0xa0e40000,
            0xffff,
        ]
    };
    let bytes: Arc<[u8]> = words
        .into_iter()
        .flat_map(u32::to_le_bytes)
        .collect::<Vec<_>>()
        .into();
    let mut effect = ShaderEffect::new(ShaderProgram::new(bytes, 0)?);
    effect.constants[0] = [coefficient; 4];
    effect.sampling = if three {
        ShaderSampling::Nearest
    } else {
        ShaderSampling::Linear
    };
    Ok(effect)
}
async fn pattern(renderer: &mut Renderer, size: [u32; 2], seed: usize) -> Result<GpuImage> {
    let w = size[0] as f32;
    let h = size[1] as f32;
    let mut scene = Scene::default();
    scene.draw_analytic(AnalyticPrimitive::rectangle(
        [0.0, 0.0, w, h],
        [0.12, 0.3, 0.75, 1.0],
    ))?;
    for i in 0..6 {
        let color = if (i + seed) % 2 == 0 {
            [1.0, 0.4, 0.15, 0.9]
        } else {
            [0.2, 0.95, 0.65, 0.75]
        };
        let mut shape =
            AnalyticPrimitive::rectangle([w * i as f32 / 6.0, h * 0.12, w / 4.0, h * 0.76], color);
        if (i + seed) % 3 == 0 {
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

pub async fn shader_effect_scene(
    renderer: &mut Renderer,
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    dpi: f32,
    size: [u32; 2],
) -> Result<Scene> {
    let mut scene = Scene::default();
    let mut legacy = ShaderEffectEngine::new(
        device,
        queue,
        wgpu::TextureFormat::Rgba8Unorm,
        ExecutionPreference::Automatic,
    )?;
    let target = SampleLattice {
        origin: [0; 2],
        size,
    };
    let ordinary_size = [(176.0 * dpi).ceil() as u32, (96.0 * dpi).ceil() as u32];
    let ordinary = pattern(renderer, ordinary_size, 0).await?;
    let alternate = pattern(renderer, [ordinary_size[0] + 11, ordinary_size[1] + 7], 1).await?;
    for index in 0..12 {
        let x = 12.25 + (index % 4) as f32 * 236.0;
        let y = 12.5 + (index / 4) as f32 * 186.0;
        let bounds = [x, y, 224.0, 172.0];
        scene.draw_analytic(AnalyticPrimitive::rectangle(bounds, [0.08, 0.1, 0.16, 1.0]))?;
        scene.push_clip_rect([x + 6.0, y + 6.0, 212.0, 160.0])?;
        let mut effect = effect(
            index % 2 != 0,
            index == 3 || index == 7,
            if index % 3 == 0 { 0.65 } else { 1.0 },
        )?;
        effect.revision = index as u32 + 1;
        if index < 4 {
            let prepared = legacy
                .prepare_legacy(LegacyShaderRequest {
                    effect: &effect,
                    input: &ordinary,
                    sampler: (index == 2).then_some(&alternate),
                    extent: ordinary_size,
                    derivative: (index == 3).then_some(0),
                })
                .await?;
            let mut encoder = device.create_command_encoder(&Default::default());
            legacy.encode(&mut encoder, &prepared)?;
            queue.submit([encoder.finish()]);
            scene.draw_image(ImageDraw::new(
                prepared.output().ok_or("missing legacy output")?.clone(),
                [x + 20.0, y + 28.0, 176.0, 96.0],
            ))?;
        } else {
            let offset = [(x + 20.0) * dpi, (y + 28.0) * dpi];
            let base = SampleFrameRecord {
                local: [0.0, 0.0, 176.0, 96.0],
                scale: [dpi; 2],
                offset,
                dpi: [f64::from(dpi); 2],
                capture_origin: [0; 2],
                capture_size: [0; 2],
                output_origin: [0; 2],
                output_size: [0; 2],
                quad: [0.0; 4],
                quad_offset: [0.0; 2],
                clip: [
                    ((x + 6.0) * dpi).ceil(),
                    ((y + 6.0) * dpi).ceil(),
                    ((x + 218.0) * dpi).floor(),
                    ((y + 166.0) * dpi).floor(),
                ],
                clip_antialias: 0,
                reserved: 0,
            };
            let frame = if index >= 8 {
                let mut affine = AffineFrameRecord {
                    placement: base,
                    source_cross: [0.16 * dpi, -0.10 * dpi],
                    quad_cross: [0.0; 2],
                };
                if index == 10 {
                    affine.placement.scale[0] = -dpi;
                    affine.placement.offset[0] = (x + 202.0) * dpi;
                }
                ShaderFrame::Affine(affine.complete()?)
            } else {
                ShaderFrame::Samples(base.complete()?)
            };
            let capture_size = match frame {
                ShaderFrame::Samples(record) => record.capture_size,
                ShaderFrame::Affine(record) => record.placement.capture_size,
                _ => unreachable!(),
            };
            let input = pattern(renderer, capture_size, index).await?;
            let vector_mask = if index == 6 || index == 11 {
                let path = Path::new(
                    vec![
                        Segment::line([x + 12.0, y + 16.0], [x + 205.0, y + 16.0]),
                        Segment::quadratic([
                            [x + 205.0, y + 16.0],
                            [x + 150.0, y + 150.0],
                            [x + 22.0, y + 150.0],
                        ]),
                        Segment::line([x + 22.0, y + 150.0], [x + 12.0, y + 16.0]),
                    ],
                    [x + 12.0, y + 16.0, x + 205.0, y + 150.0],
                    FillRule::NonZero,
                )?;
                let mut node = ClipPath::new(path);
                node.transform = Affine::IDENTITY;
                let plan = ClipPlan::new(&[node], size, dpi)?;
                Some(Arc::new(
                    renderer.prepare_shader_vector_mask(&plan, [0; 2]).await?,
                ))
            } else {
                None
            };
            scene.draw_shader_effect(ShaderEffectDraw {
                effect,
                frame,
                target,
                input,
                sampler: (index == 5 || index == 9).then(|| alternate.clone()),
                derivative: (index == 7 || index == 8).then_some(0),
                vector_mask,
            })?;
        }
        scene.pop_scope()?;
    }
    Ok(scene)
}
