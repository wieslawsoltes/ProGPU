//! Explicit little-endian wire IO, independent of Rust layout or pointer casts.
use super::{NativeSceneValidationError as E, TransportResult, error};
mod sealed {
    pub trait Sealed {}
}
trait Scalar: Copy {
    const SIZE: usize;
    fn zero() -> Self;
    fn read(bytes: &[u8]) -> Option<Self>;
    fn write(self, bytes: &mut [u8]);
}
macro_rules! scalar {
    ($ty:ty, $size:expr) => {
        impl sealed::Sealed for $ty {}
        impl WireRecord for $ty {
            const WIRE_SIZE: usize = $size;
            fn read(bytes: &[u8]) -> Option<Self> {
                Some(Self::from_le_bytes(bytes.get(..$size)?.try_into().ok()?))
            }
            fn write_into(self, bytes: &mut [u8]) -> TransportResult<()> {
                if bytes.len() < $size {
                    return Err(error(E::Range, 0));
                }
                bytes[..$size].copy_from_slice(&self.to_le_bytes());
                Ok(())
            }
        }
        impl Scalar for $ty {
            const SIZE: usize = $size;
            fn zero() -> Self {
                0 as Self
            }
            fn read(bytes: &[u8]) -> Option<Self> {
                Some(Self::from_le_bytes(bytes.get(..$size)?.try_into().ok()?))
            }
            fn write(self, bytes: &mut [u8]) {
                bytes[..$size].copy_from_slice(&self.to_le_bytes());
            }
        }
    };
}
scalar!(u8, 1);
scalar!(u16, 2);
scalar!(i32, 4);
scalar!(f64, 8);
scalar!(u32, 4);
scalar!(u64, 8);
scalar!(f32, 4);
impl<T: Scalar, const N: usize> Scalar for [T; N] {
    const SIZE: usize = T::SIZE * N;
    fn zero() -> Self {
        std::array::from_fn(|_| T::zero())
    }
    fn read(bytes: &[u8]) -> Option<Self> {
        let mut result = Self::zero();
        for (index, value) in result.iter_mut().enumerate() {
            *value = T::read(bytes.get(index * T::SIZE..(index + 1) * T::SIZE)?)?;
        }
        Some(result)
    }
    fn write(self, bytes: &mut [u8]) {
        for (value, span) in self.into_iter().zip(bytes.chunks_exact_mut(T::SIZE)) {
            value.write(span);
        }
    }
}
/// Safe original byte-codec contract; Rust memory layout is never the wire ABI.
pub trait WireRecord: Copy + sealed::Sealed {
    const WIRE_SIZE: usize;
    fn read(bytes: &[u8]) -> Option<Self>;
    fn write_into(self, bytes: &mut [u8]) -> TransportResult<()>;
}
macro_rules! record {
    ($(#[$meta:meta])* $name:ident, $size:expr, {$($field:ident: $ty:ty),* $(,)?}) => {
        $(#[$meta])*
        #[derive(Clone, Copy, Debug, PartialEq)]
        pub struct $name { $(pub $field: $ty,)* }
        impl sealed::Sealed for $name {}
        impl Default for $name {
            fn default() -> Self { Self { $($field: <$ty as Scalar>::zero(),)* } }
        }
        impl WireRecord for $name {
            const WIRE_SIZE: usize = $size;
            fn read(bytes: &[u8]) -> Option<Self> { Self::read(bytes) }
            fn write_into(self, bytes: &mut [u8]) -> TransportResult<()> { Self::write_into(self, bytes) }
        }
        impl Scalar for $name {
            const SIZE: usize = $size;
            fn zero() -> Self { Self::default() }
            fn read(bytes: &[u8]) -> Option<Self> { Self::read(bytes) }
            fn write(self, bytes: &mut [u8]) {
                self.write_into(bytes).expect("fixed record destination has its original wire extent");
            }
        }
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

mod payload;
pub use payload::*;
pub mod constants;
