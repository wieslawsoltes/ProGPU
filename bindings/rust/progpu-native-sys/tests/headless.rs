//! The engine on a wgpu-native device, headless: a frame of rectangles on a cleared target, read
//! back. Skipped (with a message) where the runtime of the SDK is not available for the target.

use progpu_native_sys::progpu::*;
use progpu_native_sys::webgpu::*;
use progpu_native_sys::Runtime;
use std::ffi::c_void;
use std::ptr;

const WIDTH: u32 = 64;
const HEIGHT: u32 = 64;

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

fn color(r: f32, g: f32, b: f32, a: f32) -> progpu_native_color {
    progpu_native_color { r, g, b, a }
}

#[test]
fn the_engine_renders_rectangles_on_a_wgpu_native_device() {
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
        assert!(!instance.is_null(), "no WebGPU instance");
        let mut adapter: WGPUAdapter = ptr::null_mut();
        w.wgpuInstanceRequestAdapter(instance, ptr::null(), Some(on_adapter), &mut adapter as *mut _ as *mut c_void);
        if adapter.is_null() {
            eprintln!("skipped: no WebGPU adapter");
            return;
        }
        let mut device: WGPUDevice = ptr::null_mut();
        w.wgpuAdapterRequestDevice(adapter, ptr::null(), Some(on_device), &mut device as *mut _ as *mut c_void);
        assert!(!device.is_null(), "no WebGPU device");
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
        let status = p.progpu_native_engine_create(&options, &mut engine);
        assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "progpu_native_engine_create");

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

        let rects = [
            progpu_native_rect { x: 8.0, y: 8.0, width: 24.0, height: 24.0, color: color(1.0, 0.0, 0.0, 1.0) },
            progpu_native_rect { x: 32.0, y: 32.0, width: 24.0, height: 24.0, color: color(0.0, 0.0, 1.0, 1.0) },
        ];
        let frame = progpu_native_frame {
            struct_size: std::mem::size_of::<progpu_native_frame>() as u32,
            width: WIDTH,
            height: HEIGHT,
            dpi_scale: 1.0,
            target_view: view as usize,
            clear_color: color(1.0, 1.0, 1.0, 1.0),
            rects: rects.as_ptr(),
            rect_count: rects.len(),
            draw_state: ptr::null(),
        };
        let mut metrics: progpu_native_frame_metrics = std::mem::zeroed();
        metrics.struct_size = std::mem::size_of::<progpu_native_frame_metrics>() as u32;
        let status = p.progpu_native_engine_render(engine, &frame, &mut metrics);
        if status != PROGPU_NATIVE_STATUS_SUCCESS {
            let mut message = [0i8; 512];
            p.progpu_native_engine_get_last_error(engine, message.as_mut_ptr(), message.len());
            panic!("progpu_native_engine_render: {status} {:?}", std::ffi::CStr::from_ptr(message.as_ptr()));
        }

        // Read the target back.
        let bytes_per_row = WIDTH * 4;
        let size = (bytes_per_row * HEIGHT) as u64;
        let buffer = w.wgpuDeviceCreateBuffer(
            device,
            &WGPUBufferDescriptor {
                nextInChain: ptr::null(),
                label: ptr::null(),
                usage: (WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst) as u32,
                size,
                mappedAtCreation: 0,
            },
        );
        let encoder = w.wgpuDeviceCreateCommandEncoder(device, ptr::null());
        w.wgpuCommandEncoderCopyTextureToBuffer(
            encoder,
            &WGPUImageCopyTexture {
                nextInChain: ptr::null(),
                texture,
                mipLevel: 0,
                origin: WGPUOrigin3D { x: 0, y: 0, z: 0 },
                aspect: WGPUTextureAspect_All,
            },
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
        assert_eq!(1, mapped, "the readback buffer was not mapped");
        let data = std::slice::from_raw_parts(w.wgpuBufferGetConstMappedRange(buffer, 0, size as usize) as *const u8, size as usize);
        let pixel = |x: u32, y: u32| {
            let index = (y * bytes_per_row + x * 4) as usize;
            [data[index], data[index + 1], data[index + 2], data[index + 3]]
        };
        assert_eq!([255, 255, 255, 255], pixel(2, 2), "the clear color");
        assert_eq!([255, 0, 0, 255], pixel(16, 16), "the red rectangle");
        assert_eq!([0, 0, 255, 255], pixel(48, 48), "the blue rectangle");
        w.wgpuBufferUnmap(buffer);

        p.progpu_native_engine_destroy(engine);
        w.wgpuBufferRelease(buffer);
        w.wgpuTextureViewRelease(view);
        w.wgpuTextureRelease(texture);
        w.wgpuQueueRelease(queue);
        w.wgpuDeviceRelease(device);
        w.wgpuAdapterRelease(adapter);
        w.wgpuInstanceRelease(instance);
    }
}
