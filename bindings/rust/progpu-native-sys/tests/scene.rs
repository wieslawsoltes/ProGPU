//! A retained scene through the C ABI over the builder: recorded, built into a stream, sent once
//! (`progpu_native_engine_update_scene`) and rendered twice (`progpu_native_engine_render_scene`);
//! the second frame uploads nothing.

mod common;

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use std::ffi::c_void;

const WIDTH: u32 = 48;
const HEIGHT: u32 = 32;

#[test]
fn a_retained_scene_is_sent_once_and_rendered_again_without_uploads() {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the device.
    unsafe {
        // Record: a cleared target and a red rounded rectangle.
        let scene_id = 7u64;
        let builder = progpu_native_scene_builder_create(scene_id, 1);
        assert!(!builder.is_null());
        assert_eq!(1, progpu_native_scene_builder_clear_target(builder, progpu_native_color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 }));
        let mut brush = 0u32;
        assert_eq!(1, progpu_native_scene_builder_add_solid_brush(builder, progpu_native_color { r: 1.0, g: 0.0, b: 0.0, a: 1.0 }, 1.0, &mut brush));
        let primitive = progpu_native_analytic_primitive {
            kind: PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE as u32,
            flags: 0,
            x: 8.0,
            y: 8.0,
            width: 32.0,
            height: 16.0,
            corner_radius: 4.0,
            stroke_thickness: 0.0,
            color: progpu_native_color { r: 1.0, g: 0.0, b: 0.0, a: 1.0 },
            transform: progpu_native_scene_builder_identity_transform(),
        };
        let bounds = progpu_native_image_rect { x: 8.0, y: 8.0, width: 32.0, height: 16.0 };
        assert_eq!(
            1,
            progpu_native_scene_builder_draw_analytic(builder, &primitive, 1, &brush, 1, bounds, PROGPU_NATIVE_SCENE_NO_INDEX as u32),
            "draw_analytic: error {}",
            progpu_native_scene_builder_last_error(builder)
        );
        let size = progpu_native_scene_builder_required_stream_size(builder);
        let mut stream = vec![0u8; size];
        let mut written = 0usize;
        assert_eq!(1, progpu_native_scene_builder_build_into(builder, stream.as_mut_ptr() as *mut c_void, stream.len(), &mut written));
        assert_eq!(size, written);
        progpu_native_scene_builder_destroy(builder);

        let mut scene_metrics: progpu_native_scene_metrics = std::mem::zeroed();
        scene_metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
        let status = progpu_native_engine_update_scene(gpu.engine, stream.as_ptr() as *const c_void, stream.len(), &mut scene_metrics);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "update_scene: validation error {}", scene_metrics.validation_error);

        let (texture, view) = gpu.target(WIDTH, HEIGHT);
        let mut frame: progpu_native_scene_frame = std::mem::zeroed();
        frame.struct_size = std::mem::size_of::<progpu_native_scene_frame>() as u32;
        frame.width = WIDTH;
        frame.height = HEIGHT;
        frame.dpi_scale = 1.0;
        frame.target_view = view as usize;
        frame.clear_color = progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 };
        frame.scene_id = scene_id;
        frame.generation = 1;
        let render = |label: &str| {
            let mut metrics: progpu_native_scene_frame_metrics = std::mem::zeroed();
            metrics.struct_size = std::mem::size_of::<progpu_native_scene_frame_metrics>() as u32;
            let status = progpu_native_engine_render_scene(gpu.engine, &frame, &mut metrics);
            assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "render_scene ({label})");
            metrics
        };
        let first = render("first");
        let second = render("stable");
        eprintln!(
            "first frame: {} draws, {} vertex bytes; stable frame: {} draws, {} vertex bytes, {} texture bytes",
            first.draw_call_count, first.vertex_upload_bytes, second.draw_call_count, second.vertex_upload_bytes, second.texture_upload_bytes
        );
        assert_eq!(0, second.vertex_upload_bytes + second.index_upload_bytes + second.texture_upload_bytes + second.brush_upload_bytes);

        // The submission of the last frame: polled without waiting (what an owner of an external
        // image does before it recycles the texture), then waited for, then polled again (a
        // completed submission stays complete).
        let mut submission = 0u64;
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_get_last_submission(gpu.engine, &mut submission));
        assert_ne!(0, submission, "the engine has submitted its frames");
        let mut complete = 0u8;
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_poll_submission(gpu.engine, submission, 0, &mut complete));
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_poll_submission(gpu.engine, submission, 1, &mut complete));
        assert_eq!(1, complete, "a waited submission is complete");
        complete = 0;
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_poll_submission(gpu.engine, submission, 0, &mut complete));
        assert_eq!(1, complete, "a completed submission stays complete");

        let pixels = gpu.read_rgba8(texture, WIDTH, HEIGHT);
        let pixel = |x: u32, y: u32| {
            let index = ((y * WIDTH + x) * 4) as usize;
            [pixels[index], pixels[index + 1], pixels[index + 2], pixels[index + 3]]
        };
        assert_eq!([255, 255, 255, 255], pixel(2, 2), "the cleared target");
        assert_eq!([255, 0, 0, 255], pixel(24, 16), "the rounded rectangle");
    }
}
