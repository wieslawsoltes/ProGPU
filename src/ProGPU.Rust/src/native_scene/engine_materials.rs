//! Original native packed brush/text-style pages and command-to-page remaps.
//! Pages are immutable candidates and retain the actual admitted stream owner.
use super::execution::NativeStateCursor;
use super::{wire, NativeScene};
use crate::Error;
use std::{
    collections::HashMap,
    hash::{BuildHasherDefault, Hash, Hasher},
    sync::Arc,
};
use wire::{constants::*, WireRecord};
mod brush;
mod text;
pub use brush::{NativeBrushDraw, NativeBrushPage};
pub use text::{native_glyph_payload, NativeTextStylePage};

#[derive(Clone, Copy, PartialEq, Eq)]
struct Key(u32, u32, u32);
impl Hash for Key {
    fn hash<H: Hasher>(&self, state: &mut H) {
        let value = u64::from(self.0).wrapping_mul(0x9e3779b185ebca87) ^ u64::from(self.1);
        let value = value.wrapping_mul(0x9e3779b185ebca87) ^ u64::from(self.2);
        state.write_u64((value ^ (value >> 32)) as usize as u64);
    }
}
#[derive(Default)]
struct KeyHasher(u64);
impl Hasher for KeyHasher {
    fn finish(&self) -> u64 {
        self.0
    }
    fn write_u64(&mut self, value: u64) {
        self.0 = value;
    }
    fn write(&mut self, bytes: &[u8]) {
        // Key::hash always writes the original already-mixed integer.
        for &byte in bytes {
            self.0 = self.0.wrapping_mul(0x9e3779b185ebca87) ^ u64::from(byte);
        }
    }
}
type Variants = HashMap<Key, u32, BuildHasherDefault<KeyHasher>>;
fn variants(capacity: usize) -> Result<Variants, Error> {
    let mut map = Variants::default();
    map.try_reserve(capacity).map_err(|_| Error::Capacity)?;
    Ok(map)
}
fn allocate<T>(capacity: usize) -> Result<Vec<T>, Error> {
    let mut values = Vec::new();
    values
        .try_reserve_exact(capacity)
        .map_err(|_| Error::Capacity)?;
    Ok(values)
}
fn push<T>(values: &mut Vec<T>, value: T) -> Result<(), Error> {
    values.try_reserve(1).map_err(|_| Error::Capacity)?;
    values.push(value);
    Ok(())
}
fn read<T: WireRecord>(bytes: &[u8], index: usize) -> Result<T, Error> {
    index
        .checked_mul(T::WIRE_SIZE)
        .and_then(|offset| bytes.get(offset..))
        .and_then(T::read)
        .ok_or(Error::InvalidInput)
}
