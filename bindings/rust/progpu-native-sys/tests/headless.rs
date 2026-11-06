//! The engine on a device of the statically linked wgpu-native, created through the standard
//! webgpu.h with the engine's procedure resolver.

mod common;

#[test]
fn the_engine_is_created_on_a_device_of_the_linked_wgpu_native() {
    let Some(gpu) = common::Gpu::new() else { return };
    assert!(!gpu.engine.is_null());
}
