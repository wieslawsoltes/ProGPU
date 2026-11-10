//! Explicit little-endian wire IO, independent of Rust layout or pointer casts.
use super::{NativeSceneValidationError as E, TransportResult, error};
trait Scalar: Copy {
    const SIZE: usize;
    fn read(bytes: &[u8]) -> Option<Self>;
    fn write(self, bytes: &mut [u8]);
}
macro_rules! scalar {
    ($ty:ty, $size:expr) => {
        impl Scalar for $ty {
            const SIZE: usize = $size;
            fn read(bytes: &[u8]) -> Option<Self> {
                Some(Self::from_le_bytes(bytes.get(..$size)?.try_into().ok()?))
            }
            fn write(self, bytes: &mut [u8]) {
                bytes[..$size].copy_from_slice(&self.to_le_bytes());
            }
        }
    };
}
scalar!(u32, 4);
scalar!(u64, 8);
scalar!(f32, 4);
macro_rules! record {
    ($(#[$meta:meta])* $name:ident, $size:expr, {$($field:ident: $ty:ty),* $(,)?}) => {
        $(#[$meta])*
        #[derive(Clone, Copy, Debug, Default, PartialEq)]
        pub struct $name { $(pub $field: $ty,)* }
        impl $name {
            pub const WIRE_SIZE: usize = $size;
            /// Read the original fixed prefix, leaving future extension bytes raw.
            pub fn read(bytes: &[u8]) -> Option<Self> {
                if bytes.len() < Self::WIRE_SIZE { return None; }
                let mut offset = 0;
                $(let $field = <$ty as Scalar>::read(&bytes[offset..])?;
                  offset += <$ty as Scalar>::SIZE;)*
                let _ = offset;
                Some(Self { $($field,)* })
            }
            /// Admission precedes every write; all trailing bytes remain intact.
            pub fn write_into(self, bytes: &mut [u8]) -> TransportResult<()> {
                if bytes.len() < Self::WIRE_SIZE { return Err(error(E::Range, 0)); }
                let mut offset = 0;
                $(self.$field.write(&mut bytes[offset..]); offset += <$ty as Scalar>::SIZE;)*
                let _ = offset;
                Ok(())
            }
        }
        const _: () = assert!($size == 0 $(+ <$ty as Scalar>::SIZE)*);
    };
}
record!(Header, 80, {
    struct_size: u32, magic: u32, stream_version: u32, endian_marker: u32,
    flags: u32, total_size: u32, scene_id: u64, generation: u64,
    command_offset: u32, command_count: u32, command_stride: u32,
    resource_offset: u32, resource_count: u32, resource_stride: u32,
    arena_offset: u32, arena_size: u32, reserved0: u32, reserved1: u32,
});
record!(Resource, 48, {
    struct_size: u32, kind: u32, flags: u32, reserved: u32,
    resource_id: u64, generation: u64, payload_offset: u32, payload_size: u32,
    auxiliary_offset: u32, auxiliary_size: u32,
});
record!(Command, 64, {
    struct_size: u32, kind: u32, flags: u32, reserved: u32, command_id: u64,
    state_index: u32, resource_index: u32, payload_offset: u32, payload_size: u32,
    bounds_x: f32, bounds_y: f32, bounds_width: f32, bounds_height: f32,
    reserved0: u32, reserved1: u32,
});
record!(Metrics, 64, {
    struct_size: u32, flags: u32, command_count: u32, resource_count: u32,
    draw_count: u32, maximum_stack_depth: u32, validation_error: u32, error_offset: u32,
    scene_id: u64, generation: u64, snapshot_bytes: u64, payload_bytes: u64,
});
