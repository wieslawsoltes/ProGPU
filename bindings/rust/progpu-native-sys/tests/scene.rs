//! A retained scene through the C ABI over the builder: recorded, built into a stream, sent once
//! (`progpu_native_engine_update_scene`) and rendered twice (`progpu_native_engine_render_scene`);
//! the second frame uploads nothing.

use progpu_native_sys::progpu::*;
use progpu_native_sys::scene_builder::*;
use progpu_native_sys::webgpu::*;
use progpu_native_sys::Runtime;
use std::ffi::c_void;
use std::ptr;

const WIDTH: u32 = 48;
const HEIGHT: u32 = 32;

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

#[test]
fn a_retained_scene_is_sent_once_and_rendered_again_without_uploads() {
    let runtime = match Runtime::load() {
        Ok(runtime) => runtime,
        Err(error) => {
            eprintln!("skipped: {error}");
            return;
        }
    };
    let w = &runtime.wgpu;
    let p = &runtime.progpu;
    unsafe {
        let instance = w.wgpuCreateInstance(ptr::null());
        let mut adapter: WGPUAdapter = ptr::null_mut();
        w.wgpuInstanceRequestAdapter(instance, ptr::null(), Some(on_adapter), &mut adapter as *mut _ as *mut c_void);
        if adapter.is_null() {
            eprintln!("skipped: no WebGPU adapter");
            return;
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

        // Record: a cleared target and a red rounded rectangle.
        let scene_id = 7u64;
        let builder = progpu_native_scene_builder_create(scene_id, 1);
        assert!(!builder.is_null());
        assert_eq!(1, progpu_native_scene_builder_clear_target(builder, progpu_native_color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 }));
        let mut brush = 0u32;
        assert_eq!(1, progpu_native_scene_builder_add_solid_brush(builder, progpu_native_color { r: 1.0, g: 0.0, b: 0.0, a: 1.0 }, 1.0, &mut brush));
        let primitive = progpu_native_analytic_primitive {
            kind: PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE as u32,
            flags: 0,
            x: 8.0,
            y: 8.0,
            width: 32.0,
            height: 16.0,
            corner_radius: 4.0,
            stroke_thickness: 0.0,
            color: progpu_native_color { r: 1.0, g: 0.0, b: 0.0, a: 1.0 },
            transform: progpu_native_scene_builder_identity_transform(),
        };
        let bounds = progpu_native_image_rect { x: 8.0, y: 8.0, width: 32.0, height: 16.0 };
        assert_eq!(
            1,
            progpu_native_scene_builder_draw_analytic(builder, &primitive, 1, &brush, 1, bounds, PROGPU_NATIVE_SCENE_NO_INDEX as u32),
            "draw_analytic: error {}",
            progpu_native_scene_builder_last_error(builder)
        );
        let size = progpu_native_scene_builder_required_stream_size(builder);
        let mut stream = vec![0u8; size];
        let mut written = 0usize;
        assert_eq!(1, progpu_native_scene_builder_build_into(builder, stream.as_mut_ptr() as *mut c_void, stream.len(), &mut written));
        assert_eq!(size, written);
        progpu_native_scene_builder_destroy(builder);

        let mut scene_metrics: progpu_native_scene_metrics = std::mem::zeroed();
        scene_metrics.struct_size = std::mem::size_of::<progpu_native_scene_metrics>() as u32;
        let status = p.progpu_native_engine_update_scene(engine, stream.as_ptr() as *const c_void, stream.len(), &mut scene_metrics);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "update_scene: validation error {}", scene_metrics.validation_error);

        let texture = w.wgpuDeviceCreateTexture(
            device,
            &WGPUTextureDescriptor {
                nextInChain: ptr::null(),
                label: ptr::null(),
                usage: (WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc) as u32,
                dimension: WGPUTextureDimension_2D,
                size: WGPUExtent3D { width: WIDTH, height: HEIGHT, depthOrArrayLayers: 1 },
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
        frame.width = WIDTH;
        frame.height = HEIGHT;
        frame.dpi_scale = 1.0;
        frame.target_view = view as usize;
        frame.clear_color = progpu_native_color { r: 0.0, g: 0.0, b: 0.0, a: 1.0 };
        frame.scene_id = scene_id;
        frame.generation = 1;
        let render = |label: &str| {
            let mut metrics: progpu_native_scene_frame_metrics = std::mem::zeroed();
            metrics.struct_size = std::mem::size_of::<progpu_native_scene_frame_metrics>() as u32;
            let status = p.progpu_native_engine_render_scene(engine, &frame, &mut metrics);
            assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "render_scene ({label})");
            metrics
        };
        let first = render("first");
        let second = render("stable");
        eprintln!(
            "first frame: {} draws, {} vertex bytes; stable frame: {} draws, {} vertex bytes, {} texture bytes",
            first.draw_call_count, first.vertex_upload_bytes, second.draw_call_count, second.vertex_upload_bytes, second.texture_upload_bytes
        );
        assert_eq!(0, second.vertex_upload_bytes + second.index_upload_bytes + second.texture_upload_bytes + second.brush_upload_bytes);

        // Read back.
        let bytes_per_row = 256u32;
        let size = (bytes_per_row * HEIGHT) as u64;
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
                layout: WGPUTextureDataLayout { nextInChain: ptr::null(), offset: 0, bytesPerRow: bytes_per_row, rowsPerImage: HEIGHT },
                buffer,
            },
            &WGPUExtent3D { width: WIDTH, height: HEIGHT, depthOrArrayLayers: 1 },
        );
        let commands = w.wgpuCommandEncoderFinish(encoder, ptr::null());
        w.wgpuQueueSubmit(queue, 1, &commands);
        let mut mapped = 0i32;
        w.wgpuBufferMapAsync(buffer, WGPUMapMode_Read as u32, 0, size as usize, Some(on_mapped), &mut mapped as *mut _ as *mut c_void);
        while mapped == 0 {
            w.wgpuDevicePoll(device, 1, ptr::null());
        }
        let data = std::slice::from_raw_parts(w.wgpuBufferGetConstMappedRange(buffer, 0, size as usize) as *const u8, size as usize);
        let pixel = |x: u32, y: u32| {
            let index = (y * bytes_per_row + x * 4) as usize;
            [data[index], data[index + 1], data[index + 2], data[index + 3]]
        };
        assert_eq!([255, 255, 255, 255], pixel(2, 2), "the cleared target");
        assert_eq!([255, 0, 0, 255], pixel(24, 16), "the rounded rectangle");
        w.wgpuBufferUnmap(buffer);
        p.progpu_native_engine_destroy(engine);
    }
}
