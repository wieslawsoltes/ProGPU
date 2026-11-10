//! Original native image quad equations and per-patch source shader metadata.
use super::*;
use crate::native_scene::execution::{compose_affine as compose, snap_image_point};
use crate::native_scene::semantic::image_payload::{
    ImageOptions, image_patch_attributes, image_vertex_color,
};
use wide::f32x4;
pub(super) fn append(
    vertices: &mut Vec<VectorVertex>,
    image: &wire::ImageDraw,
    options: &ImageOptions<'_>,
    patch: Option<&wire::ImagePatch>,
    dpi: f32,
    explicit: bool,
) {
    let source = patch.map_or(image.source_rect, |p| p.source_rect);
    let destination = patch.map_or(image.destination_rect, |p| p.destination_rect);
    let transform = patch.map_or(image.transform, |p| compose(p.transform, image.transform));
    let (color, kind, blend, opacity) = if let Some(patch) = patch {
        let a = image_patch_attributes(image, patch, options.effect.is_some());
        (a.color, a.kind, a.color_blend_mode, a.opacity)
    } else {
        (
            image_vertex_color(image, options.effect.is_some()),
            0.0,
            ((image.flags & SCENE_IMAGE_ADDRESS_U_MASK) >> SCENE_IMAGE_ADDRESS_U_SHIFT) as f32,
            ((image.flags & SCENE_IMAGE_ADDRESS_V_MASK) >> SCENE_IMAGE_ADDRESS_V_SHIFT) as f32,
        )
    };
    let samples = patch.is_none_or(|p| p.kind != SCENE_IMAGE_PATCH_FIXED_COLOR);
    let x = [destination.x, destination.x + destination.width];
    let y = [destination.y, destination.y + destination.height];
    let u = if samples {
        [
            source.x / image.image_width as f32,
            (source.x + source.width) / image.image_width as f32,
        ]
    } else {
        [0.0, 1.0]
    };
    let v = if samples {
        [
            source.y / image.image_height as f32,
            (source.y + source.height) / image.image_height as f32,
        ]
    } else {
        [0.0, 1.0]
    };
    let base = match (image.sampling, explicit) {
        (IMAGE_SAMPLING_FANT, true) => -256.0,
        (IMAGE_SAMPLING_FANT, false) => -32.0,
        (IMAGE_SAMPLING_NEAREST, true) => -128.0,
        (IMAGE_SAMPLING_LINEAR, true) => -64.0,
        _ => 0.0,
    };
    let cubic = image.sampling == IMAGE_SAMPLING_CUBIC;
    let shape_size = if cubic {
        [options.cubic_b, options.cubic_c]
    } else {
        [base, 0.5]
    };
    const CORNERS: [[usize; 2]; 4] = [[0, 0], [1, 0], [1, 1], [0, 1]];
    const TRIANGLES: [usize; 6] = [0, 1, 2, 0, 2, 3];
    // Four independent corners preserve each original multiply/add, without
    // fused arithmetic or reciprocal replacement. Repeated triangle vertices
    // select the original completed corner rather than recomputing geometry.
    let xs = f32x4::from([x[0], x[1], x[1], x[0]]);
    let ys = f32x4::from([y[0], y[0], y[1], y[1]]);
    let px = (xs * f32x4::splat(transform.m11)
        + ys * f32x4::splat(transform.m21)
        + f32x4::splat(transform.m31))
    .to_array();
    let py = (xs * f32x4::splat(transform.m12)
        + ys * f32x4::splat(transform.m22)
        + f32x4::splat(transform.m32))
    .to_array();
    for index in 0..if patch.is_some() { 6 } else { 4 } {
        let corner = if patch.is_some() {
            TRIANGLES[index]
        } else {
            index
        };
        let [cx, cy] = CORNERS[corner];
        let mut position = [px[corner], py[corner]];
        if image.flags & SCENE_IMAGE_SNAP_TO_PIXELS != 0 {
            let mut point = wire::Point {
                x: position[0],
                y: position[1],
            };
            snap_image_point(&mut point, dpi);
            position = [point.x, point.y];
        }
        vertices.push(VectorVertex {
            position,
            color,
            texture_coordinate: [u[cx], v[cy]],
            brush_index: kind,
            shape_size,
            corner_radius: blend,
            stroke_thickness: opacity,
            shape_type: 0.0,
        });
    }
}
