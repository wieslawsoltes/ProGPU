//! Groups rendered before the root pass: the same pixels as groups with passes of their own, and
//! as many content passes as the groups are deep rather than as there are groups.

mod common;

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use std::ffi::c_void;

const WIDTH: u32 = 160;
const HEIGHT: u32 = 120;

unsafe fn paged_engine(gpu: &common::Gpu) -> *mut progpu_native_engine {
    let options = progpu_native_dawn_engine_options {
        struct_size: std::mem::size_of::<progpu_native_dawn_engine_options>() as u32,
        native_abi_version: PROGPU_NATIVE_ABI_VERSION,
        adapter_abi_version: PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION,
        provider_abi_version: PROGPU_NATIVE_DAWN_REQUIRED_PROVIDER_ABI_VERSION,
        target_format: PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM as u32,
        reserved: 0,
        resolver_context: progpu_native_sys::resolver_context(),
        resolve_proc: Some(progpu_native_sys::resolve_webgpu_proc),
        instance: gpu.instance as usize,
        device: gpu.device as usize,
        queue: gpu.queue as usize,
        flags: PROGPU_NATIVE_ENGINE_GROUPS_BEFORE_ROOT_PASS as u64,
    };
    let mut engine: *mut progpu_native_engine = std::ptr::null_mut();
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_dawn_engine_create(&options, &mut engine));
    engine
}

struct Writer {
    builder: *mut progpu_native_scene_builder,
    state: u32,
}

impl Writer {
    unsafe fn rectangle(&self, x: f32, y: f32, width: f32, height: f32, color: [f32; 4]) {
        let mut brush = 0;
        let color = progpu_native_color { r: color[0], g: color[1], b: color[2], a: color[3] };
        assert_ne!(0, progpu_native_scene_builder_add_solid_brush(self.builder, color, 1.0, &mut brush));
        let primitive = progpu_native_analytic_primitive {
            kind: PROGPU_NATIVE_PRIMITIVE_RECTANGLE as u32,
            flags: 0,
            x,
            y,
            width,
            height,
            corner_radius: 0.0,
            stroke_thickness: 0.0,
            color: progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 },
            transform: progpu_native_scene_builder_identity_transform(),
        };
        let bounds = progpu_native_image_rect { x, y, width, height };
        assert_ne!(0, progpu_native_scene_builder_draw_analytic(self.builder, &primitive, 1, &brush, 1, bounds, self.state));
    }

    unsafe fn push(&self, x: f32, y: f32, width: f32, height: f32, opacity: f32) {
        let mut layer: progpu_native_scene_layer = std::mem::zeroed();
        layer.struct_size = std::mem::size_of::<progpu_native_scene_layer>() as u32;
        layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS as u32;
        layer.bounds = progpu_native_image_rect { x, y, width, height };
        layer.opacity = opacity;
        layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER as u32;
        layer.mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX as u32;
        layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX as u32;
        assert_ne!(0, progpu_native_scene_builder_push_layer(self.builder, &layer));
    }

    unsafe fn pop(&self) {
        assert_ne!(0, progpu_native_scene_builder_pop_layer(self.builder));
    }
}

unsafe fn build(id: u64, write: impl FnOnce(&Writer)) -> Vec<u8> {
    let builder = progpu_native_scene_builder_create(id, 1);
    let state = progpu_native_scene_builder_identity_state();
    let mut state_index = 0;
    assert_ne!(0, progpu_native_scene_builder_add_state(builder, &state, &mut state_index));
    let writer = Writer { builder, state: state_index };
    writer.rectangle(0.0, 0.0, WIDTH as f32, HEIGHT as f32, [0.9, 0.9, 0.8, 1.0]);
    write(&writer);
    let mut stream = vec![0u8; progpu_native_scene_builder_required_stream_size(builder)];
    let mut written = 0;
    assert_ne!(0, progpu_native_scene_builder_build_into(builder, stream.as_mut_ptr() as *mut c_void, stream.len(), &mut written));
    stream.truncate(written);
    progpu_native_scene_builder_destroy(builder);
    stream
}

/// The pixels of the scene and the content passes of its groups.
unsafe fn render(gpu: &common::Gpu, engine: *mut progpu_native_engine, id: u64, stream: &[u8]) -> (Vec<u8>, u32) {
    let mut metrics: progpu_native_scene_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
    assert_eq!(
        PROGPU_NATIVE_STATUS_SUCCESS,
        progpu_native_engine_update_scene(engine, stream.as_ptr() as *const c_void, stream.len(), &mut metrics)
    );
    let (texture, view) = gpu.target(WIDTH, HEIGHT);
    let mut frame: progpu_native_scene_frame = std::mem::zeroed();
    frame.struct_size = std::mem::size_of::<progpu_native_scene_frame>() as u32;
    frame.width = WIDTH;
    frame.height = HEIGHT;
    frame.dpi_scale = 1.0;
    frame.target_view = view as usize;
    frame.clear_color = progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 };
    frame.scene_id = id;
    frame.generation = 1;
    let mut frame_metrics: progpu_native_scene_frame_metrics = std::mem::zeroed();
    frame_metrics.struct_size = std::mem::size_of::<progpu_native_scene_frame_metrics>() as u32;
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_render_scene(engine, &frame, &mut frame_metrics));
    let mut layers: progpu_native_layer_metrics = std::mem::zeroed();
    layers.struct_size = std::mem::size_of::<progpu_native_layer_metrics>() as u32;
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_get_layer_metrics(engine, &mut layers));
    (gpu.read_rgba8(texture, WIDTH, HEIGHT), layers.content_pass_count)
}

unsafe fn compare(gpu: &common::Gpu, paged: *mut progpu_native_engine, id: u64, write: impl Fn(&Writer)) -> (u32, u32) {
    let stream = build(id, &write);
    let (expected, own_passes) = render(gpu, gpu.engine, id, &stream);
    let (actual, page_passes) = render(gpu, paged, id, &stream);
    assert!(expected == actual, "scene {id}: the pixels differ");
    (own_passes, page_passes)
}

#[test]
fn groups_on_pages_read_back_like_groups_with_their_own_passes() {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the device; the spans outlive them.
    unsafe {
        let paged = paged_engine(&gpu);

        // Twelve groups side by side: one page.
        let (own, pages) = compare(&gpu, paged, 11, |w| {
            for index in 0..12 {
                let (x, y) = ((index % 4) as f32 * 40.0 + 2.0, (index / 4) as f32 * 40.0 + 2.0);
                w.push(x, y, 36.0, 36.0, 0.5);
                w.rectangle(x - 4.0, y - 4.0, 44.0, 44.0, [0.1, 0.2, 0.9, 1.0]);
                w.rectangle(x + 8.0, y + 8.0, 12.0, 12.0, [0.9, 0.1, 0.1, 0.5]);
                w.pop();
            }
        });
        assert_eq!((12, 1), (own, pages));

        // Groups in groups, drawn between draws of the root: one page per depth.
        let (own, pages) = compare(&gpu, paged, 12, |w| {
            for index in 0..3 {
                let x = index as f32 * 52.0 + 2.0;
                w.rectangle(x, 90.0, 40.0, 20.0, [0.2, 0.7, 0.2, 1.0]);
                w.push(x, 4.0, 48.0, 80.0, 0.75);
                w.rectangle(x, 4.0, 48.0, 80.0, [0.8, 0.3, 0.1, 1.0]);
                w.push(x + 6.0, 10.0, 30.0, 30.0, 0.5);
                w.rectangle(x + 6.0, 10.0, 30.0, 30.0, [0.1, 0.1, 0.8, 1.0]);
                w.pop();
                w.pop();
            }
        });
        assert_eq!((6, 2), (own, pages));

        // Overlapping groups keep their own passes, and their order.
        let (own, pages) = compare(&gpu, paged, 13, |w| {
            w.push(10.0, 10.0, 60.0, 60.0, 0.5);
            w.rectangle(10.0, 10.0, 60.0, 60.0, [0.9, 0.1, 0.1, 1.0]);
            w.pop();
            w.push(40.0, 40.0, 60.0, 60.0, 0.5);
            w.rectangle(40.0, 40.0, 60.0, 60.0, [0.1, 0.1, 0.9, 1.0]);
            w.pop();
        });
        assert_eq!(2, own);
        assert_eq!(2, pages, "the second group overlaps the first and keeps its own pass");

        progpu_native_engine_destroy(paged);
    }
}
