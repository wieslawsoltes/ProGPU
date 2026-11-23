//! A path resource drawn again under another state and brush: the same pixels as the path sent
//! twice, with the segments in the stream once.

mod common;

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use std::ffi::c_void;

const SIZE: u32 = 96;

fn segment(kind: u32, x0: f32, y0: f32, x1: f32, y1: f32) -> progpu_native_path_segment {
    // SAFETY: an all-zero segment is a plain value.
    let mut value: progpu_native_path_segment = unsafe { std::mem::zeroed() };
    value.kind = kind;
    value.p0 = progpu_native_point { x: x0, y: y0 };
    value.p1 = progpu_native_point { x: x1, y: y1 };
    value
}

fn triangle() -> Vec<progpu_native_path_segment> {
    let line = PROGPU_NATIVE_PATH_SEGMENT_LINE as u32;
    vec![
        segment(line, 4.0, 4.0, 36.0, 4.0),
        segment(line, 36.0, 4.0, 20.0, 36.0),
        segment(line, 20.0, 36.0, 4.0, 4.0),
    ]
}

unsafe fn scene(id: u64, reuse: bool) -> (Vec<u8>, usize) {
    let builder = progpu_native_scene_builder_create(id, 1);
    let mut first_state = progpu_native_scene_builder_identity_state();
    let mut second_state = first_state;
    second_state.transform.m31 = 48.0;
    second_state.transform.m32 = 48.0;
    first_state.opacity = 1.0;
    let (mut first, mut second) = (0, 0);
    assert_ne!(0, progpu_native_scene_builder_add_state(builder, &first_state, &mut first));
    assert_ne!(0, progpu_native_scene_builder_add_state(builder, &second_state, &mut second));
    let (mut red, mut blue) = (0, 0);
    let color = |r, b| progpu_native_color { r, g: 0.0, b, a: 1.0 };
    assert_ne!(0, progpu_native_scene_builder_add_solid_brush(builder, color(1.0, 0.0), 1.0, &mut red));
    assert_ne!(0, progpu_native_scene_builder_add_solid_brush(builder, color(0.0, 1.0), 1.0, &mut blue));
    let segments = triangle();
    let bounds = progpu_native_image_rect { x: 4.0, y: 4.0, width: 32.0, height: 32.0 };
    let mut path: progpu_native_scene_path_fill = std::mem::zeroed();
    path.segment_count = segments.len() as u64;
    path.min_x = 4.0;
    path.min_y = 4.0;
    path.max_x = 36.0;
    path.max_y = 36.0;
    path.color = color(0.0, 0.0);
    path.color.a = 1.0;
    path.transform = progpu_native_scene_builder_identity_transform();
    path.fill_rule = PROGPU_NATIVE_FILL_RULE_NON_ZERO as u32;
    path.sample_grid = 4;
    let draw = |brush: &u32, state| {
        assert_ne!(0, progpu_native_scene_builder_draw_paths(builder, &path, 1, segments.as_ptr(), segments.len(), brush, 1, bounds, state));
    };
    draw(&red, first);
    if reuse {
        let mut resource = u32::MAX;
        assert_ne!(0, progpu_native_scene_builder_last_path_resource(builder, &mut resource));
        assert_ne!(0, progpu_native_scene_builder_draw_path_resource(builder, resource, &blue, 1, bounds, second));
        // A resource that is not there, and a brush list of the wrong length, are refused.
        assert_eq!(0, progpu_native_scene_builder_draw_path_resource(builder, 9999, &blue, 1, bounds, second));
        let two = [blue, blue];
        assert_eq!(0, progpu_native_scene_builder_draw_path_resource(builder, resource, two.as_ptr(), 2, bounds, second));
    } else {
        draw(&blue, second);
    }
    let mut stream = vec![0u8; progpu_native_scene_builder_required_stream_size(builder)];
    let mut written = 0;
    assert_ne!(0, progpu_native_scene_builder_build_into(builder, stream.as_mut_ptr() as *mut c_void, stream.len(), &mut written));
    stream.truncate(written);
    progpu_native_scene_builder_destroy(builder);
    let size = stream.len();
    (stream, size)
}

unsafe fn pixels(gpu: &common::Gpu, id: u64, stream: &[u8]) -> Vec<u8> {
    let mut metrics: progpu_native_scene_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
    assert_eq!(
        PROGPU_NATIVE_STATUS_SUCCESS,
        progpu_native_engine_update_scene(gpu.engine, stream.as_ptr() as *const c_void, stream.len(), &mut metrics)
    );
    let (texture, view) = gpu.target(SIZE, SIZE);
    let mut frame: progpu_native_scene_frame = std::mem::zeroed();
    frame.struct_size = std::mem::size_of::<progpu_native_scene_frame>() as u32;
    frame.width = SIZE;
    frame.height = SIZE;
    frame.dpi_scale = 1.0;
    frame.target_view = view as usize;
    frame.clear_color = progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 };
    frame.scene_id = id;
    frame.generation = 1;
    let mut frame_metrics: progpu_native_scene_frame_metrics = std::mem::zeroed();
    frame_metrics.struct_size = std::mem::size_of::<progpu_native_scene_frame_metrics>() as u32;
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_render_scene(gpu.engine, &frame, &mut frame_metrics));
    gpu.read_rgba8(texture, SIZE, SIZE)
}

#[test]
fn a_path_drawn_again_reads_back_like_the_path_sent_twice() {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the device; the spans outlive them.
    unsafe {
        let (twice, twice_size) = scene(61, false);
        let (again, again_size) = scene(62, true);
        assert!(again_size < twice_size, "{again_size} against {twice_size}");
        let expected = pixels(&gpu, 61, &twice);
        let actual = pixels(&gpu, 62, &again);
        assert!(expected == actual);
        let at = |x: usize, y: usize| &actual[(y * SIZE as usize + x) * 4..(y * SIZE as usize + x) * 4 + 3];
        assert_eq!(&[255, 0, 0], at(20, 12));
        assert_eq!(&[0, 0, 255], at(68, 60));
    }
}
