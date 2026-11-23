//! The next generation of a scene sent as the bytes that differ: it reads back like the whole
//! stream, and a delta that is not valid leaves the retained scene as it was.

mod common;

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use std::ffi::c_void;

const WIDTH: u32 = 64;
const HEIGHT: u32 = 48;

unsafe fn scene(scene_id: u64, generation: u64, rectangles: usize, red: f32) -> Vec<u8> {
    let builder = progpu_native_scene_builder_create(scene_id, generation);
    let state = progpu_native_scene_builder_identity_state();
    let mut state_index = 0;
    assert_ne!(0, progpu_native_scene_builder_add_state(builder, &state, &mut state_index));
    for index in 0..rectangles {
        let x = (index % 8) as f32 * 8.0;
        let y = (index / 8) as f32 * 8.0;
        let color = progpu_native_color { r: red, g: index as f32 / 64.0, b: 0.5, a: 1.0 };
        let mut brush = 0;
        assert_ne!(0, progpu_native_scene_builder_add_solid_brush(builder, color, 1.0, &mut brush));
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

/// The ranges of `next` that differ from `previous`, with their bytes.
fn diff(previous: &[u8], next: &[u8]) -> (Vec<progpu_native_scene_delta_range>, Vec<u8>) {
    let (mut ranges, mut bytes) = (Vec::new(), Vec::new());
    let mut at = 0;
    while at < next.len() {
        if previous.get(at) == Some(&next[at]) {
            at += 1;
            continue;
        }
        let start = at;
        while at < next.len() && previous.get(at) != Some(&next[at]) {
            at += 1;
        }
        ranges.push(progpu_native_scene_delta_range { offset: start as u64, size: (at - start) as u64 });
        bytes.extend_from_slice(&next[start..at]);
    }
    (ranges, bytes)
}

unsafe fn send(
    gpu: &common::Gpu,
    scene_id: u64,
    base_generation: u64,
    stream_size: usize,
    ranges: &[progpu_native_scene_delta_range],
    bytes: &[u8],
) -> progpu_native_status {
    let delta = progpu_native_scene_delta {
        struct_size: std::mem::size_of::<progpu_native_scene_delta>() as u32,
        range_count: ranges.len() as u32,
        scene_id,
        base_generation,
        stream_size: stream_size as u64,
        ranges: ranges.as_ptr(),
        bytes: bytes.as_ptr() as *const c_void,
        byte_count: bytes.len() as u64,
    };
    let mut metrics: progpu_native_scene_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
    progpu_native_engine_update_scene_delta(gpu.engine, &delta, &mut metrics)
}

unsafe fn whole(gpu: &common::Gpu, stream: &[u8]) {
    let mut metrics: progpu_native_scene_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
    let status = progpu_native_engine_update_scene(gpu.engine, stream.as_ptr() as *const c_void, stream.len(), &mut metrics);
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
}

unsafe fn pixels(gpu: &common::Gpu, scene_id: u64, generation: u64) -> Vec<u8> {
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
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_render_scene(gpu.engine, &frame, &mut metrics));
    gpu.read_rgba8(texture, WIDTH, HEIGHT)
}

#[test]
fn a_delta_reads_back_like_the_whole_stream() {
    let Some(gpu) = common::Gpu::new() else { return };
    let Some(reference) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the devices; the spans outlive them.
    unsafe {
        let first = scene(81, 1, 20, 0.2);
        whole(&gpu, &first);
        pixels(&gpu, 81, 1);
        for (generation, rectangles, red) in [(2, 20, 0.9), (3, 32, 0.4), (4, 11, 0.7)] {
            let previous = scene(81, generation - 1, match generation { 2 => 20, 3 => 20, _ => 32 }, match generation { 2 => 0.2, 3 => 0.9, _ => 0.4 });
            let next = scene(81, generation, rectangles, red);
            let (ranges, bytes) = diff(&previous, &next);
            assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, send(&gpu, 81, generation - 1, next.len(), &ranges, &bytes));
            whole(&reference, &scene(90 + generation, 1, rectangles, red));
            assert!(pixels(&gpu, 81, generation) == pixels(&reference, 90 + generation, 1), "generation {generation}");
        }
    }
}

#[test]
fn a_delta_that_is_not_valid_leaves_the_scene_as_it_was() {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the device; the spans outlive them.
    unsafe {
        let first = scene(82, 1, 12, 0.3);
        whole(&gpu, &first);
        let before = pixels(&gpu, 82, 1);
        let next = scene(82, 2, 12, 0.8);
        let (ranges, bytes) = diff(&first, &next);
        assert_ne!(PROGPU_NATIVE_STATUS_SUCCESS, send(&gpu, 82, 7, next.len(), &ranges, &bytes), "another base");
        assert_ne!(PROGPU_NATIVE_STATUS_SUCCESS, send(&gpu, 99, 1, next.len(), &ranges, &bytes), "another scene");
        assert_ne!(PROGPU_NATIVE_STATUS_SUCCESS, send(&gpu, 82, 1, next.len(), &ranges, &bytes[1..]), "too few bytes");
        let overlapping = [
            progpu_native_scene_delta_range { offset: 8, size: 8 },
            progpu_native_scene_delta_range { offset: 12, size: 8 },
        ];
        assert_ne!(PROGPU_NATIVE_STATUS_SUCCESS, send(&gpu, 82, 1, next.len(), &overlapping, &[0; 16]), "overlapping ranges");
        let past = [progpu_native_scene_delta_range { offset: next.len() as u64, size: 4 }];
        assert_ne!(PROGPU_NATIVE_STATUS_SUCCESS, send(&gpu, 82, 1, next.len(), &past, &[0; 4]), "past the end");
        let damaged = [progpu_native_scene_delta_range { offset: 0, size: 4 }];
        assert_ne!(PROGPU_NATIVE_STATUS_SUCCESS, send(&gpu, 82, 1, next.len(), &damaged, &[0; 4]), "a stream that does not validate");
        assert!(before == pixels(&gpu, 82, 1), "the retained scene is drawn as it was");
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, send(&gpu, 82, 1, next.len(), &ranges, &bytes), "the valid delta still applies");
    }
}
