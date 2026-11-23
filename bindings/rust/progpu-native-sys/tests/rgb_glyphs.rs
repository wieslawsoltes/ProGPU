//! Glyphs with per-channel coverage through the scene builder of the C ABI: the box filter, the
//! five-tap filter and the coverage transfer, on an opaque transient layer.

mod common;

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use std::ffi::c_void;
use std::sync::atomic::{AtomicU64, Ordering};

const SIZE: u32 = 32;
const SCENE: u64 = 71;
static NEXT_GENERATION: AtomicU64 = AtomicU64::new(1);

fn point(x: f32, y: f32) -> progpu_native_point {
    progpu_native_point { x, y }
}

fn line(from: (f32, f32), to: (f32, f32)) -> progpu_native_path_segment {
    progpu_native_path_segment {
        p0: point(from.0, from.1),
        p1: point(to.0, to.1),
        p2: point(0.0, 0.0),
        p3: point(0.0, 0.0),
        kind: PROGPU_NATIVE_PATH_SEGMENT_LINE as u32,
        pad0: 0,
        pad1: 0,
        pad2: 0,
    }
}

/// A stem from x = 6 to x = 10, 16 high, drawn in black over white with its tile at (8, 8).
unsafe fn stream(filter: u32, transfer: u32, generation: u64) -> Vec<u8> {
    let builder = progpu_native_scene_builder_create(SCENE, generation);
    assert!(!builder.is_null());
    let white = progpu_native_color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 };
    assert_eq!(1, progpu_native_scene_builder_clear_target(builder, white));
    let segments = [
        line((6.0, 0.0), (10.0, 0.0)),
        line((10.0, 0.0), (10.0, 16.0)),
        line((10.0, 16.0), (6.0, 16.0)),
        line((6.0, 16.0), (6.0, 0.0)),
    ];
    let outline = progpu_native_scene_glyph_outline {
        segment_offset: 0,
        segment_count: 4,
        min_x: 6.0,
        min_y: 0.0,
        max_x: 10.0,
        max_y: 16.0,
        raster_scale: 1.0,
        subpixel_x: 0.0,
    };
    let mut resource = 0u32;
    assert_eq!(
        1,
        progpu_native_scene_builder_add_glyph_outlines(builder, &outline, 1, segments.as_ptr(), 4, &mut resource)
    );
    let all = progpu_native_image_rect { x: 0.0, y: 0.0, width: SIZE as f32, height: SIZE as f32 };
    let layer = progpu_native_scene_layer {
        struct_size: std::mem::size_of::<progpu_native_scene_layer>() as u32,
        flags: (PROGPU_NATIVE_SCENE_LAYER_BOUNDS
            | PROGPU_NATIVE_SCENE_LAYER_INITIALIZE_FROM_BACKGROUND
            | PROGPU_NATIVE_SCENE_LAYER_IGNORE_ALPHA) as u32,
        bounds: all,
        opacity: 1.0,
        blend_mode: PROGPU_NATIVE_BLEND_SRC_OVER as u32,
        mask_resource_index: PROGPU_NATIVE_SCENE_NO_INDEX as u32,
        effect_resource_index: PROGPU_NATIVE_SCENE_NO_INDEX as u32,
        content_revision: 0,
        composite_revision: 0,
        reserved0: 0,
        reserved1: 0,
    };
    assert_eq!(1, progpu_native_scene_builder_push_layer(builder, &layer));
    let draw = progpu_native_scene_rgb_glyph_draw {
        struct_size: std::mem::size_of::<progpu_native_scene_rgb_glyph_draw>() as u32,
        glyph_count: 1,
        filter_model: filter,
        pixel_geometry: 1,
        gamma: 1.0,
        enhanced_contrast: 0.0,
        cleartype_level: 1.0,
        dpi_scale: 1.0,
        coverage_transfer: transfer,
        reserved1: 0,
    };
    let tile = progpu_native_scene_rgb_glyph_tile {
        outline_index: 0,
        width: 16,
        height: 16,
        reserved: 0,
        x_start: 0.0,
        y_start: -16.0,
        scale: 1.0,
        subpixel_x: 0.0,
        target_x: 8,
        target_y: 8,
        foreground: progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 },
    };
    let state = progpu_native_scene_builder_identity_state();
    let mut state_index = 0u32;
    assert_eq!(1, progpu_native_scene_builder_add_state(builder, &state, &mut state_index));
    assert_eq!(
        1,
        progpu_native_scene_builder_draw_rgb_glyph_run(builder, resource, &draw, &tile, 1, all, state_index),
        "draw_rgb_glyph_run: error {}",
        progpu_native_scene_builder_last_error(builder)
    );
    assert_eq!(1, progpu_native_scene_builder_pop_layer(builder));
    let size = progpu_native_scene_builder_required_stream_size(builder);
    let mut bytes = vec![0u8; size];
    let mut written = 0usize;
    assert_eq!(
        1,
        progpu_native_scene_builder_build_into(builder, bytes.as_mut_ptr() as *mut c_void, size, &mut written)
    );
    progpu_native_scene_builder_destroy(builder);
    bytes
}

/// What the engine said of the call that failed.
unsafe fn last_error(gpu: &common::Gpu) -> String {
    let mut text = [0 as core::ffi::c_char; 512];
    progpu_native_engine_get_last_error(gpu.engine, text.as_mut_ptr(), text.len());
    core::ffi::CStr::from_ptr(text.as_ptr()).to_string_lossy().into_owned()
}

/// The pixel left of the stem, the first pixel it covers fully, and one far from it.
unsafe fn edge(gpu: &common::Gpu, filter: u32, transfer: u32) -> ([u8; 3], [u8; 3], [u8; 3]) {
    // Each draw is a new generation: a generation of a scene does not change.
    let generation = NEXT_GENERATION.fetch_add(1, Ordering::Relaxed);
    let bytes = stream(filter, transfer, generation);
    let mut metrics: progpu_native_scene_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
    let data = bytes.as_ptr() as *const c_void;
    let status = progpu_native_engine_update_scene(gpu.engine, data, bytes.len(), &mut metrics);
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "{}", last_error(gpu));
    let (texture, view) = gpu.target(SIZE, SIZE);
    let mut frame: progpu_native_scene_frame = std::mem::zeroed();
    frame.struct_size = std::mem::size_of::<progpu_native_scene_frame>() as u32;
    frame.width = SIZE;
    frame.height = SIZE;
    frame.dpi_scale = 1.0;
    frame.target_view = view as usize;
    frame.scene_id = SCENE;
    frame.generation = generation;
    let mut frame_metrics: progpu_native_scene_frame_metrics = std::mem::zeroed();
    frame_metrics.struct_size = std::mem::size_of::<progpu_native_scene_frame_metrics>() as u32;
    let status = progpu_native_engine_render_scene(gpu.engine, &frame, &mut frame_metrics);
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "{}", last_error(gpu));
    let pixels = gpu.read_rgba8(texture, SIZE, SIZE);
    let at = |x: usize, y: usize| {
        let offset = (y * SIZE as usize + x) * 4;
        [pixels[offset], pixels[offset + 1], pixels[offset + 2]]
    };
    // The tile is at x = 8, so the stem covers 14..18.
    (at(13, 16), at(15, 16), at(2, 2))
}

#[test]
fn the_five_tap_filter_reaches_one_subpixel_further_than_the_box() {
    let Some(gpu) = common::Gpu::new() else { return };
    unsafe {
        let box_filter = PROGPU_NATIVE_RGB_GLYPH_FULL_PIXEL_BOX_8X8 as u32;
        let (left, inside, outside) = edge(&gpu, box_filter, 0);
        assert_eq!([0, 0, 0], inside, "inside the stem");
        assert_eq!([255, 255, 255], outside, "away from it");
        assert_eq!(255, left[1], "the box of green ends at the stem");
        assert!(left[2] < 255, "the box of blue reaches it: {left:?}");

        let (left, inside, outside) = edge(&gpu, PROGPU_NATIVE_RGB_GLYPH_FIR5_8X8 as u32, 0);
        assert_eq!([0, 0, 0], inside, "inside the stem");
        assert_eq!([255, 255, 255], outside, "away from it");
        assert_eq!(255, left[0], "red is two subpixels away from the stem and more");
        assert!(left[1] < 255, "the filter of green reaches the stem: {left:?}");
        assert!(left[2] < left[1], "blue is nearer the stem than green: {left:?}");
    }
}

#[test]
fn the_coverage_transfer_applies_to_each_channel() {
    let Some(gpu) = common::Gpu::new() else { return };
    unsafe {
        let filter = PROGPU_NATIVE_RGB_GLYPH_FIR5_8X8 as u32;
        let (plain, _, _) = edge(&gpu, filter, 0);
        let (heavier, inside, _) = edge(&gpu, filter, 78);
        assert_eq!([0, 0, 0], inside, "full coverage stays full");
        assert!(heavier[2] < plain[2], "an exponent above one darkens the edge: {plain:?} {heavier:?}");
    }
}
