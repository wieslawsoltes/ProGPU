//! The analytic shadow primitive (`PROGPU_NATIVE_PRIMITIVE_SHADOW`) through a retained scene,
//! against exact references: the blurred coverage of a square-cornered box is the product of two
//! normal CDF differences; that of a rounded box is a numerical 2D Gaussian convolution.

mod common;

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use std::ffi::c_void;

const SIZE: u32 = 128;

/// The error function, to 1.2e-7 (Numerical Recipes' erfc Chebyshev fit).
fn erf(x: f64) -> f64 {
    let z = x.abs();
    let t = 1.0 / (1.0 + 0.5 * z);
    let r = t
        * (-z * z - 1.26551223
            + t * (1.00002368
                + t * (0.37409196
                    + t * (0.09678418
                        + t * (-0.18628806
                            + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))))
            .exp();
    if x >= 0.0 {
        1.0 - r
    } else {
        r - 1.0
    }
}

fn phi(x: f64) -> f64 {
    0.5 * (1.0 + erf(x / std::f64::consts::SQRT_2))
}

/// The blurred coverage of the box [left, right] x [top, bottom] at (x, y).
fn box_reference(x: f64, y: f64, left: f64, top: f64, right: f64, bottom: f64, sigma: f64) -> f64 {
    (phi((right - x) / sigma) - phi((left - x) / sigma)) * (phi((bottom - y) / sigma) - phi((top - y) / sigma))
}

fn inside_rounded(x: f64, y: f64, left: f64, top: f64, right: f64, bottom: f64, radius: f64) -> bool {
    let cx = x.clamp(left + radius, right - radius);
    let cy = y.clamp(top + radius, bottom - radius);
    x >= left && x <= right && y >= top && y <= bottom && (x - cx).powi(2) + (y - cy).powi(2) <= radius * radius
}

/// The blurred coverage of a rounded box by numerical convolution (step 0.125 over +-4 sigma).
fn rounded_reference(x: f64, y: f64, left: f64, top: f64, right: f64, bottom: f64, radius: f64, sigma: f64) -> f64 {
    let step = 0.125;
    let reach = 4.0 * sigma;
    let mut sum = 0.0;
    let mut weight = 0.0;
    let mut dy = -reach;
    while dy <= reach {
        let mut dx = -reach;
        while dx <= reach {
            let g = (-(dx * dx + dy * dy) / (2.0 * sigma * sigma)).exp();
            weight += g;
            if inside_rounded(x + dx, y + dy, left, top, right, bottom, radius) {
                sum += g;
            }
            dx += step;
        }
        dy += step;
    }
    sum / weight
}

/// Renders black shadows on white and returns the RGBA8 pixels (tightly packed rows).
unsafe fn render(gpu: &common::Gpu, shadows: &[progpu_native_analytic_primitive]) -> Vec<u8> {
    let scene_id = 11u64;
    let builder = progpu_native_scene_builder_create(scene_id, 1);
    assert_eq!(1, progpu_native_scene_builder_clear_target(builder, progpu_native_color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 }));
    let mut brush = 0u32;
    assert_eq!(1, progpu_native_scene_builder_add_solid_brush(builder, progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 }, 1.0, &mut brush));
    let brushes = vec![brush; shadows.len()];
    let bounds = progpu_native_image_rect { x: 0.0, y: 0.0, width: SIZE as f32, height: SIZE as f32 };
    assert_eq!(
        1,
        progpu_native_scene_builder_draw_analytic(
            builder,
            shadows.as_ptr(),
            shadows.len(),
            brushes.as_ptr(),
            brushes.len(),
            bounds,
            PROGPU_NATIVE_SCENE_NO_INDEX as u32
        ),
        "draw_analytic: error {}",
        progpu_native_scene_builder_last_error(builder)
    );
    let size = progpu_native_scene_builder_required_stream_size(builder);
    let mut stream = vec![0u8; size];
    let mut written = 0usize;
    assert_eq!(1, progpu_native_scene_builder_build_into(builder, stream.as_mut_ptr() as *mut c_void, stream.len(), &mut written));
    progpu_native_scene_builder_destroy(builder);
    let mut scene_metrics: progpu_native_scene_metrics = std::mem::zeroed();
    scene_metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
    let status = progpu_native_engine_update_scene(gpu.engine, stream.as_ptr() as *const c_void, written, &mut scene_metrics);
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "update_scene: validation error {}", scene_metrics.validation_error);
    let (texture, view) = gpu.target(SIZE, SIZE);
    let mut frame: progpu_native_scene_frame = std::mem::zeroed();
    frame.struct_size = std::mem::size_of::<progpu_native_scene_frame>() as u32;
    frame.width = SIZE;
    frame.height = SIZE;
    frame.dpi_scale = 1.0;
    frame.target_view = view as usize;
    frame.scene_id = scene_id;
    frame.generation = 1;
    let mut metrics: progpu_native_scene_frame_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_frame_metrics>() as u32;
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_render_scene(gpu.engine, &frame, &mut metrics));
    eprintln!("{} shadows: {} draws", shadows.len(), metrics.draw_call_count);
    gpu.read_rgba8(texture, SIZE, SIZE)
}

fn shadow(x: f32, y: f32, width: f32, height: f32, radius: f32, sigma: f32) -> progpu_native_analytic_primitive {
    progpu_native_analytic_primitive {
        kind: PROGPU_NATIVE_PRIMITIVE_SHADOW as u32,
        flags: 0,
        x,
        y,
        width,
        height,
        corner_radius: radius,
        stroke_thickness: sigma,
        color: progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 },
        // SAFETY: a pure function of the ABI.
        transform: unsafe { progpu_native_scene_builder_identity_transform() },
    }
}

#[test]
fn analytic_shadows_match_the_gaussian_blur_of_their_box() {
    let Some(gpu) = common::Gpu::new() else { return };
    // A square-cornered box (exact reference) and a rounded one, apart.
    let (left, top, right, bottom, sigma) = (24.0, 24.0, 64.0, 56.0, 5.0);
    let (rl, rt, rr, rb, radius, rsigma) = (80.0, 72.0, 116.0, 116.0, 10.0, 4.0);
    let shadows = [
        shadow(left as f32, top as f32, (right - left) as f32, (bottom - top) as f32, 0.0, sigma as f32),
        shadow(rl as f32, rt as f32, (rr - rl) as f32, (rb - rt) as f32, radius as f32, rsigma as f32),
    ];
    // SAFETY: the device and the engine are alive for the call.
    let data = unsafe { render(&gpu, &shadows) };
    let coverage = |x: u32, y: u32| 1.0 - data[((y * SIZE + x) * 4) as usize] as f64 / 255.0;
    let mut worst: f64 = 0.0;
    // Square corners: every pixel of a window around the box.
    for y in 8..72 {
        for x in 8..76 {
            let expected = box_reference(x as f64 + 0.5, y as f64 + 0.5, left, top, right, bottom, sigma);
            worst = worst.max((coverage(x, y) - expected).abs());
        }
    }
    assert!(worst <= 4.0 / 255.0, "square-cornered shadow differs by {worst} from its exact blur");
    eprintln!("square-cornered shadow: worst difference {:.4}", worst);
    // Rounded corners: the corner regions, where the closed form approximates.
    let mut worst_rounded: f64 = 0.0;
    for &(x, y) in &[(80u32, 72u32), (84, 76), (78, 70), (88, 74), (112, 112), (118, 118), (98, 70), (78, 94), (90, 90)] {
        let expected = rounded_reference(x as f64 + 0.5, y as f64 + 0.5, rl, rt, rr, rb, radius, rsigma);
        worst_rounded = worst_rounded.max((coverage(x, y) - expected).abs());
    }
    eprintln!("rounded shadow: worst difference {:.4}", worst_rounded);
    assert!(worst_rounded <= 0.03, "rounded shadow differs by {worst_rounded} from its numerical blur");
}
