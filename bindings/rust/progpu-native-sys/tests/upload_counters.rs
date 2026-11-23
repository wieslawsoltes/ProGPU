//! The counters of page uploads: a frame that captures its stages counts what it uploaded and
//! how much of that only moved, and other frames count nothing.

mod common;

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use std::ffi::c_void;

const SIZE: u32 = 256;
const SCENE: u64 = 71;

/// A scene of rectangles of one brush, with `extra` more at the start of the page.
unsafe fn scene(generation: u64, extra: usize) -> Vec<u8> {
    let builder = progpu_native_scene_builder_create(SCENE, generation);
    let state = progpu_native_scene_builder_identity_state();
    let mut state_index = 0;
    assert_ne!(0, progpu_native_scene_builder_add_state(builder, &state, &mut state_index));
    let color = progpu_native_color { r: 0.3, g: 0.6, b: 0.2, a: 1.0 };
    let mut brush = 0;
    assert_ne!(0, progpu_native_scene_builder_add_solid_brush(builder, color, 1.0, &mut brush));
    let places = (0..extra).map(|index| (index as f32 * 3.0, 250.0));
    let places = places.chain((0..240).map(|index| ((index % 24) as f32 * 10.0, (index / 24) as f32 * 10.0)));
    for (x, y) in places {
        let primitive = progpu_native_analytic_primitive {
            kind: PROGPU_NATIVE_PRIMITIVE_RECTANGLE as u32,
            flags: 0,
            x,
            y,
            width: 6.0,
            height: 6.0,
            corner_radius: 0.0,
            stroke_thickness: 0.0,
            color: progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 },
            transform: progpu_native_scene_builder_identity_transform(),
        };
        let bounds = progpu_native_image_rect { x, y, width: 6.0, height: 6.0 };
        assert_ne!(0, progpu_native_scene_builder_draw_analytic(builder, &primitive, 1, &brush, 1, bounds, state_index));
    }
    let mut stream = vec![0u8; progpu_native_scene_builder_required_stream_size(builder)];
    let mut written = 0;
    assert_ne!(0, progpu_native_scene_builder_build_into(builder, stream.as_mut_ptr() as *mut c_void, stream.len(), &mut written));
    stream.truncate(written);
    progpu_native_scene_builder_destroy(builder);
    stream
}

unsafe fn render(gpu: &common::Gpu, view: WGPUTextureView, generation: u64, extra: usize, flags: u32) {
    let stream = scene(generation, extra);
    let mut scene_metrics: progpu_native_scene_metrics = std::mem::zeroed();
    scene_metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
    let status = progpu_native_engine_update_scene(gpu.engine, stream.as_ptr() as *const c_void, stream.len(), &mut scene_metrics);
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
    let mut frame: progpu_native_scene_frame = std::mem::zeroed();
    frame.struct_size = std::mem::size_of::<progpu_native_scene_frame>() as u32;
    frame.width = SIZE;
    frame.height = SIZE;
    frame.dpi_scale = 1.0;
    frame.target_view = view as usize;
    frame.clear_color = progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 };
    frame.scene_id = SCENE;
    frame.generation = generation;
    frame.flags = flags;
    let mut metrics: progpu_native_scene_frame_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_frame_metrics>() as u32;
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_render_scene(gpu.engine, &frame, &mut metrics));
}

unsafe fn take(gpu: &common::Gpu) -> progpu_native_upload_counters {
    let mut counters = progpu_native_upload_counters {
        struct_size: std::mem::size_of::<progpu_native_upload_counters>() as u32,
        ..Default::default()
    };
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_take_upload_counters(gpu.engine, &mut counters));
    counters
}

#[test]
fn an_insertion_counts_as_shifted_and_uncounted_frames_add_nothing() {
    let Some(gpu) = common::Gpu::new() else { return };
    let capture = PROGPU_NATIVE_SCENE_FRAME_CAPTURE_CPU_STAGES as u32;
    // SAFETY: the calls of the C ABIs with the handles of the device; the spans outlive them.
    unsafe {
        let (_texture, view) = gpu.target(SIZE, SIZE);
        render(&gpu, view, 1, 0, 0);
        render(&gpu, view, 2, 2, 0);
        assert_eq!(0, take(&gpu).uploaded_bytes, "a frame that does not capture its stages counts nothing");

        render(&gpu, view, 3, 0, capture);
        take(&gpu);
        render(&gpu, view, 4, 3, capture);
        let counters = take(&gpu);
        assert!(counters.uploaded_bytes > 0);
        assert!(counters.shifted_bytes > counters.uploaded_bytes / 2, "{counters:?}");
        assert!(counters.shifted_bytes <= counters.uploaded_bytes);
        assert!(counters.shifted_nanoseconds <= counters.upload_nanoseconds);
        assert!(counters.counting_in_encode_nanoseconds <= counters.counting_nanoseconds);
        assert_eq!(0, take(&gpu).uploaded_bytes, "taking the counters starts them again");

        let mut short = progpu_native_upload_counters { struct_size: 8, ..Default::default() };
        assert_eq!(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            progpu_native_engine_take_upload_counters(gpu.engine, &mut short)
        );
    }
}
