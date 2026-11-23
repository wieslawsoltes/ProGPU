// The C ABI of `native/progpu_native_browser_c.h` and the browser entry of the engine
// (`progpu_native_browser.h`), on wasm32-unknown-emscripten.

use crate::progpu::{progpu_native_engine, progpu_native_status};

pub const PROGPU_NATIVE_BROWSER_ADAPTER_ABI_VERSION: u32 = 1;

#[repr(C)]
#[derive(Debug, Default, Copy, Clone)]
pub struct progpu_native_browser_engine_options {
    pub struct_size: u32,
    pub native_abi_version: u32,
    pub adapter_abi_version: u32,
    pub target_format: u32,
    pub reserved0: u32,
    pub reserved1: u32,
    pub device: usize,
    pub queue: usize,
    pub flags: u64,
}

#[repr(C)]
pub struct progpu_native_browser_canvas {
    _unused: [u8; 0],
}

unsafe extern "C" {
    pub fn progpu_native_browser_engine_create(
        options: *const progpu_native_browser_engine_options,
        engine: *mut *mut progpu_native_engine,
    ) -> progpu_native_status;
    pub fn progpu_native_browser_imported_device() -> usize;
    pub fn progpu_native_browser_device_queue(device: usize) -> usize;
    pub fn progpu_native_browser_canvas_create(
        device: usize,
        selector: *const ::core::ffi::c_char,
        native_format: u32,
        canvas: *mut *mut progpu_native_browser_canvas,
    ) -> ::core::ffi::c_int;
    pub fn progpu_native_browser_canvas_destroy(canvas: *mut progpu_native_browser_canvas);
    pub fn progpu_native_browser_canvas_begin_frame(
        canvas: *mut progpu_native_browser_canvas,
        width: u32,
        height: u32,
        view: *mut usize,
    ) -> ::core::ffi::c_int;
    pub fn progpu_native_browser_canvas_end_frame(canvas: *mut progpu_native_browser_canvas);
}
