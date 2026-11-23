//! Spans under a rounded rectangle mask take the uniforms of the mask from a slot of a shared
//! uniform page instead of a buffer and a bind group of their own. A slot is given back when the
//! span goes and is handed out again in a later frame, to whichever span asks first, so the bytes
//! of a slot change while its bind group stays. However the slots come to be shared out, a frame
//! reads back the bytes of an engine that has seen nothing before: masks that move, masks that
//! swap places, more masks than a page holds, fewer masks again, a chain of two masks (which
//! keeps a buffer of its own) between them, and a masked span with enough draws to be drawn by a
//! kept render bundle whose commands name the bind group of a slot.
//!
//! With `PROGPU_NATIVE_TRACE_SCENE_ENCODE=1` the engine prints for each frame how many spans it
//! replayed directly, how many took a kept bundle and how many bundles it made.

mod common;

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use std::ffi::c_void;

const WIDTH: u32 = 256;
const HEIGHT: u32 = 160;

fn rect(x: f32, y: f32, width: f32, height: f32) -> progpu_native_image_rect {
    progpu_native_image_rect { x, y, width, height }
}

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

fn rounded(bounds: (f32, f32, f32, f32), radius: f32) -> progpu_native_scene_layer_mask {
    progpu_native_scene_layer_mask {
        struct_size: std::mem::size_of::<progpu_native_scene_layer_mask>() as u32,
        kind: PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE as u32,
        flags: 0,
        reserved: 0,
        bounds: rect(bounds.0, bounds.1, bounds.2, bounds.3),
        transform: progpu_native_affine_2d { m11: 1.0, m12: 0.0, m21: 0.0, m22: 1.0, m31: 0.0, m32: 0.0 },
        corner_radii_x: [radius; 4],
        corner_radii_y: [radius; 4],
        opacity: 1.0,
        reserved0: 0,
        reserved1: 0,
        reserved2: 0,
    }
}

/// One masked span: the bounds and the corner radius of its mask, how many rectangles it draws
/// under the mask (each larger than the mask, so the mask alone gives the span its shape), its
/// colour, and whether a second mask, inset by six, applies as well (a chain of two masks). A
/// span with more than one rectangle draws a glyph after each of them, which keeps the
/// rectangles apart as draws: two draws for each rectangle then.
#[derive(Clone, Copy)]
struct Cell {
    bounds: (f32, f32, f32, f32),
    radius: f32,
    draws: u32,
    color: (f32, f32, f32),
    chained: bool,
}

fn cell(bounds: (f32, f32, f32, f32), radius: f32, draws: u32, color: (f32, f32, f32)) -> Cell {
    Cell { bounds, radius, draws, color, chained: false }
}

unsafe fn scene(scene_id: u64, generation: u64, cells: &[Cell]) -> Vec<u8> {
    let builder = progpu_native_scene_builder_create(scene_id, generation);
    assert!(!builder.is_null());
    assert_eq!(1, progpu_native_scene_builder_clear_target(builder, progpu_native_color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 }));
    // A triangle, 10 by 10.
    let segments = [line((0.0, 0.0), (10.0, 0.0)), line((10.0, 0.0), (5.0, 10.0)), line((5.0, 10.0), (0.0, 0.0))];
    for cell in cells {
        let (x, y, width, height) = cell.bounds;
        let mut mask_index = 0u32;
        if cell.chained {
            let masks = [rounded(cell.bounds, cell.radius), rounded((x + 6.0, y + 6.0, width - 12.0, height - 12.0), cell.radius)];
            assert_eq!(
                1,
                progpu_native_scene_builder_add_analytic_mask_chain(builder, masks.as_ptr(), masks.len(), &mut mask_index),
                "add_analytic_mask_chain: error {}",
                progpu_native_scene_builder_last_error(builder)
            );
        } else {
            let mask = rounded(cell.bounds, cell.radius);
            assert_eq!(
                1,
                progpu_native_scene_builder_add_rounded_rectangle_mask(builder, &mask, &mut mask_index),
                "add_rounded_rectangle_mask: error {}",
                progpu_native_scene_builder_last_error(builder)
            );
        }
        let mut state = progpu_native_scene_builder_identity_state();
        state.flags |= PROGPU_NATIVE_SCENE_STATE_MASK as u32;
        state.mask_resource_index = mask_index;
        let mut state_index = 0u32;
        assert_eq!(
            1,
            progpu_native_scene_builder_add_state(builder, &state, &mut state_index),
            "add_state: error {}",
            progpu_native_scene_builder_last_error(builder)
        );
        for draw in 0..cell.draws {
            // The first rectangle covers the mask and more; the ones after it step inwards from
            // the left in a darker shade, so every draw of the span shows in the frame.
            let inset = draw as f32 * 2.0;
            let shade = 1.0 - draw as f32 * 0.08;
            let color = progpu_native_color { r: cell.color.0 * shade, g: cell.color.1 * shade, b: cell.color.2 * shade, a: 1.0 };
            let mut brush = 0u32;
            assert_eq!(1, progpu_native_scene_builder_add_solid_brush(builder, color, 1.0, &mut brush));
            let bounds = rect(x - 3.0 + inset, y - 3.0, width + 6.0 - inset, height + 6.0);
            let primitive = progpu_native_analytic_primitive {
                kind: PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE as u32,
                flags: 0,
                x: bounds.x,
                y: bounds.y,
                width: bounds.width,
                height: bounds.height,
                corner_radius: 0.0,
                stroke_thickness: 0.0,
                color,
                transform: progpu_native_scene_builder_identity_transform(),
            };
            assert_eq!(
                1,
                progpu_native_scene_builder_draw_analytic(builder, &primitive, 1, &brush, 1, bounds, state_index),
                "draw_analytic: error {}",
                progpu_native_scene_builder_last_error(builder)
            );
            if cell.draws == 1 {
                continue;
            }
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
            let ink = progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 };
            let style = progpu_native_scene_text_style {
                color: ink,
                text_rendering_mode: PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE as u32,
                coverage_transfer: 0,
                reserved1: 0,
                reserved2: 0,
            };
            let mut style_index = 0u32;
            assert_eq!(1, progpu_native_scene_builder_add_text_style(builder, &style, &mut style_index));
            // The glyphs step along the upper edge of the mask, the first one across its corner.
            let position = point(x + inset * 3.0, y + 1.0);
            let glyph = progpu_native_positioned_glyph {
                outline_index: 0,
                reserved: 0,
                position,
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
                progpu_native_scene_builder_draw_glyph_run(builder, resource, &glyph, 1, rect(position.x, position.y, 10.0, 10.0), state_index, style_index),
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

const WHITE: [u8; 4] = [255, 255, 255, 255];
const RED: (f32, f32, f32) = (1.0, 0.0, 0.0);
const GREEN: (f32, f32, f32) = (0.0, 1.0, 0.0);
const BLUE: (f32, f32, f32) = (0.0, 0.0, 1.0);

/// What an engine that is sent the scene for the first time draws.
unsafe fn expected(scene_id: u64, generation: u64, cells: &[Cell]) -> (u32, Vec<u8>) {
    let fresh = common::Gpu::new().expect("a second device");
    update(&fresh, &scene(scene_id, generation, cells));
    render(&fresh, scene_id, generation)
}

/// Sends the scene to an engine that has drawn others before and requires the frame of a new
/// engine, in the frame after the update and in the one after that.
unsafe fn check(gpu: &common::Gpu, scene_id: u64, generation: u64, cells: &[Cell]) -> Vec<u8> {
    let (draws, pixels) = expected(scene_id, generation, cells);
    update(gpu, &scene(scene_id, generation, cells));
    for frame in 0..2 {
        let (seen_draws, seen) = render(gpu, scene_id, generation);
        assert_eq!(draws, seen_draws, "generation {generation}, frame {frame}");
        assert!(pixels == seen, "generation {generation}, frame {frame} reads back the bytes of a new engine");
    }
    pixels
}

/// A grid of small masked spans, `count` of them, 8 by 8 with a gap of nothing: 32 in a row.
/// `shift` moves every mask by a part of a pixel and `phase` turns the colours.
fn grid(count: u32, shift: f32, phase: u32) -> Vec<Cell> {
    (0..count)
        .map(|index| {
            let column = (index % 32) as f32;
            let row = (index / 32) as f32;
            let color = [RED, GREEN, BLUE][((index + phase) % 3) as usize];
            cell((column * 8.0 + 0.5 + shift, row * 8.0 + 0.5 + shift, 7.0, 7.0), 3.0, 1, color)
        })
        .collect()
}

#[test]
fn a_mask_shapes_its_span_and_follows_the_scene() {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the devices.
    unsafe {
        // Two masked spans. The corners of a mask stay in the colour of the target.
        let first = [cell((8.0, 8.0, 80.0, 60.0), 20.0, 1, RED), cell((120.0, 40.0, 100.0, 80.0), 30.0, 1, BLUE)];
        let pixels = check(&gpu, 41, 1, &first);
        assert_eq!([255, 0, 0, 255], pixel(&pixels, 48, 38), "inside the first mask");
        assert_eq!(WHITE, pixel(&pixels, 10, 10), "the corner the first mask rounds off");
        assert_eq!(WHITE, pixel(&pixels, 6, 38), "left of the first mask, where its rectangle reaches");
        assert_eq!([0, 0, 255, 255], pixel(&pixels, 170, 80), "inside the second mask");
        assert_eq!(WHITE, pixel(&pixels, 122, 42), "the corner the second mask rounds off");

        // The masks move and change their radius: the slots are the ones of the frame before,
        // the bytes in them are new.
        let moved = [cell((20.0, 30.0, 80.0, 60.0), 6.0, 1, RED), cell((130.0, 20.0, 100.0, 80.0), 40.0, 1, BLUE)];
        let pixels = check(&gpu, 41, 2, &moved);
        assert_eq!(WHITE, pixel(&pixels, 10, 10), "where the first mask was");
        assert_eq!([255, 0, 0, 255], pixel(&pixels, 24, 34), "near the corner of the first mask, which is rounded less now");
        assert_eq!(WHITE, pixel(&pixels, 134, 24), "the corner the second mask rounds off more now");

        // The spans swap their masks: each takes the slot the other one had.
        let swapped = [cell((130.0, 20.0, 100.0, 80.0), 40.0, 1, RED), cell((20.0, 30.0, 80.0, 60.0), 6.0, 1, BLUE)];
        let pixels = check(&gpu, 41, 3, &swapped);
        assert_eq!([0, 0, 255, 255], pixel(&pixels, 60, 60));
        assert_eq!([255, 0, 0, 255], pixel(&pixels, 180, 60));

        // A chain of two masks between two single ones: the chain keeps a buffer of its own
        // and the single masks take the first two slots around it.
        let mut chain = cell((100.0, 10.0, 60.0, 60.0), 12.0, 1, GREEN);
        chain.chained = true;
        let mixed = [cell((8.0, 8.0, 80.0, 60.0), 20.0, 1, RED), chain, cell((170.0, 80.0, 70.0, 70.0), 35.0, 1, BLUE)];
        let pixels = check(&gpu, 41, 4, &mixed);
        assert_eq!([0, 255, 0, 255], pixel(&pixels, 130, 40), "inside both masks of the chain");
        assert_eq!(WHITE, pixel(&pixels, 102, 40), "inside the outer mask of the chain only");
        assert_eq!([0, 0, 255, 255], pixel(&pixels, 205, 115));

        // One mask is left.
        check(&gpu, 41, 5, &first[1..]);
        // And the scene of the beginning again.
        let again = check(&gpu, 41, 6, &first);
        assert_eq!([255, 0, 0, 255], pixel(&again, 48, 38));
    }
}

#[test]
fn more_masks_than_a_page_of_slots_and_fewer_again() {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the devices.
    unsafe {
        // Some masks, then more than the 256 slots of one page, each generation with every
        // mask moved a little and in another colour.
        let few = check(&gpu, 43, 1, &grid(40, 0.0, 0));
        assert_eq!([255, 0, 0, 255], pixel(&few, 4, 4), "the first span of the grid");
        assert_eq!([0, 255, 0, 255], pixel(&few, 12, 4), "the second span of the grid");
        assert_eq!(WHITE, pixel(&few, 0, 0), "the corner of the first mask");
        let many = check(&gpu, 43, 2, &grid(600, 0.25, 1));
        assert_eq!([0, 255, 0, 255], pixel(&many, 4, 4));
        assert_eq!([255, 0, 0, 255], pixel(&many, 188, 148), "the last span of the grid, in the third page");
        check(&gpu, 43, 3, &grid(600, 0.5, 2));
        // Fewer again: the slots of the second page are free and the first page is shared out
        // from its beginning.
        check(&gpu, 43, 4, &grid(300, 0.75, 0));
        check(&gpu, 43, 5, &grid(7, 0.0, 1));
        check(&gpu, 43, 6, &grid(257, 0.0, 2));
        // The same scene sent again: every span takes the slot it had.
        check(&gpu, 43, 7, &grid(257, 0.0, 2));
    }
}

#[test]
fn a_kept_bundle_of_a_masked_span_draws_with_the_bytes_its_slot_holds_now() {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the devices.
    unsafe {
        // Two masked spans with six rectangles and six glyphs each: enough for a render bundle
        // once the list of a span repeats. The engine draws the scene three times, so both spans
        // have bundles whose commands name the bind groups of their slots.
        let first = [cell((8.0, 8.0, 100.0, 70.0), 20.0, 6, RED), cell((130.0, 60.0, 110.0, 90.0), 30.0, 6, BLUE)];
        let (draws, pixels) = expected(45, 1, &first);
        update(&gpu, &scene(45, 1, &first));
        for frame in 0..3 {
            let (seen_draws, seen) = render(&gpu, 45, 1);
            assert_eq!(draws, seen_draws, "frame {frame}");
            assert!(pixels == seen, "frame {frame} reads back the bytes of a new engine");
        }
        assert_eq!(WHITE, pixel(&pixels, 10, 10), "the corner the first mask rounds off");

        // The masks move and the spans keep their draws: the lists name the objects they named
        // before, so the kept bundles draw the spans, under the masks as they are now.
        let moved = [cell((30.0, 40.0, 100.0, 70.0), 8.0, 6, RED), cell((140.0, 10.0, 110.0, 90.0), 45.0, 6, BLUE)];
        let (_, moved_pixels) = expected(45, 2, &moved);
        assert!(moved_pixels != pixels);
        assert_eq!(WHITE, pixel(&moved_pixels, 20, 20), "where the first mask was");
        for generation in 2..5u64 {
            // The rectangles follow the masks, so the scene is built anew for each generation.
            let seen = check(&gpu, 45, generation, &moved);
            assert!(seen == moved_pixels);
        }

        // A third span before the two: each of them takes the slot after the one it had.
        let three = [cell((200.0, 4.0, 40.0, 40.0), 10.0, 6, GREEN), moved[0], moved[1]];
        check(&gpu, 45, 5, &three);
        check(&gpu, 45, 6, &three);
        // And back to two.
        let back = check(&gpu, 45, 7, &moved);
        assert!(back == moved_pixels);
    }
}
