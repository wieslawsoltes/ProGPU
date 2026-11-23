//! Authored 24-kind retained filter gallery shared by the native and browser hosts.
use progpu::{
    Error,
    geometry::AnalyticPrimitive,
    image::{ColorFilter, Image, ImageAlpha, ImageDraw, ImageSampling},
    image_filter::*,
    scene::{LayerBlend, Scene},
    vector::{Brush, Matrix4},
};
use std::sync::Arc;
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

pub async fn image_filter_scene() -> Result<Scene> {
    let mut pixels = Vec::with_capacity(64 * 64 * 4);
    for y in 0..64u32 {
        for x in 0..64u32 {
            pixels.extend_from_slice(&[
                (x * 4) as u8,
                (y * 4) as u8,
                if (x / 8 + y / 8) % 2 == 0 { 230 } else { 35 },
                if x > 5 && y > 5 && x < 59 && y < 59 {
                    255
                } else {
                    0
                },
            ]);
        }
    }
    let image = Image::rgba8([64, 64], ImageAlpha::Straight, pixels)?;
    let crop = Some(FilterRect::new(4.25, 3.5, 59.5, 61.25));
    let blur = ImageFilter::blur([2.0, 3.0], FilterTileMode::Clamp, None, None)?;
    let offset = ImageFilter::offset([7.25, -4.5], None, None)?;
    let filtered = ImageFilter::color_filter(ColorFilter::luma(), None, None)?;
    let source = FilterImageSource {
        image: image.clone().into(),
        color_space: None,
    };
    let mut picture = Scene::default();
    picture.draw_analytic(AnalyticPrimitive::rectangle(
        [7.0, 9.0, 50.0, 46.0],
        [0.15, 0.75, 0.95, 0.9],
    ))?;
    picture.draw_analytic(AnalyticPrimitive::rectangle(
        [18.5, 3.5, 24.0, 56.0],
        [1.0, 0.2, 0.35, 0.65],
    ))?;
    let picture = FilterPictureSource {
        scene: Arc::new(picture),
        target: FilterRect::new(1.25, 2.75, 62.0, 61.0),
        color_inventory: Arc::from([PictureColorEntry::Other]),
    };
    let light = FilterLight {
        source: FilterLightSource::Distant([0.3, -0.4, 1.0]),
        color: [250, 220, 165, 255],
        surface_scale: 3.0,
        constant: 0.7,
        shininess: 8.0,
    };
    let point = FilterLight {
        source: FilterLightSource::Point([20.0, 15.0, 35.0]),
        ..light
    };
    let spot = FilterLight {
        source: FilterLightSource::Spot {
            location: [16.0, 8.0, 42.0],
            target: [32.0, 32.0, 0.0],
            exponent: 3.0,
            cutoff: 45.0,
        },
        ..light
    };
    let mut matrix = Matrix4::IDENTITY;
    matrix.0[0][0] = 0.82;
    matrix.0[0][1] = 0.13;
    matrix.0[1][0] = -0.18;
    matrix.0[3][0] = 10.0;
    let filters = vec![
        blur.clone(),
        ImageFilter::compose(blur.clone(), offset.clone())?,
        ImageFilter::drop_shadow(
            FilterShadow {
                offset: [5.0, 4.0],
                sigma: [2.0, 2.5],
                color: [30, 150, 255, 200],
                shadow_only: false,
            },
            None,
            None,
        )?,
        ImageFilter::arithmetic(
            FilterArithmetic {
                coefficients: [0.0, 0.6, 0.5, 0.0],
                enforce_premultiplied: true,
            },
            Some(offset.clone()),
            None,
            None,
        )?,
        ImageFilter::blend(
            FilterBlender::Mode(LayerBlend::Difference),
            Some(offset.clone()),
            None,
            None,
        )?,
        filtered.clone(),
        ImageFilter::dilate([2.0, 3.0], None, None)?,
        ImageFilter::displacement(
            [FilterColorChannel::Red, FilterColorChannel::Green],
            13.0,
            ImageFilter::whole_image(source.clone())?,
            None,
            crop,
        )?,
        ImageFilter::distant_diffuse(light, None, None)?,
        ImageFilter::distant_specular(light, None, None)?,
        ImageFilter::erode([2.0, 2.0], None, None)?,
        ImageFilter::image(FilterImageData {
            source,
            source_rect: FilterRect::new(8.0, 5.0, 56.0, 60.0),
            destination: FilterRect::new(3.0, 10.0, 60.0, 55.0),
            sampling: FilterSampling::MITCHELL,
        })?,
        ImageFilter::magnifier(
            FilterMagnifier {
                lens: FilterRect::new(8.0, 8.0, 56.0, 56.0),
                zoom: 2.0,
                inset: 8.0,
                sampling: FilterSampling::LINEAR,
            },
            None,
            crop,
        )?
        .ok_or(Error::InvalidInput)?,
        ImageFilter::matrix(matrix, FilterSampling::LINEAR, None)?,
        ImageFilter::convolution(
            FilterConvolution::new(
                [3, 3],
                &[0.0, -1.0, 0.0, -1.0, 5.0, -1.0, 0.0, -1.0, 0.0],
                1.0,
                0.0,
                [1, 1],
                FilterTileMode::Mirror,
                false,
            )?,
            None,
            crop,
        )?,
        ImageFilter::merge(&[Some(offset.clone()), Some(blur), None], crop)?,
        offset,
        ImageFilter::shader(
            Some(Arc::new(FilterShaderSource::Brush(Arc::new(
                Brush::solid_rgba(0x26cc73cc),
            )))),
            false,
            crop,
        )?,
        ImageFilter::picture(picture)?,
        ImageFilter::point_diffuse(point, None, None)?,
        ImageFilter::point_specular(point, None, None)?,
        ImageFilter::spot_diffuse(spot, None, None)?,
        ImageFilter::spot_specular(spot, None, None)?,
        ImageFilter::tile(
            FilterRect::new(8.0, 8.0, 24.0, 24.0),
            FilterRect::new(2.25, 3.5, 62.0, 61.0),
            filtered,
        )?,
    ];
    let mut scene = Scene::default();
    for (index, filter) in filters.into_iter().enumerate() {
        let x = 8.0 + (index % 6) as f32 * 160.0;
        let y = 8.0 + (index / 6) as f32 * 144.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 152.0, 136.0],
            [0.1, 0.15, 0.23, 1.0],
        ))?;
        let mut draw = ImageDraw::new(image.clone(), [x + 8.0, y + 10.0, 136.0, 116.0]);
        draw.sampling = ImageSampling::Linear;
        scene.draw_image_filtered_image(ImageFilteredImageDraw::new(draw, filter))?;
    }
    Ok(scene)
}
