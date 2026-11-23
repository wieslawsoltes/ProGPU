//! A page compiled again writes only the blocks that changed. Whatever was written, a frame
//! reads back the bytes of an engine that has seen nothing before: a colour changed, rectangles
//! added, rectangles taken away.

mod common;

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use std::ffi::c_void;

const WIDTH: u32 = 128;
const HEIGHT: u32 = 96;

fn color_of(index: usize) -> progpu_native_color {
    progpu_native_color {
        r: (index % 7) as f32 / 6.0,
        g: (index % 5) as f32 / 4.0,
        b: (index % 3) as f32 / 2.0,
        a: 1.0,
    }
}

/// A grid of `count` rectangles; the rectangle `changed`, if any, is white.
unsafe fn scene(scene_id: u64, generation: u64, count: usize, changed: Option<usize>) -> Vec<u8> {
    let builder = progpu_native_scene_builder_create(scene_id, generation);
    assert!(!builder.is_null());
    let state = progpu_native_scene_builder_identity_state();
    let mut state_index = 0;
    assert_ne!(0, progpu_native_scene_builder_add_state(builder, &state, &mut state_index));
    let white = progpu_native_color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 };
    for index in 0..count {
        let (x, y) = ((index % 32) as f32 * 4.0, (index / 32) as f32 * 4.0);
        let color = if changed == Some(index) { white } else { color_of(index) };
        let mut brush = 0;
        assert_ne!(0, progpu_native_scene_builder_add_solid_brush(builder, color, 1.0, &mut brush));
        let primitive = progpu_native_analytic_primitive {
            kind: PROGPU_NATIVE_PRIMITIVE_RECTANGLE as u32,
            flags: 0,
            x,
            y,
            width: 3.0,
            height: 3.0,
            corner_radius: 0.0,
            stroke_thickness: 0.0,
            color: progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 },
            transform: progpu_native_scene_builder_identity_transform(),
        };
        let bounds = progpu_native_image_rect { x, y, width: 3.0, height: 3.0 };
        assert_ne!(0, progpu_native_scene_builder_draw_analytic(builder, &primitive, 1, &brush, 1, bounds, state_index));
    }
    let mut stream = vec![0u8; progpu_native_scene_builder_required_stream_size(builder)];
    let mut written = 0;
    assert_ne!(0, progpu_native_scene_builder_build_into(builder, stream.as_mut_ptr() as *mut c_void, stream.len(), &mut written));
    stream.truncate(written);
    progpu_native_scene_builder_destroy(builder);
    stream
}

/// The pixels of a frame of the scene and the vertex bytes it uploaded.
unsafe fn draw(gpu: &common::Gpu, stream: &[u8], scene_id: u64, generation: u64) -> (Vec<u8>, u64) {
    let mut scene_metrics: progpu_native_scene_metrics = std::mem::zeroed();
    scene_metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
    let status = progpu_native_engine_update_scene(gpu.engine, stream.as_ptr() as *const c_void, stream.len(), &mut scene_metrics);
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "update_scene of generation {generation}");
    let (texture, view) = gpu.target(WIDTH, HEIGHT);
    let mut frame: progpu_native_scene_frame = std::mem::zeroed();
    frame.struct_size = std::mem::size_of::<progpu_native_scene_frame>() as u32;
    frame.width = WIDTH;
    frame.height = HEIGHT;
    frame.dpi_scale = 1.0;
    frame.target_view = view as usize;
    frame.clear_color = progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 };
    frame.scene_id = scene_id;
    frame.generation = generation;
    let mut metrics: progpu_native_scene_frame_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_frame_metrics>() as u32;
    let status = progpu_native_engine_render_scene(gpu.engine, &frame, &mut metrics);
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "render_scene of generation {generation}");
    (gpu.read_rgba8(texture, WIDTH, HEIGHT), metrics.vertex_upload_bytes)
}

#[test]
fn a_page_compiled_again_writes_what_changed_and_reads_back_like_a_new_engine() {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the device.
    unsafe {
        let first = scene(71, 1, 400, None);
        let (_, full) = draw(&gpu, &first, 71, 1);
        assert!(full >= 400 * 4 * 56, "the first frame writes every vertex: {full}");

        // One colour: a few blocks of the page (the rectangle, and those whose brush moved).
        let changed = scene(71, 2, 400, Some(37));
        let (pixels, uploaded) = draw(&gpu, &changed, 71, 2);
        assert!(uploaded > 0 && uploaded <= full / 16, "one rectangle changed, {uploaded} bytes of {full} written");
        let fresh = common::Gpu::new().expect("a second engine");
        let (expected, _) = draw(&fresh, &scene(72, 1, 400, Some(37)), 72, 1);
        assert!(pixels == expected, "a changed colour reads back like a new engine");

        // Fewer rectangles, then more than ever: what the buffer held past the end does not count.
        let (pixels, _) = draw(&gpu, &scene(71, 3, 250, None), 71, 3);
        let fresh = common::Gpu::new().expect("a third engine");
        let (expected, _) = draw(&fresh, &scene(73, 1, 250, None), 73, 1);
        assert!(pixels == expected, "fewer rectangles read back like a new engine");
        let (pixels, _) = draw(&gpu, &scene(71, 4, 700, Some(600)), 71, 4);
        let fresh = common::Gpu::new().expect("a fourth engine");
        let (expected, _) = draw(&fresh, &scene(74, 1, 700, Some(600)), 74, 1);
        assert!(pixels == expected, "more rectangles read back like a new engine");
    }
}
