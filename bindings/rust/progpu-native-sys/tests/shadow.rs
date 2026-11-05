//! The analytic shadow primitive (`PROGPU_NATIVE_PRIMITIVE_SHADOW`) through a retained scene,
//! against exact references: the blurred coverage of a square-cornered box is the product of two
//! normal CDF differences; that of a rounded box is a numerical 2D Gaussian convolution.

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use progpu_native_sys::webgpu::*;
use progpu_native_sys::Runtime;
use std::ffi::c_void;
use std::ptr;

const SIZE: u32 = 128;

unsafe extern "C" fn on_adapter(status: WGPURequestAdapterStatus, adapter: WGPUAdapter, _: *const i8, userdata: *mut c_void) {
    if status == WGPURequestAdapterStatus_Success {
        *(userdata as *mut WGPUAdapter) = adapter;
    }
}

unsafe extern "C" fn on_device(status: WGPURequestDeviceStatus, device: WGPUDevice, _: *const i8, userdata: *mut c_void) {
    if status == WGPURequestDeviceStatus_Success {
        *(userdata as *mut WGPUDevice) = device;
    }
}

unsafe extern "C" fn on_mapped(status: WGPUBufferMapAsyncStatus, userdata: *mut c_void) {
    *(userdata as *mut i32) = if status == WGPUBufferMapAsyncStatus_Success { 1 } else { -1 };
}

/// The error function, to 1.2e-7 (Numerical Recipes' erfc Chebyshev fit).
fn erf(x: f64) -> f64 {
    let z = x.abs();
    let t = 1.0 / (1.0 + 0.5 * z);
    let r = t
        * (-z * z - 1.26551223
            + t * (1.00002368
                + t * (0.37409196
                    + t * (0.09678418
                        + t * (-0.18628806
                            + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))))
            .exp();
    if x >= 0.0 {
        1.0 - r
    } else {
        r - 1.0
    }
}

fn phi(x: f64) -> f64 {
    0.5 * (1.0 + erf(x / std::f64::consts::SQRT_2))
}

/// The blurred coverage of the box [left, right] x [top, bottom] at (x, y).
fn box_reference(x: f64, y: f64, left: f64, top: f64, right: f64, bottom: f64, sigma: f64) -> f64 {
    (phi((right - x) / sigma) - phi((left - x) / sigma)) * (phi((bottom - y) / sigma) - phi((top - y) / sigma))
}

fn inside_rounded(x: f64, y: f64, left: f64, top: f64, right: f64, bottom: f64, radius: f64) -> bool {
    let cx = x.clamp(left + radius, right - radius);
    let cy = y.clamp(top + radius, bottom - radius);
    x >= left && x <= right && y >= top && y <= bottom && (x - cx).powi(2) + (y - cy).powi(2) <= radius * radius
}

/// The blurred coverage of a rounded box by numerical convolution (step 0.125 over +-4 sigma).
fn rounded_reference(x: f64, y: f64, left: f64, top: f64, right: f64, bottom: f64, radius: f64, sigma: f64) -> f64 {
    let step = 0.125;
    let reach = 4.0 * sigma;
    let mut sum = 0.0;
    let mut weight = 0.0;
    let mut dy = -reach;
    while dy <= reach {
        let mut dx = -reach;
        while dx <= reach {
            let g = (-(dx * dx + dy * dy) / (2.0 * sigma * sigma)).exp();
            weight += g;
            if inside_rounded(x + dx, y + dy, left, top, right, bottom, radius) {
                sum += g;
            }
            dx += step;
        }
        dy += step;
    }
    sum / weight
}

/// Renders black shadows on white and returns the RGBA8 rows (256-byte aligned rows).
unsafe fn render(runtime: &Runtime, shadows: &[progpu_native_analytic_primitive]) -> Option<(Vec<u8>, u32)> {
    let w = &runtime.wgpu;
    let p = &runtime.progpu;
    let instance = w.wgpuCreateInstance(ptr::null());
    let mut adapter: WGPUAdapter = ptr::null_mut();
    w.wgpuInstanceRequestAdapter(instance, ptr::null(), Some(on_adapter), &mut adapter as *mut _ as *mut c_void);
    if adapter.is_null() {
        return None;
    }
    let mut device: WGPUDevice = ptr::null_mut();
    w.wgpuAdapterRequestDevice(adapter, ptr::null(), Some(on_device), &mut device as *mut _ as *mut c_void);
    let queue = w.wgpuDeviceGetQueue(device);
    let options = progpu_native_engine_options {
        struct_size: std::mem::size_of::<progpu_native_engine_options>() as u32,
        abi_version: PROGPU_NATIVE_ABI_VERSION,
        backend_abi: PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05 as u32,
        target_format: PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM as u32,
        device: device as usize,
        queue: queue as usize,
        flags: 0,
    };
    let mut engine: *mut progpu_native_engine = ptr::null_mut();
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, p.progpu_native_engine_create(&options, &mut engine));

    let scene_id = 11u64;
    let builder = progpu_native_scene_builder_create(scene_id, 1);
    assert_eq!(1, progpu_native_scene_builder_clear_target(builder, progpu_native_color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 }));
    let mut brush = 0u32;
    assert_eq!(1, progpu_native_scene_builder_add_solid_brush(builder, progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 }, 1.0, &mut brush));
    let brushes = vec![brush; shadows.len()];
    let bounds = progpu_native_image_rect { x: 0.0, y: 0.0, width: SIZE as f32, height: SIZE as f32 };
    assert_eq!(
        1,
        progpu_native_scene_builder_draw_analytic(
            builder,
            shadows.as_ptr(),
            shadows.len(),
            brushes.as_ptr(),
            brushes.len(),
            bounds,
            PROGPU_NATIVE_SCENE_NO_INDEX as u32
        ),
        "draw_analytic: error {}",
        progpu_native_scene_builder_last_error(builder)
    );
    let size = progpu_native_scene_builder_required_stream_size(builder);
    let mut stream = vec![0u8; size];
    let mut written = 0usize;
    assert_eq!(1, progpu_native_scene_builder_build_into(builder, stream.as_mut_ptr() as *mut c_void, stream.len(), &mut written));
    progpu_native_scene_builder_destroy(builder);
    let mut scene_metrics: progpu_native_scene_metrics = std::mem::zeroed();
    scene_metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
    let status = p.progpu_native_engine_update_scene(engine, stream.as_ptr() as *const c_void, written, &mut scene_metrics);
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "update_scene: validation error {}", scene_metrics.validation_error);

    let texture = w.wgpuDeviceCreateTexture(
        device,
        &WGPUTextureDescriptor {
            nextInChain: ptr::null(),
            label: ptr::null(),
            usage: (WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc) as u32,
            dimension: WGPUTextureDimension_2D,
            size: WGPUExtent3D { width: SIZE, height: SIZE, depthOrArrayLayers: 1 },
            format: WGPUTextureFormat_RGBA8Unorm,
            mipLevelCount: 1,
            sampleCount: 1,
            viewFormatCount: 0,
            viewFormats: ptr::null(),
        },
    );
    let view = w.wgpuTextureCreateView(texture, ptr::null());
    let mut frame: progpu_native_scene_frame = std::mem::zeroed();
    frame.struct_size = std::mem::size_of::<progpu_native_scene_frame>() as u32;
    frame.width = SIZE;
    frame.height = SIZE;
    frame.dpi_scale = 1.0;
    frame.target_view = view as usize;
    frame.scene_id = scene_id;
    frame.generation = 1;
    let mut metrics: progpu_native_scene_frame_metrics = std::mem::zeroed();
    metrics.struct_size = std::mem::size_of::<progpu_native_scene_frame_metrics>() as u32;
    assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, p.progpu_native_engine_render_scene(engine, &frame, &mut metrics));
    eprintln!("{} shadows: {} draws", shadows.len(), metrics.draw_call_count);

    let bytes_per_row = SIZE * 4;
    let size = (bytes_per_row * SIZE) as u64;
    let buffer = w.wgpuDeviceCreateBuffer(
        device,
        &WGPUBufferDescriptor { nextInChain: ptr::null(), label: ptr::null(), usage: (WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst) as u32, size, mappedAtCreation: 0 },
    );
    let encoder = w.wgpuDeviceCreateCommandEncoder(device, ptr::null());
    w.wgpuCommandEncoderCopyTextureToBuffer(
        encoder,
        &WGPUImageCopyTexture { nextInChain: ptr::null(), texture, mipLevel: 0, origin: WGPUOrigin3D { x: 0, y: 0, z: 0 }, aspect: WGPUTextureAspect_All },
        &WGPUImageCopyBuffer {
            nextInChain: ptr::null(),
            layout: WGPUTextureDataLayout { nextInChain: ptr::null(), offset: 0, bytesPerRow: bytes_per_row, rowsPerImage: SIZE },
            buffer,
        },
        &WGPUExtent3D { width: SIZE, height: SIZE, depthOrArrayLayers: 1 },
    );
    let commands = w.wgpuCommandEncoderFinish(encoder, ptr::null());
    w.wgpuQueueSubmit(queue, 1, &commands);
    let mut mapped = 0i32;
    w.wgpuBufferMapAsync(buffer, WGPUMapMode_Read as u32, 0, size as usize, Some(on_mapped), &mut mapped as *mut _ as *mut c_void);
    while mapped == 0 {
        w.wgpuDevicePoll(device, 1, ptr::null());
    }
    let data = std::slice::from_raw_parts(w.wgpuBufferGetConstMappedRange(buffer, 0, size as usize) as *const u8, size as usize).to_vec();
    w.wgpuBufferUnmap(buffer);
    p.progpu_native_engine_destroy(engine);
    Some((data, bytes_per_row))
}

fn shadow(x: f32, y: f32, width: f32, height: f32, radius: f32, sigma: f32) -> progpu_native_analytic_primitive {
    progpu_native_analytic_primitive {
        kind: PROGPU_NATIVE_PRIMITIVE_SHADOW as u32,
        flags: 0,
        x,
        y,
        width,
        height,
        corner_radius: radius,
        stroke_thickness: sigma,
        color: progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 },
        // SAFETY: a pure function of the ABI.
        transform: unsafe { progpu_native_scene_builder_identity_transform() },
    }
}

#[test]
fn analytic_shadows_match_the_gaussian_blur_of_their_box() {
    let runtime = match Runtime::load() {
        Ok(runtime) => runtime,
        Err(error) => {
            eprintln!("skipped: {error}");
            return;
        }
    };
    // A square-cornered box (exact reference) and a rounded one, apart.
    let (left, top, right, bottom, sigma) = (24.0, 24.0, 64.0, 56.0, 5.0);
    let (rl, rt, rr, rb, radius, rsigma) = (80.0, 72.0, 116.0, 116.0, 10.0, 4.0);
    let shadows = [
        shadow(left as f32, top as f32, (right - left) as f32, (bottom - top) as f32, 0.0, sigma as f32),
        shadow(rl as f32, rt as f32, (rr - rl) as f32, (rb - rt) as f32, radius as f32, rsigma as f32),
    ];
    // SAFETY: the runtime is loaded; the call owns its device and engine.
    let Some((data, row)) = (unsafe { render(&runtime, &shadows) }) else {
        eprintln!("skipped: no WebGPU adapter");
        return;
    };
    let coverage = |x: u32, y: u32| 1.0 - data[(y * row + x * 4) as usize] as f64 / 255.0;
    let mut worst: f64 = 0.0;
    // Square corners: every pixel of a window around the box.
    for y in 8..72 {
        for x in 8..76 {
            let expected = box_reference(x as f64 + 0.5, y as f64 + 0.5, left, top, right, bottom, sigma);
            worst = worst.max((coverage(x, y) - expected).abs());
        }
    }
    assert!(worst <= 4.0 / 255.0, "square-cornered shadow differs by {worst} from its exact blur");
    eprintln!("square-cornered shadow: worst difference {:.4}", worst);
    // Rounded corners: the corner regions, where the closed form approximates.
    let mut worst_rounded: f64 = 0.0;
    for &(x, y) in &[(80u32, 72u32), (84, 76), (78, 70), (88, 74), (112, 112), (118, 118), (98, 70), (78, 94), (90, 90)] {
        let expected = rounded_reference(x as f64 + 0.5, y as f64 + 0.5, rl, rt, rr, rb, radius, rsigma);
        worst_rounded = worst_rounded.max((coverage(x, y) - expected).abs());
    }
    eprintln!("rounded shadow: worst difference {:.4}", worst_rounded);
    assert!(worst_rounded <= 0.03, "rounded shadow differs by {worst_rounded} from its numerical blur");
}
