//! Glyph outlines by key: an outline is registered with the engine once
//! (`progpu_native_engine_register_glyph_outlines`) and a frame names it with a few words
//! (`progpu_native_scene_builder_add_glyph_keys`). A keyed run draws the pixels of the same run
//! with its outlines in the frame, a glyph many runs name is rasterized once, and a later frame
//! that names glyphs the engine holds rasterizes nothing.

mod common;

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use std::ffi::c_void;

const WIDTH: u32 = 96;
const HEIGHT: u32 = 64;
const FACE: u64 = 7;

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

/// The segments of the two outlines of the tests: a trapezoid (four segments, 12 by 12) and a
/// triangle (three segments, 10 by 10).
fn segments() -> Vec<progpu_native_path_segment> {
    vec![
        line((0.0, 0.0), (12.0, 0.0)),
        line((12.0, 0.0), (9.0, 12.0)),
        line((9.0, 12.0), (3.0, 12.0)),
        line((3.0, 12.0), (0.0, 0.0)),
        line((0.0, 0.0), (10.0, 0.0)),
        line((10.0, 0.0), (5.0, 10.0)),
        line((5.0, 10.0), (0.0, 0.0)),
    ]
}

/// One outline of the tests (0: the trapezoid, 1: the triangle): where its segments are and its
/// bounds.
fn shape(index: u32) -> (u64, u64, f32) {
    if index == 0 {
        (0, 4, 12.0)
    } else {
        (4, 3, 10.0)
    }
}

fn outline_key(glyph: u32) -> progpu_native_glyph_outline_key {
    progpu_native_glyph_outline_key { face: FACE, glyph, variant: 0 }
}

/// The two outlines under the keys (FACE, 0) and (FACE, 1).
fn registration() -> Vec<progpu_native_registered_glyph_outline> {
    (0..2)
        .map(|glyph| {
            let (segment_offset, segment_count, extent) = shape(glyph);
            progpu_native_registered_glyph_outline {
                key: outline_key(glyph),
                segment_offset,
                segment_count,
                min_x: 0.0,
                min_y: 0.0,
                max_x: extent,
                max_y: extent,
            }
        })
        .collect()
}

unsafe fn register(gpu: &common::Gpu) -> progpu_native_status {
    let outlines = registration();
    let segments = segments();
    progpu_native_engine_register_glyph_outlines(gpu.engine, outlines.as_ptr(), outlines.len(), segments.as_ptr(), segments.len())
}

/// One glyph of a run: the outline (0 or 1), its raster scale and subpixel phase, and where it is
/// drawn.
#[derive(Clone, Copy)]
struct Glyph {
    shape: u32,
    scale: f32,
    phase: f32,
    x: f32,
    y: f32,
}

fn glyph(shape: u32, scale: f32, phase: f32, x: f32, y: f32) -> Glyph {
    Glyph { shape, scale, phase, x, y }
}

/// A scene of glyph runs, one glyph resource for each run: with `keyed` the resources are key
/// tables, without it they carry the outlines and their segments.
unsafe fn scene(scene_id: u64, generation: u64, runs: &[Vec<Glyph>], keyed: bool) -> Vec<u8> {
    let builder = progpu_native_scene_builder_create(scene_id, generation);
    assert!(!builder.is_null());
    assert_eq!(1, progpu_native_scene_builder_clear_target(builder, progpu_native_color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 }));
    let all_segments = segments();
    for (run_index, run) in runs.iter().enumerate() {
        let mut resource = 0u32;
        if keyed {
            let keys: Vec<progpu_native_scene_glyph_key> =
                run.iter().map(|g| progpu_native_scene_glyph_key { outline: outline_key(g.shape), raster_scale: g.scale, subpixel_x: g.phase }).collect();
            assert_eq!(
                1,
                progpu_native_scene_builder_add_glyph_keys(builder, keys.as_ptr(), keys.len(), &mut resource),
                "add_glyph_keys: error {}",
                progpu_native_scene_builder_last_error(builder)
            );
        } else {
            // A resource with outlines carries exactly the segments its outlines name, each
            // shape once, in the order the run first names it.
            let mut packed: Vec<progpu_native_path_segment> = Vec::new();
            let mut placed: [Option<u64>; 2] = [None, None];
            let outlines: Vec<progpu_native_scene_glyph_outline> = run
                .iter()
                .map(|g| {
                    let (source_offset, segment_count, extent) = shape(g.shape);
                    let segment_offset = *placed[g.shape as usize].get_or_insert_with(|| {
                        let offset = packed.len() as u64;
                        packed.extend_from_slice(&all_segments[source_offset as usize..(source_offset + segment_count) as usize]);
                        offset
                    });
                    progpu_native_scene_glyph_outline {
                        segment_offset,
                        segment_count,
                        min_x: 0.0,
                        min_y: 0.0,
                        max_x: extent,
                        max_y: extent,
                        raster_scale: g.scale,
                        subpixel_x: g.phase,
                    }
                })
                .collect();
            assert_eq!(
                1,
                progpu_native_scene_builder_add_glyph_outlines(
                    builder,
                    outlines.as_ptr(),
                    outlines.len(),
                    packed.as_ptr(),
                    packed.len(),
                    &mut resource
                ),
                "add_glyph_outlines: error {}",
                progpu_native_scene_builder_last_error(builder)
            );
        }
        let color = if run_index % 2 == 0 {
            progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 }
        } else {
            progpu_native_color { r: 0.1, g: 0.2, b: 1.0, a: 0.8 }
        };
        let style = progpu_native_scene_text_style {
            color,
            text_rendering_mode: PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE as u32,
            coverage_transfer: 0,
            reserved1: 0,
            reserved2: 0,
        };
        let mut style_index = 0u32;
        assert_eq!(1, progpu_native_scene_builder_add_text_style(builder, &style, &mut style_index));
        let glyphs: Vec<progpu_native_positioned_glyph> = run
            .iter()
            .enumerate()
            .map(|(index, g)| progpu_native_positioned_glyph {
                outline_index: index as u32,
                reserved: 0,
                position: point(g.x, g.y),
                basis_x: point(1.0, 0.0),
                basis_y: point(0.0, 1.0),
                color,
                atlas_to_logical_scale: 1.0 / g.scale,
                bold_offset: 0.0,
                italic_skew: 0.0,
                reserved2: 0.0,
            })
            .collect();
        assert_eq!(
            1,
            progpu_native_scene_builder_draw_glyph_run(
                builder,
                resource,
                glyphs.as_ptr(),
                glyphs.len(),
                progpu_native_image_rect { x: 0.0, y: 0.0, width: WIDTH as f32, height: HEIGHT as f32 },
                PROGPU_NATIVE_SCENE_NO_INDEX as u32,
                style_index
            ),
            "draw_glyph_run: error {}",
            progpu_native_scene_builder_last_error(builder)
        );
    }
    let size = progpu_native_scene_builder_required_stream_size(builder);
    let mut stream = vec![0u8; size];
    let mut written = 0usize;
    assert_eq!(1, progpu_native_scene_builder_build_into(builder, stream.as_mut_ptr() as *mut c_void, stream.len(), &mut written));
    assert_eq!(size, written);
    progpu_native_scene_builder_destroy(builder);
    stream
}

unsafe fn update(gpu: &common::Gpu, stream: &[u8]) -> progpu_native_status {
    let mut metrics: progpu_native_scene_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
    progpu_native_engine_update_scene(gpu.engine, stream.as_ptr() as *const c_void, stream.len(), &mut metrics)
}

/// One frame of the scene the engine holds: the status, the metrics and the pixels.
unsafe fn render(gpu: &common::Gpu, scene_id: u64, generation: u64) -> (progpu_native_status, progpu_native_scene_frame_metrics, Vec<u8>) {
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
    let pixels = if status == PROGPU_NATIVE_STATUS_SUCCESS { gpu.read_rgba8(texture, WIDTH, HEIGHT) } else { Vec::new() };
    (status, metrics, pixels)
}

fn painted(pixels: &[u8]) -> usize {
    pixels.chunks_exact(4).filter(|pixel| pixel[0] != 255 || pixel[1] != 255 || pixel[2] != 255).count()
}

/// Runs with both outlines at several raster scales and subpixel phases, and one glyph named
/// twice in one run and again in another.
fn mixed_runs() -> Vec<Vec<Glyph>> {
    vec![
        vec![glyph(0, 1.0, 0.25, 4.0, 4.0), glyph(1, 1.0, 0.0, 20.0, 5.0), glyph(0, 1.0, 0.25, 34.0, 4.0)],
        vec![glyph(1, 1.5, 0.5, 50.0, 6.0), glyph(0, 2.0, 0.75, 66.0, 4.0)],
        vec![glyph(0, 1.0, 0.25, 6.0, 36.0), glyph(1, 1.0, 0.5, 22.0, 38.0), glyph(1, 1.5, 0.5, 40.0, 36.0)],
    ]
}

#[test]
fn a_keyed_run_draws_the_pixels_of_the_run_with_its_outlines_in_the_frame() {
    let Some(inline) = common::Gpu::new() else { return };
    let Some(keyed) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the devices.
    unsafe {
        let runs = mixed_runs();
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, update(&inline, &scene(21, 1, &runs, false)));
        let (status, inline_metrics, expected) = render(&inline, 21, 1);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "the frame with the outlines in it");
        assert!(painted(&expected) > 200, "the reference frame has glyphs in it: {} pixels", painted(&expected));

        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, register(&keyed));
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, update(&keyed, &scene(21, 1, &runs, true)));
        let (status, keyed_metrics, pixels) = render(&keyed, 21, 1);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "the frame of keys");
        assert!(pixels == expected, "a keyed run draws the pixels of the run with its outlines in the frame");
        eprintln!(
            "outlines in the frame: {} coverage bytes, {} draws; keys: {} coverage bytes, {} draws",
            inline_metrics.coverage_staging_bytes, inline_metrics.draw_call_count, keyed_metrics.coverage_staging_bytes, keyed_metrics.draw_call_count
        );
        // The five different glyphs of the frame (outline, scale, phase) are rasterized once each
        // on both ways.
        assert_ne!(0, keyed_metrics.coverage_staging_bytes);
        assert_eq!(inline_metrics.coverage_staging_bytes, keyed_metrics.coverage_staging_bytes);

        // The same frame again uploads nothing.
        let (status, again, replay) = render(&keyed, 21, 1);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
        assert!(replay == expected);
        assert_eq!(0, again.coverage_staging_bytes + again.vertex_upload_bytes + again.index_upload_bytes + again.texture_upload_bytes);
    }
}

#[test]
fn a_glyph_many_runs_name_is_rasterized_once_and_kept_for_later_frames() {
    let Some(single) = common::Gpu::new() else { return };
    let Some(many) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the devices.
    unsafe {
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, register(&single));
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, register(&many));

        // One run with one glyph: the coverage of one raster.
        let one = vec![vec![glyph(0, 1.0, 0.25, 4.0, 4.0)]];
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, update(&single, &scene(31, 1, &one, true)));
        let (status, one_metrics, _) = render(&single, 31, 1);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
        assert_ne!(0, one_metrics.coverage_staging_bytes);

        // Six runs, each with its own key table, twelve glyphs in all: still one raster.
        let place = |shift: f32| -> Vec<Vec<Glyph>> {
            (0..6).map(|run| vec![glyph(0, 1.0, 0.25, 4.0 + 14.0 * run as f32, 4.0 + shift), glyph(0, 1.0, 0.25, 4.0 + 14.0 * run as f32, 24.0 + shift)]).collect()
        };
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, update(&many, &scene(31, 1, &place(0.0), true)));
        let (status, first, first_pixels) = render(&many, 31, 1);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
        assert_eq!(one_metrics.coverage_staging_bytes, first.coverage_staging_bytes, "twelve glyphs of one key are one raster");
        assert!(painted(&first_pixels) > 12 * 40, "the twelve glyphs are drawn: {} pixels", painted(&first_pixels));

        // A new generation with the glyphs at other places: the engine holds the coverage. The
        // outlines in the frame give the pixels to compare with, on an engine of their own.
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, update(&many, &scene(31, 2, &place(9.0), true)));
        let (status, second, second_pixels) = render(&many, 31, 2);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
        assert_eq!(0, second.coverage_staging_bytes, "a later frame of glyphs the engine holds rasterizes nothing");
        assert!(second_pixels != first_pixels, "the glyphs moved");
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, update(&single, &scene(31, 2, &place(9.0), false)));
        let (status, _, expected) = render(&single, 31, 2);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
        assert!(second_pixels == expected, "kept coverage draws the pixels of a new raster");

        // A glyph the engine has not drawn yet in a later frame: only that one is rasterized.
        let mut more = place(9.0);
        more.push(vec![glyph(1, 1.0, 0.0, 80.0, 40.0)]);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, update(&many, &scene(31, 3, &more, true)));
        let (status, third, _) = render(&many, 31, 3);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
        assert_ne!(0, third.coverage_staging_bytes);
        assert!(third.coverage_staging_bytes <= one_metrics.coverage_staging_bytes, "one smaller glyph is rasterized, not the frame");
    }
}

#[test]
fn a_frame_draws_only_keys_the_engine_holds() {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the device.
    unsafe {
        let runs = mixed_runs();
        // The scene is accepted without the outlines: the registry is asked when a frame is drawn.
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, update(&gpu, &scene(41, 1, &runs, true)));
        let (status, _, _) = render(&gpu, 41, 1);
        assert_eq!(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, status, "a key the engine does not hold");

        // Registered, the same scene draws.
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, register(&gpu));
        let (status, _, pixels) = render(&gpu, 41, 1);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "the scene after its outlines were registered");
        assert!(painted(&pixels) > 200);

        // The same registration again changes nothing; other content under a held key is refused
        // and leaves the registry as it was.
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, register(&gpu));
        let mut other = registration();
        other[1].max_x = 11.0;
        let all_segments = segments();
        assert_eq!(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            progpu_native_engine_register_glyph_outlines(gpu.engine, other.as_ptr(), other.len(), all_segments.as_ptr(), all_segments.len())
        );
        // Null arrays with counts and an outline outside the segments are refused.
        assert_eq!(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            progpu_native_engine_register_glyph_outlines(gpu.engine, std::ptr::null(), 1, all_segments.as_ptr(), all_segments.len())
        );
        let mut outside = registration();
        outside[0].key.glyph = 9;
        outside[0].segment_count = 8;
        assert_eq!(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            progpu_native_engine_register_glyph_outlines(gpu.engine, outside.as_ptr(), outside.len(), all_segments.as_ptr(), all_segments.len())
        );
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_register_glyph_outlines(gpu.engine, std::ptr::null(), 0, std::ptr::null(), 0));
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, update(&gpu, &scene(41, 2, &runs, true)));
        let (status, _, after) = render(&gpu, 41, 2);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
        assert!(after == pixels, "refused registrations left the outlines as they were");

        // A released face is gone: the frame is refused, and so is a registration under its number.
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_release_glyph_face(gpu.engine, FACE));
        let (status, _, _) = render(&gpu, 41, 2);
        assert_eq!(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, status, "the scene held when its face was released");
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, update(&gpu, &scene(41, 3, &runs, true)));
        let (status, _, _) = render(&gpu, 41, 3);
        assert_eq!(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, status, "a new scene of a released face");
        assert_eq!(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, register(&gpu), "a released face number is not accepted again");

        // The engine draws on: outlines in the frame need no registration.
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, update(&gpu, &scene(41, 4, &runs, false)));
        let (status, _, inline) = render(&gpu, 41, 4);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
        assert!(inline == pixels, "the outlines in the frame draw what the keys drew");
    }
}
