//! A WebGPU device of the linked wgpu-native with the engine on it, for the tests: created
//! through the standard webgpu.h, the engine through its procedure resolver.
#![allow(dead_code)]

use progpu_native_sys::progpu::*;
use progpu_native_sys::webgpu::*;
use std::ffi::c_void;
use std::ptr;

fn empty_label() -> WGPUStringView {
    WGPUStringView { data: ptr::null(), length: 0 }
}

unsafe extern "C" fn on_adapter(status: WGPURequestAdapterStatus, adapter: WGPUAdapter, _: WGPUStringView, slot: *mut c_void, _: *mut c_void) {
    if status == WGPURequestAdapterStatus_Success {
        *(slot as *mut WGPUAdapter) = adapter;
    }
}

unsafe extern "C" fn on_device(status: WGPURequestDeviceStatus, device: WGPUDevice, _: WGPUStringView, slot: *mut c_void, _: *mut c_void) {
    if status == WGPURequestDeviceStatus_Success {
        *(slot as *mut WGPUDevice) = device;
    }
}

unsafe extern "C" fn on_mapped(status: WGPUMapAsyncStatus, _: WGPUStringView, slot: *mut c_void, _: *mut c_void) {
    *(slot as *mut i32) = if status == WGPUMapAsyncStatus_Success { 1 } else { -1 };
}

/// The device, its queue and the engine.
pub struct Gpu {
    pub instance: WGPUInstance,
    pub adapter: WGPUAdapter,
    pub device: WGPUDevice,
    pub queue: WGPUQueue,
    pub engine: *mut progpu_native_engine,
}

impl Gpu {
    /// The device and the engine, or None (with a message) without an adapter.
    pub fn new() -> Option<Gpu> {
        assert!(progpu_native_sys::abi_matches(), "the engine's ABI version");
        // SAFETY: the calls of the C ABIs; the callbacks write the slots they are given.
        unsafe {
            let instance = wgpuCreateInstance(ptr::null());
            let mut adapter: WGPUAdapter = ptr::null_mut();
            wgpuInstanceRequestAdapter(
                instance,
                ptr::null(),
                WGPURequestAdapterCallbackInfo {
                    nextInChain: ptr::null_mut(),
                    mode: WGPUCallbackMode_AllowSpontaneous,
                    callback: Some(on_adapter),
                    userdata1: &mut adapter as *mut _ as *mut c_void,
                    userdata2: ptr::null_mut(),
                },
            );
            wgpuInstanceProcessEvents(instance);
            if adapter.is_null() {
                eprintln!("skipped: no WebGPU adapter");
                return None;
            }
            let mut device: WGPUDevice = ptr::null_mut();
            wgpuAdapterRequestDevice(
                adapter,
                ptr::null(),
                WGPURequestDeviceCallbackInfo {
                    nextInChain: ptr::null_mut(),
                    mode: WGPUCallbackMode_AllowSpontaneous,
                    callback: Some(on_device),
                    userdata1: &mut device as *mut _ as *mut c_void,
                    userdata2: ptr::null_mut(),
                },
            );
            wgpuInstanceProcessEvents(instance);
            assert!(!device.is_null(), "the device");
            let queue = wgpuDeviceGetQueue(device);
            let options = progpu_native_dawn_engine_options {
                struct_size: std::mem::size_of::<progpu_native_dawn_engine_options>() as u32,
                native_abi_version: PROGPU_NATIVE_ABI_VERSION,
                adapter_abi_version: PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION,
                provider_abi_version: PROGPU_NATIVE_DAWN_REQUIRED_PROVIDER_ABI_VERSION,
                target_format: PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM as u32,
                reserved: 0,
                resolver_context: progpu_native_sys::resolver_context(),
                resolve_proc: Some(progpu_native_sys::resolve_webgpu_proc),
                instance: instance as usize,
                device: device as usize,
                queue: queue as usize,
                flags: 0,
            };
            let mut engine: *mut progpu_native_engine = ptr::null_mut();
            let status = progpu_native_dawn_engine_create(&options, &mut engine);
            assert_eq!(PROGPU_NATIVE_STATUS_SUCCESS, status, "the engine on the device");
            Some(Gpu { instance, adapter, device, queue, engine })
        }
    }

    /// A render target of `RGBA8Unorm` that can be read back, with its view.
    pub fn target(&self, width: u32, height: u32) -> (WGPUTexture, WGPUTextureView) {
        // SAFETY: the device is alive; the descriptor is borrowed for the call.
        unsafe {
            let texture = wgpuDeviceCreateTexture(
                self.device,
                &WGPUTextureDescriptor {
                    nextInChain: ptr::null_mut(),
                    label: empty_label(),
                    usage: WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc,
                    dimension: WGPUTextureDimension_2D,
                    size: WGPUExtent3D { width, height, depthOrArrayLayers: 1 },
                    format: WGPUTextureFormat_RGBA8Unorm,
                    mipLevelCount: 1,
                    sampleCount: 1,
                    viewFormatCount: 0,
                    viewFormats: ptr::null(),
                },
            );
            (texture, wgpuTextureCreateView(texture, ptr::null()))
        }
    }

    /// The pixels of a target (tightly packed RGBA8 rows).
    pub fn read_rgba8(&self, texture: WGPUTexture, width: u32, height: u32) -> Vec<u8> {
        let row = (width * 4).div_ceil(256) * 256;
        let size = (row * height) as u64;
        // SAFETY: the device is alive; the mapping is read after it completed.
        unsafe {
            let buffer = wgpuDeviceCreateBuffer(
                self.device,
                &WGPUBufferDescriptor {
                    nextInChain: ptr::null_mut(),
                    label: empty_label(),
                    usage: WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst,
                    size,
                    mappedAtCreation: 0,
                },
            );
            let encoder = wgpuDeviceCreateCommandEncoder(self.device, ptr::null());
            wgpuCommandEncoderCopyTextureToBuffer(
                encoder,
                &WGPUTexelCopyTextureInfo { texture, mipLevel: 0, origin: WGPUOrigin3D { x: 0, y: 0, z: 0 }, aspect: WGPUTextureAspect_All },
                &WGPUTexelCopyBufferInfo {
                    layout: WGPUTexelCopyBufferLayout { offset: 0, bytesPerRow: row, rowsPerImage: height },
                    buffer,
                },
                &WGPUExtent3D { width, height, depthOrArrayLayers: 1 },
            );
            let commands = wgpuCommandEncoderFinish(encoder, ptr::null());
            wgpuQueueSubmit(self.queue, 1, &commands);
            let mut mapped = 0i32;
            wgpuBufferMapAsync(
                buffer,
                WGPUMapMode_Read,
                0,
                size as usize,
                WGPUBufferMapCallbackInfo {
                    nextInChain: ptr::null_mut(),
                    mode: WGPUCallbackMode_AllowSpontaneous,
                    callback: Some(on_mapped),
                    userdata1: &mut mapped as *mut _ as *mut c_void,
                    userdata2: ptr::null_mut(),
                },
            );
            while mapped == 0 {
                wgpuDevicePoll(self.device, 1, ptr::null());
            }
            assert_eq!(1, mapped, "the read-back mapping");
            let data = std::slice::from_raw_parts(wgpuBufferGetConstMappedRange(buffer, 0, size as usize) as *const u8, size as usize);
            let mut pixels = Vec::with_capacity((width * height * 4) as usize);
            for y in 0..height {
                let start = (y * row) as usize;
                pixels.extend_from_slice(&data[start..start + (width * 4) as usize]);
            }
            wgpuBufferUnmap(buffer);
            wgpuBufferRelease(buffer);
            pixels
        }
    }
}

impl Drop for Gpu {
    fn drop(&mut self) {
        // SAFETY: owned here, released once.
        unsafe {
            progpu_native_engine_destroy(self.engine);
            wgpuQueueRelease(self.queue);
            wgpuDeviceRelease(self.device);
            wgpuAdapterRelease(self.adapter);
            wgpuInstanceRelease(self.instance);
        }
    }
}
