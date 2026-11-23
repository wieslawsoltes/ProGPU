//! The Rust writer against the C++ builder: the same calls write the same bytes, and the validator
//! of the engine takes the stream.

#[allow(dead_code)]
#[path = "../../progpu-native-sys/tests/common/mod.rs"]
mod common;

use progpu_native::SceneWriter;
use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use std::ffi::c_void;

const NO_INDEX: u32 = PROGPU_NATIVE_SCENE_NO_INDEX as u32;
const IDENTITY: progpu_native_affine_2d =
    progpu_native_affine_2d { m11: 1.0, m12: 0.0, m21: 0.0, m22: 1.0, m31: 0.0, m32: 0.0 };
const ALL: progpu_native_image_rect = progpu_native_image_rect { x: 0.0, y: 0.0, width: 64.0, height: 64.0 };
const RED: progpu_native_color = progpu_native_color { r: 1.0, g: 0.0, b: 0.0, a: 1.0 };
const BLUE: progpu_native_color = progpu_native_color { r: 0.0, g: 0.0, b: 1.0, a: 0.5 };

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

fn square(size: f32) -> [progpu_native_path_segment; 4] {
    [
        line((0.0, 0.0), (size, 0.0)),
        line((size, 0.0), (size, size)),
        line((size, size), (0.0, size)),
        line((0.0, size), (0.0, 0.0)),
    ]
}

/// The same scene in the builder and the writer. Every call goes to both and must answer alike.
struct Pair {
    builder: *mut progpu_native_scene_builder,
    writer: SceneWriter,
}

impl Pair {
    fn new(scene_id: u64) -> Self {
        // SAFETY: the builder is destroyed by `Drop`.
        let builder = unsafe { progpu_native_scene_builder_create(scene_id, 3) };
        assert!(!builder.is_null());
        Pair { builder, writer: SceneWriter::new(scene_id, 3).expect("a writer") }
    }

    fn check(&self, name: &str, ok: i32) {
        // SAFETY: the builder is alive.
        assert_eq!(1, ok, "{name}: error {}", unsafe { progpu_native_scene_builder_last_error(self.builder) });
    }

    /// Both streams, which must be the same bytes.
    fn finish(&mut self) -> Vec<u8> {
        // SAFETY: the buffer is as long as the size the builder asked for.
        let expected = unsafe {
            let size = progpu_native_scene_builder_required_stream_size(self.builder);
            assert_ne!(0, size, "the scene of the builder");
            let mut bytes = vec![0u8; size];
            let mut written = 0usize;
            let ok = progpu_native_scene_builder_build_into(
                self.builder,
                bytes.as_mut_ptr() as *mut c_void,
                size,
                &mut written,
            );
            self.check("build_into", ok);
            assert_eq!(size, written);
            bytes
        };
        let actual = self.writer.build().expect("the stream of the writer");
        assert_eq!(expected.len(), actual.len(), "the sizes of the streams");
        if let Some(at) = expected.iter().zip(actual).position(|(left, right)| left != right) {
            panic!("the streams differ at byte {at}: {} against {}", expected[at], actual[at]);
        }
        expected
    }

    fn state(
        &mut self,
        transform: progpu_native_affine_2d,
        clip: Option<progpu_native_image_rect>,
        mask: Option<u32>,
    ) -> u32 {
        // SAFETY: a call without arguments.
        let mut state = unsafe { progpu_native_scene_builder_identity_state() };
        state.transform = transform;
        if let Some(clip) = clip {
            state.flags |= PROGPU_NATIVE_SCENE_STATE_CLIP_RECT as u32;
            state.clip_rect = clip;
        }
        if let Some(mask) = mask {
            state.flags |= PROGPU_NATIVE_SCENE_STATE_MASK as u32;
            state.mask_resource_index = mask;
        }
        let mut index = NO_INDEX;
        // SAFETY: the record and the index outlive the call.
        let ok = unsafe { progpu_native_scene_builder_add_state(self.builder, &state, &mut index) };
        self.check("add_state", ok);
        assert_eq!(index, self.writer.add_state(&state).expect("add_state"));
        index
    }

    fn solid(&mut self, color: progpu_native_color, opacity: f32) -> u32 {
        let mut index = NO_INDEX;
        // SAFETY: the index outlives the call.
        let ok = unsafe { progpu_native_scene_builder_add_solid_brush(self.builder, color, opacity, &mut index) };
        self.check("add_solid_brush", ok);
        assert_eq!(index, self.writer.add_solid_brush(color, opacity).expect("add_solid_brush"));
        index
    }

    fn clear(&mut self, color: progpu_native_color) {
        // SAFETY: the builder is alive.
        let ok = unsafe { progpu_native_scene_builder_clear_target(self.builder, color) };
        self.check("clear_target", ok);
        self.writer.clear_target(color).expect("clear_target");
    }

    fn push_layer(&mut self, layer: &progpu_native_scene_layer) {
        // SAFETY: the record outlives the call.
        let ok = unsafe { progpu_native_scene_builder_push_layer(self.builder, layer) };
        self.check("push_layer", ok);
        self.writer.push_layer(layer).expect("push_layer");
    }

    fn pop_layer(&mut self) {
        // SAFETY: the builder is alive.
        let ok = unsafe { progpu_native_scene_builder_pop_layer(self.builder) };
        self.check("pop_layer", ok);
        self.writer.pop_layer().expect("pop_layer");
    }

    fn analytic(&mut self, color: progpu_native_color, brushes: &[u32], state: u32) {
        let primitive = progpu_native_analytic_primitive {
            kind: PROGPU_NATIVE_PRIMITIVE_RECTANGLE as u32,
            flags: 0,
            x: 4.0,
            y: 4.0,
            width: 20.0,
            height: 12.0,
            corner_radius: 0.0,
            stroke_thickness: 0.0,
            color,
            transform: IDENTITY,
        };
        // SAFETY: the spans outlive the call.
        let ok = unsafe {
            progpu_native_scene_builder_draw_analytic(
                self.builder,
                &primitive,
                1,
                brushes.as_ptr(),
                brushes.len(),
                ALL,
                state,
            )
        };
        self.check("draw_analytic", ok);
        self.writer.draw_analytic(&[primitive], brushes, ALL, state).expect("draw_analytic");
    }
}

impl Drop for Pair {
    fn drop(&mut self) {
        // SAFETY: the builder is owned here.
        unsafe { progpu_native_scene_builder_destroy(self.builder) };
    }
}

fn layer(
    flags: u32,
    bounds: progpu_native_image_rect,
    opacity: f32,
    mask: u32,
    effect: u32,
) -> progpu_native_scene_layer {
    progpu_native_scene_layer {
        struct_size: std::mem::size_of::<progpu_native_scene_layer>() as u32,
        flags,
        bounds,
        opacity,
        blend_mode: PROGPU_NATIVE_BLEND_SRC_OVER as u32,
        mask_resource_index: mask,
        effect_resource_index: effect,
        content_revision: 0,
        composite_revision: 0,
        // What the C++ builder clears, so that the writer has to as well.
        reserved0: 7,
        reserved1: 9,
    }
}

fn gradient() -> (progpu_native_scene_brush, [progpu_native_scene_gradient_stop; 2]) {
    // SAFETY: an all-zero brush is a valid start.
    let mut brush: progpu_native_scene_brush = unsafe { std::mem::zeroed() };
    brush.type_ = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT as u32;
    brush.opacity = 1.0;
    brush.start_point = point(0.0, 0.0);
    brush.end_point = point(64.0, 0.0);
    brush.stop_count = 2;
    brush.coordinate_transform0[0] = 1.0;
    brush.coordinate_transform1[1] = 1.0;
    let stop =
        |color, offset| progpu_native_scene_gradient_stop { color, offset, reserved0: 0, reserved1: 0, reserved2: 0 };
    (brush, [stop(RED, 0.0), stop(BLUE, 1.0)])
}

/// The validator of the engine takes the stream, on a machine with a device.
fn engine_takes(stream: &[u8]) {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: the metrics record and the stream outlive the call.
    let status = unsafe {
        let mut metrics: progpu_native_scene_metrics = std::mem::zeroed();
        metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
        progpu_native_engine_update_scene(gpu.engine, stream.as_ptr() as *const c_void, stream.len(), &mut metrics)
    };
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "the engine refuses the stream");
}

#[test]
fn shapes_states_layers_masks_and_effects_are_the_same_bytes() {
    let mut pair = Pair::new(101);
    pair.clear(progpu_native_color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 });
    let red = pair.solid(RED, 1.0);
    assert_eq!(red, pair.solid(RED, 1.0), "an equal brush is the same brush");
    let blue = pair.solid(BLUE, 0.5);
    let (brush, stops) = gradient();
    let mut gradient_index = NO_INDEX;
    // SAFETY: the records outlive the call.
    let ok = unsafe {
        progpu_native_scene_builder_add_brush(pair.builder, &brush, stops.as_ptr(), stops.len(), &mut gradient_index)
    };
    pair.check("add_brush", ok);
    assert_eq!(gradient_index, pair.writer.add_brush(&brush, &stops).expect("add_brush"));

    let state = pair.state(IDENTITY, Some(rect(1.0, 2.0, 40.0, 40.0)), None);
    pair.analytic(RED, &[red], state);
    pair.analytic(BLUE, &[blue], NO_INDEX);
    pair.analytic(RED, &[gradient_index], state);

    // SAFETY: a save with a state of this scene and its restore.
    unsafe {
        let ok = progpu_native_scene_builder_save(pair.builder, state);
        pair.check("save", ok);
        pair.writer.save(state).expect("save");
        pair.analytic(RED, &[], NO_INDEX);
        let ok = progpu_native_scene_builder_restore(pair.builder);
        pair.check("restore", ok);
        pair.writer.restore().expect("restore");
    }

    let mut axis = NO_INDEX;
    // SAFETY: the index outlives the call.
    let area = rect(2.0, 2.0, 30.0, 30.0);
    let ok = unsafe { progpu_native_scene_builder_add_axis_aligned_clip_mask(pair.builder, area, &mut axis) };
    pair.check("add_axis_aligned_clip_mask", ok);
    assert_eq!(axis, pair.writer.add_axis_aligned_clip_mask(rect(2.0, 2.0, 30.0, 30.0)).expect("axis mask"));

    // SAFETY: an all-zero mask is a valid start.
    let mut rounded: progpu_native_scene_layer_mask = unsafe { std::mem::zeroed() };
    rounded.bounds = rect(4.0, 4.0, 40.0, 40.0);
    rounded.transform = IDENTITY;
    rounded.corner_radii_x = [4.0; 4];
    rounded.corner_radii_y = [4.0; 4];
    rounded.opacity = 1.0;
    rounded.reserved1 = 5;
    let mut rounded_index = NO_INDEX;
    // SAFETY: the record and the index outlive the call.
    let ok =
        unsafe { progpu_native_scene_builder_add_rounded_rectangle_mask(pair.builder, &rounded, &mut rounded_index) };
    pair.check("add_rounded_rectangle_mask", ok);
    assert_eq!(rounded_index, pair.writer.add_rounded_rectangle_mask(&rounded).expect("rounded mask"));
    let masked = pair.state(IDENTITY, None, Some(rounded_index));
    pair.analytic(BLUE, &[blue], masked);

    let masks = [rounded, rounded];
    let mut chain = NO_INDEX;
    // SAFETY: the span and the index outlive the call.
    let ok =
        unsafe { progpu_native_scene_builder_add_analytic_mask_chain(pair.builder, masks.as_ptr(), 2, &mut chain) };
    pair.check("add_analytic_mask_chain", ok);
    assert_eq!(chain, pair.writer.add_analytic_mask_chain(&masks).expect("mask chain"));

    // SAFETY: an all-zero mask is a valid start.
    let mut brush_mask: progpu_native_scene_layer_brush_mask = unsafe { std::mem::zeroed() };
    brush_mask.bounds = ALL;
    brush_mask.transform = IDENTITY;
    brush_mask.opacity = 1.0;
    brush_mask.brush = brush;
    let mut brush_mask_index = NO_INDEX;
    // SAFETY: the records and the index outlive the call.
    let ok = unsafe {
        progpu_native_scene_builder_add_brush_mask(pair.builder, &brush_mask, stops.as_ptr(), 2, &mut brush_mask_index)
    };
    pair.check("add_brush_mask", ok);
    assert_eq!(brush_mask_index, pair.writer.add_brush_mask(&brush_mask, &stops).expect("brush mask"));

    let segments = square(32.0);
    // SAFETY: an all-zero clip path is a valid start.
    let mut clip: progpu_native_scene_clip_path = unsafe { std::mem::zeroed() };
    clip.segment_count = 4;
    clip.max_x = 32.0;
    clip.max_y = 32.0;
    clip.transform = IDENTITY;
    clip.sample_grid = 4;
    let mut vector = NO_INDEX;
    // SAFETY: the spans and the index outlive the call.
    let ok = unsafe {
        progpu_native_scene_builder_add_vector_clip_mask(pair.builder, &clip, 1, segments.as_ptr(), 4, 1.0, &mut vector)
    };
    pair.check("add_vector_clip_mask", ok);
    assert_eq!(vector, pair.writer.add_vector_clip_mask(&[clip], &segments, 1.0).expect("vector mask"));

    // SAFETY: an all-zero effect is a valid start.
    let mut effect: progpu_native_group_effect = unsafe { std::mem::zeroed() };
    effect.kind = PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR as u32;
    effect.revision = 1;
    effect.sigma_x = 2.0;
    effect.sigma_y = 2.0;
    effect.reserved = 3;
    let mut effects = NO_INDEX;
    // SAFETY: the record and the index outlive the call.
    let ok = unsafe { progpu_native_scene_builder_add_effect_chain(pair.builder, &effect, 1, 1, &mut effects) };
    pair.check("add_effect_chain", ok);
    assert_eq!(effects, pair.writer.add_effect_chain(&[effect], 1).expect("effect chain"));

    let bounded = PROGPU_NATIVE_SCENE_LAYER_BOUNDS as u32;
    pair.push_layer(&layer(bounded, rect(0.0, 0.0, 32.0, 32.0), 0.5, NO_INDEX, NO_INDEX));
    pair.analytic(RED, &[], NO_INDEX);
    pair.push_layer(&layer(bounded, rect(2.0, 2.0, 20.0, 20.0), 1.0, vector, NO_INDEX));
    pair.analytic(BLUE, &[], NO_INDEX);
    pair.pop_layer();
    pair.pop_layer();
    pair.push_layer(&layer(bounded, ALL, 1.0, NO_INDEX, effects));
    pair.analytic(RED, &[], NO_INDEX);
    pair.pop_layer();
    pair.push_layer(&layer(bounded, ALL, 1.0, brush_mask_index, NO_INDEX));
    pair.analytic(RED, &[], NO_INDEX);
    pair.pop_layer();

    engine_takes(&pair.finish());
}

#[test]
fn paths_and_strokes_are_the_same_bytes() {
    let mut pair = Pair::new(102);
    let brush = pair.solid(RED, 1.0);
    let segments = square(16.0);
    // SAFETY: an all-zero path is a valid start.
    let mut path: progpu_native_scene_path_fill = unsafe { std::mem::zeroed() };
    path.segment_count = 4;
    path.max_x = 16.0;
    path.max_y = 16.0;
    path.color = RED;
    path.transform = IDENTITY;
    path.sample_grid = 4;
    // SAFETY: the spans outlive the call.
    let ok = unsafe {
        progpu_native_scene_builder_draw_paths(pair.builder, &path, 1, segments.as_ptr(), 4, &brush, 1, ALL, NO_INDEX)
    };
    pair.check("draw_paths", ok);
    pair.writer.draw_paths(&[path], &segments, &[brush], ALL, NO_INDEX).expect("draw_paths");

    let mut last = NO_INDEX;
    // SAFETY: the index outlives the call.
    let ok = unsafe { progpu_native_scene_builder_last_path_resource(pair.builder, &mut last) };
    pair.check("last_path_resource", ok);
    assert_eq!(Some(last), pair.writer.last_path_resource());
    let moved = pair.state(progpu_native_affine_2d { m31: 20.0, ..IDENTITY }, None, None);
    assert_eq!(None, pair.writer.last_path_resource(), "a state was added after the paths");
    // SAFETY: the resource is a path batch of this scene; the span outlives the call.
    let ok = unsafe { progpu_native_scene_builder_draw_path_resource(pair.builder, last, &brush, 1, ALL, moved) };
    pair.check("draw_path_resource", ok);
    pair.writer.draw_path_resource(last, &[brush], ALL, moved).expect("draw_path_resource");

    // SAFETY: an all-zero stroke is a valid start.
    let mut stroke: progpu_native_scene_stroke = unsafe { std::mem::zeroed() };
    stroke.struct_size = std::mem::size_of::<progpu_native_scene_stroke>() as u32;
    stroke.kind = PROGPU_NATIVE_SCENE_STROKE_POLYLINE as u32;
    stroke.point_count = 3;
    stroke.color = BLUE;
    stroke.transform = IDENTITY;
    stroke.stroke_thickness = 2.0;
    stroke.miter_limit = 4.0;
    let points = [point(2.0, 2.0), point(30.0, 2.0), point(30.0, 30.0)];
    // SAFETY: the spans outlive the call.
    let ok = unsafe {
        progpu_native_scene_builder_draw_strokes(
            pair.builder,
            &stroke,
            1,
            points.as_ptr(),
            3,
            std::ptr::null(),
            0,
            &brush,
            1,
            ALL,
            NO_INDEX,
        )
    };
    pair.check("draw_strokes", ok);
    pair.writer.draw_strokes(&[stroke], &points, &[], &[brush], ALL, NO_INDEX).expect("draw_strokes");

    engine_takes(&pair.finish());
}

fn image_draw(width: u32, x: f32) -> progpu_native_scene_image_draw {
    progpu_native_scene_image_draw {
        struct_size: 0,
        flags: PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED as u32,
        image_width: width,
        image_height: 2,
        row_bytes: width * 4,
        sampling: PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST as u32,
        source_rect: rect(0.0, 0.0, width as f32, 2.0),
        destination_rect: rect(x, 0.0, 8.0, 8.0),
        transform: IDENTITY,
        opacity: 1.0,
        max_anisotropy: 1,
    }
}

#[test]
fn images_and_their_batches_are_the_same_bytes() {
    let mut pair = Pair::new(103);
    let pixels = [200u8; 16];
    let (mut rgba, mut bgra, mut external) = (NO_INDEX, NO_INDEX, NO_INDEX);
    // SAFETY: the pixels and the indices outlive the calls.
    unsafe {
        let data = pixels.as_ptr() as *const c_void;
        let ok = progpu_native_scene_builder_add_rgba8_image(pair.builder, 2, 2, 8, data, 16, &mut rgba);
        pair.check("add_rgba8_image", ok);
        let ok = progpu_native_scene_builder_add_bgra8_image(pair.builder, 2, 2, 8, data, 16, &mut bgra);
        pair.check("add_bgra8_image", ok);
        let ok = progpu_native_scene_builder_add_external_image(pair.builder, 4, 2, &mut external);
        pair.check("add_external_image", ok);
    }
    assert_eq!(rgba, pair.writer.add_rgba8_image(2, 2, 8, &pixels).expect("rgba"));
    assert_eq!(bgra, pair.writer.add_bgra8_image(2, 2, 8, &pixels).expect("bgra"));
    assert_eq!(external, pair.writer.add_external_image(4, 2).expect("external").0);

    // Three draws of one image join one batch; the next image and the draw after a clear do not.
    let draws = [(rgba, 2, 0.0), (rgba, 2, 10.0), (rgba, 2, 20.0), (bgra, 2, 30.0), (external, 4, 40.0)];
    for (resource, width, x) in draws {
        let draw = image_draw(width, x);
        let bounds = draw.destination_rect;
        // SAFETY: the record outlives the call.
        let ok = unsafe {
            progpu_native_scene_builder_draw_image(pair.builder, resource, &draw, bounds, NO_INDEX, std::ptr::null())
        };
        pair.check("draw_image", ok);
        pair.writer.draw_image(resource, &draw, bounds, NO_INDEX, None).expect("draw_image");
    }
    pair.clear(RED);
    let draw = image_draw(2, 50.0);
    // SAFETY: the record outlives the call.
    let ok = unsafe {
        let bounds = draw.destination_rect;
        progpu_native_scene_builder_draw_image(pair.builder, rgba, &draw, bounds, NO_INDEX, std::ptr::null())
    };
    pair.check("draw_image", ok);
    pair.writer.draw_image(rgba, &draw, draw.destination_rect, NO_INDEX, None).expect("draw_image");

    engine_takes(&pair.finish());
}

fn outline(segment_offset: u64) -> progpu_native_scene_glyph_outline {
    progpu_native_scene_glyph_outline {
        segment_offset,
        segment_count: 4,
        min_x: 0.0,
        min_y: 0.0,
        max_x: 12.0,
        max_y: 12.0,
        raster_scale: 1.0,
        subpixel_x: 0.25,
    }
}

fn positioned(outline_index: u32, x: f32) -> progpu_native_positioned_glyph {
    progpu_native_positioned_glyph {
        outline_index,
        reserved: 0,
        position: point(x, 20.0),
        basis_x: point(1.0, 0.0),
        basis_y: point(0.0, 1.0),
        color: RED,
        atlas_to_logical_scale: 1.0,
        bold_offset: 0.0,
        italic_skew: 0.0,
        reserved2: 0.0,
    }
}

#[test]
fn text_is_the_same_bytes() {
    let mut pair = Pair::new(104);
    let segments = square(12.0);
    let outlines = [outline(0), outline(0)];
    let mut resource = NO_INDEX;
    // SAFETY: the spans and the index outlive the call.
    let ok = unsafe {
        let (outlines, segments) = (outlines.as_ptr(), segments.as_ptr());
        progpu_native_scene_builder_add_glyph_outlines(pair.builder, outlines, 2, segments, 4, &mut resource)
    };
    pair.check("add_glyph_outlines", ok);
    assert_eq!(resource, pair.writer.add_glyph_outlines(&outlines, &segments).expect("outlines"));

    let style = progpu_native_scene_text_style {
        color: RED,
        text_rendering_mode: PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE as u32,
        coverage_transfer: 35,
        reserved1: 8,
        reserved2: 9,
    };
    let mut style_index = NO_INDEX;
    // SAFETY: the record and the index outlive the call.
    let ok = unsafe { progpu_native_scene_builder_add_text_style(pair.builder, &style, &mut style_index) };
    pair.check("add_text_style", ok);
    assert_eq!(style_index, pair.writer.add_text_style(&style).expect("style"));
    assert_eq!(style_index, pair.writer.add_text_style(&style).expect("the same style"));

    let glyphs = [positioned(0, 4.0), positioned(1, 20.0)];
    for style in [style_index, NO_INDEX] {
        // SAFETY: the span outlives the call.
        let ok = unsafe {
            progpu_native_scene_builder_draw_glyph_run(pair.builder, resource, glyphs.as_ptr(), 2, ALL, NO_INDEX, style)
        };
        pair.check("draw_glyph_run", ok);
        pair.writer.draw_glyph_run(resource, &glyphs, ALL, NO_INDEX, style).expect("draw_glyph_run");
    }

    let flags = (PROGPU_NATIVE_SCENE_LAYER_BOUNDS
        | PROGPU_NATIVE_SCENE_LAYER_INITIALIZE_FROM_BACKGROUND
        | PROGPU_NATIVE_SCENE_LAYER_IGNORE_ALPHA) as u32;
    pair.push_layer(&layer(flags, ALL, 1.0, NO_INDEX, NO_INDEX));
    let draw = progpu_native_scene_rgb_glyph_draw {
        struct_size: std::mem::size_of::<progpu_native_scene_rgb_glyph_draw>() as u32,
        glyph_count: 1,
        filter_model: PROGPU_NATIVE_RGB_GLYPH_FIR5_8X8 as u32,
        pixel_geometry: 1,
        gamma: 1.0,
        enhanced_contrast: 0.0,
        cleartype_level: 1.0,
        dpi_scale: 1.0,
        coverage_transfer: 35,
        reserved1: 0,
    };
    let tile = progpu_native_scene_rgb_glyph_tile {
        outline_index: 0,
        width: 16,
        height: 14,
        reserved: 0,
        x_start: -2.0,
        y_start: -13.0,
        scale: 1.0,
        subpixel_x: 0.25,
        target_x: 4,
        target_y: 8,
        foreground: RED,
    };
    let state = pair.state(IDENTITY, None, None);
    // SAFETY: the records outlive the call.
    let ok =
        unsafe { progpu_native_scene_builder_draw_rgb_glyph_run(pair.builder, resource, &draw, &tile, 1, ALL, state) };
    pair.check("draw_rgb_glyph_run", ok);
    pair.writer.draw_rgb_glyph_run(resource, &draw, &[tile], ALL, state).expect("draw_rgb_glyph_run");
    pair.pop_layer();

    engine_takes(&pair.finish());
}

#[test]
fn glyphs_named_by_key_are_the_same_bytes() {
    let mut pair = Pair::new(105);
    let keys = [progpu_native_scene_glyph_key {
        outline: progpu_native_glyph_outline_key { face: 7, glyph: 1, variant: 0 },
        raster_scale: 1.0,
        subpixel_x: 0.5,
    }];
    let mut resource = NO_INDEX;
    // SAFETY: the span and the index outlive the call.
    let ok = unsafe { progpu_native_scene_builder_add_glyph_keys(pair.builder, keys.as_ptr(), 1, &mut resource) };
    pair.check("add_glyph_keys", ok);
    assert_eq!(resource, pair.writer.add_glyph_keys(&keys).expect("keys"));
    let glyphs = [positioned(0, 4.0)];
    // SAFETY: the span outlives the call.
    let ok = unsafe {
        progpu_native_scene_builder_draw_glyph_run(pair.builder, resource, glyphs.as_ptr(), 1, ALL, NO_INDEX, NO_INDEX)
    };
    pair.check("draw_glyph_run", ok);
    pair.writer.draw_glyph_run(resource, &glyphs, ALL, NO_INDEX, NO_INDEX).expect("draw_glyph_run");
    // The outlines of the keys are registered by a host: only the bytes are compared.
    pair.finish();
}

#[test]
fn a_writer_that_is_reset_writes_what_a_new_one_writes() {
    let mut used = SceneWriter::new(9, 1).expect("a writer");
    used.add_solid_brush(RED, 1.0).expect("a brush");
    used.clear_target(BLUE).expect("a clear");
    used.build().expect("the first stream");
    used.reset(106, 3).expect("reset");

    let mut pair = Pair::new(106);
    let brush = pair.solid(BLUE, 1.0);
    pair.analytic(RED, &[brush], NO_INDEX);
    let expected = pair.finish();

    let brush = used.add_solid_brush(BLUE, 1.0).expect("a brush");
    let primitive = progpu_native_analytic_primitive {
        kind: PROGPU_NATIVE_PRIMITIVE_RECTANGLE as u32,
        flags: 0,
        x: 4.0,
        y: 4.0,
        width: 20.0,
        height: 12.0,
        corner_radius: 0.0,
        stroke_thickness: 0.0,
        color: RED,
        transform: IDENTITY,
    };
    used.draw_analytic(&[primitive], &[brush], ALL, NO_INDEX).expect("a draw");
    assert_eq!(expected.as_slice(), used.build().expect("the second stream"));
}

#[test]
fn what_the_writer_refuses() {
    let mut writer = SceneWriter::new(107, 1).expect("a writer");
    assert!(SceneWriter::new(0, 1).is_err());
    assert!(writer.restore().is_err(), "a restore without a save");
    assert!(writer.pop_layer().is_err(), "a pop without a push");
    assert!(writer.save(5).is_err(), "a state that does not exist");
    assert!(writer.add_rgba8_image(2, 2, 8, &[0; 15]).is_err(), "too few pixels");
    writer.save(NO_INDEX).expect("a save");
    assert!(writer.pop_layer().is_err(), "a pop of a save");
    assert!(writer.build().is_err(), "a scope is open");
    writer.restore().expect("the restore");
    assert!(writer.build().is_ok());
}
