//! The spans of a compiled scene (the draws that share a clip): a span is replayed from the list
//! of its commands, and a span with enough draws becomes a render bundle once its list repeats.
//! A list names the objects a span draws with, not what is in them, so a bundle is kept across a
//! scene that is sent again for as long as its span names the same objects in the same order.
//! Whichever way a span is drawn, the frame reads back the same bytes: a scene drawn again
//! unchanged, a scene sent again with other colours, and one sent again with a span that holds
//! other draws are compared with an engine that has seen nothing before.
//!
//! With `PROGPU_NATIVE_TRACE_SCENE_ENCODE=1` the engine prints for each frame how many spans it
//! replayed directly, how many took a kept bundle and how many bundles it made.

mod common;

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use std::ffi::c_void;

const WIDTH: u32 = 96;
const HEIGHT: u32 = 64;

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

fn rect(x: f32, y: f32, width: f32, height: f32) -> progpu_native_image_rect {
    progpu_native_image_rect { x, y, width, height }
}

/// One span of the scene: its clip, how many pairs of draws it holds (a rounded rectangle and a
/// glyph, which are two draws because they are of two families) and the colour of its rectangles.
#[derive(Clone, Copy)]
struct Span {
    clip: (f32, f32, f32, f32),
    pairs: u32,
    color: (f32, f32, f32),
}

/// Three spans: two that can become bundles (ten draws in the first, twice `second_pairs` in the
/// second) and one with two draws, which is always replayed directly.
fn spans(second_color: (f32, f32, f32), second_pairs: u32) -> [Span; 3] {
    [
        Span { clip: (0.0, 0.0, 47.0, 40.0), pairs: 5, color: (1.0, 0.0, 0.0) },
        Span { clip: (49.0, 0.0, 47.0, 40.0), pairs: second_pairs, color: second_color },
        Span { clip: (0.0, 42.0, 96.0, 22.0), pairs: 1, color: (0.0, 0.5, 0.0) },
    ]
}

unsafe fn scene(scene_id: u64, generation: u64, spans: &[Span]) -> Vec<u8> {
    let builder = progpu_native_scene_builder_create(scene_id, generation);
    assert!(!builder.is_null());
    assert_eq!(1, progpu_native_scene_builder_clear_target(builder, progpu_native_color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 }));
    // A triangle, 10 by 10.
    let segments = [line((0.0, 0.0), (10.0, 0.0)), line((10.0, 0.0), (5.0, 10.0)), line((5.0, 10.0), (0.0, 0.0))];
    for span in spans {
        let (clip_x, clip_y, clip_width, clip_height) = span.clip;
        let mut state = progpu_native_scene_builder_identity_state();
        state.flags |= PROGPU_NATIVE_SCENE_STATE_CLIP_RECT as u32;
        state.clip_rect = rect(clip_x, clip_y, clip_width, clip_height);
        let mut state_index = 0u32;
        assert_eq!(1, progpu_native_scene_builder_add_state(builder, &state, &mut state_index));
        for pair in 0..span.pairs {
            // The rectangles step to the right and overlap; the last ones reach past the clip.
            let x = clip_x + 2.0 + pair as f32 * 9.0;
            let y = clip_y + 2.0 + pair as f32 * 3.0;
            let shade = 1.0 - pair as f32 * 0.15;
            let color = progpu_native_color { r: span.color.0 * shade, g: span.color.1 * shade, b: span.color.2 * shade, a: 1.0 };
            let mut brush = 0u32;
            assert_eq!(1, progpu_native_scene_builder_add_solid_brush(builder, color, 1.0, &mut brush));
            let primitive = progpu_native_analytic_primitive {
                kind: PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE as u32,
                flags: 0,
                x,
                y,
                width: 16.0,
                height: 14.0,
                corner_radius: 3.0,
                stroke_thickness: 0.0,
                color,
                transform: progpu_native_scene_builder_identity_transform(),
            };
            assert_eq!(
                1,
                progpu_native_scene_builder_draw_analytic(builder, &primitive, 1, &brush, 1, rect(x, y, 16.0, 14.0), state_index),
                "draw_analytic: error {}",
                progpu_native_scene_builder_last_error(builder)
            );

            let outline = progpu_native_scene_glyph_outline {
                segment_offset: 0,
                segment_count: segments.len() as u64,
                min_x: 0.0,
                min_y: 0.0,
                max_x: 10.0,
                max_y: 10.0,
                raster_scale: 1.0,
                subpixel_x: 0.0,
            };
            let mut resource = 0u32;
            assert_eq!(
                1,
                progpu_native_scene_builder_add_glyph_outlines(builder, &outline, 1, segments.as_ptr(), segments.len(), &mut resource),
                "add_glyph_outlines: error {}",
                progpu_native_scene_builder_last_error(builder)
            );
            let ink = progpu_native_color { r: 0.0, g: 0.0, b: 0.2 * pair as f32, a: 1.0 };
            let style = progpu_native_scene_text_style {
                color: ink,
                text_rendering_mode: PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE as u32,
                coverage_transfer: 0,
                reserved1: 0,
                reserved2: 0,
            };
            let mut style_index = 0u32;
            assert_eq!(1, progpu_native_scene_builder_add_text_style(builder, &style, &mut style_index));
            let glyph = progpu_native_positioned_glyph {
                outline_index: 0,
                reserved: 0,
                position: point(x + 3.0, y + 2.0),
                basis_x: point(1.0, 0.0),
                basis_y: point(0.0, 1.0),
                color: ink,
                atlas_to_logical_scale: 1.0,
                bold_offset: 0.0,
                italic_skew: 0.0,
                reserved2: 0.0,
            };
            assert_eq!(
                1,
                progpu_native_scene_builder_draw_glyph_run(builder, resource, &glyph, 1, rect(x + 3.0, y + 2.0, 10.0, 10.0), state_index, style_index),
                "draw_glyph_run: error {}",
                progpu_native_scene_builder_last_error(builder)
            );
        }
    }
    let size = progpu_native_scene_builder_required_stream_size(builder);
    let mut stream = vec![0u8; size];
    let mut written = 0usize;
    assert_eq!(1, progpu_native_scene_builder_build_into(builder, stream.as_mut_ptr() as *mut c_void, stream.len(), &mut written));
    assert_eq!(size, written);
    progpu_native_scene_builder_destroy(builder);
    stream
}

unsafe fn update(gpu: &common::Gpu, stream: &[u8]) {
    let mut metrics: progpu_native_scene_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
    let status = progpu_native_engine_update_scene(gpu.engine, stream.as_ptr() as *const c_void, stream.len(), &mut metrics);
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "update_scene: validation error {}", metrics.validation_error);
}

/// One frame of the scene the engine holds: the number of draws and the pixels.
unsafe fn render(gpu: &common::Gpu, scene_id: u64, generation: u64) -> (u32, Vec<u8>) {
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
    // The stages are captured so that the engine prints its span counts when it is asked to.
    frame.flags = PROGPU_NATIVE_SCENE_FRAME_CAPTURE_CPU_STAGES as u32;
    let mut metrics: progpu_native_scene_frame_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_frame_metrics>() as u32;
    let status = progpu_native_engine_render_scene(gpu.engine, &frame, &mut metrics);
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "render_scene of generation {generation}");
    (metrics.draw_call_count, gpu.read_rgba8(texture, WIDTH, HEIGHT))
}

fn pixel(pixels: &[u8], x: u32, y: u32) -> [u8; 4] {
    let index = ((y * WIDTH + x) * 4) as usize;
    [pixels[index], pixels[index + 1], pixels[index + 2], pixels[index + 3]]
}

const BLUE: (f32, f32, f32) = (0.0, 0.0, 1.0);
const ORANGE: (f32, f32, f32) = (1.0, 0.5, 0.0);

#[test]
fn a_scene_drawn_again_reads_back_the_bytes_of_its_first_frame() {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the device.
    unsafe {
        update(&gpu, &scene(31, 1, &spans(BLUE, 5)));
        // The first frame replays every span from its list.
        let (first_draws, first) = render(&gpu, 31, 1);
        assert_eq!(23, first_draws, "the clear, ten draws in each of two spans and two in the third");
        assert_eq!([255, 0, 0, 255], pixel(&first, 3, 8), "the first rectangle of the first span");
        assert_eq!([0, 0, 255, 255], pixel(&first, 52, 8), "the first rectangle of the second span");
        assert_eq!([255, 255, 255, 255], pixel(&first, 48, 20), "between the clips of the two spans");
        assert_eq!([0, 128, 0, 255], pixel(&first, 3, 50), "the rectangle of the third span");
        // The scene is drawn again as it is: the spans with ten draws go on by bundles made from
        // their lists, and by the same bundles in the frame after that.
        let (second_draws, second) = render(&gpu, 31, 1);
        let (third_draws, third) = render(&gpu, 31, 1);
        assert_eq!(first_draws, second_draws);
        assert_eq!(first_draws, third_draws);
        assert!(first == second, "the frame that turns lists into bundles reads back the bytes of the first frame");
        assert!(first == third, "the frame drawn by kept bundles reads back the bytes of the first frame");
    }
}

#[test]
fn a_scene_sent_again_reads_back_the_bytes_of_a_new_engine() {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the devices.
    unsafe {
        // What an engine that is sent the scene for the first time draws.
        let expected = |generation: u64, spans: &[Span]| {
            let fresh = common::Gpu::new().expect("a second device");
            update(&fresh, &scene(33, generation, spans));
            render(&fresh, 33, generation)
        };
        let check = |generation: u64, spans: &[Span], expected: &(u32, Vec<u8>)| {
            update(&gpu, &scene(33, generation, spans));
            let (draws, pixels) = render(&gpu, 33, generation);
            assert_eq!(expected.0, draws, "generation {generation}");
            assert!(expected.1 == pixels, "generation {generation} reads back the bytes of a new engine");
        };

        // The engine draws the scene twice, so its two spans with ten draws have bundles.
        update(&gpu, &scene(33, 1, &spans(BLUE, 5)));
        let (_, before) = render(&gpu, 33, 1);
        let (_, again) = render(&gpu, 33, 1);
        assert!(before == again);

        // Another colour in the second span. Its list names the objects it named before, so
        // the span goes on by its kept bundle and draws what the objects hold now.
        let orange = expected(2, &spans(ORANGE, 5));
        assert_eq!([255, 128, 0, 255], pixel(&orange.1, 52, 8), "the second span in its new colour");
        assert!(orange.1 != before);
        check(2, &spans(ORANGE, 5), &orange);
        check(3, &spans(ORANGE, 5), &orange);

        // Two draws fewer in the second span: its list is another one, and so is the list of
        // the span after it. The first frame replays them directly, the second one makes the
        // bundle of the second span, the third one finds it. The first span keeps its bundle
        // throughout.
        let shorter = expected(4, &spans(ORANGE, 4));
        assert_eq!(orange.0 - 2, shorter.0);
        assert!(shorter.1 != orange.1);
        for generation in 4..7u64 {
            check(generation, &spans(ORANGE, 4), &shorter);
        }

        // Too few draws for a bundle in the second span, however often it repeats.
        let short = expected(7, &spans(ORANGE, 2));
        for generation in 7..10u64 {
            check(generation, &spans(ORANGE, 2), &short);
        }

        // Back to the scene of the first generation. The bundle its second span had then was
        // given up meanwhile.
        check(10, &spans(BLUE, 5), &(23, before));
    }
}
