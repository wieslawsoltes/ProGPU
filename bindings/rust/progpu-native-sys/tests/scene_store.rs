//! Scenes kept per target: two targets on one engine find their scenes as they left them.

mod common;

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use std::ffi::c_void;

const SIZE: u32 = 32;

unsafe fn scene(scene_id: u64, generation: u64, red: f32) -> Vec<u8> {
    let builder = progpu_native_scene_builder_create(scene_id, generation);
    let state = progpu_native_scene_builder_identity_state();
    let mut state_index = 0;
    assert_ne!(0, progpu_native_scene_builder_add_state(builder, &state, &mut state_index));
    let color = progpu_native_color { r: red, g: 0.25, b: 0.5, a: 1.0 };
    let mut brush = 0;
    assert_ne!(0, progpu_native_scene_builder_add_solid_brush(builder, color, 1.0, &mut brush));
    let primitive = progpu_native_analytic_primitive {
        kind: PROGPU_NATIVE_PRIMITIVE_RECTANGLE as u32,
        flags: 0,
        x: 0.0,
        y: 0.0,
        width: SIZE as f32,
        height: SIZE as f32,
        corner_radius: 0.0,
        stroke_thickness: 0.0,
        color: progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 },
        transform: progpu_native_scene_builder_identity_transform(),
    };
    let bounds = progpu_native_image_rect { x: 0.0, y: 0.0, width: SIZE as f32, height: SIZE as f32 };
    assert_ne!(0, progpu_native_scene_builder_draw_analytic(builder, &primitive, 1, &brush, 1, bounds, state_index));
    let mut stream = vec![0u8; progpu_native_scene_builder_required_stream_size(builder)];
    let mut written = 0;
    assert_ne!(0, progpu_native_scene_builder_build_into(builder, stream.as_mut_ptr() as *mut c_void, stream.len(), &mut written));
    stream.truncate(written);
    progpu_native_scene_builder_destroy(builder);
    stream
}

unsafe fn update(gpu: &common::Gpu, stream: &[u8]) -> (progpu_native_status, u32) {
    let mut metrics: progpu_native_scene_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
    let status = progpu_native_engine_update_scene(gpu.engine, stream.as_ptr() as *const c_void, stream.len(), &mut metrics);
    (status, metrics.flags & PROGPU_NATIVE_SCENE_METRICS_SNAPSHOT_REUSED as u32)
}

unsafe fn render(gpu: &common::Gpu, scene_id: u64, generation: u64) -> Result<[u8; 4], progpu_native_status> {
    let (texture, view) = gpu.target(SIZE, SIZE);
    let mut frame: progpu_native_scene_frame = std::mem::zeroed();
    frame.struct_size = std::mem::size_of::<progpu_native_scene_frame>() as u32;
    frame.width = SIZE;
    frame.height = SIZE;
    frame.dpi_scale = 1.0;
    frame.target_view = view as usize;
    frame.clear_color = progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 };
    frame.scene_id = scene_id;
    frame.generation = generation;
    let mut metrics: progpu_native_scene_frame_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_frame_metrics>() as u32;
    let status = progpu_native_engine_render_scene(gpu.engine, &frame, &mut metrics);
    if status != PROGPU_NATIVE_STATUS_SUCCESS {
        return Err(status);
    }
    let pixels = gpu.read_rgba8(texture, SIZE, SIZE);
    Ok([pixels[0], pixels[1], pixels[2], pixels[3]])
}

unsafe fn store(gpu: &common::Gpu) -> progpu_native_scene_store_metrics {
    let mut metrics = progpu_native_scene_store_metrics {
        struct_size: std::mem::size_of::<progpu_native_scene_store_metrics>() as u32,
        ..Default::default()
    };
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_get_scene_store_metrics(gpu.engine, &mut metrics));
    metrics
}

#[test]
fn two_targets_on_one_engine_keep_their_scenes() {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: the calls of the C ABIs with the handles of the device; the spans outlive them.
    unsafe {
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_set_scene_budget(gpu.engine, 1 << 20));
        let (first, second) = (scene(41, 1, 1.0), scene(42, 1, 0.0));
        assert_eq!((PROGPU_NATIVE_STATUS_SUCCESS, 0), update(&gpu, &first));
        assert_eq!((PROGPU_NATIVE_STATUS_SUCCESS, 0), update(&gpu, &second));
        assert_eq!(1, store(&gpu).kept_scene_count);

        // The second frame of each is a snapshot hit, in either order.
        for _ in 0..2 {
            let (status, reused) = update(&gpu, &first);
            assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
            assert_ne!(0, reused);
            assert_eq!(255, render(&gpu, 41, 1).expect("the first scene")[0]);
            let (status, reused) = update(&gpu, &second);
            assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status);
            assert_ne!(0, reused);
            assert_eq!(0, render(&gpu, 42, 1).expect("the second scene")[0]);
        }
        // A frame finds a kept scene by itself.
        assert_eq!(255, render(&gpu, 41, 1).expect("the kept scene")[0]);

        // A kept scene keeps its generation rules.
        assert_ne!(PROGPU_NATIVE_STATUS_SUCCESS, update(&gpu, &scene(42, 1, 0.5)).0);
        assert_eq!(0, render(&gpu, 42, 1).expect("the scene the refused stream named")[0]);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, update(&gpu, &scene(41, 2, 0.5)).0);
        assert!(render(&gpu, 41, 1).is_err(), "the old generation is gone");

        // A released scene is gone; the active one stays.
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_release_scene(gpu.engine, 42));
        assert_eq!(0, store(&gpu).kept_scene_count);
        assert!(render(&gpu, 42, 1).is_err());
        assert!(render(&gpu, 41, 2).is_ok());
    }
}

#[test]
fn the_budget_bounds_the_kept_scenes_and_zero_keeps_one() {
    let Some(gpu) = common::Gpu::new() else { return };
    // SAFETY: as above.
    unsafe {
        let (first, second, third) = (scene(51, 1, 1.0), scene(52, 1, 0.5), scene(53, 1, 0.25));
        update(&gpu, &first);
        update(&gpu, &second);
        assert_eq!(0, store(&gpu).kept_scene_count, "no budget, no kept scene");
        assert!(render(&gpu, 51, 1).is_err());

        // Room for one scene: the scene used longest ago goes first.
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_set_scene_budget(gpu.engine, second.len() as u64));
        update(&gpu, &first);
        update(&gpu, &third);
        let kept = store(&gpu);
        assert_eq!(1, kept.kept_scene_count);
        assert!(kept.kept_scene_bytes <= kept.budget_bytes);
        assert!(render(&gpu, 52, 1).is_err());
        assert!(render(&gpu, 51, 1).is_ok());

        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, progpu_native_engine_set_scene_budget(gpu.engine, 0));
        assert_eq!(0, store(&gpu).kept_scene_count);
    }
}
