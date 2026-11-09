//! Authored image integration gallery; no external assets or pixel parity claims.
use progpu::{
    geometry::{Affine, AnalyticPrimitive},
    image::{
        Image, ImageAddress, ImageAlpha, ImageBrush, ImageColorBlend, ImageDraw, ImagePatch,
        ImagePatchKind, ImageQuad, ImageSampling, TileImageDraw, TileImageSampling,
    },
    scene::Scene,
};
use std::sync::Arc;
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

pub fn checker_image() -> Result<Image> {
    // Tiny authored fixture: retain colored transparent pixels to distinguish
    // source alpha from opacity and premultiplied normalization.
    let mut pixels = Vec::with_capacity(16 * 16 * 4);
    for y in 0..16 {
        for x in 0..16 {
            let even = (x / 2 + y / 2) % 2 == 0;
            pixels.extend([
                if even { 240 } else { 30 },
                (y * 16) as u8,
                (x * 16) as u8,
                if x < 2 { 0 } else { 220 },
            ]);
        }
    }
    Ok(Image::rgba8([16, 16], ImageAlpha::Straight, pixels)?)
}
pub fn image_scene() -> Result<Scene> {
    let mut scene = Scene::default();
    let source = checker_image()?;
    let modes = [
        ImageSampling::Nearest,
        ImageSampling::Linear,
        ImageSampling::Cubic,
        ImageSampling::Fant,
        ImageSampling::LinearMipmap,
        ImageSampling::MagLinearMinLinearMipNearest,
        ImageSampling::MagLinearMinNearestMipLinear,
        ImageSampling::MagLinearMinNearestMipNearest,
        ImageSampling::MagNearestMinLinearMipLinear,
        ImageSampling::MagNearestMinLinearMipNearest,
        ImageSampling::MagNearestMinNearestMipLinear,
    ];
    for (index, mode) in modes.into_iter().enumerate() {
        let x = 20.0 + (index % 6) as f32 * 153.0;
        let y = 20.0 + (index / 6) as f32 * 155.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x - 4.0, y - 4.0, 144.0, 144.0],
            [0.18, 0.2, 0.24, 1.0],
        ))?;
        let mut draw = ImageDraw::new(source.clone(), [x, y, 136.0, 136.0]);
        draw.sampling = mode;
        if mode.uses_mipmaps() {
            draw.source = [-32.0, -32.0, 128.0, 128.0];
            draw.extended_source = true;
            draw.address = [ImageAddress::Repeat; 2];
        }
        if mode == ImageSampling::LinearMipmap {
            draw.max_anisotropy = 8;
        }
        scene.draw_image(draw)?;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x + 14.0, y + 50.0, 100.0, 10.0],
            [1.0, 0.65, 0.1, 0.45],
        ))?;
    }
    let mut patches = ImageDraw::new(source.clone(), [20.0, 350.0, 330.0, 100.0]);
    patches.patches = Some(Arc::from([
        ImagePatch {
            source: [0.0, 0.0, 16.0, 16.0],
            destination: [20.0, 350.0, 100.0, 100.0],
            transform: Affine::IDENTITY,
            kind: ImagePatchKind::Texture,
        },
        ImagePatch {
            source: [0.0; 4],
            destination: [130.0, 350.0, 100.0, 100.0],
            transform: Affine::IDENTITY,
            kind: ImagePatchKind::FixedColor([0.2, 0.8, 0.6, 0.7]),
        },
        ImagePatch {
            source: [0.0, 0.0, 16.0, 16.0],
            destination: [240.0, 350.0, 100.0, 100.0],
            transform: Affine::IDENTITY,
            kind: ImagePatchKind::AtlasColor {
                color: [0.8, 0.2, 0.5, 0.7],
                blend: ImageColorBlend::Screen,
            },
        },
    ]));
    scene.draw_image(patches)?;
    let mut projective = ImageDraw::new(source.clone(), [370.0, 350.0, 180.0, 100.0]);
    projective.quad = Some(ImageQuad {
        points: [
            [380.0, 365.0],
            [530.0, 340.0],
            [560.0, 450.0],
            [365.0, 430.0],
        ],
        weights: [1.0, 0.7, 1.2, 1.0],
    });
    projective.snap_to_pixels = true;
    scene.draw_image(projective)?;
    let mut mapping = ImageDraw::new(source, [590.0, 350.0, 55.0, 55.0]);
    mapping.extended_source = true;
    mapping.address = [ImageAddress::Repeat, ImageAddress::MirrorRepeat];
    scene.draw_image(
        ImageBrush {
            draw: mapping,
            extend_to_fill_bounds: true,
        }
        .for_fill([590.0, 350.0, 330.0, 100.0])?,
    )?;
    let mut tile = vec![0u8; 8 * 8 * 4];
    for y in 0..5 {
        for x in 0..5 {
            let i = (y * 8 + x) * 4;
            tile[i..i + 4].copy_from_slice(&[x as u8 * 40, y as u8 * 40, 160, 200]);
        }
    }
    let tile = Image::rgba8([8, 8], ImageAlpha::Premultiplied, tile)?;
    for (index, sampling) in [
        TileImageSampling::Nearest,
        TileImageSampling::Linear,
        TileImageSampling::Fant,
    ]
    .into_iter()
    .enumerate()
    {
        let x = 20.0 + index as f32 * 306.0;
        scene.draw_tile_image(TileImageDraw {
            image: tile.clone().into(),
            occupied: [5, 5],
            output: [x, 480.0, 288.0, 120.0],
            output_to_tile: Affine([1.0 / 56.0, 0.0, 0.0, 1.0 / 56.0, -x / 56.0, -480.0 / 56.0]),
            address: [ImageAddress::Repeat, ImageAddress::MirrorRepeat],
            sampling,
            opacity: 0.9,
        })?;
    }
    Ok(scene)
}
